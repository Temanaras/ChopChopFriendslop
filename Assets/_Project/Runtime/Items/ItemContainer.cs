using System;
using System.Collections.Generic;

namespace ChopChop.Items
{
    /// <summary>
    /// A plain list of stacks with the merge and split rules in one place.
    ///
    /// Deliberately not a MonoBehaviour and not networked. The cabin chest, a player's
    /// carried inventory and, later, a corpse are all the same thing with different
    /// owners and different lifetimes (TECH 9.4), and none of them wants its own copy of
    /// "does this stack fit".
    /// </summary>
    public sealed class ItemContainer
    {
        private readonly ItemStack[] _slots;
        private readonly ItemRegistry _registry;

        public int SlotCount => _slots.Length;

        /// <summary>Raised whenever any slot changes, so a view can refresh.</summary>
        public event Action Changed;

        public ItemContainer(int slotCount, ItemRegistry registry)
        {
            _slots = new ItemStack[slotCount];
            _registry = registry;
        }

        public ItemStack this[int index] => _slots[index];

        public IReadOnlyList<ItemStack> Slots => _slots;

        /// <summary>Replaces everything, e.g. when loading a save.</summary>
        public void Load(IReadOnlyList<ItemStack> stacks)
        {
            for (int i = 0; i < _slots.Length; i++)
                _slots[i] = i < (stacks?.Count ?? 0) ? stacks[i] : ItemStack.Empty;

            Changed?.Invoke();
        }

        public ItemStack[] ToArray() => (ItemStack[])_slots.Clone();

        /// <summary>
        /// Adds what it can, merging into existing stacks first.
        /// </summary>
        /// <returns>How many could not fit. Zero means everything went in.</returns>
        public ushort TryAdd(ushort itemId, ushort count, ushort durability = 0)
        {
            if (itemId == 0 || count == 0)
                return count;

            ushort maxStack = _registry != null ? _registry.MaxStackOf(itemId) : (ushort)1;
            ushort remaining = count;

            // Top up partial stacks before opening a new slot, or a container fills with
            // singles and looks full while holding almost nothing.
            for (int i = 0; i < _slots.Length && remaining > 0; i++)
            {
                if (_slots[i].ItemId != itemId || _slots[i].Count >= maxStack)
                    continue;

                ushort room = (ushort)(maxStack - _slots[i].Count);
                ushort moved = Math.Min(room, remaining);

                _slots[i].Count += moved;
                remaining -= moved;
            }

            for (int i = 0; i < _slots.Length && remaining > 0; i++)
            {
                if (!_slots[i].IsEmpty)
                    continue;

                ushort moved = Math.Min(maxStack, remaining);
                _slots[i] = new ItemStack(itemId, moved, durability);
                remaining -= moved;
            }

            if (remaining != count)
                Changed?.Invoke();

            return remaining;
        }

        /// <summary>Removes up to <paramref name="count"/>, returning how many came out.</summary>
        public ushort TryRemove(ushort itemId, ushort count)
        {
            ushort taken = 0;

            for (int i = 0; i < _slots.Length && taken < count; i++)
            {
                if (_slots[i].ItemId != itemId)
                    continue;

                ushort moved = Math.Min(_slots[i].Count, (ushort)(count - taken));
                _slots[i].Count -= moved;
                taken += moved;

                if (_slots[i].Count == 0)
                    _slots[i] = ItemStack.Empty;
            }

            if (taken > 0)
                Changed?.Invoke();

            return taken;
        }

        /// <summary>Takes an entire slot, leaving it empty.</summary>
        public ItemStack TakeSlot(int index)
        {
            if (index < 0 || index >= _slots.Length)
                return ItemStack.Empty;

            ItemStack taken = _slots[index];
            _slots[index] = ItemStack.Empty;

            if (!taken.IsEmpty)
                Changed?.Invoke();

            return taken;
        }

        public bool SetSlot(int index, ItemStack stack)
        {
            if (index < 0 || index >= _slots.Length)
                return false;

            _slots[index] = stack;
            Changed?.Invoke();
            return true;
        }

