using System.Collections.Generic;
using ChopChop.World;
using FishNet.Connection;
using NUnit.Framework;

namespace ChopChop.Tests.Editor
{
    /// <summary>
    /// Tree diffs are the only world state players actually create, and the invariant
    /// that an untouched chunk has no entry is what keeps an unexplored world free
    /// (TECH 5.3).
    /// </summary>
    public sealed class TreeDiffStoreTests
    {
        private const long ChunkA = 1;
        private const long ChunkB = 2;

        [Test]
        public void UntouchedTreesReadAsFullHealthWithoutStoringAnything()
        {
            TreeDiffStore store = new();

            Assert.AreEqual(TreeDiff.FullHealth, store.GetHealth(ChunkA, 0));
            Assert.IsFalse(store.IsFelled(ChunkA, 0));
            Assert.AreEqual(0, store.ChunkCount, "reading must not create an entry");
        }

        [Test]
        public void DamageAccumulatesAcrossSwings()
        {
            TreeDiffStore store = new();

            Assert.IsTrue(store.TryApplyDamage(ChunkA, 5, 100, worldTick: 10, out byte remaining, out bool felled));
            Assert.AreEqual(155, remaining);
            Assert.IsFalse(felled);

            Assert.IsTrue(store.TryApplyDamage(ChunkA, 5, 100, worldTick: 11, out remaining, out felled));
            Assert.AreEqual(55, remaining);
            Assert.IsFalse(felled);

            Assert.AreEqual(1, store.ChunkCount, "one chunk touched");
            Assert.AreEqual(1, store.GetDiffs(ChunkA).Count, "one tree touched");
        }

        [Test]
        public void DamageClampsToFelledRatherThanWrapping()
        {
            TreeDiffStore store = new();

            // A byte would wrap to a healthy tree if this subtracted naively.
            Assert.IsTrue(store.TryApplyDamage(ChunkA, 0, 200, worldTick: 1, out byte remaining, out bool felled));
            Assert.AreEqual(55, remaining);

            Assert.IsTrue(store.TryApplyDamage(ChunkA, 0, 200, worldTick: 2, out remaining, out felled));
            Assert.AreEqual(0, remaining, "must clamp, not wrap");
            Assert.IsTrue(felled);
            Assert.IsTrue(store.IsFelled(ChunkA, 0));
        }

        [Test]
        public void FellingAnAlreadyFelledTreeIsRefused()
        {
            TreeDiffStore store = new();
            store.TryApplyDamage(ChunkA, 3, 255, worldTick: 5, out _, out _);

            Assert.IsFalse(store.TryApplyDamage(ChunkA, 3, 100, worldTick: 6, out byte remaining, out _),
                "a felled tree cannot take more damage");
            Assert.AreEqual(0, remaining);
        }

        [Test]
        public void FelledTickIsRecordedOnlyOnceDown()
        {
            TreeDiffStore store = new();

            store.TryApplyDamage(ChunkA, 1, 10, worldTick: 100, out _, out _);
            Assert.AreEqual(0u, store.GetDiffs(ChunkA)[0].FelledAtTick,
                "a damaged but standing tree has no felled tick");

            store.TryApplyDamage(ChunkA, 1, 255, worldTick: 250, out _, out bool felled);
            Assert.IsTrue(felled);
            Assert.AreEqual(250u, store.GetDiffs(ChunkA)[0].FelledAtTick, "regrowth measures from this");
        }

        [Test]
        public void RemovingTheLastDiffDropsTheChunkEntry()
        {
            // Regrowth reclaims trees by removing diffs; a chunk fully reclaimed must
            // cost nothing again, or an old world would never shrink back down.
            TreeDiffStore store = new();
            store.TryApplyDamage(ChunkA, 0, 255, worldTick: 1, out _, out _);
            store.TryApplyDamage(ChunkA, 1, 255, worldTick: 1, out _, out _);

            Assert.AreEqual(1, store.ChunkCount);

            Assert.IsTrue(store.RemoveDiff(ChunkA, 0));
            Assert.AreEqual(1, store.ChunkCount, "still one diff left");

            Assert.IsTrue(store.RemoveDiff(ChunkA, 1));
            Assert.AreEqual(0, store.ChunkCount, "empty chunks must not linger");
        }

        [Test]
        public void ChunksAreIndependent()
        {
            TreeDiffStore store = new();
            store.TryApplyDamage(ChunkA, 0, 255, worldTick: 1, out _, out _);

            Assert.IsTrue(store.IsFelled(ChunkA, 0));
            Assert.IsFalse(store.IsFelled(ChunkB, 0), "same index in another chunk is another tree");
        }

