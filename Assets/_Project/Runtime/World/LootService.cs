using System;
using ChopChop.Items;
using FishNet.Connection;
using FishNet.Managing;
using UnityEngine;

namespace ChopChop.World
{
    /// <summary>
    /// Turns felled trees into something you can pick up, and closes the loop: swing,
    /// tree falls, wood on the ground, wood in the chest.
    ///
    /// Server-only. Loot exists because the server said a tree fell, and a client that
    /// believes otherwise simply sees nothing.
    /// </summary>
    public sealed class LootService : IDisposable
    {
        private readonly NetworkManager _networkManager;
        private readonly GameObject _prefab;
        private TreeServer _trees;

        [Tooltip("How much wood a felled tree is worth.")]
        public ushort WoodPerTree { get; set; } = 3;

        /// <summary>Item id dropped by a tree. Data, so a different world can drop something else.</summary>
        public ushort WoodItemId { get; set; }

        /// <summary>
        /// How high above the stump the drop appears. Off the floor so it does not sink
        /// into the ground mesh and become unclickable.
        /// </summary>
        public float DropHeight { get; set; } = 0.35f;

        /// <summary>
        /// Reaches a connection's carried container. Supplied at boot, because this
        /// assembly does not know what a player is.
        /// </summary>
        public Func<NetworkConnection, ItemContainer> InventoryProvider { get; set; }

        public ItemContainer InventoryOf(NetworkConnection connection)
            => connection == null ? null : InventoryProvider?.Invoke(connection);

        public LootService(NetworkManager networkManager, GameObject prefab)
        {
            _networkManager = networkManager;
            _prefab = prefab;
        }

        /// <summary>Starts listening for felled trees.</summary>
        public void Attach(TreeServer trees)
        {
            Detach();
            _trees = trees;

            if (_trees != null)
                _trees.TreeFelled += HandleTreeFelled;
        }

        public void Detach()
        {
            if (_trees != null)
                _trees.TreeFelled -= HandleTreeFelled;

            _trees = null;
        }

        public void Dispose() => Detach();

        private void HandleTreeFelled(long chunkKey, ushort localIndex, Vector3 position)
        {
            if (_prefab == null || _networkManager == null || WoodItemId == 0 || WoodPerTree == 0)
                return;

            Spawn(position + Vector3.up * DropHeight, WoodItemId, WoodPerTree);
        }

        /// <summary>Drops a stack in the world. Server-only.</summary>
        public DroppedItem Spawn(Vector3 position, ushort itemId, ushort count)
        {
            GameObject instance = UnityEngine.Object.Instantiate(_prefab, position, Quaternion.identity);
            _networkManager.ServerManager.Spawn(instance);

            /* After spawning, not before. A SyncVar written on an object that is not yet
             * spawned has no observers and no dirty tracking to record it, so the write
             * can be lost and the client is told a pile of nothing. The cost is that the
             * stack is empty for the part of a frame between the two calls, which is why
             * IsAvailable returns false at zero rather than showing an empty prompt. */
            if (instance.TryGetComponent(out DroppedItem dropped))
                dropped.SetContents(itemId, count);

            return dropped;
        }
    }
}
