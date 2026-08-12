using System;

namespace ChopChop.Networking
{
    /// <summary>
    /// Steam launches the executable with <c>+connect_lobby &lt;id&gt;</c> when a friend
    /// accepts an invite while the game is closed. Without parsing this, cold-start
    /// invites fail silently — the game opens to the menu and the player assumes the
    /// invite was broken (TECH 8.1).
    /// </summary>
    public static class LaunchArguments
    {
        public const string ConnectLobbyFlag = "+connect_lobby";

        public static bool TryGetConnectLobby(out ulong lobbyId)
            => TryGetConnectLobby(Environment.GetCommandLineArgs(), out lobbyId);

        /// <summary>Overload taking explicit args so this stays unit-testable.</summary>
        public static bool TryGetConnectLobby(string[] args, out ulong lobbyId)
        {
            lobbyId = 0;

            if (args == null)
                return false;

            for (int i = 0; i < args.Length - 1; i++)
            {
                if (!string.Equals(args[i], ConnectLobbyFlag, StringComparison.OrdinalIgnoreCase))
                    continue;

                return ulong.TryParse(args[i + 1], out lobbyId) && lobbyId != 0;
            }

            return false;
        }
    }
}
