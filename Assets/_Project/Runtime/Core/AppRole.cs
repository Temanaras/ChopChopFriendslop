namespace ChopChop.Core
{
    /// <summary>
    /// What this process is for. A session belongs to a server; the server owns the
    /// world save and clients hold no copy of it.
    ///
    /// <see cref="HostedServer"/> is a convenience, not a third topology. It runs a
    /// server and a client in one process so that clicking Play just works, exactly
    /// as Minecraft's singleplayer runs an in-process server. The important part is
    /// that it changes nothing about how the two halves talk to each other.
    ///
    /// <b>Gameplay code must never branch on this.</b> Server logic checks
    /// <c>IsServerInitialized</c>, client logic checks <c>IsClientInitialized</c>,
    /// and neither asks whether the other happens to live in the same process. That
    /// rule is what keeps the headless build honest — the moment something asks "am
    /// I the host?", the dedicated server quietly stops matching what everyone
    /// playtests.
    /// </summary>
    public enum AppRole : byte
    {
        /// <summary>Connects to an address. Never assumes a server is present.</summary>
        Client = 0,

        /// <summary>Headless and authoritative. Owns the save. No local player.</summary>
        Server = 1,

        /// <summary>A server and a client in one process. What normal play uses.</summary>
        HostedServer = 2,
    }

    public static class AppRoleExtensions
    {
        public static bool RunsServer(this AppRole role)
            => role == AppRole.Server || role == AppRole.HostedServer;

        public static bool RunsClient(this AppRole role)
            => role == AppRole.Client || role == AppRole.HostedServer;
    }
}
