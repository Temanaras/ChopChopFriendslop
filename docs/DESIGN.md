# Untitled Forest Game — Design Document

> Working design doc. Sections marked **DECIDED** are settled. Sections marked
> **DEFERRED** are intentionally unanswered until a playable prototype exists.

---

## 1. Concept

A networked co-op survival game for 3–4 players. Players start at a cabin in a
small grassy clearing surrounded by forest. Moving outward from the center, trees
become progressively harder and require tool upgrades to fell. Density of
surrounding trees drives darkness and monster spawns, so the outer world is both
harder to harvest and more hostile. The goal is to reach the outermost ring.

**Tone:** folk horror. Eerie rather than gruesome — fear comes from isolation,
darkness, and intent, not gore. Never comedic, though the horror trappings may be
dialed back.

**Engine:** Unity
**Networking:** FishNet, server-authoritative. The server runs in-process for
normal play or standalone and headless on a box (§4)
**Transport:** FishyFacepunch (Facepunch.Steamworks) over Steam P2P, with
Multipass + Tugboat retained for local testing
**Distribution:** Steam

---

## 2. Core Loop

1. Leave the cabin, travel outward
2. Fell trees for materials — which also clears darkness and reduces spawn pressure
3. Return to the cabin, deposit materials, craft upgrades
4. Upgraded tools unlock the next ring's tree tier
5. Repeat outward

The loop is self-tensioning: harvesting trees is how you progress *and* how you
make the world safer, but the forest regrows while you're away.

---

## 3. Session & Persistence — **DECIDED**

### 3.1 Persistent world

The world persists between sessions. Drop-in / drop-out — players join and leave
freely.

### 3.2 The server owns the save — **revised**

> **This previously specified distributed saves**: every player holding a full
> copy, with a highest-version-wins rule on join. It was marked DECIDED and has
> been reversed alongside §4.

**One world file, on the server.** Nobody else writes it, so there is nothing to
merge and no version race.

The world is not lost when a friend stops playing — it lives on whatever machine
is running the server, and a group that wants it available regardless of who is
online runs a dedicated one.

**Split of ownership:**
- The **world** belongs to the server
- A player's **paperdoll** still travels with them, so gear can move between
  servers

### 3.3 Save schema

**Persisted — world**
| Data | Notes |
| --- | --- |
| World seed | Deterministic forest generation |
| Chopped tree diffs | `chunkID`, `treeIndex`, felled/damaged, timestamp |
| Chunk last-visited timestamps | Drives regrowth |
| Cabin state | Storage contents, built stations, upgrades, unlock flags |
| Ring unlock flags | May live inside cabin state |
| Save version counter | Conflict resolution |

**Persisted — per player**
| Data | Notes |
| --- | --- |
| Paperdoll | Equipped items + tiers |
| Position | Saved on logout; no checkpoints |
| Current ring | Derived, but cached |
| Carried inventory | Lost on death |

**Never persisted — rebuilt on load**
- Enemy positions and AI state
- Projectiles, ground loot, active hazards
- Summoned mounts
- Time of day / weather (if implemented)

### 3.4 Cabin as progression container

Cabin state is the canonical home for shared progression: group storage, crafting
stations, structural upgrades, and ring unlock flags. This makes group progress
physically visible and separates the two progression axes cleanly:

- **Cabin** — shared, world-owned
- **Paperdoll** — personal, portable

---

## 4. Servers — **DECIDED (revised)**

> **This section previously specified host migration**, built on top of the save
> system, with Steam lobby-ownership election and autosave rollback. It was marked
> DECIDED. It has been reversed — see "Why this changed" below.

### 4.1 A session belongs to a server

The server is authoritative and owns the world save. It runs either in the
player's own process (press Play, it just works — like Minecraft singleplayer) or
standalone and headless on a spare machine.

### 4.2 There is no host migration

When the server stops, the session ends. Players disconnect; the world is intact
on the server as of its last write, and is there when it comes back up.

**If you want the world to be available when no particular person is around, run a
dedicated server.** That is the supported answer.

### 4.3 Why this changed

Migration existed to answer one question: *the host quit, is the world gone?* It
was the most expensive item in either document — no engine supports it natively,
and the estimate was two weeks of work that would be impossible if the save layer
were even slightly wrong. It also forced distributed saves (§3.2) to supply its
payload, which brought a version-conflict rule along with it.

A server that can outlive any particular player answers the same question with a
process boundary. Two premises had also shifted: 3–4 players is a target rather
than a cap, and connecting by address is acceptable.

The cost is real and worth stating: **Steam invites do not currently reach a
dedicated server**, because the transport we use only speaks to the Steam client,
not the Steam game-server API. Address-based connection works today; invites are
scoped work for later.

---

## 5. World Structure — **DECIDED**

### 5.1 Hybrid generation

- The cabin clearing is a **handcrafted scene**
- Everything beyond is **procedural from seed**
- The spawner takes a mask (radius or painted texture) marking the no-spawn
  handcrafted area, with a 20–30m ramp band so the authored edge blends into
  generated forest rather than ending at a hard circle
