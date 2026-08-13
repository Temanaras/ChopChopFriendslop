using System.Collections.Generic;
using ChopChop.Biomes;
using ChopChop.World;
using NUnit.Framework;
using UnityEngine;

namespace ChopChop.Tests.Editor
{
    public sealed class ChunkStoreTests
    {
        private BiomeSet _biomes;
        private WorldGenSettings _settings;

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

            _biomes = ScriptableObject.CreateInstance<BiomeSet>();
            var so = new UnityEditor.SerializedObject(_biomes);
            var array = so.FindProperty("_biomes");
            array.arraySize = 1;
            array.GetArrayElementAtIndex(0).objectReferenceValue = biome;
            so.ApplyModifiedPropertiesWithoutUndo();

            _settings = WorldGenSettings.Default;
        }

        [Test]
        public void WorldToChunk_FloorsRatherThanTruncates()
        {
            /* Truncation maps -0.5 and +0.5 to the same chunk, putting a seam through
             * the origin where two positions on opposite sides share a chunk and their
             * neighbours do not. */
            ChunkStore.WorldToChunk(new Vector3(0f, 0f, 0f), out int x, out int z);
            Assert.AreEqual(0, x);
            Assert.AreEqual(0, z);

            ChunkStore.WorldToChunk(new Vector3(-1f, 0f, -1f), out x, out z);
            Assert.AreEqual(-1, x, "just left of the origin belongs to chunk -1, not 0");
            Assert.AreEqual(-1, z);

            ChunkStore.WorldToChunk(new Vector3(WorldConstants.ChunkSize - 0.01f, 0f, 0f), out x, out _);
            Assert.AreEqual(0, x, "the last metre of a chunk is still that chunk");

            ChunkStore.WorldToChunk(new Vector3(WorldConstants.ChunkSize, 0f, 0f), out x, out _);
            Assert.AreEqual(1, x);

            ChunkStore.WorldToChunk(new Vector3(-WorldConstants.ChunkSize, 0f, 0f), out x, out _);
            Assert.AreEqual(-1, x);
        }

        [Test]
        public void GetOrGenerate_CachesRatherThanRegenerating()
        {
            ChunkStore store = new(1234, _biomes, _settings);

            ChunkData first = store.GetOrGenerate(3, 4);
            ChunkData second = store.GetOrGenerate(3, 4);

            Assert.AreSame(first, second, "second call should hit the cache");
            Assert.AreEqual(1, store.LoadedCount);
        }

        [Test]
        public void UpdateResidency_LoadsAroundTheCentreAndEvictsBehind()
        {
            ChunkStore store = new(1234, _biomes, _settings);

            store.UpdateResidency(new List<Vector3> { Vector3.zero }, radiusInChunks: 2);

            int nearOrigin = store.LoadedCount;
            Assert.Greater(nearOrigin, 0);
            Assert.IsTrue(store.IsLoaded(0, 0), "the centre chunk must be resident");

            // Move far enough that none of the old set is wanted.
            Vector3 faraway = new(WorldConstants.ChunkSize * 40f, 0f, 0f);
            store.UpdateResidency(new List<Vector3> { faraway }, radiusInChunks: 2);

            Assert.IsFalse(store.IsLoaded(0, 0), "chunks left behind should be evicted");
            Assert.IsTrue(store.IsLoaded(40, 0), "chunks at the new centre should be resident");
            Assert.AreEqual(nearOrigin, store.LoadedCount, "residency size should be stable while moving");
        }

        [Test]
        public void UpdateResidency_KeepsChunksSharedByTwoCentres()
        {
            // The server keeps the band around every player, so overlapping regions must
            // not be evicted by whichever centre is processed last (TECH 5.4).
            ChunkStore store = new(1234, _biomes, _settings);

            Vector3 a = Vector3.zero;
            Vector3 b = new(WorldConstants.ChunkSize * 3f, 0f, 0f);

            store.UpdateResidency(new List<Vector3> { a, b }, radiusInChunks: 2);

            Assert.IsTrue(store.IsLoaded(0, 0), "chunk at the first centre");
            Assert.IsTrue(store.IsLoaded(3, 0), "chunk at the second centre");
            Assert.IsTrue(store.IsLoaded(1, 0), "chunk between them, wanted by both");
        }

        [Test]
        public void EvictedChunk_RegeneratesIdentically()
        {
            /* Eviction is safe precisely because generation is reproducible. If this
             * ever fails, dropping a chunk starts destroying world state. */
            ChunkStore store = new(555, _biomes, _settings);

            ChunkData before = store.GetOrGenerate(7, -2);
            int treeCount = before.Trees.Length;
            Vector3 firstPosition = treeCount > 0 ? before.Trees[0].LocalPosition : Vector3.zero;

            store.Clear();
            Assert.AreEqual(0, store.LoadedCount);

            ChunkData after = store.GetOrGenerate(7, -2);

            Assert.AreNotSame(before, after, "should genuinely have regenerated");
            Assert.AreEqual(treeCount, after.Trees.Length);

            if (treeCount > 0)
                Assert.AreEqual(firstPosition, after.Trees[0].LocalPosition);
        }

        [Test]
        public void Residency_IsCircularNotSquare()
        {
            ChunkStore store = new(1, _biomes, _settings);
            store.UpdateResidency(new List<Vector3> { Vector3.zero }, radiusInChunks: 3);

            Assert.IsTrue(store.IsLoaded(3, 0), "on-axis edge is within the radius");
            Assert.IsFalse(store.IsLoaded(3, 3),
                "the far diagonal is outside the radius; a square region would load it");
        }
    }
}
