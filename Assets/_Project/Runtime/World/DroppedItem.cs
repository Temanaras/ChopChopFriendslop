using ChopChop.Core;
using ChopChop.Items;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace ChopChop.World
{
    /// <summary>
    /// A stack of something lying on the ground, waiting to be picked up.
    ///
    /// Never saved. Ground loot is rebuilt from nothing on load (TECH 6.3), which is why
    /// this is an ordinary spawned <c>NetworkObject</c> with no presence in the save file
    /// at all — a log left in the forest overnight is gone in the morning, and that is the
    /// intended behaviour rather than an omission.
    ///
    /// Lives in World rather than Items because Items is data and deliberately has no
    /// networking reference. If ground loot grows past "a tree dropped a log", it wants
    /// its own assembly.
    /// </summary>
    public sealed class DroppedItem : NetworkBehaviour, IInteractable
    {
        [SerializeField] private float _range = 2.5f;

        [Tooltip("Despawns after this long so a long session does not carpet the forest. " +
                 "Zero means it stays until taken.")]
        [SerializeField] private float _lifetimeSeconds = 600f;

        [Tooltip("Spun slowly so it reads as loot rather than as scenery.")]
        [SerializeField] private Transform _spin;

        [SerializeField] private float _spinDegreesPerSecond = 35f;

        private readonly SyncVar<ushort> _itemId = new();
        private readonly SyncVar<ushort> _count = new();

        private ItemRegistry _registry;
        private float _spawnedAt;

        public ushort ItemId => _itemId.Value;
        public ushort Count => _count.Value;

        // ---------------- IInteractable ----------------

        public Vector3 InteractPoint => transform.position;
        public float InteractRange => _range;
        public bool IsAvailable => _count.Value > 0;

        public string Prompt
        {
            get
            {
                if (_registry == null)
                    ServiceLocator.TryGet(out _registry);

                ItemDefinition definition = _registry != null ? _registry.Get(_itemId.Value) : null;
                string name = definition != null ? definition.DisplayName : "item";

                return _count.Value > 1 ? $"Take {_count.Value} {name}" : $"Take {name}";
            }
        }

        public void Interact() => RequestPickup();

        // ---------------- Lifecycle ----------------

        private void OnEnable() => Interactables.Register(this);

        private void OnDisable() => Interactables.Unregister(this);

        private void Update()
        {
            if (_spin != null)
                _spin.Rotate(Vector3.up, _spinDegreesPerSecond * Time.deltaTime, Space.Self);

            /* Server decides when it expires. A client running this would despawn it
             * locally and leave a ghost the server still believes in. */
            if (!IsServerInitialized || _lifetimeSeconds <= 0f)
                return;

            if (Time.time - _spawnedAt >= _lifetimeSeconds)
                Despawn();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            _spawnedAt = Time.time;
        }

        /// <summary>Sets what this pile is. Server-only, called right after spawning.</summary>
        public void SetContents(ushort itemId, ushort count)
        {
            _itemId.Value = itemId;
            _count.Value = count;
        }

        // ---------------- Server ----------------

        [ServerRpc(RequireOwnership = false)]
        private void RequestPickup(NetworkConnection sender = null)
        {
            if (sender?.FirstObject == null || _count.Value == 0)
                return;

            float distance = Vector3.Distance(sender.FirstObject.transform.position, transform.position);

            // Same slack as every other reach check: enough for latency, not enough to
            // vacuum the forest from the cabin.
            if (distance > _range + 2f)
                return;

            if (!ServiceLocator.TryGet(out LootService loot))
                return;

            ItemContainer inventory = loot.InventoryOf(sender);

            if (inventory == null)
                return;

            /* Whatever does not fit stays on the ground. Silently eating the remainder
             * because a backpack was full is the kind of loss players never forgive. */
            ushort leftover = inventory.TryAdd(_itemId.Value, _count.Value);

            if (leftover == _count.Value)
                return;

            if (leftover > 0)
            {
                _count.Value = leftover;
                return;
            }

            Despawn();
        }

        private void Despawn()
        {
            if (IsServerInitialized && IsSpawned)
                ServerManager.Despawn(gameObject);
        }
    }
}
