using System.Collections.Generic;

namespace ChopChop.World
{
    /// <summary>
    /// Every deviation from generated tree state, keyed by chunk (TECH 5.3).
    ///
    /// **A chunk with no diffs has no entry.** That is the whole point: an untouched
    /// world costs nothing, and only what players actually changed is stored or sent.
    /// This is also why a <c>SyncDictionary</c> would be wrong here — it would replicate
    /// the entire world's diffs to every client and grow without bound. Diffs go out by
    /// chunk-scoped broadcast instead (TECH 5.5).
    ///
    /// Server-authoritative. Clients keep their own instance as a cache of what the
    /// server has told them, but never write to it on their own initiative.
    /// </summary>
    public sealed class TreeDiffStore
    {
        private readonly Dictionary<long, List<TreeDiff>> _byChunk = new();

        /// <summary>Chunks that have at least one diff.</summary>
        public int ChunkCount => _byChunk.Count;

        public IReadOnlyDictionary<long, List<TreeDiff>> All => _byChunk;

        /// <summary>Diffs for a chunk, or null when it is untouched.</summary>
        public List<TreeDiff> GetDiffs(long chunkKey)
            => _byChunk.TryGetValue(chunkKey, out List<TreeDiff> diffs) ? diffs : null;

        public TreeDiff[] GetDiffsArray(long chunkKey)
            => _byChunk.TryGetValue(chunkKey, out List<TreeDiff> diffs)
                ? diffs.ToArray()
                : System.Array.Empty<TreeDiff>();

        /// <summary>
        /// Current health of a tree. Returns <see cref="TreeDiff.FullHealth"/> for
        /// anything with no diff, since untouched is the default state.
        /// </summary>
        public byte GetHealth(long chunkKey, ushort localIndex)
        {
            if (!TryFind(chunkKey, localIndex, out _, out int index))
                return TreeDiff.FullHealth;

            return _byChunk[chunkKey][index].HealthRemaining;
        }

        public bool IsFelled(long chunkKey, ushort localIndex) => GetHealth(chunkKey, localIndex) == 0;

        /// <summary>
        /// Applies damage and reports the resulting health.
        /// </summary>
        /// <returns>False if the tree was already felled, in which case nothing changed.</returns>
        public bool TryApplyDamage(long chunkKey, ushort localIndex, byte damage, uint worldTick,
            out byte remaining, out bool felled)
        {
            remaining = TreeDiff.FullHealth;
            felled = false;

            if (!_byChunk.TryGetValue(chunkKey, out List<TreeDiff> diffs))
            {
                diffs = new List<TreeDiff>();
                _byChunk[chunkKey] = diffs;
            }

            int index = IndexOf(diffs, localIndex);
            byte current = index >= 0 ? diffs[index].HealthRemaining : TreeDiff.FullHealth;

            if (current == 0)
            {
                remaining = 0;
                return false;
            }

            remaining = damage >= current ? (byte)0 : (byte)(current - damage);
            felled = remaining == 0;

            // feltAtTick only means anything once the tree is down; regrowth measures
            // from it (TECH 7.1).
            TreeDiff updated = new(localIndex, remaining, felled ? worldTick : 0u);

            if (index >= 0)
                diffs[index] = updated;
            else
                diffs.Add(updated);

            return true;
        }

        /// <summary>
        /// Drops a diff, returning the tree to its generated state. This is what regrowth
        /// does (TECH 7.1) — a reclaimed tree is not "regrown", it simply stops having a
        /// deviation recorded.
        /// </summary>
        public bool RemoveDiff(long chunkKey, ushort localIndex)
        {
            if (!_byChunk.TryGetValue(chunkKey, out List<TreeDiff> diffs))
                return false;

            int index = IndexOf(diffs, localIndex);

            if (index < 0)
                return false;

            diffs.RemoveAt(index);

            // Keep the invariant: no diffs means no entry, so an untouched chunk costs
            // nothing to hold or to save.
            if (diffs.Count == 0)
                _byChunk.Remove(chunkKey);

            return true;
        }

        /// <summary>Replaces a whole chunk's diffs. Used when the server sends them.</summary>
        public void SetChunkDiffs(long chunkKey, IReadOnlyList<TreeDiff> diffs)
        {
            if (diffs == null || diffs.Count == 0)
            {
                _byChunk.Remove(chunkKey);
                return;
            }

            if (!_byChunk.TryGetValue(chunkKey, out List<TreeDiff> list))
            {
                list = new List<TreeDiff>(diffs.Count);
                _byChunk[chunkKey] = list;
            }
            else
            {
                list.Clear();
            }

            for (int i = 0; i < diffs.Count; i++)
                list.Add(diffs[i]);
        }

        /// <summary>Applies a single diff sent by the server.</summary>
        public void ApplyDiff(long chunkKey, TreeDiff diff)
        {
            if (!_byChunk.TryGetValue(chunkKey, out List<TreeDiff> diffs))
            {
                diffs = new List<TreeDiff>();
                _byChunk[chunkKey] = diffs;
            }

            int index = IndexOf(diffs, diff.LocalIndex);

            if (index >= 0)
                diffs[index] = diff;
            else
                diffs.Add(diff);
        }

        public void Clear() => _byChunk.Clear();

        private bool TryFind(long chunkKey, ushort localIndex, out List<TreeDiff> diffs, out int index)
        {
            index = -1;

            if (!_byChunk.TryGetValue(chunkKey, out diffs))
                return false;

            index = IndexOf(diffs, localIndex);
            return index >= 0;
        }

        /// <summary>
        /// Linear scan on purpose. Per-chunk diff lists are small — a 64m chunk holds at
        /// most 64 trees — so a dictionary per chunk would cost more in allocation and
        /// indirection than it saves in lookups.
        /// </summary>
        private static int IndexOf(List<TreeDiff> diffs, ushort localIndex)
        {
            for (int i = 0; i < diffs.Count; i++)
            {
                if (diffs[i].LocalIndex == localIndex)
                    return i;
            }

            return -1;
        }
    }
}
