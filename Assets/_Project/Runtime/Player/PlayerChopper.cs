using ChopChop.World;
using FishNet.Object;
using UnityEngine;
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

        [Tooltip("Where the swing originates. Falls back to this transform.")]
        [SerializeField] private Transform _origin;

        [SerializeField] private LayerMask _hitMask = ~0;

        private InputAction _attack;
        private TreeClient _trees;
        private bool _resolved;

        public override void OnStartClient()
        {
            base.OnStartClient();

            if (!IsOwner)
            {
                enabled = false;
                return;
            }

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
        }

        /// <summary>Wired at boot, since the tree client lives outside the scene.</summary>
        public void Bind(TreeClient trees)
        {
            _trees = trees;
            _resolved = true;
        }

        private void Update()
        {
            if (!IsOwner || _attack == null)
                return;

            if (!_attack.WasPressedThisFrame())
                return;

            Swing();
        }

        private void Swing()
        {
            // The swing itself would go here — animation, whoosh, impact decal. It is
            // unconditional on purpose.

            if (!_resolved || _trees == null)
                return;

            Transform from = _origin != null ? _origin : transform;

            if (!Physics.Raycast(from.position, from.forward, out RaycastHit hit, _range, _hitMask))
                return;

            if (!hit.collider.TryGetComponent(out TreeCollider tree))
                return;

            /* Only the id crosses the network. The server looks the tree up in its own
             * regenerated chunk data, so a client cannot describe a tree that does not
             * exist by lying about its position or tier. */
            _trees.RequestChop(tree.Id.ChunkKey, tree.Id.LocalIndex);
        }
    }
}
