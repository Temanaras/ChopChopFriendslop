using ChopChop.Combat;
using ChopChop.Player;
using TMPro;
using UnityEngine;

namespace ChopChop.UI
{
    /// <summary>
    /// Rounds in the magazine, shown only while the gun is out.
    ///
    /// Visibility follows <see cref="PlayerLoadout"/> rather than being always on: the
    /// axe and the gun share the primary button, so which tool is held is already the
    /// most load-bearing fact on screen, and an ammo count under an axe would say the
    /// wrong thing about what pressing fire is about to do.
    /// </summary>
    public sealed class AmmoView : PlayerBoundView
    {
        [SerializeField] private TMP_Text _label;
        [SerializeField] private CanvasGroup _group;

        protected override void Bind(LocalPlayer player)
        {
            if (player.Ammo != null)
                player.Ammo.Changed += HandleAmmoChanged;

            if (player.Loadout != null)
                player.Loadout.SelectionChanged += HandleToolChanged;

            if (player.Ammo != null)
                HandleAmmoChanged(player.Ammo.Current, player.Ammo.Capacity);

            ApplyVisibility();
        }

        protected override void Unbind(LocalPlayer player)
        {
            if (player.Ammo != null)
                player.Ammo.Changed -= HandleAmmoChanged;

            if (player.Loadout != null)
                player.Loadout.SelectionChanged -= HandleToolChanged;
        }

        private void HandleAmmoChanged(ushort current, ushort capacity)
        {
            if (_label != null)
                _label.text = $"{current} / {capacity}";
        }

        private void HandleToolChanged(PlayerLoadout.Tool tool) => ApplyVisibility();

        private void ApplyVisibility()
        {
            if (_group == null)
                return;

            bool holdingGun = Player?.Loadout != null
                              && Player.Loadout.IsHolding(PlayerLoadout.Tool.Gun);

            _group.alpha = holdingGun ? 1f : 0f;
        }
    }
}
