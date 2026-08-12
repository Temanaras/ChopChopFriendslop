using MemoryPack;

namespace ChopChop.Items
{
    /// <summary>
    /// Paperdoll slots, fixed indices (TECH 9.2). The numbers are part of the save
    /// format and the wire format — insert new slots at the end, never renumber.
    /// </summary>
    public enum ItemSlot : byte
    {
        None = 0,
        Axe = 1,
        Gun = 2,
        Mount = 3,
        Light = 4,
        Armor = 5,
        Backpack = 6,
    }

    public static class ItemSlots
    {
        /// <summary>
        /// Length of a paperdoll array. Slot values are used as indices directly, so
        /// this is the highest slot plus one, including <see cref="ItemSlot.None"/>.
        /// </summary>
        public const int Count = (int)ItemSlot.Backpack + 1;
    }

    /// <summary>
    /// Instance data for an item: what it is, how many, how worn. Networked and saved
    /// (TECH 9.1).
    ///
    /// Only <see cref="ItemId"/> crosses the network or reaches disk — never the
    /// definition, never a name, never a Unity asset reference. The registry resolves
    /// the id back to an ItemDefinition at each end.
    ///
    /// Every scrap of progression in the game is one of these (TECH 2.3), which is what
    /// makes the shared cabin chest work as a catch-up mechanism.
    /// </summary>
    [MemoryPackable]
    public partial struct ItemStack
    {
        public ushort ItemId;
        public ushort Count;

        /// <summary>Remaining durability, for item types that use it. Zero otherwise.</summary>
        public ushort Durability;

        public ItemStack(ushort itemId, ushort count, ushort durability = 0)
        {
            ItemId = itemId;
            Count = count;
            Durability = durability;
        }

        /// <summary>An empty slot. Id zero is reserved and never a real item.</summary>
        public static ItemStack Empty => default;

        [MemoryPackIgnore]
        public bool IsEmpty => ItemId == 0 || Count == 0;
    }
}
