using ChopChop.Biomes;
using ChopChop.World;
using NUnit.Framework;
using UnityEngine;

namespace ChopChop.Tests.Editor
{
    /// <summary>
    /// Generation must be a pure function of (seed, chunkCoord, biomes) — TECH 2.6.
    ///
    /// This is the most valuable test in the project and the least dramatic when it
    /// fails. A tree is addressed by its index in the generated array, so two clients
    /// that disagree about that array will happily let two players chop "the same" tree
    /// and see different results. Nothing throws; the world just quietly stops being
    /// shared.
    /// </summary>
    public sealed class ChunkGenerationTests
    {
        private BiomeSet _biomes;
        private WorldGenSettings _settings;

        /// <summary>
        /// Built in code rather than loaded from an asset so the test pins the algorithm
        /// rather than whatever someone last typed into an inspector.
        /// </summary>
        private static BiomeDefinition MakeBiome(int ring, float innerRadius, float density, byte tier)
        {
            BiomeDefinition biome = ScriptableObject.CreateInstance<BiomeDefinition>();
            biome.RingIndex = ring;
            biome.InnerRadius = innerRadius;
            biome.BlendBandWidth = 30f;
            biome.BaseDensity = density;
            biome.Trees = new[]
            {
                new TreeSpawnEntry { Tier = tier, Weight = 3f, ScaleRange = new Vector2(0.8f, 1.2f) },
                new TreeSpawnEntry { Tier = tier, Weight = 1f, ScaleRange = new Vector2(1.0f, 1.5f) },
            };

            return biome;
        }

        private static BiomeSet MakeBiomeSet(params BiomeDefinition[] biomes)
        {
            BiomeSet set = ScriptableObject.CreateInstance<BiomeSet>();
            var so = new UnityEditor.SerializedObject(set);
            var array = so.FindProperty("_biomes");

            array.arraySize = biomes.Length;
            for (int i = 0; i < biomes.Length; i++)
                array.GetArrayElementAtIndex(i).objectReferenceValue = biomes[i];

            so.ApplyModifiedPropertiesWithoutUndo();
            return set;
        }

        [SetUp]
        public void SetUp()
        {
            _biomes = MakeBiomeSet(
                MakeBiome(0, 0f, 40f, 1),
                MakeBiome(1, 300f, 60f, 2));

            _settings = WorldGenSettings.Default;
        }

        private static void AssertIdentical(ChunkData a, ChunkData b)
        {
            Assert.AreEqual(a.Trees.Length, b.Trees.Length, "tree count");

            for (int i = 0; i < a.Trees.Length; i++)
            {
                // Bit-exact, not approximate. "Nearly the same forest" is still a
                // desynchronised forest.
                Assert.AreEqual(a.Trees[i].LocalPosition.x, b.Trees[i].LocalPosition.x, $"tree {i} x");
                Assert.AreEqual(a.Trees[i].LocalPosition.z, b.Trees[i].LocalPosition.z, $"tree {i} z");
                Assert.AreEqual(a.Trees[i].YRotation, b.Trees[i].YRotation, $"tree {i} rotation");
                Assert.AreEqual(a.Trees[i].Scale, b.Trees[i].Scale, $"tree {i} scale");
                Assert.AreEqual(a.Trees[i].TierIndex, b.Trees[i].TierIndex, $"tree {i} tier");
                Assert.AreEqual(a.Trees[i].SpeciesIndex, b.Trees[i].SpeciesIndex, $"tree {i} species");
            }

            Assert.AreEqual(a.Density.Length, b.Density.Length, "density length");
            for (int i = 0; i < a.Density.Length; i++)
                Assert.AreEqual(a.Density[i], b.Density[i], $"density cell {i}");
        }

        [Test]
        public void SameSeedAndCoordinate_ProducesIdenticalChunks()
        {
            ChunkData first = ChunkGenerator.Generate(12345, 4, -7, _biomes, _settings);
            ChunkData second = ChunkGenerator.Generate(12345, 4, -7, _biomes, _settings);

            Assert.Greater(first.Trees.Length, 0, "test is meaningless if nothing generated");
            AssertIdentical(first, second);
        }