        [Test]
        public void SetChunkDiffsReplacesRatherThanMerges()
        {
            // This is what a client does when the server sends a chunk's diffs; stale
            // local state must not survive it.
            TreeDiffStore store = new();
            store.TryApplyDamage(ChunkA, 9, 255, worldTick: 1, out _, out _);

            store.SetChunkDiffs(ChunkA, new List<TreeDiff> { new(4, 100, 0) });

            Assert.AreEqual(1, store.GetDiffs(ChunkA).Count);
            Assert.AreEqual(TreeDiff.FullHealth, store.GetHealth(ChunkA, 9), "old diff should be gone");
            Assert.AreEqual(100, store.GetHealth(ChunkA, 4));
        }

        [Test]
        public void SetChunkDiffsWithNothingClearsTheChunk()
        {
            TreeDiffStore store = new();
            store.TryApplyDamage(ChunkA, 0, 255, worldTick: 1, out _, out _);

            store.SetChunkDiffs(ChunkA, System.Array.Empty<TreeDiff>());

            Assert.AreEqual(0, store.ChunkCount);
            Assert.AreEqual(TreeDiff.FullHealth, store.GetHealth(ChunkA, 0));
        }

        [Test]
        public void ApplyDiffUpdatesInPlace()
        {
            TreeDiffStore store = new();

            store.ApplyDiff(ChunkA, new TreeDiff(7, 200, 0));
            store.ApplyDiff(ChunkA, new TreeDiff(7, 120, 0));

            Assert.AreEqual(1, store.GetDiffs(ChunkA).Count, "same tree must not be added twice");
            Assert.AreEqual(120, store.GetHealth(ChunkA, 7));
        }
    }

    public sealed class ChunkSubscriptionTests
    {
        /// <summary>
        /// A real ClientId is required, not decoration. FishNet's
        /// <c>NetworkConnection.Equals</c> returns false whenever either side has the
        /// unset id of -1 — so a default-constructed connection is not equal to *itself*
        /// and can never be found again once used as a dictionary key. Real connections
        /// always carry an assigned id, so this only bites in tests, but it bites
        /// silently: the map just quietly stops matching.
        /// </summary>
        private static NetworkConnection MakeConnection(int clientId) => new() { ClientId = clientId };

        [Test]
        public void SubscribingReportsOnlyTheNewlyAddedChunks()
        {
            /* Only new chunks need their diffs sent. Resending everything on every move
             * would scale with how much a player walks rather than what they discover. */
            ChunkSubscriptions subs = new();
            NetworkConnection connection = MakeConnection(1);
            List<long> added = new();

            subs.SetSubscriptions(connection, new long[] { 1, 2, 3 }, added);
            CollectionAssert.AreEquivalent(new long[] { 1, 2, 3 }, added);

            subs.SetSubscriptions(connection, new long[] { 2, 3, 4 }, added);
            CollectionAssert.AreEquivalent(new long[] { 4 }, added, "2 and 3 were already held");
        }

        [Test]
        public void ChunksLeftBehindStopReceivingUpdates()
        {
            ChunkSubscriptions subs = new();
            NetworkConnection connection = MakeConnection(1);

            subs.SetSubscriptions(connection, new long[] { 1, 2 }, null);
            Assert.IsTrue(subs.IsSubscribed(connection, 1));

            subs.SetSubscriptions(connection, new long[] { 2 }, null);

            Assert.IsFalse(subs.IsSubscribed(connection, 1), "walking away should unsubscribe");
            Assert.IsTrue(subs.IsSubscribed(connection, 2));
            CollectionAssert.DoesNotContain(subs.SubscribersOf(1), connection);
        }

        [Test]
        public void TwoPlayersCanShareAChunk()
        {
            ChunkSubscriptions subs = new();
            NetworkConnection a = MakeConnection(1);
            NetworkConnection b = MakeConnection(2);

            subs.SetSubscriptions(a, new long[] { 10 }, null);
            subs.SetSubscriptions(b, new long[] { 10 }, null);

            Assert.AreEqual(2, subs.SubscribersOf(10).Count);

            // One leaving must not take the other's subscription with it.
            subs.SetSubscriptions(a, System.Array.Empty<long>(), null);

            Assert.AreEqual(1, subs.SubscribersOf(10).Count);
            CollectionAssert.Contains(subs.SubscribersOf(10), b);
        }

        [Test]
        public void SubscribersOfAnUnknownChunkIsEmptyNotNull()
        {
            ChunkSubscriptions subs = new();

            Assert.IsNotNull(subs.SubscribersOf(12345));
            Assert.AreEqual(0, subs.SubscribersOf(12345).Count);
        }

        [Test]
        public void DisconnectingForgetsEverything()
        {
            // Without this the map grows for the lifetime of the server.
            ChunkSubscriptions subs = new();
            NetworkConnection connection = MakeConnection(1);

            subs.SetSubscriptions(connection, new long[] { 1, 2, 3 }, null);
            Assert.AreEqual(3, subs.ChunkCount);

            subs.RemoveConnection(connection);

            Assert.AreEqual(0, subs.ChunkCount, "empty chunk entries should be dropped too");
            Assert.IsFalse(subs.IsSubscribed(connection, 1));
        }
    }
}
