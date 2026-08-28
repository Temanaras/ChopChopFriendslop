using ChopChop.Core;
using ChopChop.Player;
using ChopChop.World;
using UnityEngine;
using UnityEngine.UI;

namespace ChopChop.UI
{
    /// <summary>
    /// The chopping timing game, drawn.
    ///
    /// A semicircle with the pointer running up and down its curved edge. One half is the
    /// target and carries Perfect / Good / Bad bands; the other half is a miss. The active
    /// half swaps as the wedge opens, so the cut alternates sides the way a lumberjack's
    /// does.
    ///
    /// **Draws only. It decides nothing.** Every number here comes from
    /// <see cref="ChopMeter"/>, evaluated against the synced network tick, and the server
    /// evaluates the same function for the tick the swing claims. What the player sees and
    /// what the server grades are the same computation, which is the only reason this can
    /// exist without weakening TECH 2.1.
    ///
    /// Bands are radial-filled Images rather than a generated mesh: an arc is exactly what
    /// <see cref="Image.Type.Filled"/> already draws, and a custom mesh would be a
    /// rebuild-per-frame for a shape uGUI can express.
    /// </summary>
    public sealed class ChopMeterView : PlayerBoundView
    {
        [Header("Wiring")]
        [SerializeField] private CanvasGroup _group;
        [SerializeField] private RectTransform _pointer;
        [SerializeField] private Image _missHalf;
        [SerializeField] private Image _badBand;
        [SerializeField] private Image _goodBand;
        [SerializeField] private Image _perfectBand;
        [SerializeField] private UiTheme _theme;

        [Header("Geometry")]
        [Tooltip("Degrees the pointer sweeps across, centred on the right of the circle. " +
                 "180 is the full semicircle: straight down to straight up.")]
        [SerializeField] private float _arcDegrees = 180f;

        [Tooltip("Seconds to fade in and out. The meter appearing instantly on every " +
                 "glance at a trunk reads as a flicker.")]
        [SerializeField] private float _fadeSeconds = 0.12f;

        private TreeClient _trees;
        private TreeDiffStore _diffs;
        private float _shownAlpha;

        protected override void Bind(LocalPlayer player) { }
        protected override void Unbind(LocalPlayer player) { }

        private void Update()
        {
            bool visible = TryDraw();

            if (_group == null)
                return;

            _shownAlpha = _fadeSeconds <= 0f
                ? (visible ? 1f : 0f)
                : Mathf.MoveTowards(_shownAlpha, visible ? 1f : 0f, Time.deltaTime / _fadeSeconds);

            _group.alpha = _shownAlpha;
        }

        /// <returns>Whether there is anything worth showing.</returns>
        private bool TryDraw()
        {
            PlayerChopper chopper = Player?.Chopper;

            if (chopper == null || !chopper.TryGetTarget(out TreeId target, out byte treeTier))
                return false;

            if (!Resolve())
                return false;

            byte axeTier = Player.Paperdoll != null ? Player.Paperdoll.AxeTier : (byte)0;

            /* Below tier the tree cannot be damaged at all, so there is no timing to play
             * (DESIGN 8.4). Showing a meter that cannot succeed would be the opposite of
             * the legible failure that section asks for — the tier feedback says the
             * useful thing instead. */
            if (axeTier < treeTier)
                return false;

            ChopMeterSettings settings = ChopMeterSettings.For(axeTier, treeTier);

            byte health = _diffs.GetHealth(target.ChunkKey, target.LocalIndex);
            bool topActive = ChopMeter.TopHalfActive(health);
            float phase = ChopMeter.Phase(_trees.Tick, _trees.TickRate, target, settings);

            LayoutBands(topActive, settings);
            PlacePointer(phase);
            return true;
        }

        private bool Resolve()
        {
            if (_trees == null)
                ServiceLocator.TryGet(out _trees);

            if (_diffs == null)
                ServiceLocator.TryGet(out _diffs);

            return _trees != null && _diffs != null;
        }

        /// <summary>
        /// Points the bands at the live half and puts the miss zone on the other.
        ///
        /// The pointer only ever travels the right-hand half of the circle — 6 o'clock up
        /// through 3 o'clock to 12 o'clock. So a "half of the arc" is a *quarter* of the
        /// circle, and a band spanning the whole half is a 0.25 fill, not 0.5. Getting
        /// this wrong paints a full pie and puts the miss zone on the left, where the
        /// pointer never goes and nothing can ever be missed.
        ///
        /// **Three images, five bands.** Perfect sits in the middle of the active half,
        /// so Good and Bad each appear twice — once either side of it. Painting them
        /// widest-first and letting the narrower ones cover the middle produces
        /// <c>Bad | Good | Perfect | Good | Bad</c> out of three concentric spans, rather
        /// than five separately positioned slivers that would each need their own angle.
        /// </summary>
        private void LayoutBands(bool topActive, in ChopMeterSettings settings)
        {
            SetBand(_badBand, topActive, 0.5f, _theme != null ? _theme.Bad : Color.grey);
            SetBand(_goodBand, topActive, settings.GoodHalfWidth, _theme != null ? _theme.Good : Color.green);
            SetBand(_perfectBand, topActive, settings.PerfectHalfWidth, _theme != null ? _theme.Perfect : Color.yellow);

            // The dead half, on whichever side is not currently the target.
            SetBand(_missHalf, !topActive, 0.5f, _theme != null ? _theme.Miss : Color.black);
        }

        /// <summary>
        /// Draws one band centred on the middle of its half, reaching
        /// <paramref name="halfWidth"/> in phase units either side. A half-width of 0.5
        /// therefore covers the whole half.
        ///
        /// A radial fill grows from its origin, so a *centred* span needs the image
        /// rotated until its origin sits at the band's leading edge. That is the one
        /// place a rotation is legitimate here — the fill still describes the width, and
        /// the rotation only describes where it starts.
        /// </summary>
        private static void SetBand(Image band, bool topHalf, float halfWidth, Color colour)
        {
            if (band == null)
                return;

            float width = Mathf.Clamp(halfWidth, 0f, 0.5f);

            /* Distance from the tip of the half to the band's leading edge, in phase
             * units. The leading edge is the end nearer the tip, because both fills below
             * run from their tip toward the middle of the sweep. */
            float lead = 0.5f - width;
            float degrees = lead * QuarterDegrees;

            /* Clockwise from 12 o'clock reaches 3 o'clock; anticlockwise from 6 o'clock
             * also reaches 3 o'clock. So each half fills from its own tip inward, and the
             * rotation that offsets the start mirrors with it. */
            band.fillOrigin = (int)(topHalf ? Image.Origin360.Top : Image.Origin360.Bottom);
            band.fillClockwise = topHalf;
            band.fillAmount = QuarterTurn * 2f * width;
            band.transform.localRotation = Quaternion.Euler(0f, 0f, topHalf ? -degrees : degrees);
            band.color = colour;
        }

        /// <summary>A quarter of the circle: one half of the pointer's 180-degree sweep.</summary>
        private const float QuarterTurn = 0.25f;

        private const float QuarterDegrees = 90f;

        private void PlacePointer(float phase)
        {
            if (_pointer == null)
                return;

            // phase is -1 at the bottom of the arc and +1 at the top.
            float half = _arcDegrees * 0.5f;
            _pointer.localRotation = Quaternion.Euler(0f, 0f, phase * half);
        }
    }
}
