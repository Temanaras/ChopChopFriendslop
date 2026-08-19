using ChopChop.Core;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace ChopChop.Cabin
{
    /// <summary>
    /// A fire you can light and put out. The first networked interactable, and mostly
    /// here to prove that shape end to end (TECH 9.4-style round trip: ask, validate,
    /// broadcast).
    ///
    /// Whether it is lit is server state, replicated to everyone — two players must never
    /// disagree about whether the cabin is dark. The light and the visuals hang off that
    /// one bool, so a client that joins late gets the right state for free.
    ///
    /// Note it is **not** an <see cref="ICabinFixture"/>: it needs nothing from outside
    /// the Cabin assembly, so it does not ask for a context. The fixture seam is opt-in,
    /// and a thing that has no dependencies should not pretend to have some.
    /// </summary>
    public sealed class Campfire : NetworkBehaviour, IInteractable
    {
        [Header("Interaction")]
        [SerializeField] private float _range = 3f;
        [SerializeField] private string _lightPrompt = "Light the fire";
        [SerializeField] private string _extinguishPrompt = "Put out the fire";

        [Header("Visuals")]
        [Tooltip("Switched with the fire. Everything here is presentation and is never " +
                 "consulted for state.")]
        [SerializeField] private Light _light;

        [SerializeField] private GameObject[] _litObjects;

        [Tooltip("Starts lit, so a fresh cabin is not pitch dark before anyone touches it.")]
        [SerializeField] private bool _startLit = true;

        /// <summary>
        /// The whole of this thing's state. Server-written, read everywhere.
        /// </summary>
        private readonly SyncVar<bool> _lit = new();

        /// <summary>True when burning. Presentation and UI read this; nothing writes it.</summary>
        public bool IsLit => _lit.Value;

        // ---------------- IInteractable ----------------

        public Vector3 InteractPoint => transform.position;
        public float InteractRange => _range;
        public bool IsAvailable => true;
        public string Prompt => _lit.Value ? _extinguishPrompt : _lightPrompt;

        /// <summary>Asks the server to flip it. The client changes nothing itself.</summary>
        public void Interact() => RequestToggle();

        // ---------------- Lifecycle ----------------

        private void Awake()
        {
            _lit.OnChange += HandleLitChanged;

            /* Registered here rather than in OnStartClient so the entry is balanced
             * against OnDisable and cannot leak if the object is destroyed while
             * despawning. */
            ApplyLit(_lit.Value);
        }

        private void OnEnable() => Interactables.Register(this);

        private void OnDisable() => Interactables.Unregister(this);

        private void OnDestroy() => _lit.OnChange -= HandleLitChanged;

        public override void OnStartServer()
        {
            base.OnStartServer();
            _lit.Value = _startLit;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            // A late joiner never saw the change, so read the current value once.
            ApplyLit(_lit.Value);
        }

        private void HandleLitChanged(bool previous, bool next, bool asServer) => ApplyLit(next);

        private void ApplyLit(bool lit)
        {
            if (_light != null)
                _light.enabled = lit;

            if (_litObjects == null)
                return;

            for (int i = 0; i < _litObjects.Length; i++)
                if (_litObjects[i] != null)
                    _litObjects[i].SetActive(lit);
        }

        // ---------------- Server ----------------

        [ServerRpc(RequireOwnership = false)]
        private void RequestToggle(NetworkConnection sender = null)
        {
            /* Re-checked here, because the client asking is the client that decided it was
             * close enough. Same slack as the chest and the chop: enough for latency,
             * not enough to reach across the cabin. */
            if (sender?.FirstObject == null)
                return;

            float distance = Vector3.Distance(sender.FirstObject.transform.position, transform.position);

            if (distance > _range + 2f)
                return;

            _lit.Value = !_lit.Value;
        }
    }
}
