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

        /// <summary>
        /// Whether to stop at a menu, or launch straight into a session.
        ///
        /// Lives here rather than in the bootstrap because it is a statement about what
        /// a role means, and because the failure it guards against is invisible: a
        /// dedicated server waiting forever at a screen nobody is watching looks like a
        /// hang, not a bug. Nothing else in the boot path is allowed to reference the
        /// composition root, so this is also the only place a test can reach it.
        /// </summary>
        /// <param name="enabled">Whether this build shows a start screen at all.</param>
        /// <param name="launchedWithIntent">
        /// Whether the command line already answered the question the menu asks —
        /// <c>-server</c> or <c>-connect</c>. A cold-start Steam invite is handled
        /// earlier still, because it outranks the configured role entirely (TECH 8.1).
        /// </param>
        public static bool WaitsOnStartScreen(this AppRole role, bool enabled, bool launchedWithIntent)
            => enabled && role.RunsClient() && !launchedWithIntent;
    }
}
