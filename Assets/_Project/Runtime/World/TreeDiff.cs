using MemoryPack;

namespace ChopChop.World
{
    public static class WorldConstants
    {
        /// <summary>
        /// Chunk edge length in metres (TECH 5.1). Smaller means finer streaming and
        /// regrowth but more bookkeeping; 64m keeps a chunk's diff payload inside a
        /// single reliable message.
        /// </summary>
        public const int ChunkSize = 64;
    }

    /// <summary>
    /// Chunk coordinates are <c>(int x, int z)</c> packed into one long so they can key
    /// a dictionary without allocating or hashing a struct (TECH 5.1).
    /// </summary>
    public static class ChunkKey
    {
        public static long Pack(int x, int z) => ((long)(uint)x << 32) | (uint)z;

        public static void Unpack(long key, out int x, out int z)
        {
            x = (int)(key >> 32);
            z = (int)(key & 0xFFFFFFFFL);
        }
    }

    /// <summary>
    /// A single deviation from generated tree state (TECH 5.3).
    ///
    /// Trees themselves are never stored — they are regenerated from the seed, and only
    /// what players changed is kept. A chunk nobody has touched has no diffs at all,
    /// which is what lets the world hold tens of thousands of trees. Chopping adds a
    /// diff; regrowth removes one.
    /// </summary>
    [MemoryPackable]
    public partial struct TreeDiff
    {
        /// <summary>Index into the chunk's deterministically generated tree array.</summary>
        public ushort LocalIndex;

        /// <summary>255 is untouched, 0 is felled.</summary>
        public byte HealthRemaining;

        /// <summary>World tick the tree was felled on. Drives regrowth (TECH 7).</summary>
        public uint FelledAtTick;

        public TreeDiff(ushort localIndex, byte healthRemaining, uint felledAtTick)
        {
            LocalIndex = localIndex;
            HealthRemaining = healthRemaining;
            FelledAtTick = felledAtTick;
        }

        public const byte FullHealth = 255;

        [MemoryPackIgnore]
        public bool IsFelled => HealthRemaining == 0;
    }
}
