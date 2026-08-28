using ChopChop.World;
using NUnit.Framework;
using UnityEngine;

namespace ChopChop.Tests.Editor
{
    /// <summary>
    /// The chop meter is graded on the server from the tick the client claims, so client
    /// and server must compute the identical pointer position from the identical inputs.
    /// If that ever stops being true, the game silently starts refusing swings that
    /// looked perfect — which reads as lag, not as a bug, and would be very hard to find.
    /// </summary>
    public sealed class ChopMeterTests
    {
        private const uint TickRate = 30;

        private static readonly TreeId Tree = new(1234567L, 42);

        /// <summary>Perfect within 0.1 of the middle, Good within 0.3, Bad beyond.</summary>
        private static readonly ChopMeterSettings Settings = new(1f, 0.1f, 0.3f);

        // ---- phase ----------------------------------------------------------

        [Test]
        public void PhaseIsDeterministicForTheSameTick()
        {
            // This is the whole authority argument: two machines, same answer.
            for (uint tick = 0; tick < 200; tick++)
                Assert.AreEqual(
                    ChopMeter.Phase(tick, TickRate, Tree, Settings),
                    ChopMeter.Phase(tick, TickRate, Tree, Settings),
                    "tick " + tick);
        }

        [Test]
        public void PhaseStaysWithinTheArc()
        {
            for (uint tick = 0; tick < 2000; tick++)
            {
                float phase = ChopMeter.Phase(tick, TickRate, Tree, Settings);
                Assert.GreaterOrEqual(phase, -1.0001f, "tick " + tick);
                Assert.LessOrEqual(phase, 1.0001f, "tick " + tick);
            }
        }

        [Test]
        public void PhaseIsContinuous()
        {
            /* A triangle wave, so consecutive ticks may differ by at most the distance
             * the pointer travels in one tick. A jump means the pointer teleports, and a
             * player cannot time what teleports. */
            float perTick = 4f * Settings.CyclesPerSecond / TickRate;
            float previous = ChopMeter.Phase(0, TickRate, Tree, Settings);

            for (uint tick = 1; tick < 500; tick++)
            {
                float phase = ChopMeter.Phase(tick, TickRate, Tree, Settings);
                Assert.LessOrEqual(Mathf.Abs(phase - previous), perTick + 0.0001f, "tick " + tick);
                previous = phase;
            }
        }

        [Test]
        public void DifferentTreesAreOutOfStep()
        {
            // Otherwise the forest pulses in unison and one learned rhythm fells anything.
            float a = ChopMeter.OffsetOf(new TreeId(1L, 0));
            float b = ChopMeter.OffsetOf(new TreeId(1L, 1));
            float c = ChopMeter.OffsetOf(new TreeId(2L, 0));

            Assert.AreNotEqual(a, b, "same chunk, different tree");
            Assert.AreNotEqual(a, c, "same index, different chunk");
        }

        [Test]
        public void OffsetIsWithinOneCycle()
        {
            for (ushort i = 0; i < 64; i++)
            {
                float offset = ChopMeter.OffsetOf(new TreeId(-9876543210L, i));
                Assert.GreaterOrEqual(offset, 0f);
                Assert.Less(offset, 1f);
            }
        }

        [Test]
        public void ZeroTickRateDoesNotDivideByZero()
        {
            // TimeManager reports 0 before the connection is up, and the meter is drawn
            // from the first frame the axe is out.
            Assert.AreEqual(0f, ChopMeter.Phase(10, 0, Tree, Settings));
        }

        // ---- sweep speed ----------------------------------------------------

        [Test]
        public void ATierOneTreeTakesTheBaseSweepToCross()
        {
            // The number a tuner actually reasons about: how long you wait between
            // chances to strike.
            ChopMeterSettings settings = ChopMeterSettings.For(axeTier: 1, treeTier: 1);
            Assert.AreEqual(ChopMeter.BaseSweepSeconds, settings.SweepSeconds, 0.0001f);
            Assert.AreEqual(1.2f, settings.SweepSeconds, 0.0001f, "slowed deliberately; see TECH 5.6a");
        }

        [Test]
        public void AHarderTreeSweepsFaster()
        {
            Assert.Less(
                ChopMeterSettings.For(1, 3).SweepSeconds,
                ChopMeterSettings.For(1, 1).SweepSeconds);
        }

        [Test]
        public void ThePerfectWindowIsLongEnoughToHitOnPurpose()
        {
            /* Not a style point. Below roughly a tenth of a second a human cannot aim at
             * a moving mark, only gamble at it, and the whole feature becomes a slot
             * machine. If a tuning change trips this, the change is wrong. */
            float window = ChopMeterSettings.For(axeTier: 1, treeTier: 1).PerfectWindowSeconds;
            Assert.GreaterOrEqual(window, 0.1f, "perfect window in seconds");
        }

