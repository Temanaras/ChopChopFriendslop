using ChopChop.Core;
using ChopChop.Networking;
using FishNet.Managing;
using FishNet.Transporting.Multipass;
using UnityEngine;

namespace ChopChop.Bootstrap
{
    /// <summary>
    /// Composition root. This is the one place that knows about every system, which
    /// is why it lives outside <c>ChopChop.Core</c> — Core is what everything else
    /// depends on, so it cannot itself depend on anything.
    ///
    /// Put this on a single object in the boot scene alongside the NetworkManager.
    /// </summary>
    public sealed class GameBootstrap : MonoBehaviour
    {
        [Header("Scene references")]
        [SerializeField] private NetworkManager _networkManager;
        [SerializeField] private SteamRuntime _steam;

        /// <summary>What an instance should do on the local Tugboat path.</summary>
        public enum LocalRole : byte
        {
            /// <summary>Host if the port is free, otherwise join. Right for multi-instance testing.</summary>
            Auto = 0,
            Host = 1,
            Join = 2,
        }

        [Header("Local testing (Tugboat)")]
        [Tooltip("Bypass Steam entirely and host/join over plain UDP. Required for " +
                 "multi-instance testing, since Steam P2P cannot connect to itself.")]
        [SerializeField] private bool _localTestMode;

        [Tooltip("Auto lets four instances launch in any order with identical settings: " +
                 "the first one up takes the port and hosts, the rest join it.")]
        [SerializeField] private LocalRole _localRole = LocalRole.Auto;

        [SerializeField] private string _localAddress = "127.0.0.1";
        [SerializeField] private ushort _localPort = 7770;

        private AppStateMachine _state;
        private TransportRouter _router;
        private LobbyService _lobby;
        private SessionCoordinator _session;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);

            if (_networkManager == null)
            {
                Debug.LogError("[Bootstrap] NetworkManager reference is not assigned.");
                enabled = false;
                return;
            }

            Multipass multipass = _networkManager.GetComponent<Multipass>();

            if (multipass == null)
            {
                Debug.LogError(
                    "[Bootstrap] No Multipass component on the NetworkManager. Multipass must be " +
                    "the active transport, with Tugboat and FishyFacepunch listed inside it.");
                enabled = false;
                return;
            }

            _state = new AppStateMachine();
            _router = new TransportRouter(multipass);
            _lobby = new LobbyService();
            _session = new SessionCoordinator(_networkManager, _router, _lobby);

            _lobby.Failed += message => Debug.LogError($"[Lobby] {message}");

            ServiceLocator.Register(_state);
            ServiceLocator.Register(_router);
            ServiceLocator.Register(_lobby);
            ServiceLocator.Register(_session);

            if (_steam != null)
                ServiceLocator.Register(_steam);
        }

        private void Start()
        {
            if (_localTestMode)
            {
                RunLocalTest();
                return;
            }

            if (_steam == null)
            {
                Debug.LogError("[Bootstrap] SteamRuntime is not assigned and local test mode is off.");
                return;
            }

            if (_steam.IsReady)
                OnSteamReady();
            else
                _steam.Ready += OnSteamReady;
        }

        private void RunLocalTest()
        {
            _state.Set(AppState.Connecting);

            switch (_localRole)
            {
                case LocalRole.Host:
                    Debug.Log($"[Bootstrap] Local test: hosting on port {_localPort}.");
                    _session.StartLocalHost(_localPort);
                    break;

                case LocalRole.Join:
                    Debug.Log($"[Bootstrap] Local test: joining {_localAddress}:{_localPort}.");
                    _session.JoinLocal(_localAddress, _localPort);
                    break;

                default:
                    Debug.Log($"[Bootstrap] Local test: hosting on port {_localPort}, or joining it if taken.");
                    _session.StartLocalAuto(_localAddress, _localPort);
                    break;
            }
        }

        private void OnSteamReady()
        {
            _steam.Ready -= OnSteamReady;
            _lobby.Initialize();
            _state.Set(AppState.Menu);

            // Cold start: Steam launched us with "+connect_lobby <id>" because a
            // friend accepted an invite while the game was closed.
            if (!LaunchArguments.TryGetConnectLobby(out ulong lobbyId))
                return;

            Debug.Log($"[Bootstrap] Cold-start invite to lobby {lobbyId}.");
            _state.Set(AppState.Connecting);
            _ = _lobby.JoinAsync(lobbyId);
        }

        /// <summary>Host a Steam lobby and start a listen server. Hook this to a menu button.</summary>
        public void HostSteamSession()
        {
            _state.Set(AppState.Connecting);
            _ = _lobby.HostAsync();
        }

        private void OnDestroy()
        {
            if (_steam != null)
                _steam.Ready -= OnSteamReady;

            _session?.Dispose();
            _lobby?.Dispose();
            ServiceLocator.Clear();
        }
    }
}
