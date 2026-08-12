using UnityEngine;

namespace ChopChop.World
{
    /// <summary>
    /// Everything generation produces for one chunk. All of it is reproducible from
    /// <c>(seed, chunkCoord, biomes)</c>, so none of it is ever saved or networked.
    /// </summary>
    public sealed class ChunkData
    {
        /// <summary>Cell size of the density grid, in metres (TECH 12.1).</summary>
        public const int DensityCellSize = 4;

        /// <summary>16 across a 64m chunk.</summary>
        public const int DensityResolution = WorldConstants.ChunkSize / DensityCellSize;

        public readonly int ChunkX;
        public readonly int ChunkZ;

        public readonly GeneratedTree[] Trees;

        /// <summary>
        /// Local tree density per cell, roughly 0–1, row-major as <c>z * res + x</c>.
        ///
        /// Baked here rather than measured later because it is a byproduct of placing
        /// trees — the generator already knows where they went. The darkness system then
        /// costs one array read per frame instead of a spatial query (TECH 12.2).
        /// </summary>
        public readonly float[] Density;

        public ChunkData(int chunkX, int chunkZ, GeneratedTree[] trees, float[] density)
        {
            ChunkX = chunkX;
            ChunkZ = chunkZ;
            Trees = trees;
            Density = density;
        }

        public long Key => ChunkKey.Pack(ChunkX, ChunkZ);

        /// <summary>World-space position of the chunk's minimum corner.</summary>
        public Vector3 Origin => new(ChunkX * WorldConstants.ChunkSize, 0f, ChunkZ * WorldConstants.ChunkSize);

        public Vector3 WorldPositionOf(int treeIndex) => Origin + Trees[treeIndex].LocalPosition;

        /// <summary>
        /// Bilinear density sample from a position local to this chunk. Bilinear rather
        /// than nearest so walking a cell boundary does not step the lighting.
        /// </summary>
        public float SampleDensity(Vector3 localPosition)
        {
            float fx = Mathf.Clamp(localPosition.x / DensityCellSize - 0.5f, 0f, DensityResolution - 1f);
            float fz = Mathf.Clamp(localPosition.z / DensityCellSize - 0.5f, 0f, DensityResolution - 1f);

            int x0 = Mathf.FloorToInt(fx);
            int z0 = Mathf.FloorToInt(fz);
            int x1 = Mathf.Min(x0 + 1, DensityResolution - 1);
            int z1 = Mathf.Min(z0 + 1, DensityResolution - 1);

            float tx = fx - x0;
            float tz = fz - z0;

            float bottom = Mathf.Lerp(Density[z0 * DensityResolution + x0], Density[z0 * DensityResolution + x1], tx);
            float top = Mathf.Lerp(Density[z1 * DensityResolution + x0], Density[z1 * DensityResolution + x1], tx);

            return Mathf.Lerp(bottom, top, tz);
        }
    }
}
