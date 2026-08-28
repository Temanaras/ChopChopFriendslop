using ChopChop.Core;
using ChopChop.Player;
using UnityEngine;

namespace ChopChop.UI
{
    /// <summary>
    /// Where the camera is pointing. Both the chop raycast and the shot trace start from
    /// the crosshair, so it is the only honest answer to "what am I about to hit" in a
    /// third-person camera that sits metres behind the body (TECH 10.2).
    /// </summary>
    public sealed class CrosshairView : PlayerBoundView
    {
        [SerializeField] private CanvasGroup _group;

        protected override void Bind(LocalPlayer player) { }
        protected override void Unbind(LocalPlayer player) { }

        private void Update()
        {
            if (_group == null)
                return;

            // Nothing to aim with behind an open screen, and the cursor is the pointer
            // there instead.
            _group.alpha = Player != null && !UiFocus.GameplayBlocked ? 1f : 0f;
        }
    }
}
