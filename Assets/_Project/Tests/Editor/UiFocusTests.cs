using System;
using ChopChop.Core;
using NUnit.Framework;

namespace ChopChop.Tests.Editor
{
    /// <summary>
    /// <see cref="UiFocus"/> is the only thing standing between an open inventory and the
    /// player still chopping, moving and firing behind it. It is also a static, so a
    /// leaked capture does not clear itself — the failure mode is a game that never gives
    /// input back, with nothing on screen to explain why.
    /// </summary>
    public sealed class UiFocusTests
    {
        [SetUp]
        [TearDown]
        public void Reset() => UiFocus.Clear();

        [Test]
        public void NothingHeldMeansGameplayRuns()
        {
            Assert.IsFalse(UiFocus.GameplayBlocked);
            Assert.IsFalse(UiFocus.WantsCursor);
        }

        [Test]
        public void ACaptureBlocksGameplay()
        {
            using (UiFocus.Capture())
            {
                Assert.IsTrue(UiFocus.GameplayBlocked);
                Assert.IsTrue(UiFocus.WantsCursor);
            }

            Assert.IsFalse(UiFocus.GameplayBlocked);
        }

        [Test]
        public void CapturesStack()
        {
            // The chest opens over the inventory; closing the inventory first must not
            // hand movement back while the chest is still up.
            IDisposable inventory = UiFocus.Capture();
            IDisposable chest = UiFocus.Capture();

            inventory.Dispose();
            Assert.IsTrue(UiFocus.GameplayBlocked, "still one screen open");

            chest.Dispose();
            Assert.IsFalse(UiFocus.GameplayBlocked);
        }

        [Test]
        public void DisposeIsIdempotent()
        {
            // A screen torn down twice (OnDisable then OnDestroy, say) must not decrement
            // somebody else's capture.
            IDisposable a = UiFocus.Capture();
            IDisposable b = UiFocus.Capture();

            a.Dispose();
            a.Dispose();
            a.Dispose();

            Assert.IsTrue(UiFocus.GameplayBlocked, "b is still holding it");
            b.Dispose();
            Assert.IsFalse(UiFocus.GameplayBlocked);
        }

        [Test]
        public void CursorIsWantedIfAnyHolderWantsIt()
        {
            IDisposable fade = UiFocus.Capture(cursor: false);
            Assert.IsTrue(UiFocus.GameplayBlocked);
            Assert.IsFalse(UiFocus.WantsCursor);

            IDisposable panel = UiFocus.Capture(cursor: true);
            Assert.IsTrue(UiFocus.WantsCursor);

            panel.Dispose();
            Assert.IsFalse(UiFocus.WantsCursor, "the only holder that wanted it has gone");
            Assert.IsTrue(UiFocus.GameplayBlocked);

            fade.Dispose();
        }

        [Test]
        public void ChangedFiresOnTransitionsOnly()
        {
            int raised = 0;
            Action handler = () => raised++;
            UiFocus.Changed += handler;

            try
            {
                IDisposable a = UiFocus.Capture();
                Assert.AreEqual(1, raised, "false -> true");

                IDisposable b = UiFocus.Capture();
                Assert.AreEqual(1, raised, "a second capture changes neither flag");

                a.Dispose();
                Assert.AreEqual(1, raised, "still blocked, still wanting a cursor");

                b.Dispose();
                Assert.AreEqual(2, raised, "true -> false");
            }
            finally
            {
                UiFocus.Changed -= handler;
            }
        }

        [Test]
        public void ClearDropsEverything()
        {
            IDisposable a = UiFocus.Capture();
            UiFocus.Capture();
            UiFocus.Capture();

            UiFocus.Clear();
            Assert.IsFalse(UiFocus.GameplayBlocked);

            // A token from before the clear must not re-block anything on disposal.
            a.Dispose();
            Assert.IsFalse(UiFocus.GameplayBlocked);
        }
    }
}
