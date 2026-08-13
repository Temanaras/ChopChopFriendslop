using UnityEngine;

namespace ChopChop.Items
{
    /// <summary>
    /// What an item <em>is</em> — static, shared, and never networked (TECH 9.1).
    ///
    /// The split matters: this is authored data that both sides already have, so only
    /// the <see cref="Id"/> ever crosses the wire or reaches a save file. Sending the
    /// definition would be sending the same bytes over and over; serialising by name or
    /// by asset reference would break the moment anything is renamed or moved.
    ///
    /// **Everything that counts as progression is one of these** (TECH 2.3). No XP, no
    /// levels, no learned recipes. If a feature cannot be put in a box and handed to
    /// someone else, it is the wrong shape for this game.
    /// </summary>
    [CreateAssetMenu(menuName = "ChopChop/Item Definition", fileName = "Item")]
    public sealed class ItemDefinition : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Stable and never reused. This is what is saved and sent; changing it " +
                 "silently turns every existing copy into a different item.")]
        public ushort Id;

        public string DisplayName = "Item";

        public Sprite Icon;

        [Header("Equipment")]
        [Tooltip("Which paperdoll slot this occupies, or None for loose cargo.")]
        public ItemSlot ValidSlot = ItemSlot.None;

        [Tooltip("Tool tier. An axe fells trees up to its own tier and no further — a " +
                 "hard gate, not a slower chop (TECH 5.6).")]
        public byte Tier;

        [Header("Stacking")]
        [Min(1)] public ushort MaxStack = 1;

        /// <summary>True when this can be equipped rather than only carried.</summary>
        public bool IsEquipment => ValidSlot != ItemSlot.None;

        private void OnValidate()
        {
            // Id zero means "nothing" throughout, so a real item may never claim it.
            if (Id == 0)
                Debug.LogWarning($"[Items] {name} has id 0, which is reserved for an empty slot.", this);

            if (MaxStack == 0)
                MaxStack = 1;
        }
    }
}
