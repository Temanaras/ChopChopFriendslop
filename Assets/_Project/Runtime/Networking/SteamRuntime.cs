using System;
using Steamworks;
using UnityEngine;

namespace ChopChop.Networking
{
    /// <summary>
    /// Owns the Steam client lifetime for the whole process (TECH 8.1). Survives
    /// scene loads and there is exactly one.
    ///
    /// Callbacks are pumped manually from <see cref="Update"/> rather than relying
    /// solely on Facepunch's async timer, so they arrive on the Unity main thread.
    ///
    /// We are not guaranteed to be the one who initializes Steam. FishNet's
    /// NetworkManager runs at <c>DefaultExecutionOrder(short.MinValue)</c> and
    /// initializes every transport inside Multipass from its own Awake, and
    /// FishyFacepunch calls <c>SteamClient.Init</c> there if Steam isn't up yet.
    /// So whoever wins, we adopt the running client rather than treating it as a
    /// failure — a second Init throws, and that used to leave IsReady false
    /// forever, which stalls GameBootstrap before it ever reaches the menu.
    ///
    /// Consequence: <see cref="_appId"/> must match the AppID on the
    /// FishyFacepunch component, since either may be the one that takes effect.
    /// </summary>
    public sealed class SteamRuntime : MonoBehaviour
    {
        /// <summary>Spacewar — Valve's public test AppID. Swap once we have a real one.</summary>
        public const uint SpacewarAppId = 480;

        [SerializeField] private uint _appId = SpacewarAppId;

        public bool IsReady { get; private set; }
        public SteamId LocalSteamId { get; private set; }
        public string LocalName { get; private set; } = "Unknown";

        public event Action Ready;

        /// <summary>Only the initializer shuts Steam down again.</summary>
        private bool _ownsClient;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            TryInitialize();
        }

        private void TryInitialize()
        {
            if (SteamClient.IsValid)
            {
                // Someone got here first — almost certainly FishyFacepunch.
                Debug.Log("[Steam] Adopting an already-initialized SteamClient.");
            }
            else
            {
                try
                {
                    // asyncCallbacks: false — we pump RunCallbacks ourselves.
                    SteamClient.Init(_appId, false);
                    _ownsClient = true;
                }
                catch (Exception e)
                {
                    Debug.LogError(
                        $"[Steam] Init failed for AppID {_appId}. Steam must be running, and " +
                        $"steam_appid.txt must sit next to the executable. {e.Message}");
                    return;
                }

                if (!SteamClient.IsValid)
                {
                    Debug.LogError("[Steam] SteamClient reported invalid immediately after Init.");
                    return;
                }
            }

            LocalSteamId = SteamClient.SteamId;
            LocalName = SteamClient.Name;
            IsReady = true;

            Debug.Log($"[Steam] Ready as {LocalName} ({LocalSteamId.Value}).");
            Ready?.Invoke();
        }

        private void Update()
        {
            if (IsReady)
                SteamClient.RunCallbacks();
        }

        private void OnDestroy()
        {
            if (!IsReady)
                return;

            IsReady = false;

            // Shutting down a client FishyFacepunch owns would pull the transport
            // out from under an active session.
            if (_ownsClient)
                SteamClient.Shutdown();
        }
    }
}
