using System;
using ChopChop.Items;
using FishNet.Connection;

namespace ChopChop.Cabin
{
    /// <summary>
    /// Everything a cabin fixture needs from outside this assembly.
    ///
    /// Exists so that adding a dependency later — an item registry, a crafting service —
    /// is a field here rather than a new argument on every fixture. The Cabin assembly
    /// deliberately knows nothing about players (TECH 3), so anything player-shaped
    /// arrives as a delegate rather than as a type.
    /// </summary>
    public sealed class CabinContext
    {
        private readonly Func<NetworkConnection, ItemContainer> _inventory;

        public CabinContext(Func<NetworkConnection, ItemContainer> inventory)
        {
            _inventory = inventory;
        }

        /// <summary>A connection's carried container, or null if it has none.</summary>
        public ItemContainer InventoryOf(NetworkConnection connection)
            => connection == null ? null : _inventory?.Invoke(connection);
    }

    /// <summary>
    /// Something in the cabin that needs wiring up at boot — a chest, a workbench, a
    /// stove.
    ///
    /// The point of the interface is that <see cref="CabinBuilding"/> finds every one of
    /// these in its own hierarchy and hands each the same context. Adding a fixture is
    /// dropping it into the prefab and implementing this; nothing outside the cabin has
    /// to learn that it exists, and the bootstrap never grows another branch.
    /// </summary>
    public interface ICabinFixture
    {
        /// <summary>
        /// Called once on the server, after the cabin is spawned. Store what you need;
        /// do not assume any other fixture has been bound yet.
        /// </summary>
        void Bind(CabinContext context);
    }
}