        // ---- grading --------------------------------------------------------

        [Test]
        public void TheWrongHalfIsAMiss()
        {
            Assert.AreEqual(ChopGrade.Miss, ChopMeter.Grade(-0.5f, topHalfActive: true, Settings));
            Assert.AreEqual(ChopGrade.Miss, ChopMeter.Grade(0.5f, topHalfActive: false, Settings));
        }

        [Test]
        public void PerfectSitsInTheMiddleOfTheActiveHalf()
        {
            // Moved from the tip: the middle leaves a margin either side rather than one.
            Assert.AreEqual(ChopGrade.Perfect, ChopMeter.Grade(0.5f, topHalfActive: true, Settings));
            Assert.AreEqual(ChopGrade.Perfect, ChopMeter.Grade(-0.5f, topHalfActive: false, Settings));
        }

        [Test]
        public void TheTipsOfTheArcAreBad()
        {
            // The old layout graded these Perfect. Anything still assuming that is stale.
            Assert.AreEqual(ChopGrade.Bad, ChopMeter.Grade(1f, topHalfActive: true, Settings));
            Assert.AreEqual(ChopGrade.Bad, ChopMeter.Grade(-1f, topHalfActive: false, Settings));
        }

        [Test]
        public void TheActiveHalfReadsBadGoodPerfectGoodBad()
        {
            /* Walking the half end to end must produce exactly those five runs, in that
             * order. This is the layout as a player sees it, asserted as a shape rather
             * than as five separate boundary checks. */
            var runs = new System.Collections.Generic.List<ChopGrade>();

            for (int i = 0; i <= 1000; i++)
            {
                ChopGrade grade = ChopMeter.Grade(i / 1000f, topHalfActive: true, Settings);

                if (runs.Count == 0 || runs[runs.Count - 1] != grade)
                    runs.Add(grade);
            }

            Assert.AreEqual(
                new[] { ChopGrade.Bad, ChopGrade.Good, ChopGrade.Perfect, ChopGrade.Good, ChopGrade.Bad },
                runs.ToArray());
        }

        [Test]
        public void BandsAreSymmetricAboutTheMiddle()
        {
            for (int i = 0; i <= 50; i++)
            {
                float offset = i / 100f;
                Assert.AreEqual(
                    ChopMeter.Grade(0.5f + offset, true, Settings),
                    ChopMeter.Grade(0.5f - offset, true, Settings),
                    "offset " + offset);
            }
        }

        [Test]
        public void BandBoundariesAreInclusive()
        {
            // A swing exactly on the line must not fall through to the worse grade.
            Assert.AreEqual(ChopGrade.Perfect, ChopMeter.Grade(0.5f + Settings.PerfectHalfWidth, true, Settings));
            Assert.AreEqual(ChopGrade.Good, ChopMeter.Grade(0.5f + Settings.GoodHalfWidth, true, Settings));
        }

        [Test]
        public void AWiderAxeGradesMoreGenerously()
        {
            ChopMeterSettings stone = ChopMeterSettings.For(axeTier: 1, treeTier: 1);
            ChopMeterSettings iron = ChopMeterSettings.For(axeTier: 2, treeTier: 1);

            Assert.Greater(iron.PerfectHalfWidth, stone.PerfectHalfWidth);
            Assert.Greater(iron.GoodHalfWidth, stone.GoodHalfWidth);
        }

        [Test]
        public void BadSurvivesAtEveryAxeTier()
        {
            /* Good must never swallow the whole half, or the two Bad bands vanish and the
             * five-band layout quietly becomes three. */
            for (byte axe = 0; axe < 12; axe++)
            {
                ChopMeterSettings settings = ChopMeterSettings.For(axe, 1);
                Assert.Greater(settings.GoodHalfWidth, settings.PerfectHalfWidth, "axe tier " + axe);
                Assert.Less(settings.GoodHalfWidth, 0.5f, "axe tier " + axe);
            }
        }

        // ---- alternation ----------------------------------------------------

        [Test]
        public void EveryGradeAdvancesTheWedgeByAnOddNumberOfSteps()
        {
            /* This is what makes the side swap on *any* landed hit. If a grade ever took
             * an even number of steps, the wedge would stop alternating for that grade
             * and the meter would start grading against the wrong half — silently, and
             * only for players who happened to hit that grade. */
            foreach (ChopGrade grade in new[] { ChopGrade.Bad, ChopGrade.Good, ChopGrade.Perfect })
                Assert.AreEqual(1, ChopMeter.StepsFor(grade) & 1, grade + " must take an odd number of steps");

            Assert.AreEqual(0, ChopMeter.StepsFor(ChopGrade.Miss), "a miss takes nothing");
        }

