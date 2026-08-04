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
> (`Assets/Editor/Validation/PhysicsSolver/`) — the **`NS-4`** suite shipped 2026-08-03 with 25 baselines over the
> real `VoxelRigidbody` + `World.CheckPhysicsCollision`, closing the "largest system with no automated guard" gap
> (see [`../Design/VALIDATION_SUITE_COVERAGE_ROADMAP.md`](../Design/VALIDATION_SUITE_COVERAGE_ROADMAP.md)). `B18`–`B23`
> pin the grounded verdict (the retired §04's territory); §05 below is the one solver defect still open there.
> `B24` (added 2026-08-04 as `PH-1`'s step 0) pins the horizontal multi-cell aggregation this entry's "what this is
> NOT" section measured but left unguarded — a full cube at `x = 10.0` beside an east-half slab at `x = 10.5` stops
> the body at `10.00`, in both scan orderings.

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

## 05. Resolving an overlap the body is *inside* ejects it along the movement axis — jumping while embedded drops the player through the floor

**Severity:** Bug — **high consequence, hard to reach.** The usual outcome is a harmless auto-correction onto the
surface; the bad outcome is falling out of a one-block-thick floor into a cave or the void. Reachable today with
console commands only (see triggers), and observed in game exactly once. Also the path any future non-player
`VoxelRigidbody` will meet, since **the player is currently the only one that moves**.
**Status:** Open — mechanism confirmed in the harness and in game (2026-08-03); the *entry* condition is not yet
fully pinned (below)
**Files:** `Assets/Scripts/Physics/VoxelRigidbody.cs` (`ResolveMovement`), `World.CheckPhysicsCollision`

**One rule, three outcomes.** §3.3 resolves a contact by "the correction that fully resolves ALL overlaps on this
axis". For a body *inside* a cell that means leaving the cell along the movement axis, and the cost depends on which
way it was moving. Harness measurements, body embedded 0.8 into a one-block floor (feet 4.200, head 6.000, floor cell
`y = 4`):

| Tick's movement | `dir` | Correction | Outcome |
|---|---|---|---|
| **Downward** (gravity, standing) | −1 | `+0.80` (`blockTop − feet`) | feet → **5.001**, on top of the block, grounded — a clean auto-recovery |
| **Upward** (jump, momentum = `jumpForce`) | +1 | **`−2.00`** (`blockBottom − head`) | feet → **2.199**: shoved down by the whole collider height plus the embed depth, **through the floor**, still falling |
| **Upward, solid rock below** | +1 | `−2.00` | down to 2.199, then the downward recovery walks it back to 5.001 over the next ticks |
| **Horizontal, `IsGrounded == false`** | ±1 | `∓0.90` | input reversed into a ~1-block backward hop |
| **Horizontal, `IsGrounded == true`** | — | — | ✅ no ejection: the **step-up pre-pass** lifts the body out and preserves the input exactly |

**Why it is almost never seen:** gravity makes nearly every tick a *downward* one, and the downward case resolves the
right way — onto the surface. In-game attempts to provoke it (`/teleport ~ ~-1 ~` into ground, `/teleport ~-1 ~ ~`
into a tree, `/setblock ~ ~1 ~ stone` inside the player) were auto-corrected onto the block roughly 9 times in 10.
The one failure was a `/teleport ~ ~-1 ~` that left the player embedded *without* the upward correction firing,
followed by a jump: the body dropped through the block into the cave below.

**The unpinned half — how a body stays embedded long enough to jump.** Remaining embedded requires a tick whose
`movement.y` is *exactly* zero, because the zero-vertical-movement branch reads the ground but never corrects
position. Two candidates, neither confirmed: flying with no vertical intent (`_verticalMomentum` is assigned a hard
`0`), and `IsTeleportHeld` suspending `FixedUpdate` for an arrival hold. Worth instrumenting before designing a fix,
since a fix aimed at the wrong entry path would leave the reachable one open. Note the §04 fix did **not** widen
this: the previous code also reported grounded for an embedded body (a positive correction satisfies its
`> -0.01f` test), so jumping from inside a block was already permitted.

**What this is NOT** (an earlier filing of this entry claimed otherwise, corrected by measurement 2026-08-03):

- It is **not** a far face wrongly dominating a nearer one. For the containing cell there is no nearer blocking face:
  a 0.8-wide collider centred in a 1.0 cell is 0.9 from clearing it in *either* direction (`dir=+1` → `−0.9`,
  `dir=−1` → `+0.9`). "Prefer the nearest exit face" therefore fixes nothing.
- It is **not** depth-dependent on the horizontal axis: embeds of 0.1 and 0.4 both resolve to `−0.9`.
- The largest-absolute-correction rule is **not** itself the defect and must not simply be replaced: for a
  non-embedded body approaching two cells whose blocking faces differ (a full cube at `x = 10.0` beside an east-half
  slab at `x = 10.5`) it correctly stops at the nearer face, `10.00`.

**§3.3 documents the rule two ways that only agree outside the geometry:** "choose LOWEST `blockBounds.min`
(*nearest blocking face*)" and "always pick the contact that produces the *LARGEST absolute correction*, which fully
resolves ALL overlaps". For an embedded body those are different instructions, and the code implements the second.

**How a body reaches the embedded state** (placement cannot do it — `PlaceCellOverlapsPlayer` refuses):
`/teleport` into geometry, `/setblock` into the player's cell, disabling noclip while inside a block (the documented
escape route from the former §04), or a chunk generating/loading around the player. All are rare or admin-only for a
player; none would be rare for a spawned entity.

**Relationship to §01:** stronger than first recorded. §01's trigger is "single-block-wide tunnels **or when flying
through caves**", and flying means `IsGrounded == false` — precisely the state that turns the un-stick into a
backward hop. §01 remains the in-game symptom to chase for a repro.

**The upward correction is surface-seeking, not a local un-stick** (measured 2026-08-03). Each tick it raises the
body to the top of the **highest solid cell its AABB overlaps**, and it repeats every tick:

| Setup | Outcome |
|---|---|
| Buried at `y = 20.5` in solid stone, surface top `y = 60`, gravity only | **Reaches the surface in 20 ticks (~0.4 s)** — about 2.5 blocks per tick |
| Same column with a 2-block air pocket at `y = 30` | Stops at `30.001` — the first pocket tall enough wins |
| Same column with a **fluid** pocket at `y = 30–32` | Stops at `30.001` — the sweep skips fluids, so the body surfaces *into* the lava/water and stays there |
| Shallow embeds of 0.05 / 0.25 / 0.50 / 0.95 in a one-block floor | Moves `+0.051 / +0.251 / +0.501 / +0.951` — exactly the embed depth, a proportional nudge |

The last row is the important one: **the same rule is well-behaved for a shallow embed and pathological for a buried
body**, and the only difference is how many solid cells the AABB spans. So the design lever is the correction's
**magnitude**, not its direction.

**Why this stops being latent soon:** burial is currently near-unreachable, which is the only reason it has never
been hit. Falling blocks (gravel/sand) would make burial a routine gameplay event, so the behavior should be settled
before they land.

**Design options (brainstorm 2026-08-03, NOT decided — do not implement from this list):**

1. **Cap the un-stick correction.** Below the cap, nudge the body out (identical to today's shallow behavior); above
   it, apply nothing and leave the body stuck to dig itself out. Naturally splits the two cases above at their real
   boundary, is the genre-standard answer to being inside a block, and needs no change to
   `CheckPhysicsCollision`. Open question: the cap value, and whether it is expressed in blocks or as a fraction of
   the collider height.
2. **Direction from the entity's own movement / nearest exit.** For a shallow embed the nearest exit already *is*
   upward and equals the embed depth, so this converges with option 1 in the common case; for a buried body there is
   no near exit at all, which again means "stay stuck". Mostly a restatement of option 1 in directional terms.
3. **A very tiny constant push.** ⚠️ Does not work alone: a per-tick nudge still accumulates (0.05/tick is 2.5
   blocks per second), so it reproduces the surfacing behavior in slow motion and is harder to notice. Needs option
   1's cap or a gate regardless.
4. **Flag the uncapped upward ejection for dropped entities only.** Reasonable for future falling-block entities,
   which have no gameplay stake in their position, while players keep the capped behavior.

**Fix the source too, not only the solver.** For falling blocks the correct primary fix is refusing to settle a block
into an entity's AABB (drop it as an item, or delay it); the solver's un-stick should be a last-resort fail-safe that
is explicitly not permitted to teleport. That argues for a cap independently of which option above is chosen.

**Superseded recommendation:** an earlier revision of this entry proposed "always eject an embedded body upward,
whatever the movement direction". The measurements above withdraw it — that would have made the surface-seeking
behavior universal instead of incidental.

**Route:** repro exists in game (jump while embedded over a cave) and in the harness; what is missing is the entry condition above and a decision, because the desired behavior is a
**design decision, not an obvious correction** — which is also why no `K`-scenario is filed yet: a known-bug scenario
must assert the correct behavior, and that has not been chosen. The two candidates, neither costed:

1. The magnitude-capped family in the **Design options** block above (currently the most promising direction).
2. **Resolve only against cells the body is entering** — ignore cells it already overlaps at the start of the resolve,
   so an embedded body walks out rather than being pushed out. Closer to how most voxel engines behave, but it changes
   every horizontal sweep in the engine and would need `SUB_VOXEL_COLLISION_SYSTEM.md` §3.3 rewritten plus new `NS-4`
   scenarios; the C-case measurement above is the baseline it must not regress.

Whichever is picked, the §3.3 wording divergence noted above should be resolved in the same change. Related:
**VQ-4** (compound collision bounds) touches the same aggregation code, and **PH-1** restructures the queries this
path issues.

---
