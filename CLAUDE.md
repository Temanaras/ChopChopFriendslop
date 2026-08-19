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

## Working out loud

**Every command shown or submitted for approval must say what it is
trying to find out or change, not what it mechanically is.** A
permission prompt is a decision point, and "running code in the editor"
gives nothing to decide on.

State the goal, and the target when it is not obvious:

- Bad: "Executing code" / "Running tests" / "Checking status"
- Good: "Counting rendered tree instances before and after felling one,
  to prove the renderer honours the diff store"
- Good: "Deleting Assets/AfricanTrees — 30MB, already confirmed
  unreferenced by any GUID outside the pack"

This matters most for the destructive and the opaque: anything that
deletes, overwrites, force-pushes, or edits vendored code under
`Assets/Plugins`, and every `execute_code` call, which is otherwise a
wall of C# with no stated purpose. If a command is exploratory, say
what question it answers. If it changes something, say what and where.