        [Test]
        public void TheSideSwapsOnAnyLandedHitAndHoldsOnAMiss()
        {
            /* Only while the trunk is still standing. FullHealth is 255, which is not a
             * whole number of wedge steps, so the felling blow clamps to zero and its
             * step count is short by the remainder. That is unobservable: a felled tree
             * has no next swing and no meter, and every intermediate health is exactly
             * FullHealth minus a whole number of steps. */
            foreach (ChopGrade grade in new[] { ChopGrade.Bad, ChopGrade.Good, ChopGrade.Perfect })
            {
                int health = TreeDiff.FullHealth;
                bool side = ChopMeter.TopHalfActive((byte)health);

                for (int hit = 1; hit <= 3; hit++)
                {
                    int next = health - ChopMeter.DamageFor(grade);

                    if (next <= 0)
                        break;

                    health = next;
                    bool side2 = ChopMeter.TopHalfActive((byte)health);

                    Assert.AreNotEqual(side, side2, grade + " hit " + hit + " should have swapped the side");
                    side = side2;
                }
            }
        }

        [Test]
        public void AMissLeavesTheSideAlone()
        {
            // A miss takes nothing off the trunk, so the health the side is derived from
            // has not moved. Holding is the point: a whiff must not cost your place.
            byte health = 255;
            bool before = ChopMeter.TopHalfActive(health);

            health -= ChopMeter.DamageFor(ChopGrade.Miss);

            Assert.AreEqual(255, health, "a miss must deal no damage");
            Assert.AreEqual(before, ChopMeter.TopHalfActive(health));
        }

        [Test]
        public void MixedGradesStillAlternate()
        {
            // The property has to hold for a real sequence, not just a uniform one.
            ChopGrade[] sequence =
            {
                ChopGrade.Bad, ChopGrade.Perfect, ChopGrade.Good, ChopGrade.Bad, ChopGrade.Bad,
            };

            int health = TreeDiff.FullHealth;
            bool side = ChopMeter.TopHalfActive((byte)health);
            int landed = 0;

            // Standing trunk only, for the reason given above.
            for (int i = 0; i < sequence.Length; i++)
            {
                int next = health - ChopMeter.DamageFor(sequence[i]);

                if (next <= 0)
                    break;

                health = next;
                landed++;

                bool side2 = ChopMeter.TopHalfActive((byte)health);
                Assert.AreNotEqual(side, side2, "after " + sequence[i]);
                side = side2;
            }

            Assert.GreaterOrEqual(landed, 4, "the sequence should exercise several hits before felling");
        }

        [Test]
        public void ClientAndServerAgreeOnTheActiveHalfForEveryHealth()
        {
            /* The client draws the bands and the server grades against them. Both call
             * this one function, so the test is that it is total: every health value has
             * an answer and it never throws. */
            for (int health = 0; health <= 255; health++)
                Assert.DoesNotThrow(() => ChopMeter.TopHalfActive((byte)health), "health " + health);
        }

        // ---- damage ---------------------------------------------------------

        [Test]
        public void DamageFollowsTheGrade()
        {
            Assert.AreEqual(0, ChopMeter.DamageFor(ChopGrade.Miss));
            Assert.Less(ChopMeter.DamageFor(ChopGrade.Bad), ChopMeter.DamageFor(ChopGrade.Good));
            Assert.Less(ChopMeter.DamageFor(ChopGrade.Good), ChopMeter.DamageFor(ChopGrade.Perfect));
        }

        [Test]
        public void DamageIsAlwaysAWholeNumberOfWedgeSteps()
        {
            // TopHalfActive divides by the step, so a remainder would desynchronise the
            // step count from the damage actually dealt.
            foreach (ChopGrade grade in new[] { ChopGrade.Miss, ChopGrade.Bad, ChopGrade.Good, ChopGrade.Perfect })
                Assert.AreEqual(0, ChopMeter.DamageFor(grade) % ChopMeter.WedgeStep, grade.ToString());
        }

        [Test]
        public void PlayingWellFellsATreeInFewerSwings()
        {
            Assert.AreEqual(3, SwingsToFell(ChopGrade.Perfect), "perfect");
            Assert.AreEqual(4, SwingsToFell(ChopGrade.Good), "good");
            Assert.AreEqual(11, SwingsToFell(ChopGrade.Bad), "bad");
        }

        private static int SwingsToFell(ChopGrade grade)
        {
            int health = TreeDiff.FullHealth;
            int swings = 0;

            while (health > 0 && swings < 100)
            {
                health -= ChopMeter.DamageFor(grade);
                swings++;
            }

            return swings;
        }
    }
}
