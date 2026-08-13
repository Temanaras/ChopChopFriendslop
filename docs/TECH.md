# Technical Architecture

> Companion to `DESIGN.md`. That document answers *what the game is*; this one
> answers *how it is built*. Read `DESIGN.md` first for intent.
>
> This document is written to be read by Claude Code as a working reference.
> Section 2 (Invariants) is the most important part — those rules are not
> negotiable without an explicit design conversation.

---

## 1. Stack

| Component | Choice | Notes |
| --- | --- | --- |
| Engine | Unity 6 LTS | FishNet fully supports Unity 6 LTS |
| Render pipeline | URP | Needed for the volumetric-ish fog and post-processing the darkness system relies on |
| Networking | FishNet (Fish-Networking) 4.7.x | Free, no CCU caps, actively maintained |
| Transport (ship) | FishyFacepunch | Steam P2P / SteamSockets |
| Transport (dev) | Tugboat | Plain UDP, localhost |
| Transport router | Multipass | Swap between the two without touching scenes |
| Steam API | Facepunch.Steamworks | Requires .NET 4.x |
| Editor multi-instance | ParrelSync or Unity 6 Multiplayer Play Mode | For 2–4 local test clients |
| Serialization | MemoryPack or MessagePack-CSharp | See §6.2 |

**Install FishNet via Package Manager:**
`https://github.com/FirstGearGames/FishNet.git?path=Assets/FishNet#<commit>`

**Pin it.** The URL must carry a commit or tag. Unpinned, a package re-resolve
silently takes whatever `main` is that day — for a library whose IL post-processor
rewrites your compiled code. MemoryPack and NuGetForUnity are pinned too; keep it
that way.

**FishyFacepunch and Facepunch.Steamworks are vendored** into `Assets/Plugins`
rather than installed as packages. They have no version and no update path, and
edits to them are edits to the project. Treat them as source you own.

FishNet uses an IL post-processor that injects networking logic at compile time
rather than using runtime reflection. Consequence: RPC and SyncType changes
require a full recompile to take effect, and codegen errors surface as build
errors in generated assemblies rather than in your source file. If a `[ServerRpc]`
silently does nothing, suspect stale codegen first.

**Steam AppID:** develop against `480` (Spacewar). Requires a `steam_appid.txt`
file containing `480` in the project root for the editor to initialize. Steam must
be running even in-editor.

---

## 2. Invariants

These are architectural commitments. Violating any of them breaks something
downstream that is expensive to repair.

### 2.1 Authority

**One direction only: client sends input → server mutates state → state
replicates back.** Never write gameplay logic that runs in `Update` on both host
and client and hope it stays in sync.

- The server owns: tree health and felled state, all inventories, cabin state,
  ring unlocks, enemy state, damage application, crafting results.
- The client owns: its own movement input (predicted), and all presentation.
- Anything visual — darkness, fog, audio, particles — is client-local and must
  never be networked.

### 2.2 Trees are not NetworkObjects

Trees are procedural data, not spawned network entities. See §5. A `NetworkObject`
per tree fails at a few hundred instances; the target is tens of thousands.

### 2.3 All progression is a transferable item

No XP, no character levels, no learned recipes, no bound items. Every progression
element must be an item instance that can be placed in a container. This is what
makes the shared cabin chest function as the catch-up mechanism. If a feature
request implies non-transferable progress, stop and raise it.

### 2.4 The save format is the transfer payload

A world snapshot must be complete enough to reconstruct a world from nothing — the
same bytes serve the disk save and the late-join transfer, and there is no second
"network format" that can drift from the one on disk. See §6 and §8.

*(Previously "the migration payload". Migration is gone with the server pivot, but
the requirement survives intact: late join needs exactly the same completeness.)*

### 2.5 Late join is a day-one requirement

Every stateful system must be able to serialize its full current state for a
joining client. When adding a new networked system, the late-join path is part of
the definition of done, not a follow-up task.

### 2.6 Determinism of worldgen

Given the same seed and the same biome definitions, every client must generate a
bit-identical forest. This means:

- No `UnityEngine.Random` in generation. Use an explicitly seeded PRNG passed
  through the call chain.
- No dependence on iteration order of `Dictionary` or `HashSet`.
- No dependence on frame timing, `Time.time`, or physics.
- Generation is a pure function of `(seed, chunkCoord, biomeDefs)`.

Any change to a biome definition or the generation algorithm invalidates existing
saves. Version the generation algorithm (§6.3).

---

## 3. Project Layout

