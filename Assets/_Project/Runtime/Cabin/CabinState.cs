using ChopChop.Items;
using MemoryPack;

namespace ChopChop.Cabin
{
    /// <summary>
    /// Shared cabin state (TECH 6.3). Server-owned and part of the world save, not a
    /// NetworkObject inventory (TECH 9.4).
    ///
    /// This is the catch-up mechanism: because every progression element is a
    /// transferable item (TECH 2.3), a player who has fallen behind can be handed
    /// equipment straight out of storage. Anything added here must keep that property.
    /// </summary>
    [MemoryPackable]
    public partial class CabinState
    {
        public ItemStack[] Storage;

        /// <summary>Ids of crafting stations that have been built.</summary>
        public byte[] BuiltStationIds;

        /// <summary>Ring indices the group has unlocked.</summary>
        public byte[] UnlockedRings;

        [MemoryPackConstructor]
        public CabinState(ItemStack[] storage, byte[] builtStationIds, byte[] unlockedRings)
        {
            Storage = storage;
            BuiltStationIds = builtStationIds;
            UnlockedRings = unlockedRings;
        }

        public CabinState() : this(new ItemStack[0], new byte[0], new byte[0]) { }
    }
}
