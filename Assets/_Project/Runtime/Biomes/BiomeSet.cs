using System;
using UnityEngine;

namespace ChopChop.Biomes
{
    /// <summary>
    /// The ordered rings that make up a world, innermost first.
    ///
    /// This is a generation input, so it is part of what "the same world" means: the
    /// same seed with a different set produces a different forest (TECH 2.6). Treat
    /// changes to it the way you would treat changes to the algorithm.
    /// </summary>
    [CreateAssetMenu(menuName = "ChopChop/Biome Set", fileName = "BiomeSet")]
    public sealed class BiomeSet : ScriptableObject
    {
        [Tooltip("Ordered by InnerRadius, innermost first. Sorted on validate.")]
        [SerializeField] private BiomeDefinition[] _biomes = Array.Empty<BiomeDefinition>();

        public int Count => _biomes.Length;
        public BiomeDefinition this[int index] => _biomes[index];

        /// <summary>
        /// The ring a point belongs to for gameplay purposes. Discrete on purpose:
        /// unlocks, spawn floors and difficulty use a hard index so a player cannot
        /// straddle a boundary and get the better half of both (TECH 13).
        /// </summary>
        public int GetRingIndex(float distanceFromOrigin)
        {
            int ring = 0;

            for (int i = 0; i < _biomes.Length; i++)
            {
                if (_biomes[i] != null && distanceFromOrigin >= _biomes[i].InnerRadius)
                    ring = i;
            }

            return ring;
        }

        /// <summary>
        /// Resolves the ring at a distance, plus how much the previous ring still bleeds
        /// in. Only appearance and spawn weights use this; gameplay uses
        /// <see cref="GetRingIndex"/>. Blended visuals, discrete logic — much easier to
        /// reason about, and it stops threat type snapping at an invisible line.
        /// </summary>
        /// <param name="blend">
        /// 0 means fully <paramref name="current"/>; 1 means fully
        /// <paramref name="previous"/>. Always 0 when there is no previous ring.
        /// </param>
        public void Resolve(float distanceFromOrigin, out BiomeDefinition current,
            out BiomeDefinition previous, out float blend)
        {
            current = null;
            previous = null;
            blend = 0f;

            if (_biomes.Length == 0)
                return;

            int ring = GetRingIndex(distanceFromOrigin);
            current = _biomes[ring];

            if (ring == 0 || current == null)
                return;

            previous = _biomes[ring - 1];

            if (previous == null || current.BlendBandWidth <= 0f)
                return;

            // Full previous at the inner edge, fading to none a band-width later.
            float intoRing = distanceFromOrigin - current.InnerRadius;
            blend = Mathf.Clamp01(1f - intoRing / current.BlendBandWidth);
        }

        private void OnValidate()
        {
            /* Sorted here rather than trusted from the inspector: resolution walks the
             * array in order, and an out-of-order entry would silently produce a world
             * whose rings do not match their radii. */
            Array.Sort(_biomes, (a, b) =>
            {
                if (a == null)
                    return b == null ? 0 : 1;
                if (b == null)
                    return -1;

                return a.InnerRadius.CompareTo(b.InnerRadius);
            });
        }
    }
}
