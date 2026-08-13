using System;
using System.Collections.Generic;
using ChopChop.Biomes;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Transporting;
using UnityEngine;

namespace ChopChop.World
{
    /// <summary>
    /// Server-side authority over trees (TECH 5.6).
    ///
    /// The client's swing is a lie until this says otherwise. It plays the animation
    /// immediately for feel, sends a request, and this decides whether the tree actually
    /// took damage. Nothing rolls back — a rejected chop simply deals no damage, which at
    /// four players nobody notices (TECH 4.3).
    ///
    /// Everything that matters is validated here and only here: that the tree exists,
    /// that it is still standing, that the player is close enough, that their axe is good
    /// enough, and that they are not swinging faster than an axe can swing.
    /// </summary>
    public sealed class TreeServer : IDisposable
    {
        /// <summary>
        /// Generous on purpose. The client raycast happened at their position some
        /// milliseconds ago, and there is no lag compensation (TECH 4.4), so the tolerance
        /// absorbs the difference rather than punishing players for their ping.
        /// </summary>
        public const float RangeTolerance = 3f;

        private readonly NetworkManager _networkManager;
        private readonly ChunkStore _chunks;
        private readonly TreeDiffStore _diffs;
        private readonly ChunkSubscriptions _subscriptions = new();

        private readonly Dictionary<NetworkConnection, uint> _lastChopTick = new();
        private readonly List<long> _addedScratch = new();

        private bool _subscribed;

        /// <summary>Damage one swing does. Tuning lever; tier gating is separate.</summary>
        public byte DamagePerSwing { get; set; } = 64;

        /// <summary>Minimum ticks between accepted chops from one player.</summary>
        public uint SwingCooldownTicks { get; set; } = 15;

        /// <summary>
        /// Reads a connection's equipped axe tier. Supplied at boot so this assembly does
        /// not have to know what a paperdoll is.
        /// </summary>
        public Func<NetworkConnection, byte> AxeTierProvider { get; set; }

        /// <summary>Used when no provider is set, e.g. in tests.</summary>
        public byte FallbackAxeTier { get; set; } = 1;

        private byte AxeTierFor(NetworkConnection connection)
            => AxeTierProvider != null ? AxeTierProvider(connection) : FallbackAxeTier;

        /// <summary>How far a player may be from a tree and still fell it.</summary>
        public float ChopRange { get; set; } = 4f;

        /// <summary>Raised when a tree is felled, so loot and effects can hang off it.</summary>
        public event Action<long, ushort, Vector3> TreeFelled;

        public TreeDiffStore Diffs => _diffs;
        public ChunkSubscriptions Subscriptions => _subscriptions;

        /// <param name="worldTick">
        /// Persistent elapsed world time, not FishNet's network tick. Regrowth measures
        /// against this and it must survive restarts, or every felled tree would look
        /// freshly cut each session and nothing would ever grow back.
        /// </param>
        public TreeServer(NetworkManager networkManager, ChunkStore chunks, TreeDiffStore diffs,
            Func<uint> worldTick, RegrowthService regrowth = null)
        {
            _networkManager = networkManager ? networkManager : throw new ArgumentNullException(nameof(networkManager));
            _chunks = chunks ?? throw new ArgumentNullException(nameof(chunks));
            _diffs = diffs ?? throw new ArgumentNullException(nameof(diffs));
            _worldTick = worldTick ?? throw new ArgumentNullException(nameof(worldTick));
            _regrowth = regrowth;

            _networkManager.ServerManager.RegisterBroadcast<SubscribeChunksBroadcast>(HandleSubscribe);
            _networkManager.ServerManager.RegisterBroadcast<ChopRequestBroadcast>(HandleChopRequest);
            _networkManager.ServerManager.OnRemoteConnectionState += HandleConnectionState;
            _networkManager.TimeManager.OnTick += HandleTick;
            _subscribed = true;
        }

        private readonly Func<uint> _worldTick;
        private readonly RegrowthService _regrowth;
        private readonly List<long> _occupiedScratch = new();

        /// <summary>
        /// Keeps every held chunk's clock current. While anyone is subscribed the gap
        /// never grows, so regrowth cannot creep into ground players are working
        /// (TECH 7.1).
        /// </summary>
        private void HandleTick()
        {
            if (_regrowth == null)
                return;

            uint tick = _worldTick();

            _occupiedScratch.Clear();
            _subscriptions.CollectOccupiedChunks(_occupiedScratch);

            for (int i = 0; i < _occupiedScratch.Count; i++)
                _regrowth.MarkOccupied(_occupiedScratch[i], tick);
        }

        public void Dispose()
        {
            if (!_subscribed)
                return;

            _subscribed = false;
            _networkManager.ServerManager.UnregisterBroadcast<SubscribeChunksBroadcast>(HandleSubscribe);
            _networkManager.ServerManager.UnregisterBroadcast<ChopRequestBroadcast>(HandleChopRequest);
            _networkManager.ServerManager.OnRemoteConnectionState -= HandleConnectionState;
            _networkManager.TimeManager.OnTick -= HandleTick;

            _subscriptions.Clear();
            _lastChopTick.Clear();
        }

        private void HandleConnectionState(NetworkConnection connection, RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState != RemoteConnectionState.Stopped)
                return;

            // Without this the subscriber map grows for the lifetime of the server.
            _subscriptions.RemoveConnection(connection);
            _lastChopTick.Remove(connection);
        }

        // ---------------- Subscription ----------------

