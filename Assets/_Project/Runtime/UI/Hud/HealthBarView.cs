using ChopChop.Combat;
using ChopChop.Player;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ChopChop.UI
{
    /// <summary>
    /// How much of the player is left.
    ///
    /// Reads <see cref="Health.Normalized"/> rather than doing the division here: maximum
    /// is prefab-authored and not a SyncVar, so it is identical on every machine and the
    /// component is entitled to answer that question itself.
    ///
    /// The fill eases toward the true value instead of snapping. Not decoration — a bar
    /// that jumps has no direction, and the thing a player needs to read in the half
    /// second after being hit is *how fast* they are losing, not the new number.
    /// </summary>
    public sealed class HealthBarView : PlayerBoundView
    {
        [SerializeField] private Image _fill;
        [SerializeField] private TMP_Text _label;
        [SerializeField] private CanvasGroup _group;

        [Tooltip("Seconds for the bar to catch up to a change. Zero snaps.")]
        [SerializeField] private float _easeSeconds = 0.25f;

        [Tooltip("Fraction of maximum below which the bar reads as critical.")]
        [Range(0f, 1f)][SerializeField] private float _criticalAt = 0.3f;

        [SerializeField] private UiTheme _theme;

        private float _shown = 1f;
        private float _target = 1f;

        protected override void Bind(LocalPlayer player)
        {
            if (player.Health == null)
                return;

            player.Health.Changed += HandleChanged;

            // Bind, then read: joining mid-session means the last Changed already fired.
            _target = player.Health.Normalized;
            _shown = _target;
            Apply();
        }

        protected override void Unbind(LocalPlayer player)
        {
            if (player.Health != null)
                player.Health.Changed -= HandleChanged;
        }

        private void HandleChanged(ushort previous, ushort next)
        {
            if (Player?.Health == null)
                return;

            _target = Player.Health.Normalized;

            if (_label != null)
                _label.text = $"{next} / {Player.Health.Maximum}";
        }

        private void Update()
        {
            // Hidden rather than left showing a stale bar when there is no player yet.
            if (_group != null)
                _group.alpha = Player != null ? 1f : 0f;

            if (Mathf.Approximately(_shown, _target))
                return;

            _shown = _easeSeconds <= 0f
                ? _target
                : Mathf.MoveTowards(_shown, _target, Time.deltaTime / _easeSeconds);

            Apply();
        }

        private void Apply()
        {
            if (_fill == null)
                return;

            _fill.fillAmount = _shown;

            if (_theme != null)
                _fill.color = _shown <= _criticalAt
                    ? Color.Lerp(_theme.Health, Color.white, 0.35f)
                    : _theme.Health;
        }
    }
}
