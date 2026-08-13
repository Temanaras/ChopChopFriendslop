using ChopChop.World;
using FishNet.Object;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

namespace ChopChop.Player
{
    /// <summary>
    /// Turns a swing into a chop request (TECH 5.6).
    ///
    /// The order matters: the swing plays *immediately*, then the request goes out. The
    /// visual is a lie until the server confirms it, and that is deliberate — waiting a
    /// round trip before the axe moves would make the game feel broken at any ping. If
    /// the server refuses, the tree simply takes no damage; nothing rolls back, because
    /// at four players nobody will notice a swing that did nothing (TECH 4.3).
    ///
    /// Owner-only. Nothing here decides anything.
    /// </summary>
    public sealed class PlayerChopper : NetworkBehaviour
    {
        [Tooltip("How far the chop raycast reaches. The server allows a little more, to " +
                 "absorb the difference between here and there.")]
        [SerializeField] private float _range = 4f;

        [Tooltip("Where the swing originates. Must be on the body, not the camera: range " +
                 "is measured from here and the server measures from the player, so a " +
                 "third-person camera boom would put every tree out of reach.")]
        [SerializeField] private Transform _origin;

        [Tooltip("What the player is looking at. Supplies direction only.")]
        [SerializeField] private Transform _aim;

        [SerializeField] private LayerMask _hitMask = ~0;

        [Header("Presentation")]
        [Tooltip("Fired the instant the swing starts, before the server has any opinion. " +
                 "Hook the swing animation and the whoosh here.")]
        public UnityEvent Swung;

        [Tooltip("Fired with the impact point when the swing connects with a tree.")]
        public UnityEvent<Vector3> HitTree;

        [Tooltip("Fired when the server refuses the chop, with the tier the tree needed. " +
                 "A bounce or a thunk goes here — silence reads as a bug (TECH 5.6).")]
        public UnityEvent<byte> Rejected;

        private InputAction _attack;
        private TreeClient _trees;
        private PlayerLoadout _loadout;
        private bool _resolved;

        public override void OnStartClient()
        {
            base.OnStartClient();

            if (!IsOwner)
            {
                enabled = false;
                return;
            }

            TryGetComponent(out _loadout);

            InputActionAsset actions = InputSystem.actions;
            _attack = actions != null ? actions.FindAction("Player/Attack") : null;

            if (_attack == null)
                Debug.LogError("[Chop] No Player/Attack action; chopping is disabled.");
            else
                _attack.Enable();
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            _attack?.Disable();

            if (_trees != null)
                _trees.ChopRejected -= HandleRejected;
        }

        /// <summary>Wired at boot, since the tree client lives outside the scene.</summary>
        public void Bind(TreeClient trees)
        {
            _trees = trees;
            _resolved = true;

            if (IsOwner)
                _trees.ChopRejected += HandleRejected;
        }

        private void HandleRejected(ChopRejectedBroadcast message)
        {
            // Tier is the one refusal a player can act on: a better axe, not a closer
            // stance. The rest still get a thunk so nothing ever fails silently.
            Rejected?.Invoke(message.RequiredTier);
        }

        private void Update()
        {
            if (!IsOwner || _attack == null)
                return;

            // The axe and the gun share the primary button, so only the held one acts.
            if (_loadout != null && !_loadout.IsHolding(PlayerLoadout.Tool.Axe))
                return;

            if (!_attack.WasPressedThisFrame())
                return;

            Swing();
        }

        private void Swing()
        {
            // Unconditional and first: the swing plays whether or not it connects, and
            // whether or not the server later agrees.
            Swung?.Invoke();

            if (!_resolved || _trees == null)
                return;

            Transform from = _origin != null ? _origin : transform;
            Vector3 direction = _aim != null ? _aim.forward : from.forward;

            if (!Physics.Raycast(from.position, direction, out RaycastHit hit, _range, _hitMask))
                return;

            if (!hit.collider.TryGetComponent(out TreeCollider tree))
                return;

            HitTree?.Invoke(hit.point);

            /* Only the id crosses the network. The server looks the tree up in its own
             * regenerated chunk data, so a client cannot describe a tree that does not
             * exist by lying about its position or tier. */
            _trees.RequestChop(tree.Id.ChunkKey, tree.Id.LocalIndex);
        }
    }
}
