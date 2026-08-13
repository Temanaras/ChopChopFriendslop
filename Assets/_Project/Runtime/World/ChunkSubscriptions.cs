using System.Collections.Generic;
using FishNet.Connection;

namespace ChopChop.World
{
    /// <summary>
    /// Which clients care about which chunks (TECH 5.5).
    ///
    /// Trees are exempt from FishNet's observer system because they are not
    /// NetworkObjects, so this is the hand-rolled interest management that takes its
    /// place. Without it, felling one tree would tell every player in the world about it.
    ///
    /// Server-side only.
    /// </summary>
    public sealed class ChunkSubscriptions
    {
        private readonly Dictionary<long, HashSet<NetworkConnection>> _byChunk = new();
        private readonly Dictionary<NetworkConnection, HashSet<long>> _byConnection = new();

        private static readonly HashSet<NetworkConnection> Empty = new();

        public int ChunkCount => _byChunk.Count;

        /// <summary>
        /// Connections subscribed to a chunk. Never null, so callers can iterate without
        /// checking.
        /// </summary>
        public IReadOnlyCollection<NetworkConnection> SubscribersOf(long chunkKey)
            => _byChunk.TryGetValue(chunkKey, out HashSet<NetworkConnection> set) ? set : Empty;

        public bool IsSubscribed(NetworkConnection connection, long chunkKey)
            => _byConnection.TryGetValue(connection, out HashSet<long> keys) && keys.Contains(chunkKey);

        /// <summary>
        /// Replaces a connection's subscriptions wholesale.
        /// </summary>
        /// <param name="added">
        /// Chunks newly subscribed to. These are the ones needing their diffs sent; the
        /// ones already held do not, which is what keeps a player walking across the
        /// world from re-downloading chunks they never left.
        /// </param>
        public void SetSubscriptions(NetworkConnection connection, IReadOnlyList<long> chunkKeys,
            List<long> added)
        {
            added?.Clear();

            if (!_byConnection.TryGetValue(connection, out HashSet<long> current))
            {
                current = new HashSet<long>();
                _byConnection[connection] = current;
            }

            _incoming.Clear();

            for (int i = 0; i < chunkKeys.Count; i++)
            {
                long key = chunkKeys[i];
                _incoming.Add(key);

                if (current.Contains(key))
                    continue;

                if (!_byChunk.TryGetValue(key, out HashSet<NetworkConnection> subscribers))
                {
                    subscribers = new HashSet<NetworkConnection>();
                    _byChunk[key] = subscribers;
                }

                subscribers.Add(connection);
                added?.Add(key);
            }

            // Drop what the client no longer wants.
            _removalScratch.Clear();

            foreach (long key in current)
            {
                if (!_incoming.Contains(key))
                    _removalScratch.Add(key);
            }

            for (int i = 0; i < _removalScratch.Count; i++)
                Unsubscribe(connection, _removalScratch[i], current);

            current.Clear();
            foreach (long key in _incoming)
                current.Add(key);
        }

        /// <summary>Forgets a connection entirely. Call on disconnect or the map leaks.</summary>
        public void RemoveConnection(NetworkConnection connection)
        {
            if (!_byConnection.TryGetValue(connection, out HashSet<long> keys))
                return;

            foreach (long key in keys)
            {
                if (!_byChunk.TryGetValue(key, out HashSet<NetworkConnection> subscribers))
                    continue;

                subscribers.Remove(connection);

                if (subscribers.Count == 0)
                    _byChunk.Remove(key);
            }

            _byConnection.Remove(connection);
        }

        public void Clear()
        {
            _byChunk.Clear();
            _byConnection.Clear();
        }

        private void Unsubscribe(NetworkConnection connection, long chunkKey, HashSet<long> current)
        {
            current.Remove(chunkKey);

            if (!_byChunk.TryGetValue(chunkKey, out HashSet<NetworkConnection> subscribers))
                return;

            subscribers.Remove(connection);

            if (subscribers.Count == 0)
                _byChunk.Remove(chunkKey);
        }

        private readonly HashSet<long> _incoming = new();
        private readonly List<long> _removalScratch = new();
    }
}
