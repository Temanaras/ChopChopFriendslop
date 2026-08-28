using TMPro;
using UnityEngine;

namespace ChopChop.UI
{
    /// <summary>
    /// Every colour and font the UI is allowed to use.
    ///
    /// An asset rather than constants so the look can be moved without a recompile, and
    /// so there is one place to answer "what colour is a tier-2 item" — which is asked by
    /// the inventory, the paperdoll, the chest and the chop feedback, and would otherwise
    /// be answered four times slightly differently.
    /// </summary>
    [CreateAssetMenu(menuName = "ChopChop/UI Theme", fileName = "UiTheme")]
    public sealed class UiTheme : ScriptableObject
    {
        [Header("Surfaces")]
        public Color Panel = new(0.06f, 0.06f, 0.07f, 0.94f);
        public Color PanelHeader = new(0.10f, 0.10f, 0.12f, 1f);
        public Color Slot = new(0.14f, 0.14f, 0.16f, 1f);
        public Color SlotHover = new(0.22f, 0.22f, 0.25f, 1f);
        public Color SlotAccepting = new(0.20f, 0.34f, 0.22f, 1f);
        public Color SlotRefusing = new(0.36f, 0.16f, 0.16f, 1f);

        [Header("Text")]
        public Color Text = new(0.92f, 0.91f, 0.88f, 1f);
        public Color TextDim = new(0.62f, 0.61f, 0.58f, 1f);
        public TMP_FontAsset Font;

        [Header("Vitals")]
        public Color Health = new(0.72f, 0.22f, 0.20f, 1f);
        public Color HealthBacking = new(0.16f, 0.08f, 0.08f, 0.85f);
        public Color Ammo = new(0.85f, 0.76f, 0.48f, 1f);

        [Header("Chop grades")]
        /* Green reads as the best outcome and yellow as the acceptable one, so the
         * innermost band is the green. Perfect is the more saturated of the two on
         * purpose: it is the smallest band on screen and has to hold its own against the
         * yellow surrounding it on both sides. */
        public Color Perfect = new(0.35f, 0.82f, 0.40f, 1f);
        public Color Good = new(0.94f, 0.83f, 0.35f, 1f);
        public Color Bad = new(0.55f, 0.50f, 0.42f, 1f);
        public Color Miss = new(0.30f, 0.14f, 0.14f, 1f);

        [Header("Item tiers")]
        [Tooltip("Indexed by ItemDefinition.Tier. The last entry covers everything beyond " +
                 "the end, so adding a tier does not require editing this to avoid a hole.")]
        public Color[] Tiers =
        {
            new(0.62f, 0.61f, 0.58f, 1f),
            new(0.55f, 0.72f, 0.60f, 1f),
            new(0.50f, 0.64f, 0.82f, 1f),
            new(0.72f, 0.55f, 0.82f, 1f),
        };

        public Color ForTier(byte tier)
        {
            if (Tiers == null || Tiers.Length == 0)
                return Text;

            return Tiers[Mathf.Min(tier, Tiers.Length - 1)];
        }

        public Color ForGrade(int grade) => grade switch
        {
            3 => Perfect,
            2 => Good,
            1 => Bad,
            _ => Miss,
        };
    }
}