```
Assets/
  _Project/
    Runtime/
      Core/            Bootstrap, service locator, app state machine
      Networking/      Lobby, roles, connection flow, broadcasts, snapshot transfer
      World/           Chunks, worldgen, tree data, regrowth, streaming
      Biomes/          Biome ScriptableObject definitions + authored assets
      Items/           Item definitions, instances, inventory, paperdoll
      Cabin/           Cabin state, storage, stations, unlocks
      Player/          Controller, prediction, interaction, mounts
      Combat/          Weapons, hitscan, damage, health
      AI/              Enemy behavior, spawning, director
      Atmosphere/      Density sampling, darkness driver, audio
      Persistence/     Save schema, serialization, snapshot assembly
      UI/
    Editor/
    Tests/
      Runtime/
      Editor/
```

**Assembly definitions:** one `asmdef` per top-level folder under `Runtime/`.
This keeps compile times sane and, more importantly, makes illegal dependencies a
compile error. Enforce these rules:

- `Atmosphere` may depend on `World` but **never** on `Networking`. Darkness is
  client-local by construction; the assembly boundary proves it.
- `World` must not depend on `AI` or `Combat`.
- `Persistence` depends on everything's data types but nothing depends on
  `Persistence` except `Core` and `Networking`.

---

## 4. Networking Topology

### 4.1 Model

**A session belongs to a server.** The server is authoritative and owns the world
save; clients hold no copy of it.

The server runs in one of two ways, from the same code and the same binary:

| Role | What it is |
| --- | --- |
| `HostedServer` | Server and client in one process. Normal play — press Play and it works. |
| `Server` | Headless and standalone, on a spare machine or a rented box. |
| `Client` | Connects to an address. Never assumes a server is present. |

Selected at runtime with `-server`, or `-connect <host[:port]>`, falling back to
the inspector default. **Not** by build target: Unity's dedicated-server subtarget
defines `UNITY_SERVER`, and FishyFacepunch compiles its entire Steam path out under
that define, which would rule out Steam invites later. One binary, a flag.

`HostedServer` is a convenience, not a third topology — Minecraft's singleplayer
server is in-process too. **Gameplay code must never branch on being the host.**
Server logic checks `IsServerInitialized`, client logic checks
`IsClientInitialized`, and neither asks whether the other lives in the same
process. The moment something asks "am I the host?", the headless build stops
matching what everyone playtests.

3–4 players is a target, not a cap; solo and two-player are normal.

> **This replaces the original "host-authoritative listen server, no dedicated
> servers, ever."** That choice carried host migration and distributed saves as
> downstream commitments — the two most expensive items in this document — to solve
> a problem a server does not have. A host leaving now ends that session; if you
> want the world to outlive any particular player, run a dedicated server. See
> `DESIGN.md` §3.2 and §4.

### 4.2 Tick rate

`TimeManager.TickRate = 30`. Sufficient for co-op PvE and halves bandwidth versus
60. Run all gameplay simulation on `TimeManager.OnTick`, never in `Update`.

### 4.3 Prediction

Use FishNet's prediction v2 for player movement only. Everything else is
server-authoritative with no prediction:

- **Movement:** predicted + reconciled
- **Chopping:** client plays the swing animation immediately for feel, server
  validates and applies tree damage. The visual is a lie until confirmed; if the
  server rejects, the tree simply doesn't take damage. No rollback needed.
- **Shooting:** see §10.
- **Crafting, inventory transfers, container access:** full round trip. These are
  not latency-sensitive and predicting them creates desync bugs for no benefit.

**Do not use physics prediction.** Nothing in this game requires networked
rigidbodies — mounts are summoned kinematic equipment (§11), not vehicles. This
avoids the single largest source of jank in co-op games.

### 4.4 Lag compensation

None. This is 4-player PvE. Server-side hit validation uses current positions with
a generous tolerance (§10.2).

### 4.5 Observers / interest management

Enemies and dropped items use FishNet's `NetworkObserver` with a distance
condition (~80m) so clients don't receive updates for entities across the map.
Players are always observed by each other.

Trees are exempt — they aren't NetworkObjects. Tree diffs are distributed by chunk
subscription (§5.5), which is a hand-rolled interest system.

---

## 5. World & Tree Architecture

This is the load-bearing technical system. Everything else is comparatively
routine.

### 5.1 Chunks

The world is a grid of chunks. **Chunk size: 64m × 64m.** Chunk coordinate is
`(int x, int z)`, packed to a single `long` for dictionary keys.

