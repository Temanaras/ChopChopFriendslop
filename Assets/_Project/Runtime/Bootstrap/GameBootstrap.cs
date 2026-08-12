using ChopChop.Core;
using ChopChop.Networking;
using ChopChop.Persistence;
using FishNet.Managing;
using FishNet.Managing.Scened;
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

        [Header("Role")]
        [Tooltip("Used when the command line says nothing. -server, or -connect <host>, " +
                 "overrides this. HostedServer is the normal way to play: a server and a " +
                 "client in one process.")]
        [SerializeField] private AppRole _defaultRole = AppRole.HostedServer;

        [Tooltip("When serving, take the port if it is free and otherwise connect to " +
                 "whoever already has it. This is what lets several editor instances " +
                 "launch in any order with identical settings (TECH 15).")]
        [SerializeField] private bool _connectIfPortTaken = true;

        [Header("Address")]
        [SerializeField] private string _address = "127.0.0.1";
        [SerializeField] private ushort _port = 7770;

        [Header("World")]
        [Tooltip("Seed used when no save exists yet. Server-side only.")]
        [SerializeField] private int _newWorldSeed = 1337;

        [Tooltip("Scene the server loads as a global scene once it is listening. Clients " +
                 "receive it automatically on connect.")]
        [SerializeField] private string _worldScene = "Assets/Scenes/Clearing.unity";

        /// <summary>Resolved once at boot; the command line wins over the inspector.</summary>
        public AppRole Role { get; private set; }

        private AppStateMachine _state;
        private TransportRouter _router;
        private LobbyService _lobby;
        private SessionCoordinator _session;
        private WorldSaveService _world;

        private bool _hasColdStartInvite;
        private ulong _coldStartLobbyId;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);

            ResolveRole();

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

            /* The world is server-owned, so this is only wired where a server runs.
             * Deliberately not created here: a HostedServer that finds its port taken
             * falls back to being a plain client, and a client holding a world save
             * service would be a second writer pointed at the same file. */
            if (Role.RunsServer())
                _session.ServerStarted += OnServerStarted;
        }

        private void OnServerStarted()
        {
            _session.ServerStarted -= OnServerStarted;

            _world = new WorldSaveService(SaveStore.Default);
            ServiceLocator.Register(_world);

            if (_world.LoadOrCreate(_newWorldSeed) == SaveLoadStatus.TooNew || _world.World == null)
            {
                // Refusing to load beats overwriting a save we do not understand.
                Debug.LogError("[Bootstrap] Stopping the session: the world could not be loaded.");
                _session.Stop();
                return;
            }

            LoadWorldScene();
            _state.Set(AppState.InGame);
        }

        /// <summary>
        /// The server decides what world everyone is in, so it loads that scene itself
        /// rather than leaving it to FishNet's DefaultScene component.
        ///
        /// DefaultScene was doing this, and it silently did nothing in a headless build:
        /// it only acts when exactly one transport reports Started, and Multipass starts
        /// every transport it holds. It also depends on subscribing during OnEnable,
        /// which resolved differently in a player than in the editor. Loading the scene
        /// here puts it on the same path as the rest of server startup, right after the
        /// world it belongs to has loaded.
        /// </summary>
        private void LoadWorldScene()
        {
            if (string.IsNullOrEmpty(_worldScene))
            {
                Debug.LogError("[Bootstrap] No world scene configured; clients will have nothing to load.");
                return;
            }

            /* By name, never by asset path. A player has no AssetDatabase, so
             * "Assets/Scenes/Clearing.unity" resolves in the editor and silently fails
             * in a build with "global scenes ... could not be found" — the server keeps
             * running, clients spawn into a world with no ground, and fall forever.
             * Accepting either form here means the inspector value cannot cause that. */
            string sceneName = System.IO.Path.GetFileNameWithoutExtension(_worldScene);

            SceneLoadData data = new(sceneName)
            {
                // Boot is a composition root, not somewhere to play. Everything that has
                // to outlive this is already DontDestroyOnLoad.
                ReplaceScenes = ReplaceOption.All,
            };

            Debug.Log($"[Bootstrap] Loading world scene '{sceneName}' as a global scene.");
            _networkManager.SceneManager.LoadGlobalScenes(data);
        }

        private void Update()
        {
            _world?.Tick(Time.deltaTime);
        }

        /// <summary>
        /// The command line wins over the inspector, so one build can be launched as a
        /// server or a client without touching the project. Address and port are read
        /// here too, since a dedicated server is configured entirely from its launch
        /// command.
        /// </summary>
        private void ResolveRole()
        {
            string[] args = System.Environment.GetCommandLineArgs();

            Role = LaunchArguments.TryGetRole(args, out AppRole fromArgs) ? fromArgs : _defaultRole;

            if (LaunchArguments.TryGetConnect(args, _port, out string host, out ushort connectPort))
            {
                _address = host;
                _port = connectPort;
            }

            // An explicit -port always wins; -connect host:port only supplies a default.
            if (LaunchArguments.TryGetPort(args, out ushort explicitPort))
                _port = explicitPort;

            _hasColdStartInvite = LaunchArguments.TryGetConnectLobby(args, out _coldStartLobbyId);

            Debug.Log($"[Bootstrap] Role {Role}, address {_address}:{_port}.");
        }

        private void Start()
        {
            // A dedicated server has no Steam client and no player to invite.
            if (Role.RunsClient() && _steam != null)
            {
                if (_steam.IsReady)
                    OnSteamReady();
                else
                    _steam.Ready += OnSteamReady;
            }

            /* Steam launched us with "+connect_lobby <id>" because a friend accepted an
             * invite while the game was closed. That is an explicit instruction to join
             * one specific person, so it outranks this build's default role — starting
             * our own server here would strand the player in an empty world and look
             * like the invite silently failed (TECH 8.1). */
            if (_hasColdStartInvite)
            {
                Debug.Log($"[Bootstrap] Cold-start invite to lobby {_coldStartLobbyId}; deferring to Steam.");
                return;
            }

            StartByRole();
        }

        private void StartByRole()
        {
            _state.Set(AppState.Connecting);

            if (Role == AppRole.Client)
            {
                Debug.Log($"[Bootstrap] Connecting to {_address}:{_port}.");
                _session.ConnectClient(_address, _port);
                return;
            }

            bool withLocalClient = Role == AppRole.HostedServer;

            /* Only a hosted server may fall back to connecting. A machine launched as a
             * dedicated server has no local player to hand over to, so a taken port is a
             * misconfiguration that should be reported, not quietly worked around. */
            if (withLocalClient && _connectIfPortTaken)
            {
                Debug.Log($"[Bootstrap] Serving on port {_port}, or connecting to it if taken.");
                _session.StartServerOrConnect(_address, _port);
                return;
            }

            Debug.Log($"[Bootstrap] Serving on port {_port}.");
            _session.StartServer(_port, withLocalClient);
        }

        private void OnSteamReady()
        {
            _steam.Ready -= OnSteamReady;
            _lobby.Initialize();

            if (!_hasColdStartInvite)
                return;

            _state.Set(AppState.Connecting);
            _ = _lobby.JoinAsync(_coldStartLobbyId);
        }

        /// <summary>
        /// Host a Steam lobby and serve over Steam P2P. Kept for when the Steam
        /// game-server transport lands; the address path is what v1 actually uses.
        /// </summary>
        public void HostSteamSession()
        {
            _state.Set(AppState.Connecting);
            _ = _lobby.HostAsync();
        }

        private void OnDestroy()
        {
            if (_steam != null)
                _steam.Ready -= OnSteamReady;

            if (_session != null)
                _session.ServerStarted -= OnServerStarted;

            // Order matters: the world writes a final snapshot on dispose, and it should
            // do that while the session is still up rather than during teardown.
            _world?.Dispose();
            _session?.Dispose();
            _lobby?.Dispose();
            ServiceLocator.Clear();
        }
    }
}
