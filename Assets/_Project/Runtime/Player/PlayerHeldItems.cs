using System.Collections.Generic;
using ChopChop.Items;
using FishNet.Object;
using UnityEngine;

namespace ChopChop.Player
{
    /// <summary>
    /// Puts equipped items in the player's hands.
    ///
    /// Presentation only, and driven entirely by <see cref="PlayerPaperdoll.Equipped"/> —
    /// which is replicated to everyone precisely so other players can see your axe
    /// (TECH 9.2). Nothing here is authoritative and nothing here is networked: the
    /// paperdoll already agreed what you are holding, this only makes it visible.
    ///
    /// The item says what to show and the rig says where. Which bone a slot hangs off is
    /// the only thing that lives here; the offset within that bone is authored into the
    /// held prefab, because it is a property of the axe's own geometry rather than of the
    /// character wearing it.
    /// </summary>
    public sealed class PlayerHeldItems : MonoBehaviour
    {
        /// <summary>Where one slot's item hangs, and how it sits once there.</summary>
        [System.Serializable]
        public struct Socket
        {
            public ItemSlot Slot;

            [Tooltip("Humanoid bone to attach to. Resolved through the avatar, so it works " +
                     "on any rigged character without knowing its bone names.")]
            public HumanBodyBones Bone;

            [Tooltip("Local offset from the bone. This one is a property of the hand rather " +
                     "than of the item, so it is expected to need a nudge when the " +
                     "character model changes.")]
            public Vector3 LocalPosition;

            public Vector3 LocalEuler;

            [Tooltip("Uniform scale, for when a prop was authored at a different size.")]
            public float Scale;
        }

        [Tooltip("Rig to hang items off. Found in children if left empty.")]
        [SerializeField] private Animator _animator;

        /* Defaults measured off the current rig rather than dialled in by hand: the shaft
         * of a gripped tool runs along the knuckle line, so the rotation is the one that
         * lays the axe's local +Z along pinky-to-index with the head leaving on the thumb
         * side, and the position is the palm, halfway from wrist to knuckles. Written
         * down as plain numbers because they are the kind of thing an animator will want
         * to nudge in the inspector, but that is where they came from.
         *
         * The roll about the shaft is the one axis a grip does not pin down, and idle and
         * chop disagree about it — the two clips hold the wrist about 30 degrees apart, so
         * no fixed roll is right for both. Measured across the whole sweep: the roll that
         * cuts perfectly edge-first shows almost no blade while walking, and the one that
         * shows the most blade strikes the tree with its cheek. This sits at the knee of
         * that curve, keeping 0.74 of the best rest silhouette for a 27-degree cant at the
         * strike, which does not read at swing speed. Fixing it properly means an idle
         * authored for a held axe, not a better number here. */
        [SerializeField]
        private Socket[] _sockets =
        {
            new()
            {
                Slot = ItemSlot.Axe,
                Bone = HumanBodyBones.RightHand,
                LocalPosition = new Vector3(0f, 0.073f, 0f),
                LocalEuler = new Vector3(351f, 270f, 34f),
                Scale = 1f,
            },
        };

        private PlayerPaperdoll _paperdoll;
        private ItemRegistry _registry;

        private readonly Dictionary<ItemSlot, GameObject> _spawned = new();

        private void Awake()
        {
            _paperdoll = GetComponent<PlayerPaperdoll>();

            if (_animator == null)
                _animator = GetComponentInChildren<Animator>();
        }

        private void OnEnable()
        {
            if (_paperdoll != null)
                _paperdoll.Equipped += HandleEquipped;
        }

        private void OnDisable()
        {
            if (_paperdoll != null)
                _paperdoll.Equipped -= HandleEquipped;
        }

        private void Start()
        {
            /* The registry is registered at boot on every machine, so this works on a
             * client that never runs the server's equip logic. */
            Core.ServiceLocator.TryGet(out _registry);

            /* Catch up on what is already equipped. The event only fires on change, and a
             * player who joins with an axe already in hand — which is everyone, since the
             * server equips one before the client finishes spawning — would otherwise
             * arrive empty-handed and stay that way. */
            RefreshAll();
        }

        private void RefreshAll()
        {
            if (_paperdoll == null)
                return;

            for (int i = 0; i < _sockets.Length; i++)
                HandleEquipped(_sockets[i].Slot, _paperdoll.GetEquipped(_sockets[i].Slot));
        }

        private void HandleEquipped(ItemSlot slot, ItemStack stack)
        {
            if (!TryGetSocket(slot, out Socket socket))
                return;

            if (_spawned.TryGetValue(slot, out GameObject existing) && existing != null)
            {
                Destroy(existing);
                _spawned.Remove(slot);
            }

            if (stack.IsEmpty || _registry == null)
                return;

            ItemDefinition definition = _registry.Get(stack.ItemId);

            if (definition == null || definition.HeldPrefab == null)
                return;

            Transform bone = ResolveBone(socket.Bone);

            if (bone == null)
                return;

            GameObject instance = Instantiate(definition.HeldPrefab, bone);
            instance.transform.SetLocalPositionAndRotation(
                socket.LocalPosition, Quaternion.Euler(socket.LocalEuler));

            /* Bone scale is inherited from the rig, which is rarely exactly 1. Setting a
             * local scale here would compound with it and make the axe grow or shrink
             * with whatever character is holding it. */
            float scale = socket.Scale <= 0f ? 1f : socket.Scale;
            Vector3 boneScale = bone.lossyScale;

            instance.transform.localScale = new Vector3(
                scale / Mathf.Max(0.0001f, boneScale.x),
                scale / Mathf.Max(0.0001f, boneScale.y),
                scale / Mathf.Max(0.0001f, boneScale.z));

            _spawned[slot] = instance;
        }

        private bool TryGetSocket(ItemSlot slot, out Socket socket)
        {
            for (int i = 0; i < _sockets.Length; i++)
            {
                if (_sockets[i].Slot != slot)
                    continue;

                socket = _sockets[i];
                return true;
            }

            socket = default;
            return false;
        }

        /// <summary>
        /// Asks the avatar for the bone rather than searching by name. Bone naming is a
        /// per-pack convention and the character is already Humanoid, so this survives
        /// swapping the model for a differently-named rig.
        /// </summary>
        private Transform ResolveBone(HumanBodyBones bone)
        {
            if (_animator == null || !_animator.isHuman)
                return null;

            return _animator.GetBoneTransform(bone);
        }
    }
}