Chunk size is a tradeoff: smaller chunks mean finer-grained streaming and
regrowth but more bookkeeping. 64m at ~40 trees per chunk in dense ring keeps
per-chunk diff payloads small enough to fit in a single reliable message.

### 5.2 Tree data model

A tree is **never** a class instance in the general case. It's an index into
procedurally regenerated data.

```
TreeId = (chunkCoord, ushort localIndex)
```

Generation produces, for a given chunk, a deterministic array of:

```
struct GeneratedTree         // generated, never saved
{
    Vector3 localPosition;   // relative to chunk origin
    float    yRotation;
    float    scale;
    byte     tierIndex;      // from biome blend, but intrinsic once assigned
    byte     speciesIndex;   // visual variant within tier
}
```

*Named `GeneratedTree`, not `TreeInstance`: `UnityEngine.TreeInstance` already exists
(a Terrain type), and the collision would need an alias in every file importing both
namespaces.*

**Density ceiling.** Placement uses a jittered 8×8 grid per chunk, one tree per cell
at most, so a biome's `baseDensity` cannot exceed 64. The ~40 target above fits
comfortably. Raising the grid resolution changes generation output and requires a
`worldGenVersion` bump.

**Tier is intrinsic.** Biome blending (see `DESIGN.md` §5.3) determines the
*probability* that a given tree is tier N, but once generated the tree carries its
tier regardless of which ring it physically sits in. An early high-tier tree is a
locked teaser, not a balance leak.

### 5.3 Tree diffs

Only deviations from generated state are stored or networked.

```
struct TreeDiff
{
    ushort localIndex;
    byte   healthRemaining;  // 255 = untouched; 0 = felled
    uint   feltAtTick;       // world tick, for regrowth (§7)
}
```

Per-chunk diffs live in a `Dictionary<long, List<TreeDiff>>`. A chunk with no
diffs has no entry. Chopping produces a diff; regrowth removes one.

**Do not use a `SyncDictionary` for tree diffs.** It would replicate the entire
world's diffs to every client and grow unboundedly. Diffs are distributed by
explicit chunk-scoped broadcasts (§5.5).

### 5.4 Collider and visual streaming

Three levels of representation, keyed on distance from the nearest local player:

| Band | Representation |
| --- | --- |
| 0–~48m (active) | Real GameObjects with colliders, from a pool |
| ~48–200m (visual) | GPU instanced meshes, no colliders |
| beyond 200m | Impostors / billboards, or nothing |

Only the active band supports interaction. Chopping requires a collider, so the
server must guarantee the active band exists around every player — including
players on other clients, since the server validates their chop attempts. **The
server keeps active-band colliders loaded around all players; each client keeps
them only around itself.**

