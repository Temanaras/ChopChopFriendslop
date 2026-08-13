using ChopChop.Combat;
using FishNet.Managing;
using FishNet.Object;
using FishNet.Transporting;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

namespace ChopChop.Player
{
    /// <summary>
    /// Fires the gun, optimistically (TECH 10.2).
    ///
    /// The muzzle flash, tracer and impact all play here and now, before the server has
    /// any opinion. That is the entire point: at any real ping, waiting for confirmation
    /// makes a gun feel broken. A shot the server refuses simply deals no damage, and
    /// nothing is rolled back.
    ///
    /// Owner-only, and it decides nothing.
    /// </summary>
    public sealed class PlayerWeapon : NetworkBehaviour
    {
        [Header("Aim")]
        [Tooltip("Where the shot comes from. Must be on the player's body, not the " +
                 "camera — the server checks the origin against where it has the player, " +
                 "and a third-person camera sits metres behind them.")]
        [SerializeField] private Transform _muzzle;

        [Tooltip("What the player is looking at. Supplies direction only.")]
        [SerializeField] private Transform _aim;

        [SerializeField] private float _range = 120f;
        [SerializeField] private LayerMask _hitMask = ~0;

        [Header("Feel")]
        [Tooltip("Client-side rate limit so held fire looks right. The server enforces " +
                 "the real one and does not trust this.")]
        [SerializeField] private float _localCooldownSeconds = 0.18f;

        [Header("Presentation")]
        [Tooltip("Fired the instant the trigger is pulled. Hook the muzzle flash, the " +
                 "sound and the recoil animation here.")]
        public UnityEvent Fired;

        [Tooltip("Fired with the impact point of the local, optimistic trace.")]
        public UnityEvent<Vector3> ImpactedAt;

        private InputAction _fire;
        private NetworkManager _manager;
        private PlayerLoadout _loadout;
        private float _nextAllowedTime;

        public override void OnStartClient()
        {
            base.OnStartClient();

            if (!IsOwner)
            {
                enabled = false;
                return;
            }

            _manager = NetworkManager;
            TryGetComponent(out _loadout);

            InputActionAsset actions = InputSystem.actions;
            _fire = actions != null ? actions.FindAction("Player/Attack") : null;

            if (_fire == null)
                Debug.LogError("[Weapon] No Player/Attack action; the gun is disabled.");
            else
                _fire.Enable();
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            _fire?.Disable();
        }

        private void Update()
        {
            if (!IsOwner || _fire == null)
                return;

            // Shares the primary button with the axe; only the held tool acts.
            if (_loadout != null && !_loadout.IsHolding(PlayerLoadout.Tool.Gun))
                return;

            if (!_fire.IsPressed() || Time.time < _nextAllowedTime)
                return;

            _nextAllowedTime = Time.time + _localCooldownSeconds;
            Fire();
        }

        private void Fire()
        {
            Transform muzzle = _muzzle != null ? _muzzle : transform;
            Vector3 origin = muzzle.position;

            // Draw first, ask afterwards.
            Fired?.Invoke();

            Vector3 direction = ResolveDirection(origin);

            Vector3 end = Physics.Raycast(origin, direction, out RaycastHit info, _range, _hitMask)
                ? info.point
                : origin + direction * _range;

            ImpactedAt?.Invoke(end);

            /* Only where and which way. What was hit, and whether it took damage, is the
             * server's to work out from its own world — otherwise a client could claim
             * any hit it liked. */
            _manager.ClientManager.Broadcast(new FireRequestBroadcast
            {
                Origin = origin,
                Direction = direction,
                Tick = _manager.TimeManager.Tick,
            }, Channel.Reliable);
        }

        /// <summary>
        /// Aim toward whatever the camera is pointing at, from the body.
        ///
        /// Firing straight down the camera's forward would send the shot parallel to the
        /// view from a point metres in front of it, so it would miss what the crosshair
        /// is on. Tracing from the camera first and then aiming the body at that point is
        /// what makes a third-person shot land where the player is looking.
        /// </summary>
        private Vector3 ResolveDirection(Vector3 origin)
        {
            if (_aim == null)
                return (_muzzle != null ? _muzzle : transform).forward;

            Vector3 lookPoint = Physics.Raycast(_aim.position, _aim.forward, out RaycastHit look, _range, _hitMask)
                ? look.point
                : _aim.position + _aim.forward * _range;

            Vector3 direction = lookPoint - origin;

            return direction.sqrMagnitude > 0.0001f ? direction.normalized : _aim.forward;
        }
    }
}
