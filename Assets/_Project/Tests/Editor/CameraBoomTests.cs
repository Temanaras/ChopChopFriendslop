using ChopChop.Player;
using NUnit.Framework;

namespace ChopChop.Tests.Editor
{
    /// <summary>
    /// The third-person boom used to pass straight through the cabin walls, so a joining
    /// player's first frame was the inside of one. The fix has to be asymmetric — instant
    /// on the way in, smoothed on the way out — and it is the instant half that is easy to
    /// lose to a well-meaning tidy-up, because a single lerp reads as "smoother" right up
    /// until it puts the camera inside geometry for three frames and turns the wall into a
    /// hole. These tests exist to make that regression fail loudly.
    /// </summary>
    public sealed class CameraBoomTests
    {
        private const float Min = 0.6f;
        private const float Max = 3f;
        private const float ReturnSpeed = 6f;
        private const float Frame = 1f / 60f;

        private static float Resolve(bool blocked, float hitDistance, float current) =>
            PlayerCameraRig.ResolveBoom(blocked, hitDistance, current, Min, Max, ReturnSpeed, Frame);

        [Test]
        public void RestsAtFullLengthWithNothingInTheWay()
        {
            Assert.AreEqual(Max, Resolve(false, Max, Max), 1e-4f);
        }

        /// <summary>
        /// The one that matters. Anything other than an exact match here means frames
        /// spent inside the wall.
        /// </summary>
        [Test]
        public void ObstructionPullsInOnTheSameFrame()
        {
            Assert.AreEqual(1f, Resolve(true, 1f, Max), 1e-4f);
        }

        [Test]
        public void ClearingEasesOutRatherThanSnapping()
        {
            float next = Resolve(false, Max, 1f);

            Assert.AreEqual(1f + ReturnSpeed * Frame, next, 1e-4f);
            Assert.Less(next, Max, "Returning to full length must take more than one frame.");
        }

        [Test]
        public void EasingOutArrivesAndStops()
        {
            float boom = 1f;

            for (int i = 0; i < 240; i++)
                boom = Resolve(false, Max, boom);

            Assert.AreEqual(Max, boom, 1e-4f, "The boom must reach full length and stay there.");
        }

        [Test]
        public void NeverRetreatsPastTheMinimum()
        {
            Assert.AreEqual(Min, Resolve(true, 0.05f, Max), 1e-4f);
        }

        [Test]
        public void NeverExtendsPastTheAuthoredLength()
        {
            Assert.AreEqual(Max, Resolve(true, 99f, Max), 1e-4f);
        }

        /// <summary>
        /// A spherecast that starts already overlapping a collider reports distance zero,
        /// which is the case the camera hits when the player backs flat against a wall.
        /// Treating it as "no obstruction" would put the camera through the wall at exactly
        /// the moment it matters most.
        /// </summary>
        [Test]
        public void FullyOccludedMeansMinimumNotZero()
        {
            Assert.AreEqual(Min, Resolve(true, 0f, Max), 1e-4f);
        }

        /// <summary>
        /// An obstruction further out than the camera currently sits is still an
        /// obstruction, but moving toward it is moving outward — so it eases rather than
        /// jumping, or stepping out of a doorway would lurch.
        /// </summary>
        [Test]
        public void RecedingObstructionEasesOutToo()
        {
            float next = Resolve(true, 2.5f, 1f);

            Assert.AreEqual(1f + ReturnSpeed * Frame, next, 1e-4f);
        }
    }
}
