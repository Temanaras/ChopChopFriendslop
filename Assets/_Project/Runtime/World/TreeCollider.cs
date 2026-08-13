using UnityEngine;

namespace ChopChop.World
{
    /// <summary>
    /// Identifies a tree: which chunk, and which slot in that chunk's generated array
    /// (TECH 5.2). This is the only handle that crosses the network.
    /// </summary>
    public readonly struct TreeId
    {
        public readonly long ChunkKey;
        public readonly ushort LocalIndex;

        public TreeId(long chunkKey, ushort localIndex)
        {
            ChunkKey = chunkKey;
            LocalIndex = localIndex;
        }

        public override string ToString()
        {
            // Qualified because the field name shadows the ChunkKey helper class.
            World.ChunkKey.Unpack(ChunkKey, out int x, out int z);
            return $"tree {LocalIndex} in chunk ({x},{z})";
        }
    }

    /// <summary>
    /// Sits on a pooled collider in the active band and says which tree it is.
    ///
    /// This is what a chop raycast hits, and reading the id off it is step one of the
    /// chopping flow (TECH 5.6). The GameObject is transient — it exists only while a
    /// player is close enough — so nothing may ever store state here that matters.
    /// </summary>
    public sealed class TreeCollider : MonoBehaviour
    {
        public TreeId Id { get; private set; }

        public void Bind(TreeId id) => Id = id;
    }
}
