using System;
using System.Collections.Generic;
using FishNet.Managing;
using FishNet.Transporting;
using UnityEngine;

namespace ChopChop.World
{
    /// <summary>
    /// The client half of tree replication (TECH 5.5).
    ///
    /// Tells the server which chunks it is standing near, keeps a local copy of the diffs
    /// the server sends back, and raises events so the collider band and effects can
    /// react. It never decides anything — the local diff store here is a cache of what
    /// the server said, not an opinion about what is true.
    /// </summary>
    public sealed class TreeClient : IDisposable
    {
        private readonly NetworkManager _networkManager;
        private readonly TreeDiffStore _diffs;

        private readonly HashSet<long> _subscribed = new();
        private readonly List<long> _pending = new();

        private bool _registered;

        public TreeDiffStore Diffs => _diffs;

        /// <summary>Chunk key and local index of a tree whose state changed.</summary>
        public event Action<long, ushort> TreeChanged;

        /// <summary>Raised when a whole chunk's diffs arrive, including on late join.</summary>
        public event Action<long> ChunkDiffsReceived;

        /// <summary>Raised when the server refuses a chop, so the client can show why.</summary>
        public event Action<ChopRejectedBroadcast> ChopRejected;

        public TreeClient(NetworkManager networkManager, TreeDiffStore diffs)
        {
            _networkManager = networkManager ? networkManager : throw new ArgumentNullException(nameof(networkManager));
            _diffs = diffs ?? throw new ArgumentNullException(nameof(diffs));

            _networkManager.ClientManager.RegisterBroadcast<ChunkDiffsBroadcast>(HandleChunkDiffs);
            _networkManager.ClientManager.RegisterBroadcast<TreeDamagedBroadcast>(HandleDamaged);
            _networkManager.ClientManager.RegisterBroadcast<TreeFelledBroadcast>(HandleFelled);
            _networkManager.ClientManager.RegisterBroadcast<ChopRejectedBroadcast>(HandleRejected);
            _registered = true;
        }

        public void Dispose()
        {
            if (!_registered)
                return;

            _registered = false;
            _networkManager.ClientManager.UnregisterBroadcast<ChunkDiffsBroadcast>(HandleChunkDiffs);
            _networkManager.ClientManager.UnregisterBroadcast<TreeDamagedBroadcast>(HandleDamaged);
            _networkManager.ClientManager.UnregisterBroadcast<TreeFelledBroadcast>(HandleFelled);
            _networkManager.ClientManager.UnregisterBroadcast<ChopRejectedBroadcast>(HandleRejected);
        }

        /// <summary>
        /// Declares which chunks this client cares about. Sends nothing when the set is
        /// unchanged, so this is safe to call every time streaming updates.
        /// </summary>
        public void SetSubscribedChunks(IEnumerable<long> chunkKeys)
        {
            _pending.Clear();

            bool changed = false;
            int count = 0;

            foreach (long key in chunkKeys)
            {
                _pending.Add(key);
                count++;

                if (!_subscribed.Contains(key))
                    changed = true;
            }

            // A different size with no new keys means keys were dropped.
            if (!changed && count == _subscribed.Count)
                return;

            _subscribed.Clear();
            for (int i = 0; i < _pending.Count; i++)
                _subscribed.Add(_pending[i]);

            _networkManager.ClientManager.Broadcast(new SubscribeChunksBroadcast
            {
                ChunkKeys = _pending.ToArray(),
            }, Channel.Reliable);
        }

        /// <summary>
        /// Asks the server to chop. The caller has already played the swing — this is a
        /// request, and the visual is a lie until the server agrees (TECH 4.3).
        /// </summary>
        public void RequestChop(long chunkKey, ushort localIndex)
        {
            _networkManager.ClientManager.Broadcast(new ChopRequestBroadcast
            {
                ChunkKey = chunkKey,
                LocalIndex = localIndex,
            }, Channel.Reliable);
        }

        private void HandleChunkDiffs(ChunkDiffsBroadcast message, Channel channel)
        {
            _diffs.SetChunkDiffs(message.ChunkKey, message.Diffs);
            ChunkDiffsReceived?.Invoke(message.ChunkKey);
        }

        private void HandleDamaged(TreeDamagedBroadcast message, Channel channel)
        {
            _diffs.ApplyDiff(message.ChunkKey, new TreeDiff(message.LocalIndex, message.HealthRemaining, 0));
            TreeChanged?.Invoke(message.ChunkKey, message.LocalIndex);
        }

        private void HandleFelled(TreeFelledBroadcast message, Channel channel)
        {
            _diffs.ApplyDiff(message.ChunkKey, new TreeDiff(message.LocalIndex, 0, message.FelledAtTick));
            TreeChanged?.Invoke(message.ChunkKey, message.LocalIndex);
        }

        private void HandleRejected(ChopRejectedBroadcast message, Channel channel)
        {
            /* A refusal always gets a response. Feeding back nothing would read as the
             * game being broken rather than the axe being wrong (TECH 5.6). */
            ChopRejected?.Invoke(message);
        }
    }
}
