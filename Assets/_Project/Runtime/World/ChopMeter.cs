using UnityEngine;

namespace ChopChop.World
{
    /// <summary>How well a swing was timed. Wire format — append, never renumber.</summary>
    public enum ChopGrade : byte
    {
        /// <summary>Struck the wrong half of the arc. No damage, and the swing stumbles.</summary>
        Miss = 0,
        Bad = 1,
        Good = 2,
        Perfect = 3,
    }

    /// <summary>
    /// How wide the bands are and how long the pointer takes to cross. Derived from the
    /// axe and the tree, so a better axe is a wider target and a harder tree is a faster
    /// one.
    /// </summary>
    public readonly struct ChopMeterSettings
    {
        /// <summary>Full sweeps of the arc per second. Higher is harder.</summary>
        public readonly float CyclesPerSecond;

        /// <summary>
        /// Half-width of the Perfect band, in phase units either side of the middle of
        /// the active half.
        /// </summary>
        public readonly float PerfectHalfWidth;

        /// <summary>Half-width of the Good band. Must exceed <see cref="PerfectHalfWidth"/>.</summary>
        public readonly float GoodHalfWidth;

        public ChopMeterSettings(float cyclesPerSecond, float perfectHalfWidth, float goodHalfWidth)
        {
            CyclesPerSecond = cyclesPerSecond;
            PerfectHalfWidth = perfectHalfWidth;
            GoodHalfWidth = goodHalfWidth;
        }

        /// <summary>
        /// Seconds for the pointer to cross the arc once, end to end. This is the number
        /// worth thinking in: it is how long a player waits between chances to strike.
        /// One crossing is half a cycle, hence the halving.
        /// </summary>
        public float SweepSeconds => CyclesPerSecond <= 0f ? 0f : 0.5f / CyclesPerSecond;

        /// <summary>
        /// Seconds the pointer spends inside the Perfect band. Useful for judging whether
        /// a tuning change is playable rather than merely different.
        /// </summary>
        public float PerfectWindowSeconds => SweepSeconds * PerfectHalfWidth;

        /// <summary>
        /// The settings for a given pairing. A tier-3 tree swept at the tier-1 rate would
        /// be trivial, and a tier-1 tree swept at the tier-3 rate would be miserable, so
        /// both ends move.
        /// </summary>
        public static ChopMeterSettings For(byte axeTier, byte treeTier)
        {
            float sweep = ChopMeter.BaseSweepSeconds
                          / (1f + ChopMeter.TierSpeedup * Mathf.Max(0, treeTier - 1));

            /* Deliberately gentle at the bottom. The first tree a new player swings at is
             * also the first time they see this meter, and a wall there teaches them the
             * game is unfair rather than that timing matters. */
            float perfect = Mathf.Clamp(0.07f + 0.02f * axeTier, 0.04f, 0.20f);
            float good = Mathf.Clamp(0.18f + 0.04f * axeTier, perfect + 0.04f, 0.42f);

            return new ChopMeterSettings(0.5f / sweep, perfect, good);
        }
    }

    /// <summary>
    /// The chopping timing game (DESIGN 2, TECH 5.6a).
    ///
    /// A pointer travels up and down the curved edge of a semicircle. One half of the arc
    /// is the target and the other is a miss. The target half carries five bands, laid
    /// out symmetrically about its middle:
    ///
    /// <code>
    ///     Bad | Good | PERFECT | Good | Bad
    /// </code>
    ///
    /// **The target half swaps on every landed hit and holds on a miss**, so the wedge is
    /// opened from alternating sides the way a real one is, and a whiffed swing does not
    /// cost you your place in the rhythm.
    ///
    /// **Pure, and deterministic from the tick.** This is the whole reason the feature
    /// does not weaken TECH 2.1: the client does not tell the server how well it did. The
    /// pointer's position is a function of the synced network tick and the tree's id, so
    /// the client draws it and the server independently recomputes the same value for the
    /// tick the swing claims. Nothing new is trusted — a client can lie about *when* it
    /// swung, and <see cref="TreeServer"/> bounds that against its own clock.
    ///
    /// No Unity types beyond <see cref="Mathf"/>, no state, no time source of its own.
    /// </summary>
    public static class ChopMeter
    {
        /// <summary>Seconds for one crossing of the arc against a tier-1 tree.</summary>
        public const float BaseSweepSeconds = 1.2f;

        /// <summary>How much faster each tier above the first sweeps.</summary>
        public const float TierSpeedup = 0.22f;

        /// <summary>
        /// The unit trunk damage is counted in, and the unit the wedge alternates on.
        ///
        /// Owned here rather than by <see cref="TreeServer"/> because both sides need it:
        /// the server to apply damage, the client to know which half of the arc is live.
        /// One number, so the two cannot drift apart and start disagreeing about which
        /// swings are misses.
        /// </summary>
        public const byte WedgeStep = 24;

        /* Steps each grade takes out of the trunk.
         *
         * **Every one of these must be ODD.** The live half is derived from how many
         * steps have been cut (see TopHalfActive), and adding an odd number of steps is
         * what flips it — which is the rule: swap sides on any landed hit, hold on a
         * miss. An even entry here would silently stop the wedge alternating for that
         * grade, and the meter would start grading against the wrong half. Pinned by
         * EveryGradeAdvancesTheWedgeByAnOddNumberOfSteps in ChopMeterTests. */
        private const int BadSteps = 1;
        private const int GoodSteps = 3;
        private const int PerfectSteps = 5;

