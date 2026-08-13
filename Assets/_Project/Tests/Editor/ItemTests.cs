using ChopChop.Cabin;
using ChopChop.Items;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace ChopChop.Tests.Editor
{
    /// <summary>
    /// Every progression element is a transferable item (TECH 2.3), so these rules are
    /// load-bearing for the whole game rather than for an inventory screen.
    /// </summary>
    public sealed class ItemContainerTests
    {
        private ItemRegistry _registry;

        private const ushort Axe = 1;
        private const ushort Wood = 4;

        [SetUp]
        public void SetUp() => _registry = MakeRegistry();

        internal static ItemRegistry MakeRegistry()
        {
            ItemDefinition axe = ScriptableObject.CreateInstance<ItemDefinition>();
            axe.Id = Axe; axe.ValidSlot = ItemSlot.Axe; axe.Tier = 1; axe.MaxStack = 1;

            ItemDefinition wood = ScriptableObject.CreateInstance<ItemDefinition>();
            wood.Id = Wood; wood.ValidSlot = ItemSlot.None; wood.MaxStack = 64;

            ItemRegistry registry = ScriptableObject.CreateInstance<ItemRegistry>();
            var so = new UnityEditor.SerializedObject(registry);
            var array = so.FindProperty("_items");
            array.arraySize = 2;
            array.GetArrayElementAtIndex(0).objectReferenceValue = axe;
            array.GetArrayElementAtIndex(1).objectReferenceValue = wood;
            so.ApplyModifiedPropertiesWithoutUndo();

            registry.Validate();
            return registry;
        }

        [Test]
        public void StacksFillPartialSlotsBeforeOpeningNewOnes()
        {
            // Otherwise a container fills with singles and reads as full while holding
            // almost nothing.
            ItemContainer container = new(4, _registry);

            container.TryAdd(Wood, 10);
            container.TryAdd(Wood, 10);

            Assert.AreEqual(20, container[0].Count);
            Assert.IsTrue(container[1].IsEmpty, "should not have opened a second slot");
        }

        [Test]
        public void OversizedStacksSplitAcrossSlots()
        {
            ItemContainer container = new(4, _registry);

            ushort leftover = container.TryAdd(Wood, 100);   // max stack is 64

            Assert.AreEqual(0, leftover);
            Assert.AreEqual(64, container[0].Count);
            Assert.AreEqual(36, container[1].Count);
        }

        [Test]
        public void WhatCannotFitIsReportedRatherThanLost()
        {
            // Silently dropping the remainder would destroy player progress.
            ItemContainer container = new(1, _registry);

            ushort leftover = container.TryAdd(Wood, 100);

            Assert.AreEqual(36, leftover);
            Assert.AreEqual(64, container.CountOf(Wood));
        }

        [Test]
        public void UnstackableItemsTakeOneSlotEach()
        {
            ItemContainer container = new(4, _registry);

            container.TryAdd(Axe, 3);

            Assert.AreEqual(1, container[0].Count);
            Assert.AreEqual(1, container[1].Count);
            Assert.AreEqual(1, container[2].Count);
        }

        [Test]
        public void RemovingDrainsAcrossStacks()
        {
            ItemContainer container = new(4, _registry);
            container.TryAdd(Wood, 100);

            Assert.AreEqual(70, container.TryRemove(Wood, 70));
            Assert.AreEqual(30, container.CountOf(Wood));
        }

        [Test]
        public void ClearEmptiesEverything()
        {
            // This is what dying costs (TECH 9.3).
            ItemContainer container = new(4, _registry);
            container.TryAdd(Wood, 50);

            container.Clear();

            Assert.AreEqual(0, container.CountOf(Wood));
        }
    }

    public sealed class CabinStorageTests
    {
        private ItemRegistry _registry;
        private CabinState _state;
        private CabinStorage _storage;

        private const ushort Axe = 1;
        private const ushort Wood = 4;

        [SetUp]
        public void SetUp()
        {
            _registry = ItemContainerTests.MakeRegistry();
            _state = new CabinState();
            _storage = new CabinStorage(_state, _registry, slotCount: 8);
        }

        [Test]
        public void TwoPlayersCannotTakeTheSameStack()
        {
            /* The case TECH 9.4 says will happen on day one. Both clients send the same
             * slot index; validating against current server state rather than what they
             * believed they saw is what stops one axe becoming two. */
            _storage.Add(Axe, 1);

            ItemContainer alice = new(4, _registry);
            ItemContainer bob = new(4, _registry);

            Assert.AreEqual(TransferResult.Ok, _storage.Withdraw(alice, 0));
            Assert.AreEqual(TransferResult.NothingThere, _storage.Withdraw(bob, 0));

            Assert.AreEqual(1, alice.CountOf(Axe));
            Assert.AreEqual(0, bob.CountOf(Axe));
        }

        [Test]
        public void DepositingIntoAFullChestLeavesTheItemWhereItWas()
        {
            // Taking from the source before checking the destination would delete it.
            CabinStorage tiny = new(new CabinState(), _registry, slotCount: 1);
            tiny.Add(Wood, 64);

            ItemContainer player = new(4, _registry);
            player.TryAdd(Axe, 1);

            Assert.AreEqual(TransferResult.NoRoom, tiny.Deposit(player, 0));
            Assert.AreEqual(1, player.CountOf(Axe), "the axe must still be in the player's hands");
        }

        [Test]
        public void DepositThenWithdrawRoundTrips()
        {
            ItemContainer player = new(4, _registry);
            player.TryAdd(Wood, 30);

            Assert.AreEqual(TransferResult.Ok, _storage.Deposit(player, 0));
            Assert.AreEqual(0, player.CountOf(Wood));

            Assert.AreEqual(TransferResult.Ok, _storage.Withdraw(player, 0));
            Assert.AreEqual(30, player.CountOf(Wood));
        }

        [Test]
        public void TransfersWriteThroughToTheSavedCabinState()
        {
            // Storage lives in the world save, not on a NetworkObject (TECH 9.4), which
            // is what makes it survive a restart.
            _storage.Add(Wood, 20);

            Assert.IsNotNull(_state.Storage);
            Assert.AreEqual(20, _state.Storage[0].Count);
        }

        [Test]
        public void EmptySlotsAndBadIndicesAreRefusedNotThrown()
        {
            ItemContainer player = new(4, _registry);

            Assert.AreEqual(TransferResult.NothingThere, _storage.Withdraw(player, 0));
            Assert.AreEqual(TransferResult.Invalid, _storage.Withdraw(player, 999));
            Assert.AreEqual(TransferResult.Invalid, _storage.Deposit(null, 0));
        }
    }

    public sealed class ItemRegistryTests
    {
        [Test]
        public void DuplicateIdsAreRefused()
        {
            /* Ids are what saves and packets carry. A duplicate means two items share a
             * save entry and one silently becomes the other — nothing throws on its own. */
            ItemDefinition a = ScriptableObject.CreateInstance<ItemDefinition>();
            a.Id = 7; a.MaxStack = 1;

            ItemDefinition b = ScriptableObject.CreateInstance<ItemDefinition>();
            b.Id = 7; b.MaxStack = 1;

            ItemRegistry registry = ScriptableObject.CreateInstance<ItemRegistry>();
            var so = new UnityEditor.SerializedObject(registry);
            var array = so.FindProperty("_items");
            array.arraySize = 2;
            array.GetArrayElementAtIndex(0).objectReferenceValue = a;
            array.GetArrayElementAtIndex(1).objectReferenceValue = b;
            so.ApplyModifiedPropertiesWithoutUndo();

            LogAssert.ignoreFailingMessages = true;
            bool valid = registry.Validate();
            LogAssert.ignoreFailingMessages = false;

            Assert.IsFalse(valid);
        }

        [Test]
        public void IdZeroIsRefusedBecauseItMeansEmpty()
        {
            ItemDefinition item = ScriptableObject.CreateInstance<ItemDefinition>();
            item.Id = 0; item.MaxStack = 1;

            ItemRegistry registry = ScriptableObject.CreateInstance<ItemRegistry>();
            var so = new UnityEditor.SerializedObject(registry);
            var array = so.FindProperty("_items");
            array.arraySize = 1;
            array.GetArrayElementAtIndex(0).objectReferenceValue = item;
            so.ApplyModifiedPropertiesWithoutUndo();

            LogAssert.ignoreFailingMessages = true;
            bool valid = registry.Validate();
            LogAssert.ignoreFailingMessages = false;

            Assert.IsFalse(valid);
        }

        [Test]
        public void UnknownIdsResolveToHarmlessDefaults()
        {
            // A save referencing an item this build does not have must not crash the load.
            ItemRegistry registry = ScriptableObject.CreateInstance<ItemRegistry>();
            registry.Validate();

            Assert.IsNull(registry.Get(1234));
            Assert.AreEqual(0, registry.TierOf(1234));
            Assert.AreEqual(1, registry.MaxStackOf(1234));
            Assert.AreEqual(ItemSlot.None, registry.SlotOf(1234));
        }
    }
}
