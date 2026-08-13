using System;
using System.Collections.Generic;
using UnityEngine;

namespace ChopChop.Items
{
    /// <summary>
    /// Resolves the ids that cross the network back into definitions (TECH 9.1).
    ///
    /// Validated at boot for duplicate and missing ids, because both failures are silent
    /// otherwise: a duplicate means two items share a save entry and one quietly becomes
    /// the other, and a missing id means an item a player owns simply stops existing on
    /// load. Neither throws on its own.
    /// </summary>
    [CreateAssetMenu(menuName = "ChopChop/Item Registry", fileName = "ItemRegistry")]
    public sealed class ItemRegistry : ScriptableObject
    {
        [SerializeField] private ItemDefinition[] _items = Array.Empty<ItemDefinition>();

        private Dictionary<ushort, ItemDefinition> _byId;

        public int Count => _items.Length;

        /// <summary>
        /// Builds the lookup and reports anything wrong with it.
        /// </summary>
        /// <returns>False when the registry is unusable, so boot can refuse rather than
        /// run with items that will disappear.</returns>
        public bool Validate()
        {
            _byId = new Dictionary<ushort, ItemDefinition>(_items.Length);

            bool ok = true;

            for (int i = 0; i < _items.Length; i++)
            {
                ItemDefinition item = _items[i];

                if (item == null)
                {
                    Debug.LogError($"[Items] Registry slot {i} is empty.", this);
                    ok = false;
                    continue;
                }

                if (item.Id == 0)
                {
                    Debug.LogError($"[Items] {item.name} uses id 0, which means 'nothing'.", item);
                    ok = false;
                    continue;
                }

                if (_byId.TryGetValue(item.Id, out ItemDefinition existing))
                {
                    Debug.LogError(
                        $"[Items] Id {item.Id} is used by both {existing.name} and {item.name}. " +
                        "Ids are what saves and packets carry, so one would silently become the other.", item);
                    ok = false;
                    continue;
                }

                _byId[item.Id] = item;
            }

            return ok;
        }

        public bool TryGet(ushort id, out ItemDefinition item)
        {
            if (_byId == null)
                Validate();

            return _byId.TryGetValue(id, out item);
        }

        public ItemDefinition Get(ushort id) => TryGet(id, out ItemDefinition item) ? item : null;

        /// <summary>Tier of an item, or 0 when unknown. Used for the chop gate.</summary>
        public byte TierOf(ushort id) => TryGet(id, out ItemDefinition item) ? item.Tier : (byte)0;

        /// <summary>Largest stack this item allows, or 1 when unknown.</summary>
        public ushort MaxStackOf(ushort id) => TryGet(id, out ItemDefinition item) ? item.MaxStack : (ushort)1;

        /// <summary>Which slot an item may be equipped into, if any.</summary>
        public ItemSlot SlotOf(ushort id) => TryGet(id, out ItemDefinition item) ? item.ValidSlot : ItemSlot.None;
    }
}
