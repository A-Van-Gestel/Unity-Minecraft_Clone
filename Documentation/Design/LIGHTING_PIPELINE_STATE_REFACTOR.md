# Lighting Pipeline State & Gate Refactor (LP-*)

**Version:** 1.0
**Date:** 2026-07-06
**Status:** Proposed design — not implemented.
**Target:** Unity 6.4 (Mono for dev; IL2CPP for production)

> Clean-up / refactor plan for the async lighting engine's orchestration layer — the `ChunkData`
> lifecycle-flag cluster, the three neighbor-readiness gates, and the scheduling paths around them.
> The single most important decision: **the flag cluster is NOT collapsed into an exclusive
> state-machine enum — the flags are a legal *set* of pending-work kinds, not a position in a
> chain — it is instead collapsed into a `[Flags]` work byte behind a named transition API, with
> the neighbor gates extracted into one shared pure predicate.** Storage stays trivially cheap and
> combination states stay representable; what becomes structured (and unit-testable, and
> harness-shared) is the *transitions* and the *gate computation*, which is where every historical
> pipeline bug actually lived. PRIMARY goal is clarity/testability; performance is SECONDARY
> (one optional micro-phase). Zero on-disk change in every phase — no AOT migration is required
> anywhere in this plan, by construction.

**Audited:** 2026-07-06, at commit `4cb80e4` (branch `feat/async-lighting-validation-suite`).
Findings are from static review of `Data/ChunkData.cs` (flag cluster L111–183, `Reset` L242–288,
`ModifyVoxel` L470–569), `World.cs` (Update scan arm L1558–1682, startup coroutine L1024–1123,
gates L1883–2028, `UnloadChunks` L2360–2404, `LoadOrGenerateChunk` L836–928),
`WorldJobManager.cs` (`ScheduleLightingUpdate` L550–643, `ProcessLightingJobs` + completion driver

+ `MergeCompletedLightingJob` L1008–1301, `TriggerNeighborEdgeChecks` L1617–1641),
  `Helpers/LightingScanDecision.cs`, `Helpers/LightingScheduleDecision.cs`,
  `Helpers/LightingCompletionPass.cs`, `Helpers/LightWorkScheduler.cs`,
  `Serialization/ChunkSerializer.cs` (L115–258), and the editor harness
  (`LightingFrameSimulator.cs` structure, `LightingTestWorld.cs` gate analogs L379–410). Line
  numbers are anchors for the executor, not contracts — re-verify before editing.

**Relationship to other documents:**

- [`../Architecture/CHUNK_LIFECYCLE_PIPELINE.md`](../Architecture/CHUNK_LIFECYCLE_PIPELINE.md) —
  the authoritative flag/gate reference (§2/§3) this plan restructures; every phase doc-syncs it.
- [`../Architecture/LIGHTING_SYSTEM_OVERVIEW.md`](../Architecture/LIGHTING_SYSTEM_OVERVIEW.md) —
  the async BFS model; §3.2/§3.5/§3.6 describe the scheduling/gate behavior LP-2/LP-5 touch.
- [`LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md`](LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md) — AS-2 +
  HF-4 delivered the shared scan arm (`LightingScanDecision`), schedule guard
  (`LightingScheduleDecision`), and completion pass (`LightingCompletionPass`). **This plan builds
  ON those extractions and must not redo them**; it extends the same shared-guard pattern to the
  two surfaces HF-4 did not reach (neighbor gates, flag transitions). AS-3/AS-4/AS-5 are
  orthogonal (scenario/fuzz work, not structure) and keep their own IDs.
- [`../Architecture/Testing Framework/LIGHTING_VALIDATION_HARNESS_FIDELITY.md`](../Architecture/Testing%20Framework/LIGHTING_VALIDATION_HARNESS_FIDELITY.md)
  — LP-2 closes the B2 remainder (readiness *computation* out of harness scope); LP-4 upgrades the
  B4 surface (flag transitions become shared named methods).
- [`../Architecture/Testing Framework/LIGHTING_FRAME_SIMULATOR_DESIGN.md`](../Architecture/Testing%20Framework/LIGHTING_FRAME_SIMULATOR_DESIGN.md)
  — the simulator both modes of which are the regression instrument for every phase here.
- [`VALIDATION_SUITE_COVERAGE_ROADMAP.md`](VALIDATION_SUITE_COVERAGE_ROADMAP.md) — NS-3 (chunk
  lifecycle state-machine suite) names the flag-pairing assertion family; LP-1's invariant probes
  and LP-4's transition API are deliberate groundwork for NS-3.
- [`MESHING_PIPELINE_ORCHESTRATION_REFACTOR.md`](MESHING_PIPELINE_ORCHESTRATION_REFACTOR.md) —
  the MP-* meshing sibling of this plan (same patterns: probes, pure-decision extraction, shared
  completion skeleton). Coordination points: MP-4 renames `LightingCompletionPass` (order against
  LP-3 freely — the suites arbitrate); MP-2 can consume LP-2's `NeighborReadinessDecision` facts
  if LP-2 lands first, but has no hard dependency on it.

---

## 1. Goals & non-goals

### Goals

1. **Make the implicit per-chunk lighting state machine explicit and auditable** — every flag
   transition a named method with a documented trigger, instead of ~20 scattered raw writes
   across 4 files (§2.4 census).
2. **Close the remaining production/harness drift surfaces** — the neighbor gates and the startup
   coroutine still hand-mirror logic the harness cannot drive (the pattern HF-4 fixed for the
   scan arm and completion pass).
3. **Make illegal *partial transitions* unrepresentable** — atomic schedule-clear, atomic edge
   re-arm (round decrement + both flags together), so the "flag set whose clear site is
   unreachable" bug class (three historical deadlocks) loses its raw material.
4. **Preserve behavior byte-for-byte at every phase boundary** — 62 lighting baselines + scheduler
   mode + LightScheduler suite green, no on-disk change, MT-2 promotion contract intact.
5. *(SECONDARY)* Trim redundant per-frame gate work in the ready-set scan (LP-6, optional,
   measured before shipped).

### Non-goals (v1)