- Host rolls the seed at world creation and transmits it on connect; all clients
  generate an identical forest

### 5.2 Dynamic rings

Ring count is **data-driven and unbounded**. Each ring is a biome definition, so
new rings can be stacked by authoring new definitions without code changes.

**A biome definition contains:**
- Tree tier(s) and spawn weights
- Base density
- Blend band width
- Darkness curve parameters
- Enemy spawn table
- Regrowth rate multiplier

### 5.3 Blended boundaries

Rings blend visually. Approaching a boundary, trees from the next ring appear
interspersed and grow more common until they dominate.

**Rules:**
- Blending controls **appearance and spawn weight only**
- A tree always carries its own tier regardless of where it lands, so an early
  high-tier tree is a **locked teaser**, not a balance leak — it advertises what's
  next
- Enemy spawn tables blend on the same weighted interpolation, so threat type
  doesn't snap at an invisible line
- **Gameplay ring index stays discrete:** `floor(distance / ringWidth)`. Unlock
  flags, spawn floors, and darkness use the hard index. Blended visuals, discrete
  logic.

### 5.4 Tree scaling (technical)

Trees are **not** individual `NetworkObject`s — that fails at a few hundred.

- Forest generated deterministically from seed; every client builds the same
  forest locally
- Only **diffs** are networked: `{chunkID, treeIndex, health}` in a small synced
  collection
- Real GameObjects with colliders spawn only in chunks near a player
- Everything else is GPU-instanced visuals or impostors
- Ring tier is a function of distance, computed at generation time

---

## 6. Darkness — **DECIDED**

Local tree density drives darkness and fear.

**Implementation:** bake a density value into a low-resolution grid at world
generation (one float per ~4m cell). Sample at player position each frame and
drive fog density, light range, and post-processing.

- **Client-side only.** No networking required.
- Effectively free at runtime.
- Density naturally rises outward, so the horror curve emerges from the world
  rather than being scripted.

No raycasts, no collider queries, no per-tree lookups.

---

## 7. Traversal — **DECIDED**

### 7.1 Mounts as summoned equipment

Mounts (ATVs) are **WoW-style**, not world vehicles:

- Occupy a paperdoll slot
- Summoned with a **cast time**
- Do **not** persist in the world without a player

This avoids networked rigidbody-with-passengers physics entirely, and keeps
vehicles out of the save format — no orphaned ATV parked in ring 4.

**The cast time is the primary horror lever.** It is interruptible by damage, so a
mount can never be a panic button. Mounts are for deliberate travel through
cleared territory, not escape.

### 7.2 Roads via collision size

Mounted players use a **wider pathing hitbox** so dense forest is impassable.
Chopping therefore doubles as **road building** — the axe is how the map becomes
traversable, and cleared corridors persist.

**Implementation notes:**
- Widen the mounted capsule but **not** its height, to avoid canopy clipping
- Be generous with required corridor width; a wedged ATV reads as broken, not
  charming
- **Auto-dismount on hard collision** rather than allowing players to grind
  against trunks

**Resulting danger gradient:** on the road you are fast and relatively safe. Off
the road you are on foot in the dark. This is where the fear lives.

### 7.3 No checkpoints

Player position is saved on logout. Traversal upgrades replace the need for
multiple home bases or per-ring checkpoints.

---

## 8. Progression — **DECIDED**

### 8.1 Paperdoll slots

| Slot | Role |
| --- | --- |
| Axe | Ring gating, road building |
| Gun | Primary combat |
| Mount | Traversal |
| Light source | Counters darkness |
| Armor | Survivability |
| Backpack | Carry capacity |

**Notes on two load-bearing slots:**

- **Light source is the horror dial.** It's the only slot that directly counters
  the darkness system, so upgrading it trades atmosphere for safety. Consider
  making light attract attention as a counterweight.
- **Backpack is the trip-length governor.** Capacity determines expedition length
  before hauling back, making it the strongest pacing lever available. It also
  self-balances against death: bigger pack, bigger loss.

**Known gap:** armor is currently the only defensive axis, so defensive upgrades
may feel flat. Watch, don't solve yet.

### 8.2 Hard constraint: everything is a transferable item

**No XP. No character levels. No learned recipes. No bound items.**

Every piece of progression must be a plain object that can sit in a box. This is
what makes shared storage function as the catch-up mechanism — a veteran can drop
a tier-2 lantern in the cabin chest for an infrequent friend. The moment any
progression becomes non-transferable, catch-up breaks and lapsed players are
permanently stranded.

### 8.3 Catch-up via group storage

Divergence is the expected failure mode of per-player progression in a drop-in
game: the friend who plays twice a month can't harvest in the group's current
ring and contributes nothing. **Group storage in the cabin is the answer** —
players leave surplus materials and outgrown tools for others.

### 8.4 Tool tier gating: hard walls

A tool below the required tier does **zero** damage. The game states that a
better axe is needed.

**Rationale:** soft gates force a decision — is the grind worth it? — that is
boring, has no interesting answer, and breeds resentment when players grind
anyway. A hard wall asks nothing.

**Requirements:**
- **Physical failure feedback** — thunk, bounce, bark chips, recoil. Silent
  nothing reads as a bug.
