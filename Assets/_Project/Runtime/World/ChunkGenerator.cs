using System.Collections.Generic;
using ChopChop.Biomes;
using UnityEngine;

namespace ChopChop.World
{
    /// <summary>
    /// Settings that shape generation but are not per-biome. Part of the generation
    /// input, so changing any of it changes what a seed produces.
    /// </summary>
    public struct WorldGenSettings
    {
        /// <summary>Radius around the origin where nothing spawns — the authored clearing.</summary>
        public float ClearingRadius;

        /// <summary>
        /// Width of the band outside the clearing where density ramps from zero up to
        /// the biome's base value. Without it the authored edge ends in a visible circle
        /// (TECH 5.7).
        /// </summary>
        public float ClearingRampWidth;

        public static WorldGenSettings Default => new()
        {
            ClearingRadius = 60f,
            ClearingRampWidth = 25f,
        };
    }

    /// <summary>
    /// Turns <c>(seed, chunkCoord, biomes)</c> into a chunk. This is the load-bearing
    /// system; everything else in the world is comparatively routine.
    ///
    /// **This must be a pure function** (TECH 2.6). Every client generates the forest
    /// independently and they must agree bit for bit, because a tree is addressed by its
    /// index in this array — disagree about the array and two players chop different
    /// trees while both believing they hit the same one. Concretely, nothing in here may
    /// touch:
    ///
    /// <list type="bullet">
    /// <item><c>UnityEngine.Random</c> or <c>System.Random</c> — see
    /// <see cref="DeterministicRandom"/>.</item>
    /// <item>Iteration order of <c>Dictionary</c> or <c>HashSet</c>.</item>
    /// <item>Frame timing, <c>Time.time</c>, physics, or anything already in the scene.</item>
    /// <item>Which chunks were generated before this one.</item>
    /// </list>
    ///
    /// Any change to the algorithm invalidates saved diffs. Bump
    /// <c>SaveFormat.WorldGenVersion</c> when you touch it.
    /// </summary>
    public static class ChunkGenerator
    {
        /// <summary>
        /// Candidate positions come from a jittered grid rather than uniform random
        /// scatter: uniform scatter clumps, and clumps read as bald patches next to
        /// thickets rather than as forest. This is also O(cells) with no rejection loop,
        /// which keeps generation inside the 4ms budget (TECH 14) — measured at ~0.07ms
        /// per chunk.
        ///
        /// **This caps a chunk at one tree per cell**, so <see cref="MaxTreesPerChunk"/>
        /// is the ceiling on any biome's BaseDensity. TECH 5.1 targets ~40 in the dense
        /// ring, so there is room, but a biome asking for more will silently get less.
        /// Raising this changes generation output — bump <c>WorldGenVersion</c>.
        /// </summary>
        private const int PlacementGridResolution = 8;

        /// <summary>Hard ceiling on trees in one chunk, set by the placement grid.</summary>
        public const int MaxTreesPerChunk = PlacementGridResolution * PlacementGridResolution;

