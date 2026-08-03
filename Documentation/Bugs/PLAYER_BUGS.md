# Known Player related bugs

This document outlines **open** bugs related to the player controller and interaction systems. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** August 2026
>
> **Numbering note:** `§03` is **retired, not free.** It belonged to the world-gen-tags-leak-into-placement
> bug (fixed June 2026, archived) and is still cited by name from `PlacementValidationSuite*.cs`,
> `PlacementTagMigration.cs`, `WORLD_SCALING_FLOATING_ORIGIN.md` and `FLUID_BUGS.md`. Reusing the number
> would silently redirect all of those. New entries continue from `§04`.
>
> **Validation suite:** none yet — the physics/collision solver is the largest system in the engine with no
> automated guard. Tracked as **`NS-4`** in
> [`../Design/VALIDATION_SUITE_COVERAGE_ROADMAP.md`](../Design/VALIDATION_SUITE_COVERAGE_ROADMAP.md); §04
> below is a motivating repro candidate for it.

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

## 04. Player embeds in a block after landing and can no longer jump

**Severity:** High — the player is stranded; escape requires a debug capability (flight/noclip)
**Status:** Open
**Files:** `Assets/Scripts/Physics/VoxelRigidbody.cs` (`ResolveMovement`), `World.CheckPhysicsCollision`

**Description (user-observed in game, 2026-08-03):**
After landing from a fall the player can end up **stuck inside a block**. While stuck, **jump input is
refused** — not merely ineffective. The only known escape is to enable flight/noclip, fly up, and land
softly. Reported as intermittent ("sometimes"), and **correlated with higher fall speed**.

**Reproduction Steps (not yet reduced to a deterministic case):**

1. Fall from a height — the higher the fall speed, the more often it triggers.
2. Land on solid ground.
3. Occasionally the player settles embedded in the block rather than on top of it.
4. Press jump — nothing happens, repeatedly.
5. Enable flight/noclip, ascend, disable flight, land gently → normal behavior returns.

**Narrowing (static analysis only — NOT yet instrumented, treat as a lead, not a diagnosis):**

- `VoxelRigidbody.RequestJump()` is the **only** jump entry point and gates solely on
  `IsGrounded && !isFlying`. "Jump refused" therefore means **`IsGrounded == false`** while the player is
  visually at rest. This is a solver *state* problem, not an input-layer one.
- `IsGrounded` is written in only four places, all in the vertical resolve at the end of `ResolveMovement`
  (`IsGrounded = groundedByStep`, the `ySign < 0` hit, and the zero-vertical-movement branch) plus the
  `FixedUpdate` reset. That is a small, tractable surface to instrument.
- The zero-vertical-movement branch grounds on `groundContact.Hit && groundContact.Correction > -0.01f` —
  a bare threshold worth logging alongside the actual correction when the bug fires.
- Fall speed as the aggravator points at the substepping / tunneling guard
  (`SUB_VOXEL_COLLISION_SYSTEM.md` §3.4.4, `MIN_COLLISION_THICKNESS`-derived max step): a displacement
  large enough to need substeps is exactly the high-speed case.
- The workaround restoring normal behavior (rather than a reload being required) suggests **transient
  solver state**, not corrupted world data.

**Relationship to §01:** both are "collision gets stuck", and they may share a root cause — but the
triggers differ (§01 is tight spaces / flying through caves; §04 is a high-speed landing on open ground).
Do not assume they are one bug until instrumented.

**Route:** `voxel-debugging` (instrument first, fix second). Once a deterministic repro exists, it is a
prime candidate to be the first scenario of the **`NS-4`** physics suite, per
`validation-driven-bugfix` — prove-red before trusting any fix.

---
