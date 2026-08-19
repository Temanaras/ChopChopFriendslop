using System.Collections.Generic;
using ChopChop.Core;
using ChopChop.Networking;
using ChopChop.Persistence;
using ChopChop.World;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Managing.Scened;
using FishNet.Transporting;
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
    public sealed class GameBootstrap : MonoBehaviour, ISessionLauncher
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

        [Tooltip("Wait on the start screen instead of launching straight into a world. " +
                 "Ignored when the command line already said what to do, and never shown " +
                 "by a dedicated server, which has nobody to show it to.")]
        [SerializeField] private bool _showStartScreen = true;

        [Header("World")]
        [Tooltip("Seed used when no save exists yet. Server-side only.")]
        [SerializeField] private int _newWorldSeed = 1337;

        [Tooltip("Scene the server loads as a global scene once it is listening. Clients " +
                 "receive it automatically on connect.")]
        [SerializeField] private string _worldScene = "Assets/Scenes/Clearing.unity";

        [Tooltip("Biomes the server generates from. Must match what clients use, or the " +
                 "two will disagree about which trees exist.")]
        [SerializeField] private ChopChop.Biomes.BiomeSet _biomes;

        [Tooltip("Placeholder targets so the gun has something to prove itself against.")]
        [SerializeField] private GameObject _targetDummy;

        [Tooltip("Enemy the director spawns. Leave empty to disable spawning entirely.")]
        [SerializeField] private FishNet.Object.NetworkObject _enemyPrefab;

        [Header("Cabin")]
        [Tooltip("The home cabin, spawned at the world origin. Everything inside it comes " +
                 "along with it, so adding fixtures is a change to this prefab alone.")]
        [SerializeField] private GameObject _cabinPrefab;

        [Header("Items")]
        [Tooltip("Every item in the game. Validated at boot; duplicate or missing ids " +
                 "are refused rather than allowed to corrupt saves.")]
        [SerializeField] private ChopChop.Items.ItemRegistry _items;

        [Tooltip("Given to a player the first time they spawn, so the axe gate can be felt.")]
        [SerializeField] private ChopChop.Items.ItemDefinition _startingAxe;

        [Tooltip("What a felled tree drops.")]
        [SerializeField] private ChopChop.Items.ItemDefinition _wood;

        [Tooltip("Prefab for a stack of something lying on the ground.")]
        [SerializeField] private GameObject _droppedItem;

        /// <summary>Resolved once at boot; the command line wins over the inspector.</summary>
        public AppRole Role { get; private set; }

        private AppStateMachine _state;
        private TransportRouter _router;
        private LobbyService _lobby;
        private SessionCoordinator _session;
        private WorldSaveService _world;

        private TreeDiffStore _diffs;
        private TreeServer _treeServer;
        private LootService _loot;
        private TreeClient _treeClient;
        private ChunkStore _serverChunks;
        private RegrowthService _regrowth;
        private ChopChop.Combat.WeaponServer _weapons;
        private ChopChop.AI.EnemyDirector _director;
        private ChopChop.Cabin.CabinStorage _storage;
        private WorldStreamingContext _streamingContext;
        private WorldStreamer _streamer;

        private bool _hasColdStartInvite;
        private ulong _coldStartLobbyId;
        private bool _launchedWithIntent;

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

            // Registered as the interface, not the concrete type: the menu is only allowed
            // to know about the contract in Core, never about the bootstrap.
            ServiceLocator.Register<ISessionLauncher>(this);

            if (_steam != null)
                ServiceLocator.Register(_steam);

            /* One diff store per process. On a server it is the authority; on a client a
             * cache of what the server said. A hosted server shares one instance, which
             * is correct — there is only one truth in that process. */
            /* Validated before anything can hold an item. A duplicate id means two items
             * share a save entry and one silently becomes the other; a missing one means
             * an item a player owns stops existing on load. Both are silent otherwise. */
            if (_items != null && !_items.Validate())
                Debug.LogError("[Bootstrap] Item registry is invalid; items will not behave correctly.");

            if (_items != null)
                ServiceLocator.Register(_items);

            _diffs = new TreeDiffStore();
            ServiceLocator.Register(_diffs);

            _streamingContext = new WorldStreamingContext { Diffs = _diffs, WorldSeed = _newWorldSeed };
            ServiceLocator.Register(_streamingContext);

            if (Role.RunsClient())
            {
                _treeClient = new TreeClient(_networkManager, _diffs);
                ServiceLocator.Register(_treeClient);
                _treeClient.TreeChanged += HandleTreeChanged;
            }

            /* The world is server-owned, so this is only wired where a server runs.
             * Deliberately not created here: a HostedServer that finds its port taken
             * falls back to being a plain client, and a client holding a world save
             * service would be a second writer pointed at the same file. */
            if (Role.RunsServer())
                _session.ServerStarted += OnServerStarted;
        }

        /// <summary>
        /// A reclaimed tree is standing again, so the collider band has to be told —
        /// otherwise the next stream would be the first time anything could hit it.
        /// </summary>
        private void HandleTreeReclaimed(long chunkKey, ushort localIndex)
        {
            // Nothing to remove; the band adds it back on its next pass now that the
            // diff is gone. This hook exists for loot cleanup and effects later.
        }

        /// <summary>A felled tree loses its collider at once rather than at the next stream.</summary>
        private void HandleTreeChanged(long chunkKey, ushort localIndex)
        {
            if (!_diffs.IsFelled(chunkKey, localIndex))
                return;

            if (_streamer != null)
                _streamer.OnTreeFelled(chunkKey, localIndex);
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

            /* The server generates its own chunks. It cannot rely on the streamer in the
             * scene, because it needs tree data to validate chops whether or not anything
             * is being drawn — and on a headless server, nothing is. */
            _serverChunks = new ChunkStore(_world.World.WorldSeed, _biomes, WorldGenSettings.Default);

            _world.Diffs = _diffs;
            _world.RestoreDiffs();

            _regrowth = new RegrowthService(_diffs, _biomes);
            _regrowth.TreeReclaimed += HandleTreeReclaimed;
            ServiceLocator.Register(_regrowth);

            _treeServer = new TreeServer(_networkManager, _serverChunks, _diffs,
                () => _world.World?.WorldTick ?? 0u, _regrowth)
            {
                // The gate now reads the server's copy of the paperdoll. Before this it
                // was a constant, so every tier looked choppable.
                AxeTierProvider = AxeTierFor,
            };

            ServiceLocator.Register(_treeServer);

            /* Closes the loop: a felled tree becomes wood on the ground, which becomes
             * wood in a backpack, which becomes wood in the chest. Registered before
             * anything can fell a tree, because loot with no service behind it would
             * simply never appear and would look like a broken drop rate. */
            _loot = new LootService(_networkManager, _droppedItem)
            {
                InventoryProvider = InventoryFor,
                WoodItemId = _wood != null ? _wood.Id : (ushort)0,
            };

            _loot.Attach(_treeServer);
            ServiceLocator.Register(_loot);

            if (_wood == null)
                Debug.LogWarning("[Loot] No wood item assigned; felled trees will drop nothing.");

            _storage = new ChopChop.Cabin.CabinStorage(_world.World.Cabin, _items);
            ServiceLocator.Register(_storage);

            _networkManager.ServerManager.OnRemoteConnectionState += HandleClientForItems;

            _weapons = new ChopChop.Combat.WeaponServer(_networkManager, ~0);
            ServiceLocator.Register(_weapons);

            /* Deferred until the world scene is in. LoadWorldScene replaces every scene,
             * so anything spawned before it is destroyed on arrival — the dummies did
             * spawn, and were gone a frame later. */
            _networkManager.SceneManager.OnLoadEnd += HandleWorldSceneLoaded;

            // World time advances on the tick loop, not per frame, so it is the same
            // clock everywhere regardless of how fast the server is rendering.
            _networkManager.TimeManager.OnTick += _world.AdvanceTick;

            // The streamer picks these up when the world scene loads.
            _streamingContext.WorldSeed = _world.World.WorldSeed;
            _streamingContext.ServerCentres = CollectServerCentres;

            Debug.Log($"[World] {_diffs.ChunkCount} chunk(s) carry player changes.");

            LoadWorldScene();
            _state.Set(AppState.InGame);
        }

        /// <summary>
        /// The equipped axe tier for a connection, read from the server's own paperdoll.
        /// Bare hands are tier 0, which fells nothing.
        /// </summary>
        private byte AxeTierFor(NetworkConnection connection)
        {
            if (connection?.FirstObject == null)
                return 0;

            return connection.FirstObject.TryGetComponent(out ChopChop.Player.PlayerPaperdoll paperdoll)
                ? paperdoll.AxeTier
                : (byte)0;
        }

        /// <summary>A connection's carried container, or null if they have no player yet.</summary>
        private ChopChop.Items.ItemContainer InventoryFor(NetworkConnection connection)
        {
            if (connection?.FirstObject == null)
                return null;

            return connection.FirstObject.TryGetComponent(out ChopChop.Player.PlayerPaperdoll paperdoll)
                ? paperdoll.Inventory
                : null;
        }

        /// <summary>
        /// Hands a new player their starting kit and binds the registry, which lives
        /// outside the scene the player is spawned into.
        /// </summary>
        private void HandleClientForItems(NetworkConnection connection, RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState != RemoteConnectionState.Started)
                return;

            StartCoroutine(EquipWhenSpawned(connection));
        }

        private System.Collections.IEnumerator EquipWhenSpawned(NetworkConnection connection)
        {
            // The player object is spawned a moment after the connection is up.
            float deadline = Time.time + 10f;

            while (connection.FirstObject == null && Time.time < deadline)
                yield return null;

            if (connection.FirstObject == null)
                yield break;

            if (!connection.FirstObject.TryGetComponent(out ChopChop.Player.PlayerPaperdoll paperdoll))
                yield break;

            paperdoll.Bind(_items);

            // Give the HUD its starting ammo reading rather than leaving it at zero until
            // the first shot.
            _weapons?.Reload(connection);

            /* Handed out rather than granted as a stat: the axe is an item, so it can be
             * dropped in the chest for whoever needs it next (TECH 2.3). */
            if (_startingAxe != null && !paperdoll.HasEquipped(ChopChop.Items.ItemSlot.Axe))
                paperdoll.TryEquip(new ChopChop.Items.ItemStack(_startingAxe.Id, 1), out _);
        }

        private void HandleWorldSceneLoaded(SceneLoadEndEventArgs args)
        {
            _networkManager.SceneManager.OnLoadEnd -= HandleWorldSceneLoaded;
            SpawnTargetDummies();
            SpawnCabin();

            /* The director needs the density field, and the density field needs a chunk
             * store, so it can only exist once the world is up. Enemy positions are never
             * saved (TECH 6.3) — everything it spawns is rebuilt from scratch each run. */
            _director = new ChopChop.AI.EnemyDirector(
                _networkManager, _enemyPrefab, new DensityField(_serverChunks), _biomes);

            ServiceLocator.Register(_director);
        }

        /// <summary>
        /// Raises the cabin at the world origin, which the clearing mask keeps free of
        /// trees (TECH 5.7).
        ///
        /// Spawned rather than authored into the scene so there is one prefab to edit and
        /// one place that decides where it stands. It has to happen here rather than at
        /// boot for the same reason the target dummies do: loading the world scene
        /// replaces every scene, and anything spawned before that goes with them.
        ///
        /// This is the only line the bootstrap will ever need about the cabin. What is
        /// inside it is the prefab's business, and the fixtures bind themselves through
        /// the context below.
        /// </summary>
        private void SpawnCabin()
        {
            if (_cabinPrefab == null)
            {
                Debug.LogWarning("[Cabin] No cabin prefab assigned; the clearing will be empty.");
                return;
            }

            GameObject instance = Instantiate(_cabinPrefab, Vector3.zero, Quaternion.identity);
            _networkManager.ServerManager.Spawn(instance);

            if (instance.TryGetComponent(out ChopChop.Cabin.CabinBuilding cabin))
                cabin.Bind(new ChopChop.Cabin.CabinContext(InventoryFor));
            else
                Debug.LogError("[Cabin] The cabin prefab has no CabinBuilding; nothing inside it will be wired.");
        }

        /// <summary>
        /// Something to shoot at until there is an enemy to shoot at. Server-spawned so
        /// they are real NetworkObjects with real server-owned health, which is what
        /// makes them worth testing against — a local prop would prove nothing.
        /// </summary>
        private void SpawnTargetDummies()
        {
            if (_targetDummy == null)
                return;

            Vector3[] positions =
            {
                new(6f, 0f, 14f),
                new(-9f, 0f, 16f),
                new(2f, 0f, 22f),
            };

            foreach (Vector3 position in positions)
            {
                GameObject instance = Instantiate(_targetDummy, position, Quaternion.identity);
                _networkManager.ServerManager.Spawn(instance);
            }

            Debug.Log($"[Combat] Spawned {positions.Length} target dummies.");
        }

        /// <summary>
        /// Every player position the server must keep chunks and colliders around
        /// (TECH 5.4). A tree with no collider cannot be hit, so a player whose
        /// surroundings the server has not loaded could not chop anything.
        /// </summary>
        private void CollectServerCentres(List<Vector3> into)
        {
            foreach (NetworkConnection connection in _networkManager.ServerManager.Clients.Values)
            {
                if (connection?.FirstObject != null)
                    into.Add(connection.FirstObject.transform.position);
            }
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
            _director?.Tick(Time.deltaTime);

            // The streamer appears with the world scene, so it is found rather than wired.
            if (_streamer == null)
                _streamer = FindObjectOfType<WorldStreamer>();
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

            bool roleFromArgs = LaunchArguments.TryGetRole(args, out AppRole fromArgs);
            Role = roleFromArgs ? fromArgs : _defaultRole;

            bool connectFromArgs = LaunchArguments.TryGetConnect(args, _port, out string host, out ushort connectPort);

            if (connectFromArgs)
            {
                _address = host;
                _port = connectPort;
            }

            /* The command line is an instruction, not a preference. Somebody who launched
             * with -server or -connect has already answered the question the start screen
             * asks, and stopping to ask it again would strand a scripted launch or a
             * headless box at a menu nobody is watching. */
            _launchedWithIntent = roleFromArgs || connectFromArgs;

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

            if (ShouldWaitOnStartScreen())
            {
                Debug.Log("[Bootstrap] Waiting on the start screen.");
                _state.Set(AppState.Menu);
                return;
            }

            StartByRole();
        }

        /// <summary>
        /// A dedicated server never waits: it has no screen and nobody to press a button.
        /// Neither does a launch that already carried its instructions on the command
        /// line, nor an invite accepted from Steam.
        /// </summary>
        private bool ShouldWaitOnStartScreen()
            => _showStartScreen && Role.RunsClient() && !_launchedWithIntent;

        // ---------------- ISessionLauncher ----------------

        public string DefaultAddress => _address;

        public void HostNewGame()
        {
            Role = AppRole.HostedServer;

            /* StartServer rather than StartServerOrConnect. The fallback exists so several
             * editor instances can launch identically and sort themselves out (TECH 15),
             * but somebody who pressed "New Game" asked for their own world — silently
             * joining a stranger's because a port was busy is the wrong answer to that. */
            Debug.Log($"[Bootstrap] New game; serving on port {_port}.");
            _state.Set(AppState.Connecting);
            _session.StartServer(_port, true);
        }

        public void JoinGame(string address)
        {
            if (!string.IsNullOrWhiteSpace(address))
                ApplyAddress(address.Trim());

            Role = AppRole.Client;

            Debug.Log($"[Bootstrap] Joining {_address}:{_port}.");
            _state.Set(AppState.Connecting);
            _session.ConnectClient(_address, _port);
        }

        /// <summary>
        /// Accepts "host" or "host:port". Typing a port is the kind of thing people do
        /// without being asked, and dropping it silently would connect them somewhere
        /// they did not choose.
        /// </summary>
        private void ApplyAddress(string address)
        {
            int separator = address.LastIndexOf(':');

            if (separator > 0 && ushort.TryParse(address[(separator + 1)..], out ushort typedPort))
            {
                _address = address[..separator];
                _port = typedPort;
                return;
            }

            _address = address;
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

            if (_treeClient != null)
                _treeClient.TreeChanged -= HandleTreeChanged;

            if (_regrowth != null)
                _regrowth.TreeReclaimed -= HandleTreeReclaimed;

            if (_world != null && _networkManager != null)
                _networkManager.TimeManager.OnTick -= _world.AdvanceTick;

            if (_networkManager != null)
                _networkManager.ServerManager.OnRemoteConnectionState -= HandleClientForItems;

            // Order matters: the world writes a final snapshot on dispose, and it should
            // do that while the session is still up rather than during teardown.
            _director?.Dispose();
            _weapons?.Dispose();
            _loot?.Dispose();
            _treeServer?.Dispose();
            _treeClient?.Dispose();
            _world?.Dispose();
            _session?.Dispose();
            _lobby?.Dispose();
            ServiceLocator.Clear();
        }
    }
}
