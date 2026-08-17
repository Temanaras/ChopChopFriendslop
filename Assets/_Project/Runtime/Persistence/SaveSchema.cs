using System.Collections.Generic;
using ChopChop.Cabin;
using ChopChop.Items;
using ChopChop.World;
using MemoryPack;
using UnityEngine;

namespace ChopChop.Persistence
{
    public static class SaveFormat
    {
        /// <summary>
        /// Bump on any schema change, and add an upgrade step in
        /// <see cref="SaveSerializer"/>.
        /// </summary>
        public const uint Version = 1;

        /// <summary>
        /// Bump whenever generation output changes. Tree diffs index into generated
        /// arrays, so a change here means existing indices may point at different trees
        /// (TECH 6.3).
        /// </summary>
        /// <remarks>
        /// 2: the placement grid went from 8 to 16 cells and jitter was inset to keep a
        /// minimum gap, so every tree in the world moved and the indices diffs refer to
        /// now name different trees.
        /// 3: the starting clearing halved to a 30m radius, so chunks near the origin
        /// grow trees where they previously grew none.
        /// </remarks>
        public const uint WorldGenVersion = 3;
    }

    /// <summary>
    /// The complete world state (TECH 6.3).
    ///
    /// Every client holds a full copy of this (TECH 6.1) — no single machine owns the
    /// world. It is also the host migration payload (TECH 2.4), which is why saving and
    /// migration are not separate systems: a snapshot must be complete enough that a
    /// fresh host can load it and carry on.
    ///
    /// Note what is absent. Enemy positions, projectiles, ground loot, summoned mounts,
    /// time of day and weather are rebuilt on load rather than stored.
    /// </summary>
    [MemoryPackable]
    public partial class WorldSave
    {
        public uint SaveFormatVersion;
        public uint WorldGenVersion;

        /// <summary>
        /// Monotonic counter, incremented by the host on every autosave. On join the
        /// highest value wins outright and the joiner overwrites its own copy — there
        /// is no merging (TECH 6.1).
        /// </summary>
        public uint SaveVersion;

        public int WorldSeed;

        /// <summary>Authoritative elapsed world time. Regrowth is measured against this.</summary>
        public uint WorldTick;

        public CabinState Cabin;

        /// <summary>Keyed by <see cref="ChunkKey"/>. Chunks with no diffs have no entry.</summary>
        public Dictionary<long, ChunkSave> Chunks;

        [MemoryPackConstructor]
        public WorldSave(uint saveFormatVersion, uint worldGenVersion, uint saveVersion, int worldSeed,
            uint worldTick, CabinState cabin, Dictionary<long, ChunkSave> chunks)
        {
            SaveFormatVersion = saveFormatVersion;
            WorldGenVersion = worldGenVersion;
            SaveVersion = saveVersion;
            WorldSeed = worldSeed;
            WorldTick = worldTick;
            Cabin = cabin;
            Chunks = chunks;
        }

        public WorldSave() : this(SaveFormat.Version, SaveFormat.WorldGenVersion, 0, 0, 0,
            new CabinState(), new Dictionary<long, ChunkSave>()) { }

        /// <summary>A brand new world for the given seed.</summary>
        public static WorldSave CreateNew(int worldSeed)
        {
            WorldSave save = new();
            save.WorldSeed = worldSeed;
            return save;
        }
    }

    [MemoryPackable]
    public partial class ChunkSave
    {
        /// <summary>
        /// Last tick a player was subscribed to this chunk. Regrowth is computed from
        /// the gap since, never simulated (TECH 7.1), so an unvisited chunk costs
        /// nothing no matter how long it is left alone.
        /// </summary>
        public uint LastVisitedTick;

        public TreeDiff[] Diffs;

        [MemoryPackConstructor]
        public ChunkSave(uint lastVisitedTick, TreeDiff[] diffs)
        {
            LastVisitedTick = lastVisitedTick;
            Diffs = diffs;
        }

        public ChunkSave() : this(0, new TreeDiff[0]) { }
    }

    /// <summary>
    /// Per-player state, stored separately from the world (TECH 6.3).
    ///
    /// The split matters: a player's world copy is replaced wholesale on join, but the
    /// paperdoll travels with the player and is never overwritten by someone else's
    /// world (TECH 6.1).
    /// </summary>
    [MemoryPackable]
    public partial class PlayerSave
    {
        public ulong SteamId;

        /// <summary>Slot-indexed by <see cref="ItemSlot"/>. Never lost on death.</summary>
        public ItemStack[] Paperdoll;

        /// <summary>Carried items. Lost on death (TECH 9.3).</summary>
        public ItemStack[] Inventory;

        public Vector3 Position;
        public Quaternion Rotation;

        [MemoryPackConstructor]
        public PlayerSave(ulong steamId, ItemStack[] paperdoll, ItemStack[] inventory,
            Vector3 position, Quaternion rotation)
        {
            SteamId = steamId;
            Paperdoll = paperdoll;
            Inventory = inventory;
            Position = position;
            Rotation = rotation;
        }

        public PlayerSave() : this(0, new ItemStack[ItemSlots.Count], new ItemStack[0],
            Vector3.zero, Quaternion.identity) { }

        public static PlayerSave CreateNew(ulong steamId) => new() { SteamId = steamId };
    }
}
