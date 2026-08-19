using ChopChop.Core;
using FishNet.Object;
using UnityEngine;

namespace ChopChop.Player
{
    /// <summary>
    /// Picks what the local player is about to use, and asks for it when they press the
    /// button.
    ///
    /// Owner-only. A remote player's choice of target is their business and is never
    /// simulated here — the only thing that crosses the wire is the request the chosen
    /// interactable sends for itself.
    ///
    /// Registered in the <see cref="ServiceLocator"/> so the HUD can ask what is
    /// currently in reach without having to hunt for the local player.
    /// </summary>
    public sealed class PlayerInteractor : NetworkBehaviour
    {
        [Tooltip("Where reach is measured from. The body, not the camera — a third-person " +
                 "camera sits metres behind and would let you use things through walls.")]
        [SerializeField] private Transform _origin;

        [Tooltip("How much being looked at counts against being close. 0 picks purely by " +
                 "distance, which makes two things side by side flicker between each other.")]
        [Range(0f, 1f)][SerializeField] private float _aimBias = 0.6f;

        private PlayerInputReader _input;
        private Camera _camera;
        private bool _owns;

        /// <summary>What pressing the button right now would use, or null.</summary>
        public IInteractable Current { get; private set; }

        private void Awake()
        {
            _input = GetComponent<PlayerInputReader>();

            if (_origin == null)
                _origin = transform;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            if (!IsOwner)
            {
                enabled = false;
                return;
            }

            _owns = true;
            ServiceLocator.Register(this);
        }

        public override void OnStopClient()
        {
            base.OnStopClient();

            if (_owns)
                ServiceLocator.Unregister<PlayerInteractor>();

            _owns = false;
            Current = null;
        }

        private void Update()
        {
            if (!_owns)
                return;

            Current = FindBest();

            if (Current != null && _input != null && _input.ConsumeInteract())
                Current.Interact();
        }

        /// <summary>
        /// Nearest thing in reach, preferring whatever is closest to the middle of the
        /// screen. Both matter: distance alone makes a shelf behind you win over the
        /// thing you are facing, and aim alone lets you use something across the room.
        /// </summary>
        private IInteractable FindBest()
        {
            if (_camera == null || !_camera.isActiveAndEnabled)
                _camera = Camera.main;

            Vector3 from = _origin.position;
            Vector3 look = _camera != null ? _camera.transform.forward : transform.forward;

            IInteractable best = null;
            float bestScore = float.MinValue;

            var all = Interactables.All;

            for (int i = 0; i < all.Count; i++)
            {
                IInteractable candidate = all[i];

                if (candidate == null || !candidate.IsAvailable)
                    continue;

                Vector3 offset = candidate.InteractPoint - from;
                float distance = offset.magnitude;

                if (distance > candidate.InteractRange)
                    continue;

                // 1 when it is dead ahead, 0 when it is off to the side or behind.
                float facing = distance > 0.01f
                    ? Mathf.Clamp01(Vector3.Dot(look, offset / distance))
                    : 1f;

                float closeness = 1f - Mathf.Clamp01(distance / Mathf.Max(0.01f, candidate.InteractRange));
                float score = Mathf.Lerp(closeness, facing, _aimBias);

                if (score <= bestScore)
                    continue;

                bestScore = score;
                best = candidate;
            }

            return best;
        }
    }
}
