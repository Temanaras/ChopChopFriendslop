using ChopChop.Core;
using UnityEngine;

namespace ChopChop.UI
{
    /// <summary>
    /// Decides whether there is a HUD on screen at all.
    ///
    /// The canvas outlives the boot scene (see <see cref="UiRoot"/>), so it is up during
    /// the start screen and during a connect that may never succeed. A health bar drawn
    /// over the main menu is not a cosmetic problem — it says a game is running when one
    /// is not.
    ///
    /// Watches <see cref="AppStateMachine"/> rather than the presence of a player: the
    /// two disagree for the seconds between a client arriving in the world scene and its
    /// player object spawning, and the state machine is the thing that already knows
    /// which of those is happening.
    /// </summary>
    public sealed class HudController : MonoBehaviour
    {
        [Tooltip("Everything that should only exist during play.")]
        [SerializeField] private CanvasGroup _hud;

        private AppStateMachine _state;

        private void OnEnable() => Apply();

        private void OnDisable()
        {
            if (_state != null)
                _state.StateChanged -= HandleStateChanged;

            _state = null;
        }

        /* Resolved here rather than in OnEnable because the bootstrap registers the state
         * machine in its own Awake, and the order between two Awakes in the same scene is
         * not something to rely on. Cheap: a dictionary lookup until it succeeds once. */
        private void Update()
        {
            if (_state != null)
                return;

            if (!ServiceLocator.TryGet(out _state))
                return;

            _state.StateChanged += HandleStateChanged;
            Apply();
        }

        private void HandleStateChanged(AppState previous, AppState next) => Apply();

        private void Apply()
        {
            if (_hud == null)
                return;

            bool playing = _state != null && _state.Current == AppState.InGame;

            _hud.alpha = playing ? 1f : 0f;
            _hud.blocksRaycasts = playing;
        }
    }
}
