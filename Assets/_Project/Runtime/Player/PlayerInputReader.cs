using UnityEngine;
using UnityEngine.InputSystem;

namespace ChopChop.Player
{
    /// <summary>
    /// Samples local input, and only on the machine that owns the player. Input has
    /// exactly one source and travels in one direction (TECH 2.1); this component is
    /// disabled everywhere else, so there is no second opinion about what a player
    /// pressed.
    ///
    /// Held state (movement) is read at tick time. One-shot state (jump) is latched
    /// here in <see cref="Update"/> instead, because at a 30Hz tick a press and its
    /// release can both land inside a single tick and would otherwise never be seen.
    /// </summary>
    public sealed class PlayerInputReader : MonoBehaviour
    {
        private InputAction _move;
        private InputAction _jump;
        private bool _jumpLatched;

        /// <summary>Movement on the XZ plane, unrotated. Camera-relative movement comes later.</summary>
        public Vector2 MoveInput => _move?.ReadValue<Vector2>() ?? Vector2.zero;

        private void Awake()
        {
            InputActionAsset actions = InputSystem.actions;

            if (actions == null)
            {
                Debug.LogError(
                    "[Input] No project-wide input actions asset is assigned " +
                    "(Project Settings > Input System Package). Player input is disabled.");
                enabled = false;
                return;
            }

            _move = actions.FindAction("Player/Move");
            _jump = actions.FindAction("Player/Jump");

            if (_move == null || _jump == null)
            {
                Debug.LogError(
                    "[Input] The project-wide actions asset has no Player/Move or Player/Jump action.");
                enabled = false;
            }
        }

        private void OnEnable()
        {
            _move?.Enable();
            _jump?.Enable();
        }

        private void OnDisable()
        {
            _move?.Disable();
            _jump?.Disable();

            // Don't let a press survive across a disable and fire on re-enable.
            _jumpLatched = false;
        }

        private void Update()
        {
            if (_jump != null && _jump.WasPressedThisFrame())
                _jumpLatched = true;
        }

        /// <summary>
        /// Reads and clears the latched jump. Call exactly once per tick — a second
        /// call in the same tick would report no jump.
        /// </summary>
        public bool ConsumeJump()
        {
            bool jump = _jumpLatched;
            _jumpLatched = false;
            return jump;
        }
    }
}
