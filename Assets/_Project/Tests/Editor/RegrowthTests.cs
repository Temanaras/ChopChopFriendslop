using ChopChop.Biomes;
using ChopChop.World;
using NUnit.Framework;
using UnityEngine;

namespace ChopChop.Tests.Editor
{
    /// <summary>
    /// Regrowth is the balance answer to clear-cutting and a horror mechanic at once
    /// (TECH 7). It is also the easiest system here to get quietly wrong: too eager and
    /// roads are not worth building, too slow and the map ends up permanently stripped.
    /// </summary>
    public sealed class RegrowthTests
    {
        private const long ChunkNearOrigin = 0;

        private TreeDiffStore _diffs;
        private RegrowthService _regrowth;

        /// <summary>One tree reclaimed per 100 ticks, which keeps the arithmetic obvious.</summary>
        private const float Rate = 0.01f;

        [SetUp]
        public void SetUp()
        {
            BiomeDefinition biome = ScriptableObject.CreateInstance<BiomeDefinition>();
            biome.RingIndex = 0;
            biome.InnerRadius = 0f;
            biome.RegrowthRatePerTick = Rate;

            BiomeSet set = ScriptableObject.CreateInstance<BiomeSet>();
            var so = new UnityEditor.SerializedObject(set);
            var array = so.FindProperty("_biomes");
            array.arraySize = 1;
            array.GetArrayElementAtIndex(0).objectReferenceValue = biome;
            so.ApplyModifiedPropertiesWithoutUndo();

            _diffs = new TreeDiffStore();
            _regrowth = new RegrowthService(_diffs, set);
        }

        private void Fell(ushort index, uint atTick)
        {
            _diffs.TryApplyDamage(ChunkNearOrigin, index, 255, atTick, out _, out _);
        }

        [Test]
        public void NothingReclaimsWithoutTimePassing()
        {
            Fell(0, 100);
            _diffs.SetLastVisitedTick(ChunkNearOrigin, 100);

            Assert.AreEqual(0, _regrowth.Evaluate(ChunkNearOrigin, 100));
            Assert.IsTrue(_diffs.IsFelled(ChunkNearOrigin, 0));
        }

        [Test]
        public void TimeAwayReclaimsProportionally()
        {
            for (ushort i = 0; i < 5; i++)
                Fell(i, 0);

            _diffs.SetLastVisitedTick(ChunkNearOrigin, 0);

            // 300 ticks at 0.01 per tick is a budget of 3.
            Assert.AreEqual(3, _regrowth.Evaluate(ChunkNearOrigin, 300));
            Assert.AreEqual(2, _diffs.GetDiffs(ChunkNearOrigin).Count, "two should still be down");
        }

        [Test]
        public void OldestFelledComesBackFirst()
        {
            /* So a road stays a road at the end you are working while its far end closes
             * over. Reclaiming newest-first would eat the path in front of you. */
            Fell(10, 50);    // oldest
            Fell(11, 900);
            Fell(12, 400);

            _diffs.SetLastVisitedTick(ChunkNearOrigin, 900);
            _regrowth.Evaluate(ChunkNearOrigin, 1000);

            Assert.AreEqual(TreeDiff.FullHealth, _diffs.GetHealth(ChunkNearOrigin, 10), "oldest should be back");
            Assert.IsTrue(_diffs.IsFelled(ChunkNearOrigin, 11), "newest should still be down");
            Assert.IsTrue(_diffs.IsFelled(ChunkNearOrigin, 12));
        }

        [Test]
        public void ReclaimIsCappedPerEvaluation()
        {
            /* Without the cap a chunk left for a month snaps back to pristine the moment
             * someone returns, which reads as arbitrary rather than eerie (TECH 7.3). */
            for (ushort i = 0; i < 20; i++)
                Fell(i, 0);

            _diffs.SetLastVisitedTick(ChunkNearOrigin, 0);
            _regrowth.MaxReclaimedPerEvaluation = 6;

            // A million ticks of budget; the cap is what should decide the outcome.
            Assert.AreEqual(6, _regrowth.Evaluate(ChunkNearOrigin, 1_000_000));
            Assert.AreEqual(14, _diffs.GetDiffs(ChunkNearOrigin).Count);
        }

