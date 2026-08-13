using System.Collections.Generic;
using UnityEngine;

namespace ChopChop.World
{
    /// <summary>
    /// Keeps real colliders around a set of points, pooled (TECH 5.4).
    ///
    /// This is the only place a tree becomes a physical object, and the band is
    /// deliberately small — chopping needs something to raycast against, and nothing else
    /// does. Everything further out is drawn as instanced geometry with no collider at
    /// all, which is what keeps tens of thousands of trees affordable.
    ///
    /// Used by both sides for different reasons: a client keeps colliders around itself
    /// so its own raycasts hit, and the server keeps them around *every* player because
    /// it has to validate what they chop.
    /// </summary>
    public sealed class TreeColliderBand
    {
        private readonly Transform _parent;
        private readonly float _radius;
        private readonly Stack<TreeCollider> _pool = new();

        /* Keyed by (chunk, index) rather than a packed long. The chunk key already uses
         * all 64 bits, so shifting it to make room for the index silently drops the top
         * of the x coordinate and lets two distant chunks share a slot. */
        private readonly Dictionary<(long chunkKey, ushort localIndex), TreeCollider> _active = new();
        private readonly List<(long chunkKey, ushort localIndex)> _evictionScratch = new();

        /// <summary>Trees currently carrying a collider.</summary>
        public int ActiveCount => _active.Count;

        public int PooledCount => _pool.Count;

        public TreeColliderBand(Transform parent, float radius)
        {
            _parent = parent;
            _radius = radius;
        }

        /// <summary>
        /// Brings colliders into being around <paramref name="centres"/> and returns
        /// anything outside to the pool.
        /// </summary>
        /// <param name="diffs">
        /// Consulted so felled trees get no collider. A stump you can still chop is worse
        /// than no stump at all.
        /// </param>
        public void Update(IEnumerable<ChunkData> chunks, IReadOnlyList<Vector3> centres, TreeDiffStore diffs)
        {
            _wanted.Clear();

            float sqrRadius = _radius * _radius;

            foreach (ChunkData chunk in chunks)
            {
                Vector3 origin = chunk.Origin;

                for (int i = 0; i < chunk.Trees.Length; i++)
                {
                    if (diffs != null && diffs.IsFelled(chunk.Key, (ushort)i))
                        continue;

                    Vector3 world = origin + chunk.Trees[i].LocalPosition;

                    if (!IsNearAnyCentre(world, centres, sqrRadius))
                        continue;

                    var id = (chunk.Key, (ushort)i);
                    _wanted.Add(id);

                    if (_active.ContainsKey(id))
                        continue;

                    TreeCollider collider = Rent();
                    collider.Bind(new TreeId(chunk.Key, (ushort)i));
                    collider.transform.SetPositionAndRotation(
                        world, Quaternion.Euler(0f, chunk.Trees[i].YRotation, 0f));
                    collider.transform.localScale = Vector3.one * chunk.Trees[i].Scale;
                    collider.gameObject.SetActive(true);

                    _active[id] = collider;
                }
            }

            _evictionScratch.Clear();

            foreach (var pair in _active)
            {
                if (!_wanted.Contains(pair.Key))
                    _evictionScratch.Add(pair.Key);
            }

            for (int i = 0; i < _evictionScratch.Count; i++)
                Release(_evictionScratch[i]);
        }

        /// <summary>Immediately removes one tree's collider, e.g. when it is felled.</summary>
        public void Remove(long chunkKey, ushort localIndex) => Release((chunkKey, localIndex));

        public void Clear()
        {
            _evictionScratch.Clear();
            foreach (var id in _active.Keys)
                _evictionScratch.Add(id);

            for (int i = 0; i < _evictionScratch.Count; i++)
                Release(_evictionScratch[i]);
        }

        private static bool IsNearAnyCentre(Vector3 position, IReadOnlyList<Vector3> centres, float sqrRadius)
        {
            for (int i = 0; i < centres.Count; i++)
            {
                // Horizontal distance: a player on a hill above a tree is still next to it.
                float dx = position.x - centres[i].x;
                float dz = position.z - centres[i].z;

                if (dx * dx + dz * dz <= sqrRadius)
                    return true;
            }

            return false;
        }

        private TreeCollider Rent()
        {
            if (_pool.Count > 0)
                return _pool.Pop();

            GameObject go = new("TreeCollider");
            go.transform.SetParent(_parent, false);

            /* A capsule rather than a mesh collider: the trunk is what players aim at,
             * and a primitive is far cheaper to move in and out of the physics scene
             * hundreds of times a minute. */
            CapsuleCollider capsule = go.AddComponent<CapsuleCollider>();
            capsule.height = 6f;
            capsule.radius = 0.4f;
            capsule.center = new Vector3(0f, 3f, 0f);

            return go.AddComponent<TreeCollider>();
        }

        private void Release((long chunkKey, ushort localIndex) id)
        {
            if (!_active.TryGetValue(id, out TreeCollider collider))
                return;

            _active.Remove(id);

            // Deactivated rather than destroyed: churn here is constant as players walk,
            // and allocating a GameObject per step would be the whole budget.
            collider.gameObject.SetActive(false);
            _pool.Push(collider);
        }

        private readonly HashSet<(long chunkKey, ushort localIndex)> _wanted = new();
    }
}
