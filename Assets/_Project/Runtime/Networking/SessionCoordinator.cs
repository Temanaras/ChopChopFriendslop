using System;
using FishNet.Managing;
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

        public SessionCoordinator(NetworkManager networkManager, TransportRouter router, LobbyService lobby)
        {
            _networkManager = networkManager ? networkManager : throw new ArgumentNullException(nameof(networkManager));
            _router = router ?? throw new ArgumentNullException(nameof(router));
            _lobby = lobby ?? throw new ArgumentNullException(nameof(lobby));

            _lobby.LobbyHosted += HandleLobbyHosted;
            _lobby.HostResolved += HandleHostResolved;
        }

        public void Dispose()
        {
            _lobby.LobbyHosted -= HandleLobbyHosted;
            _lobby.HostResolved -= HandleHostResolved;
        }

        // ---------------- Steam path ----------------

        private void HandleLobbyHosted(Lobby lobby)
        {
            _router.Use(TransportMode.Steam);
            StartListenServer();
        }

        private void HandleHostResolved(SteamId hostId)
        {
            _router.Use(TransportMode.Steam);
            _router.SetClientAddress(hostId.Value.ToString());
            _networkManager.ClientManager.StartConnection();
        }

        // ---------------- Local testing path ----------------

        /// <summary>
        /// Start a listen server on Tugboat. Used for multi-instance testing, where
        /// Steam P2P cannot connect to itself.
        /// </summary>
        public void StartLocalHost(ushort port)
        {
            _router.Use(TransportMode.Local);
            _router.SetPort(port);
            StartListenServer();
        }

        public void JoinLocal(string address, ushort port)
        {
            _router.Use(TransportMode.Local);
            _router.SetPort(port);
            _router.SetClientAddress(address);
            _networkManager.ClientManager.StartConnection();
        }

        // ---------------- Shared ----------------

        private void StartListenServer()
        {
            if (!_networkManager.ServerManager.StartConnection())
            {
                Debug.LogError("[Session] Server failed to start.");
                return;
            }

            // The host is also a player, so it connects a local client to itself.
            _networkManager.ClientManager.StartConnection();
        }

        public void Stop()
        {
            _networkManager.ClientManager.StopConnection();
            _networkManager.ServerManager.StopConnection(sendDisconnectMessage: true);
            _lobby.Leave();
        }
    }
}
