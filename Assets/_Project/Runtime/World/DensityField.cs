using UnityEngine;

namespace ChopChop.World
{
    /// <summary>
    /// Reads local tree density anywhere in the world (TECH 12.1).
    ///
    /// The grid is a byproduct of generation, so this is one array read per sample —
    /// **no raycasts, no collider queries, no per-tree lookups** (TECH 12.2). That budget
    /// is the whole reason darkness can run every frame.
    ///
    /// Sampling crosses chunk boundaries properly: each of the four cells around a point
    /// is fetched from whichever chunk owns it. Clamping inside a single chunk instead
    /// would put a visible seam every 64 metres, which reads as a lighting glitch rather
    /// than as forest.
    /// </summary>
    public sealed class DensityField
    {
        private readonly ChunkStore _store;

        public DensityField(ChunkStore store)
        {
            _store = store;
        }

        /// <summary>
        /// Density at a world position, roughly 0 (open ground) to 1 (dense canopy).
        /// Unloaded chunks read as open, so the world fades toward light at the edge of
        /// what is resident rather than snapping to black.
        /// </summary>
        public float Sample(Vector3 world)
        {
            const float cell = ChunkData.DensityCellSize;

            // Half-cell offset so values sit at cell centres rather than corners.
            float fx = world.x / cell - 0.5f;
            float fz = world.z / cell - 0.5f;

            int x0 = Mathf.FloorToInt(fx);
            int z0 = Mathf.FloorToInt(fz);

            float tx = fx - x0;
            float tz = fz - z0;

            float bottom = Mathf.Lerp(CellValue(x0, z0), CellValue(x0 + 1, z0), tx);
            float top = Mathf.Lerp(CellValue(x0, z0 + 1), CellValue(x0 + 1, z0 + 1), tx);

            return Mathf.Lerp(bottom, top, tz);
        }

        /// <summary>One cell in world-grid space, resolved to whichever chunk holds it.</summary>
        private float CellValue(int globalX, int globalZ)
        {
            const int res = ChunkData.DensityResolution;

            // Floor division, so negative coordinates land in the chunk left of origin
            // rather than being pulled back toward it.
            int chunkX = Mathf.FloorToInt((float)globalX / res);
            int chunkZ = Mathf.FloorToInt((float)globalZ / res);

            if (!_store.TryGet(chunkX, chunkZ, out ChunkData chunk))
                return 0f;

            int localX = globalX - chunkX * res;
            int localZ = globalZ - chunkZ * res;

            return chunk.Density[localZ * res + localX];
        }
    }
}
