using System;
using FishNet.Managing;
using FishNet.Transporting;
using Steamworks;
using Steamworks.Data;
using UnityEngine;

namespace ChopChop.Networking
{
    /// <summary>
    /// Translates lobby events into FishNet connections.
    ///
    /// Host-authoritative listen server: the host runs a server *and* a local
    /// client, so it is simultaneously authority and player (TECH 4.1).
    /// </summary>
    public sealed class SessionCoordinator : IDisposable
    {
        private readonly NetworkManager _networkManager;
        private readonly TransportRouter _router;
        private readonly LobbyService _lobby;

        /// <summary>True between asking for a server and hearing back about it.</summary>
        private bool _startingHost;

        /// <summary>Whether a client in this same process should connect once the server is up.</summary>
        private bool _attachLocalClient;

        /// <summary>Where to fall back to if the server can't take the port.</summary>
        private string _fallbackAddress;
        private ushort _fallbackPort;
        private bool _fallbackToJoin;

        /// <summary>
        /// The server is listening. This is where server-owned systems come up — the
        /// world save above all, since the server owns it and clients hold no copy.
        /// </summary>
        public event Action ServerStarted;

        public SessionCoordinator(NetworkManager networkManager, TransportRouter router, LobbyService lobby)
        {
            _networkManager = networkManager ? networkManager : throw new ArgumentNullException(nameof(networkManager));
            _router = router ?? throw new ArgumentNullException(nameof(router));
            _lobby = lobby ?? throw new ArgumentNullException(nameof(lobby));

            _lobby.LobbyHosted += HandleLobbyHosted;
            _lobby.HostResolved += HandleHostResolved;
            _networkManager.ServerManager.OnServerConnectionState += HandleServerState;
        }

        public void Dispose()
        {
            _lobby.LobbyHosted -= HandleLobbyHosted;
            _lobby.HostResolved -= HandleHostResolved;

            if (_networkManager != null)
                _networkManager.ServerManager.OnServerConnectionState -= HandleServerState;
        }

        // ---------------- Steam path ----------------

        private void HandleLobbyHosted(Lobby lobby)
        {
            _router.Use(TransportMode.Steam);
            StartServerInternal(withLocalClient: true);
        }

        private void HandleHostResolved(SteamId hostId)
        {
            _router.Use(TransportMode.Steam);
            _router.SetClientAddress(hostId.Value.ToString());
            _networkManager.ClientManager.StartConnection();
        }

        // ---------------- Address path ----------------

        /// <summary>
        /// Bring up the authoritative server.
        /// </summary>
        /// <param name="withLocalClient">
        /// True for <see cref="AppRole.HostedServer"/>, where a client shares the
        /// process. False for a headless <see cref="AppRole.Server"/>, which has no
        /// local player at all. Nothing downstream of here is allowed to care which
        /// one it was.
        /// </param>
        public void StartServer(ushort port, bool withLocalClient)
        {
            _router.Use(TransportMode.Local);
            _router.SetPort(port);
            StartServerInternal(withLocalClient);
        }

        public void ConnectClient(string address, ushort port)
        {
            _router.Use(TransportMode.Local);
            _router.SetPort(port);
            _router.SetClientAddress(address);
            _networkManager.ClientManager.StartConnection();
        }

        /// <summary>
        /// Take the port if it's free, otherwise connect to whoever already has it.
        ///
        /// Several editor instances is the target test configuration (TECH 15), and
        /// they share one copy of the scene, so no serialized flag can tell them apart.
        /// This lets every instance launch with identical settings in any order: the
        /// first one up serves and the rest connect to it.
        /// </summary>
        public void StartServerOrConnect(string address, ushort port)
        {
            _fallbackAddress = address;
            _fallbackPort = port;
            _fallbackToJoin = true;

            StartServer(port, withLocalClient: true);
        }

        // ---------------- Shared ----------------

        private void StartServerInternal(bool withLocalClient)
        {
            _startingHost = true;
            _attachLocalClient = withLocalClient;
            _networkManager.ServerManager.StartConnection();
        }

        /// <summary>
        /// The local client is started here rather than straight after StartConnection
        /// because the transports report a failed bind asynchronously — Tugboat returns
        /// true and only then discovers the port is taken. Connecting a client to a
        /// server that never came up would look like a hang.
        /// </summary>
        private void HandleServerState(ServerConnectionStateArgs args)
        {
            if (!_startingHost)
                return;

            // Only the transport the client is about to use decides this. Multipass
            // starts all of them, so FishyFacepunch reporting Started tells us nothing
            // about whether Tugboat got its port.
            if (args.TransportIndex != _router.ActiveTransportIndex)
                return;

            if (args.ConnectionState == LocalConnectionState.Started)
            {
                _startingHost = false;
                _fallbackToJoin = false;

                ServerStarted?.Invoke();

                if (!_attachLocalClient)
                {
                    // Headless. Nobody is playing on this machine.
                    Debug.Log("[Session] Server is up and listening.");
                    return;
                }

                // Sharing the process with a client, so connect it over the loopback
                // like any other client would.
                _networkManager.ClientManager.StartConnection();
            }
            else if (args.ConnectionState == LocalConnectionState.Stopped)
            {
                _startingHost = false;

                if (_fallbackToJoin)
                {
                    _fallbackToJoin = false;
                    Debug.Log($"[Session] Port {_fallbackPort} is already taken; connecting to that instance instead.");

                    // Multipass started the other transports too. Drop the whole server
                    // before connecting, or this instance would be a client and a
                    // half-open server at the same time.
                    _networkManager.ServerManager.StopConnection(sendDisconnectMessage: false);
                    ConnectClient(_fallbackAddress, _fallbackPort);
                }
                else
                {
                    Debug.LogError("[Session] Server failed to start.");
                }
            }
        }

        public void Stop()
        {
            _networkManager.ClientManager.StopConnection();
            _networkManager.ServerManager.StopConnection(sendDisconnectMessage: true);
            _lobby.Leave();
        }
    }
}
