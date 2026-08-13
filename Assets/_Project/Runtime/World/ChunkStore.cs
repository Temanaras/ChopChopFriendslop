using System.Collections.Generic;
using ChopChop.Biomes;
using UnityEngine;

namespace ChopChop.World
{
    /// <summary>
    /// Keeps generated chunks around while they are needed and drops them when they are
    /// not.
    ///
    /// Nothing here is authoritative and nothing is saved — a dropped chunk is
    /// regenerated identically on demand (TECH 2.6), so eviction can never lose
    /// anything. Player changes live in tree diffs, which are held elsewhere and
    /// deliberately outlive the generated data.
    ///
    /// Used by both sides for different reasons: clients keep chunks around themselves
    /// to draw, and the server keeps them around every player because it has to validate
    /// what they chop (TECH 5.4).
    /// </summary>
    public sealed class ChunkStore
    {
        private readonly Dictionary<long, ChunkData> _loaded = new();
        private readonly List<long> _evictionScratch = new();

        private readonly int _worldSeed;
        private readonly BiomeSet _biomes;
        private readonly WorldGenSettings _settings;

        public ChunkStore(int worldSeed, BiomeSet biomes, WorldGenSettings settings)
        {
            _worldSeed = worldSeed;
            _biomes = biomes;
            _settings = settings;
        }

        public int LoadedCount => _loaded.Count;

        /// <summary>
        /// Loaded chunks. Iteration order is not stable and must never feed generation
        /// or anything else that has to be deterministic (TECH 2.6).
        /// </summary>
        public IEnumerable<ChunkData> Loaded => _loaded.Values;

        public bool IsLoaded(int chunkX, int chunkZ) => _loaded.ContainsKey(ChunkKey.Pack(chunkX, chunkZ));

        /// <summary>Returns the chunk, generating it if it isn't loaded yet.</summary>
        public ChunkData GetOrGenerate(int chunkX, int chunkZ)
        {
            long key = ChunkKey.Pack(chunkX, chunkZ);

            if (_loaded.TryGetValue(key, out ChunkData existing))
                return existing;

            ChunkData generated = ChunkGenerator.Generate(_worldSeed, chunkX, chunkZ, _biomes, _settings);
            _loaded[key] = generated;

            return generated;
        }

        public bool TryGet(int chunkX, int chunkZ, out ChunkData chunk)
            => _loaded.TryGetValue(ChunkKey.Pack(chunkX, chunkZ), out chunk);

        /// <summary>
        /// Loads every chunk within <paramref name="radiusInChunks"/> of the given
        /// centres and drops everything else.
        /// </summary>
        /// <returns>How many chunks were generated this call.</returns>
        public int UpdateResidency(IReadOnlyList<Vector3> centres, int radiusInChunks)
        {
            int generated = 0;

            _wanted.Clear();

            for (int i = 0; i < centres.Count; i++)
            {
                WorldToChunk(centres[i], out int centreX, out int centreZ);

                for (int z = centreZ - radiusInChunks; z <= centreZ + radiusInChunks; z++)
                for (int x = centreX - radiusInChunks; x <= centreX + radiusInChunks; x++)
                {
                    // Circular rather than square, so residency does not depend on which
                    // diagonal a player happens to be facing.
                    int dx = x - centreX;
                    int dz = z - centreZ;

                    if (dx * dx + dz * dz > radiusInChunks * radiusInChunks)
                        continue;

                    long key = ChunkKey.Pack(x, z);
                    _wanted.Add(key);

                    if (_loaded.ContainsKey(key))
                        continue;

                    _loaded[key] = ChunkGenerator.Generate(_worldSeed, x, z, _biomes, _settings);
                    generated++;
                }
            }

            _evictionScratch.Clear();

            foreach (KeyValuePair<long, ChunkData> pair in _loaded)
            {
                if (!_wanted.Contains(pair.Key))
                    _evictionScratch.Add(pair.Key);
            }

            for (int i = 0; i < _evictionScratch.Count; i++)
                _loaded.Remove(_evictionScratch[i]);

            return generated;
        }

        private readonly HashSet<long> _wanted = new();

        public void Clear() => _loaded.Clear();

        /// <summary>
        /// World position to the chunk containing it. Floor, not truncate — truncation
        /// would map -0.5 and 0.5 to the same chunk and put a seam through the origin.
        /// </summary>
        public static void WorldToChunk(Vector3 position, out int chunkX, out int chunkZ)
        {
            chunkX = Mathf.FloorToInt(position.x / WorldConstants.ChunkSize);
            chunkZ = Mathf.FloorToInt(position.z / WorldConstants.ChunkSize);
        }
    }
}
