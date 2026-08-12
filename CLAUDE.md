# Project Context

Unity 6 LTS co-op survival game. FishNet networking, Steam P2P.

**Read `docs/TECH.md` before making architectural changes.**
Section 2 (Invariants) is non-negotiable — if a task appears to
require violating one, stop and raise it.

`docs/DESIGN.md` covers intent and game design decisions.

## Quick rules
- Trees are never NetworkObjects
- Gameplay simulation runs in TimeManager.OnTick, not Update
- All progression must be a transferable ItemDefinition
- Nothing in the Atmosphere assembly may be networked
- FishNet v4 API — most online samples are v3, verify before copying