        /// <summary>The middle of the active half, where Perfect sits.</summary>
        private const float Centre = 0.5f;

        /// <summary>
        /// Slack on band comparisons, so a boundary is inclusive from both sides despite
        /// float rounding. See <see cref="Grade"/>.
        /// </summary>
        private const float Edge = 1e-4f;

        /// <summary>
        /// Where the pointer is, from -1 (bottom of the arc) through +1 (top) and back.
        ///
        /// A triangle wave rather than a sine: a sine lingers at the extremes and hurries
        /// through the middle, and the middle is exactly where the Perfect band now sits.
        /// A constant speed is also the only honest one to read off a moving pointer.
        /// </summary>
        public static float Phase(uint tick, uint tickRate, TreeId id, in ChopMeterSettings settings)
        {
            if (tickRate == 0)
                return 0f;

            /* Offset per tree so the whole forest does not pulse in lockstep, which would
             * let a player learn one rhythm and apply it everywhere without looking. The
             * offset is derived from the id, so both sides agree without sending it. */
            float offset = OffsetOf(id);

            float seconds = tick / (float)tickRate;
            float cycle = Mathf.Repeat(seconds * settings.CyclesPerSecond + offset, 1f);

            // 0 -> 1 -> 0 becomes -1 -> +1 -> -1.
            return 1f - 4f * Mathf.Abs(cycle - 0.5f);
        }

        /// <summary>
        /// Stable per-tree phase offset in [0,1). Uses the same FNV-1a the world generator
        /// hashes with, so it is deterministic across machines and platforms (TECH 2.6).
        /// </summary>
        public static float OffsetOf(TreeId id)
        {
            uint hash = Core.Fnv1a.Hash(id.ChunkKey, id.LocalIndex);
            return (hash & 0xFFFF) / 65536f;
        }

        /// <summary>
        /// Which half of the arc is the target. Swaps on every landed hit — Bad, Good or
        /// Perfect alike — and holds on a miss, because a miss takes nothing off the
        /// trunk.
        ///
        /// Derived from the tree's remaining health rather than tracked per player, and
        /// that is load-bearing: health is already replicated to everyone subscribed to
        /// the chunk, so this costs no bytes, needs no save-format change, is correct for
        /// a client that joined halfway through felling the tree, and gives two players
        /// on the same trunk the same answer.
        ///
        /// It works because every grade takes an *odd* number of <see cref="WedgeStep"/>s
        /// out of the trunk, so the parity of the step count flips on each hit whatever
        /// the grade was.
        /// </summary>
        public static bool TopHalfActive(byte healthRemaining)
        {
            int steps = (TreeDiff.FullHealth - healthRemaining) / WedgeStep;
            return (steps & 1) == 0;
        }

        /// <summary>
        /// Grades a swing taken at <paramref name="phase"/>.
        ///
        /// Perfect sits at the *middle* of the active half, with Good either side of it
        /// and Bad outside those — five bands in all. The middle leaves a margin of error
        /// on both sides rather than only one, and it is where a moving pointer is
        /// easiest to read against a fixed mark.
        /// </summary>
        public static ChopGrade Grade(float phase, bool topHalfActive, in ChopMeterSettings settings)
        {
            // Wrong half of the arc entirely. This is the stumble.
            if (topHalfActive ? phase < 0f : phase > 0f)
                return ChopGrade.Miss;

            float depth = Mathf.Abs(phase);
            float distance = Mathf.Abs(depth - Centre);

            /* Compared with a slack of one Edge, because `0.5f + 0.1f` and `0.5f - 0.1f`
             * are not the same distance from 0.5 in float arithmetic — one lands a ULP
             * inside the band and the other a ULP outside. Without this the bands are
             * very slightly asymmetric, which is not a desync (both sides compute it the
             * same way) but is a real edge a player could sit on and get different
             * answers from either side of the middle. The slack is a ten-thousandth of a
             * sweep: tens of microseconds, and far below a frame. */
            if (distance <= settings.PerfectHalfWidth + Edge)
                return ChopGrade.Perfect;

            return distance <= settings.GoodHalfWidth + Edge ? ChopGrade.Good : ChopGrade.Bad;
        }

        /// <summary>
        /// Damage for a grade. A trunk starts at <see cref="TreeDiff.FullHealth"/>, so it
        /// falls in three swings played perfectly, four played well, and eleven scraped.
        /// </summary>
        public static byte DamageFor(ChopGrade grade) => (byte)Mathf.Min(StepsFor(grade) * WedgeStep, 255);

        /// <summary>
        /// How many wedge steps a grade takes. Exposed so a test can assert the oddness
        /// the alternation depends on, rather than that rule living only in a comment.
        /// </summary>
        public static int StepsFor(ChopGrade grade) => grade switch
        {
            ChopGrade.Perfect => PerfectSteps,
            ChopGrade.Good => GoodSteps,
            ChopGrade.Bad => BadSteps,
            _ => 0,
        };
    }
}
