using System;
using ChopChop.Core;
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
    public sealed class CabinChest : NetworkBehaviour, ICabinFixture, IInteractable
    {
        [Tooltip("How close a player must be to reach the chest.")]
        [SerializeField] private float _useRange = 4f;

        [SerializeField] private string _prompt = "Open the chest";

        /// <summary>
        /// Mirrors storage to every client. One chest shared by a handful of players, so
        /// sending it to everyone is cheaper than tracking who has it open — and it means
        /// the contents are already there when someone walks up.
        /// </summary>
        private readonly SyncList<ItemStack> _contents = new();

        private CabinStorage _storage;

        /// <summary>
        /// Supplied by <see cref="CabinBuilding"/> at boot, so this assembly does not need
        /// to know what a player is.
        /// </summary>
        private CabinContext _context;

        void ICabinFixture.Bind(CabinContext context) => _context = context;

        /// <summary>Raised on clients whenever the contents change, for the UI to redraw.</summary>
        public event Action ContentsChanged;

        /// <summary>
        /// Raised on the client that opened a chest, for a screen to show itself.
        ///
        /// Static because the UI cannot hold a reference to a chest that is spawned into
        /// the world scene long after it exists, and because there is exactly one screen
        /// however many chests there turn out to be.
        /// </summary>
        public static event Action<CabinChest> Opened;

        /// <summary>Raised on the asking client with the outcome of a transfer.</summary>
        public event Action<ItemMoveResult> TransferAnswered;

        public int SlotCount => _contents.Count;
        public ItemStack GetSlot(int index) => index >= 0 && index < _contents.Count ? _contents[index] : ItemStack.Empty;

        // ---------------- IInteractable ----------------

        public Vector3 InteractPoint => transform.position;
        public float InteractRange => _useRange;
        public bool IsAvailable => true;
        public string Prompt => _prompt;

        /// <summary>
        /// Runs on the interacting client. Opening is a local act — no server needs to
        /// know a panel appeared, and the contents are already replicated. Only the
        /// transfers below are authoritative.
        /// </summary>
        public void Interact() => Opened?.Invoke(this);

        private void OnEnable() => Interactables.Register(this);

        private void OnDisable() => Interactables.Unregister(this);

        private void Awake()
        {
            _contents.OnChange += HandleContentsChanged;
        }

        private void OnDestroy()
        {
            Interactables.Unregister(this);
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

        /// <summary>
        /// Put a carried stack in the chest. A negative <paramref name="storageSlot"/>
        /// means "anywhere it fits"; a drag names the square it landed on.
        /// </summary>
        [ServerRpc(RequireOwnership = false)]
        public void RequestDeposit(int inventorySlot, int storageSlot, NetworkConnection sender = null)
        {
            if (!CanReach(sender))
            {
                Answer(sender, ItemMoveResult.OutOfReach);
                return;
            }

            ItemContainer inventory = _context?.InventoryOf(sender);

            if (inventory == null)
            {
                Answer(sender, ItemMoveResult.Invalid);
                return;
            }

            /* Validated against server state, not against what the client believed. Two
             * players moving the same stack on the same frame is expected, and the second
             * one has to be refused rather than duplicated (TECH 9.4). */
            Answer(sender, Translate(_storage.Deposit(inventory, inventorySlot, storageSlot)));
        }

        /// <summary>Take a stack out. Negative <paramref name="inventorySlot"/> means anywhere.</summary>
        [ServerRpc(RequireOwnership = false)]
        public void RequestWithdraw(int storageSlot, int inventorySlot, NetworkConnection sender = null)
        {
            if (!CanReach(sender))
            {
                Answer(sender, ItemMoveResult.OutOfReach);
                return;
            }

            ItemContainer inventory = _context?.InventoryOf(sender);

            if (inventory == null)
            {
                Answer(sender, ItemMoveResult.Invalid);
                return;
            }

            Answer(sender, Translate(_storage.Withdraw(inventory, storageSlot, inventorySlot)));
        }

        /// <summary>Tidy the chest in place, without anything passing through a backpack.</summary>
        [ServerRpc(RequireOwnership = false)]
        public void RequestMoveStorage(int fromSlot, int toSlot, NetworkConnection sender = null)
        {
            if (!CanReach(sender))
            {
                Answer(sender, ItemMoveResult.OutOfReach);
                return;
            }

            Answer(sender, Translate(_storage.Move(fromSlot, toSlot)));
        }

        /* The storage layer had its own result enum before the player's inventory needed
         * one. Translating rather than merging them keeps Cabin from having to agree with
         * Player about a wire format neither of them owns. */
        private static ItemMoveResult Translate(TransferResult result) => result switch
        {
            TransferResult.Ok => ItemMoveResult.Ok,
            TransferResult.NothingThere => ItemMoveResult.NothingThere,
            TransferResult.NoRoom => ItemMoveResult.NoRoom,
            _ => ItemMoveResult.Invalid,
        };

        private void Answer(NetworkConnection sender, ItemMoveResult result)
        {
            if (sender != null)
                AnswerTransfer(sender, result);
        }

        [TargetRpc]
        private void AnswerTransfer(NetworkConnection connection, ItemMoveResult result)
            => TransferAnswered?.Invoke(result);

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