        [Test]
        public void HoldingGroundStopsRegrowthEntirely()
        {
            // The occupancy rule: while anyone is subscribed the clock keeps pace, so the
            // gap never grows and nothing reclaims (TECH 7.1).
            for (ushort i = 0; i < 5; i++)
                Fell(i, 0);

            for (uint tick = 0; tick <= 5000; tick += 10)
                _regrowth.MarkOccupied(ChunkNearOrigin, tick);

            Assert.AreEqual(0, _regrowth.Evaluate(ChunkNearOrigin, 5000),
                "ground under a player must never reclaim");
            Assert.AreEqual(5, _diffs.GetDiffs(ChunkNearOrigin).Count);
        }

        [Test]
        public void FullyReclaimedChunkCostsNothingAgain()
        {
            // The point of regrowth for a persistent world: an old chunk shrinks back out
            // of the save rather than only ever growing.
            Fell(0, 0);
            Fell(1, 0);
            _diffs.SetLastVisitedTick(ChunkNearOrigin, 0);

            _regrowth.Evaluate(ChunkNearOrigin, 100_000);

            Assert.AreEqual(0, _diffs.ChunkCount, "chunk entry should be gone entirely");
            Assert.AreEqual(TreeDiff.FullHealth, _diffs.GetHealth(ChunkNearOrigin, 0));
        }

        [Test]
        public void EvaluationStampsTheClockSoItDoesNotDoubleCount()
        {
            for (ushort i = 0; i < 10; i++)
                Fell(i, 0);

            _diffs.SetLastVisitedTick(ChunkNearOrigin, 0);

            int first = _regrowth.Evaluate(ChunkNearOrigin, 300);
            int immediatelyAgain = _regrowth.Evaluate(ChunkNearOrigin, 300);

            Assert.AreEqual(3, first);
            Assert.AreEqual(0, immediatelyAgain, "re-evaluating at the same tick must reclaim nothing");
        }

        [Test]
        public void ClockGoingBackwardsDoesNotReclaimEverything()
        {
            /* A restored save or a reset world tick. Treating a negative gap as enormous
             * elapsed time would wipe every diff in the chunk at once. */
            Fell(0, 500);
            Fell(1, 500);
            _diffs.SetLastVisitedTick(ChunkNearOrigin, 5000);

            Assert.AreEqual(0, _regrowth.Evaluate(ChunkNearOrigin, 100));
            Assert.AreEqual(2, _diffs.GetDiffs(ChunkNearOrigin).Count);
        }

        [Test]
        public void DamagedTreesHealBeforeStumpsReturn()
        {
            // Damaged-but-standing diffs carry a felled tick of zero, so they sort first.
            // Scratches closing before stumps regrow is the reading we want.
            _diffs.TryApplyDamage(ChunkNearOrigin, 5, 50, 800, out _, out _);  // damaged
            Fell(6, 100);                                                       // felled earlier

            _diffs.SetLastVisitedTick(ChunkNearOrigin, 800);
            _regrowth.Evaluate(ChunkNearOrigin, 900);

            Assert.AreEqual(TreeDiff.FullHealth, _diffs.GetHealth(ChunkNearOrigin, 5), "damage should heal first");
            Assert.IsTrue(_diffs.IsFelled(ChunkNearOrigin, 6));
        }

        [Test]
        public void UntouchedChunksAreFreeToEvaluate()
        {
            // Regrowth is never ticked; evaluating a chunk nobody has touched must do no
            // work at all, however long it has been (TECH 7.1).
            Assert.AreEqual(0, _regrowth.Evaluate(12345, 10_000_000));
            Assert.AreEqual(0, _diffs.ChunkCount);
        }
    }
}
