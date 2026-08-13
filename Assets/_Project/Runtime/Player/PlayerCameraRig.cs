using ChopChop.World;
using FishNet.Object;
using UnityEngine;

namespace ChopChop.Player
{
    /// <summary>
    /// Enables the local player's camera and nothing else. Presentation is client-local
    /// by construction (TECH 2.1), so there is no networked state here.
    ///
    /// The camera is parented under the graphical child rather than the predicted root.
    /// FishNet smooths the graphical object between ticks and after reconciliation; a
    /// camera on the root instead inherits every correction as a jolt.
    /// </summary>
    public sealed class PlayerCameraRig : NetworkBehaviour
    {
        [Tooltip("Camera object to switch on for the owning client. Should be a child of " +
                 "the NetworkObject's graphical object, not of the root.")]
        [SerializeField] private GameObject _camera;

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

            /* The forest streams around whoever is playing here. Found rather than
             * injected because the streamer lives in the world scene and the player is
             * spawned into it by the server — neither can hold a serialised reference to
             * the other. */
            WorldStreamer streamer = FindObjectOfType<WorldStreamer>();

            if (streamer != null)
                streamer.SetCentre(transform);
        }
    }
}
