using System;
using ChopChop.Items;
using FishNet.Object;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ChopChop.Player
{
    /// <summary>
    /// Which tool the primary action currently uses.
    ///
    /// A deliberate stand-in, not the real thing. Once the paperdoll exists this becomes
    /// a read of the <see cref="ItemSlot.Axe"/> and <see cref="ItemSlot.Gun"/> slots and
    /// the tier gating comes with it (TECH 9.2). For now it exists because the axe and
    /// the gun both want the primary button, and having them both fire on one click is
    /// worse than either.
    ///
    /// Client-local: this only decides which local system reacts to a button. Both of
    /// them still ask the server for permission, so nothing here is worth cheating.
    /// </summary>
    public sealed class PlayerLoadout : NetworkBehaviour
    {
        public enum Tool : byte
        {
            Axe = 0,
            Gun = 1,
        }

        [SerializeField] private Tool _selected = Tool.Axe;

        private InputAction _next;
        private InputAction _previous;

        public Tool Selected => _selected;
        public bool IsHolding(Tool tool) => _selected == tool;

        /// <summary>Raised locally when the selection changes, for HUD and held-item visuals.</summary>
        public event Action<Tool> SelectionChanged;

        public override void OnStartClient()
        {
            base.OnStartClient();

            if (!IsOwner)
            {
                enabled = false;
                return;
            }

            InputActionAsset actions = InputSystem.actions;

            if (actions == null)
                return;

            _next = actions.FindAction("Player/Next");
            _previous = actions.FindAction("Player/Previous");

            _next?.Enable();
            _previous?.Enable();
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            _next?.Disable();
            _previous?.Disable();
        }

        private void Update()
        {
            if (!IsOwner)
                return;

            bool toggled = (_next != null && _next.WasPressedThisFrame())
                           || (_previous != null && _previous.WasPressedThisFrame());

            if (!toggled)
                return;

            // Two tools, so next and previous do the same thing. This gets replaced
            // wholesale by slot selection rather than extended.
            Select(_selected == Tool.Axe ? Tool.Gun : Tool.Axe);
        }

        public void Select(Tool tool)
        {
            if (_selected == tool)
                return;

            _selected = tool;
            SelectionChanged?.Invoke(tool);
        }
    }
}
