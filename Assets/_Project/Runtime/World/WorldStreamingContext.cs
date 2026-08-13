namespace ChopChop.World
{
    /// <summary>
    /// What a <see cref="WorldStreamer"/> needs from outside the scene it lives in.
    ///
    /// The streamer sits in the world scene, which the server loads *after* boot, so
    /// nothing in the boot scene can hold a serialised reference to it and it cannot
    /// hold one back. The bootstrap publishes this instead and the streamer collects it
    /// when it wakes up.
    /// </summary>
    public sealed class WorldStreamingContext
    {
        /// <summary>Authoritative on the server, a cache of what the server said on a client.</summary>
        public TreeDiffStore Diffs;

        /// <summary>
        /// Set only on a server, where residency must follow every player rather than
        /// one local one (TECH 5.4). Null on a client.
        /// </summary>
        public WorldStreamer.CentreProvider ServerCentres;

        /// <summary>The seed the server actually loaded, which may differ from the inspector.</summary>
        public int WorldSeed;
    }
}