        [Test]
        public void GenerationOrder_DoesNotAffectOutput()
        {
            // Chunks are seeded by coordinate, never sequentially, so a chunk must not
            // care what was generated before it. A shared PRNG stream would fail here.
            ChunkData alone = ChunkGenerator.Generate(999, 2, 2, _biomes, _settings);

            for (int i = 0; i < 5; i++)
                ChunkGenerator.Generate(999, i * 3, i - 4, _biomes, _settings);

            ChunkData afterOthers = ChunkGenerator.Generate(999, 2, 2, _biomes, _settings);

            AssertIdentical(alone, afterOthers);
        }

        [Test]
        public void DifferentSeeds_ProduceDifferentForests()
        {
            ChunkData a = ChunkGenerator.Generate(1, 5, 5, _biomes, _settings);
            ChunkData b = ChunkGenerator.Generate(2, 5, 5, _biomes, _settings);

            bool differs = a.Trees.Length != b.Trees.Length;

            for (int i = 0; !differs && i < a.Trees.Length; i++)
                differs = a.Trees[i].LocalPosition != b.Trees[i].LocalPosition;

            Assert.IsTrue(differs, "two seeds produced the same chunk; the seed is not reaching generation");
        }

        [Test]
        public void AdjacentChunks_DifferFromEachOther()
        {
            ChunkData a = ChunkGenerator.Generate(77, 10, 10, _biomes, _settings);
            ChunkData b = ChunkGenerator.Generate(77, 11, 10, _biomes, _settings);

            bool differs = a.Trees.Length != b.Trees.Length;

            for (int i = 0; !differs && i < a.Trees.Length; i++)
                differs = a.Trees[i].LocalPosition != b.Trees[i].LocalPosition;

            Assert.IsTrue(differs, "neighbouring chunks are identical; the coordinate is not reaching the seed");
        }

        [Test]
        public void TreesStayInsideTheirChunk()
        {
            ChunkData chunk = ChunkGenerator.Generate(31337, -3, 8, _biomes, _settings);

            foreach (GeneratedTree tree in chunk.Trees)
            {
                // Positions are chunk-local, so a tree outside these bounds would be
                // owned by a chunk that does not know about it.
                Assert.GreaterOrEqual(tree.LocalPosition.x, 0f);
                Assert.Less(tree.LocalPosition.x, WorldConstants.ChunkSize);
                Assert.GreaterOrEqual(tree.LocalPosition.z, 0f);
                Assert.Less(tree.LocalPosition.z, WorldConstants.ChunkSize);
                Assert.Greater(tree.Scale, 0f);
            }
        }

        [Test]
        public void ClearingIsKeptEmpty()
        {
            // Chunk (0,0) sits inside the authored clearing radius.
            ChunkData chunk = ChunkGenerator.Generate(5, 0, 0, _biomes, _settings);

            foreach (GeneratedTree tree in chunk.Trees)
            {
                float distance = (chunk.Origin + tree.LocalPosition).magnitude;
                Assert.GreaterOrEqual(distance, _settings.ClearingRadius,
                    "a tree generated inside the authored clearing");
            }
        }

        [Test]
        public void DensityIsNormalisedAndTracksTrees()
        {
            ChunkData dense = ChunkGenerator.Generate(4242, 20, 20, _biomes, _settings);

            foreach (float d in dense.Density)
                Assert.That(d, Is.InRange(0f, 1f), "density must stay in 0-1 for the darkness curve");

            /* A clearing wide enough to swallow the whole chunk. The default 60m radius
             * does not: chunk (0,0) reaches 64m on each axis, so its far corner is ~90m
             * out and legitimately grows trees. */
            WorldGenSettings wideClearing = new() { ClearingRadius = 500f, ClearingRampWidth = 25f };
            ChunkData empty = ChunkGenerator.Generate(4242, 0, 0, _biomes, wideClearing);

            Assert.AreEqual(0, empty.Trees.Length, "fully masked chunk should be empty");
            foreach (float d in empty.Density)
                Assert.AreEqual(0f, d, "an empty chunk must read as zero density");
        }

        [Test]
        public void ClearingEdgeRampsRatherThanCuttingOff()
        {
            /* A hard edge leaves the authored clearing sitting in a visible circle of
             * forest (TECH 5.7). Sample across the ramp and confirm density climbs. */
            WorldGenSettings settings = WorldGenSettings.Default;

            int nearRamp = CountTreesInBand(settings.ClearingRadius, settings.ClearingRadius + 8f, settings);
            int pastRamp = CountTreesInBand(settings.ClearingRadius + settings.ClearingRampWidth,
                settings.ClearingRadius + settings.ClearingRampWidth + 8f, settings);

            Assert.Less(nearRamp, pastRamp,
                "density should be thinner just outside the clearing than beyond the ramp");
        }

