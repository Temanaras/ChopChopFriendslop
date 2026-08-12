namespace ChopChop.World
{
    /// <summary>
    /// A small, explicitly seeded PRNG. Every client must generate a bit-identical
    /// forest from the same seed (TECH 2.6), which rules out both of the obvious
    /// choices:
    ///
    /// <list type="bullet">
    /// <item><c>UnityEngine.Random</c> — global mutable state, so anything else that
    /// draws a number changes what generation produces.</item>
    /// <item><c>System.Random</c> — its algorithm is an implementation detail and has
    /// changed between .NET versions. Same seed, different runtime, different numbers.
    /// That failure would appear as players seeing different forests, with nothing
    /// throwing.</item>
    /// </list>
    ///
    /// This is xorshift128, which is fixed here in source and therefore cannot move
    /// under us. State is a value type passed by reference through the call chain
    /// rather than held anywhere, so there is no shared stream to interfere with.
    /// </summary>
    public struct DeterministicRandom
    {
        private uint _x, _y, _z, _w;

        public DeterministicRandom(ulong seed)
        {
            /* SplitMix64 to expand the seed. Seeding xorshift directly from a small
             * number leaves the first values poorly mixed, which shows up as visible
             * structure in the first trees of every chunk. */
            ulong s = seed;

            _x = (uint)(NextSplitMix(ref s) >> 16);
            _y = (uint)(NextSplitMix(ref s) >> 16);
            _z = (uint)(NextSplitMix(ref s) >> 16);
            _w = (uint)(NextSplitMix(ref s) >> 16);

            // All-zero state is a fixed point for xorshift: it would emit zero forever.
            if ((_x | _y | _z | _w) == 0)
                _x = 0x9E3779B9;
        }

        private static ulong NextSplitMix(ref ulong state)
        {
            state += 0x9E3779B97F4A7C15UL;

            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;

            return z ^ (z >> 31);
        }

        /// <summary>Mixes several values into one seed. Order of arguments matters.</summary>
        public static ulong Seed(int worldSeed, int chunkX, int chunkZ, uint salt = 0)
        {
            /* Chunk seeds are derived, never sequential, so a chunk generates the same
             * way regardless of which chunks were generated before it — or whether any
             * were. Generation order must not be an input (TECH 2.6). */
            ulong h = (uint)worldSeed;

            h = Mix(h, (uint)chunkX);
            h = Mix(h, (uint)chunkZ);
            h = Mix(h, salt);

            return h;
        }

        private static ulong Mix(ulong h, uint value)
        {
            h ^= value + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2);
            return h;
        }

        public uint NextUInt()
        {
            uint t = _x ^ (_x << 11);

            _x = _y;
            _y = _z;
            _z = _w;
            _w = _w ^ (_w >> 19) ^ t ^ (t >> 8);

            return _w;
        }

        /// <summary>Uniform in [0, 1). Never returns 1.</summary>
        public float NextFloat()
        {
            // 24 bits, which is all a float can hold exactly, divided by 2^24.
            return (NextUInt() >> 8) * (1f / 16777216f);
        }

        public float NextFloat(float min, float max) => min + NextFloat() * (max - min);

        /// <summary>Uniform in [0, exclusiveMax). Returns 0 when the range is empty.</summary>
        public int NextInt(int exclusiveMax)
        {
            if (exclusiveMax <= 0)
                return 0;

            return (int)(NextUInt() % (uint)exclusiveMax);
        }

        public bool NextBool(float trueChance) => NextFloat() < trueChance;
    }
}
