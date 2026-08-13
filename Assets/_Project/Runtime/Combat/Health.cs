using System;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace ChopChop.Combat
{
    /// <summary>
    /// Anything that can be shot, bitten, or killed.
    ///
    /// **Server-authoritative without exception.** Clients read the value to draw a bar
    /// and play a flinch; they never write it and never decide a death. A client that
    /// believes something is dead when the server does not is a client that has stopped
    /// playing the same game as everyone else.
    /// </summary>
    public sealed class Health : NetworkBehaviour
    {
        [SerializeField] private ushort _maximum = 100;

        private readonly SyncVar<ushort> _current = new();

        /// <summary>Raised on both sides whenever the value changes, for bars and flinches.</summary>
        public event Action<ushort, ushort> Changed;

        /// <summary>Raised on the server only. Nothing else may decide something has died.</summary>
        public event Action<Health, NetworkConnection> Died;

        public ushort Maximum => _maximum;
        public ushort Current => _current.Value;
        public bool IsAlive => _current.Value > 0;
        public float Normalized => _maximum == 0 ? 0f : (float)_current.Value / _maximum;

        private void Awake()
        {
            _current.OnChange += HandleChanged;
        }

        private void OnDestroy()
        {
            _current.OnChange -= HandleChanged;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            // Set here rather than in Awake: SyncVars only have somewhere to send to once
            // the object is network-initialised.
            _current.Value = _maximum;
        }

        private void HandleChanged(ushort previous, ushort next, bool asServer) => Changed?.Invoke(previous, next);

        /// <summary>
        /// Applies damage. Server-only by construction — there is no client path.
        /// </summary>
        /// <returns>False when already dead, so callers can skip effects and loot.</returns>
        public bool TryApplyDamage(ushort amount, NetworkConnection source)
        {
            if (!IsServerInitialized)
            {
                Debug.LogError("[Health] Damage attempted off the server; ignoring.");
                return false;
            }

            if (!IsAlive)
                return false;

            // Clamp rather than subtract: ushort would wrap a nearly-dead thing back to
            // full health, which is the same class of bug as tree damage had.
            ushort remaining = amount >= _current.Value ? (ushort)0 : (ushort)(_current.Value - amount);
            _current.Value = remaining;

            if (remaining == 0)
                Died?.Invoke(this, source);

            return true;
        }

        /// <summary>Server-only. Used by respawns and, later, healing items.</summary>
        public void ResetToFull()
        {
            if (IsServerInitialized)
                _current.Value = _maximum;
        }
    }
}
