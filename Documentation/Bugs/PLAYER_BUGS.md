# Known Player related bugs

This document outlines **open** bugs related to the player controller and interaction systems. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** August 2026
>
> **Numbering note:** `§03` and `§04` are **retired, not free.** `§03` belonged to the
> world-gen-tags-leak-into-placement bug (fixed June 2026, archived) and is still cited by name from
> `PlacementValidationSuite*.cs`, `PlacementTagMigration.cs`, `WORLD_SCALING_FLOATING_ORIGIN.md` and
> `FLUID_BUGS.md`; `§04` belonged to the stuck-`IsGrounded` bug (fixed August 2026, archived as Player & Input §08)
> and is cited from `PhysicsSolverValidationSuite.Baseline.cs`, `SUB_VOXEL_COLLISION_SYSTEM.md` and the validation
> coverage roadmap. Reusing either number would silently redirect all of those. New entries continue from `§08`.
>
> **Validation suite:** `Minecraft Clone/Dev/Validate Physics Solver`
> (`Assets/Editor/Validation/PhysicsSolver/`) — the **`NS-4`** suite shipped 2026-08-03 with 26 baselines over the
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

## 06. Movement renders stepped at the physics rate — no interpolation between fixed steps

**Severity:** Polish / feel — not a correctness bug. Constant, mild, and present since the original player
controller (reported by the user 2026-08-04 as "slightly stuttery", long predating `PH-1`/`PH-2`).
**Status:** Open — **mechanism CONFIRMED in game 2026-08-04** by the five-condition test in *Measurements* below.
The cause is the inter-step position freeze; frame-rate caps and timestep retunes are both **ruled out** as fixes.
**Files:** `Assets/Scripts/Physics/VoxelRigidbody.cs` (`FixedUpdate`), `Assets/Scripts/Player.cs` (`Update`),
`Assets/Scenes/World.unity` (camera parenting), `ProjectSettings/TimeManager.asset`

**The player's position advances only 50 times a second, but the frame renders more often than that, and nothing
fills the gap.**

