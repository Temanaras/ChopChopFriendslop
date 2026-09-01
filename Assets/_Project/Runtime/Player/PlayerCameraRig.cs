using ChopChop.Core;
using ChopChop.World;
using FishNet.Object;
using UnityEngine;

namespace ChopChop.Player
{
    /// <summary>
    /// The local player's camera: switches it on, turns it with the mouse, and keeps the
    /// boom out of the scenery.
    ///
    /// Presentation is client-local by construction (TECH 2.1), so there is no networked
    /// state here — two players looking in different directions is not a disagreement,
    /// and neither is one of them standing closer to a wall than the other.
    ///
    /// The rig is parented under the graphical child rather than the predicted root.
    /// FishNet smooths the graphical object between ticks and after reconciliation; a
    /// camera on the root instead inherits every correction as a jolt.
    ///
    /// Yaw is not purely cosmetic: <see cref="PlayerMotor"/> reads <see cref="Yaw"/> to
    /// turn stick input into world-space movement, because a camera you can turn while
    /// W still walks toward world north is worse than no camera control at all.
    /// </summary>
    public sealed class PlayerCameraRig : NetworkBehaviour
    {
        [Tooltip("Camera object to switch on for the owning client. Should be a child of " +
                 "the NetworkObject's graphical object, not of the root.")]
        [SerializeField] private GameObject _camera;

        [Tooltip("What actually rotates. The camera hangs off this on a local -Z boom, so " +
                 "pitching the pivot orbits the camera around the player rather than " +
                 "tilting it in place.")]
        [SerializeField] private Transform _pivot;

        [Header("Look")]
        [Tooltip("Degrees per unit of look input. Mouse deltas arrive in counts, so this " +
                 "is roughly degrees per pixel.")]
        [SerializeField] private float _sensitivity = 0.12f;

        [Tooltip("How far down the player can look. The boom pulls in on the ground now " +
                 "rather than passing through it, so this is a framing choice.")]
        [SerializeField] private float _minPitch = -30f;

        [Tooltip("How far up. Kept below 90 so the camera never passes over the head and " +
                 "flips the horizon.")]
        [SerializeField] private float _maxPitch = 70f;

        [SerializeField] private bool _invertY;

        [Tooltip("Capture the cursor while playing. Escape releases it in the editor.")]
        [SerializeField] private bool _captureCursor = true;

        [Header("Obstruction")]
        [Tooltip("What the boom is allowed to collide with. No custom layers exist yet, so " +
                 "this is everything but Ignore Raycast; putting the player on its own " +
                 "layer later is a change to this field, not to the code.")]
        [SerializeField] private LayerMask _obstruction = ~(1 << 2);

        [Tooltip("Radius of the probe. The near plane's corner sits 0.13m off-axis at 16:9 " +
                 "and 0.16m at 21:9, so 0.2 clears both. Much larger and the camera pulls " +
                 "in at doorways it would have fitted through.")]
        [SerializeField] private float _probeRadius = 0.2f;

        [Tooltip("How close the camera may get before it stops retreating. Below about 0.4 " +
                 "the near plane starts slicing the back of the head.")]
        [SerializeField] private float _minDistance = 0.6f;

        [Tooltip("Metres per second the boom extends once the obstruction clears. Pulling " +
                 "in is instant; only the way back out is smoothed.")]
        [SerializeField] private float _returnSpeed = 6f;

        private PlayerInputReader _input;
        private bool _owns;

        private Transform _cameraTransform;
        private RaycastHit[] _hits;
        private float _boom;
        private float _maxBoom;

        /// <summary>Heading in degrees. Read by the motor to orient movement.</summary>
        public float Yaw { get; private set; }

        /// <summary>Elevation in degrees, negative looking down.</summary>
        public float Pitch { get; private set; }

        private void Awake()
        {
            _input = GetComponent<PlayerInputReader>();
            _hits = new RaycastHit[8];

            /* The authored boom length lives in the prefab, not in a constant here, so
             * there is one number to change and it is the one visible in the scene. */
            if (_camera != null)
            {
                _cameraTransform = _camera.transform;
                _maxBoom = -_cameraTransform.localPosition.z;
            }

            if (_maxBoom <= 0f)
                _maxBoom = 3f;

            _boom = _maxBoom;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            if (_camera == null)
            {
                Debug.LogError($"[Player] No camera assigned on {name}.");
                return;
            }

            // Every client has exactly one owned player, so exactly one camera and one
            // AudioListener end up active.
            _camera.SetActive(IsOwner);

            if (!IsOwner)
                return;

            _owns = true;

            /* Start facing wherever the body was placed, so the first frame is not a
             * lurch from world north to whatever the spawn happened to choose. */
            Yaw = transform.eulerAngles.y;
            ApplyRotation();

            /* And solve the boom before the first frame is drawn rather than during it.
             * A player spawned indoors would otherwise still get exactly one frame of
             * wall, which is the whole complaint this was written to answer. */
            SolveBoom(0f);

            /* The cursor belongs to whichever screen is open, not to this component.
             * Owning it here and handing it over on request keeps one writer, so a
             * panel closing cannot leave the pointer stranded over a captured game. */
            UiFocus.Changed += ApplyCursor;
            ApplyCursor();

            /* The forest streams around whoever is playing here. Found rather than
             * injected because the streamer lives in the world scene and the player is
             * spawned into it by the server — neither can hold a serialised reference to
             * the other. */
            WorldStreamer streamer = FindObjectOfType<WorldStreamer>();

            if (streamer != null)
                streamer.SetCentre(transform);

            if (Core.ServiceLocator.TryGet(out TreeClient trees) && TryGetComponent(out PlayerChopper chopper))
                chopper.Bind(trees);
        }

