using System.Collections.Generic;
using ChopChop.Biomes;
using ChopChop.World;
using NUnit.Framework;
using UnityEngine;

namespace ChopChop.Tests.Editor
{
    /// <summary>
    /// Darkness reads this every frame, so it has to be cheap and it has to be seamless.
    /// A discontinuity at a chunk boundary reads as a lighting glitch, not as forest.
    /// </summary>
    public sealed class DensityFieldTests
    {
        private ChunkStore _store;

        [SetUp]
        public void SetUp()
        {
            BiomeDefinition biome = ScriptableObject.CreateInstance<BiomeDefinition>();
            biome.RingIndex = 0;
            biome.InnerRadius = 0f;
            biome.BaseDensity = 40f;
            biome.Trees = new[]
            {
                new TreeSpawnEntry { Tier = 1, Weight = 1f, ScaleRange = new Vector2(1f, 1f) },
            };

            BiomeSet set = ScriptableObject.CreateInstance<BiomeSet>();
            var so = new UnityEditor.SerializedObject(set);
            var array = so.FindProperty("_biomes");
            array.arraySize = 1;
            array.GetArrayElementAtIndex(0).objectReferenceValue = biome;
            so.ApplyModifiedPropertiesWithoutUndo();

            _store = new ChunkStore(4242, set, WorldGenSettings.Default);
        }

        [Test]
        public void UnloadedChunksReadAsOpenGround()
        {
            // Zero rather than an exception or a stale value: the world should fade
            // toward light at the edge of what is resident, not snap to black.
            DensityField field = new(_store);

            Assert.AreEqual(0f, field.Sample(new Vector3(100000f, 0f, 100000f)));
        }

        [Test]
        public void SamplesStayInRange()
        {
            for (int x = -2; x <= 2; x++)
            for (int z = -2; z <= 2; z++)
                _store.GetOrGenerate(x, z);

            DensityField field = new(_store);

            for (float x = -128f; x < 128f; x += 7f)
            for (float z = -128f; z < 128f; z += 7f)
                Assert.That(field.Sample(new Vector3(x, 0f, z)), Is.InRange(0f, 1f));
        }

        [Test]
        public void SamplingIsContinuousAcrossAChunkBoundary()
        {
            /* The seam that would appear if sampling clamped inside one chunk instead of
             * fetching each neighbouring cell from whichever chunk owns it. */
            for (int x = -1; x <= 1; x++)
            for (int z = -1; z <= 1; z++)
                _store.GetOrGenerate(x, z);

            DensityField field = new(_store);

            const float boundary = WorldConstants.ChunkSize; // x = 64, the chunk 0/1 edge
            float justBefore = field.Sample(new Vector3(boundary - 0.05f, 0f, 30f));
            float justAfter = field.Sample(new Vector3(boundary + 0.05f, 0f, 30f));

            Assert.That(Mathf.Abs(justAfter - justBefore), Is.LessThan(0.02f),
                "density jumped at a chunk boundary");
        }

        [Test]
        public void NegativeCoordinatesResolveToTheChunkLeftOfOrigin()
        {
            // Truncating instead of flooring would fold -0.5 and +0.5 into one chunk and
            // put a seam straight through the origin.
            _store.GetOrGenerate(-1, -1);
            _store.GetOrGenerate(0, 0);

            DensityField field = new(_store);

            float justLeft = field.Sample(new Vector3(-0.05f, 0f, -0.05f));
            float justRight = field.Sample(new Vector3(0.05f, 0f, 0.05f));

            Assert.That(Mathf.Abs(justRight - justLeft), Is.LessThan(0.02f),
                "density jumped across the origin");
        }

        [Test]
        public void DenseAreasReadHigherThanTheClearing()
        {
            // The clearing is masked empty, so it must read as open however the rest of
            // the world is tuned. Without this the darkness curve has nothing to say.
            for (int x = -4; x <= 4; x++)
            for (int z = -4; z <= 4; z++)
                _store.GetOrGenerate(x, z);

            DensityField field = new(_store);

            float clearing = field.Sample(Vector3.zero);

            float highest = 0f;
            foreach (ChunkData chunk in _store.Loaded)
            {
                foreach (GeneratedTree tree in chunk.Trees)
                    highest = Mathf.Max(highest, field.Sample(chunk.Origin + tree.LocalPosition));
            }

            Assert.AreEqual(0f, clearing, "the authored clearing must read as open ground");
            Assert.Greater(highest, 0.5f,
                "forest should reach well up the range, or half the darkness curve is unusable");
        }
    }
}
