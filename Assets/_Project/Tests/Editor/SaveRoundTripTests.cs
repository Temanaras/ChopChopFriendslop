using System.Collections.Generic;
using System.IO;
using ChopChop.Cabin;
using ChopChop.Items;
using ChopChop.Persistence;
using ChopChop.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace ChopChop.Tests.Editor
{
    /// <summary>
    /// Serialize, deserialize, compare (TECH 15). Run on every schema change.
    ///
    /// The save format is also the host migration payload (TECH 2.4), so a field that
    /// silently fails to round-trip does not just lose progress — it loses the world the
    /// moment a host drops.
    /// </summary>
    public sealed class SaveRoundTripTests
    {
        private static WorldSave BuildPopulatedWorld()
        {
            WorldSave save = WorldSave.CreateNew(worldSeed: -1234567);
            save.SaveVersion = 9;
            save.WorldTick = 543210;

            save.Cabin = new CabinState(
                storage: new[]
                {
                    new ItemStack(1, 64),
                    new ItemStack(7, 1, durability: 250),
                    ItemStack.Empty,
                },
                builtStationIds: new byte[] { 3, 9 },
                unlockedRings: new byte[] { 0, 1 });

            // Negative coordinates matter: the key packs two ints into one long, and
            // sign extension is the easy thing to get wrong.
            save.Chunks[ChunkKey.Pack(0, 0)] = new ChunkSave(100, new[]
            {
                new TreeDiff(0, TreeDiff.FullHealth, 0),
                new TreeDiff(41, 128, 55),
            });
            save.Chunks[ChunkKey.Pack(-3, 12)] = new ChunkSave(4096, new[]
            {
                new TreeDiff(ushort.MaxValue, 0, uint.MaxValue),
            });

            return save;
        }

        private static void AssertWorldsMatch(WorldSave expected, WorldSave actual)
        {
            Assert.AreEqual(expected.WorldSeed, actual.WorldSeed, "worldSeed");
            Assert.AreEqual(expected.SaveVersion, actual.SaveVersion, "saveVersion");
            Assert.AreEqual(expected.WorldTick, actual.WorldTick, "worldTick");

            Assert.AreEqual(expected.Cabin.Storage.Length, actual.Cabin.Storage.Length, "storage length");
            for (int i = 0; i < expected.Cabin.Storage.Length; i++)
            {
                Assert.AreEqual(expected.Cabin.Storage[i].ItemId, actual.Cabin.Storage[i].ItemId, $"storage[{i}].itemId");
                Assert.AreEqual(expected.Cabin.Storage[i].Count, actual.Cabin.Storage[i].Count, $"storage[{i}].count");
                Assert.AreEqual(expected.Cabin.Storage[i].Durability, actual.Cabin.Storage[i].Durability, $"storage[{i}].durability");
            }

            CollectionAssert.AreEqual(expected.Cabin.BuiltStationIds, actual.Cabin.BuiltStationIds, "builtStationIds");
            CollectionAssert.AreEqual(expected.Cabin.UnlockedRings, actual.Cabin.UnlockedRings, "unlockedRings");

            Assert.AreEqual(expected.Chunks.Count, actual.Chunks.Count, "chunk count");

            foreach (KeyValuePair<long, ChunkSave> pair in expected.Chunks)
            {
                Assert.IsTrue(actual.Chunks.ContainsKey(pair.Key), $"missing chunk {pair.Key}");
                ChunkSave actualChunk = actual.Chunks[pair.Key];

                Assert.AreEqual(pair.Value.LastVisitedTick, actualChunk.LastVisitedTick, "lastVisitedTick");
                Assert.AreEqual(pair.Value.Diffs.Length, actualChunk.Diffs.Length, "diff count");

                for (int i = 0; i < pair.Value.Diffs.Length; i++)
                {
                    Assert.AreEqual(pair.Value.Diffs[i].LocalIndex, actualChunk.Diffs[i].LocalIndex, "localIndex");
                    Assert.AreEqual(pair.Value.Diffs[i].HealthRemaining, actualChunk.Diffs[i].HealthRemaining, "healthRemaining");
                    Assert.AreEqual(pair.Value.Diffs[i].FelledAtTick, actualChunk.Diffs[i].FelledAtTick, "felledAtTick");
                }
            }
        }

        [Test]
        public void WorldSave_RoundTrips()
        {
            WorldSave original = BuildPopulatedWorld();

            byte[] bytes = SaveSerializer.Serialize(original);
            Assert.Greater(bytes.Length, 0, "serialized to nothing");

            SaveLoadStatus status = SaveSerializer.TryDeserialize(bytes, out WorldSave loaded);

            Assert.AreEqual(SaveLoadStatus.Ok, status);
            AssertWorldsMatch(original, loaded);
        }

        [Test]
        public void ChunkKey_RoundTripsNegativeCoordinates()
        {
            int[] coords = { 0, 1, -1, 12, -3, int.MaxValue, int.MinValue };

            foreach (int x in coords)
            foreach (int z in coords)
            {
                ChunkKey.Unpack(ChunkKey.Pack(x, z), out int outX, out int outZ);
                Assert.AreEqual(x, outX, $"x for ({x},{z})");
                Assert.AreEqual(z, outZ, $"z for ({x},{z})");
            }
        }

        [Test]
        public void ChunkKey_IsUniquePerCoordinate()
        {
            HashSet<long> seen = new();

            for (int x = -8; x <= 8; x++)
            for (int z = -8; z <= 8; z++)
                Assert.IsTrue(seen.Add(ChunkKey.Pack(x, z)), $"collision at ({x},{z})");
        }

        [Test]
        public void PlayerSave_RoundTripsIncludingUnityTypes()
        {
            PlayerSave original = PlayerSave.CreateNew(76561197993206881UL);
            original.Position = new Vector3(12.5f, -3.25f, 400f);
            original.Rotation = Quaternion.Euler(0f, 137f, 0f);
            original.Paperdoll[(int)ItemSlot.Axe] = new ItemStack(11, 1, 900);
            original.Paperdoll[(int)ItemSlot.Backpack] = new ItemStack(22, 1);
            original.Inventory = new[] { new ItemStack(5, 30) };

            byte[] bytes = SaveSerializer.Serialize(original);
            SaveLoadStatus status = SaveSerializer.TryDeserialize(bytes, out PlayerSave loaded);

            Assert.AreEqual(SaveLoadStatus.Ok, status);
            Assert.AreEqual(original.SteamId, loaded.SteamId);
            Assert.AreEqual(ItemSlots.Count, loaded.Paperdoll.Length, "paperdoll is slot-indexed");
            Assert.AreEqual(11, loaded.Paperdoll[(int)ItemSlot.Axe].ItemId);
            Assert.AreEqual(900, loaded.Paperdoll[(int)ItemSlot.Axe].Durability);
            Assert.AreEqual(22, loaded.Paperdoll[(int)ItemSlot.Backpack].ItemId);
            Assert.AreEqual(1, loaded.Inventory.Length);
            Assert.AreEqual(5, loaded.Inventory[0].ItemId);

            // Unity types need MemoryPack's Unity formatters, which are otherwise only
            // registered when entering play mode.
            Assert.That(loaded.Position.x, Is.EqualTo(original.Position.x).Within(0.0001f));
            Assert.That(loaded.Position.y, Is.EqualTo(original.Position.y).Within(0.0001f));
            Assert.That(loaded.Position.z, Is.EqualTo(original.Position.z).Within(0.0001f));
            Assert.That(Quaternion.Angle(original.Rotation, loaded.Rotation), Is.LessThan(0.01f));
        }

        [Test]
        public void Deserialize_RefusesSaveFromNewerBuild()
        {
            WorldSave original = BuildPopulatedWorld();
            byte[] bytes = SaveSerializer.Serialize(original);

            // Reach in and claim a future format, the way a save written by a later
            // build would look.
            SaveSerializer.TryDeserialize(bytes, out WorldSave loaded);
            loaded.SaveFormatVersion = SaveFormat.Version + 1;
            byte[] futureBytes = MemoryPack.MemoryPackSerializer.Serialize(loaded);

            LogAssert.ignoreFailingMessages = true;
            SaveLoadStatus status = SaveSerializer.TryDeserialize(futureBytes, out WorldSave _);
            LogAssert.ignoreFailingMessages = false;

            Assert.AreEqual(SaveLoadStatus.TooNew, status);
        }

        [Test]
        public void Deserialize_ReportsCorruptRatherThanThrowing()
        {
            byte[] garbage = { 0xFF, 0x01, 0x02, 0x03, 0x04 };

            LogAssert.ignoreFailingMessages = true;
            SaveLoadStatus status = SaveSerializer.TryDeserialize(garbage, out WorldSave _);
            LogAssert.ignoreFailingMessages = false;

            Assert.AreNotEqual(SaveLoadStatus.Ok, status);
        }

        [Test]
        public void SaveStore_WriteIsAtomicAndKeepsABackup()
        {
            string dir = Path.Combine(Path.GetTempPath(), "chopchop-savetests-" + Path.GetRandomFileName());
            SaveStore store = new(dir);

            try
            {
                WorldSave first = WorldSave.CreateNew(1);
                first.SaveVersion = 1;
                Assert.IsTrue(store.TryWrite("world.sav", SaveSerializer.Serialize(first)));

                WorldSave second = WorldSave.CreateNew(2);
                second.SaveVersion = 2;
                Assert.IsTrue(store.TryWrite("world.sav", SaveSerializer.Serialize(second)));

                Assert.IsTrue(store.TryRead("world.sav", out byte[] data, out bool usedBackup));
                Assert.IsFalse(usedBackup);
                SaveSerializer.TryDeserialize(data, out WorldSave loaded);
                Assert.AreEqual(2u, loaded.SaveVersion, "second write should win");

                // The previous copy must survive as the backup.
                Assert.IsTrue(File.Exists(store.PathFor("world.sav") + SaveStore.BackupExtension));

                // No temp file may be left lying around.
                Assert.IsFalse(File.Exists(store.PathFor("world.sav") + ".tmp"));
            }
            finally
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void SaveStore_FallsBackToBackupWhenPrimaryIsUnreadable()
        {
            string dir = Path.Combine(Path.GetTempPath(), "chopchop-savetests-" + Path.GetRandomFileName());
            SaveStore store = new(dir);

            try
            {
                WorldSave good = WorldSave.CreateNew(1);
                good.SaveVersion = 1;
                store.TryWrite("world.sav", SaveSerializer.Serialize(good));

                WorldSave newer = WorldSave.CreateNew(2);
                newer.SaveVersion = 2;
                store.TryWrite("world.sav", SaveSerializer.Serialize(newer));

                // Simulate a crash that left the primary truncated.
                File.WriteAllBytes(store.PathFor("world.sav"), new byte[0]);

                LogAssert.ignoreFailingMessages = true;
                bool read = store.TryRead("world.sav", out byte[] data, out bool usedBackup);
                LogAssert.ignoreFailingMessages = false;

                Assert.IsTrue(read, "should have recovered from backup");
                Assert.IsTrue(usedBackup);

                SaveSerializer.TryDeserialize(data, out WorldSave loaded);
                Assert.AreEqual(1u, loaded.SaveVersion, "backup holds the previous write");
            }
            finally
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
        }
    }
}
