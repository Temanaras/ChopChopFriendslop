using ChopChop.Core;
using ChopChop.Items;
using ChopChop.Player;
using ChopChop.World;
using TMPro;
using UnityEngine;

namespace ChopChop.UI
{
    /// <summary>
    /// Says what just happened to the axe.
    ///
    /// Two jobs, both of which the game was previously silent about:
    ///
    /// **Grades.** "Perfect" over the trunk, so the timing game is legible. Without it a
    /// player has a meter and no confirmation that hitting it did anything.
    ///
    /// **The tier wall.** DESIGN 8.4 is explicit: a tool below tier does zero damage, and
    /// because there is no grind-through escape, the failure has to say what would unlock
    /// it. Naming the axe by looking the tier up in the registry means adding a tier is
    /// still just authoring an <see cref="ItemDefinition"/>.
    /// </summary>
    public sealed class ChopFeedbackView : PlayerBoundView
    {
        [SerializeField] private TMP_Text _grade;
        [SerializeField] private TMP_Text _message;
        [SerializeField] private CanvasGroup _group;
        [SerializeField] private UiTheme _theme;

        [Tooltip("Seconds a grade stays up. Short: it is a confirmation, not a scoreboard.")]
        [SerializeField] private float _gradeSeconds = 0.6f;

        [Tooltip("Seconds a refusal stays up. Longer, because it has to be read.")]
        [SerializeField] private float _messageSeconds = 2.5f;

        private TreeClient _trees;
        private ItemRegistry _registry;
        private float _gradeUntil;
        private float _messageUntil;

        protected override void Bind(LocalPlayer player)
        {
            if (player.Chopper != null)
            {
                player.Chopper.Rejected.AddListener(HandleTierRefusal);
                player.Chopper.Missed.AddListener(HandleMiss);
            }

            Resolve();
        }

        protected override void Unbind(LocalPlayer player)
        {
            if (player.Chopper != null)
            {
                player.Chopper.Rejected.RemoveListener(HandleTierRefusal);
                player.Chopper.Missed.RemoveListener(HandleMiss);
            }
        }

        protected override void OnDisable()
        {
            base.OnDisable();

            if (_trees != null)
                _trees.TreeDamaged -= HandleDamaged;

            _trees = null;
        }

        private void Resolve()
        {
            if (_trees == null && ServiceLocator.TryGet(out _trees))
                _trees.TreeDamaged += HandleDamaged;

            if (_registry == null)
                ServiceLocator.TryGet(out _registry);
        }

        private void Update()
        {
            Resolve();

            if (_grade != null && _grade.enabled && Time.time > _gradeUntil)
                _grade.enabled = false;

            if (_message != null && _message.enabled && Time.time > _messageUntil)
                _message.enabled = false;

            if (_group != null)
                _group.alpha = Player != null ? 1f : 0f;
        }

        private void HandleDamaged(TreeDamagedBroadcast message)
        {
            /* Everyone subscribed to the chunk hears this, including players who did not
             * swing. Showing them somebody else's grade would be noise, so it is filtered
             * to the tree this player is actually aiming at. */
            if (Player?.Chopper == null
                || !Player.Chopper.TryGetTarget(out TreeId target, out _)
                || target.ChunkKey != message.ChunkKey
                || target.LocalIndex != message.LocalIndex)
                return;

            Show(message.Grade);
        }

        private void HandleMiss() => Show(ChopGrade.Miss);

        private void Show(ChopGrade grade)
        {
            if (_grade == null)
                return;

            _grade.text = grade switch
            {
                ChopGrade.Perfect => "PERFECT",
                ChopGrade.Good => "GOOD",
                ChopGrade.Bad => "GLANCING",
                _ => "MISS",
            };

            _grade.color = _theme != null ? _theme.ForGrade((int)grade) : Color.white;
            _grade.enabled = true;
            _gradeUntil = Time.time + _gradeSeconds;
        }

        /// <summary>
        /// The axe is too weak. Names the tool that would work rather than the number,
        /// because "needs tier 2" is not something a player can go and find.
        /// </summary>
        private void HandleTierRefusal(byte requiredTier)
        {
            if (_message == null)
                return;

            _message.text = $"Needs {NameOfAxeAtTier(requiredTier)}.";
            _message.color = _theme != null ? _theme.SlotRefusing : Color.red;
            _message.enabled = true;
            _messageUntil = Time.time + _messageSeconds;
        }

        private string NameOfAxeAtTier(byte tier)
        {
            if (_registry == null)
                return "a better axe";

            /* The lowest-tier axe that would do, so a tier-4 trunk asks for the next step
             * up rather than the best axe in the game. Walking the registry keeps this
             * working when a tier is added, with no list here to update. */
            ItemDefinition best = null;

            foreach (ItemDefinition definition in _registry.All)
            {
                if (definition == null || definition.ValidSlot != ItemSlot.Axe || definition.Tier < tier)
                    continue;

                if (best == null || definition.Tier < best.Tier)
                    best = definition;
            }

            return best != null ? $"a {best.DisplayName}" : "a better axe";
        }
    }
}