        public static ChunkData Generate(int worldSeed, int chunkX, int chunkZ,
            BiomeSet biomes, WorldGenSettings settings)
        {
            const int size = WorldConstants.ChunkSize;
            const float cell = (float)size / PlacementGridResolution;

            DeterministicRandom random = new(DeterministicRandom.Seed(worldSeed, chunkX, chunkZ));

            List<GeneratedTree> trees = new();
            float[] density = new float[ChunkData.DensityResolution * ChunkData.DensityResolution];

            Vector3 origin = new(chunkX * size, 0f, chunkZ * size);

            /* Fixed iteration order. A dictionary or a parallel loop here would be a
             * determinism bug that only appears on some machines. */
            for (int gz = 0; gz < PlacementGridResolution; gz++)
            for (int gx = 0; gx < PlacementGridResolution; gx++)
            {
                // Draw for every cell whether or not a tree lands, so the sequence of
                // random numbers does not depend on how many trees were placed.
                float jitterX = random.NextFloat();
                float jitterZ = random.NextFloat();
                float acceptRoll = random.NextFloat();
                float tierRoll = random.NextFloat();
                float speciesRoll = random.NextFloat();
                float rotation = random.NextFloat(0f, 360f);
                float scaleRoll = random.NextFloat();

                Vector3 local = new((gx + jitterX) * cell, 0f, (gz + jitterZ) * cell);
                float distance = (origin + local).magnitude;

                biomes.Resolve(distance, out BiomeDefinition current, out BiomeDefinition previous, out float blend);

                if (current == null)
                    continue;

                float baseDensity = current.BaseDensity;

                if (previous != null && blend > 0f)
                    baseDensity = Mathf.Lerp(current.BaseDensity, previous.BaseDensity, blend);

                // Trees-per-chunk to a per-cell probability.
                float cellCount = PlacementGridResolution * PlacementGridResolution;
                float chance = Mathf.Clamp01(baseDensity / cellCount) * ClearingMask(distance, settings);

                if (acceptRoll >= chance)
                    continue;

                BiomeDefinition source = previous != null && blend > 0f && speciesRoll < blend
                    ? previous
                    : current;

                if (!TryPickTree(source, tierRoll, out TreeSpawnEntry entry, out byte speciesIndex))
                    continue;

                float scale = Mathf.Lerp(
                    Mathf.Max(0.01f, entry.ScaleRange.x),
                    Mathf.Max(0.01f, entry.ScaleRange.y),
                    scaleRoll);

                trees.Add(new GeneratedTree(local, rotation, scale, entry.Tier, speciesIndex));
                AccumulateDensity(density, local);
            }

            NormalizeDensity(density);

            return new ChunkData(chunkX, chunkZ, trees.ToArray(), density);
        }

        /// <summary>
        /// 0 inside the authored clearing, ramping to 1 across the band outside it
        /// (TECH 5.7).
        /// </summary>
        private static float ClearingMask(float distanceFromOrigin, WorldGenSettings settings)
        {
            if (distanceFromOrigin <= settings.ClearingRadius)
                return 0f;

            if (settings.ClearingRampWidth <= 0f)
                return 1f;

            float intoRamp = distanceFromOrigin - settings.ClearingRadius;
            return Mathf.Clamp01(intoRamp / settings.ClearingRampWidth);
        }

        /// <summary>Weighted pick over the biome's tree entries.</summary>
        private static bool TryPickTree(BiomeDefinition biome, float roll,
            out TreeSpawnEntry entry, out byte speciesIndex)
        {
            entry = default;
            speciesIndex = 0;

            float total = biome.TotalTreeWeight;

            if (biome.Trees.Length == 0 || total <= 0f)
                return false;

            float target = roll * total;
            float running = 0f;

            for (int i = 0; i < biome.Trees.Length; i++)
            {
                running += Mathf.Max(0f, biome.Trees[i].Weight);

                if (target >= running)
                    continue;

                entry = biome.Trees[i];
                speciesIndex = (byte)i;
                return true;
            }

            // Float error at the very top of the range.
            entry = biome.Trees[biome.Trees.Length - 1];
            speciesIndex = (byte)(biome.Trees.Length - 1);
            return true;
        }

        /// <summary>
        /// Splats a tree into the density grid, including its immediate neighbours, so
        /// density reads as a smooth field rather than a checkerboard of occupied cells.
        /// </summary>
        private static void AccumulateDensity(float[] density, Vector3 local)
        {
            const int res = ChunkData.DensityResolution;

            int cx = Mathf.Clamp((int)(local.x / ChunkData.DensityCellSize), 0, res - 1);
            int cz = Mathf.Clamp((int)(local.z / ChunkData.DensityCellSize), 0, res - 1);

            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = cx + dx;
                int z = cz + dz;

                if (x < 0 || x >= res || z < 0 || z >= res)
                    continue;

                // Centre counts fully, neighbours bleed.
                density[z * res + x] += dx == 0 && dz == 0 ? 1f : 0.35f;
            }
        }

        /// <summary>
        /// Maps accumulated counts into roughly 0–1 against a fixed reference rather than
        /// the chunk's own maximum. Normalising per chunk would make a sparse chunk look
        /// as dark as a dense one, and the darkness system would stop meaning anything.
        /// </summary>
        private static void NormalizeDensity(float[] density)
        {
            const float reference = 4f;

            for (int i = 0; i < density.Length; i++)
                density[i] = Mathf.Clamp01(density[i] / reference);
        }
    }
}
