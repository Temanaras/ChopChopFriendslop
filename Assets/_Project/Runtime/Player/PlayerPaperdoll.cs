using System;
using ChopChop.Items;
using FishNet.Connection;
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

        /// <summary>
        /// Raised on the owning client whenever carried cargo changes.
        ///
        /// The server has <see cref="ItemContainer.Changed"/>; the owner had nothing and
        /// had to poll. The replicated list was already raising this internally — it
        /// simply had no listener.
        /// </summary>
        public event Action CarriedChanged;

        /// <summary>
        /// Raised on the owning client when the server refuses one of the requests below.
        /// A slot that springs back without saying why reads as a bug.
        /// </summary>
        public event Action<ItemMoveResult> RequestRefused;

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
            _carried.OnChange += HandleCarriedChanged;
        }

        private void OnDestroy()
        {
            _equipped.OnChange -= HandleEquippedChanged;
            _carried.OnChange -= HandleCarriedChanged;
        }

        /// <summary>Called at boot; the registry lives outside the scene.</summary>
        public void Bind(ItemRegistry registry)
        {
            _registry = registry;

            if (IsServerInitialized && _inventory == null)
                CreateInventory();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            /* The bootstrap binds the registry server-side, on the line where it hands
             * out the starting axe. A client has no equivalent moment, so without this
             * every tier reads as zero however well-equipped the player actually is.
             *
             * That is not cosmetic: the chop meter sizes its bands from AxeTier, and the
             * server grades against the real one. A client drawing tier-0 bands would
             * watch perfectly-timed swings come back as misses, and it would read as lag
             * rather than as a bug. Only a real client catches this — a hosted server
             * shares the server's already-bound instance (TECH 15). */
            if (_registry == null && Core.ServiceLocator.TryGet(out ItemRegistry registry))
                _registry = registry;
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

        private void HandleCarriedChanged(SyncListOperation op, int index, ItemStack previous,
            ItemStack next, bool asServer)
        {
            /* Server-side writes are already visible through ItemContainer.Changed, and
             * on a hosted server both fire for the same edit. Raising only the client
             * pass keeps this event meaning exactly one thing: "what the owner can see
             * has changed". */
            if (!asServer)
                CarriedChanged?.Invoke();
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

        // ---------------- Client requests ----------------

        /*
         * Everything below is a *request*. The client names slots and nothing else; the
         * server re-reads its own state and decides (TECH 9.4). Written with
         * RequireOwnership = true — unlike chopping or the chest, which are things you do
         * to the world, this is a player rummaging in their own pockets, and nobody else
         * has any business asking.
         *
         * Every path answers, including the successful one, because "no reply" and
         * "refused" have to be distinguishable from a dropped packet.
         */

        /// <summary>
        /// Wear what is in a carried slot; whatever it displaces lands there.
        ///
        /// <paramref name="intended"/> is the slot the player actually pointed at.
        /// <see cref="ItemSlot.None"/> means "wherever it belongs", for a future
        /// right-click-to-equip. A drag names the slot, and naming the wrong one is
        /// refused rather than quietly redirected — a rifle dropped on the axe slot that
        /// silently lands in the gun slot moves an item the player was not looking at.
        /// </summary>
        [ServerRpc]
        public void RequestEquip(int carriedSlot, ItemSlot intended)
        {
            if (_inventory == null || _registry == null
                || carriedSlot < 0 || carriedSlot >= _inventory.SlotCount)
            {
                Answer(ItemMoveResult.Invalid);
                return;
            }

            ItemStack stack = _inventory[carriedSlot];

            if (stack.IsEmpty)
            {
                Answer(ItemMoveResult.NothingThere);
                return;
            }

            /* Checked against the definition, not against what the client claims: the
             * client says where it aimed, the server says what the item is. */
            if (intended != ItemSlot.None && _registry.SlotOf(stack.ItemId) != intended)
            {
                Answer(ItemMoveResult.WrongSlot);
                return;
            }

            // TryEquip re-checks the item's own definition, so a client naming an axe for
            // the light slot gets nowhere.
            if (!TryEquip(stack, out ItemStack displaced))
            {
                Answer(ItemMoveResult.WrongSlot);
                return;
            }

            /* A single write rather than TakeSlot-then-SetSlot: equipping is a swap, and
             * the intermediate state where the item is in neither place would be pushed
             * to the client as an inventory that briefly lost something. */
            _inventory.SetSlot(carriedSlot, displaced);
            Answer(ItemMoveResult.Ok);
        }

        /// <summary>
        /// Take something off. <paramref name="toCarriedSlot"/> below zero means anywhere
        /// it fits; naming an occupied slot swaps, provided the occupant belongs in the
        /// slot being emptied.
        /// </summary>
        [ServerRpc]
        public void RequestUnequip(ItemSlot slot, int toCarriedSlot)
        {
            if (_inventory == null || _registry == null || slot == ItemSlot.None)
            {
                Answer(ItemMoveResult.Invalid);
                return;
            }

            ItemStack worn = GetEquipped(slot);

            if (worn.IsEmpty)
            {
                Answer(ItemMoveResult.NothingThere);
                return;
            }

            if (toCarriedSlot >= _inventory.SlotCount)
            {
                Answer(ItemMoveResult.Invalid);
                return;
            }

            if (toCarriedSlot >= 0)
            {
                ItemStack destination = _inventory[toCarriedSlot];

                if (!destination.IsEmpty)
                {
                    // Dropping the worn axe onto another axe is a swap, not a refusal.
                    if (_registry.SlotOf(destination.ItemId) != slot)
                    {
                        Answer(ItemMoveResult.WrongSlot);
                        return;
                    }

                    TryEquip(destination, out ItemStack displaced);
                    _inventory.SetSlot(toCarriedSlot, displaced);
                    Answer(ItemMoveResult.Ok);
                    return;
                }

                _inventory.SetSlot(toCarriedSlot, Unequip(slot));
                Answer(ItemMoveResult.Ok);
                return;
            }

            if (!_inventory.HasRoomFor(worn.ItemId, worn.Count))
            {
                // Checked before unequipping: taking it off first and then failing to
                // stow it would delete the item.
                Answer(ItemMoveResult.NoRoom);
                return;
            }

            Unequip(slot);
            _inventory.TryAdd(worn.ItemId, worn.Count, worn.Durability);
            Answer(ItemMoveResult.Ok);
        }

        /// <summary>Rearrange carried cargo: merge onto a matching stack, or swap.</summary>
        [ServerRpc]
        public void RequestMoveCarried(int from, int to)
        {
            if (_inventory == null)
            {
                Answer(ItemMoveResult.Invalid);
                return;
            }

            Answer(_inventory.Move(from, to) ? ItemMoveResult.Ok : ItemMoveResult.NothingThere);
        }

        private void Answer(ItemMoveResult result) => AnswerRequest(Owner, result);

        [TargetRpc]
        private void AnswerRequest(NetworkConnection connection, ItemMoveResult result)
            => RequestRefused?.Invoke(result);

        public ItemStack[] EquippedToArray()
        {
            ItemStack[] result = new ItemStack[ItemSlots.Count];

            for (int i = 0; i < result.Length && i < _equipped.Count; i++)
                result[i] = _equipped[i];

            return result;
        }
    }
}
