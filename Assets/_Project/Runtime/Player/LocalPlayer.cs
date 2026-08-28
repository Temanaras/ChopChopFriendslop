using System;
using ChopChop.Combat;
using ChopChop.Core;
using FishNet.Object;
using UnityEngine;

namespace ChopChop.Player
{
    /// <summary>
    /// The player this machine is actually playing. One handle for everything a HUD has
    /// to read.
    ///
    /// Without it every view resolves <see cref="PlayerInteractor"/> from the
    /// <see cref="ServiceLocator"/> — the only thing that registers itself owner-only —
    /// and then reaches sideways with GetComponent for the thing it actually wanted.
    /// That works, and it means the health bar depends on the interaction system for no
    /// reason. This is the same registration, done once, for the components a screen
    /// legitimately needs.
    ///
    /// Registered on the owning client only, so on a hosted server the server's own
    /// copies of other players never claim the slot.
    /// </summary>
    public sealed class LocalPlayer : NetworkBehaviour
    {
        /// <summary>
        /// Raised when the local player arrives or goes away. Views may also poll
        /// <see cref="ServiceLocator.TryGet{T}"/> — this exists so a view that is already
        /// bound knows to rebind rather than holding a destroyed component.
        /// </summary>
        public static event Action Changed;

        private bool _owns;

        public PlayerPaperdoll Paperdoll { get; private set; }
        public PlayerLoadout Loadout { get; private set; }
        public PlayerInteractor Interactor { get; private set; }
        public PlayerChopper Chopper { get; private set; }
        public Health Health { get; private set; }
        public WeaponAmmo Ammo { get; private set; }

        private void Awake()
        {
            TryGetComponent(out PlayerPaperdoll paperdoll);
            TryGetComponent(out PlayerLoadout loadout);
            TryGetComponent(out PlayerInteractor interactor);
            TryGetComponent(out PlayerChopper chopper);
            TryGetComponent(out Health health);
            TryGetComponent(out WeaponAmmo ammo);

            Paperdoll = paperdoll;
            Loadout = loadout;
            Interactor = interactor;
            Chopper = chopper;
            Health = health;
            Ammo = ammo;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            if (!IsOwner)
                return;

            _owns = true;
            ServiceLocator.Register(this);
            Changed?.Invoke();
        }

        public override void OnStopClient()
        {
            base.OnStopClient();

            if (!_owns)
                return;

            _owns = false;
            ServiceLocator.Unregister<LocalPlayer>();
            Changed?.Invoke();
        }
    }
}
