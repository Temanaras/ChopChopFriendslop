using FishNet.Broadcast;

namespace ChopChop.World
{
    /* Broadcasts rather than RPCs throughout (TECH 17): trees are not NetworkObjects and
     * never will be, so there is no entity to hang an RPC on. This is systems-level
     * messaging about a chunk, not about a spawned thing. */

    /// <summary>
    /// Client tells the server which chunks it is standing near. The server replies with
    /// each chunk's diffs and starts sending it updates for them.
    /// </summary>
    public struct SubscribeChunksBroadcast : IBroadcast
    {
        public long[] ChunkKeys;
    }

    /// <summary>
    /// A chunk's full diff list. Sent on subscribe, which doubles as the late-join path
    /// for trees — a joining client gets the same message as a client that just walked
    /// over (TECH 8.4).
    /// </summary>
    public struct ChunkDiffsBroadcast : IBroadcast
    {
        public long ChunkKey;
        public TreeDiff[] Diffs;
    }

    /// <summary>Sent only to clients subscribed to that chunk.</summary>
    public struct TreeDamagedBroadcast : IBroadcast
    {
        public long ChunkKey;
        public ushort LocalIndex;
        public byte HealthRemaining;

        /// <summary>
        /// How the server graded the swing. Appended, never renumbered — same rule as
        /// EnemyState (TECH 10.3). Sent to everyone subscribed rather than only the
        /// chopper, so a second player watching sees the same wedge open up.
        /// </summary>
        public ChopGrade Grade;
    }

    /// <summary>Sent only to clients subscribed to that chunk.</summary>
    public struct TreeFelledBroadcast : IBroadcast
    {
        public long ChunkKey;
        public ushort LocalIndex;
        public uint FelledAtTick;
    }

    /// <summary>Client asks to chop. The server decides whether anything happens.</summary>
    public struct ChopRequestBroadcast : IBroadcast
    {
        public long ChunkKey;
        public ushort LocalIndex;

        /// <summary>
        /// The tick the player pressed on, so the swing is graded on when they actually
        /// swung rather than on when the packet happened to land — at 100ms that is three
        /// ticks of drift, which is most of a band.
        ///
        /// Not trusted: the server bounds it against its own clock and refuses anything
        /// outside the window (see TreeServer). A client can shift its swing within a
        /// few ticks, which is exactly the latency it is being compensated for.
        /// </summary>
        public uint Tick;
    }

    /// <summary>
    /// Why a chop did nothing. Silence would read as a bug, so a refusal is always
    /// answered — the client turns this into a bounce or a thunk (TECH 5.6).
    /// </summary>
    public struct ChopRejectedBroadcast : IBroadcast
    {
        public long ChunkKey;
        public ushort LocalIndex;
        public ChopRejection Reason;
        public byte RequiredTier;
    }

    public enum ChopRejection : byte
    {
        None = 0,

        /// <summary>Index is not a tree in that chunk, or the chunk is not loaded server-side.</summary>
        NoSuchTree = 1,

        AlreadyFelled = 2,

        /// <summary>Too far away, even allowing for latency tolerance.</summary>
        OutOfRange = 3,

        /// <summary>Equipped axe tier is below the tree's. A hard gate: zero damage.</summary>
        TierTooLow = 4,

        /// <summary>Swinging faster than the axe allows.</summary>
        TooSoon = 5,

        /// <summary>
        /// The tick claimed is too far from the server's own for the grade to mean
        /// anything. Either a very bad connection or a client trying to pick its moment.
        /// </summary>
        Mistimed = 6,

        /// <summary>Struck the wrong half of the arc. Nothing wrong with the request.</summary>
        Missed = 7,
    }
}