- **Visually legible tiers** — bark color, trunk size, silhouette — so players
  learn to read the forest instead of swinging at walls.
- **Legible acquisition** — the failure must communicate what would unlock it.
  With no grind-through escape valve, an unclear recipe leaves players fully
  stuck.

---

## 9. Regrowth — **DECIDED**

**Cleared areas regrow while no players are nearby.** Return to old territory and
find it overtaken again, disorienting and unfamiliar.

This solves the clear-cutting problem — a persistent world would otherwise be
permanently stripped bare and stop being scary — but more importantly it *is* the
Xarol's thesis expressed as a system. The forest undoing your work is the
antagonist's argument, not a balance patch. It also makes roads **maintained**
rather than **built**, giving the group reason to return to territory instead of
only pushing outward.

**Implementation:**
- Per-chunk last-visited timestamp
- Regrowth computed **on load**, not simulated — costs nothing while unoccupied
- Rate scales by ring: outer rings reclaim faster, so deep territory is genuinely
  hard to hold and ring difficulty partly lives here

**Required counterweight: a compass.** This is load-bearing, not a convenience —
it's the difference between eerie and rage-quitting. Points to the cabin only. No
map. Diegetic if possible.

**Tuning risk:** too aggressive and roads aren't worth building, so players just
summon mounts and weave. Too slow and the map ends up stripped. Ship the regrowth
*system* early with placeholder numbers so it can be felt.

---

## 10. Combat & Threats — partially decided

**DECIDED:** Primarily gunplay over melee. Threat escalates outward — wolves and
bears in the inner rings, increasingly twisted things further out.

**DECIDED (concept):** The **Xarol** — "Lorax" backwards — a folk-horror
antagonist punishing lumberjacks for deforestation. Thematically the resource
you're harvesting is also the cover you're hiding in, which inverts the core loop
into a moral one.

**Netcode:** hitscan. Client fires immediately for feel; server validates and
applies damage. No lag compensation — it's PvE co-op with 4 players.

**Spawn pressure** is keyed to local density *and* a per-ring floor, so outer
rings stay lethal regardless of how bare they're stripped.

**Production note:** the chosen tone lets us skip expensive horror production —
gore VFX, elaborate death animations, startle-grade sound design — and lean on the
cheap techniques that do most of the work: darkness falloff, distance fog, sparse
audio, and silhouettes never fully seen. The existing density system already
delivers most of this.

---

## 11. Deferred Until Prototype — **DEFERRED**

Intentionally unanswered. These are better decided with hands on a playable build.

| Item | Why deferred |
| --- | --- |
| Crafting tree specifics | Pure tuning data — recipes, costs, upgrade paths |
| Weapon roster & ammo economy | Scarcity is a feel question, unanswerable on paper |
| Death resolution | Corpse run vs. straight loss depends on actual travel times |
| Enemy roster & behavior | Beyond the broad outward escalation |
| Xarol design | Singular stalker vs. ambient threat — decide once the woods exist |
| Mount cast time | Tuning number |
| Mounted hitbox & corridor width | Tuning numbers |
| Darkness falloff curve | Must be seen to be judged |
| Storage rules | Permissions, capacity, one chest or many |

**Standing decision:** death costs carried inventory but never the paperdoll.
Progress is held in the paperdoll. Exact recovery mechanics are deferred.

### The trap in this list

Several deferred items — **death penalty, ammo scarcity, Xarol design** — are the
ones that determine whether the game is *fun*, not merely whether it works.
Deferring them is correct, but the prototype must not become so invested in
systems that these can't be answered boldly when the time comes.

---

## 12. Known Risks

| Risk | Mitigation |
| --- | --- |
| Nobody wants to be the one who hosts | Dedicated server is a supported, documented mode (§4) |
| Steam invites do not reach a dedicated server | Address-based connection ships first; game-server transport is scoped work |
| Save divergence between separate sessions | Server is the single writer; there is nothing to diverge |
| Progression divergence between friends | Everything transferable + group storage |
| Clear-cutting destroys the horror | Regrowth + per-ring spawn floors |
| Regrowth mistuned in either direction | Ship the system early with placeholder numbers |
| Building all rings before anything is fun | Vertical slice first (§13) |
| Late-join treated as a later feature | Build it on day one — deferring it rots into a rewrite |

---

## 13. Milestone 1 — Vertical Slice

In order. The purpose is to answer "is chopping trees with friends fun?" before
any content scale exists.

1. FishNet + Steam lobby; 4 players spawning in a grey-box clearing
2. Networked movement that feels correct at 100ms ping
3. **World state / save format** — the foundation everything hangs off
4. One tree that can be chopped, with the diff replicating to **late joiners**
5. One gun, hitscan, server-validated
6. One enemy that chases and can be killed
7. Density-driven darkness with placeholder curve
8. Regrowth system with placeholder rates

If the loop isn't fun in grey-box, more rings will not fix it.

**Late-join is a day-one requirement.** Player 4 connecting mid-session must
receive tree diffs, cabin state, and current inventories. This is the most common
reason projects of this shape stall around month three.