Use `Graphics.RenderMeshInstanced` (or Unity's `BatchRendererGroup` on Unity 6)
for the visual band. Do not instantiate GameObjects for it.

### 5.5 Chunk subscription

Each client subscribes to chunks within a radius. On subscribe, the server sends
that chunk's diff list as a targeted broadcast. On unsubscribe, the client drops
the diffs and lets regeneration handle it next time.

```
Client → Server:  SubscribeChunksBroadcast   { long[] chunkKeys }
Server → Client:  ChunkDiffsBroadcast        { long chunkKey, TreeDiff[] diffs }
Server → Clients: TreeDamagedBroadcast       { long chunkKey, ushort idx, byte hp }
Server → Clients: TreeFelledBroadcast        { long chunkKey, ushort idx, uint tick }
```

`TreeDamaged` / `TreeFelled` go only to clients subscribed to that chunk. Maintain
a `Dictionary<long, HashSet<NetworkConnection>>` server-side for this.

Use FishNet **broadcasts** rather than RPCs for these — broadcasts don't require a
`NetworkObject` and are the right tool for systems-level messaging.

### 5.6 Chopping flow

1. Client raycasts, hits a tree collider, reads `TreeId` off the collider's
   component
2. Client plays swing + impact VFX immediately
3. Client sends `ChopRequest { chunkKey, localIndex }`
4. Server validates: does the tree exist, is it not already felled, is the player
   within range (with tolerance), does the player's equipped axe tier meet the
   tree's tier
5. **Tier failure is a hard gate** — zero damage. Server replies with
   `ChopRejected { requiredTier }` so the client can show feedback. Client plays a
   bounce/thunk. Silent nothing reads as a bug.
6. On success, server applies damage, updates the diff, broadcasts to subscribers,
   and spawns loot if felled

Rate-limit `ChopRequest` server-side to the axe's swing cadence. Never trust
client timing.

### 5.7 Handcrafted center

The cabin clearing is an authored scene, additively loaded. The generator takes a
**mask** — radius or painted texture — marking no-spawn area, with a 20–30m ramp
band where density interpolates from zero to the biome's base value so the
authored edge blends outward rather than ending in a visible circle.

---

## 6. Persistence

### 6.1 The server owns the save

**The server is the single writer.** Clients hold no copy of the world. There is
one file, on the machine running the server, and `WorldSaveService` is the only
thing that writes it.

`saveVersion` survives as a plain monotonic counter, useful for reading logs and
telling two snapshots apart. It is no longer a conflict-resolution mechanism,
because there is no longer a conflict to resolve.

A player's **paperdoll** is still stored separately from the world (§6.3). The
reason changed: it is no longer about surviving a world copy being replaced, but
about a player being able to carry their gear between servers.

> **This replaces the distributed save model** — every client holding a full copy,
> with a highest-version-wins rule on join. That existed to stop the world dying
> with the host. A server that outlives any player solves the same problem without
> a merge rule, a version race, or four machines disagreeing about what happened.
> See `DESIGN.md` §3.2.

### 6.2 Serialization

Use **MemoryPack** (or MessagePack-CSharp) rather than `JsonUtility` or FishNet's
serializer. Rationale: world snapshots are large, get written every 30–60s, and
get transmitted over the network. Binary and allocation-light matters here. JSON
would be convenient for debugging — add a debug-only JSON export path instead of
using it as the primary format.

### 6.3 Schema

```
WorldSave
{
    uint    saveFormatVersion    // bump on any schema change
    uint    worldGenVersion      // bump when generation output changes
    uint    saveVersion          // monotonic counter, conflict resolution
    int     worldSeed
    uint    worldTick            // authoritative elapsed world time
    CabinState cabin
    Dictionary<long, ChunkSave> chunks
}

ChunkSave
{
    uint      lastVisitedTick    // drives regrowth (§7)
    TreeDiff[] diffs
}

CabinState
{
    ItemStack[]  storage
    byte[]       builtStationIds
    byte[]       unlockedRings
    // Extend here for future shared progression
}

PlayerSave                       // stored separately, per player
{
    ulong       steamId
    ItemStack[] paperdoll         // slot-indexed, see §9.2
    ItemStack[] inventory         // lost on death
    Vector3     position
    Quaternion  rotation
}
```

**Never persisted, rebuilt on load:** enemy positions and AI state, projectiles,
ground loot, summoned mounts, time of day, weather.

**Migration policy:** on load, if `saveFormatVersion` is older than current, run
sequential upgrade steps. If `worldGenVersion` differs, tree indices may no longer
correspond — either discard diffs for affected chunks or refuse to load. Decide
per change; don't silently corrupt.

### 6.4 Autosave

Host writes a snapshot every 45s and broadcasts it to all clients (§8.2). Also
snapshot on: player join, player leave, graceful shutdown.

Write to a temp file and atomically rename, so a crash mid-write can't corrupt the
save. Keep the previous snapshot as a `.bak`.

### 6.5 Large payload transmission

A full world snapshot will exceed FishNet's per-message limits once players have
explored meaningfully. Do not assume a single broadcast will carry it.

**Chunk the snapshot manually:** split into ~32KB segments with
`{ transferId, segmentIndex, segmentCount, byte[] data }`, reassemble client-side,
and validate with a hash. Send on the reliable channel. Rate-limit segments across
several ticks so the transfer doesn't stall gameplay — this is background traffic,
not urgent.

This carries late join (§8.4). It is server → client only: the server is the sole
holder of the world, so nothing needs to travel the other way. Note the segment
size is safe in that direction only — a client-to-server transfer would have to fit
under `TransportManager.MaximumClientPacketSize`.

---

## 7. Regrowth

Cleared areas regrow while unoccupied. This is both the balance answer to
clear-cutting and a horror mechanic (see `DESIGN.md` §9).

### 7.1 Computed, not simulated

Regrowth is never ticked. It's evaluated lazily when a chunk is next loaded:

```
elapsed        = worldTick - chunk.lastVisitedTick
regrowthAmount = elapsed * biome.regrowthRatePerTick

for each diff in chunk, oldest-felled first:
    if diff can be reclaimed by remaining regrowthAmount:
        remove diff        // tree returns, fully grown
        decrement remaining
```

Then set `lastVisitedTick = worldTick`. Cost is zero while nobody is there,
regardless of how long they're away.

**Occupancy definition:** a chunk is occupied if any player is subscribed to it.
`lastVisitedTick` updates continuously while subscribed, so regrowth cannot
progress in territory players are actively holding.

### 7.2 Rate scales by ring

`regrowthRatePerTick` lives in the biome definition. Outer rings reclaim faster,
so deep territory is genuinely hard to hold and part of the ring difficulty curve
lives here rather than only in enemy stats.

### 7.3 Cap regrowth per load

Clamp how much a single chunk can regrow in one evaluation. Otherwise a chunk
untouched for a month instantly returns to pristine, and a player's carefully cut
road vanishes in a way that feels arbitrary rather than eerie.

### 7.4 Tuning warning

Too aggressive and roads aren't worth building, so players just summon mounts and
weave between trunks. Too slow and the map ends up permanently stripped. **Ship
this system in the vertical slice with placeholder rates** so it can be felt
early — it cannot be tuned on paper.

**Current placeholders**, at tick rate 30 and `regrowthRatePerTick = 0.0001`:

| | |
| --- | --- |
| One tree reclaimed per | ~5.6 minutes of absence |
| Cap per evaluation | 6 trees |
| So a chunk maxes out after | ~33 minutes away |

Two things to feel for once there is real play:

- **Is a road worth cutting?** Six trees back is enough to close a narrow one, so
  half an hour away may already be too punishing.
- **Can the map recover?** The cap means a month away reclaims the same six trees
  as half an hour. That protects against a chunk snapping back to pristine (§7.3),
  but it also means heavy clear-cutting is close to permanent. If the world ends
  up stripped, the cap is the thing to raise, not the rate.

---

## 8. Connection, Late Join, and Server Lifetime

### 8.1 Connection flow

**By address (current).** The server listens on Tugboat. Clients connect to
`host:port`, from the connect menu or with `-connect <host[:port]>`. A
`HostedServer` starts its server and then connects its own client over the
loopback, exactly as a remote client would.

**By Steam invite (not yet).** The lobby layer exists (`LobbyService`, §8.1 flow
below) but cannot serve a headless server, because **FishyFacepunch only ever
calls `SteamClient` and never the game-server API**. Facepunch.Steamworks ships
`SteamServer` / `ISteamGameServer` / `SteamGameServerNetworkingSockets`; the
transport simply does not use them. Wiring that up is a scoped piece of work, not
a line change. Until then the lobby path only works host-to-host:

1. `SteamClient.Init(appId)` at boot in a `DontDestroyOnLoad` singleton
2. Host calls `SteamMatchmaking.CreateLobbyAsync(4)`; on success writes its own
   SteamID into lobby data under a known key
3. Friend joins via overlay → Steam fires `OnGameLobbyJoinRequested` → join lobby
   → `OnLobbyEntered` reads the host SteamID from lobby data → sets it as the
   transport's client address → `ClientManager.StartConnection()`
4. **Cold start:** Steam launches the executable with `+connect_lobby <id>` in the
   command line. Parse `Environment.GetCommandLineArgs()` on boot or invites
   silently fail when the game wasn't already running. A cold-start invite
   outranks the configured role — the player was asked to join someone.

**Known constraint:** FishyFacepunch cannot connect to itself locally, since it
uses Steam P2P. Multipass with Tugboat is required for local multi-instance
testing — this is why Multipass is in the stack rather than optional.

**Multipass starts every transport it holds, server-side.** Anything reacting to
"a server started" must filter on transport index, or FishyFacepunch coming up
will mask Tugboat failing to bind. This is also why FishNet's `DefaultScene`
component is not used: its guard is `IsOnlyOneServerStarted()`, which is written
for a single transport.

### 8.1a Scenes are addressed by name

`SceneLoadData` takes a scene **name**, never an asset path. A path resolves in the
editor via the AssetDatabase and fails silently in a build — the server logs
"global scenes ... could not be found", keeps running, and clients spawn into a
world with no ground and fall forever. Anything that configures a scene must
normalise to the name before use.

### 8.2 Server lifetime — migration removed

**There is no host migration.** When the server stops, the session ends. Clients
disconnect and the world is exactly as of the server's last write.

This is the whole point of the pivot. Migration was previously the answer to "the
host quit and took the world with them", and it was the most expensive thing in
this document — FishNet has no built-in migration, neither do Mirror or NGO, and
the estimate here was two weeks *if* the save layer was already correct. Making
the server able to outlive any particular player answers the same question with a
process boundary instead.

**If you want the world to persist beyond one person's session, run a dedicated
server.** That is the supported answer, and it is Minecraft's answer too.

*(Removed: the autosave-rollback strategy, Steam lobby-ownership election, the
45s snapshot broadcast to every client, and the list of state discarded on
migration. None of it has a job any more.)*

### 8.3 Connect and disconnect UX

Joining takes as long as the snapshot transfer takes. Show a deliberate, in-fiction
transition rather than a spinner — a fade, wind, a screen of trees. Players read a
freeze as a crash.

Losing the server is now an ordinary disconnect rather than a recoverable event.
Say so plainly and offer to reconnect; do not imply the world is gone, because it
is not — it is on the server, as of its last write.

### 8.4 Late join

Server → client, always. A joining client must receive, before gaining control:

1. World seed and `worldGenVersion`
2. `worldTick`
3. Cabin state (storage, stations, unlocks)
4. Its own `PlayerSave` (paperdoll travels with the player, not the world)
5. Diffs for its initially subscribed chunks
6. Currently spawned enemies and ground loot — handled automatically by FishNet
   object spawning

Use the chunked transfer from §6.5. Gate player control behind transfer
completion.

---

## 9. Items, Inventory, Paperdoll

### 9.1 Item model

Two-layer, standard pattern:

```
ItemDefinition : ScriptableObject     // static, shared, never networked
{
    ushort   id;                      // stable, never reused
    string   displayName;
    Sprite   icon;
    ItemSlot validSlot;               // which paperdoll slot, or None
    byte     tier;
    ushort   maxStack;
    // tool/weapon/mount stats as needed
}

struct ItemStack                      // instance data, networked & saved
{
    ushort itemId;
    ushort count;
    ushort durability;                // if used
}
```

**Only `itemId` crosses the network**, never the definition. Maintain a registry
resolving `ushort → ItemDefinition`, validated at boot for duplicate or missing
ids. Never serialize by name or by Unity asset reference.

### 9.2 Paperdoll

Six slots, fixed indices:

```
enum ItemSlot : byte
{
    None = 0, Axe = 1, Gun = 2, Mount = 3, Light = 4, Armor = 5, Backpack = 6
}
```

Paperdoll is `ItemStack[7]`, slot-indexed. Server-authoritative. Replicate a
player's paperdoll to all clients — others need to see equipped gear, and the
server needs it for tier checks.

**Two slots are load-bearing systems, not stat sticks:**

- **Light** is the only counter to the darkness system, so upgrading it trades
  atmosphere for safety. Consider making brighter light increase enemy aggro
  radius as a counterweight.
- **Backpack** governs expedition length and therefore is the strongest pacing
  lever in the game. It also self-balances against death, since inventory is lost.

### 9.3 Inventory and death

Carried inventory is lost on death. Paperdoll is never lost. Progress lives in the
paperdoll.

Exact recovery mechanics (corpse run vs. straight loss) are deferred until travel
times are real. **Design the death handler with a hook for dropping a container**
so either resolution is a small change rather than a refactor.

### 9.4 Containers

Cabin storage is a server-owned container in `CabinState`, not a `NetworkObject`
inventory. All transfers are `ServerRpc` round trips with server-side validation
of both source and destination. Assume concurrent access — two players will grab
the same stack simultaneously on day one. Validate against current server state,
not against what the client thinks it saw.

---

## 10. Combat

### 10.1 Weapons

Primarily gunplay. Hitscan only — no networked projectiles in the vertical slice.

### 10.2 Hitscan flow

1. Client raycasts locally, plays muzzle flash, tracer, and impact VFX immediately
2. Client sends `FireRequest { origin, direction, tick }`
3. Server validates: fire rate, ammo, that `origin` is plausibly near the player's
   server position (generous tolerance, ~2m), re-raycasts server-side
4. Server applies damage and broadcasts the confirmed hit

Client visuals are optimistic. A rejected shot simply deals no damage. Do not
attempt rollback — at 4 players it will not be noticed.

**Fire from the body, aim with the camera.** The origin check compares against
where the server has the *player*, and a third-person camera sits metres behind
them — firing from the camera fails plausibility on every shot. Trace from the
camera to find what the crosshair is on, then aim the body at that point. The same
applies to the chop reach in §5.6, which measures range from the player.

### 10.3 Enemy AI

**AI runs server-only.** Enemies are `NetworkObject`s with `NetworkTransform`,
spawned and despawned by the server. Clients receive transforms and play
animations; they never run behavior logic.

**State is the animation contract.** `EnemyState` (Idle, Patrol, Chase, Attack,
Stagger, Dead) and planar speed are both replicated, and `EnemyAnimationBridge`
turns them into Animator parameters. Speed is replicated rather than derived from
transform deltas, which would be noisy at any real ping and make a locomotion
blend jitter. The enum values are wire format — append, never renumber.

A rig drops in by replacing the `Model` child and pointing the bridge at its
Animator; no behaviour code is involved.

Cull aggressively: despawn enemies with no player within ~120m. Use
`NetworkObserver` distance conditions so transforms aren't sent to distant
clients.

### 10.4 Spawn director

Spawn pressure is a function of **local tree density** and a **per-ring floor**.
The floor is essential — without it, a clear-cut ring becomes safe and the horror
evaporates permanently in a persistent world.

Spawn tables live in biome definitions and **blend across ring boundaries on the
same weighted interpolation as trees**, so threat type doesn't snap at an
invisible line.

Server-side, cap concurrent enemies globally (start at ~24) regardless of what the
density math wants. This is both a performance and a fairness bound.

---

## 11. Mounts

Mounts are **summoned paperdoll equipment**, not world vehicles. This is a
deliberate technical simplification as much as a design one: it eliminates
networked rigidbodies with passengers, which is the jankiest thing in co-op games,
and keeps vehicles out of the save format entirely.

- Occupies the `Mount` slot
- Summoning has a **cast time**, interruptible by damage
- Does not persist in the world without a player
- Never saved

**Implementation:** mounting swaps the player's movement parameters and collision
capsule. It does not spawn a separate networked vehicle object. The mount mesh is
cosmetic, parented to the player.

**Wider capsule while mounted** so dense forest is impassable — this is what makes
chopping double as road building. Critical details:

- Widen the capsule but **not** its height, to avoid canopy clipping
- Be generous with the corridor width players actually need; a wedged ATV reads as
  broken, not charming
- **Auto-dismount on hard collision** rather than letting players grind against
  trunks

Cast time is the primary horror lever — it prevents mounts from being a panic
button. Expose it as tuning data.

---

## 12. Atmosphere & Darkness

**Entirely client-local. Never networked.** The `Atmosphere` assembly must not
reference `Networking` (§3).

### 12.1 Density grid

Baked at chunk generation: one float per ~4m cell, stored as a low-resolution
array per chunk. This is a byproduct of generation, not a separate pass.

### 12.2 Sampling

Each frame, sample the grid bilinearly at the local player's position and drive:

- URP fog density and color
- Directional light intensity and ambient
- Post-process exposure and vignette
- Ambient audio mix

Smooth the sampled value over ~0.5s so walking past a single tree doesn't flicker
the lighting.

**No raycasts. No collider queries. No per-tree lookups.** Cost is one array read
per frame.

### 12.3 Falloff curve

Density-to-darkness mapping lives in the biome definition as a curve so it can be
authored per ring. Must be tuned visually — it cannot be specified on paper.

---

## 13. Biome Definitions

`ScriptableObject`, one per ring, authored in `Assets/_Project/Runtime/Biomes/`.
Ring count is unbounded — stacking a new definition adds a ring with no code
changes.

```
BiomeDefinition : ScriptableObject
{
    int      ringIndex;
    float    innerRadius;
    float    blendBandWidth;          // how far the previous ring bleeds in

    TreeSpawnEntry[] trees;           // prefab/mesh, tier, weight
    float    baseDensity;
    float    regrowthRatePerTick;

    AnimationCurve densityToDarkness;
    EnemySpawnEntry[] enemies;        // prefab, weight, level
    float    spawnRateFloor;          // survives clear-cutting

    LootTable groundLoot;
}
```

**Gameplay ring index is discrete:** `floor(distance / ringWidth)`. Unlock flags,
spawn floors, and difficulty use the hard index. Only *appearance and spawn
weights* blend. Blended visuals, discrete logic — much easier to reason about, and
prevents boundary-straddling exploits.

---

## 14. Performance Budgets

Targets for a mid-range machine at 1080p, since this ships to friends' PCs and one
of them will have a GTX 1060.

| Budget | Target |
| --- | --- |
| Frame time | 16ms (60fps) in dense forest |
| Active-band tree colliders | < 400 concurrent |
| Instanced tree draws | < 40 draw calls via batching |
| Concurrent enemies | < 24 server-side |
| Bandwidth per client | < 32 KB/s steady state |
| Snapshot transfer | Background, must not drop frames |
| Chunk generation | < 4ms, off main thread where possible |

Generate chunks on a background thread using Burst-compatible code where
practical. Chunk generation must never block the main thread — a hitch every time
a player crosses a chunk boundary is the most likely performance complaint.

---

## 15. Testing

- **Local multi-instance:** ParrelSync or Unity 6 Multiplayer Play Mode, with
  Multipass set to Tugboat. Four instances is the target test configuration.
- **Latency simulation:** FishNet's built-in latency simulator, at 100ms RTT with
  2% loss. Anything that feels fine only at 0ms is untested.
- **Determinism test (automated):** generate the same chunk twice with the same
  seed, assert byte-identical output. Run in CI. This catches the single most
  insidious class of bug in this architecture.
- **Save round-trip test (automated):** serialize → deserialize → compare. Run on
  every schema change.
- **Late-join test:** manual, but run it every session. Chop a road, join a fourth
  player, confirm the road is there.
- **Headless test:** build, run `ChopChop.exe -server -batchmode -nographics`, and
  connect the editor to `127.0.0.1`. **Run this after any change to boot, scene
  loading, or role handling.** Everything else can pass with a hosted server in
  disguise; only this catches editor-only assumptions, and it has already caught
  two (scene-by-path, and `DefaultScene` never subscribing in a player).
- **Disconnect test:** kill the server process rather than quitting it, and confirm
  clients drop cleanly instead of hanging.

---

## 16. Milestone 1 — Build Order

The purpose of the vertical slice is to answer "is chopping trees with friends
fun?" before any content scale exists.

1. **Bootstrap + connection.** Players in a grey-box clearing. Multipass with both
   transports. ✅
2. **Networked movement.** Predicted, verified at 100ms. ✅
3. **Save schema + serialization + chunked transfer.** Foundation for everything.
   Build this before content. ✅
3a. **Server pivot.** Roles, server-owned save, headless build, connect by address.
   Done before step 4 because step 5 bakes the topology in permanently. ✅
4. **Chunk system + deterministic generation.** One biome, one tree tier.
   Determinism test in CI. ✅ *(test written; CI itself deferred)*
5. **One choppable tree** with diffs replicating, plus the late-join path. ✅
6. **Density grid + darkness.** Placeholder curve. Cheap, and it's the mood. ✅
7. **Regrowth.** Placeholder rates, so it can be felt early. ✅
8. **One gun, hitscan, server-validated.** ✅
9. **One enemy** that chases and can be killed. ✅
10. **Paperdoll + cabin storage.** Minimal, but proves the transferable-item rule.

Deliberately **not** in Milestone 1: Steam game-server transport (invites to a
dedicated server), mounts, multiple rings, crafting tree, the Xarol.

Host migration is not deferred — it is gone. See §8.2.

If the loop isn't fun in grey-box, more rings will not fix it.

---

## 17. Notes for Claude Code

- Read §2 (Invariants) before making architectural changes. If a task appears to
  require violating one, stop and raise it rather than working around it.
- **Never branch gameplay on being the host.** Check `IsServerInitialized` or
  `IsClientInitialized`; never ask whether the two happen to share a process. This
  is the one rule that keeps the headless build honest (§4.1).
- **The editor is not the target.** Scene references resolve by name in a build,
  not by asset path; component initialisation order differs; `Application`
  conveniences may not exist. After touching boot, scene loading, or roles, run the
  headless test in §15.
- Gameplay simulation goes in `TimeManager.OnTick`, not `Update`.
- Any new networked state needs a late-join serialization path in the same PR.
- Any new progression element must be an `ItemDefinition`. If it can't be put in a
  box, it's wrong.
- Do not add `NetworkObject` to trees, ever.
- Do not network anything in the `Atmosphere` assembly.
- Prefer FishNet **broadcasts** for systems-level messaging and **RPCs** for
  entity-scoped messaging.
- When adding a `SyncVar` or RPC, remember codegen runs at compile time — a full
  recompile is required, and silent no-ops usually mean stale generated assemblies.
- FishNet v4 uses `IsServerInitialized` / `IsClientInitialized`, not the v3
  `IsServer` / `IsClient`. Much online sample code is v3; check before copying.
- Verify FishNet API signatures against the current docs at
  `https://fish-networking.gitbook.io/docs` rather than trusting samples or
  memory — the v3 → v4 API break is significant and widely mis-documented in
  tutorials.
