namespace ChopChop.Core
{
    /// <summary>
    /// FNV-1a 64-bit. Not cryptographic — this exists to catch a corrupted or
    /// mis-assembled payload, not a malicious one, and it is deterministic across
    /// platforms and runs, which <c>string.GetHashCode</c> is not.
    /// </summary>
    public static class Fnv1a
    {
        private const ulong Offset = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        /// <summary>
        /// Hashes a tree's identity without allocating.
        ///
        /// The array overload below is fine for a save payload hashed once; this one is
        /// called every frame the chop meter is on screen, and a byte[] per frame per
        /// tree is exactly the kind of garbage that shows up as a stutter in a forest.
        /// Same algorithm, same bytes, same answer.
        /// </summary>
        public static uint Hash(long chunkKey, ushort localIndex)
        {
            ulong hash = Offset;

            for (int i = 0; i < 8; i++)
            {
                hash ^= (byte)(chunkKey >> (i * 8));
                hash *= Prime;
            }

            for (int i = 0; i < 2; i++)
            {
                hash ^= (byte)(localIndex >> (i * 8));
                hash *= Prime;
            }

            // Folded rather than truncated, so the high bits are not simply discarded.
            return (uint)(hash ^ (hash >> 32));
        }

        public static ulong Hash(byte[] data)
        {
            if (data == null)
                return Offset;

            ulong hash = Offset;

            for (int i = 0; i < data.Length; i++)
            {
                hash ^= data[i];
                hash *= Prime;
            }

            return hash;
        }
    }
}