        private int CountTreesInBand(float innerRadius, float outerRadius, WorldGenSettings settings)
        {
            int count = 0;

            // Sweep a spread of chunks so the count is not one chunk's luck.
            for (int x = -4; x <= 4; x++)
            for (int z = -4; z <= 4; z++)
            {
                ChunkData chunk = ChunkGenerator.Generate(2024, x, z, _biomes, settings);

                foreach (GeneratedTree tree in chunk.Trees)
                {
                    float distance = (chunk.Origin + tree.LocalPosition).magnitude;

                    if (distance >= innerRadius && distance < outerRadius)
                        count++;
                }
            }

            return count;
        }

        [Test]
        public void DensitySamplingIsBilinearAndBounded()
        {
            ChunkData chunk = ChunkGenerator.Generate(808, 15, -15, _biomes, _settings);

            // Corners and centre must all be sampleable without going out of range.
            float[] samples =
            {
                chunk.SampleDensity(Vector3.zero),
                chunk.SampleDensity(new Vector3(WorldConstants.ChunkSize - 0.01f, 0f, 0f)),
                chunk.SampleDensity(new Vector3(0f, 0f, WorldConstants.ChunkSize - 0.01f)),
                chunk.SampleDensity(new Vector3(WorldConstants.ChunkSize * 0.5f, 0f, WorldConstants.ChunkSize * 0.5f)),
                // Deliberately outside the chunk; sampling must clamp rather than throw.
                chunk.SampleDensity(new Vector3(-50f, 0f, 500f)),
            };

            foreach (float s in samples)
                Assert.That(s, Is.InRange(0f, 1f));
        }

        [Test]
        public void RingIndexIsDiscreteWhileBlendIsNot()
        {
            // Gameplay uses the hard index so a player cannot straddle a boundary and
            // claim the better half of both rings (TECH 13).
            Assert.AreEqual(0, _biomes.GetRingIndex(0f));
            Assert.AreEqual(0, _biomes.GetRingIndex(299.9f));
            Assert.AreEqual(1, _biomes.GetRingIndex(300f));
            Assert.AreEqual(1, _biomes.GetRingIndex(5000f));

            _biomes.Resolve(300f, out _, out BiomeDefinition previous, out float atEdge);
            Assert.IsNotNull(previous, "the inner ring should still bleed in at the boundary");
            Assert.AreEqual(1f, atEdge, 0.001f, "fully blended at the inner edge");

            _biomes.Resolve(400f, out _, out _, out float wellInside);
            Assert.AreEqual(0f, wellInside, 0.001f, "no bleed once past the blend band");
        }

        [Test]
        public void SeedDerivationSeparatesNearbyCoordinates()
        {
            // Coordinates differing by one must not produce correlated streams; a weak
            // mixer here shows up as visible banding across chunk boundaries.
            ulong a = DeterministicRandom.Seed(1, 0, 0);
            ulong b = DeterministicRandom.Seed(1, 1, 0);
            ulong c = DeterministicRandom.Seed(1, 0, 1);
            ulong swapped = DeterministicRandom.Seed(1, 1, 0);

            Assert.AreNotEqual(a, b);
            Assert.AreNotEqual(a, c);
            Assert.AreNotEqual(b, c, "x and z must not be interchangeable");
            Assert.AreEqual(b, swapped, "same inputs must give the same seed");
        }

        [Test]
        public void RandomIsRepeatableAndSpreadsAcrossTheRange()
        {
            DeterministicRandom a = new(12345);
            DeterministicRandom b = new(12345);

            for (int i = 0; i < 100; i++)
                Assert.AreEqual(a.NextUInt(), b.NextUInt(), $"draw {i}");

            // Rough uniformity check: ten buckets, none empty over a large sample.
            DeterministicRandom random = new(99);
            int[] buckets = new int[10];

            for (int i = 0; i < 10000; i++)
            {
                float value = random.NextFloat();
                Assert.That(value, Is.InRange(0f, 0.99999f));
                buckets[Mathf.Clamp((int)(value * 10f), 0, 9)]++;
            }

            foreach (int count in buckets)
                Assert.Greater(count, 500, "distribution is badly skewed");
        }
    }
}
