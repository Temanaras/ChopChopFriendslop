using ChopChop.Items;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ChopChop.UI
{
    /// <summary>Which container a slot belongs to. Decides what a drop out of it means.</summary>
    public enum SlotKind : byte
    {
        /// <summary>The player's backpack. Lost on death (TECH 9.3).</summary>
        Carried = 0,

        /// <summary>A paperdoll slot. Fixed index, only accepts its own kind of item.</summary>
        Equipment = 1,

        /// <summary>The cabin chest, or any other shared container.</summary>
        Storage = 2,
    }

    /// <summary>
    /// One square. Draws whatever stack it is told to, and reports drags.
    ///
    /// Deliberately knows nothing about networking or about where the stack came from —
    /// it holds a kind and an index and hands both to its host on a drop. That is what
    /// lets the same prefab serve the backpack, the paperdoll and the chest, and it is
    /// why moving between them is one code path rather than six.
    ///
    /// **No icons exist yet.** Every <see cref="ItemDefinition.Icon"/> in the project is
    /// unassigned, so the fallback is a tinted chip with the item's initials. The icon is
    /// preferred whenever there is one, so filling those fields in is the whole migration.
    /// </summary>
    public sealed class ItemSlotView : MonoBehaviour,
        IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler, IPointerEnterHandler, IPointerExitHandler
    {
        [SerializeField] private Image _background;
        [SerializeField] private Image _icon;
        [SerializeField] private Image _tierStripe;
        [SerializeField] private TMP_Text _chip;
        [SerializeField] private TMP_Text _count;
        [SerializeField] private UiTheme _theme;

        private ISlotHost _host;

        /// <summary>Which container this belongs to.</summary>
        public SlotKind Kind { get; private set; }

        /// <summary>Index within that container. For <see cref="SlotKind.Equipment"/> this is the <see cref="ItemSlot"/>.</summary>
        public int Index { get; private set; }

        /// <summary>Only meaningful for equipment slots.</summary>
        public ItemSlot Slot => (ItemSlot)Index;

        /// <summary>What is currently drawn here.</summary>
        public ItemStack Stack { get; private set; }

        public bool IsEmpty => Stack.IsEmpty;

        public void Configure(ISlotHost host, SlotKind kind, int index, UiTheme theme)
        {
            _host = host;
            Kind = kind;
            Index = index;

            if (theme != null)
                _theme = theme;
        }

        public void Draw(ItemStack stack, ItemRegistry registry)
        {
            Stack = stack;

            ItemDefinition definition = null;

            if (!stack.IsEmpty && registry != null)
                registry.TryGet(stack.ItemId, out definition);

            bool empty = stack.IsEmpty || definition == null;

            if (_icon != null)
            {
                _icon.sprite = definition != null ? definition.Icon : null;
                _icon.enabled = _icon.sprite != null;
            }

            if (_chip != null)
            {
                // Shown only when there is no icon, so real art simply replaces it.
                bool showChip = !empty && (_icon == null || _icon.sprite == null);
                _chip.enabled = showChip;

                if (showChip)
                {
                    _chip.text = Initials(definition.DisplayName);
                    _chip.color = _theme != null ? _theme.ForTier(definition.Tier) : Color.white;
                }
            }

            if (_tierStripe != null)
            {
                // Tier is the whole gating system (DESIGN 8.4), so it gets its own mark
                // rather than only tinting text that an icon would later cover.
                _tierStripe.enabled = !empty && definition.Tier > 0;

                if (_tierStripe.enabled && _theme != null)
                    _tierStripe.color = _theme.ForTier(definition.Tier);
            }

            if (_count != null)
            {
                // A lone item shows no "1"; the number is only information above one.
                bool showCount = !empty && stack.Count > 1;
                _count.enabled = showCount;

                if (showCount)
                    _count.text = stack.Count.ToString();
            }

            Tint(_theme != null ? _theme.Slot : Color.grey);
        }

        /// <summary>
        /// Two letters standing in for an icon. Initials for a multi-word name, first and
        /// last letter for a single word — so Wood and Wool do not both read "WO".
        /// </summary>
        private static string Initials(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "??";

            string[] words = name.Split(' ');

            if (words.Length > 1 && words[0].Length > 0 && words[1].Length > 0)
                return $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}";

            string word = words[0];
            return word.Length == 1
                ? char.ToUpperInvariant(word[0]).ToString()
                : $"{char.ToUpperInvariant(word[0])}{char.ToUpperInvariant(word[word.Length - 1])}";
        }

        public void Tint(Color colour)
        {
            if (_background != null)
                _background.color = colour;
        }

        // ---- pointer ----------------------------------------------------------

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_theme != null && _host != null && !_host.IsDragging)
                Tint(_theme.SlotHover);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (_theme != null && _host != null && !_host.IsDragging)
                Tint(_theme.Slot);
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (_host == null || IsEmpty)
                return;

            _host.BeginDrag(this);
        }

        public void OnDrag(PointerEventData eventData) => _host?.UpdateDrag(eventData);

        public void OnEndDrag(PointerEventData eventData) => _host?.EndDrag();

        /* Fires on the slot under the pointer, before OnEndDrag on the slot that started
         * it — so the host still knows what is being dragged when this arrives. */
        public void OnDrop(PointerEventData eventData) => _host?.Drop(this);
    }

    /// <summary>
    /// Whatever owns a set of slots and knows how to ask the server to move things
    /// between them. Keeps <see cref="ItemSlotView"/> free of any opinion about
    /// containers, ownership or RPCs.
    /// </summary>
    public interface ISlotHost
    {
        bool IsDragging { get; }
        void BeginDrag(ItemSlotView source);
        void UpdateDrag(PointerEventData eventData);
        void EndDrag();
        void Drop(ItemSlotView destination);
    }
}