- `VoxelRigidbody.FixedUpdate` is the **only** writer of the player's position — `transform.Translate(Velocity,
  Space.World)` followed by `ClampToWorldBorder`. Nothing moves the player in `Update` or `LateUpdate`.
- The fixed timestep is `2822399 / 141120000 s` = **0.0199999929 s → 50.00002 Hz** (`TimeManager.asset`).
- **`Main Camera` is a child of the player transform** (`World.unity`: player root `&151001796` → children
  `Main Camera` at local `y = 1.65` and `PlayerBody` at `y = 1`; the root's `m_Father` is `0`). So the camera's
  *position* inherits the 50 Hz stepping.
- `Player.Update` rotates the body yaw and the camera pitch **every rendered frame**.
- There is **no interpolation layer**. `Rigidbody.interpolation` — Unity's built-in answer to exactly this — does
  not apply, because this is a custom transform-driven body, not a `Rigidbody`.

**Why it is visible rather than theoretical.** At a 60 Hz display (`vSyncCount: 1` on quality levels 2–4;
levels 0, 1 and 5 are uncapped) against 50 Hz physics, 6 rendered frames span 5 physics steps: **one frame in six
draws the previous position again**, a **10 Hz** beat. At walk speed the step is `3 m/s × 0.02 s` = **0.06 m**
(sprint: 0.12 m), so the eye sees a ~6 cm hitch ten times a second. At an uncapped 144 Hz the ratio is 2.88
frames per step, which is not an integer, so the repeat pattern is uneven (3, 3, 3, 2, …) — the same artifact,
less regular.

**The per-frame look rotation makes it worse, not better.** Because yaw/pitch update smoothly every frame while
translation does not, the view gives the eye a continuously-moving reference against which the stepped
translation stands out. A build that stepped *both* would read as lower frame rate rather than as stutter.

**What this is NOT:**

- **Not the substep chain.** Substeps all resolve inside one `FixedUpdate` and the net displacement is applied
  once; the chain changes nothing about how often the transform moves.
- **Not `PH-1` or `PH-2`.** Both were shown behavior-neutral by shadow-compare (0 mismatches over 142 sweeps and
  5,846 substepped ticks respectively), and the symptom predates them by the whole life of the controller.
  `PH-2` in fact *removes* transform writes; it cannot add stepping.
- **Not main-thread hitching** from meshing/lighting/streaming. That is irregular and load-dependent; this is
  periodic and reproducible at constant speed on flat ground with no chunks loading. If the stutter is instead
  found to be irregular and to track chunk load, it is a different problem and belongs with `P-4`/`FP-*`.

### Measurements (in game, 2026-08-04) — 2560×1440 @ 240 Hz display, ~140 fps uncapped

Five conditions, rated **blind** (the settings behind each were withheld until all five were done). Only
`Time.fixedDeltaTime` and `Application.targetFrameRate` were touched, both runtime-only.

> **Methodology note — what these conditions do and do not control for.** They were run in **editor play
> mode**, where the display path is not characterised: the Game View is a composited child window, and
> `QualitySettings.vSyncCount` reads `0` in edit mode but `1` in play mode, so what actually governs
> presentation is unverified. **The conclusions below deliberately do not rest on it.** The load-bearing
> comparison is **4 vs 5**, which ran at the *same* 100 fps cap and therefore presented identically — the only
> difference was the CPU-side tick rate, and they were rated differently. No display or compositor behavior can
> produce a difference between two conditions that present the same way. The **3 vs 4** pair closes the other
> direction: their frame rates *did* differ (140 vs 100) and they were rated the *same*. Anything that needs the
> display path itself characterised must be re-run in a **standalone build**.

| # | Physics | Render | Frames per step | Observed |
|---|---|---|---:|---|
| 1 | **100 Hz** | ~117 uncapped | 1.17 | walking "kinda smoother"; **jump/collisions broke** — see §07 |
| 2 | 50 Hz | 50 cap | **1.00** | "slideshow"; movement responsive; physics correct |
| 3 | 50 Hz | ~140 uncapped | 2.80 | **baseline** — microstutter visible strafing past a block line |
| 4 | 50 Hz | 100 cap | **2.00 exact** | microstutter **the same as baseline**; physics correct |
| 5 | **100 Hz** | 100 cap | **1.00** | **worse than 4** — "smoother at some times, larger microstutter at others" |

**What the conditions establish:**

- **The artifact is the inter-step position freeze, not a beat frequency.** Conditions 3 (2.80) and 4 (exactly
  2.00) are indistinguishable. If the uneven 3,3,3,2 hold pattern were responsible, the exact integer ratio would
  have cleaned it up — it did not, because the position still holds still between steps either way. **This rules
  out frame-rate capping as a fix.**
- **Matching the render rate to the physics rate does NOT fix it, and can make it worse.** Condition 5 (1:1 at
  100 Hz) was rated *worse* than condition 4 (2:1 at the same 100 fps) — same frame rate, same everything else.
  The reason: **`FixedUpdate` is not phase-locked to rendering.** When the frame period ≈ the physics period,
  ordinary frame-time jitter makes some frames run **0** physics steps and others run **2**, so apparent velocity
  alternates between zero and double. Near-1:1 is the *unstable* regime. Condition 2 escaped it only because a
  50 fps cap leaves the frame budget so loose that the accumulator never slips.
  > ⚠️ An earlier revision of this entry asserted that "only render-rate equal to physics-rate removes the
  > artifact without interpolation". **Condition 5 refutes that.** No rate relationship removes it.
- **A high-refresh display makes it worse, not better** — consistent with the symptom being long-standing on this
  machine while being a minor complaint on 60 Hz hardware.

**Conclusion: render-time interpolation is the only fix that can work.** The four-option list this entry
originally carried collapses to one, because the test eliminated the others empirically rather than by argument.

**Fix — the remaining option (design not yet settled):**

**Interpolate the visual at render time.** Keep physics authoritative at 50 Hz; in `Update`/`LateUpdate` place the
*rendered* transform at `lerp(previousFixedPos, currentFixedPos, alpha)` with
`alpha = (Time.time - Time.fixedTime) / Time.fixedDeltaTime`. Three things to settle first:

1. It adds **up to one physics step (20 ms) of visual latency**. Extrapolation avoids the latency but overshoots
   into geometry on the frame of a collision and snaps back — usually worse in a game where the player is
   constantly against blocks.
2. It **decouples the camera from the collider**. `Main Camera` is a child of the physics transform and
   `PlacementController`'s interaction ray originates there, so the ray would be cast from an interpolated
   position that is not where the body is. Either the ray keeps using the physics position, or the discrepancy is
   accepted and bounded.
3. It should live in the body or a shared visual-follow component, not in `Player` — every future
   `VoxelRigidbody` renders stepped for the same reason.

**Ruled out by measurement, do not re-propose:**

- **Frame-rate caps / matching rates** — conditions 4 and 5 above.
- **Raising the physics rate** — it never removes the freeze (only shortens it), *and* it is independently
  blocked by **§07**: the solver's behaviour is not tick-rate invariant, so a retune breaks jumping and
  collisions. This is also why the user's earlier `0.02 → 0.01` experiment was reverted.
- **Integrating movement in `Update` with a variable delta** — the tunneling model in
  `SUB_VOXEL_COLLISION_SYSTEM.md` §3.4.4 is built on a bounded per-step displacement, and `IsGrounded`, jump
  height and step-up timing would all become frame-rate dependent. Much larger than the symptom warrants, and
  §07 shows the solver is already rate-sensitive at *fixed* rates.

**No validation baseline is proposed.** `NS-4` drives ticks directly with no render loop, so the suite
structurally cannot observe a rendering-cadence artifact — a green suite says nothing here either way, and a
baseline that pretended otherwise would be a false green. Verification for this entry is in-game only.

**Applies to future entities too:** any `VoxelRigidbody` will render stepped for the same reason, so whatever is
chosen should live in the body or a shared visual-follow component rather than in `Player`.

---

## 07. Solver behavior is not tick-rate invariant — halving `fixedDeltaTime` breaks jumping and collisions

**Severity:** Bug — latent at the shipped 50 Hz, but it **blocks any physics-rate change** and would surface on
any platform or setting that retunes the timestep.
**Status:** Open — **reproduced in game 2026-08-04**, in both directions, twice each. **Not root-caused**; the
mechanisms below are hypotheses, deliberately labelled as such.
**Files:** `Assets/Scripts/Physics/VoxelRigidbody.cs` (`CalculateVelocity`, `ResolveMovement`),
`ProjectSettings/TimeManager.asset`

**Symptom.** With `Time.fixedDeltaTime = 0.01` (100 Hz) instead of the project's `0.02` (50 Hz), horizontal
movement is unaffected but **jumping and collision response misbehave** — reported as the body "lagging behind"
and being "pushed back way too strong". At `0.02` the same session behaves correctly.

**Controlled observation** (from §06's five-condition test — the two variables were separated there):

| `fixedDeltaTime` | Render | Jump / collision |
|---|---|---|
| 0.01 (100 Hz) | ~117 fps uncapped | **broken** |
| 0.02 (50 Hz) | 50 cap | correct |
| 0.02 (50 Hz) | ~140 uncapped | correct |
| 0.02 (50 Hz) | 100 cap | correct |
| 0.01 (100 Hz) | 100 cap | **broken** |

Render rate varies across the correct rows and across the broken rows, so **the render rate is not the variable —
the tick rate is**. Nothing else changed between conditions.

**Why this is surprising, and why the suite did not catch it.** The solver's *velocity* terms are correctly
dt-scaled (`Velocity = intent * (dt * MoveSpeed)`, `_verticalMomentum += dt * gravity`), so speeds and terminal
velocity are rate-invariant by construction. What is **not** rate-scaled are the solver's fixed spatial
constants, which are applied **per resolve** rather than per unit time. `NS-4` cannot see this: it derives every
expectation *from* `Time.fixedDeltaTime` rather than asserting that two different rates produce the same physical
trajectory, so a retune moves the expectations along with the behavior. **There is no rate-invariance baseline.**

**Hypotheses (unverified — instrument before fixing):**

1. **`COLLISION_EPSILON` (0.001) is applied per resolve.** Every axis correction adds a stand-off epsilon. At
   double the tick rate it is applied twice as often per second, so anything involving sustained contact
   accumulates push-out at double the rate. Best fit for "pushed back way too strong".
2. **`COLLISION_JITTER_TOLERANCE` (0.001) gates on correction magnitude, which scales with dt.** Per-tick
   penetration depth is proportional to per-tick displacement — a walk step is 0.06 m at 50 Hz and 0.03 m at
   100 Hz — so halving dt changes *which* corrections fall under the tolerance and therefore which ones get the
   epsilon at all. The gate is a fixed distance compared against a dt-proportional quantity.
3. **`GROUND_PROBE_SKIN` (0.002) versus per-tick fall distance.** The grounded probe reaches a fixed distance
   below the feet while the distance fallen per tick halves, so the tick on which ground is detected shifts —
   plausible for "lagging behind" on landing and for jump timing.
4. **Semi-implicit Euler jump apex.** Integration error is ≈ `v·dt/2` = 5.7 cm at 50 Hz vs 2.85 cm at 100 Hz, so
   jump height genuinely differs by ~3 cm between rates. Real, but too small to explain the reported severity —
   listed so it is not mistaken for the cause.
5. **The step-up pre-pass fires per resolve**, snapping `movement.y` whenever horizontal movement is blocked and
   the body is grounded. At double the rate it fires twice as often against the same wall.

**Route:** instrument before fixing, per the `voxel-debugging` protocol — log correction magnitudes, epsilon
applications and `IsGrounded` transitions per tick at both rates against the *same* scripted movement, and
compare. The likely shape of a fix is to express the stand-off/jitter constants in terms that do not accumulate
with tick count, but that should follow the measurement, not precede it.

**Candidate baseline once the intended behavior is decided:** a **rate-invariance differential** in `NS-4` — run
the same physical trajectory at `fixedDeltaTime` and `fixedDeltaTime / 2` and require the same landing height,
rest position and grounded verdict within tolerance. This is the same shape as `B15` (substep invariance), one
level up: `B15` varies the substep count within a tick, this would vary the tick rate itself. It does **not**
exist today, which is precisely why this defect was invisible until someone changed the setting by hand.

**Consequences elsewhere:**

- **Blocks §06's "raise the physics rate" option permanently** — that option is already ruled out on its own
  merits (it cannot remove the inter-step freeze), but this makes it unsafe as well.
- **`PH-1`/`PH-2`'s measured per-tick figures are rate-specific** (2.05 and 2.477 substeps per tick at 50 Hz).
  They would need re-recording after any retune — a bookkeeping consequence, not a correctness one.

---