- **Sun→Sky naming unification** (`SunlightBfsQueue`, `AddToSunLightQueue`,
  `SunlightRecalculationQueue`, …) — owned by the existing **Phase B legacy-light-removal plan**
  (see `project_phase_b_legacy_light_removal` / DATA_STRUCTURES notes). LP-7 fixes only the
  doubled-word typo `RecalculateSunLightLight`.
- **Re-extracting the scan arm, schedule guard, or completion pass** — done (HF-4 #1/#2, AS-2).
- **Changing MT-2 scheduling semantics** (ready/waiting split, promotion events, `PromoteAll`
  fail-safe) — the split is intentional, guarded by its own suite, and out of scope. LP-4 only
  funnels the *callback firing* through one site with identical observable semantics.
- **Relaxing or tightening any readiness gate** — `AreNeighborsMeshReady` stays deliberately
  relaxed (the §9.3 wave-front deadlock fix); `AreNeighborsReadyAndLit` stays the edge arm's
  gate. LP-2 is a pure re-housing of the existing predicates.
- **Persisting the new work byte** — the serialized surface stays exactly one bool
  (`NeedsInitialLighting`). If a future feature ever persists more, that is a
  `serialization-migration` item outside this plan.
- **Lighting→meshing handoff coverage** (fidelity B5) — unchanged boundary.

---

## 2. Current state — the flag & gate surface

### 2.1 Per-chunk state inventory

All mutation is main-thread-only (chunk-pipeline rule); jobs read snapshots. "Callback" = setter
fires `ChunkData.OnLightWorkFlagged` → `LightWorkScheduler.Flag` on a false→true transition.

| State                         | Storage                                    | Serialized?                                                                                                          | Callback | Set by (sites)                                                                                                                                                                                                                                                                                                                                             | Cleared by                                                                                                              |
|-------------------------------|--------------------------------------------|----------------------------------------------------------------------------------------------------------------------|:--------:|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------|
| `IsPopulated`                 | plain bool, `[NonSerialized]`              | no (implied true by a chunk record existing)                                                                         |    no    | `Populate` / `PopulateFromFlattened` path; `ChunkSerializer.ReadChunkInternal:249`                                                                                                                                                                                                                                                                         | `Reset()`                                                                                                               |
| `IsLoading`                   | plain bool, `[NonSerialized]`              | no                                                                                                                   |    no    | `CheckViewDistance` (`World.cs:2550`)                                                                                                                                                                                                                                                                                                                      | `Reset()` only (F9)                                                                                                     |
| `NeedsInitialLighting`        | property + backing bool, `[NonSerialized]` | **YES — the only persisted flag** (`ChunkSerializer.cs:123` write, `:206` read; `Migration_v2_to_v3` forces it true) |   yes    | `ProcessGenerationJobs` (`WorldJobManager.cs:817`); disk read (`ChunkSerializer.cs:206`)                                                                                                                                                                                                                                                                   | scan initial arm (`World.cs:1652`); `LoadOrGenerateChunk:905`; coroutine (`World.cs:1047`, `1119`); `Reset()`           |
| `HasLightChangesToProcess`    | property + backing bool, `[NonSerialized]` | no — **re-derived on load** from non-empty persisted BFS queues (`ChunkSerializer.cs:246`)                           |   yes    | `AddToSunLightQueue`/`AddToBlockLightQueue` (edits, cross-chunk mods, wake-ups); schedule-declined (`WorldJobManager.cs:563`); edge arm pre-set (`World.cs:1647`, coroutine `:1063`); stable re-arm (`WJM:1291`); unstable (`WJM:1298`); merge fault (`WJM:1126`); neighbor edge trigger (`WJM:1641`); pending-column recovery (`WJM:805`, `World.cs:847`) | `ScheduleLightingUpdate` success (`WJM:630`); disabled-lighting clears (`World.cs:1120`); `Reset()`                     |
| `NeedsEdgeCheck`              | property + backing bool, `[NonSerialized]` | no — re-derived: disk-loaded stable chunks get it set (`World.cs:919`)                                               |   yes    | stable re-arm (`WJM:1290`); `TriggerNeighborEdgeChecks` (`WJM:1640`); disk-load-stable (`World.cs:919`)                                                                                                                                                                                                                                                    | `ScheduleLightingUpdate` success (`WJM:631` — read into `PerformEdgeCheck` at `:624` first); disabled clears; `Reset()` |
| `IsAwaitingMainThreadProcess` | plain public bool, `[NonSerialized]`       | no                                                                                                                   |    no    | merge start (`MergeCompletedLightingJob`, `WJM:1169`)                                                                                                                                                                                                                                                                                                      | completion driver `ReleaseJob` finally (`WJM:1135`) — **same `ProcessLightingJobs` pass** (F1); `Reset()`               |
| `RemainingEdgeCheckRounds`    | plain int, `[NonSerialized]`, default 2    | no                                                                                                                   |    no    | re-grant to ≥1 on border-column opacity edit (`ChunkData.cs:551–554`, Bug 05)                                                                                                                                                                                                                                                                              | decrement per stable pass (`WJM:1285`); `Reset()` → 2                                                                   |

**Off-chunk state that co-encodes the machine** (an on-chunk representation can never be
authoritative for these): `JobManager.GenerationJobs` / `LightingJobs` / `MeshJobs` membership
(in-flight axes), `LightWorkScheduler` ready/waiting/staging membership,
`worldData.SunlightRecalculationQueue` (per-chunk pending column sets — a fourth work store, F6),
the managed BFS queues on `ChunkData`, `_meshBuildQueue` membership, `Chunk.IsActive`.

### 2.2 The gates

| Gate                               | Checks per neighbor (8 horizontal)                                                                                   | Used by                                                                               | Notes                                                                                                                            |
|------------------------------------|----------------------------------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------|
| `AreNeighborsDataReady` (W:2002)   | in-world skip; no gen job; exists + `IsPopulated`                                                                    | initial + regular scan arms; `ScheduleLightingUpdate` guard; `LoadOrGenerateChunk`    | one `AllNeighborOffsets` loop                                                                                                    |
| `AreNeighborsReadyAndLit` (W:1883) | DataReady + no lighting job + `!HasLightChangesToProcess` + `!NeedsInitialLighting` + `!IsAwaitingMainThreadProcess` | edge arm only (production); harness analog `LightingTestWorld.cs:379` hand-mirrors it | **two duplicated loops** (cardinals then diagonals, identical predicate — F3); an orphaned docstring sits above it (W:1864–1870) |
| `AreNeighborsMeshReady` (W:1966)   | in-world skip; no gen job; exists + `IsPopulated`; `!NeedsInitialLighting` (skipped when lighting disabled)          | `ScheduleMeshing`                                                                     | deliberately relaxed — the §9.3 wave-front fix; must stay relaxed                                                                |

### 2.3 The implicit state machine the code actually relies on

Three semi-independent axes, not one chain:

- **Data axis (exclusive, monotonic per lifecycle):**
  `Placeholder → (Loading | Generating) → Populated`, encoded by `IsLoading` + `IsPopulated` +
  `GenerationJobs` membership. Reset by pool recycle.
- **Lighting work axis (a SET, not a chain):** the bits `I` (`NeedsInitialLighting`),
  `C` (`HasLightChangesToProcess`), `E` (`NeedsEdgeCheck`), plus job-in-flight `J`
  (`LightingJobs` membership), rounds counter `R ∈ {0,1,2}`, and scheduler membership
  (ready / waiting / absent).
- **Merge-transient axis:** `IsAwaitingMainThreadProcess` (`A`) — see F1: its true-window is
  confined to one main-thread call stack.

**Legal bit combinations observed in code** (all 8 are reachable; an exclusive enum would need
the full power set):

| `I C E` | How it arises                                                                                                        |
|:-------:|----------------------------------------------------------------------------------------------------------------------|
| `0 0 0` | idle / just-scheduled (all clears at `ScheduleLightingUpdate`)                                                       |
| `1 0 0` | generation completed (`WJM:817`); disk load with persisted `I=1` and empty queues                                    |
| `1 1 0` | disk load with `I=1` **and** non-empty persisted queues (`ChunkSerializer:206` + `:246`); pending-column restore     |
| `0 1 0` | edits / cross-chunk mods / unstable completion / schedule-declined                                                   |
| `0 0 1` | disk-load-stable (`World.cs:919`) or `TriggerNeighborEdgeChecks` on a quiet neighbor — waits on the strict edge gate |
| `0 1 1` | stable re-arm (`WJM:1290–1291`); neighbor edge trigger onto an already-dirty chunk; the §7 weak-gate fallback state  |
| `1 0 1` | disk-load-stable chunk whose neighbor then re-arms it… then a mod arrives → `1 1 1`; rare but reachable              |
| `1 1 1` | union of the above — legal, drains in priority order I → E → C                                                       |

**Transition census** (the ground truth LP-4's API must reproduce; arrows are bit effects):

| #  | Trigger (site)                                                                        | Effect                                                                                                     |
|----|---------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------|
| 1  | Generation completes (`WJM:817`)                                                      | `I:=1`                                                                                                     |
| 2  | Disk read: persisted flag / non-empty queues (`ChunkSerializer:206/246`)              | `I:=persisted`, queues>0 → `C:=1` *(background thread — the callback's thread-safe staging path)*          |
| 3  | Disk-load-stable (`World:919`) / pending columns (`World:847`) / recovery (`WJM:805`) | `E:=1` / `C:=1` / `C:=1`                                                                                   |
| 4  | Voxel edit / cross-chunk apply / wake-up (`AddTo*Queue`)                              | `C:=1`                                                                                                     |
| 5  | Border-column opacity edit (`ChunkData:554`, Bug 05)                                  | `R := max(R, 1)`                                                                                           |
| 6  | Scan **initial** arm schedules (`World:1644–1655`)                                    | recalc fills queues (`C:=1`), schedule → `C:=0, E:=0(if set)`, then `I:=0`; `J:=1`                         |
| 7  | Scan **edge** arm schedules (`World:1647`)                                            | `C:=1` (pre-set so the schedule guard passes), schedule reads `E→PerformEdgeCheck`, → `C:=0, E:=0`; `J:=1` |
| 8  | Scan **regular** arm schedules                                                        | schedule → `C:=0`, **and `E:=0` if set — the §7 weak-gate fallback (F4)**; `J:=1`                          |
| 9  | Schedule declined `NeighborsNotReady` (`WJM:563`)                                     | `C:=1` (re-asserted), caller parks                                                                         |
| 10 | Merge, stable, `R>0` (`WJM:1283–1293`)                                                | `R--`, `E:=1, C:=1` on self; `E:=1, C:=1` on 4 populated+lit cardinals (`WJM:1640–1641`)                   |
| 11 | Merge, unstable (`WJM:1298`) / merge fault (`WJM:1126`)                               | `C:=1`                                                                                                     |
| 12 | Merge bracket (`WJM:1169` / `:1135`)                                                  | `A:=1` … `A:=0` in the same pass (F1)                                                                      |
| 13 | Lighting disabled (`World:1119–1121`, §6 of the lighting overview)                    | `I,C,E := 0`                                                                                               |
| 14 | Pool recycle (`Reset()`, `ChunkData:242`)                                             | everything := defaults, `R:=2`                                                                             |
| 15 | Startup coroutine sweeps (`World:1036–1075`)                                          | hand-mirrored copies of #6/#7/#8 with `Allocator.TempJob` (F2)                                             |

Scheduler-membership transitions ride these: any bit 0→1 fires the callback → staging → ready;
park on gate-fail / in-flight / unpopulated; promote on completion (`WJM:1149`), generation/load
completion, own re-flag, or the ~1 s `PromoteAll` fail-safe.

### 2.4 Findings (the clean-up backlog this plan executes)

| #   | Finding                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                              |          Addressed by           |
|-----|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:-------------------------------:|
| F1  | **`IsAwaitingMainThreadProcess` has a ~zero observable window.** It is set at merge start and cleared in the same `ProcessLightingJobs` pass's per-job `finally` (`WJM:1169`/`1135`). Every reader (`AreNeighborsReadyAndLit` W:1920/1943, `UnloadChunks` W:2373, harness analog) runs in a *different* step of the frame, after the pass completed — so it can never observe `true`. The in-flight window it was presumably meant to guard is already covered by `LightingJobs.ContainsKey`, which the same gates check. Candidate for deletion — after instrumentation proof, not reasoning alone. |           LP-1 → LP-3           |
| F2  | **The startup coroutine hand-mirrors the scan arms** (`World.cs:1036–1075`): initial/edge/regular arm bodies duplicated inline, NOT routed through `LightingScanDecision` (HF-4 reached only the `Update` scan). A drift surface: the startup path can silently disagree with the steady-state scan the harness guards.                                                                                                                                                                                                                                                                              |              LP-5               |
| F3  | **Gate duplication ×3.** `AreNeighborsReadyAndLit` runs two identical loops (cardinals, then diagonals); the three production gates are three hand-rolled loops over the same neighbor facts; the harness hand-mirrors two of them (`LightingTestWorld.cs:379`) — the fidelity-B2 remainder ("a bug in the readiness computation itself is out of scope"). Plus an orphaned stray docstring above `PromoteLightWorkNeighborhood` (W:1864–1870).                                                                                                                                                      |              LP-2               |
| F4  | **`ScheduleLightingUpdate` silently reads + clears `NeedsEdgeCheck`** (`WJM:624/631`). This makes the §7 weak-gate fallback (edge check running under `AreNeighborsDataReady`) an *implicit* side effect of the regular arm — documented in the pipeline doc but invisible in any signature, and covered by **no dedicated baseline** today.                                                                                                                                                                                                                                                         |              LP-5               |
| F5  | **`HasLightChangesToProcess` triple duty**: "managed queues have nodes", "reschedule me" (unstable/fault), and "satisfy the schedule guard" (edge-arm pre-set W:1647). The bit is fine; the *intent* is invisible at call sites.                                                                                                                                                                                                                                                                                                                                                                     |              LP-4               |
| F6  | **`SunlightRecalculationQueue` is a fourth work store guarded by convention only.** Every current enqueuer also sets `C` (verified: `ModifyVoxel`, `World:847`, `WJM:805`), but nothing enforces "queued column ⇒ chunk flagged", and the fail-safe scan checks only the three flags — an unflagged entry would sleep until unload persists it.                                                                                                                                                                                                                                                      | LP-1 (probe), LP-4 (structural) |
| F7  | **Eager double-gate evaluation in the scan** (`World:1630–1631`): both `AreNeighborsDataReady` AND `AreNeighborsReadyAndLit` are computed for every ready chunk each visit (each 8 dictionary lookups + job-dict probes), though each arm needs only one. Small (O(ready) per frame, post-MT-2), but free to fix once gates are consolidated.                                                                                                                                                                                                                                                        |         LP-6 (optional)         |
| F8  | **Naming:** `RecalculateSunLightLight()` (doubled word). The wider Sun/Sky split is Phase B's — out of scope here.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   |              LP-7               |
| F9  | **`IsLoading` is never cleared** outside `Reset()` — benign today (`IsPopulated` short-circuits the duplicate-load check once loaded), but the pipeline doc's §2 already has to footnote it. Fold into the data-axis clarity work; no dedicated phase.                                                                                                                                                                                                                                                                                                                                               |           LP-4 (doc)            |
| F10 | **Initial arm does work before the schedule can decline** (`World:1644–1649`): `RecalculateSunLightLight()` runs before `ScheduleLightingUpdate`; on a decline the queue-fill repeats next visit. Benign (idempotent), noted for the LP-5 executor; not worth its own change.                                                                                                                                                                                                                                                                                                                        |            — (noted)            |

---

## 3. Decision: how to structure the per-chunk lighting state

The pivotal choice — everything else in the plan is either preparation for it or independent of it.

### Option A — one exclusive lifecycle enum (`ChunkLightingState { Placeholder, …, Lit }`) (rejected)

- ✅ The intuitive reading of "collapse the flag cluster into a state machine"; a single field to
  inspect in the debugger; some illegal states genuinely unrepresentable.
- ❌ **The work flags are a set, not a chain.** All 8 `I/C/E` combinations are reachable and
  meaningful (§2.3 table) — an exclusive enum must enumerate the power set (× rounds counter),
  and every "state" is really "which work kinds are pending", i.e. a bit-set wearing an enum
  costume.
- ❌ **The machine's other halves live off-chunk** (`LightingJobs`/`GenerationJobs` membership,
  scheduler ready/waiting, mesh queue). An on-chunk enum claiming to be *the* state would be
  authoritative for none of them and would need constant reconciliation — a new bug class, the
  opposite of the goal.
- ❌ Every consumer (gates, scan arms, serializer) reads individual bits; an enum forces
  decode/re-encode at each site for zero information gain.

### Option B — `[Flags]` work byte + named transition API + shared gate predicate ✅ **CHOSEN**

Keep the three work bits as data (one `byte`), and make the **transitions** the structured,
testable artifact: every mutation goes through a named `ChunkData` method mapping 1:1 to a §2.3
census row, with the flag→scheduler callback fired from a single funnel. Pair it with extracting
the per-neighbor gate predicate into shared pure code (the harness currently hand-mirrors it).
This is the proven house pattern — it is exactly what `LightingScheduleDecision`,
`LightingScanDecision`, `LightingJobProcessor`, and `LightingCompletionPass` did for their slices
(A2/B2/HF-4 all CLOSED on the fidelity backlog), extended to the last two unshared surfaces.

What becomes unrepresentable is precisely what historically broke: **partial transitions**. The
schedule-clear is one atomic method (can no longer clear `C` but strand `E`, or vice versa); the
stable re-arm decrements `R` and sets `E`+`C` in one call (a recycled-counter or half-armed state
can't be authored); `Reset()` clears through the same funnel `B34`'s reflection backstop guards.
Editor-only assertions (`[Conditional]`, the HF-1 pattern) can then enforce transition
preconditions at zero IL2CPP cost.

### Option C — status quo + naming/docs only (rejected)

- ✅ Zero risk; the pipeline doc §2 already tabulates the flags well.
- ❌ **Leaves every F-finding standing**: the gates and coroutine stay hand-mirrored drift
  surfaces, transitions stay a 20-site scatter enforceable only by rule-following, and the NS-3
  flag-pairing suite would have no structural hook to assert against.

---

## 4. Target architecture

### 4.1 `LightingWork` byte + transition API (LP-4)

```csharp
/// <summary>Pending lighting work kinds for one chunk (a set — kinds combine; see the
/// transition methods on ChunkData for the only legal mutation sites).</summary>
[Flags]
public enum LightingWork : byte
{
    None            = 0,
    InitialLighting = 1 << 0, // was NeedsInitialLighting  (the only persisted bit)
    LightChanges    = 1 << 1, // was HasLightChangesToProcess
    EdgeCheck       = 1 << 2, // was NeedsEdgeCheck
}
```

On `ChunkData`: one `[NonSerialized] private LightingWork _lightingWork;` replaces the three
backing bools. The three existing bool properties remain as thin bit adapters during migration
(and possibly permanently — decided by call-site count at LP-4 execution, §8 Q2). All writes are
replaced by transition methods (1:1 with the §2.3 census; names final at implementation):

| Method                                  | Census rows | Semantics                                                                                                         |
|-----------------------------------------|:-----------:|-------------------------------------------------------------------------------------------------------------------|
| `FlagInitialLighting()`                 |    1, 2     | `I:=1`                                                                                                            |
| `FlagLightWork()`                       | 3, 4, 9, 11 | `C:=1`                                                                                                            |
| `FlagEdgeCheck()`                       |    3, 10    | `E:=1` (disk-load-stable; neighbor trigger)                                                                       |
| `ArmEdgeCheckRoundIfAvailable()` → bool |     10      | if `R>0`: `R--`, `E:=1`, `C:=1`, return true — the atomic stable re-arm                                           |
| `RegrantBorderEditEdgeRound()`          |      5      | `R := max(R, BORDER_EDIT_EDGE_CHECK_ROUNDS)` — the Bug-05 fix, preserved verbatim                                 |
| `OnLightingJobScheduled()`              |   6, 7, 8   | `C:=0; E:=0` — the atomic schedule-clear (`PerformEdgeCheck` is *read* by the caller before scheduling, as today) |
| `ClearInitialLighting()`                |      6      | `I:=0` after a successful initial schedule (kept separate: the coroutine/load paths clear it independently)       |
| `ClearAllLightingWork()`                |   13, 14    | disabled-lighting paths + `Reset()`                                                                               |

**Callback funnel:** one private `SetWork(LightingWork next)` compares old/new masks and fires
`OnLightWorkFlagged(Position)` when any bit transitions 0→1 — preserving today's per-property
semantics with one accepted, verified-equivalent delta: sites that today set two properties
back-to-back (e.g. the stable re-arm) fire the callback **once instead of twice**. Downstream is
a `ConcurrentQueue` drained into a `HashSet` (`LightWorkScheduler.DrainStaging` → `AddReady`), so
duplicate enqueues were already deduplicated — observable behavior is identical. The funnel keeps
the thread-safety property the serializer path relies on (row 2 sets bits from a background
thread; the callback is the thread-safe member).

**Hot-path cost:** byte masks replace bool fields for main-thread readers (scan visits only the
ready set, O(schedulable); gates read 8 neighbors per call). No Burst surface exists — jobs never
read these flags (chunk-pipeline rule). No allocation anywhere (methods are plain instance
methods; the funnel is a compare+branch, same as today's property setters).

### 4.2 Shared neighbor-gate predicate (LP-2)

```csharp
/// <summary>Pure per-neighbor readiness predicate shared by World's three gates and the
/// editor harness — the gate-side completion of the shared-guard pattern
/// (LightingScheduleDecision / LightingScanDecision / LightingCompletionPass).</summary>
public static class NeighborReadinessDecision
{
    public enum Gate : byte { DataReady, ReadyAndLit, MeshReady }

    /// <summary>Facts about ONE neighbor, assembled by the caller (World or the harness).</summary>
    public readonly struct NeighborFacts { /* inWorld, generationInFlight, lightingInFlight,
        existsAndPopulated, needsInitialLighting, hasLightChanges, awaitingMainThread,
        lightingEnabled — plain bools, no references */ }

    public static bool NeighborBlocks(Gate gate, in NeighborFacts facts);
}
```

`World`'s three gates become one `AllNeighborOffsets` loop each (killing `AreNeighborsReadyAndLit`'s
duplicated cardinal/diagonal loops), assembling `NeighborFacts` and calling the shared predicate.
`LightingTestWorld.AreNeighborsReadyAndLit`/`AreNeighborsDataReady` assemble harness facts and call
the *same* predicate — the readiness computation stops being a hand-mirrored fidelity gap (B2
remainder). `in`-struct of bools: no allocation, trivially inlined.

### 4.3 What deliberately does NOT change

- `LightWorkScheduler` (MT-2): untouched. Promotion contract, fail-safe, staging — all as-is.
- `LightingScanDecision` / `LightingScheduleDecision` / `LightingCompletionPass`: untouched in
  LP-1..4 (LP-5 adds a caller; LP-6 may add an overload — both keep the shared-code property).
- `RemainingEdgeCheckRounds` semantics incl. the Bug-05 border-edit re-grant: preserved verbatim
  behind named methods.
- The relaxed `AreNeighborsMeshReady` contract and the `NeedsEdgeCheck`-is-not-a-gate-input rule
  (pipeline doc §3.3 note): preserved bit-for-bit by LP-2.
- `RunReGrantedEdgeCheckRound` (harness legacy-mode backstop): untouched.

---

## 5. Serialization impact (all phases)

The save boundary carries exactly **one** lighting flag today: `NeedsInitialLighting`, one bool in
the chunk record (`ChunkSerializer.cs:123/206`; `Migration_v2_to_v3_RestoreLighting` force-writes
it). `HasLightChangesToProcess` is *re-derived* on read from the persisted BFS queue counts
(`:246`), and `NeedsEdgeCheck` is re-derived by `LoadOrGenerateChunk` (`World.cs:919`).
`IsAwaitingMainThreadProcess` and `RemainingEdgeCheckRounds` are `[NonSerialized]`.

Consequently: **no phase in this plan changes the on-disk byte layout.** LP-4's serializer edit is
a mapping change only (write the `InitialLighting` bit as the same bool at the same offset; read
it back through `FlagInitialLighting()`). No `SaveSystem.CURRENT_VERSION` bump, no migration step.

**Tripwire for executors:** if any phase finds itself wanting to persist the work byte, additional
flags, or the rounds counter — stop; that is an AOT-migration item (`serialization-migration`
skill: version bump + frozen-DTO migration step) and a scope change to bring back to the user.

---

## 6. Constraint compliance checklist

| Project constraint                              | How this plan complies                                                                                                      |
|-------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------|
| Voxels are packed `uint`s, no per-voxel objects | Untouched — this is chunk-level orchestration state only.                                                                   |
| Burst jobs 100 % Burst-compatible               | Jobs never read lifecycle flags (main-thread-only rule); no job file is edited.                                             |
| No GC / LINQ in hot paths                       | Transition methods and `NeighborFacts` are allocation-free; no delegates in per-frame paths (LP-6 uses a cached interface). |
| Pooling conventions                             | `Reset()` keeps clearing every transient through the funnel; B34's reflection backstop still guards new fields generically. |
| No BinaryFormatter/JSON for terrain             | Serializer edit is a bit↔bool mapping at the existing offset; layout unchanged (§5).                                        |
| BlockIDs constants, no raw IDs                  | N/A — no block-level code touched.                                                                                          |

---

## 7. Phased implementation plan

Ranked by value-vs-risk with PRIMARY = clarity/testability. Every phase is independently
landable and leaves the repo green. **Universal regression gate for every phase** (stated once,
applies to all): all 62 baselines of `Minecraft Clone/Dev/Validate Lighting Engine` green (legacy

+ scheduler mode), the LightScheduler suite (9 baselines) green,
  `dotnet build "Assembly-CSharp.csproj"` AND `dotnet build "Assembly-CSharp-Editor.csproj"` clean
  (harness files are editor-assembly), plus the per-phase extras below. Workflow gotchas apply:
  newly created `.cs` files need a Unity import before `dotnet build` sees them; the menu suite can
  run stale code after compilation — confirm red/green flips with a fresh
  `RequestScriptCompilation` + `Unity_RunCommand` wave.

| Phase                                               | Scope (files)                                                                                                                                     | Effort | Depends on                         |
|-----------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------|:------:|------------------------------------|
| **LP-1 — Invariant probes**                         | `World.cs`, `WorldJobManager.cs` (editor-only diagnostics)                                                                                        |   🟢   | —                                  |
| **LP-2 — Shared neighbor-gate predicate**           | new `Helpers/NeighborReadinessDecision.cs`; `World.cs` gates; `LightingTestWorld.cs`                                                              |   🟡   | —                                  |
| **LP-3 — Retire `IsAwaitingMainThreadProcess`**     | `ChunkData.cs`, `WorldJobManager.cs`, `World.cs`, harness, rules/docs                                                                             |   🟡   | LP-1 (evidence), LP-2              |
| **LP-4 — `LightingWork` byte + transition API**     | `ChunkData.cs`; call sites in `World.cs`, `WorldJobManager.cs`, `ChunkSerializer.cs`, `ChunkStorageManager.cs`; harness; new transition baselines |   🔴   | LP-2 (fewer sites); LP-3 preferred |
| **LP-5 — Explicit scheduling contract + coroutine** | `WorldJobManager.ScheduleLightingUpdate`; `World.cs` coroutine; new fallback baseline                                                             |   🟡   | LP-4                               |
| **LP-6 — Lazy strict-gate evaluation** *(optional)* | `LightingScanDecision.cs` overload; `World.cs` scan; `LightingFrameSimulator.cs`                                                                  |   🟢   | LP-2                               |
| **LP-7 — Naming & doc hygiene**                     | `RecalculateSunLightLight` rename; residual doc alignment                                                                                         |   🟢   | —                                  |

**Minimal standalone-value set:** LP-1 + LP-2 (closes the fidelity B2 remainder and de-risks
everything after). **Validation is built alongside, not after** — LP-4 and LP-5 each add
baselines (B71+; check the suite tip before numbering — B62–B70 are taken, B17–B21/B23–B25
retired) in the same commit as the code.

---

### LP-1 — Invariant probes (🟢, no behavior change)

**Delivers:** mechanical evidence for the two convention-only invariants (F1, F6) that later
phases rely on — the same "instrument before you refactor" discipline as HF-1.

- **Scope:** editor/dev-only (`[Conditional("UNITY_EDITOR")]` + `DEVELOPMENT_BUILD`, HF-1's dual
  pattern; zero IL2CPP cost):
    1. In `AreNeighborsReadyAndLit` and `UnloadChunks`, count observations of
       `IsAwaitingMainThreadProcess == true` (a static counter + one `Debug.LogWarning` on first
       hit, naming the chunk). Expected: **zero, ever** (F1's claim).
    2. In the ~1 s fail-safe scan (`World.cs:1574–1592`), assert per entry of
       `worldData.SunlightRecalculationQueue` that the owning chunk has a work flag set; log an
       error otherwise (F6's claim: never fires).
- **Acceptance:** universal gate + an in-game soak (normal play: streaming, edits, a reload) with
  both probes silent. Record the result in this doc (Amended line) — LP-3 is **blocked** until
  probe 1 has a silent soak on record.
- **Testability gain:** turns two "should hold" conventions into observable invariants; probe 2
  is the first concrete member of NS-3's flag-pairing assertion family.
- **Doc-sync:** none (no behavior change). **Serialization:** none.

### LP-2 — Shared neighbor-gate predicate (🟡)

**Delivers:** §4.2. One predicate, three thin gates, harness drives the same code.

- **Scope:** new `Assets/Scripts/Helpers/NeighborReadinessDecision.cs` (runtime assembly — the
  editor harness references runtime helpers already, per `LightingScanDecision` precedent);
  `World.cs:1883–2028` (three gates → single-loop bodies; merge ReadyAndLit's two loops; delete
  the orphaned docstring at W:1864–1870); `LightingTestWorld.cs:379+` (both gate analogs route
  through the predicate; keep their grid-boundary skip documented as today).
- **Ordering:** independent; do before LP-4 (it shrinks LP-4's blast radius).
- **Trap (gate ordering, chunk-lifecycle skill):** this is a *re-housing*, not a redesign. The
  relaxed `AreNeighborsMeshReady` must stay relaxed (§9.3 wave-front deadlock); `enableLighting`
  gating of the `NeedsInitialLighting` check in MeshReady must be preserved; out-of-world
  neighbors stay "ready"; `IsChunkInWorld` and dictionary probes stay caller-side facts.
- **Prove-red:** temporarily invert the `lightingInFlight` term inside the predicate → expect
  scheduler-mode baselines (B66/B67/B70) and edge-check baselines to red; restore → green. This
  proves the suite actually flows through the shared code.
- **Acceptance / regression:** universal gate **+ the meshing suite** (`Validate Meshing`,
  B1–B21 — `AreNeighborsMeshReady` feeds `ScheduleMeshing`) **+ in-game smoke**: fly a sustained
  straight line (the wave-front pattern) and confirm no stuck-unmeshed swathes and zero recurring
  fail-safe promotions (`enableDiagnosticLogs`).
- **Testability gain:** fidelity **B2 remainder closes** — the readiness computation itself
  becomes shared, unit-testable code; a future gate bug is a suite red, not an in-game mystery.
- **Doc-sync (same commit):** `CHUNK_LIFECYCLE_PIPELINE.md` §3 (add the shared-predicate pointer
  per gate table), `LIGHTING_SYSTEM_OVERVIEW.md` §3.5 (one line), fidelity doc B2 entry (flip the
  remainder note). **Serialization:** none.

### LP-3 — Retire `IsAwaitingMainThreadProcess` (🟡, evidence-gated)

**Delivers:** one dead axis removed from the state machine, gates and the completion driver
simplified.

- **Precondition (hard):** LP-1 probe 1 recorded silent over a real soak. If it ever fired, STOP
  — the flag is load-bearing somewhere this analysis missed; file the finding and re-plan.
- **Scope:** delete the field (`ChunkData.cs:163–167`) + `Reset()` line; delete set/clear
  (`WJM:1169`, the `ReleaseJob` clear at `WJM:1135` — the container release stays); remove the
  gate terms (`World.cs:1920/1943`, `UnloadChunks` W:2373) and the `NeighborFacts` member
  (LP-2 landed first); harness: `LightingTestWorld` set/clear sites + gate analog + B34's
  transient-surface list (the reflection backstop adapts automatically — field gone).
- **Why safe:** the whole flight window is guarded by `LightingJobs.ContainsKey` in the same
  gates; merge atomicity is main-thread-guaranteed; the per-job `finally` pairing (HF-2) becomes
  vacuous for this flag while container release keeps its own `finally`.
- **Prove-red:** n/a (a deletion has no red to prove). Regression carries the weight:
  universal gate, **B65 specifically** (fault-isolation semantics of `ReleaseJob` change shape),
  plus an in-game streaming soak with unload/reload cycles (UnloadChunks touched) watching for
  stuck chunks and fail-safe promotion counts.
- **Testability gain:** the state machine loses an axis no test could ever exercise (zero
  observable window ⇒ untestable by construction); the §2.3 census shrinks.
- **Doc-sync (same commit):** `CHUNK_LIFECYCLE_PIPELINE.md` §2 (row delete) + §3 gate tables +
  §9.6 code excerpt, `LIGHTING_SYSTEM_OVERVIEW.md` (§3.4 mentions), fidelity doc (B4/B7 entries
  mention the flag), `.agents/rules/chunk-pipeline.md` + `pool-reset-safety.md` flag lists, and
  the `chunk-lifecycle` skill's flag enumeration. **Serialization:** none (`[NonSerialized]`).

### LP-4 — `LightingWork` byte + transition API (🔴, the headline)

**Delivers:** §4.1 in full. Every §2.3 census row becomes a named method; partial transitions
become unrepresentable; transitions become directly baselinable.

- **Scope:** `ChunkData.cs` (bits + funnel + methods; three bool properties kept as thin
  adapters *during* the migration); call-site migration —
  `WorldJobManager.cs` (`:563→FlagLightWork`, `:624–631→` read `NeedsEdgeCheck` then
  `OnLightingJobScheduled()` after `job.Schedule()` succeeds (preserve the current
  clear-after-schedule ordering — on a schedule throw the flags stay set, as today),
  `:817→FlagInitialLighting`, `:1126/1298→FlagLightWork`,
  `:1283–1293→ArmEdgeCheckRoundIfAvailable` (+ keep `LastEdgeRecycleJobCount`),
  `:1640–1641→FlagEdgeCheck+FlagLightWork` — or a dedicated `FlagNeighborEdgeCheck()` setting
  both, executor's call);
  `World.cs` (`:847/805-equivalent→FlagLightWork`, `:905/1047/1652→ClearInitialLighting`,
  `:919→FlagEdgeCheck`, `:1119–1121→ClearAllLightingWork`, `:1647→FlagLightWork` (edge-arm
  pre-set — name the intent in a comment));
  `ChunkData.ModifyVoxel:554→RegrantBorderEditEdgeRound`;
  `ChunkSerializer.cs:123/206` (bit↔bool mapping), `:246→FlagLightWork`;
  `ChunkStorageManager.cs:217` (snapshot reads the bit);
  harness (`LightingTestWorld`/`TestChunk` route their real-`ChunkData` writes through the same
  methods). `Migration_v2_to_v3` is untouched (it writes stream bytes, not `ChunkData`).
- **Callback-delta check (the one behavioral micro-delta, §4.1):** combined transitions fire
  `OnLightWorkFlagged` once where two property writes fired twice. Verify equivalence explicitly:
  the LightScheduler suite green + a scheduler-mode suite run + reasoning note in the commit
  (staging dedupes at drain).
- **Editor-only transition assertions** (HF-1 pattern, zero IL2CPP cost): e.g.
  `OnLightingJobScheduled` asserts a job was actually registered by the caller;
  `ArmEdgeCheckRoundIfAvailable` asserts main-thread. Keep light — assertions document the
  contract, they don't re-implement the scheduler.
- **New baselines (B71+, same commit):** a transition-census baseline family in the lighting
  suite (oracle-free, the B34/B47 style): for each transition method assert before-bits →
  after-bits, rounds-counter effect, and callback fire-count (installable sink — the harness
  already owns `OnLightWorkFlagged` save/restore). This is the NS-3 flag-pairing family's second
  concrete member.
- **Prove-red:** sabotage `ArmEdgeCheckRoundIfAvailable` to skip `C:=1` (arm E without C) → the
  edge-round-dependent baselines (B8 initial-wave family / B70 border-fuzz reconcile) must red;
  restore → green. Also run the B34 reflection backstop unmodified — it must still pass with the
  byte field (it walks `[NonSerialized]` primitives; an enum-typed byte qualifies — verify, and
  extend the backstop if enum fields are skipped).
- **Acceptance / regression:** universal gate + full in-game session (streaming + edits +
  border edits + reload — the Bug-05 re-grant path and the serializer path both need live
  confirmation).
- **Testability gain:** transitions unit-baselinable; illegal partial transitions
  unrepresentable; the frame simulator and production share the *mutation* layer on top of the
  already-shared decision/completion layers — the full scheduling stack is now one code path.
- **Doc-sync (same commit):** `CHUNK_LIFECYCLE_PIPELINE.md` §2 (rewrite the flag table around
  bits + transition methods; note F9's `IsLoading` status honestly), §4 pseudocode names the
  transition methods; `LIGHTING_SYSTEM_OVERVIEW.md` §3.2/§3.4 mentions;
  `pool-reset-safety.md` "property setter subtlety" section (funnel replaces per-property
  setters); `chunk-lifecycle` skill flag list; fidelity doc B4 note.
  **Serialization:** mapping-only; layout unchanged (§5 tripwire applies).

### LP-5 — Explicit scheduling contract + startup-coroutine unification (🟡)

**Delivers:** F2 + F4 closed — the silent `NeedsEdgeCheck` read/clear becomes an explicit,
baselined contract, and the startup coroutine stops hand-mirroring the scan arms.

- **Scope:**
    1. `WorldJobManager.ScheduleLightingUpdate`: the job's `PerformEdgeCheck` is populated from an
       explicit read (`chunkData.NeedsEdgeCheck` — unchanged) but the clear moves into
       `OnLightingJobScheduled()` (done in LP-4); ADD an XML-doc'd statement of the weak-gate
       fallback contract on the method (edge work rides ANY successful schedule) — the §7 pipeline
       behavior, now visible at the signature.
    2. `World.cs:1036–1075` (coroutine Steps 2a/2b): replace the hand-mirrored arms with
       `LightingScanDecision.EvaluateReadyChunk` + the same switch the Update scan runs, preserving
       the coroutine's specifics: `Allocator.TempJob`, sweep-until-quiescent structure,
       `CompleteAndProcessLightingJobs()` between sweeps, safety-break diagnostics. The arm
       *decision* becomes shared; the sweep *driver* stays coroutine-specific.
- **New baseline (B7x, same commit — closes F4's coverage gap):** the §7 weak-gate fallback has
  NO dedicated baseline today. Scheduler-mode scenario: chunk with `E=1, C=1`, neighbors
  data-ready but NOT lit (in-flight neighbor) → assert the regular arm schedules with
  `PerformEdgeCheck = true` and both flags clear. Prove-red: neuter the fallback (make the
  regular-arm schedule drop `E` without passing it to the job) → new baseline reds; restore.
- **Acceptance / regression:** universal gate + **world-load in-game checks** (the coroutine is
  the startup path): load an existing world AND create a new one; confirm the
  "exceeded max iterations" safety-break never fires and load-time lighting converges as before.
- **Testability gain:** the startup path's arm selection is now the same shared, sim-guarded
  decision as the steady-state scan — a whole hand-mirrored surface deleted.
- **Doc-sync (same commit):** `CHUNK_LIFECYCLE_PIPELINE.md` §4 "Critical Scheduling Detail" + §7
  fallback section (rewrite as explicit contract + baseline pointer);
  `LIGHTING_SYSTEM_OVERVIEW.md` §3.6 step 3. **Serialization:** none.

### LP-6 — Lazy strict-gate evaluation (🟢, optional, SECONDARY perf)

**Delivers:** F7 — the scan computes `AreNeighborsReadyAndLit` only when the edge arm needs it.

- **Scope:** add an `EvaluateReadyChunk` overload taking a small gate-provider interface
  (`INeighborGates { bool DataReady(); bool ReadyAndLit(); }`) implemented by a **cached** adapter
  on `World` and on the sim (zero alloc, no per-call delegates); the laziness lives inside the
  shared function so both callers stay identical. Delete the old always-eager call pattern at
  `World.cs:1630–1631` and the sim's mirror in the same commit (both callers move atomically —
  the shared-code invariant).
- **Gate:** universal gate + a before/after measurement of the `WorldFrameProfiler` Light phase
  under a streaming load. **Ship only on a measured win** (perf-benchmark discipline); otherwise
  record NO-GO here and close the phase — the clarity value alone does not justify signature
  churn.
- **Doc-sync:** pipeline §4 pseudocode note. **Serialization:** none.

### LP-7 — Naming & doc hygiene (🟢)

- **Scope:** `RecalculateSunLightLight()` → `RecalculateSunlight()` via the `refactor-safely`
  skill (callers: `World.cs:1645`, `:899`, `:1044`, harness if referenced); verify no serialized
  name is touched (method — safe). Residual doc alignment (anything §2 of the pipeline doc still
  footnotes that LP-3/LP-4 made false). Explicitly does NOT start the Sun→Sky rename (Phase B).
- **Gate:** universal gate. **Doc-sync:** pipeline/lighting docs mention the method by name in
  pseudocode — update in the same commit. **Serialization:** none.

---

## 8. Open questions

1. **LP-1 probe results** — does `IsAwaitingMainThreadProcess` ever read true at a gate in a real
   soak? Resolves LP-3's go/no-go; the answer lands here as an Amended line + a checkbox in LP-3.
2. **Keep or remove the three bool adapter properties after LP-4?** Decide by call-site count at
   execution time: if ≤ a handful of readers remain (gates read via LP-2 facts, scan reads via
   the decision inputs), remove them and read bits directly; otherwise keep the adapters
   permanently as the read API. Either way, *writes* go through transition methods only.
3. **LP-6 worth it?** Only a measurement answers it; the phase carries its own GO/NO-GO gate and
   a NO-GO is a valid close-out.

---

## Document History

* **v1.0** - Initial design (analysis + LP-1…LP-7 phased plan; flag/gate census at `4cb80e4`)

---

**Last Updated:** 2026-07-06
**Next Review:** when LP-1 starts (re-verify §2 line anchors against HEAD before editing)
