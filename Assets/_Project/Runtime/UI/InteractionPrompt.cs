using ChopChop.Core;
using ChopChop.Player;
using UnityEngine;

namespace ChopChop.UI
{
    /// <summary>
    /// Shows what pressing the interact key would do.
    ///
    /// **Deliberately IMGUI and deliberately plain**, for the same reason
    /// <see cref="ConnectMenu"/> is: the real HUD is being designed, and this should be
    /// deleted wholesale when it arrives rather than migrated. Building it in UI Toolkit
    /// now would mean guessing at the layout it has to live in, and guessing wrong is
    /// more expensive than starting again from one label.
    ///
    /// Reads the local player's interactor out of the <see cref="ServiceLocator"/>, so it
    /// does not care where the player object came from or when it arrived.
    /// </summary>
    public sealed class InteractionPrompt : MonoBehaviour
    {
        [SerializeField] private string _key = "E";

        [Tooltip("Height up the screen, 0 is the bottom. Sits under the crosshair rather " +
                 "than over it.")]
        [Range(0f, 1f)][SerializeField] private float _screenHeight = 0.42f;

        private PlayerInteractor _interactor;
        private GUIStyle _style;

        private void OnGUI()
        {
            if (!TryResolveInteractor())
                return;

            IInteractable target = _interactor.Current;

            if (target == null || string.IsNullOrEmpty(target.Prompt))
                return;

            _style ??= new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 18,
            };

            string text = $"[{_key}]  {target.Prompt}";
            float y = Screen.height * (1f - _screenHeight);
            Rect area = new(0f, y, Screen.width, 30f);

            // Cheap drop shadow, so the text survives a bright wall behind it.
            _style.normal.textColor = new Color(0f, 0f, 0f, 0.7f);
            GUI.Label(new Rect(area.x + 1f, area.y + 1f, area.width, area.height), text, _style);

            _style.normal.textColor = Color.white;
            GUI.Label(area, text, _style);
        }

        /// <summary>
        /// Re-resolved rather than cached once: the player is spawned into the world scene
        /// well after this exists, and is replaced on respawn.
        /// </summary>
        private bool TryResolveInteractor()
        {
            if (_interactor == null)
                ServiceLocator.TryGet(out _interactor);

            return _interactor != null;
        }
    }
}
