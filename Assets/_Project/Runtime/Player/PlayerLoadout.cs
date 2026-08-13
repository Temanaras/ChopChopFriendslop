using System;
using ChopChop.Items;
using FishNet.Object;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ChopChop.Player
{
    /// <summary>
    /// Which equipped tool the primary action currently uses.
    ///
    /// Selection is client-local — it only decides which local system reacts to a
    /// button, and both still ask the server for permission, so there is nothing here
    /// worth cheating. What is *equippable* is not local: a tool can only be selected if
    /// the paperdoll actually holds it, and the server has the final say on both the tier
    /// gate and every shot.
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
        private PlayerPaperdoll _paperdoll;

        public Tool Selected => _selected;

        /// <summary>
        /// True only when this tool is both selected and actually equipped. Bare hands
        /// select nothing, so an empty axe slot means no swing rather than an invisible
        /// one the server would refuse anyway.
        /// </summary>
        public bool IsHolding(Tool tool) => _selected == tool && HasEquipped(tool);

        private bool HasEquipped(Tool tool)
        {
            if (_paperdoll == null)
                return true;   // no paperdoll wired: fall back to letting tools work

            return _paperdoll.HasEquipped(SlotFor(tool));
        }

        private static ItemSlot SlotFor(Tool tool) => tool == Tool.Gun ? ItemSlot.Gun : ItemSlot.Axe;

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

            TryGetComponent(out _paperdoll);

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
