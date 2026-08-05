# PH-1 — Gather-once collision sweeps

**Date:** 2026-08-04  
**Branch:** `feat/world-scaling`  
**Item:** `PERFORMANCE_IMPROVEMENTS_REPORT.md` → `PH-1`  
**Verdict:** ✅ **GO** — 2.08× fewer voxel cell reads per `FixedUpdate`, zero fallbacks, behavior unchanged.

---

## 1. What was measured, and why not frame time

`PH-1`'s benefit is rated ⚪ in the report: **one entity runs this solver**, so no frame-time movement was
expected and **none was looked for**. Measuring frame time here would have produced noise and an unfalsifiable
claim. The metric is therefore the one the item is actually about — **voxel cell reads per physics tick** — which
scales linearly with entity count and is what every future mob or dropped item will multiply.

**No A/B build was needed.** `PhysicsCellBuffer.TryQuery` already derives each sweep's own floor-range in order to
decide containment, and that range is *exactly* what the pre-`PH-1` direct scan would have read for that sweep.
Summing it (`CellsScannedIfUngathered`) alongside the real gathered count yields before **and** after from a single
session — no second build, no drift between runs, no differing player path.

## 2. Session

In-editor play session (Mono), ordinary gameplay driven by hand: walking into walls and sliding, repeated step-ups
onto half-slabs and full blocks, jumps including into a low ceiling, sprinting over uneven terrain, fast flight
(the substep chain), falls from height onto both flat ground and slabs, and a one-block-wide gap.

**15,851 physics ticks ≈ 317 s at 50 Hz.**

## 3. Results

| Counter | Total | Per tick |
|---|---:|---:|
| Physics ticks (`CalculateVelocity` with collision) | 15,851 | — |
| Substeps / gathers | 32,555 | 2.05 |
| Sweeps issued | 80,691 | 5.09 |
| **Fallbacks to a direct scan** | **0** | **0** |
| Cells read — **after** (gathered) | 309,306 | **19.51** |
| Cells read — **before** (counterfactual) | 644,428 | **40.66** |
| Direct scans (`World.CheckPhysicsCollision`) | 0 | 0 |

**Reduction: 2.08×** (644,428 → 309,306).

Derived shape of the win:

| Ratio | Value | Reading |
|---|---:|---|
| Sweeps per gather | **2.48** | How many sweeps each gather amortizes over — the leverage |
| Cells per gather | 9.50 | What one gather costs |
| Cells per sweep (counterfactual) | 7.99 | What one old scan cost |

## 4. Honest reading of the number

**2.08× is below the 2.5–3× predicted in the plan, and the prediction was wrong for a specific, understandable
reason.** The item's headline — "up to ~7 sweeps per resolve" — is a genuine worst case, not a typical one:
`ResolveMovement`'s step-up pre-pass and its three follow-up sweeps run **only** when horizontal movement is
blocked *and* the body is grounded. On most ticks the body is unobstructed, so only the vertical resolve and one
or two horizontal probes fire. Measured average: **2.48 sweeps per gather**, not 7.

Two consequences worth recording rather than glossing:

- **The ceiling on this item is ~2.5×, not ~7×**, for a single player moving normally. A crowd of entities
  colliding with walls (mobs pathing into geometry) would sit closer to the worst case, which is where the
  remaining headroom lives.
- **The envelope is not wastefully large.** A gather reads 9.50 cells; a single old sweep read 7.99. The gather is
  only ~19 % wider than one sweep's own range while serving 2.48 of them, so there is very little left to reclaim
  by shrinking it — and shrinking it is what `B25` now forbids.

**Zero fallbacks over 32,555 gathers** is the other half of the result: the envelope derivation (body ∪
destination, plus `stepHeight` head-room above and `GROUND_PROBE_SKIN` below) covered every sweep of real
gameplay, including step-ups and fast substepped flight. The `CellsScannedDirectly` counter independently agrees
at 0 — `World.CheckPhysicsCollision` was never entered during play.

## 5. Correctness evidence (not a perf claim, but the gate this rests on)

- **Shadow-compare pass:** both paths run on every sweep, asserting exact float equality of
  `hit` / `Correction` / `ContactFace` — **0 mismatches over 142 sweeps** of the `NS-4` suite. Removed afterwards.
- **`Validate All`: 410 baselines across 17 suites green**, Physics Solver 25/25.
- **`B25`** pins the envelope and was proven red against a shrunken one (3 of 4 sweeps fell back).
- **In-game confirmed 2026-08-04**: all seven movement cases reported as feeling unchanged.

## 6. Instrumentation

`Physics/PhysicsQueryStats` — all increments behind `[Conditional("UNITY_EDITOR")]` /
`[Conditional("DEVELOPMENT_BUILD")]`, so release builds compile them away. Counters zero on play-mode entry via
their own `[RuntimeInitializeOnLoadMethod]` (domain reload is disabled in this project).

Kept rather than removed: it is the only instrument that can tell "gathered once" from "silently re-scanning per
sweep", and it will be needed again the first time a non-player `VoxelRigidbody` exists.
