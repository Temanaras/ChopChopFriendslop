using ChopChop.Core;
using ChopChop.Player;
using TMPro;
using UnityEngine;

namespace ChopChop.UI
{
    /// <summary>
    /// Shows what pressing the interact key would do. The Canvas replacement for the
    /// IMGUI prompt this project shipped Milestone 1 with.
    ///
    /// Polled rather than event-driven, deliberately: <see cref="PlayerInteractor"/>
    /// recomputes its best candidate every frame from the player's position and facing,
    /// so there is no moment to raise an event on that is not simply "every frame". The
    /// work is a string comparison.
    /// </summary>
    public sealed class InteractionPromptView : PlayerBoundView
    {
        [SerializeField] private TMP_Text _label;
        [SerializeField] private CanvasGroup _group;

        [Tooltip("Glyph shown in the brackets. Matches Player/Interact in the actions " +
                 "asset; there is no rebinding yet for it to read from.")]
        [SerializeField] private string _key = "E";

        private string _shown;

        protected override void Bind(LocalPlayer player) { }
        protected override void Unbind(LocalPlayer player) { }

        private void Update()
        {
            string prompt = CurrentPrompt();

            if (_group != null)
                _group.alpha = string.IsNullOrEmpty(prompt) ? 0f : 1f;

            if (prompt == _shown)
                return;

            _shown = prompt;

            if (_label != null && !string.IsNullOrEmpty(prompt))
                _label.text = $"[{_key}]  {prompt}";
        }

        private string CurrentPrompt()
        {
            // A screen is open; the interact key belongs to it, so promising a world
            // action would be a lie.
            if (UiFocus.GameplayBlocked)
                return null;

            IInteractable target = Player?.Interactor?.Current;

            return target == null || string.IsNullOrEmpty(target.Prompt) ? null : target.Prompt;
        }
    }
}
