using System;
using UnityEngine;

namespace ChopChop.Biomes
{
    /// <summary>
    /// One kind of tree a biome can produce.
    /// </summary>
    /// <remarks>
    /// Weight and tier are generation inputs and must stay stable — changing either
    /// changes what the same seed produces, which invalidates saved tree diffs. Mesh and
    /// material are presentation only and can be swapped freely.
    /// </remarks>
    [Serializable]
    public struct TreeSpawnEntry
    {
        public Mesh Mesh;
        public Material Material;

        [Tooltip("Tool tier required to fell this. Intrinsic to the tree once generated.")]
        public byte Tier;

        [Tooltip("Relative chance against the other entries in this biome.")]
        [Min(0f)] public float Weight;

        [Tooltip("Uniform scale range, x to y.")]
        public Vector2 ScaleRange;
    }

    /// <summary>
    /// A ring of the world (TECH 13). Ring count is unbounded — stacking another one of
    /// these adds a ring with no code changes, which is the whole reason this is data
    /// rather than a switch statement.
    /// </summary>
    [CreateAssetMenu(menuName = "ChopChop/Biome Definition", fileName = "Biome")]
    public sealed class BiomeDefinition : ScriptableObject
    {
        [Header("Placement")]
        [Tooltip("0 is the cabin ring. Used directly for unlocks and difficulty.")]
        public int RingIndex;

        [Tooltip("Distance from the world origin where this ring starts, in metres.")]
        [Min(0f)] public float InnerRadius;

        [Tooltip("How far the previous ring bleeds into this one. Appearance and spawn " +
                 "weights blend across this band; gameplay ring index does not (TECH 13).")]
        [Min(0f)] public float BlendBandWidth = 30f;

        [Header("Trees")]
        public TreeSpawnEntry[] Trees = Array.Empty<TreeSpawnEntry>();

        [Tooltip("Trees per chunk at full density, before masking. A chunk is 64m square. " +
                 "The generator's placement grid caps this at 64; values above that " +
                 "silently produce fewer trees than asked for.")]
        [Range(0f, 64f)] public float BaseDensity = 40f;

        [Header("Regrowth")]
        [Tooltip("Fraction of a felled tree reclaimed per tick while nobody is subscribed. " +
                 "Outer rings reclaim faster, so deep territory is hard to hold (TECH 7.2).")]
        [Min(0f)] public float RegrowthRatePerTick = 0.0001f;

        [Header("Atmosphere")]
        [Tooltip("Maps local tree density to darkness. Client-local; must be tuned by eye.")]
        public AnimationCurve DensityToDarkness = AnimationCurve.Linear(0f, 0f, 1f, 1f);

        [Header("Threat")]
        [Tooltip("Spawn pressure that survives clear-cutting. Without a floor, a stripped " +
                 "ring becomes permanently safe and the horror evaporates (TECH 10.4).")]
        [Min(0f)] public float SpawnRateFloor = 0.1f;

        /// <summary>Sum of tree weights. Zero means this biome grows nothing.</summary>
        public float TotalTreeWeight
        {
            get
            {
                float total = 0f;

                for (int i = 0; i < Trees.Length; i++)
                    total += Mathf.Max(0f, Trees[i].Weight);

                return total;
            }
        }
    }
}
