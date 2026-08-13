using System;
using ChopChop.Combat;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Utility.Template;
using UnityEngine;

namespace ChopChop.AI
{
    /// <summary>
    /// One enemy's behaviour (TECH 10.3).
    ///
    /// **Runs server-only, without exception.** Clients receive the transform and the
    /// state and play animations; they never run a line of this. That is what keeps four
    /// machines agreeing about where a wolf is and what it is doing, and it is why there
    /// is no prediction here — enemies are server-authoritative with no client-side
    /// guessing at all (TECH 4.3).
    ///
    /// Simulation runs on the tick, never in Update (TECH 4.2).
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(Health))]
    public sealed class EnemyBrain : TickNetworkBehaviour
    {
        [Header("Senses")]
        [Tooltip("How far it notices a player.")]
        [SerializeField] private float _aggroRadius = 22f;

        [Tooltip("Once chasing, how far a player must get to break away. Larger than " +
                 "aggro so a target on the edge does not flicker in and out of the chase.")]
        [SerializeField] private float _loseTargetRadius = 34f;

        [Header("Movement")]
        [SerializeField] private float _patrolSpeed = 1.8f;
        [SerializeField] private float _chaseSpeed = 5.2f;
        [SerializeField] private float _turnDegreesPerSecond = 540f;
        [SerializeField] private float _gravity = -18f;

        [Header("Attack")]
        [SerializeField] private float _attackRange = 2.4f;
        [SerializeField] private ushort _attackDamage = 12;

        [Tooltip("Seconds between strikes. Also how long the Attack state is held, so the " +
                 "animation has room to play.")]
        [SerializeField] private float _attackInterval = 1.4f;

        [Tooltip("Delay from entering Attack to damage landing, so the hit lands on the " +
                 "animation rather than the instant the state changes.")]
        [SerializeField] private float _attackWindup = 0.45f;

        [Header("Reactions")]
        [Tooltip("Seconds of stagger when hurt. Zero disables the flinch entirely.")]
        [SerializeField] private float _staggerSeconds = 0.35f;

        [Tooltip("How long the corpse remains before despawning, to let a death " +
                 "animation finish.")]
        [SerializeField] private float _corpseSeconds = 3f;

        private readonly SyncVar<EnemyState> _state = new();

        /// <summary>
        /// Planar speed, replicated so clients can blend a locomotion tree without
        /// guessing from transform deltas — which would be noisy at any real ping.
        /// </summary>
        private readonly SyncVar<float> _speed = new();

        private CharacterController _controller;
        private Health _health;
        private Transform _target;

        private float _verticalVelocity;
        private float _nextAttackTime;
        private float _attackLandsAt;
        private float _staggerUntil;
        private float _despawnAt;
        private Vector3 _patrolTarget;
        private bool _hasPatrolTarget;

        /// <summary>Current state. Read this to drive animation; never write it.</summary>
        public EnemyState State => _state.Value;

        /// <summary>Planar speed in metres per second, for locomotion blending.</summary>
        public float Speed => _speed.Value;

        /// <summary>Raised on every machine when the state changes.</summary>
        public event Action<EnemyState, EnemyState> StateChanged;

        /// <summary>Raised on every machine when a strike lands, for impact effects.</summary>
        public event Action Struck;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _health = GetComponent<Health>();

            _state.OnChange += HandleStateChanged;
            SetTickCallbacks(TickCallback.Tick);
        }

        private void OnDestroy()
        {
            _state.OnChange -= HandleStateChanged;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            _health.Died += HandleDied;
            _health.Changed += HandleHealthChanged;
        }

        public override void OnStopServer()
        {
            base.OnStopServer();

            _health.Died -= HandleDied;
            _health.Changed -= HandleHealthChanged;
        }

        private void HandleStateChanged(EnemyState previous, EnemyState next, bool asServer)
        {
            // Fires on clients too, which is the point: this is the animation trigger.
            StateChanged?.Invoke(previous, next);
        }

        private void HandleHealthChanged(ushort previous, ushort next)
        {
            if (!IsServerInitialized || next == 0 || next >= previous)
                return;

            if (_staggerSeconds <= 0f || _state.Value == EnemyState.Dead)
                return;

            _staggerUntil = Time.time + _staggerSeconds;
            SetState(EnemyState.Stagger);
        }

        private void HandleDied(Health health, NetworkConnection killer)
        {
            SetState(EnemyState.Dead);
            _speed.Value = 0f;

            // Left standing briefly so the death animation can play out before the object
            // disappears from under it.
            _despawnAt = Time.time + _corpseSeconds;
        }

        protected override void TimeManager_OnTick()
        {
            // Everything below is authority. A client reaching this would be deciding
            // where an enemy is, which is exactly what must never happen.
            if (!IsServerInitialized)
                return;

            float delta = (float)TimeManager.TickDelta;

            if (_state.Value == EnemyState.Dead)
            {
                if (Time.time >= _despawnAt)
                    Despawn();

                return;
            }

            AcquireTarget();

            if (_state.Value == EnemyState.Stagger && Time.time < _staggerUntil)
            {
                ApplyMotion(Vector3.zero, delta);
                return;
            }

            if (_target != null)
                TickCombat(delta);
            else
                TickIdle(delta);
        }

        /// <summary>
        /// Picks the nearest living player in range. Hysteresis between acquiring and
        /// losing keeps a target on the boundary from flickering the state, which would
        /// look like a twitching animation rather than a hunting animal.
        /// </summary>
        private void AcquireTarget()
        {
            float keepRadius = _target != null ? _loseTargetRadius : _aggroRadius;

            if (_target != null && !IsValidTarget(_target, keepRadius))
                _target = null;

            if (_target != null)
                return;

            float bestSqr = _aggroRadius * _aggroRadius;
            Transform best = null;

            foreach (NetworkConnection connection in NetworkManager.ServerManager.Clients.Values)
            {
                NetworkObject candidate = connection?.FirstObject;

                if (candidate == null)
                    continue;

                if (candidate.TryGetComponent(out Health health) && !health.IsAlive)
                    continue;

                float sqr = (candidate.transform.position - transform.position).sqrMagnitude;

                if (sqr > bestSqr)
                    continue;

                bestSqr = sqr;
                best = candidate.transform;
            }

            _target = best;
        }

        private bool IsValidTarget(Transform candidate, float radius)
        {
            if (candidate == null)
                return false;

            if (candidate.TryGetComponent(out Health health) && !health.IsAlive)
                return false;

            return (candidate.position - transform.position).sqrMagnitude <= radius * radius;
        }

        private void TickCombat(float delta)
        {
            Vector3 toTarget = _target.position - transform.position;
            toTarget.y = 0f;

            float distance = toTarget.magnitude;

            if (distance <= _attackRange)
            {
                ApplyMotion(Vector3.zero, delta);
                FaceTowards(toTarget, delta);
                TickAttack();
                return;
            }

            SetState(EnemyState.Chase);

            Vector3 direction = distance > 0.001f ? toTarget / distance : transform.forward;
            FaceTowards(direction, delta);
            ApplyMotion(direction * _chaseSpeed, delta);
        }

        private void TickAttack()
        {
            // Damage lands partway through, so the strike connects on the animation
            // rather than the moment the state flips.
            if (_attackLandsAt > 0f && Time.time >= _attackLandsAt)
            {
                _attackLandsAt = 0f;
                LandStrike();
            }

            if (Time.time < _nextAttackTime)
                return;

            _nextAttackTime = Time.time + _attackInterval;
            _attackLandsAt = Time.time + _attackWindup;
            SetState(EnemyState.Attack);
        }

        private void LandStrike()
        {
            if (_target == null || !IsValidTarget(_target, _attackRange + 0.75f))
                return;

            if (_target.TryGetComponent(out Health health))
                health.TryApplyDamage(_attackDamage, null);

            NotifyStruck();
        }

        [ObserversRpc(RunLocally = true)]
        private void NotifyStruck() => Struck?.Invoke();

        private void TickIdle(float delta)
        {
            if (!_hasPatrolTarget || (transform.position - _patrolTarget).sqrMagnitude < 4f)
            {
                Vector2 offset = UnityEngine.Random.insideUnitCircle * 12f;
                _patrolTarget = transform.position + new Vector3(offset.x, 0f, offset.y);
                _hasPatrolTarget = true;
            }

            Vector3 toTarget = _patrolTarget - transform.position;
            toTarget.y = 0f;

            if (toTarget.sqrMagnitude < 1f)
            {
                SetState(EnemyState.Idle);
                ApplyMotion(Vector3.zero, delta);
                return;
            }

            SetState(EnemyState.Patrol);

            Vector3 direction = toTarget.normalized;
            FaceTowards(direction, delta);
            ApplyMotion(direction * _patrolSpeed, delta);
        }

        private void FaceTowards(Vector3 direction, float delta)
        {
            direction.y = 0f;

            if (direction.sqrMagnitude < 0.0001f)
                return;

            Quaternion wanted = Quaternion.LookRotation(direction);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, wanted, _turnDegreesPerSecond * delta);
        }

        private void ApplyMotion(Vector3 planarVelocity, float delta)
        {
            if (_controller.isGrounded && _verticalVelocity < 0f)
                _verticalVelocity = -2f;

            _verticalVelocity += _gravity * delta;

            Vector3 motion = planarVelocity;
            motion.y = _verticalVelocity;

            _controller.Move(motion * delta);

            // Replicated rather than derived from transform deltas, which would be noisy
            // on a client and make a locomotion blend jitter.
            _speed.Value = new Vector2(planarVelocity.x, planarVelocity.z).magnitude;
        }

        private void SetState(EnemyState next)
        {
            if (_state.Value == next || _state.Value == EnemyState.Dead)
                return;

            _state.Value = next;
        }

        private void Despawn()
        {
            if (IsServerInitialized)
                base.Despawn(gameObject);
        }
    }
}
