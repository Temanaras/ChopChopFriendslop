using System;
using ChopChop.Items;

namespace ChopChop.Cabin
{
    public enum TransferResult : byte
    {
        Ok = 0,

        /// <summary>The slot named was empty, or held something else by the time this ran.</summary>
        NothingThere = 1,

        /// <summary>Destination is full.</summary>
        NoRoom = 2,

        /// <summary>Slot index out of range, or the request made no sense.</summary>
        Invalid = 3,
    }

    /// <summary>
    /// The shared chest (TECH 9.4).
    ///
    /// Server-owned, and deliberately **not a NetworkObject inventory** — it lives in
    /// <see cref="CabinState"/> and therefore in the world save, which is what makes it
    /// survive restarts and what makes it the group's catch-up mechanism: because every
    /// progression element is a transferable item (TECH 2.3), a player who has fallen
    /// behind can be handed an axe straight out of here.
    ///
    /// **Assume two players grab the same stack on the same frame.** Every transfer
    /// validates against current server state rather than what a client believed it saw,
    /// so the second one gets a refusal rather than a duplicate.
    /// </summary>
    public sealed class CabinStorage
    {
        private readonly ItemContainer _container;
        private readonly CabinState _state;

        /// <summary>Raised after any successful transfer, so views and the save can react.</summary>
        public event Action Changed;

        public int SlotCount => _container.SlotCount;
        public ItemStack this[int index] => _container[index];
        public ItemContainer Container => _container;

        public CabinStorage(CabinState state, ItemRegistry registry, int slotCount = 40)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _container = new ItemContainer(slotCount, registry);
            _container.Load(_state.Storage);
        }

        /// <summary>
        /// Moves a stack from a player's container into the chest.
        /// </summary>
        /// <remarks>
        /// The stack is re-read from the source here rather than taken from the request.
        /// A client that says "I am depositing 40 wood" may be wrong for entirely
        /// innocent reasons — someone else took it, or they died holding it — and the
        /// only state worth believing is the one on this machine.
        /// </remarks>
        public TransferResult Deposit(ItemContainer from, int fromSlot)
        {
            if (from == null || fromSlot < 0 || fromSlot >= from.SlotCount)
                return TransferResult.Invalid;

            ItemStack stack = from[fromSlot];

            if (stack.IsEmpty)
                return TransferResult.NothingThere;

            if (!_container.HasRoomFor(stack.ItemId, stack.Count))
                return TransferResult.NoRoom;

            // Taken only once the destination is known to have room, so a full chest
            // cannot swallow items on the way in.
            ItemStack taken = from.TakeSlot(fromSlot);
            ushort leftover = _container.TryAdd(taken.ItemId, taken.Count, taken.Durability);

            // Belt and braces: if anything failed to land, it goes back rather than
            // evaporating.
            if (leftover > 0)
                from.TryAdd(taken.ItemId, leftover, taken.Durability);

            Sync();
            return TransferResult.Ok;
        }

        /// <summary>Moves a stack out of the chest and into a player's container.</summary>
        public TransferResult Withdraw(ItemContainer to, int storageSlot)
        {
            if (to == null || storageSlot < 0 || storageSlot >= _container.SlotCount)
                return TransferResult.Invalid;

            ItemStack stack = _container[storageSlot];

            /* This is where the race lands. Two players clicking the same stack both send
             * the same slot index; the first empties it, and the second arrives here to
             * find nothing. Refusing is correct — the alternative is two axes from one. */
            if (stack.IsEmpty)
                return TransferResult.NothingThere;

            if (!to.HasRoomFor(stack.ItemId, stack.Count))
                return TransferResult.NoRoom;

            ItemStack taken = _container.TakeSlot(storageSlot);
            ushort leftover = to.TryAdd(taken.ItemId, taken.Count, taken.Durability);

            if (leftover > 0)
                _container.TryAdd(taken.ItemId, leftover, taken.Durability);

            Sync();
            return TransferResult.Ok;
        }

        /// <summary>Puts items straight in, e.g. loot from a felled tree.</summary>
        public ushort Add(ushort itemId, ushort count)
        {
            ushort leftover = _container.TryAdd(itemId, count);
            Sync();
            return leftover;
        }

        /// <summary>
        /// Writes back into the cabin state so the next autosave carries it. Storage is
        /// part of the world, not of any player.
        /// </summary>
        private void Sync()
        {
            _state.Storage = _container.ToArray();
            Changed?.Invoke();
        }
    }
}
