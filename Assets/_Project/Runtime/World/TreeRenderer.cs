using System.Collections.Generic;
using ChopChop.Biomes;
using UnityEngine;

namespace ChopChop.World
{
    /// <summary>
    /// Draws the visual band of trees with GPU instancing (TECH 5.4).
    ///
    /// **No GameObjects.** A GameObject per tree fails at a few hundred instances and the
    /// target is tens of thousands, so the visual band is drawn straight from generated
    /// data with nothing in the scene at all. Colliders only exist in the much smaller
    /// active band, and those arrive with chopping.
    ///
    /// Purely presentation, and client-only — a headless server never draws a tree.
    /// </summary>
    public sealed class TreeRenderer
    {
        /// <summary>
        /// Unity's per-call instancing ceiling. Larger batches are silently truncated,
        /// which shows up as trees missing from the far side of a chunk.
        /// </summary>
        private const int MaxInstancesPerBatch = 1023;

        private readonly BiomeSet _biomes;

        /// <summary>Matrices grouped by species, reused every frame rather than reallocated.</summary>
        private readonly Dictionary<byte, List<Matrix4x4>> _bySpecies = new();

        private readonly Matrix4x4[] _batch = new Matrix4x4[MaxInstancesPerBatch];

        public int LastDrawCallCount { get; private set; }
        public int LastInstanceCount { get; private set; }

        public TreeRenderer(BiomeSet biomes)
        {
            _biomes = biomes;
        }

        /// <summary>
        /// Draws every loaded chunk for this frame. Immediate mode: nothing persists
        /// between frames, so a chunk that stops being resident simply stops being drawn.
        /// </summary>
        /// <param name="diffs">
        /// Consulted so felled trees are not drawn. Generation is pure and never forgets
        /// a tree (TECH 2.6), so the diff store is the only thing that knows a tree is
        /// gone — without this a chopped tree keeps standing, visible and intangible,
        /// because the collider band checks the diffs and this did not.
        /// </param>
        public void Render(IEnumerable<ChunkData> chunks, TreeDiffStore diffs, Camera camera)
        {
            foreach (List<Matrix4x4> list in _bySpecies.Values)
                list.Clear();

            Bounds bounds = new(Vector3.zero, Vector3.zero);
            bool hasBounds = false;

            foreach (ChunkData chunk in chunks)
            {
                Vector3 origin = chunk.Origin;
                long chunkKey = chunk.Key;

                for (int i = 0; i < chunk.Trees.Length; i++)
                {
                    if (diffs != null && diffs.IsFelled(chunkKey, (ushort)i))
                        continue;

                    GeneratedTree tree = chunk.Trees[i];
                    Vector3 world = origin + tree.LocalPosition;

                    if (!_bySpecies.TryGetValue(tree.SpeciesIndex, out List<Matrix4x4> list))
                    {
                        list = new List<Matrix4x4>();
                        _bySpecies[tree.SpeciesIndex] = list;
                    }

                    list.Add(Matrix4x4.TRS(
                        world,
                        Quaternion.Euler(0f, tree.YRotation, 0f),
                        Vector3.one * tree.Scale));

                    if (hasBounds)
                    {
                        bounds.Encapsulate(world);
                    }
                    else
                    {
                        bounds = new Bounds(world, Vector3.one);
                        hasBounds = true;
                    }
                }
            }

            LastDrawCallCount = 0;
            LastInstanceCount = 0;

            if (!hasBounds)
                return;

            // Instances are not individually culled, so the bounds have to cover them all
            // or Unity culls the whole batch the moment the centre leaves the frustum.
            bounds.Expand(20f);

            foreach (KeyValuePair<byte, List<Matrix4x4>> pair in _bySpecies)
            {
                if (pair.Value.Count == 0)
                    continue;

                if (!TryGetSpecies(pair.Key, out Mesh mesh, out Material[] materials))
                    continue;

                /* A submesh at a time, each with its own material. Tree models split
                 * trunk from canopy, so drawing only submesh 0 leaves a bare pole
                 * standing where a tree should be. */
                int submeshes = Mathf.Min(mesh.subMeshCount, materials.Length);

                for (int submesh = 0; submesh < submeshes; submesh++)
                {
                    if (materials[submesh] == null)
                        continue;

                    RenderParams parameters = new(materials[submesh])
                    {
                        worldBounds = bounds,
                        shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On,
                        receiveShadows = true,
                        camera = camera,
                    };

                    DrawBatched(in parameters, mesh, submesh, pair.Value);
                }
            }
        }

        private void DrawBatched(in RenderParams parameters, Mesh mesh, int submesh, List<Matrix4x4> instances)
        {
            for (int start = 0; start < instances.Count; start += MaxInstancesPerBatch)
            {
                int count = Mathf.Min(MaxInstancesPerBatch, instances.Count - start);

                for (int i = 0; i < count; i++)
                    _batch[i] = instances[start + i];

                Graphics.RenderMeshInstanced(parameters, mesh, submesh, _batch, count);

                LastDrawCallCount++;
                LastInstanceCount += count;
            }
        }

        /// <summary>
        /// Species index is the tree's slot in its biome's entry list. One biome for now;
        /// this needs a proper registry once rings blend species across boundaries.
        /// </summary>
        private bool TryGetSpecies(byte speciesIndex, out Mesh mesh, out Material[] materials)
        {
            mesh = null;
            materials = null;

            if (_biomes == null || _biomes.Count == 0)
                return false;

            BiomeDefinition biome = _biomes[0];

            if (biome == null || speciesIndex >= biome.Trees.Length)
                return false;

            mesh = biome.Trees[speciesIndex].Mesh;
            materials = biome.Trees[speciesIndex].Materials;

            return mesh != null && materials != null && materials.Length > 0;
        }
    }
}
