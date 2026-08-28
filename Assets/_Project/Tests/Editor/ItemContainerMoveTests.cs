using ChopChop.Items;
using NUnit.Framework;

namespace ChopChop.Tests.Editor
{
    /// <summary>
    /// <see cref="ItemContainer.Move"/> is what a drag actually does, and it is the only
    /// container operation a player performs by hand rather than as a side effect of
    /// picking something up. Getting a merge wrong here destroys items silently.
    /// </summary>
    public sealed class ItemContainerMoveTests
    {
        private const ushort Axe = 1;
        private const ushort Wood = 4;

        private ItemRegistry _registry;

        [SetUp]
        public void SetUp() => _registry = ItemContainerTests.MakeRegistry();

        private ItemContainer Container(int slots = 4) => new(slots, _registry);

        [Test]
        public void MovingOntoAnEmptySlotRelocates()
        {
            ItemContainer container = Container();
            container.SetSlot(0, new ItemStack(Wood, 10));

            Assert.IsTrue(container.Move(0, 2));

            Assert.IsTrue(container[0].IsEmpty);
            Assert.AreEqual(Wood, container[2].ItemId);
            Assert.AreEqual(10, container[2].Count);
        }

        [Test]
        public void MovingOntoADifferentItemSwaps()
        {
            ItemContainer container = Container();
            container.SetSlot(0, new ItemStack(Wood, 10));
            container.SetSlot(1, new ItemStack(Axe, 1));

            Assert.IsTrue(container.Move(0, 1));

            Assert.AreEqual(Axe, container[0].ItemId);
            Assert.AreEqual(Wood, container[1].ItemId);
            Assert.AreEqual(10, container[1].Count);
        }

        [Test]
        public void MovingOntoTheSameItemMerges()
        {
            ItemContainer container = Container();
            container.SetSlot(0, new ItemStack(Wood, 10));
            container.SetSlot(1, new ItemStack(Wood, 5));

            Assert.IsTrue(container.Move(0, 1));

            Assert.IsTrue(container[0].IsEmpty, "the source should be emptied by a full merge");
            Assert.AreEqual(15, container[1].Count);
        }

        [Test]
        public void MergeLeavesTheRemainderInHand()
        {
            // Wood caps at 64. Dropping 40 onto 50 moves 14 and keeps 26 where it was —
            // anything else either destroys items or refuses a move a player expects.
            ItemContainer container = Container();
            container.SetSlot(0, new ItemStack(Wood, 40));
            container.SetSlot(1, new ItemStack(Wood, 50));

            Assert.IsTrue(container.Move(0, 1));

            Assert.AreEqual(64, container[1].Count);
            Assert.AreEqual(26, container[0].Count);
            Assert.AreEqual(90, container[0].Count + container[1].Count, "no item may be created or lost");
        }

        [Test]
        public void MovingOntoAFullStackOfTheSameItemSwapsRatherThanNoOps()
        {
            // A full destination cannot absorb anything, so the useful reading of the
            // drag is "put these two somewhere else round" rather than silence.
            ItemContainer container = Container();
            container.SetSlot(0, new ItemStack(Wood, 20));
            container.SetSlot(1, new ItemStack(Wood, 64));

            Assert.IsTrue(container.Move(0, 1));

            Assert.AreEqual(64, container[0].Count);
            Assert.AreEqual(20, container[1].Count);
        }

        [Test]
        public void DifferentDurabilityNeverMerges()
        {
            // Merging them would quietly average away the wear on one of the two.
            ItemContainer container = Container();
            container.SetSlot(0, new ItemStack(Axe, 1, 100));
            container.SetSlot(1, new ItemStack(Axe, 1, 40));

            Assert.IsTrue(container.Move(0, 1));

            Assert.AreEqual(40, container[0].Durability);
            Assert.AreEqual(100, container[1].Durability);
        }

        [Test]
        public void MovingAnEmptySlotDoesNothing()
        {
            ItemContainer container = Container();
            container.SetSlot(1, new ItemStack(Wood, 10));

            Assert.IsFalse(container.Move(0, 1), "dragging nothing is not a move");
            Assert.AreEqual(10, container[1].Count);
        }

        [Test]
        public void OutOfRangeAndSelfMovesAreRefused()
        {
            ItemContainer container = Container();
            container.SetSlot(0, new ItemStack(Wood, 10));

            Assert.IsFalse(container.Move(0, 0));
            Assert.IsFalse(container.Move(-1, 0));
            Assert.IsFalse(container.Move(0, 99));
            Assert.AreEqual(10, container[0].Count, "a refused move must not disturb anything");
        }

        [Test]
        public void ChangedIsRaisedExactlyOncePerMove()
        {
            /* Twice would be a view refreshing mid-move, which is precisely the state
             * where the dragged stack exists in neither slot. */
            ItemContainer container = Container();
            container.SetSlot(0, new ItemStack(Wood, 10));
            container.SetSlot(1, new ItemStack(Wood, 5));

            int raised = 0;
            container.Changed += () => raised++;

            container.Move(0, 1);
            Assert.AreEqual(1, raised, "merge");

            raised = 0;
            container.SetSlot(2, new ItemStack(Axe, 1));
            raised = 0;
            container.Move(1, 2);
            Assert.AreEqual(1, raised, "swap");

            raised = 0;
            container.Move(3, 0);
            Assert.AreEqual(0, raised, "a refused move must not notify");
        }
    }
}
