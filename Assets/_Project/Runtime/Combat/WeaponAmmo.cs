using System;
using FishNet.Object;
using FishNet.Object.Synchronizing;

namespace ChopChop.Combat
{
    /// <summary>
    /// The ammo count, in a form a client can actually read.
    ///
    /// <see cref="WeaponServer"/> owns the real number — it is what refuses a shot from
    /// an empty magazine, and it must stay server-side for that to mean anything. This
    /// mirrors it outward so a HUD has something to bind to.
    ///
    /// Owner-only: your magazine is nobody else's business, and sending it to every
    /// client would be both wasted bandwidth and free information.
    /// </summary>
    public sealed class WeaponAmmo : NetworkBehaviour
    {
        private readonly SyncVar<ushort> _current = new(new SyncTypeSettings(ReadPermission.OwnerOnly));
        private readonly SyncVar<ushort> _capacity = new(new SyncTypeSettings(ReadPermission.OwnerOnly));

        /// <summary>Rounds left in the magazine.</summary>
        public ushort Current => _current.Value;

        /// <summary>Magazine size, so a HUD can render "8 / 12" without a second source.</summary>
        public ushort Capacity => _capacity.Value;

        /// <summary>Raised on the owning client whenever the count changes.</summary>
        public event Action<ushort, ushort> Changed;

        private void Awake()
        {
            _current.OnChange += HandleChanged;
        }

        private void OnDestroy()
        {
            _current.OnChange -= HandleChanged;
        }

        private void HandleChanged(ushort previous, ushort next, bool asServer)
            => Changed?.Invoke(next, _capacity.Value);

        /// <summary>Server-only. Called by the weapon server after every accepted shot.</summary>
        public void Set(ushort current, ushort capacity)
        {
            if (!IsServerInitialized)
                return;

            _current.Value = current;
            _capacity.Value = capacity;
        }
    }
}
