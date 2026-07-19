# Known Player related bugs

This document outlines **open** bugs related to the player controller and interaction systems. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** March 2026

---

## 01. Collision Issues in Tight Spaces

**Severity:** Bug
**Files:** Player Controller

Player collision can get stuck / flaky in tight spaces (eg: single block wide tunnels or when flying trough caves).

---

## 02. Movement Speed Reset on Fly Mode Toggle

**Severity:** Bug
**Files:** Player Controller

When increasing the player movement speed, the horizontal speed is still increased when the player is falling after turning fly mode off. The movement should be "reset" back to the standard player movement speed. The actual movement speed override itself should be saved in the game-state however for when the player turns fly mode back on.

---

## 03. Far-Lands Voxel Modification Broken — Placement/Breaking Fail Near the ±2³¹ Edge (Possibly Earlier)

**Severity:** Low (far-lands only; normal-play range unaffected)
**Status:** Open — logged 2026-07-19 during the Bug 19 far-coordinate verification session (symptoms observed
BEFORE commit `ed8cb69` landed; NOT yet re-tested against it — see Notes).
**Files:** `PlacementController.cs`, `PlayerInteraction.cs`, `World.cs` (`ApplyModifications`), `Commands/` (`/setblock`)

**Description (observed near-ish the ±2³¹ voxel limit, possibly starting earlier):**

1. **Placement highlight missing:** with F2 highlight toggles on, only the *breakage* highlight renders — the
   placement highlight never appears.
2. **Breaking does not work** despite its highlight rendering.
3. **Placement consumes inventory but places nothing:** the hotbar block count decrements, but the voxel
   data/mesh never change — consistent with the `VoxelMod` being enqueued, then dropped or misrouted in
   `World.ApplyModifications` (the hotbar decrement happens at interaction time, the actual write later).
4. **`/setblock` at these scales placed a block, but at a DRIFTED location** (not the specified cell) —
   suspected float precision on a coordinate path the command feeds.

**Root Cause Suspected:**
The same two classes as lighting Bug 19 (`_FIXED_BUGS.md` #24): (a) an int→float round-trip or implicit
`Vector3Int`→`Vector3` conversion losing integer precision past ±2²⁴ on a placement/mod-routing path, and/or
(b) genuine `int` arithmetic wraparound at the ±2³¹ edge (documented-only class, `WORLD_SCALING_FLOATING_ORIGIN.md`
§9). **Several candidate seams on exactly these paths were already fixed by `ed8cb69`** (`ApplyModifications`'
chunk routing at `World.cs:2421` via `ChunkCoord.FromVoxelPosition(Vector3Int)`, the mod local-position
derivations at `World.cs:922/:2500` via `WorldData.GetLocalVoxelPositionInChunk(Vector3Int)`), so the observed
symptoms may be partially or fully resolved already. The `/setblock` drift suggests at least one float seam on
the command/coordinate path survives (e.g. a `Vector3`-typed carrier between parse and mod creation). The
dev-build ±2²⁴ tripwire (`WorldData.AssertWithinFloatPrecision`, latched, logs once) now flags any float-space
chunk query at far magnitude — reproducing in the editor should name a surviving offender directly.

**Reproduction Steps:**

1. `/teleport` far out (near ±2³¹; also test ±2×10⁷ and just past ±2²⁴ = 16,777,216 to bracket the onset).
2. Toggle highlights (F2); aim at terrain: observe missing placement highlight.
3. Try breaking (fails) and placing (hotbar decrements, world unchanged).
4. `/setblock` a specific cell; observe the block appearing at a drifted position.

**Notes:**

- **Re-test after `ed8cb69` FIRST** — the Bug 19 fix landed integer overloads on the exact mod-routing seams;
  the symptom set must be re-established against current code before any analysis.
- Related deliberately-open items: the two `ToVector3Int` round-vs-floor sites and the spawn-height probe's
  absolute `Vector3` (`WORLD_SCALING_FLOATING_ORIGIN.md` §9); the ±2³¹-edge overflow class is documented-only
  by decision — if part of the symptom set only reproduces at the actual edge, it belongs to that class and is
  a non-issue, not a fix target.

**Testing environment:** Editor (Mono), Bug 19 verification session, 2026-07-19.

---