        public ushort CountOf(ushort itemId)
        {
            int total = 0;

            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].ItemId == itemId)
                    total += _slots[i].Count;
            }

            return (ushort)Math.Min(total, ushort.MaxValue);
        }

        public bool HasRoomFor(ushort itemId, ushort count)
        {
            ushort maxStack = _registry != null ? _registry.MaxStackOf(itemId) : (ushort)1;
            int room = 0;

            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].IsEmpty)
                    room += maxStack;
                else if (_slots[i].ItemId == itemId && _slots[i].Count < maxStack)
                    room += maxStack - _slots[i].Count;

                if (room >= count)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Moves one slot onto another: merges if they stack, swaps otherwise.
        ///
        /// This is what a drag actually is, and it did not exist — the closest the
        /// container offered was <see cref="TakeSlot"/> followed by
        /// <see cref="SetSlot"/>, which is two <see cref="Changed"/> events and a moment
        /// in between where the dragged stack exists nowhere. A view refreshing on the
        /// first of those draws an inventory that has lost an item.
        ///
        /// Partial merges leave the remainder behind rather than failing, so dropping 40
        /// wood onto a stack of 50 with a cap of 64 moves 14 and keeps 26 in hand — which
        /// is what a player dragging it expects to see.
        /// </summary>
        /// <returns>False if either index is out of range, or nothing moved.</returns>
        public bool Move(int from, int to)
        {
            if (from == to)
                return false;

            if (from < 0 || from >= _slots.Length || to < 0 || to >= _slots.Length)
                return false;

            ItemStack source = _slots[from];

            if (source.IsEmpty)
                return false;

            ItemStack destination = _slots[to];
            bool sameItem = !destination.IsEmpty
                            && destination.ItemId == source.ItemId
                            && destination.Durability == source.Durability;

            if (sameItem)
            {
                ushort maxStack = _registry != null ? _registry.MaxStackOf(source.ItemId) : (ushort)1;

                if (destination.Count >= maxStack)
                    return Swap(from, to);

                ushort moved = Math.Min((ushort)(maxStack - destination.Count), source.Count);

                destination.Count += moved;
                source.Count -= moved;

                _slots[to] = destination;
                _slots[from] = source.Count == 0 ? ItemStack.Empty : source;

                Changed?.Invoke();
                return true;
            }

            /* Different items, or the same item at different durability — which is a
             * different item as far as a player is concerned, and merging them would
             * quietly destroy the wear on one of them. */
            return Swap(from, to);
        }

        /// <summary>
        /// The same move, but across two containers — a drag between the backpack and the
        /// chest.
        ///
        /// Written as one operation rather than take-then-add for the reason
        /// <see cref="Move"/> is: the halfway state has the stack in neither container,
        /// and both of them push to clients on change. A player watching the chest would
        /// see the item blink out of existence before arriving.
        ///
        /// Static because it belongs to neither container. Both are re-read here, on the
        /// server, rather than trusted from whoever asked (TECH 9.4).
        /// </summary>
        /// <returns>False if nothing moved: bad index, empty source, or a full destination.</returns>
        public static bool MoveBetween(ItemContainer from, int fromSlot, ItemContainer to, int toSlot)
        {
            if (from == null || to == null)
                return false;

            if (ReferenceEquals(from, to))
                return from.Move(fromSlot, toSlot);

            if (fromSlot < 0 || fromSlot >= from._slots.Length || toSlot < 0 || toSlot >= to._slots.Length)
                return false;

            ItemStack source = from._slots[fromSlot];

            if (source.IsEmpty)
                return false;

            ItemStack destination = to._slots[toSlot];

            if (!destination.IsEmpty
                && destination.ItemId == source.ItemId
                && destination.Durability == source.Durability)
            {
                ushort maxStack = to._registry != null ? to._registry.MaxStackOf(source.ItemId) : (ushort)1;

                if (destination.Count < maxStack)
                {
                    ushort moved = Math.Min((ushort)(maxStack - destination.Count), source.Count);

                    destination.Count += moved;
                    source.Count -= moved;

                    to._slots[toSlot] = destination;
                    from._slots[fromSlot] = source.Count == 0 ? ItemStack.Empty : source;

                    from.Changed?.Invoke();
                    to.Changed?.Invoke();
                    return true;
                }
            }

            /* A swap has to be checked against the *destination's* stack limit as well:
             * the chest may cap an item lower than a backpack does, and a blind exchange
             * would create a stack the destination is not allowed to hold. */
            if (!destination.IsEmpty && !to.Accepts(source) )
                return false;

            if (!destination.IsEmpty && !from.Accepts(destination))
                return false;

            from._slots[fromSlot] = destination;
            to._slots[toSlot] = source;

            from.Changed?.Invoke();
            to.Changed?.Invoke();
            return true;
        }

        /// <summary>Whether a single slot here may legally hold this whole stack.</summary>
        private bool Accepts(ItemStack stack)
        {
            ushort maxStack = _registry != null ? _registry.MaxStackOf(stack.ItemId) : (ushort)1;
            return stack.Count <= maxStack;
        }

        private bool Swap(int from, int to)
        {
            (_slots[from], _slots[to]) = (_slots[to], _slots[from]);
            Changed?.Invoke();
            return true;
        }

        /// <summary>Empties everything. This is what dying costs (TECH 9.3).</summary>
        public void Clear()
        {
            for (int i = 0; i < _slots.Length; i++)
                _slots[i] = ItemStack.Empty;

            Changed?.Invoke();
        }
    }
}
