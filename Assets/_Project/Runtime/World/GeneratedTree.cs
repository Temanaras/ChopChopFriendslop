using UnityEngine;

namespace ChopChop.World
{
    /// <summary>
    /// One generated tree (TECH 5.2). **Generated, never saved** — regenerated from the
    /// seed every time a chunk loads, with only player-made deviations stored as
    /// <see cref="TreeDiff"/>.
    ///
    /// That is what makes tens of thousands of trees affordable, and it is why these are
    /// not <c>NetworkObject</c>s and never will be (TECH 2.2).
    ///
    /// A tree is addressed by <c>(chunkCoord, localIndex)</c>, where the index is its
    /// position in the chunk's generated array. Any change to generation therefore
    /// invalidates saved diffs — see <c>SaveFormat.WorldGenVersion</c>.
    /// </summary>
    public struct GeneratedTree
    {
        /// <summary>Relative to the chunk origin, so a chunk is position-independent.</summary>
        public Vector3 LocalPosition;

        public float YRotation;
        public float Scale;

        /// <summary>
        /// Which tool tier is needed to fell this. Biome blending decides the
        /// *probability* of a tier, but once generated the tree carries it regardless of
        /// which ring it physically sits in — an early high-tier tree is a locked
        /// teaser, not a balance leak (TECH 5.2).
        /// </summary>
        public byte TierIndex;

        /// <summary>Visual variant within the tier. No gameplay meaning.</summary>
        public byte SpeciesIndex;

        public GeneratedTree(Vector3 localPosition, float yRotation, float scale,
            byte tierIndex, byte speciesIndex)
        {
            LocalPosition = localPosition;
            YRotation = yRotation;
            Scale = scale;
            TierIndex = tierIndex;
            SpeciesIndex = speciesIndex;
        }
    }
}
