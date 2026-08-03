# Known Player related bugs

This document outlines **open** bugs related to the player controller and interaction systems. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** August 2026
>
> **Numbering note:** `§03` is **retired, not free.** It belonged to the world-gen-tags-leak-into-placement
> bug (fixed June 2026, archived) and is still cited by name from `PlacementValidationSuite*.cs`,
> `PlacementTagMigration.cs`, `WORLD_SCALING_FLOATING_ORIGIN.md` and `FLUID_BUGS.md`. Reusing the number
> would silently redirect all of those. New entries continue from `§04`.
>
> **Validation suite:** `Minecraft Clone/Dev/Validate Physics Solver`
> (`Assets/Editor/Validation/PhysicsSolver/`) — the **`NS-4`** suite shipped 2026-08-03 with 17 baselines over the
> real `VoxelRigidbody` + `World.CheckPhysicsCollision`, closing the "largest system with no automated guard" gap
> (see [`../Design/VALIDATION_SUITE_COVERAGE_ROADMAP.md`](../Design/VALIDATION_SUITE_COVERAGE_ROADMAP.md)). It is
> the vehicle for §04's repro: the suite deliberately does **not** pin `IsGrounded` after a high-speed landing or a
> horizontal-only resolve, because that verdict is what §04 is about.

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

**Observed while building the `NS-4` suite (2026-08-03) — harness observation, NOT an in-game diagnosis:**

- Several baselines had to leave `IsGrounded` unasserted because the solver reports **`false` immediately after a
  resolve that leaves the body flush on a surface**. Two shapes were seen in the harness: a horizontal-only resolve
  (`movement.y == 0`, which routes to the zero-vertical-movement branch), and the *tail* substeps of a high-speed
  landing, where the first substep lands and the remainder run with `movement.y` at or near zero.
- This is consistent with §04's fall-speed correlation and with the `Narrowing` notes above, and it sharpens the
  question for instrumentation: *what does `IsGrounded` end a tick as, and which of the four write sites decided
  it?* It is **not** confirmation — nothing has been instrumented in the running game, and the harness cannot
  observe the frame-to-frame momentum sequence a real fall produces.
- **Second observation, found by accident while authoring a baseline:** once the body's AABB overlaps a block it is
  *inside*, a horizontal resolve can eject it by roughly a whole block. `CheckPhysicsCollision` aggregates by
  **largest absolute correction**, and for the containing cell the far-face correction is nearly the full cell width,
  so it dominates the genuine near-face contact. Measured in the harness: a body with its feet 0.1 below a half-slab's
  top, pushed +0.2 along X, resolved to **−0.9** — a metre backwards. Correct behavior for the aggregation rule as
  specified (§3.3), which assumes the body is *outside* the geometry; embedded bodies violate that assumption. This
  is a plausible shared mechanism with **§01** ("collision gets stuck / flaky in tight spaces") and worth
  instrumenting alongside §04, since §04's symptom *is* an embedded body.

**Route:** `voxel-debugging` (instrument first, fix second) — confirm or refute the observation above rather than
building on it. The **`NS-4`** suite now exists (`Minecraft Clone/Dev/Validate Physics Solver`), so the repro lands
there as a `K`-scenario per `validation-driven-bugfix` — prove-red before trusting any fix, then promote to a
baseline after in-game confirmation.

---
