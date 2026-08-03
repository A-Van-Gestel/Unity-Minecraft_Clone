# Known Player related bugs

This document outlines **open** bugs related to the player controller and interaction systems. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** August 2026
>
> **Numbering note:** `§03` and `§04` are **retired, not free.** `§03` belonged to the
> world-gen-tags-leak-into-placement bug (fixed June 2026, archived) and is still cited by name from
> `PlacementValidationSuite*.cs`, `PlacementTagMigration.cs`, `WORLD_SCALING_FLOATING_ORIGIN.md` and
> `FLUID_BUGS.md`; `§04` belonged to the stuck-`IsGrounded` bug (fixed August 2026, archived as Player & Input §08)
> and is cited from `PhysicsSolverValidationSuite.Baseline.cs`, `SUB_VOXEL_COLLISION_SYSTEM.md` and the validation
> coverage roadmap. Reusing either number would silently redirect all of those. New entries continue from `§06`.
>
> **Validation suite:** `Minecraft Clone/Dev/Validate Physics Solver`
> (`Assets/Editor/Validation/PhysicsSolver/`) — the **`NS-4`** suite shipped 2026-08-03 with 23 baselines over the
> real `VoxelRigidbody` + `World.CheckPhysicsCollision`, closing the "largest system with no automated guard" gap
> (see [`../Design/VALIDATION_SUITE_COVERAGE_ROADMAP.md`](../Design/VALIDATION_SUITE_COVERAGE_ROADMAP.md)). `B18`–`B23`
> pin the grounded verdict (the retired §04's territory); §05 below is the one solver defect still open there.

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

## 05. An embedded body is ejected about a whole block by a horizontal resolve

**Severity:** Bug — no known stranding, but it teleports the player and is a candidate root cause for §01
**Status:** Open — found in the harness, **not yet observed in game**
**Files:** `World.CheckPhysicsCollision` (the aggregation rule), `Assets/Scripts/Physics/VoxelRigidbody.cs`

**Description (found while authoring an `NS-4` baseline, 2026-08-03):**
Once the body's AABB overlaps a block it is *inside*, a horizontal resolve can eject it by roughly a whole block in the
**wrong direction**. `CheckPhysicsCollision` aggregates contacts by **largest absolute correction**; for the containing
cell the far-face correction is nearly the full cell width, so it dominates the genuine near-face contact. Measured in
the harness: a body with its feet 0.1 below a half-slab's top, pushed +0.2 along X, resolved to **−0.9** — a metre
backwards.

This is correct behavior *for the rule as specified* (`SUB_VOXEL_COLLISION_SYSTEM.md` §3.3), which assumes the body is
**outside** the geometry. Embedded bodies violate that assumption, and nothing currently prevents one: a block placed
into the player's cell, a chunk loading around them, or a teleport can all produce one.

**Why it is filed separately from §04:** §04 was instrumented and found to leave the body correctly *on* the surface —
so this is not §04's mechanism, and fixing §04 does not touch it. Its trigger (a body already inside geometry) matches
§01 ("collision gets stuck / flaky in tight spaces") far better.

**Route:** needs an in-game repro before a fix — the aggregation rule is on every sweep the engine performs, so
changing it (e.g. prefer the nearest exit face, or the face opposing the movement direction) needs its own `NS-4`
scenarios and a `SUB_VOXEL_COLLISION_SYSTEM.md` §3.3 update. Related: **VQ-4** (compound collision bounds) touches the
same aggregation code.

---