        public override void OnStopClient()
        {
            base.OnStopClient();

            if (_owns)
            {
                UiFocus.Changed -= ApplyCursor;
                SetCursorCaptured(false);
            }

            _owns = false;
        }

        /* LateUpdate, not Update: the prediction smoother writes the graphical object's
         * transform during the frame, and a camera that reads its parent before that
         * lands trails the body by a frame. Rotation here is local to the pivot, so it
         * survives whatever the smoother does above it.
         *
         * Not OnTick either, despite the boom being a physics query. Boom length is
         * presentation (TECH 2.1) — never sent, never reconciled — and solving it at tick
         * rate would both look steppy and read collider positions before the smoother has
         * placed the body for the frame. */
        private void LateUpdate()
        {
            if (!_owns)
                return;

            /* Looking is gated on the reader; the boom is not. Another player can walk
             * you into a wall while your inventory is open. */
            if (_input != null)
            {
                Vector2 look = _input.LookInput;

                // No deltaTime. See PlayerInputReader.LookInput.
                Yaw += look.x * _sensitivity;
                Pitch += (_invertY ? look.y : -look.y) * _sensitivity;

                Yaw = Mathf.Repeat(Yaw, 360f);
                Pitch = Mathf.Clamp(Pitch, _minPitch, _maxPitch);
            }

            ApplyRotation();

            // After ApplyRotation, so the cast uses this frame's final pivot orientation.
            SolveBoom(Time.deltaTime);
        }

        /// <summary>
        /// Set in world space rather than locally: the pivot hangs under the graphical
        /// object, and if anything ever rotates that, a local rotation would compose with
        /// it and the camera would drift away from where the player is pointing.
        /// </summary>
        private void ApplyRotation()
        {
            if (_pivot != null)
                _pivot.rotation = Quaternion.Euler(Pitch, Yaw, 0f);
        }

        /// <summary>
        /// Pulls the camera in when there is scenery between it and the head.
        ///
        /// The probe starts inside our own CharacterController and nothing sits on a
        /// dedicated player layer, so self-hits are rejected by hierarchy rather than by
        /// mask. One check covers the controller, the model and the axe socketed to its
        /// hand, because all three hang under this transform.
        /// </summary>
        private void SolveBoom(float deltaTime)
        {
            if (_cameraTransform == null || _pivot == null)
                return;

            bool blocked = false;
            float nearest = _maxBoom;

            int count = Physics.SphereCastNonAlloc(
                _pivot.position, _probeRadius, -_pivot.forward, _hits, _maxBoom,
                _obstruction, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < count; i++)
            {
                if (_hits[i].collider == null || _hits[i].collider.transform.IsChildOf(transform))
                    continue;

                blocked = true;

                if (_hits[i].distance < nearest)
                    nearest = _hits[i].distance;
            }

            _boom = ResolveBoom(blocked, nearest, _boom, _minDistance, _maxBoom,
                _returnSpeed, deltaTime);

            _cameraTransform.localPosition = new Vector3(0f, 0f, -_boom);
        }

        /// <summary>
        /// Where the camera should sit this frame, given what the probe found.
        ///
        /// Pure, so it can be tested without a scene. The asymmetry is the whole design:
        /// closing the gap must be instant, because a smoothed approach spends frames with
        /// the camera inside geometry and backface culling turns that into a hole in the
        /// world — worse than the problem being solved. Opening it must not be, because
        /// occlusion is binary and snapping out lurches every time you step past a trunk.
        ///
        /// MoveTowards rather than the exponential damping the animation bridges use: that
        /// shape never quite arrives, and its speed depends on how far it has to go, which
        /// here would mean a camera that returns faster in open ground than in a doorway.
        /// </summary>
        public static float ResolveBoom(bool blocked, float hitDistance, float current,
                                        float min, float max, float returnSpeed, float deltaTime)
        {
            float target = blocked ? Mathf.Clamp(hitDistance, min, max) : max;

            if (target <= current)
                return target;

            return Mathf.MoveTowards(current, target, returnSpeed * deltaTime);
        }

        private void ApplyCursor() => SetCursorCaptured(!UiFocus.WantsCursor);

        private void SetCursorCaptured(bool captured)
        {
            if (!_captureCursor)
                return;

            Cursor.lockState = captured ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !captured;
        }
    }
}
