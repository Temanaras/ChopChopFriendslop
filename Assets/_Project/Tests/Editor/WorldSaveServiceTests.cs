using System.IO;
using ChopChop.Persistence;
using ChopChop.World;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace ChopChop.Tests.Editor
{
    /// <summary>
    /// The server is the single writer of the world. These pin the two behaviours that
    /// turn a recoverable problem into a lost world: silently replacing a save that
    /// could not be read, and losing the tail of a session on shutdown.
    /// </summary>
    public sealed class WorldSaveServiceTests
    {
        private string _dir;
        private SaveStore _store;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "chopchop-worldsave-" + Path.GetRandomFileName());
            _store = new SaveStore(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }

        [Test]
        public void CreatesANewWorldWhenNothingIsSaved()
        {
            WorldSaveService service = new(_store);

            Assert.AreEqual(SaveLoadStatus.Ok, service.LoadOrCreate(4242));
            Assert.IsNotNull(service.World);
            Assert.AreEqual(4242, service.World.WorldSeed);
        }

        [Test]
        public void ReloadsWhatItWrote()
        {
            WorldSaveService first = new(_store);
            first.LoadOrCreate(99);
            first.World.WorldTick = 12345;
            first.World.Chunks[ChunkKey.Pack(-2, 3)] = new ChunkSave(500, new[]
            {
                new TreeDiff(7, 0, 480),
            });

            Assert.IsTrue(first.Save());

            WorldSaveService second = new(_store);
            Assert.AreEqual(SaveLoadStatus.Ok, second.LoadOrCreate(newWorldSeed: 1));

            Assert.AreEqual(99, second.World.WorldSeed, "should not have used the new-world seed");
            Assert.AreEqual(12345u, second.World.WorldTick);
            Assert.AreEqual(1, second.World.Chunks.Count);

            ChunkSave chunk = second.World.Chunks[ChunkKey.Pack(-2, 3)];
            Assert.AreEqual(500u, chunk.LastVisitedTick);
            Assert.AreEqual(7, chunk.Diffs[0].LocalIndex);
            Assert.IsTrue(chunk.Diffs[0].IsFelled);
        }

        [Test]
        public void RefusesToStartOnAnUnreadableSaveRatherThanReplacingIt()
        {
            // Both the primary and its backup are unusable, which is the only case where
            // the store cannot recover on its own.
            Directory.CreateDirectory(_dir);
            File.WriteAllBytes(_store.PathFor(WorldSaveService.WorldFileName), new byte[] { 9, 9, 9, 9 });
            File.WriteAllBytes(_store.PathFor(WorldSaveService.WorldFileName) + SaveStore.BackupExtension,
                new byte[] { 9, 9, 9, 9 });

            WorldSaveService service = new(_store);

            LogAssert.ignoreFailingMessages = true;
            SaveLoadStatus status = service.LoadOrCreate(1);
            LogAssert.ignoreFailingMessages = false;

            Assert.AreNotEqual(SaveLoadStatus.Ok, status);
            Assert.IsNull(service.World, "a world we cannot read must not be silently replaced");
        }

        [Test]
        public void AutosaveWaitsForTheInterval()
        {
            WorldSaveService service = new(_store);
            service.LoadOrCreate(1);

            service.Tick(WorldSaveService.AutosaveIntervalSeconds - 1f);
            Assert.IsFalse(_store.Exists(WorldSaveService.WorldFileName), "saved too early");

            service.Tick(2f);
            Assert.IsTrue(_store.Exists(WorldSaveService.WorldFileName), "should have saved once the interval passed");
        }

        [Test]
        public void DisposeWritesTheTailOfTheSession()
        {
            WorldSaveService service = new(_store);
            service.LoadOrCreate(7);
            service.World.WorldTick = 999;

            // Nowhere near the autosave interval; a clean shutdown should still not
            // cost the player the last stretch of play.
            service.Tick(1f);
            service.Dispose();

            WorldSaveService reloaded = new(_store);
            reloaded.LoadOrCreate(0);
            Assert.AreEqual(999u, reloaded.World.WorldTick);
        }

        [Test]
        public void SaveVersionAdvancesWithEachWrite()
        {
            WorldSaveService service = new(_store);
            service.LoadOrCreate(1);

            uint before = service.World.SaveVersion;
            service.Save();
            service.Save();

            Assert.AreEqual(before + 2, service.World.SaveVersion);
        }

        [Test]
        public void SavedEventCarriesTheWrittenBytes()
        {
            WorldSaveService service = new(_store);
            service.LoadOrCreate(1);

            byte[] captured = null;
            service.Saved += bytes => captured = bytes;
            service.Save();

            Assert.IsNotNull(captured, "the snapshot transfer needs these bytes");
            Assert.AreEqual(SaveLoadStatus.Ok, SaveSerializer.TryDeserialize(captured, out WorldSave _));
        }
    }
}
