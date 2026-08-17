using FishNet.Object;
using FishNet.Object.Prediction;
using FishNet.Transporting;
using FishNet.Utility.Template;
using UnityEngine;

namespace ChopChop.Player
{
    /// <summary>
    /// Predicted player movement — the only predicted system in the game (TECH 4.3).
    /// Everything else is a plain server round trip.
    ///
    /// The shape is FishNet prediction v2: the owner builds a <see cref="MoveData"/>
    /// each tick and runs it locally at once, the server runs the same data as
    /// authority, and the server's resulting <see cref="MotorState"/> is reconciled
    /// back. On a mismatch FishNet rewinds to the server state and replays every
    /// input since, so <see cref="Simulate"/> must be a pure function of
    /// (state, input) and must never read wall-clock time or per-frame values.
    ///
    /// Deliberately not a Rigidbody. TECH 4.3 rules out physics prediction outright,
    /// and mounts are equipment rather than vehicles (TECH 11), so nothing in the
    /// game needs networked rigidbodies.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class PlayerMotor : TickNetworkBehaviour
    {
        /// <summary>What the owner pressed during one tick. This is the only thing a client sends.</summary>
        public struct MoveData : IReplicateData
        {
            public Vector2 Move;
            public bool Jump;

            private uint _tick;

            public MoveData(Vector2 move, bool jump)
            {
                Move = move;
                Jump = jump;
                _tick = 0;
            }

            public void Dispose() { }
            public uint GetTick() => _tick;
            public void SetTick(uint value) => _tick = value;
        }

        /// <summary>
        /// Everything <see cref="Simulate"/> carries between ticks. If a value influences
        /// the next tick's outcome it belongs here, or replays will diverge from the
        /// server and the player will see rubber-banding that looks like packet loss.
        /// </summary>
        public struct MotorState : IReconcileData
        {
            public Vector3 Position;
            public float VerticalVelocity;

            private uint _tick;

            public MotorState(Vector3 position, float verticalVelocity)
            {
                Position = position;
                VerticalVelocity = verticalVelocity;
                _tick = 0;
            }

            public void Dispose() { }
            public uint GetTick() => _tick;
            public void SetTick(uint value) => _tick = value;
        }

        [Header("Movement")]
        [SerializeField] private float _moveSpeed = 5f;
        [SerializeField] private float _jumpSpeed = 7f;

        [Tooltip("Multiplier on Physics.gravity. Above 1 makes the fall snappier than " +
                 "real gravity, which reads better than a floaty arc.")]
        [SerializeField] private float _gravityScale = 2.5f;

        [Tooltip("Fastest the player may fall. Also bounds how far a mispredicted fall " +
                 "can drift before the reconcile catches it.")]
        [SerializeField] private float _terminalVelocity = -40f;

        private CharacterController _controller;
        private PlayerInputReader _input;
        private PlayerCameraRig _cameraRig;

        private float _verticalVelocity;

        /// <summary>Most recent input actually ticked, used to extrapolate for spectators.</summary>
        private MoveData _lastTickedMove;

        /// <summary>
        /// Planar speed in metres per second, for driving a locomotion blend.
        ///
        /// Valid on every machine rather than only the owner: state forwarding means
        /// spectators replay the same inputs through <see cref="Simulate"/>, so they
        /// arrive at the same speed rather than having to guess it from transform deltas.
        /// </summary>
        public float PlanarSpeed { get; private set; }

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _input = GetComponent<PlayerInputReader>();
            _cameraRig = GetComponent<PlayerCameraRig>();

            // Reconcile is built at the end of OnTick, after the move has been applied.
            // CharacterController.Move resolves immediately rather than deferring to the
            // physics step, so the transform is already final by then.
            SetTickCallbacks(TickCallback.Tick);
        }

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            // Owner.IsLocalClient rather than IsOwner: FishNet's codegen rejects IsOwner
            // this early (FN0007), since ownership is still being assigned.
            if (_input != null)
                _input.enabled = Owner.IsLocalClient;
        }

        protected override void TimeManager_OnTick()
        {
            Simulate(BuildMoveData());
            CreateReconcile();
        }

        private MoveData BuildMoveData()
        {
            // Only the controller builds input. For everyone else FishNet supplies the
            // data it received, or a predicted state.
            if (!IsOwner || _input == null)
                return default;

            return new MoveData(ToWorldSpace(_input.MoveInput), _input.ConsumeJump());
        }

        /// <summary>
        /// Turns camera-relative input into a world-space direction.
        ///
        /// Done here, on the owner, and never inside <see cref="Simulate"/>. The camera's
        /// heading is a per-frame client-local value that the server has no copy of, and
        /// a replayed tick must not re-read it — by then the player has looked somewhere
        /// else, and the same input would move them somewhere new every replay. Baking
        /// the heading into <see cref="MoveData"/> keeps the simulation a pure function of
        /// the data it was handed, and costs nothing on the wire: the field was always a
        /// direction, it is now simply a direction in world space.
        /// </summary>
        private Vector2 ToWorldSpace(Vector2 input)
        {
            if (_cameraRig == null || input.sqrMagnitude <= 0f)
                return input;

            float yaw = _cameraRig.Yaw * Mathf.Deg2Rad;
            float sin = Mathf.Sin(yaw);
            float cos = Mathf.Cos(yaw);

            // Forward is (sin, cos) and right is (cos, -sin), matching a Y rotation.
            return new Vector2(
                input.x * cos + input.y * sin,
                input.y * cos - input.x * sin);
        }

        public override void CreateReconcile()
        {
            // Both sides build this. The server's copy is authority; the client's is a
            // fallback so a dropped reconcile packet doesn't stall the replay.
            PerformReconcile(new MotorState(transform.localPosition, _verticalVelocity));
        }

        [Replicate]
        private void Simulate(MoveData md, ReplicateState state = ReplicateState.Invalid,
            Channel channel = Channel.Unreliable)
        {
            // Always tick-delta, never Time.deltaTime — this method is replayed many
            // times per frame during reconciliation and must produce the same result
            // every time for the same input.
            float delta = (float)TimeManager.TickDelta;
            bool idle = false;

            /* State forwarding is on, so spectators run this too. Their copy of another
             * player's input necessarily arrives late, and a spectator that simply
             * freezes until data lands looks worse than one that guesses. Hold the last
             * known input for a single tick, then give up and idle. */
            if (!IsServerStarted && !IsOwner)
            {
                if (state.ContainsTicked())
                {
                    _lastTickedMove.Dispose();
                    _lastTickedMove = md;
                }
                else if (state.IsFuture())
                {
                    if (md.GetTick() - _lastTickedMove.GetTick() > 1)
                    {
                        idle = true;
                    }
                    else
                    {
                        md.Dispose();
                        md = _lastTickedMove;

                        // Never guess a jump. Holding jump across two ticks is unlikely
                        // and a wrong guess is the most visible kind of correction there is.
                        md.Jump = false;
                    }
                }
            }

            Vector3 motion;

            if (idle)
            {
                /* Passing Vector3.zero to a CharacterController lets other colliders creep
                 * into it, and repeated reconciles make that a certainty rather than a
                 * risk. A token downward nudge keeps the controller resolving contacts. */
                motion = new Vector3(0f, -1f, 0f);
            }
            else
            {
                // Pin to the ground rather than letting gravity accumulate while standing,
                // otherwise the first step off a ledge starts with a large stored velocity.
                if (_controller.isGrounded && _verticalVelocity < 0f)
                    _verticalVelocity = -2f;

                if (md.Jump && _controller.isGrounded)
                    _verticalVelocity = _jumpSpeed;

                _verticalVelocity += Physics.gravity.y * _gravityScale * delta;

                if (_verticalVelocity < _terminalVelocity)
                    _verticalVelocity = _terminalVelocity;

                // Clamp rather than normalize: a half-deflected stick should move slowly,
                // but a diagonal on the keyboard must not move faster than a straight line.
                Vector2 input = Vector2.ClampMagnitude(md.Move, 1f);

                motion = new Vector3(input.x, 0f, input.y) * _moveSpeed;
                motion.y = _verticalVelocity;
            }

            _controller.Move(motion * delta);

            /* Only from a ticked run, never a replay. Reconciliation replays many ticks
             * in a single frame, and letting those write here would make the animation
             * flicker through whatever the player was doing seconds ago. */
            if (state.ContainsTicked())
                PlanarSpeed = new Vector2(motion.x, motion.z).magnitude;
        }

        [Reconcile]
        private void PerformReconcile(MotorState state, Channel channel = Channel.Unreliable)
        {
            /* The CharacterController keeps its own idea of where it is. Writing the
             * transform while it is enabled moves the visual but leaves its collision at
             * the old position until the next simulate, which desyncs prediction from
             * collision in a way that is very hard to spot later. */
            _controller.enabled = false;
            transform.localPosition = state.Position;
            _controller.enabled = true;

            _verticalVelocity = state.VerticalVelocity;
        }
    }
}
