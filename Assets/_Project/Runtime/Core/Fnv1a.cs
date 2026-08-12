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
