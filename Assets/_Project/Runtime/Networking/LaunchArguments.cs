using System;
using ChopChop.Core;

namespace ChopChop.Networking
{
    /// <summary>
    /// Everything this process can be told at launch.
    ///
    /// Steam launches the executable with <c>+connect_lobby &lt;id&gt;</c> when a friend
    /// accepts an invite while the game is closed. Without parsing this, cold-start
    /// invites fail silently — the game opens to the menu and the player assumes the
    /// invite was broken (TECH 8.1).
    ///
    /// The role flags matter because the server is selected at runtime rather than
    /// by build target. Unity's dedicated-server subtarget defines <c>UNITY_SERVER</c>,
    /// and FishyFacepunch compiles its entire Steam path out under that define — so a
    /// true server build would have no Steam transport at all, closing off the invite
    /// support we want next. One binary, a flag, and both futures stay open.
    /// </summary>
    public static class LaunchArguments
    {
        public const string ConnectLobbyFlag = "+connect_lobby";

        /// <summary>Run as a headless authoritative server.</summary>
        public const string ServerFlag = "-server";

        /// <summary>Port to serve on, or to connect to when paired with -connect.</summary>
        public const string PortFlag = "-port";

        /// <summary>Address to join, as <c>host</c> or <c>host:port</c>.</summary>
        public const string ConnectFlag = "-connect";

        /// <summary>
        /// Reads the role from the command line. Returns false when the command line
        /// says nothing about it, leaving the caller to apply its own default.
        /// </summary>
        public static bool TryGetRole(out AppRole role)
            => TryGetRole(Environment.GetCommandLineArgs(), out role);

        public static bool TryGetRole(string[] args, out AppRole role)
        {
            role = AppRole.Client;

            if (args == null)
                return false;

            // -server wins if both are present: a process told to serve should serve,
            // and silently becoming a client instead would be very confusing to debug.
            if (HasFlag(args, ServerFlag))
            {
                role = AppRole.Server;
                return true;
            }

            if (TryGetValue(args, ConnectFlag, out string _))
            {
                role = AppRole.Client;
                return true;
            }

            return false;
        }

        public static bool TryGetPort(out ushort port)
            => TryGetPort(Environment.GetCommandLineArgs(), out port);

        public static bool TryGetPort(string[] args, out ushort port)
        {
            port = 0;

            return TryGetValue(args, PortFlag, out string raw)
                   && ushort.TryParse(raw, out port)
                   && port != 0;
        }

        /// <summary>
        /// Reads <c>-connect host</c> or <c>-connect host:port</c>. When no port is
        /// embedded, <paramref name="port"/> is left at <paramref name="defaultPort"/>
        /// so an explicit <c>-port</c> can still supply it.
        /// </summary>
        public static bool TryGetConnect(string[] args, ushort defaultPort, out string host, out ushort port)
        {
            host = null;
            port = defaultPort;

            if (!TryGetValue(args, ConnectFlag, out string raw) || string.IsNullOrWhiteSpace(raw))
                return false;

            int separator = raw.LastIndexOf(':');

            if (separator < 0)
            {
                host = raw;
                return true;
            }

            // A trailing colon with no digits, or a nonsense port, is a typo worth
            // surfacing rather than silently treating the whole string as a hostname.
            if (!ushort.TryParse(raw.Substring(separator + 1), out ushort parsed) || parsed == 0)
                return false;

            host = raw.Substring(0, separator);
            port = parsed;
            return !string.IsNullOrWhiteSpace(host);
        }

        public static bool HasFlag(string[] args, string flag)
        {
            if (args == null)
                return false;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool TryGetValue(string[] args, string flag, out string value)
        {
            value = null;

            if (args == null)
                return false;

            for (int i = 0; i < args.Length - 1; i++)
            {
                if (!string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    continue;

                value = args[i + 1];
                return true;
            }

            return false;
        }

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
