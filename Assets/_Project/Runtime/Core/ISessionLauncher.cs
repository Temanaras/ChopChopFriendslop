namespace ChopChop.Core
{
    /// <summary>
    /// How a menu asks for a session to begin.
    ///
    /// The composition root implements this and registers itself. The menu could not
    /// call it directly: everything depends on the bootstrap and the bootstrap depends
    /// on everything, so nothing is allowed to reference it back (TECH 3). Declaring the
    /// contract here keeps the port, the address and the role in one place — the
    /// bootstrap already owns them, and a menu holding its own copy would drift the first
    /// time either changed.
    /// </summary>
    public interface ISessionLauncher
    {
        /// <summary>Address to show in the join field before the player types anything.</summary>
        string DefaultAddress { get; }

        /// <summary>Start a server here and play on it.</summary>
        void HostNewGame();

        /// <summary>Join someone else's. Host or "host:port"; the port is optional.</summary>
        void JoinGame(string address);
    }
}
