using System.Collections.Generic;
using ChopChop.Biomes;
using UnityEngine;

namespace ChopChop.World
{
    /// <summary>
    /// Reclaims cleared ground while nobody is there (TECH 7).
    ///
    /// **Never ticked.** Regrowth is evaluated lazily, once, when a chunk becomes
    /// occupied again, from the gap between now and when it was last held. A chunk
    /// nobody has visited for a month costs exactly the same as one visited a second ago
    /// — nothing at all — which is what makes an unbounded persistent world affordable.
    ///
    /// This is both the balance answer to clear-cutting and a horror mechanic: the road
    /// you cut last session is not quite where you left it.
    ///
    /// Server-only. Clients are told what happened; they never decide it.
    /// </summary>
    public sealed class RegrowthService
    {
        /// <summary>
        /// Most trees one chunk may reclaim in a single evaluation (TECH 7.3).
        ///
        /// Without a cap, a chunk left alone for a month returns to pristine the instant
        /// someone walks back in, and a carefully cut road vanishes in a way that reads
        /// as arbitrary rather than eerie. The forest should close in, not blink back.
        /// </summary>
        public int MaxReclaimedPerEvaluation { get; set; } = 6;

        private readonly TreeDiffStore _diffs;
        private readonly BiomeSet _biomes;
        private readonly List<TreeDiff> _ordered = new();

        /// <summary>Chunk key, tree index. Raised per reclaimed tree so colliders and visuals can react.</summary>
        public event System.Action<long, ushort> TreeReclaimed;

        public RegrowthService(TreeDiffStore diffs, BiomeSet biomes)
        {
            _diffs = diffs;
            _biomes = biomes;
        }

        /// <summary>
        /// Works out what grew back in a chunk since it was last held, and applies it.
        /// Call when a chunk becomes occupied, before sending its diffs to anyone.
        /// </summary>
        /// <returns>How many trees were reclaimed.</returns>
        public int Evaluate(long chunkKey, uint worldTick)
        {
            List<TreeDiff> diffs = _diffs.GetDiffs(chunkKey);

            if (diffs == null || diffs.Count == 0)
                return 0;

            uint lastVisited = _diffs.GetLastVisitedTick(chunkKey);

            // Clock went backwards — a restored save, or a world tick that was reset.
            // Treat it as visited now rather than reclaiming the whole chunk at once.
            if (worldTick <= lastVisited)
            {
                _diffs.SetLastVisitedTick(chunkKey, worldTick);
                return 0;
            }

            uint elapsed = worldTick - lastVisited;
            float rate = RateFor(chunkKey);

            int budget = Mathf.Min(Mathf.FloorToInt(elapsed * rate), MaxReclaimedPerEvaluation);

            if (budget <= 0)
            {
                _diffs.SetLastVisitedTick(chunkKey, worldTick);
                return 0;
            }

            /* Oldest first. A tree felled last week comes back before one felled a minute
             * ago, so a road stays a road at its working end while its far end closes
             * over. Damaged-but-standing trees carry a felled tick of zero and therefore
             * heal first, which reads correctly — scratches close before stumps do. */
            _ordered.Clear();
            _ordered.AddRange(diffs);
            _ordered.Sort(static (a, b) => a.FelledAtTick.CompareTo(b.FelledAtTick));

            int reclaimed = 0;

            for (int i = 0; i < _ordered.Count && reclaimed < budget; i++)
            {
                ushort index = _ordered[i].LocalIndex;

                if (!_diffs.RemoveDiff(chunkKey, index))
                    continue;

                reclaimed++;
                TreeReclaimed?.Invoke(chunkKey, index);
            }

            // Removing the last diff drops the chunk entry, and with it the tick. Only
            // stamp a chunk that still has something left to reclaim.
            _diffs.SetLastVisitedTick(chunkKey, worldTick);

            return reclaimed;
        }

        /// <summary>
        /// Marks a chunk as currently held. While a player is subscribed this runs every
        /// tick, so the gap never grows and regrowth cannot progress in territory people
        /// are actively working (TECH 7.1).
        /// </summary>
        public void MarkOccupied(long chunkKey, uint worldTick) => _diffs.SetLastVisitedTick(chunkKey, worldTick);

        /// <summary>
        /// Reclaim rate for a chunk's ring. Outer rings reclaim faster, so deep territory
        /// is genuinely hard to hold and part of the difficulty curve lives here rather
        /// than only in enemy stats (TECH 7.2).
        /// </summary>
        private float RateFor(long chunkKey)
        {
            if (_biomes == null || _biomes.Count == 0)
                return 0f;

            ChunkKey.Unpack(chunkKey, out int x, out int z);

            // Distance to the chunk centre, so a chunk resolves to one ring rather than
            // straddling by corner.
            const float size = WorldConstants.ChunkSize;
            Vector2 centre = new(x * size + size * 0.5f, z * size + size * 0.5f);

            _biomes.Resolve(centre.magnitude, out BiomeDefinition biome, out _, out _);

            return biome != null ? biome.RegrowthRatePerTick : 0f;
        }
    }
}
