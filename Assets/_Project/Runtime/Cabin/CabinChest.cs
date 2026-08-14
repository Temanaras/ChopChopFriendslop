using System;
using ChopChop.Items;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace ChopChop.Cabin
{
    /// <summary>
    /// The chest as players interact with it (TECH 9.4).
    ///
    /// <see cref="CabinStorage"/> holds the actual contents and lives in the world save.
    /// This is the networked face of it: it mirrors those contents outward so a UI has
    /// something to draw, and it takes requests to move things.
    ///
    /// **Every transfer is a round trip.** Nothing is predicted, because inventory
    /// transfers are not latency-sensitive and guessing at them creates desync bugs for
    /// no benefit (TECH 4.3). A client asks; the server checks its own state and answers.
    /// </summary>
    public sealed class CabinChest : NetworkBehaviour
    {
        [Tooltip("How close a player must be to reach the chest.")]
        [SerializeField] private float _useRange = 4f;

        /// <summary>
        /// Mirrors storage to every client. One chest shared by a handful of players, so
        /// sending it to everyone is cheaper than tracking who has it open — and it means
        /// the contents are already there when someone walks up.
        /// </summary>
        private readonly SyncList<ItemStack> _contents = new();

        private CabinStorage _storage;

        /// <summary>
        /// Supplies a connection's carried container. Injected at boot so this assembly
        /// does not need to know what a player is.
        /// </summary>
        public Func<NetworkConnection, ItemContainer> InventoryProvider { get; set; }

        /// <summary>Raised on clients whenever the contents change, for the UI to redraw.</summary>
        public event Action ContentsChanged;

        public int SlotCount => _contents.Count;
        public ItemStack GetSlot(int index) => index >= 0 && index < _contents.Count ? _contents[index] : ItemStack.Empty;

        private void Awake()
        {
            _contents.OnChange += HandleContentsChanged;
        }

        private void OnDestroy()
        {
            _contents.OnChange -= HandleContentsChanged;

            if (_storage != null)
                _storage.Changed -= PushContents;
        }

        private void HandleContentsChanged(SyncListOperation op, int index, ItemStack previous,
            ItemStack next, bool asServer) => ContentsChanged?.Invoke();

        public override void OnStartServer()
        {
            base.OnStartServer();

            if (!Core.ServiceLocator.TryGet(out _storage))
            {
                Debug.LogError("[Cabin] No CabinStorage registered; the chest will be inert.");
                return;
            }

            for (int i = _contents.Count; i < _storage.SlotCount; i++)
                _contents.Add(ItemStack.Empty);

            _storage.Changed += PushContents;
            PushContents();
        }

        private void PushContents()
        {
            if (!IsServerInitialized || _storage == null)
                return;

            for (int i = 0; i < _contents.Count && i < _storage.SlotCount; i++)
                _contents[i] = _storage[i];
        }

        // ---------------- Requests ----------------

        [ServerRpc(RequireOwnership = false)]
        public void RequestDeposit(int inventorySlot, NetworkConnection sender = null)
        {
            if (!CanReach(sender))
                return;

            ItemContainer inventory = InventoryProvider?.Invoke(sender);

            if (inventory == null)
                return;

            /* Validated against server state, not against what the client believed. Two
             * players moving the same stack on the same frame is expected, and the second
             * one has to be refused rather than duplicated (TECH 9.4). */
            _storage.Deposit(inventory, inventorySlot);
        }

        [ServerRpc(RequireOwnership = false)]
        public void RequestWithdraw(int storageSlot, NetworkConnection sender = null)
        {
            if (!CanReach(sender))
                return;

            ItemContainer inventory = InventoryProvider?.Invoke(sender);

            if (inventory == null)
                return;

            _storage.Withdraw(inventory, storageSlot);
        }

        /// <summary>
        /// Range is checked here rather than trusted from the client, or a player could
        /// empty the chest from the far side of the map.
        /// </summary>
        private bool CanReach(NetworkConnection sender)
        {
            if (_storage == null || sender?.FirstObject == null)
                return false;

            float distance = Vector3.Distance(sender.FirstObject.transform.position, transform.position);

            // A little slack for latency, in the same spirit as the chop and shot checks.
            return distance <= _useRange + 2f;
        }
    }
}
