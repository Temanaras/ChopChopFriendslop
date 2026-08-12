using System;
using Steamworks;
using UnityEngine;

namespace ChopChop.Networking
{
    /// <summary>
    /// Owns the Steam client lifetime for the whole process (TECH 8.1). Survives
    /// scene loads and there is exactly one.
    ///
    /// Callbacks are pumped manually from <see cref="Update"/> rather than using
    /// Facepunch's async timer, so they arrive on the Unity main thread.
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

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            TryInitialize();
        }

        private void TryInitialize()
        {
            try
            {
                // asyncCallbacks: false — we pump RunCallbacks ourselves.
                SteamClient.Init(_appId, false);
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
            SteamClient.Shutdown();
        }
    }
}
