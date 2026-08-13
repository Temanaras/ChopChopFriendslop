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

        /// <summary>Empties everything. This is what dying costs (TECH 9.3).</summary>
        public void Clear()
        {
            for (int i = 0; i < _slots.Length; i++)
                _slots[i] = ItemStack.Empty;

            Changed?.Invoke();
        }
    }
}
