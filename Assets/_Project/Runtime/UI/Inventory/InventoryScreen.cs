using System;
using System.Collections.Generic;
using ChopChop.Cabin;
using ChopChop.Core;
using ChopChop.Items;
using ChopChop.Player;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ChopChop.UI
{
    /// <summary>
    /// The backpack and what the player is wearing, and the one place that turns a drag
    /// into a request.
    ///
    /// Every drop is a *request*: the client names two slots and the server re-reads its
    /// own state before agreeing (TECH 9.4). Nothing here moves an item locally, so a
    /// refused drag simply never redraws, and two players grabbing the same stack cannot
    /// both win.
    ///
    /// Slots are built at runtime from one prefab rather than authored twelve times, so
    /// changing the backpack size is a number on <see cref="PlayerPaperdoll"/> and not a
    /// re-layout.
    /// </summary>
    public sealed class InventoryScreen : PlayerBoundView, ISlotHost
    {
        [Header("Wiring")]
        [SerializeField] private GameObject _panel;
        [SerializeField] private RectTransform _carriedGrid;
        [SerializeField] private RectTransform _equipmentGrid;
        [SerializeField] private GameObject _storageWindow;
        [SerializeField] private RectTransform _storageGrid;
        [SerializeField] private ItemSlotView _slotPrefab;
        [SerializeField] private RectTransform _dragGhost;
        [SerializeField] private TMP_Text _dragGhostLabel;
        [SerializeField] private TMP_Text _message;
        [SerializeField] private UiTheme _theme;

        [Tooltip("Seconds a refusal stays on screen.")]
        [SerializeField] private float _messageSeconds = 2.5f;

        /// <summary>Paperdoll slots, in the order they are shown. Index 0 (None) is never one.</summary>
        private static readonly ItemSlot[] EquipmentOrder =
        {
            ItemSlot.Axe, ItemSlot.Gun, ItemSlot.Light, ItemSlot.Armor, ItemSlot.Backpack, ItemSlot.Mount,
        };

        private readonly List<ItemSlotView> _carried = new();
        private readonly List<ItemSlotView> _equipment = new();
        private readonly List<ItemSlotView> _storage = new();

        private CabinChest _chest;

        private ItemRegistry _registry;
        private InputAction _toggle;
        private InputAction _cancel;
        private IDisposable _focus;
        private ItemSlotView _dragging;
        private float _messageUntil;
        private bool _built;

        public bool IsOpen { get; private set; }

        public bool IsDragging => _dragging != null;

        protected override void OnEnable()
        {
            base.OnEnable();

            InputActionAsset actions = InputSystem.actions;
            _toggle = actions?.FindAction("Player/Inventory");
            _cancel = actions?.FindAction("UI/Cancel");

            if (_toggle == null)
                Debug.LogError("[UI] No Player/Inventory action; the inventory cannot be opened.");
            else
                _toggle.Enable();

            _cancel?.Enable();

            CabinChest.Opened += HandleChestOpened;

            Close();
        }

        protected override void OnDisable()
        {
            base.OnDisable();

            _toggle?.Disable();
            _cancel?.Disable();
            CabinChest.Opened -= HandleChestOpened;
            Close();
        }

        protected override void Bind(LocalPlayer player)
        {
            if (player.Paperdoll == null)
                return;

            player.Paperdoll.CarriedChanged += Redraw;
            player.Paperdoll.Equipped += HandleEquipped;
            player.Paperdoll.RequestRefused += HandleRefused;

            Build(player.Paperdoll.CarriedCount);
            Redraw();
        }

        protected override void Unbind(LocalPlayer player)
        {
            if (player.Paperdoll == null)
                return;

            player.Paperdoll.CarriedChanged -= Redraw;
            player.Paperdoll.Equipped -= HandleEquipped;
            player.Paperdoll.RequestRefused -= HandleRefused;

            // A screen left open over a dead player would keep the cursor and the input.
            Close();
        }

        private void HandleEquipped(ItemSlot slot, ItemStack stack) => Redraw();

        // ---- open / close ------------------------------------------------------

        private void Update()
        {
            if (Time.unscaledTime > _messageUntil && _message != null && _message.enabled)
                _message.enabled = false;

            if (Player == null)
            {
                if (IsOpen)
                    Close();

                return;
            }

            /* Walking away closes the chest. The server refuses out-of-range transfers
             * anyway, so this is not a security measure — it is that a chest panel open
             * across the clearing is a lie about what the player can reach. */
            if (IsOpen && _chest != null && !WithinReach())
                Close();

            if (_toggle != null && _toggle.WasPressedThisFrame())
            {
                if (IsOpen)
                    Close();
                else
                    Open();
            }
            else if (IsOpen && _cancel != null && _cancel.WasPressedThisFrame())
            {
                Close();
            }
        }

        private void HandleChestOpened(CabinChest chest)
        {
            if (chest == null || Player == null)
                return;

            DetachChest();

            _chest = chest;
            _chest.ContentsChanged += Redraw;
            _chest.TransferAnswered += HandleRefused;

            BuildStorage(_chest.SlotCount);

            if (_storageWindow != null)
                _storageWindow.SetActive(true);

            Open();
        }

        private void DetachChest()
        {
            if (_chest != null)
            {
                _chest.ContentsChanged -= Redraw;
                _chest.TransferAnswered -= HandleRefused;
            }

            _chest = null;

            if (_storageWindow != null)
                _storageWindow.SetActive(false);
        }

        public void Open()
        {
            if (IsOpen || Player?.Paperdoll == null)
                return;

            IsOpen = true;

            /* Taking the capture is what actually stops the player moving, chopping and
             * firing behind the screen, and what releases the cursor. Held for exactly as
             * long as the panel is up. */
            _focus = UiFocus.Capture();

            if (_panel != null)
                _panel.SetActive(true);

            Redraw();
        }

        public void Close()
        {
            IsOpen = false;
            CancelDrag();
            DetachChest();

            _focus?.Dispose();
            _focus = null;

            if (_panel != null)
                _panel.SetActive(false);
        }

        private bool WithinReach()
        {
            if (_chest == null || Player == null)
                return false;

            // Matches the server's own slack, so the panel does not close a step before
            // a transfer would still have been accepted.
            float slack = _chest.InteractRange + 2f;
            return (Player.transform.position - _chest.InteractPoint).sqrMagnitude <= slack * slack;
        }

        // ---- building and drawing ----------------------------------------------

        private void Build(int carriedSlots)
        {
            if (_built || _slotPrefab == null)
                return;

            if (!ServiceLocator.TryGet(out _registry))
                Debug.LogWarning("[UI] No ItemRegistry registered; slots will draw empty.");

            for (int i = 0; i < carriedSlots; i++)
                _carried.Add(MakeSlot(_carriedGrid, SlotKind.Carried, i));

            foreach (ItemSlot slot in EquipmentOrder)
                _equipment.Add(MakeSlot(_equipmentGrid, SlotKind.Equipment, (int)slot));

            _built = true;
        }

        private void BuildStorage(int slots)
        {
            // Chest size comes from CabinStorage and is not known until it has spawned,
            // so these are built on first open rather than alongside the backpack.
            if (_storage.Count >= slots || _slotPrefab == null || _storageGrid == null)
                return;

            for (int i = _storage.Count; i < slots; i++)
                _storage.Add(MakeSlot(_storageGrid, SlotKind.Storage, i));
        }

        private ItemSlotView MakeSlot(RectTransform parent, SlotKind kind, int index)
        {
            ItemSlotView view = Instantiate(_slotPrefab, parent);
            view.name = $"{kind}{index}";
            view.Configure(this, kind, index, _theme);
            return view;
        }

        private void Redraw()
        {
            PlayerPaperdoll paperdoll = Player?.Paperdoll;

            if (paperdoll == null)
                return;

            for (int i = 0; i < _carried.Count; i++)
                _carried[i].Draw(paperdoll.GetCarried(i), _registry);

            for (int i = 0; i < _equipment.Count; i++)
                _equipment[i].Draw(paperdoll.GetEquipped(_equipment[i].Slot), _registry);

            for (int i = 0; i < _storage.Count; i++)
                _storage[i].Draw(_chest != null ? _chest.GetSlot(i) : ItemStack.Empty, _registry);
        }

        // ---- dragging ------------------------------------------------------------

        public void BeginDrag(ItemSlotView source)
        {
            if (!IsOpen)
                return;

            _dragging = source;

            if (_dragGhost == null)
                return;

            _dragGhost.gameObject.SetActive(true);

            if (_dragGhostLabel != null && _registry != null
                && _registry.TryGet(source.Stack.ItemId, out ItemDefinition definition))
            {
                _dragGhostLabel.text = source.Stack.Count > 1
                    ? $"{definition.DisplayName} x{source.Stack.Count}"
                    : definition.DisplayName;
            }
        }

        public void UpdateDrag(PointerEventData eventData)
        {
            if (_dragging == null || _dragGhost == null)
                return;

            // The ghost lives on the same canvas, so screen space is the right space and
            // no camera is involved for a Screen Space - Overlay canvas.
            _dragGhost.position = eventData.position;
        }

        public void EndDrag() => CancelDrag();

        private void CancelDrag()
        {
            _dragging = null;

            if (_dragGhost != null)
                _dragGhost.gameObject.SetActive(false);
        }

        /// <summary>
        /// A drop landed on <paramref name="destination"/>. Turns the pair of slots into
        /// the one request that describes it, and sends nothing if the pair is meaningless.
        /// </summary>
        public void Drop(ItemSlotView destination)
        {
            ItemSlotView source = _dragging;

            if (source == null || destination == null || source == destination)
                return;

            PlayerPaperdoll paperdoll = Player?.Paperdoll;

            if (paperdoll == null)
                return;

            switch (source.Kind)
            {
                case SlotKind.Carried when destination.Kind == SlotKind.Carried:
                    paperdoll.RequestMoveCarried(source.Index, destination.Index);
                    break;

                /* The slot dropped on is passed through so the server can refuse a
                 * mismatch. It is not trusted as the destination — the item's own
                 * definition still decides where it lands. */
                case SlotKind.Carried when destination.Kind == SlotKind.Equipment:
                    paperdoll.RequestEquip(source.Index, destination.Slot);
                    break;

                case SlotKind.Equipment when destination.Kind == SlotKind.Carried:
                    paperdoll.RequestUnequip(source.Slot, destination.Index);
                    break;

                case SlotKind.Equipment when destination.Kind == SlotKind.Equipment:
                    // Nothing sensible: an item has exactly one valid slot, so moving
                    // between two of them is never a move a player can mean.
                    break;

                case SlotKind.Carried when destination.Kind == SlotKind.Storage:
                    _chest?.RequestDeposit(source.Index, destination.Index);
                    break;

                case SlotKind.Storage when destination.Kind == SlotKind.Carried:
                    _chest?.RequestWithdraw(source.Index, destination.Index);
                    break;

                case SlotKind.Storage when destination.Kind == SlotKind.Storage:
                    /* Tidying inside the chest is still a shared-container edit, so it
                     * goes through the chest rather than the player: the reach check and
                     * the concurrent-access rules are the same ones a deposit needs. */
                    _chest?.RequestMoveStorage(source.Index, destination.Index);
                    break;

                case SlotKind.Equipment when destination.Kind == SlotKind.Storage:
                    /* Two authorities, two round trips, and the second could fail after
                     * the first succeeded — leaving the item on the floor of a state
                     * nobody asked for. Take it off first, then move it. */
                    Say("Take it off first.");
                    break;

                case SlotKind.Storage when destination.Kind == SlotKind.Equipment:
                    Say("Take it out first.");
                    break;
            }

            CancelDrag();
        }

        private void HandleRefused(ItemMoveResult result)
        {
            // Ok comes back too, so a successful move is distinguishable from a lost
            // packet; it just has nothing to say.
            if (result == ItemMoveResult.Ok)
                return;

            Say(result switch
            {
                ItemMoveResult.NoRoom => "No room for that.",
                ItemMoveResult.WrongSlot => "That does not go there.",
                ItemMoveResult.NothingThere => "It has already been moved.",
                ItemMoveResult.OutOfReach => "Too far away.",
                _ => "That cannot be done.",
            });
        }

        private void Say(string text)
        {
            if (_message == null)
                return;

            _message.text = text;
            _message.enabled = true;
            _messageUntil = Time.unscaledTime + _messageSeconds;
        }
    }
}
