using ChopChop.World;
using FishNet.Object;
using UnityEngine;

namespace ChopChop.Player
{
    /// <summary>
    /// The local player's camera: switches it on, and turns it with the mouse.
    /// Presentation is client-local by construction (TECH 2.1), so there is no networked
    /// state here — two players looking in different directions is not a disagreement.
    ///
    /// The rig is parented under the graphical child rather than the predicted root.
    /// FishNet smooths the graphical object between ticks and after reconciliation; a
    /// camera on the root instead inherits every correction as a jolt.
    ///
    /// Yaw is not purely cosmetic: <see cref="PlayerMotor"/> reads <see cref="Yaw"/> to
    /// turn stick input into world-space movement, because a camera you can turn while
    /// W still walks toward world north is worse than no camera control at all.
    /// </summary>
    public sealed class PlayerCameraRig : NetworkBehaviour
    {
        [Tooltip("Camera object to switch on for the owning client. Should be a child of " +
                 "the NetworkObject's graphical object, not of the root.")]
        [SerializeField] private GameObject _camera;

        [Tooltip("What actually rotates. The camera hangs off this on a local -Z boom, so " +
                 "pitching the pivot orbits the camera around the player rather than " +
                 "tilting it in place.")]
        [SerializeField] private Transform _pivot;

        [Header("Look")]
        [Tooltip("Degrees per unit of look input. Mouse deltas arrive in counts, so this " +
                 "is roughly degrees per pixel.")]
        [SerializeField] private float _sensitivity = 0.12f;

        [Tooltip("How far down the player can look. Limited by the boom hitting the ground.")]
        [SerializeField] private float _minPitch = -30f;

        [Tooltip("How far up. Kept below 90 so the camera never passes over the head and " +
                 "flips the horizon.")]
        [SerializeField] private float _maxPitch = 70f;

        [SerializeField] private bool _invertY;

        [Tooltip("Capture the cursor while playing. Escape releases it in the editor.")]
        [SerializeField] private bool _captureCursor = true;

        private PlayerInputReader _input;
        private bool _owns;

        /// <summary>Heading in degrees. Read by the motor to orient movement.</summary>
        public float Yaw { get; private set; }

        /// <summary>Elevation in degrees, negative looking down.</summary>
        public float Pitch { get; private set; }

        private void Awake() => _input = GetComponent<PlayerInputReader>();

        public override void OnStartClient()
        {
            base.OnStartClient();

            if (_camera == null)
            {
                Debug.LogError($"[Player] No camera assigned on {name}.");
                return;
            }

            // Every client has exactly one owned player, so exactly one camera and one
            // AudioListener end up active.
            _camera.SetActive(IsOwner);

            if (!IsOwner)
                return;

            _owns = true;

            /* Start facing wherever the body was placed, so the first frame is not a
             * lurch from world north to whatever the spawn happened to choose. */
            Yaw = transform.eulerAngles.y;
            ApplyRotation();
            SetCursorCaptured(true);

            /* The forest streams around whoever is playing here. Found rather than
             * injected because the streamer lives in the world scene and the player is
             * spawned into it by the server — neither can hold a serialised reference to
             * the other. */
            WorldStreamer streamer = FindObjectOfType<WorldStreamer>();

            if (streamer != null)
                streamer.SetCentre(transform);

            if (Core.ServiceLocator.TryGet(out TreeClient trees) && TryGetComponent(out PlayerChopper chopper))
                chopper.Bind(trees);
        }

        public override void OnStopClient()
        {
            base.OnStopClient();

            if (_owns)
                SetCursorCaptured(false);

            _owns = false;
        }

        /* LateUpdate, not Update: the prediction smoother writes the graphical object's
         * transform during the frame, and a camera that reads its parent before that
         * lands trails the body by a frame. Rotation here is local to the pivot, so it
         * survives whatever the smoother does above it. */
        private void LateUpdate()
        {
            if (!_owns || _input == null)
                return;

            Vector2 look = _input.LookInput;

            // No deltaTime. See PlayerInputReader.LookInput.
            Yaw += look.x * _sensitivity;
            Pitch += (_invertY ? look.y : -look.y) * _sensitivity;

            Yaw = Mathf.Repeat(Yaw, 360f);
            Pitch = Mathf.Clamp(Pitch, _minPitch, _maxPitch);

            ApplyRotation();
        }

        /// <summary>
        /// Set in world space rather than locally: the pivot hangs under the graphical
        /// object, and if anything ever rotates that, a local rotation would compose with
        /// it and the camera would drift away from where the player is pointing.
        /// </summary>
        private void ApplyRotation()
        {
            if (_pivot != null)
                _pivot.rotation = Quaternion.Euler(Pitch, Yaw, 0f);
        }

        private void SetCursorCaptured(bool captured)
        {
            if (!_captureCursor)
                return;

            Cursor.lockState = captured ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !captured;
        }
    }
}
