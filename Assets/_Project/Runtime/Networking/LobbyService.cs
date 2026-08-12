using System;
using System.Threading.Tasks;
using Steamworks;
using Steamworks.Data;
using UnityEngine;

namespace ChopChop.Networking
{
    /// <summary>
    /// Steam lobby ownership and membership. Deliberately knows nothing about
    /// FishNet — it reports what happened and <see cref="SessionCoordinator"/>
    /// decides what to do about it.
    ///
    /// The host writes its SteamID into lobby data under <see cref="HostSteamIdKey"/>;
    /// joiners read it back and use it as the transport address. Steam auto-migrates
    /// lobby ownership when the owner leaves, which is the free half of host
    /// migration later on (TECH 8.2).
    /// </summary>
    public sealed class LobbyService : IDisposable
    {
        /// <summary>Lobby data key holding the host's SteamID64, as a decimal string.</summary>
        public const string HostSteamIdKey = "chopchop.host";

        public const int MaxMembers = 4;

        public Lobby? Current { get; private set; }

        /// <summary>We own a lobby and the host key is written. Time to start a server.</summary>
        public event Action<Lobby> LobbyHosted;

        /// <summary>We joined someone else's lobby and know their SteamID.</summary>
        public event Action<SteamId> HostResolved;

        public event Action<string> Failed;

        private bool _subscribed;

        /// <summary>
        /// Hook Steam callbacks. Must not run before <c>SteamClient.Init</c> has
        /// succeeded, which is why it is separate from the constructor — in local
        /// Tugboat testing Steam may never come up at all.
        /// </summary>
        public void Initialize()
        {
            if (_subscribed)
                return;

            SteamMatchmaking.OnLobbyEntered += HandleLobbyEntered;
            SteamFriends.OnGameLobbyJoinRequested += HandleJoinRequested;
            _subscribed = true;
        }

        public void Dispose()
        {
            if (!_subscribed)
                return;

            SteamMatchmaking.OnLobbyEntered -= HandleLobbyEntered;
            SteamFriends.OnGameLobbyJoinRequested -= HandleJoinRequested;
            _subscribed = false;
        }

        public async Task HostAsync()
        {
            Lobby? created = await SteamMatchmaking.CreateLobbyAsync(MaxMembers);

            if (created == null)
            {
                Failed?.Invoke("Steam refused to create a lobby.");
                return;
            }

            Lobby lobby = created.Value;
            lobby.SetFriendsOnly();
            lobby.SetJoinable(true);
            lobby.SetData(HostSteamIdKey, SteamClient.SteamId.Value.ToString());

            Current = lobby;
            Debug.Log($"[Lobby] Hosting {lobby.Id.Value} for up to {MaxMembers}.");
            LobbyHosted?.Invoke(lobby);
        }

        public async Task JoinAsync(SteamId lobbyId)
        {
            Lobby? joined = await SteamMatchmaking.JoinLobbyAsync(lobbyId);

            if (joined == null)
                Failed?.Invoke($"Could not join lobby {lobbyId.Value}.");
        }

        public void Leave()
        {
            Current?.Leave();
            Current = null;
        }

        /// <summary>Friend accepted an invite through the Steam overlay while we were running.</summary>
        private async void HandleJoinRequested(Lobby lobby, SteamId _)
        {
            try
            {
                await JoinAsync(lobby.Id);
            }
            catch (Exception e)
            {
                Failed?.Invoke($"Overlay join failed: {e.Message}");
            }
        }

        private void HandleLobbyEntered(Lobby lobby)
        {
            Current = lobby;

            // We created this one; HostAsync already raised LobbyHosted.
            if (lobby.IsOwnedBy(SteamClient.SteamId))
                return;

            string raw = lobby.GetData(HostSteamIdKey);

            if (!ulong.TryParse(raw, out ulong hostId) || hostId == 0)
            {
                Failed?.Invoke($"Lobby {lobby.Id.Value} has no usable '{HostSteamIdKey}' value.");
                return;
            }

            Debug.Log($"[Lobby] Entered {lobby.Id.Value}; host is {hostId}.");
            HostResolved?.Invoke(hostId);
        }
    }
}
