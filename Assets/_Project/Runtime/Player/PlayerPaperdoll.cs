using System;
using ChopChop.Items;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace ChopChop.Player
{
    /// <summary>
    /// What a player is wearing and carrying (TECH 9.2, 9.3).
    ///
    /// The paperdoll is **replicated to everyone**, not just the owner: other players
    /// need to see the axe in your hands, and the server needs your axe tier to decide
    /// whether a tree falls. It is server-authoritative — a client asking to equip
    /// something is a request, and the server checks the item is real and belongs in that
    /// slot before agreeing.
    ///
    /// Carried inventory is lost on death. The paperdoll never is, which is what makes
    /// progression survive a bad night in the woods (TECH 9.3).
    /// </summary>
    public sealed class PlayerPaperdoll : NetworkBehaviour
    {
        [Tooltip("Slots of carried cargo, lost on death.")]
        [SerializeField] private int _inventorySlots = 12;

        /// <summary>
        /// Slot-indexed by <see cref="ItemSlot"/>, so index 1 is always the axe whatever
        /// else changes. Sent to every client.
        /// </summary>
        private readonly SyncList<ItemStack> _equipped = new();

        /// <summary>
        /// Owner-only: nobody else needs to know what is in your backpack, and sending it
        /// to everyone would be both wasted bandwidth and free information.
        /// </summary>
        private readonly SyncList<ItemStack> _carried = new(new SyncTypeSettings(ReadPermission.OwnerOnly));

        private ItemRegistry _registry;
        private ItemContainer _inventory;

        /// <summary>Raised on every machine when equipment changes, for held-item visuals.</summary>
        public event Action<ItemSlot, ItemStack> Equipped;

        public ItemStack GetEquipped(ItemSlot slot)
        {
            int index = (int)slot;
            return index >= 0 && index < _equipped.Count ? _equipped[index] : ItemStack.Empty;
        }

        /// <summary>
        /// Tier of the equipped axe, which is the hard gate on what can be felled
        /// (TECH 5.6). Zero means bare hands.
        /// </summary>
        public byte AxeTier => TierOf(ItemSlot.Axe);

        public byte TierOf(ItemSlot slot)
        {
            ItemStack stack = GetEquipped(slot);
            return stack.IsEmpty || _registry == null ? (byte)0 : _registry.TierOf(stack.ItemId);
        }

        public bool HasEquipped(ItemSlot slot) => !GetEquipped(slot).IsEmpty;

        /// <summary>Carried cargo. Server-side only; clients read <see cref="CarriedSlots"/>.</summary>
        public ItemContainer Inventory => _inventory;

        public int CarriedCount => _carried.Count;
        public ItemStack GetCarried(int index) => index >= 0 && index < _carried.Count ? _carried[index] : ItemStack.Empty;

        private void Awake()
        {
            _equipped.OnChange += HandleEquippedChanged;
        }

        private void OnDestroy()
        {
            _equipped.OnChange -= HandleEquippedChanged;
        }

        /// <summary>Called at boot; the registry lives outside the scene.</summary>
        public void Bind(ItemRegistry registry)
        {
            _registry = registry;

            if (IsServerInitialized && _inventory == null)
                CreateInventory();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            // Every slot exists from the start, empty, so index always means slot.
            for (int i = _equipped.Count; i < ItemSlots.Count; i++)
                _equipped.Add(ItemStack.Empty);

            for (int i = _carried.Count; i < _inventorySlots; i++)
                _carried.Add(ItemStack.Empty);

            if (_registry != null)
                CreateInventory();
        }

        private void CreateInventory()
        {
            _inventory = new ItemContainer(_inventorySlots, _registry);
            _inventory.Changed += PushInventory;
        }

        private void HandleEquippedChanged(SyncListOperation op, int index, ItemStack previous,
            ItemStack next, bool asServer)
        {
            if (op == SyncListOperation.Set && index >= 0 && index < ItemSlots.Count)
                Equipped?.Invoke((ItemSlot)index, next);
        }

        /// <summary>Mirrors the server-side container into the replicated list.</summary>
        private void PushInventory()
        {
            if (!IsServerInitialized || _inventory == null)
                return;

            for (int i = 0; i < _carried.Count && i < _inventory.SlotCount; i++)
                _carried[i] = _inventory[i];
        }

        // ---------------- Server ----------------

        /// <summary>
        /// Equips a stack, returning whatever it displaced. Server-only.
        /// </summary>
        public bool TryEquip(ItemStack stack, out ItemStack displaced)
        {
            displaced = ItemStack.Empty;

            if (!IsServerInitialized || _registry == null || stack.IsEmpty)
                return false;

            ItemSlot slot = _registry.SlotOf(stack.ItemId);

            // Checked here rather than trusted from the caller: an item only goes where
            // its definition says it goes, so no amount of client insistence puts an axe
            // in the light slot.
            if (slot == ItemSlot.None)
                return false;

            int index = (int)slot;
            displaced = _equipped[index];
            _equipped[index] = stack;

            return true;
        }

        public ItemStack Unequip(ItemSlot slot)
        {
            if (!IsServerInitialized || slot == ItemSlot.None)
                return ItemStack.Empty;

            int index = (int)slot;
            ItemStack removed = _equipped[index];
            _equipped[index] = ItemStack.Empty;

            return removed;
        }

        /// <summary>
        /// Everything carried is gone; everything worn stays. Called from the death
        /// handler (TECH 9.3).
        /// </summary>
        public void DropCarriedOnDeath()
        {
            if (!IsServerInitialized)
                return;

            _inventory?.Clear();
        }

        /// <summary>Restores from a save. Server-only.</summary>
        public void LoadFrom(ItemStack[] paperdoll, ItemStack[] carried)
        {
            if (!IsServerInitialized)
                return;

            for (int i = 0; i < ItemSlots.Count; i++)
                _equipped[i] = paperdoll != null && i < paperdoll.Length ? paperdoll[i] : ItemStack.Empty;

            _inventory?.Load(carried);
            PushInventory();
        }

        public ItemStack[] EquippedToArray()
        {
            ItemStack[] result = new ItemStack[ItemSlots.Count];

            for (int i = 0; i < result.Length && i < _equipped.Count; i++)
                result[i] = _equipped[i];

            return result;
        }
    }
}
