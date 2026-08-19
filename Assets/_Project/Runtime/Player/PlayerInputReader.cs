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
        private InputAction _look;
        private InputAction _jump;
        private InputAction _sprint;
        private InputAction _interact;
        private bool _interactLatched;
        private bool _jumpLatched;
        private bool _resolved;

        /// <summary>
        /// Movement on the XZ plane, relative to the camera. <see cref="PlayerMotor"/>
        /// rotates it into world space before it becomes a tick's input.
        /// </summary>
        public Vector2 MoveInput => _move?.ReadValue<Vector2>() ?? Vector2.zero;

        /// <summary>
        /// Look delta for this frame, in mouse counts rather than degrees.
        ///
        /// Already a per-frame delta, so it must never be scaled by
        /// <c>Time.deltaTime</c> — a mouse that moved 40 counts moved 40 counts whether
        /// the frame took 4ms or 40. A gamepad stick on the same action reports a
        /// position rather than a delta and would need that scaling, which is the reason
        /// this is exposed raw instead of pre-converted.
        /// </summary>
        public Vector2 LookInput => _look?.ReadValue<Vector2>() ?? Vector2.zero;

        /// <summary>
        /// Whether sprint is held right now. Sampled at tick time like movement rather
        /// than latched like jump: holding it is the whole interaction, so there is no
        /// brief press to miss between ticks.
        /// </summary>
        public bool SprintHeld => _sprint?.IsPressed() ?? false;

        /* Actions are resolved on enable rather than in Awake, and this component is
         * only enabled for the owning client. Unity runs Awake even on disabled
         * components, so resolving there would have every player on a headless server
         * reach into the Input System and switch action maps on — for players who are
         * not on that machine and inputs nobody will ever read. */
        private void OnEnable()
        {
            if (!TryResolveActions())
            {
                enabled = false;
                return;
            }

            _move.Enable();
            _look.Enable();
            _jump.Enable();
            _sprint.Enable();
            _interact.Enable();
        }

        private bool TryResolveActions()
        {
            if (_resolved)
                return _move != null && _look != null && _jump != null
                       && _sprint != null && _interact != null;

            _resolved = true;

            InputActionAsset actions = InputSystem.actions;

            if (actions == null)
            {
                Debug.LogError(
                    "[Input] No project-wide input actions asset is assigned " +
                    "(Project Settings > Input System Package). Player input is disabled.");
                return false;
            }

            _move = actions.FindAction("Player/Move");
            _look = actions.FindAction("Player/Look");
            _jump = actions.FindAction("Player/Jump");
            _sprint = actions.FindAction("Player/Sprint");
            _interact = actions.FindAction("Player/Interact");

            if (_move != null && _look != null && _jump != null && _sprint != null && _interact != null)
                return true;

            Debug.LogError("[Input] The project-wide actions asset is missing one of " +
                           "Player/Move, Player/Look, Player/Jump, Player/Sprint or Player/Interact.");
            return false;
        }

        private void OnDisable()
        {
            _move?.Disable();
            _look?.Disable();
            _jump?.Disable();
            _sprint?.Disable();
            _interact?.Disable();
            _interactLatched = false;

            // Don't let a press survive across a disable and fire on re-enable.
            _jumpLatched = false;
        }

        private void Update()
        {
            if (_jump != null && _jump.WasPressedThisFrame())
                _jumpLatched = true;

            if (_interact != null && _interact.WasPressedThisFrame())
                _interactLatched = true;
        }

        /// <summary>
        /// Reads and clears the latched interact press. Latched rather than polled so a
        /// tap between two reads is not lost — the same reason jump is.
        /// </summary>
        public bool ConsumeInteract()
        {
            bool pressed = _interactLatched;
            _interactLatched = false;
            return pressed;
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