        private void HandleSubscribe(NetworkConnection connection, SubscribeChunksBroadcast message, Channel channel)
        {
            if (message.ChunkKeys == null)
                return;

            _subscriptions.SetSubscriptions(connection, message.ChunkKeys, _addedScratch);

            /* Only newly-subscribed chunks get their diffs sent. A player walking around
             * re-sends their whole subscription set every time it changes, and resending
             * diffs for chunks they never left would scale badly with how much they move
             * rather than with how much they discover. */
            uint tick = _worldTick();

            for (int i = 0; i < _addedScratch.Count; i++)
            {
                long key = _addedScratch[i];

                /* Regrowth runs when a chunk becomes occupied and before its diffs go
                 * out, so the joining player is told the world as it is now rather than
                 * as it was when they left. Only on the transition into occupancy — a
                 * second player arriving at a chunk someone is already holding must not
                 * re-evaluate it. */
                if (_regrowth != null && _subscriptions.SubscribersOf(key).Count == 1)
                    _regrowth.Evaluate(key, tick);

                _networkManager.ServerManager.Broadcast(connection, new ChunkDiffsBroadcast
                {
                    ChunkKey = key,
                    Diffs = _diffs.GetDiffsArray(key),
                }, true, Channel.Reliable);
            }
        }

        // ---------------- Chopping ----------------

        private void HandleChopRequest(NetworkConnection connection, ChopRequestBroadcast message, Channel channel)
        {
            // Rate limiting uses the network tick, which is the right clock for "how fast
            // is this player swinging". The world tick below is the right clock for "when
            // was this tree felled", which has to outlive the session.
            uint tick = _networkManager.TimeManager.Tick;
            uint worldTick = _worldTick();

            if (!TryValidate(connection, message, tick, out ChunkData chunk, out GeneratedTree tree,
                    out ChopRejection rejection))
            {
                Reject(connection, message, rejection, tree.TierIndex);
                return;
            }

            _lastChopTick[connection] = tick;

            if (!_diffs.TryApplyDamage(message.ChunkKey, message.LocalIndex, DamagePerSwing, worldTick,
                    out byte remaining, out bool felled))
            {
                Reject(connection, message, ChopRejection.AlreadyFelled, tree.TierIndex);
                return;
            }

            if (felled)
            {
                BroadcastToSubscribers(message.ChunkKey, new TreeFelledBroadcast
                {
                    ChunkKey = message.ChunkKey,
                    LocalIndex = message.LocalIndex,
                    FelledAtTick = worldTick,
                });

                TreeFelled?.Invoke(message.ChunkKey, message.LocalIndex, chunk.Origin + tree.LocalPosition);
                return;
            }

            BroadcastToSubscribers(message.ChunkKey, new TreeDamagedBroadcast
            {
                ChunkKey = message.ChunkKey,
                LocalIndex = message.LocalIndex,
                HealthRemaining = remaining,
            });
        }

        private bool TryValidate(NetworkConnection connection, ChopRequestBroadcast message, uint tick,
            out ChunkData chunk, out GeneratedTree tree, out ChopRejection rejection)
        {
            chunk = null;
            tree = default;
            rejection = ChopRejection.None;

            // Never trust client timing (TECH 5.6).
            if (_lastChopTick.TryGetValue(connection, out uint last) && tick - last < SwingCooldownTicks)
            {
                rejection = ChopRejection.TooSoon;
                return false;
            }

            ChunkKey.Unpack(message.ChunkKey, out int chunkX, out int chunkZ);
            chunk = _chunks.GetOrGenerate(chunkX, chunkZ);

            if (message.LocalIndex >= chunk.Trees.Length)
            {
                rejection = ChopRejection.NoSuchTree;
                return false;
            }

            tree = chunk.Trees[message.LocalIndex];

            if (_diffs.IsFelled(message.ChunkKey, message.LocalIndex))
            {
                rejection = ChopRejection.AlreadyFelled;
                return false;
            }

            /* Tier is a hard gate, checked before range so the player is told the useful
             * thing: walking closer will not help if the axe is wrong. Read from the
             * server's copy of the paperdoll, never from anything the client sent. */
            if (AxeTierFor(connection) < tree.TierIndex)
            {
                rejection = ChopRejection.TierTooLow;
                return false;
            }

            if (!TryGetPlayerPosition(connection, out Vector3 playerPosition))
            {
                rejection = ChopRejection.OutOfRange;
                return false;
            }

            Vector3 treeWorld = chunk.Origin + tree.LocalPosition;
            float allowed = ChopRange + RangeTolerance;

            // Horizontal only: standing on a rock should not put a tree out of reach.
            Vector2 flatTree = new(treeWorld.x, treeWorld.z);
            Vector2 flatPlayer = new(playerPosition.x, playerPosition.z);

            if ((flatTree - flatPlayer).sqrMagnitude > allowed * allowed)
            {
                rejection = ChopRejection.OutOfRange;
                return false;
            }

            return true;
        }

        private bool TryGetPlayerPosition(NetworkConnection connection, out Vector3 position)
        {
            position = default;

            if (connection == null || connection.FirstObject == null)
                return false;

            position = connection.FirstObject.transform.position;
            return true;
        }

        private void Reject(NetworkConnection connection, ChopRequestBroadcast message,
            ChopRejection reason, byte requiredTier)
        {
            // Silent nothing reads as a bug, so every refusal is answered (TECH 5.6).
            _networkManager.ServerManager.Broadcast(connection, new ChopRejectedBroadcast
            {
                ChunkKey = message.ChunkKey,
                LocalIndex = message.LocalIndex,
                Reason = reason,
                RequiredTier = requiredTier,
            }, true, Channel.Reliable);
        }

        private void BroadcastToSubscribers<T>(long chunkKey, T message) where T : struct, FishNet.Broadcast.IBroadcast
        {
            foreach (NetworkConnection subscriber in _subscriptions.SubscribersOf(chunkKey))
                _networkManager.ServerManager.Broadcast(subscriber, message, true, Channel.Reliable);
        }
    }
}
