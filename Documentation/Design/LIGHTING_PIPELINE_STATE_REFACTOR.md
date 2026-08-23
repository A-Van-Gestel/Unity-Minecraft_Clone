# Lighting Pipeline State & Gate Refactor (LP-*)

**Version:** 1.3  
**Date:** 2026-07-06  
**Status:** Partially implemented — **LP-1 and LP-2 shipped 2026-08-23** (probes live and soak silent; the shared gate predicate is in with NS-3 baseline B7 — see §7). LP-3…LP-7 remain proposed. §2 re-audited against HEAD on 2026-08-23.  
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

**Audited:** 2026-08-23, at commit `6b899481` (branch `feat/world-scaling`) — a **full re-audit** superseding the
original 2026-07-06 pass at `4cb80e4`, which HEAD had left 631 commits behind. Findings are from static review of
`Data/ChunkData.cs` (flag cluster L112–177, `Reset` L253–288, `ModifyVoxel` L555–590, loaded-data adoption L440–460,
BFS enqueues L1348–1373, `RecalculateSunLightLight` L1431–1445), `World.cs` (Update scan arm L2495–2570, startup
coroutine L1388–1486, gates L2850–3010, `UnloadChunks` L3419–3570, `LoadOrGenerateChunkInner` L1185–1290, CP-1 probe
block L375–415, ~1 s fail-safe scan L2385–2410, load-arm fault path L1015–1032), `WorldJobManager.cs`
(`ScheduleLightingUpdate` L781–935, completion driver L1592–1650, `MergeCompletedLightingJob` L1655–1830,
`TriggerNeighborEdgeChecks` L2186–2213, generation completion L1200–1245),
`Helpers/LightingScanDecision.cs`, `Helpers/LightingScheduleDecision.cs`,
`Helpers/EdgeCheckCascadeDecision.cs`, `Helpers/JobCompletionPass.cs`, `Helpers/LightWorkScheduler.cs`,
`Data/WorldData.cs` (`QueueSunlightRecalculation` L455–473), `Serialization/ChunkSerializer.cs` (L134–275),
`Serialization/ChunkStorageManager.cs` (L810–815), and the editor harness (`LightingTestWorld.cs` gate analogs
L420–480, `ChunkPipelineSimulator.cs`, `LightingFrameSimulator.cs` structure). Line numbers are anchors for the
executor, not contracts — re-verify before editing.

**What the re-audit changed** (the v1.0 → v1.1 delta; §3 decision and §4 target architecture were re-checked and
still stand):

- **F9 is now FALSE and has been struck.** CP-3 gave `IsLoading` two clear sites outside `Reset()`.
- **F1 gained a fourth reader** (`HasPendingEdgeChecks`) and **F6's cited enqueue sites were all wrong** — there
  are now three enqueue paths, two of which bypass the `WorldData` API entirely.
- **Census row 10 split in three.** P9-2's `EdgeCheckCascadeDecision` replaced the unconditional stable re-arm with
  a `None` / `SpendOnly` / `SpendAndRearm` outcome, which **changes LP-4's transition-API shape** (§4.1).
- **Three new census rows** (P-4 rec 3 unload-persist re-light, loaded-data adoption, CP-3 load-arm fault).
- **`NeedsEdgeCheck` gained a second reader inside `ScheduleLightingUpdate`** (LI-2's band derivation), which
  widens F4.
- Every line anchor, and every baseline count, was re-derived.

**Relationship to other documents:**

- [`../Architecture/CHUNK_LIFECYCLE_PIPELINE.md`](../Architecture/CHUNK_LIFECYCLE_PIPELINE.md) — the authoritative flag/gate reference (§2/§3) this plan restructures; every phase doc-syncs it.
- [`../Architecture/LIGHTING_SYSTEM_OVERVIEW.md`](../Architecture/LIGHTING_SYSTEM_OVERVIEW.md) — the async BFS model; §3.2/§3.5/§3.6 describe the scheduling/gate behavior LP-2/LP-5 touch.
- [`LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md`](LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md) — AS-2 + HF-4 delivered the shared scan arm (`LightingScanDecision`), schedule guard (`LightingScheduleDecision`), and completion pass (`JobCompletionPass`). **This plan builds ON those extractions and must not redo them**; it extends the same shared-guard pattern to the two surfaces HF-4 did not reach (neighbor gates, flag transitions). AS-3/AS-4/AS-5 are orthogonal (scenario/fuzz work, not structure) and keep their own IDs.
- [`../Architecture/Testing Framework/LIGHTING_VALIDATION_HARNESS_FIDELITY.md`](../Architecture/Testing%20Framework/LIGHTING_VALIDATION_HARNESS_FIDELITY.md)
  — LP-2 closes the B2 remainder (readiness *computation* out of harness scope); LP-4 upgrades the B4 surface (flag transitions become shared named methods).
- [`../Architecture/Testing Framework/LIGHTING_FRAME_SIMULATOR_DESIGN.md`](../Architecture/Testing%20Framework/LIGHTING_FRAME_SIMULATOR_DESIGN.md)
  — the simulator both modes of which are the regression instrument for every phase here.
- [`VALIDATION_SUITE_COVERAGE_ROADMAP.md`](VALIDATION_SUITE_COVERAGE_ROADMAP.md) — NS-3 (chunk lifecycle state-machine suite) names the flag-pairing assertion family; LP-1's invariant probes and LP-4's transition API are deliberate groundwork for NS-3.
- [`MESHING_PIPELINE_ORCHESTRATION_REFACTOR.md`](MESHING_PIPELINE_ORCHESTRATION_REFACTOR.md) — the MP-* meshing sibling of this plan (same patterns: probes, pure-decision extraction, shared completion skeleton). Coordination points: **MP-4 SHIPPED first (2026-07-25)** — the skeleton is already renamed `LightingCompletionPass` → `Helpers/JobCompletionPass.cs` and `ILightingCompletionDriver<TKey>` →
  `IJobCompletionDriver<TKey>`, and now carries optional `window`/`startIndex` parameters, so **LP-3 edits the lighting driver's `ReleaseJob` under the new names** (no rebase owed; the suites arbitrate). A later MP-5 code-review round also added a
  `_curLightJob = default;` line at the end of that same `ReleaseJob` (symmetry with the mesh driver — the per-job scratch must not outlive its release); it is unrelated to LP-3's
  `IsAwaitingMainThreadProcess` clear and **stays** when that clear is deleted. MP-2 can consume LP-2's `NeighborReadinessDecision` facts if LP-2 lands first, but has no hard dependency on it.

---

## 1. Goals & non-goals

### Goals

1. **Make the implicit per-chunk lighting state machine explicit and auditable** — every flag transition a named method with a documented trigger, instead of ~20 scattered raw writes across 4 files (§2.4 census).
2. **Close the remaining production/harness drift surfaces** — the neighbor gates and the startup coroutine still hand-mirror logic the harness cannot drive (the pattern HF-4 fixed for the scan arm and completion pass).
3. **Make illegal *partial transitions* unrepresentable** — atomic schedule-clear, atomic edge re-arm (round decrement + both flags together), so the "flag set whose clear site is unreachable" bug class (three historical deadlocks) loses its raw material.
4. **Preserve behavior byte-for-byte at every phase boundary** — 62 lighting baselines + scheduler mode + LightScheduler suite green, no on-disk change, MT-2 promotion contract intact.
5. *(SECONDARY)* Trim redundant per-frame gate work in the ready-set scan (LP-6, optional, measured before shipped).

### Non-goals (v1)

- **Sun→Sky naming unification** (`SunlightBfsQueue`, `AddToSunLightQueue`,
  `SunlightRecalculationQueue`, …) — owned by the existing **Phase B legacy-light-removal plan**
  (see `project_phase_b_legacy_light_removal` / DATA_STRUCTURES notes). LP-7 fixes only the doubled-word typo `RecalculateSunLightLight`.
- **Re-extracting the scan arm, schedule guard, or completion pass** — done (HF-4 #1/#2, AS-2).
- **Changing MT-2 scheduling semantics** (ready/waiting split, promotion events, `PromoteAll`
  fail-safe) — the split is intentional, guarded by its own suite, and out of scope. LP-4 only funnels the *callback firing* through one site with identical observable semantics.
- **Relaxing or tightening any readiness gate** — `AreNeighborsMeshReady` stays deliberately relaxed (the §9.3 wave-front deadlock fix); `AreNeighborsReadyAndLit` stays the edge arm's gate. LP-2 is a pure re-housing of the existing predicates.
- **Persisting the new work byte** — the serialized surface stays exactly one bool (`NeedsInitialLighting`). If a future feature ever persists more, that is a
  `serialization-migration` item outside this plan.
- **Lighting→meshing handoff coverage** (fidelity B5) — unchanged boundary.

---

## 2. Current state — the flag & gate surface

### 2.1 Per-chunk state inventory

All mutation is main-thread-only (chunk-pipeline rule); jobs read snapshots. "Callback" = setter fires `ChunkData.OnLightWorkFlagged` → `LightWorkScheduler.Flag` on a false→true transition.

| State                         | Storage                                    | Serialized?                                                                                                          | Callback | Set by (sites)                                                                                                                                                                                                                                                                                                                                             | Cleared by                                                                                                              |
|-------------------------------|--------------------------------------------|----------------------------------------------------------------------------------------------------------------------|:--------:|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------|
| `IsPopulated`                 | plain bool, `[NonSerialized]`              | no (implied true by a chunk record existing)                                                                         |    no    | generation populate (`ChunkData.cs:346`); loaded-data adoption (`ChunkData.cs:460`); disk read (`ChunkSerializer.cs:269`)                                                                                                                                                                                                                                                          | `Reset()` (`ChunkData.cs:258`)                                                                                          |
| `IsLoading`                   | plain bool, `[NonSerialized]`              | no                                                                                                                   |    no    | generation-request admission (`World.cs:3778`)                                                                                                                                                                                                                                                                                                             | **CP-3 load-arm fault** (`World.cs:1029`, guarded by `LifecycleEpoch` — see F9); stale-data recovery (`WJM:1088`); `Reset()` (`ChunkData.cs:259`) |
| `NeedsInitialLighting`        | property + backing bool, `[NonSerialized]` | **YES — the only persisted flag** (`ChunkSerializer.cs:142` write, `:226` read; `Migration_v2_to_v3_RestoreLighting.cs:108` forces it true) |   yes    | generation completes (`WJM:1240`); disk read (`ChunkSerializer.cs:226`); loaded-data adoption (`ChunkData.cs:450`); **P-4 rec 3 unload-persist re-light** (`World.cs:3562`)                                                                                                                                                                                 | scan initial arm (`World.cs:2540`); `LoadOrGenerateChunkInner` (`World.cs:1268`); coroutine (`World.cs:1410`, disabled-arm `:1482`); `Reset()` (`ChunkData.cs:263`) |
| `HasLightChangesToProcess`    | property + backing bool, `[NonSerialized]` | no — **re-derived on load** from non-empty persisted BFS queues (`ChunkSerializer.cs:266`)                           |   yes    | `AddToSunLightQueue`/`AddToBlockLightQueue` (`ChunkData.cs:1357`/`:1371`, **both `enableLighting`-gated**); `QueueSunlightRecalculation` (`WorldData.cs:471`); schedule-declined (`WJM:794`); edge arm pre-set (`World.cs:2535`, coroutine `:1426`); stable re-arm (`WJM:1816`); unstable (`WJM:1823`); merge fault (`WJM:1620`); neighbor edge trigger (`WJM:2210`); pending-column restore (`WJM:1228`, `World.cs:1210`); loaded-data adoption (`ChunkData.cs:449`) | `ScheduleLightingUpdate` success (`WJM:922`); disabled-lighting clears (`World.cs:1483`); `Reset()` (`ChunkData.cs:264`) |
| `NeedsEdgeCheck`              | property + backing bool, `[NonSerialized]` | no — re-derived: disk-loaded stable chunks get it set (`World.cs:1282`)                                              |   yes    | stable re-arm, `SpendAndRearm` only (`WJM:1815`); `TriggerNeighborEdgeChecks` (`WJM:2209`); disk-load-stable (`World.cs:1282`)                                                                                                                                                                                                                              | `ScheduleLightingUpdate` success (`WJM:923`) — **two readers first**: `PerformEdgeCheck` (`WJM:916`) and LI-2's `DeriveBandHeight` (`WJM:868`); disabled clears (`World.cs:1484`); `Reset()` (`ChunkData.cs:266`) |
| `IsAwaitingMainThreadProcess` | plain public bool, `[NonSerialized]`       | no                                                                                                                   |    no    | merge start (`MergeCompletedLightingJob`, `WJM:1668`)                                                                                                                                                                                                                                                                                                      | completion driver `ReleaseJob` finally (`WJM:1629`) — **same `ProcessLightingJobs` pass** (F1); `Reset()` (`ChunkData.cs:265`) |
| `RemainingEdgeCheckRounds`    | plain int, `[NonSerialized]`, default 2    | no                                                                                                                   |    no    | re-grant to ≥1 on border-column opacity **or sky-obstruction** edit (`ChunkData.cs:568–581`, Bug 05 + _FIXED_BUGS Lighting #25)                                                                                                                                                                                                                             | decrement on any non-`None` cascade outcome (`WJM:1806`); `Reset()` → 2 (`ChunkData.cs:267`)                            |

**Off-chunk state that co-encodes the machine** (an on-chunk representation can never be authoritative for these): `JobManager.GenerationJobs` / `LightingJobs` / `MeshJobs` membership (in-flight axes), `LightWorkScheduler` ready/waiting/staging membership,
`worldData.SunlightRecalculationQueue` (per-chunk pending column sets — a fourth work store, F6; **drained inside
`ScheduleLightingUpdate` at `WJM:842–849`, before the flag clears at `:922`**), the managed BFS queues on
`ChunkData`, `MeshBuildQueue` membership, `Chunk.IsActive`, and `LightingStateManager`'s persisted pending-column /
pending-blocklight stores (the disk-side mirror the restore paths in row 3 drain from).

### 2.2 The gates

| Gate                               | Checks per neighbor (8 horizontal)                                                                                   | Used by                                                                               | Notes                                                                                                                            |
|------------------------------------|----------------------------------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------|
| `AreNeighborsDataReady` (W:2988)   | in-world skip; no gen job; exists + `IsPopulated`                                                                    | initial + regular scan arms; `ScheduleLightingUpdate` guard (`WJM:786`); `LoadOrGenerateChunkInner`; coroutine `HasPendingInitialLighting` / `HasPendingLightChangesOnMainThread` | one `AllNeighborOffsets` loop                                                                                                    |
| `AreNeighborsReadyAndLit` (W:2850) | DataReady + no lighting job + `!HasLightChangesToProcess` + `!NeedsInitialLighting` + `!IsAwaitingMainThreadProcess` | edge arm (`World.cs:2519`); coroutine edge arm (`:1424`); **coroutine `HasPendingEdgeChecks` (`:1688`)**; harness analog `LightingTestWorld.cs:461` hand-mirrors it | **two duplicated loops** (cardinals `W:2853–2892`, then diagonals `W:2897–2913`, identical predicate — F3); an orphaned docstring sits above it (W:2800–2806) |
| `AreNeighborsMeshReady` (W:2933)   | in-world skip; no gen job; exists + `IsPopulated`; `!NeedsInitialLighting` (skipped when lighting disabled)          | `ScheduleMeshing` (via `World.cs:2969` `IMeshDrainHost.TrySchedule`)                  | deliberately relaxed — the §9.3 wave-front fix; must stay relaxed                                                                |

### 2.3 The implicit state machine the code actually relies on

Three semi-independent axes, not one chain:

- **Data axis (exclusive, monotonic per lifecycle):**
  `Placeholder → (Loading | Generating) → Populated`, encoded by `IsLoading` + `IsPopulated` +
  `GenerationJobs` membership. Reset by pool recycle.
- **Lighting work axis (a SET, not a chain):** the bits `I` (`NeedsInitialLighting`),
  `C` (`HasLightChangesToProcess`), `E` (`NeedsEdgeCheck`), plus job-in-flight `J`
  (`LightingJobs` membership), rounds counter `R ∈ {0,1,2}`, and scheduler membership (ready / waiting / absent).
- **Merge-transient axis:** `IsAwaitingMainThreadProcess` (`A`) — see F1: its true-window is confined to one main-thread call stack.

**Legal bit combinations observed in code** (all 8 are reachable; an exclusive enum would need the full power set):

| `I C E` | How it arises                                                                                                        |
|:-------:|----------------------------------------------------------------------------------------------------------------------|
| `0 0 0` | idle / just-scheduled (all clears at `ScheduleLightingUpdate`, `WJM:922–923`)                                        |
| `1 0 0` | generation completed (`WJM:1240`); disk load with persisted `I=1` and empty queues; P-4 rec 3 persist-arm (`W:3562`) |
| `1 1 0` | disk load with `I=1` **and** non-empty persisted queues (`ChunkSerializer:226` + `:266`); pending-column restore     |
| `0 1 0` | edits / cross-chunk mods / unstable completion / schedule-declined                                                   |
| `0 0 1` | disk-load-stable (`World.cs:1282`) or `TriggerNeighborEdgeChecks` on a quiet neighbor — waits on the strict edge gate |
| `0 1 1` | stable re-arm, `SpendAndRearm` only (`WJM:1815–1816`); neighbor edge trigger onto a dirty chunk; §7 weak-gate state  |
| `1 0 1` | disk-load-stable chunk whose neighbor then re-arms it… then a mod arrives → `1 1 1`; rare but reachable              |
| `1 1 1` | union of the above — legal, drains in priority order I → E → C                                                       |

**Transition census** (the ground truth LP-4's API must reproduce; arrows are bit effects):

| #   | Trigger (site)                                                                          | Effect                                                                                                       |
|-----|-----------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------|
| 1   | Generation completes (`WJM:1240`)                                                       | `I:=1`                                                                                                       |
| 2   | Disk read: persisted flag / non-empty queues (`ChunkSerializer:226/266`)                | `I:=persisted`, queues>0 → `C:=1` *(background thread — the callback's thread-safe staging path)*            |
| 3   | Disk-load-stable (`World:1282`) / pending columns (`World:1210`) / recovery (`WJM:1228`) | `E:=1` / `C:=1` / `C:=1`  *(the two restore paths write `SunlightRecalculationQueue` **directly**, bypassing `WorldData.QueueSunlightRecalculation` — F6)* |
| 3b  | **Loaded-data adoption** (`ChunkData:449–450`)                                           | `C \|= loaded.C`, `I \|= loaded.I` — the temp deserialization instance's flags are OR-ed into the live chunk  |
| 4   | Voxel edit / cross-chunk apply / wake-up (`AddTo*Queue`, `ChunkData:1357/1371`)          | `C:=1` — **both enqueues are `enableLighting`-gated** (no flag when lighting is off)                          |
| 4b  | Column recalc queued (`WorldData.QueueSunlightRecalculation:471`)                        | `C:=1` on the routed owner chunk, **if resident**; drives `RecalculateSunLightLight`'s 256-column fill        |
| 5   | Border-column opacity **or sky-obstruction** edit (`ChunkData:568–581`, Bug 05)          | `R := max(R, 1)`                                                                                             |
| 6   | Scan **initial** arm schedules (`World:2533–2541`)                                       | recalc fills queues (`C:=1` via #4b), schedule → `C:=0, E:=0(if set)`, then `I:=0`; `J:=1`                   |
| 7   | Scan **edge** arm schedules (`World:2535`)                                               | `C:=1` (pre-set so the schedule guard passes), schedule reads `E→PerformEdgeCheck`, → `C:=0, E:=0`; `J:=1`   |
| 8   | Scan **regular** arm schedules                                                           | schedule → `C:=0`, **and `E:=0` if set — the §7 weak-gate fallback (F4)**; `J:=1`                            |
| 9   | Schedule declined `NeighborsNotReady` (`WJM:794`)                                        | `C:=1` (re-asserted), caller parks                                                                           |
| 10a | Merge, stable, cascade `SpendOnly` (`WJM:1806`, P9-2)                                    | `R--` **only** — no re-arm, no neighbor trigger (the pass moved no light)                                    |
| 10b | Merge, stable, cascade `SpendAndRearm` (`WJM:1806–1818`)                                 | `R--`, `E:=1, C:=1` on self; `E:=1, C:=1` on the 4 cardinals that are populated **and** `!I` (`WJM:2207–2210`) |
| 10c | Merge, stable, cascade `None` (`R<=0`)                                                   | nothing — the budget refusal                                                                                 |
| 11  | Merge, unstable (`WJM:1823`) / merge fault (`WJM:1620`)                                  | `C:=1`                                                                                                       |
| 12  | Merge bracket (`WJM:1668` / `:1629`)                                                     | `A:=1` … `A:=0` in the same pass (F1)                                                                        |
| 13  | Lighting disabled (`World:1482–1484`, §6 of the lighting overview)                       | `I,C,E := 0`                                                                                                 |
| 14  | Pool recycle (`Reset()`, `ChunkData:253`)                                                | everything := defaults, `R:=2`, `LifecycleEpoch++`                                                           |
| 15  | Startup coroutine sweeps (`World:1398–1440`)                                             | hand-mirrored copies of #6/#7/#8 with `Allocator.TempJob` (F2)                                               |
| 16  | **P-4 rec 3 unload-persist arm** (`World:3562`)                                          | `I:=1` immediately before the save snapshot — a persist-arm chunk must re-light fully on reload              |
| 17  | **CP-3 load-arm fault** (`World:1029`) / stale-data recovery (`WJM:1088`)                | `IsLoading:=0` — guarded by a `LifecycleEpoch` compare so a late fault cannot clear a successor's flag        |

**The cascade split (10a/10b/10c) is new since v1.0 and is the one census change with design
consequences:** P9-2 replaced the unconditional "stable ⇒ spend a round and re-arm" rule with the shared
`EdgeCheckCascadeDecision.Evaluate(convergentCascadeEnabled, remainingRounds, lightChanged, hasPendingLightWork)`,
which returns `None` / `SpendOnly` / `SpendAndRearm`. **LP-4's `ArmEdgeCheckRoundIfAvailable()` as sketched in §4.1
no longer matches production** — see the note there.

Scheduler-membership transitions ride these: any bit 0→1 fires the callback → staging → ready; park on gate-fail / in-flight / unpopulated; promote on completion (`WJM:1149`), generation/load completion, own re-flag, or the ~1 s `PromoteAll` fail-safe.

### 2.4 Findings (the clean-up backlog this plan executes)

| #   | Finding                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                              |          Addressed by           |
|-----|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:-------------------------------:|
| F1  | **`IsAwaitingMainThreadProcess` has a ~zero observable window.** It is set at merge start and cleared in the same `ProcessLightingJobs` pass's per-job `finally` (`WJM:1668`/`1629`). All **four** readers — `AreNeighborsReadyAndLit` (W:2887 cardinal, W:2910 diagonal), `UnloadChunks` (W:3465), and the coroutine's `HasPendingEdgeChecks` (W:1688, via the gate) — plus the harness analog run in a *different* step of the frame, after the pass completed, so none can observe `true`. Re-verified at HEAD: `codegraph callees MergeCompletedLightingJob` reaches no gate reader, so no reader runs inside the merge stack. The in-flight window it was presumably meant to guard is already covered by `LightingJobs.ContainsKey`, which the same gates check. Candidate for deletion — after instrumentation proof, not reasoning alone. |           LP-1 → LP-3           |
| F2  | **The startup coroutine hand-mirrors the scan arms** (`World.cs:1398–1440`): initial/edge/regular arm bodies duplicated inline, NOT routed through `LightingScanDecision` (HF-4 reached only the `Update` scan). A drift surface: the startup path can silently disagree with the steady-state scan the harness guards. Its three `HasPending*` loop conditions (`World.cs:1651–1700`) hand-mirror the gates a second time.                                                                                                                                                                          |              LP-5               |
| F3  | **Gate duplication ×3.** `AreNeighborsReadyAndLit` runs two identical loops (cardinals W:2853–2892, then diagonals W:2897–2913); the three production gates are three hand-rolled loops over the same neighbor facts; the harness hand-mirrors two of them (`LightingTestWorld.cs:426`/`:461`) — the fidelity-B2 remainder ("a bug in the readiness computation itself is out of scope"). Plus an orphaned stray docstring above `PromoteLightWorkNeighborhood` (W:2800–2806).                                                                                                                       |              LP-2               |
| F4  | **`ScheduleLightingUpdate` silently reads + clears `NeedsEdgeCheck`** (`WJM:916`/`923`). This makes the §7 weak-gate fallback (edge check running under `AreNeighborsDataReady`) an *implicit* side effect of the regular arm — documented in the pipeline doc but invisible in any signature, and covered by **no dedicated baseline** today. **Widened since v1.0:** LI-2 added a *second*, equally silent reader in the same method — `LightingBandDecision.DeriveBandHeight` takes `chunkData.NeedsEdgeCheck` (`WJM:868`) to force a full-height band. The flag now steers two behaviors, and LP-5's contract statement must name both. |              LP-5               |
| F5  | **`HasLightChangesToProcess` triple duty**: "managed queues have nodes", "reschedule me" (unstable/fault), and "satisfy the schedule guard" (edge-arm pre-set W:1647). The bit is fine; the *intent* is invisible at call sites.                                                                                                                                                                                                                                                                                                                                                                     |              LP-4               |
| F6  | **`SunlightRecalculationQueue` is a fourth work store guarded by convention only.** Every current enqueuer also sets `C`, but nothing enforces "queued column ⇒ chunk flagged", and the fail-safe scan (`World.cs:2385–2396`) checks only the three flags — an unflagged entry would sleep until unload persists it. **Re-audited: the surface is now three paths, not one.** `WorldData.QueueSunlightRecalculation` (`:455`) sets `C` *itself* at `:471` — and only if the owner chunk is resident. The other two, both bulk restores of persisted columns, **write the dictionary directly and set `C` by hand adjacent**: `World.cs:1201–1210` (disk load) and `WJM:1218–1228` (generation-completion restore, additionally `enableLighting`-gated). Two hand-maintained pairings are exactly the raw material F6 describes. | LP-1 (probe), LP-4 (structural) |
| F7  | **Eager double-gate evaluation in the scan** (`World:2518–2519`): both `AreNeighborsDataReady` AND `AreNeighborsReadyAndLit` are computed for every ready chunk each visit (each 8 dictionary lookups + job-dict probes), though each arm needs only one. Small (O(ready) per frame, post-MT-2), but free to fix once gates are consolidated.                                                                                                                                                                                                                                                        |         LP-6 (optional)         |
| F8  | **Naming:** `RecalculateSunLightLight()` (doubled word, `ChunkData.cs:1434`). The wider Sun/Sky split is Phase B's — out of scope here.                                                                                                                                                                                                                                                                                                                                                                                                                                                              |              LP-7               |
| ~~F9~~ | ~~**`IsLoading` is never cleared** outside `Reset()`.~~ **STRUCK 2026-08-23 — no longer true.** CP-3 gave it two clear sites: the load-arm fault path (`World.cs:1029`, guarded by a `LifecycleEpoch` compare so a late fault cannot clear a *successor* load's flag) and stale-data recovery (`WJM:1088`). CP-1 also shipped a dev-only stuck-`IsLoading` detector (`World.cs:1039–1071`). Nothing remains for LP-4 to document here.                                                                                                                                                              |        — (closed by CP-3)       |
| F10 | **Initial arm does work before the schedule can decline** (`World:2533–2537`): `RecalculateSunLightLight()` runs before `ScheduleLightingUpdate`; on a decline the queue-fill repeats next visit. Benign (idempotent), noted for the LP-5 executor; not worth its own change. Note the fill is now 256 `QueueSunlightRecalculation` calls, so the repeat also re-touches the F6 store.                                                                                                                                                                                                                |            — (noted)            |
| F11 | **New (2026-08-23): the P9-2 cascade decision is a fourth shared guard LP-4 must compose with.** `EdgeCheckCascadeDecision` already owns the stable-merge branch that census rows 10a–10c describe, and it is *pure* — the caller applies the effects (`WJM:1800–1818`). LP-4's transition API takes over exactly those effect applications, so the two must be designed together or the decision's three outcomes will be flattened back into two.                                                                                                                                                  |              LP-4               |

---

## 3. Decision: how to structure the per-chunk lighting state

The pivotal choice — everything else in the plan is either preparation for it or independent of it.

### Option A — one exclusive lifecycle enum (`ChunkLightingState { Placeholder, …, Lit }`) (rejected)

- ✅ The intuitive reading of "collapse the flag cluster into a state machine"; a single field to inspect in the debugger; some illegal states genuinely unrepresentable.
- ❌ **The work flags are a set, not a chain.** All 8 `I/C/E` combinations are reachable and meaningful (§2.3 table) — an exclusive enum must enumerate the power set (× rounds counter), and every "state" is really "which work kinds are pending", i.e. a bit-set wearing an enum costume.
- ❌ **The machine's other halves live off-chunk** (`LightingJobs`/`GenerationJobs` membership, scheduler ready/waiting, mesh queue). An on-chunk enum claiming to be *the* state would be authoritative for none of them and would need constant reconciliation — a new bug class, the opposite of the goal.
- ❌ Every consumer (gates, scan arms, serializer) reads individual bits; an enum forces decode/re-encode at each site for zero information gain.

### Option B — `[Flags]` work byte + named transition API + shared gate predicate ✅ **CHOSEN**

Keep the three work bits as data (one `byte`), and make the **transitions** the structured, testable artifact: every mutation goes through a named `ChunkData` method mapping 1:1 to a §2.3 census row, with the flag→scheduler callback fired from a single funnel. Pair it with extracting the per-neighbor gate predicate into shared pure code (the harness currently hand-mirrors it). This is the proven house pattern — it is exactly what `LightingScheduleDecision`,
`LightingScanDecision`, `LightingJobProcessor`, and `JobCompletionPass` did for their slices (A2/B2/HF-4 all CLOSED on the fidelity backlog), extended to the last two unshared surfaces.

What becomes unrepresentable is precisely what historically broke: **partial transitions**. The schedule-clear is one atomic method (can no longer clear `C` but strand `E`, or vice versa); the stable re-arm decrements `R` and sets `E`+`C` in one call (a recycled-counter or half-armed state can't be authored); `Reset()` clears through the same funnel `B34`'s reflection backstop guards. Editor-only assertions (`[Conditional]`, the HF-1 pattern) can then enforce transition preconditions at zero IL2CPP cost.

### Option C — status quo + naming/docs only (rejected)

- ✅ Zero risk; the pipeline doc §2 already tabulates the flags well.
- ❌ **Leaves every F-finding standing**: the gates and coroutine stay hand-mirrored drift surfaces, transitions stay a 20-site scatter enforceable only by rule-following, and the NS-3 flag-pairing suite would have no structural hook to assert against.

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

On `ChunkData`: one `[NonSerialized] private LightingWork _lightingWork;` replaces the three backing bools. The three existing bool properties remain as thin bit adapters during migration (and possibly permanently — decided by call-site count at LP-4 execution, §8 Q2). All writes are replaced by transition methods (1:1 with the §2.3 census; names final at implementation):

| Method                                  | Census rows | Semantics                                                                                                         |
|-----------------------------------------|:-----------:|-------------------------------------------------------------------------------------------------------------------|
| `FlagInitialLighting()`                 |  1, 2, 16   | `I:=1`                                                                                                            |
| `FlagLightWork()`                       | 3, 4, 4b, 9, 11 | `C:=1`                                                                                                        |
| `FlagEdgeCheck()`                       |   3, 10b    | `E:=1` (disk-load-stable; neighbor trigger)                                                                       |
| `SpendEdgeCheckRound(bool rearm)`       | 10a, 10b, 10c | `R--`; when `rearm`, also `E:=1, C:=1`. See the cascade note below — this replaces v1.0's `ArmEdgeCheckRoundIfAvailable()` |
| `RegrantBorderEditEdgeRound()`          |      5      | `R := max(R, BORDER_EDIT_EDGE_CHECK_ROUNDS)` — the Bug-05 fix, preserved verbatim                                 |
| `OnLightingJobScheduled()`              |   6, 7, 8   | `C:=0; E:=0` — the atomic schedule-clear (`PerformEdgeCheck` **and** LI-2's band derivation *read* `E` before this fires, as today — F4) |
| `ClearInitialLighting()`                |      6      | `I:=0` after a successful initial schedule (kept separate: the coroutine/load paths clear it independently)       |
| `ClearAllLightingWork()`                |   13, 14    | disabled-lighting paths + `Reset()`                                                                               |

> **Cascade note (re-audit 2026-08-23).** v1.0 sketched `ArmEdgeCheckRoundIfAvailable()` as "if `R>0`:
> `R--`, `E:=1`, `C:=1`" — a two-outcome method that made the budget check *and* the re-arm one indivisible
> decision. P9-2 has since split those apart: `EdgeCheckCascadeDecision.Evaluate` owns the choice (`None` /
> `SpendOnly` / `SpendAndRearm`) and the caller applies it, so a spent-but-not-re-armed round is now a **legal,
> load-bearing** state rather than the illegal partial transition the original method existed to forbid. The
> transition API must therefore take the outcome as an *input* (`SpendEdgeCheckRound(rearm:)`) rather than
> re-deriving it from `R`. What stays unrepresentable is what still matters: `E` set without `C` on the re-arm
> path. **LP-4's executor must read `EdgeCheckCascadeDecision` before designing this method.**

**Callback funnel:** one private `SetWork(LightingWork next)` compares old/new masks and fires
`OnLightWorkFlagged(Position)` when any bit transitions 0→1 — preserving today's per-property semantics with one accepted, verified-equivalent delta: sites that today set two properties back-to-back (e.g. the stable re-arm) fire the callback **once instead of twice**. Downstream is a `ConcurrentQueue` drained into a `HashSet` (`LightWorkScheduler.DrainStaging` → `AddReady`), so duplicate enqueues were already deduplicated — observable behavior is identical. The funnel keeps the thread-safety property the serializer path relies on (row 2 sets bits from a
background thread; the callback is the thread-safe member).

**Hot-path cost:** byte masks replace bool fields for main-thread readers (scan visits only the ready set, O (schedulable); gates read 8 neighbors per call). No Burst surface exists — jobs never read these flags (chunk-pipeline rule). No allocation anywhere (methods are plain instance methods; the funnel is a compare+branch, same as today's property setters).

### 4.2 Shared neighbor-gate predicate (LP-2)

```csharp
/// <summary>Pure per-neighbor readiness predicate shared by World's three gates and the
/// editor harness — the gate-side completion of the shared-guard pattern
/// (LightingScheduleDecision / LightingScanDecision / JobCompletionPass).</summary>
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

`World`'s three gates become one `AllNeighborOffsets` loop each (killing `AreNeighborsReadyAndLit`'s duplicated cardinal/diagonal loops), assembling `NeighborFacts` and calling the shared predicate.
`LightingTestWorld.AreNeighborsReadyAndLit`/`AreNeighborsDataReady` assemble harness facts and call the *same* predicate — the readiness computation stops being a hand-mirrored fidelity gap (B2 remainder). `in`-struct of bools: no allocation, trivially inlined.

### 4.3 What deliberately does NOT change

- `LightWorkScheduler` (MT-2): untouched. Promotion contract, fail-safe, staging — all as-is.
- `LightingScanDecision` / `LightingScheduleDecision` / `JobCompletionPass`: untouched in LP-1..4 (LP-5 adds a caller; LP-6 may add an overload — both keep the shared-code property).
- `RemainingEdgeCheckRounds` semantics incl. the Bug-05 border-edit re-grant: preserved verbatim behind named methods.
- The relaxed `AreNeighborsMeshReady` contract and the `NeedsEdgeCheck`-is-not-a-gate-input rule (pipeline doc §3.3 note): preserved bit-for-bit by LP-2.
- `RunReGrantedEdgeCheckRound` (harness legacy-mode backstop): untouched.

---

## 5. Serialization impact (all phases)

The save boundary carries exactly **one** lighting flag today: `NeedsInitialLighting`, one bool in the chunk record (`ChunkSerializer.cs:142` write / `:226` read; `Migration_v2_to_v3_RestoreLighting.cs:108` force-writes it). `HasLightChangesToProcess` is *re-derived* on read from the persisted BFS queue counts (`:266`), and `NeedsEdgeCheck` is re-derived by `LoadOrGenerateChunkInner` (`World.cs:1282`). Re-verified at HEAD: the flag is still the **first** field after the chunk header, so the offset LP-4 must preserve is unchanged.
`IsAwaitingMainThreadProcess` and `RemainingEdgeCheckRounds` are `[NonSerialized]`.

Consequently: **no phase in this plan changes the on-disk byte layout.** LP-4's serializer edit is a mapping change only (write the `InitialLighting` bit as the same bool at the same offset; read it back through `FlagInitialLighting()`). No `SaveSystem.CURRENT_VERSION` bump, no migration step.

**Tripwire for executors:** if any phase finds itself wanting to persist the work byte, additional flags, or the rounds counter — stop; that is an AOT-migration item (`serialization-migration`
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

Ranked by value-vs-risk with PRIMARY = clarity/testability. Every phase is independently landable and leaves the repo green. **Universal regression gate for every phase** (stated once, applies to all): all **106** baselines of `Minecraft Clone/Dev/Validate Lighting Engine` green (legacy + scheduler mode), the LightScheduler suite (9 baselines) green, **and the NS-3 Chunk Pipeline suite (6 baselines) green** — it did not exist at v1.0 and it models the flag cluster directly, so it is now the closest thing to a state-machine regression gate this plan has;
`dotnet build "Assembly-CSharp.csproj"` AND `dotnet build "Assembly-CSharp-Editor.csproj"` clean (harness files are editor-assembly), plus the per-phase extras below. Workflow gotchas apply:
newly created `.cs` files need a Unity import before `dotnet build` sees them; the menu suite can run stale code after compilation — confirm red/green flips with a fresh
`RequestScriptCompilation` + `Unity_RunCommand` wave, gating on the DLL timestamp rather than `IsCompiling`.

> **Baseline counts are re-verified as of 2026-08-23** against `VALIDATION_SUITE_COVERAGE_ROADMAP.md`'s census
> (567 baselines / 25 suites). v1.0's "62 lighting baselines" and its "B71+ / B62–B70 taken" numbering advice are
> both stale — **do not number new baselines from them**; read the suite's own registration files first.

| Phase                                               | Scope (files)                                                                                                                                     | Effort | Depends on                         |
|-----------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------|:------:|------------------------------------|
| **LP-1 — Invariant probes** ✅ **SHIPPED 2026-08-23** | `World.cs`, `DebugScreen.cs`, `WorldFrameProfiler.cs` (dev/editor-only diagnostics)                                                               |   🟢   | —                                  |
| **LP-2 — Shared neighbor-gate predicate** ✅ **SHIPPED 2026-08-23** | `Helpers/NeighborReadinessDecision.cs` (new); `World.cs` gates + `VoxelData.cs`; `LightingTestWorld.cs`; NS-3 baseline **B7**                |   🟡   | —                                  |
| **LP-3 — Retire `IsAwaitingMainThreadProcess`**     | `ChunkData.cs`, `WorldJobManager.cs`, `World.cs`, harness, rules/docs                                                                             |   🟡   | LP-1 (evidence), LP-2              |
| **LP-4 — `LightingWork` byte + transition API**     | `ChunkData.cs`; call sites in `World.cs`, `WorldJobManager.cs`, `ChunkSerializer.cs`, `ChunkStorageManager.cs`; harness; new transition baselines |   🔴   | LP-2 (fewer sites); LP-3 preferred |
| **LP-5 — Explicit scheduling contract + coroutine** | `WorldJobManager.ScheduleLightingUpdate`; `World.cs` coroutine; new fallback baseline                                                             |   🟡   | LP-4                               |
| **LP-6 — Lazy strict-gate evaluation** *(optional)* | `LightingScanDecision.cs` overload; `World.cs` scan; `LightingFrameSimulator.cs`                                                                  |   🟢   | LP-2                               |
| **LP-7 — Naming & doc hygiene**                     | `RecalculateSunLightLight` rename; residual doc alignment                                                                                         |   🟢   | —                                  |

**Minimal standalone-value set:** LP-1 + LP-2 (closes the fidelity B2 remainder and de-risks everything after). **Validation is built alongside, not after** — LP-4 and LP-5 each add baselines in the same commit as the code. **Number them from the suite's current tip, read at execution time** (2026-08-23: the lighting suite's registrations run past B114 and are spread across `Lighting/` and `Lighting/Baselines/`; v1.0's "B71+" is long overtaken).

---

### LP-1 — Invariant probes (🟢, no behavior change)

**Delivers:** mechanical evidence for the two convention-only invariants (F1, F6) that later phases rely on — the same "instrument before you refactor" discipline as HF-1.

**Follow the CP-1 precedent, do not invent a mechanism.** CP-1 shipped this exact pattern after v1.0 was
written: a probe block at `World.cs:375–415` (counters + XML-doc'd public properties),
`[Conditional("UNITY_EDITOR")] [Conditional("DEVELOPMENT_BUILD")]` **void** helpers (`CountLoadFault`,
`TrackStuckLoadingChunk`, `FinalizeStuckLoadingScan`), a HUD surface at `DebugScreen.cs:497–516`, and — critically
— its static reset folded into the **existing** `World.DomainReset` (`World.cs:513–518`) rather than a second
`[RuntimeInitializeOnLoadMethod]` (UDR0005). Its stuck-`IsLoading` detector already rides the same ~1 s walk probe 2
needs. Extend that block.

- **Scope:** editor/dev-only (`[Conditional("UNITY_EDITOR")]` + `[Conditional("DEVELOPMENT_BUILD")]`, HF-1/CP-1's dual pattern; zero IL2CPP cost). `World.cs` + `DebugScreen.cs` only — **no editor-assembly file changes**, so the editor `dotnet build` is a no-op confirmation this phase, not a real gate:
    1. Count observations of `IsAwaitingMainThreadProcess == true` **inside `AreNeighborsReadyAndLit`** (both loops, `W:2887`/`:2910`) and in `UnloadChunks` (`W:3465`). Instrumenting inside the gate rather than at its call sites covers all four readers — the `Update` scan, the coroutine's edge arm, and the coroutine's `HasPendingEdgeChecks` — for two call sites. One `Debug.LogWarning` on first hit naming the chunk + site, then counter-only. Prefer **instance** fields: `World` is re-instantiated per play session, so unlike CP-1's static `s_loadArmFaults` they need no `DomainReset` line. Expected: **zero, ever** (F1's claim).
    2. In the ~1 s fail-safe scan (`World.cs:2385–2410`), assert per **key** of `worldData.SunlightRecalculationQueue` that the owning chunk has a work flag set (F6's claim: never fires). Iterate keys only — the `Dictionary` enumerator is a struct, so the walk stays allocation-free. Use the dictionary key directly as the owner coord; do **not** re-derive it from a column through `SunlightColumnRouting`, which would introduce a routing bug into the probe itself. A key whose chunk is **not resident** gets its own separate counter and no error — the unload drain at `World.cs:3539` legitimately produces that state, so treating it as a violation would be a false positive (decided 2026-08-23).
- **Positive control (required — silence alone proves nothing):** before the soak, temporarily force each probe to trip — set `IsAwaitingMainThreadProcess` outside the merge pass, and enqueue a column while suppressing the `C` write — and confirm each logs and increments. Revert **by hand-edit, never `git checkout --`** (the tree is normally dirty). Without this, a probe that is silent because it is dead is indistinguishable from a probe that is silent because the invariant holds.
- **Acceptance:** universal gate + an in-game soak (streaming, edits, border edits, a save/reload) with both probes silent. Record the result here (Amended line) — LP-3 is **blocked** until probe 1 has a silent soak on record.
- **Known limits of the evidence** (state these in the Amended line rather than overclaiming): neither probe is reachable from any validation suite — `LightingTestWorld` and NS-3's `ChunkPipelineSimulator` each keep their **own** gate analog and their own `IsAwaitingMainThreadProcess` model, so nothing in either suite flows through the instrumented production code. Probe 2 samples at ~1 Hz, so a violation that self-heals within a second is invisible. Both probes compile out of IL2CPP, so production is unobserved.
- **Testability gain:** turns two "should hold" conventions into observable invariants; probe 2 is the first concrete member of NS-3's flag-pairing assertion family.
- **Doc-sync:** none (no behavior change). **Serialization:** none.

**Amended:** 2026-08-23 — **LP-1 shipped and its soak ran silent. Probe 1's precondition for LP-3 is met.**

*What was observed.* One interactive editor session (Mono, `enableLighting` and `EnablePersistence` both on),
covering sustained streaming, ~20 sky-affecting block edits, ~10 chunk-border edits, three edit-then-flee rounds
(confirmed to have exercised the persist path — the console carries `[LIGHTING RESCUE] Saved …` for
`ChunkCoord(-47, 12…14)`), and a save/reload with a return to the edited region.

**The soak spanned two `World` instances, and this splits the evidence — read the two halves separately.** The
reload was performed by returning to the main-menu scene, which unloads the World scene and destroys its `World`;
the reloaded world therefore got a **fresh instance with every probe counter back at zero** (the counters are
instance fields, deliberately, so a play session starts clean). The console confirms exactly two
`--- Startup complete ---` entries in the session, and was never cleared.

- **Counters cover the post-reload segment only** (steps after the reload). At the end, with **841 chunks
  resident and 35 live keys in `SunlightRecalculationQueue`**: probe 1 read `cardinal 0 / diagonal 0 / unload 0`;
  probe 2 read `violations 0` (gauge and total), `orphaned 0`, `unpopulated 0` (gauge and total). All 35 queued
  keys had a populated resident owner carrying a work flag — F6's pairing held on every key that segment walked.
  The pre-reload segment's counters were lost with its `World` and were never read.
- **The first-hit warnings cover the whole soak, and they are what carries the verdict.** `_awaitProbeLogged` and
  `_sunlightQueueProbeLogged` are per-instance, so each of the two `World` instances had its own unused warning
  budget, and Unity's console retains entries across a scene reload within one play session. **No `[LP-1]`
  warning was emitted by either instance at any point**, so neither probe observed a violation across steps 1–5.
  **Console eviction was ruled out rather than assumed** — the buffer held 7262 entries whose *oldest* predates
  the first world load (`--- Startup complete ---` at index 37, the second at 7157), so with FIFO eviction
  nothing from the session was dropped and "no warning" means "no observation", not "the record was lost". The
  only two `[LP-1]` entries in the buffer sit at indices 7238/7240 and name the two chunks used by the liveness
  injections below — they are artefacts of that check, not soak findings. Note the segment split is lopsided:
  ~7120 entries before the reload versus ~105 after, so the counters above cover only a short tail while the
  warnings cover everything.

*Why the silence is evidence and not a dead probe.* Both probes were proven live **on the post-reload instance,
immediately after the soak**, by injection: stripping the work flags off one queued owner made probe 2 report a violation within
two scans, and flagging `IsAwaitingMainThreadProcess` on a chunk whose neighbour was then dirtied drove probe 1's
cardinal counter to 17. (A first probe-1 attempt read zero because the flag and the dirtied chunk were the same
chunk — the gate inspects a chunk's *neighbours*, so that arrangement cannot trip it. The mis-arming, not the
probe, was at fault.) Counter values recorded on the HUD after those injections are injection artefacts, not
soak results.

*Known limits of this evidence — carry these into LP-3's go/no-go rather than reading the zeros as proof:*

- **No validation suite reaches either probe.** `LightingTestWorld` and NS-3's `ChunkPipelineSimulator` each keep
  their own gate analog and their own `IsAwaitingMainThreadProcess` model, so no suite run exercises the
  instrumented production code. The 106/9/6/22 green gate says the probes broke nothing; it says nothing about
  what they observed.
- **Probe 1's zero is partly structural.** The set (`WJM:1668`) and the clear (`WJM:1629`) sit inside one
  `try`/`finally` iteration of `JobCompletionPass`, and `AreNeighborsReadyAndLit` guards the same window with
  `LightingJobs.ContainsKey` before reaching the flag at all. A silent probe 1 therefore confirms *no re-entrant
  reader appeared during this soak* — it does not independently re-prove F1's zero-window claim, which remains a
  structural argument.
- **~1 Hz sampling.** Probe 2 walks the queue once per fail-safe scan, so a violation that self-heals inside a
  second is invisible. The `unpopulated` counter exists because that class of state provably does self-heal.
- **`orphaned` and `unpopulated` were never observed arising naturally** — both stayed at zero across the soak and
  were only ever exercised by forced controls. Their branches are proven reachable, not proven to occur in play.
- **Counters do not survive a world reload.** Being instance fields, they reset whenever the World scene is
  unloaded (returning to the main menu, or loading another world) — which happened once mid-soak here. A future
  soak that wants whole-session *counter* totals must either avoid reloading or read the counters immediately
  before each reload; the first-hit warnings are the only signal that spans instances.
- **IL2CPP is unobserved.** Both probes compile out under `[Conditional]`, so production behaviour is untested.
- **One config unsampled.** Probe 2 is hosted inside the `enableLighting` block, so a lighting-disabled session
  observes nothing. This is by construction — enqueue is lighting-gated too — but it means "silent" says nothing
  about that config.

*Corrections made to this packet's own text while executing it* (see also the Document History entry): the
justification for treating an ownerless queue key as non-violating was wrong. The unload drain does **not**
produce that state — it removes the key (`World.cs:3673`, re-anchored from `:3539`) and releases the pooled set
strictly before the only `worldData.RemoveChunk` (`:3731`). The real source is
`WorldData.QueueSunlightRecalculation`, which writes the key unconditionally but sets the flag only when the
owner is resident, so a BFS spilling across a border into unloaded territory mints one by design. **The decision
stands; only its reason was wrong.**

### LP-2 — Shared neighbor-gate predicate (🟡) ✅ **SHIPPED 2026-08-23**

**Delivers:** §4.2. One predicate, three thin gates, harness drives the same code.

- **Scope:** new `Assets/Scripts/Helpers/NeighborReadinessDecision.cs` (runtime assembly — the editor harness references runtime helpers already, per `LightingScanDecision` precedent);
  `World.cs:2850–3010` (three gates → single-loop bodies; merge ReadyAndLit's two loops; delete the orphaned docstring at W:2800–2806); `LightingTestWorld.cs:420–480` (both gate analogs — `AreNeighborsDataReady` at `:426`, `AreNeighborsReadyAndLit` at `:461` — route through the predicate; keep their grid-boundary skip documented as today).
- **Ordering:** independent; do before LP-4 (it shrinks LP-4's blast radius).
- **Trap (gate ordering, chunk-lifecycle skill):** this is a *re-housing*, not a redesign. The relaxed `AreNeighborsMeshReady` must stay relaxed (§9.3 wave-front deadlock); `enableLighting`
  gating of the `NeedsInitialLighting` check in MeshReady must be preserved; out-of-world neighbors stay "ready"; `IsChunkInWorld` and dictionary probes stay caller-side facts.
- **Prove-red:** temporarily invert the `lightingInFlight` term inside the predicate → expect scheduler-mode baselines (B66/B67/B70) and edge-check baselines to red; restore → green. This proves the suite actually flows through the shared code.
- **Acceptance / regression:** universal gate **+ the meshing suite** (`Validate Meshing`, **57 baselines** as of 2026-08-23 — `AreNeighborsMeshReady` feeds `ScheduleMeshing` via `World.cs:2969`) **+ in-game smoke**: fly a sustained straight line (the wave-front pattern) and confirm no stuck-unmeshed swathes and zero recurring fail-safe promotions (`enableDiagnosticLogs`).
- **Testability gain:** fidelity **B2 remainder closes** — the readiness computation itself becomes shared, unit-testable code; a future gate bug is a suite red, not an in-game mystery.
- **Doc-sync (same commit):** `CHUNK_LIFECYCLE_PIPELINE.md` §3 (add the shared-predicate pointer per gate table), `LIGHTING_SYSTEM_OVERVIEW.md` §3.5 (one line), fidelity doc B2 entry (flip the remainder note). **Serialization:** none.

**Amended:** 2026-08-23 — **LP-2 shipped.** `Helpers/NeighborReadinessDecision.cs` now backs all three
`World` gates and the harness's `AreNeighborsReadyAndLit`; NS-3 gained baseline **B7** (census). Gate: Chunk
Pipeline 7/7, Lighting 106/106 (both modes), LightScheduler 9/9, Meshing 57/57, both `dotnet build` targets
clean. Four corrections to this packet's own text, all found while executing it:

1. **The predicate returns a reason, not a bool.** §4.2 sketches `bool NeighborBlocks(...)`. That cannot
   coexist with LP-1's probe, which counts `IsAwaitingMainThreadProcess` observations *from inside the gate
   loop* — a bool forces the term to be re-tested caller-side, re-duplicating the exact term LP-2 unifies.
   Shipped as `BlockReason Evaluate(Gate, in NeighborFacts)`. **This is groundwork for LP-3:** deleting the
   flag becomes one enum member plus one probe call site, not a hunt across three gates.
2. **The harness has only ONE routable gate analog.** The scope line says to route both
   `LightingTestWorld.AreNeighborsDataReady` (`:426`) and `AreNeighborsReadyAndLit` (`:461`). The former is
   not a loop — it is `GetChunk(coord).NeighborsReady`, a coarse per-chunk bool — so there is nothing to
   route. Only `:461` was rewired. Deriving that toggle from real neighbor state stays a fidelity-backlog
   item, not LP-2 scope.
3. **The meshing suite is NOT a gate for this phase — the acceptance line was wrong.** It claimed the suite
   covers `AreNeighborsMeshReady` because that gate "feeds `ScheduleMeshing`". It does not: the suite passes
   `neighborsMeshReady` as an *input bool* and never calls the gate. **NS-3 is the only suite that reaches
   the production computation** (`ChunkPipelineFixture` stands up a stub `World` and calls the real gates).
   Run the meshing 57 as a non-regression check, never as this phase's gate. *(The decision to run it
   stands; only its stated reason was wrong.)*
4. **The predicted prove-red does not happen — the measured one is narrower, and it matters.** The packet
   predicted that inverting `lightingInFlight` would red scheduler-mode baselines B66/B67/B70. **Measured:
   it reds B7 alone.** All 106 lighting baselines stayed green, and so did NS-3's own B1–B6 — the pump
   converges either way, and the lighting harness exercises the *handling* of a readiness result, never its
   computation. **Consequence: B7 is the sole guard on the gate-term matrix.** Had LP-2 shipped without it
   (the packet listed no new baseline for this phase), a gate-term regression would have had no tripwire at
   all. Weigh that before trimming validation from a "pure re-housing" phase.

*Known limits of this evidence:* the suites are edit-mode Mono, so IL2CPP is unobserved as usual; the
in-game smoke below is one flight, not a soak; and B7 asserts the predicate against a hand-written oracle,
so a defect present in *both* would pass — the oracle was transcribed from the three original `World` loops
rather than from the extracted code, which is the only mitigation.

*Also corrected while executing:* the packet says to **delete** the orphaned docstring at `W:2800–2806`
(re-anchored: `2964–2970`). Deleting it would have left `AreNeighborsReadyAndLit` — a public method — with
no XML doc at all, since the orphan *was* its docstring, detached. It was moved onto the method and
corrected: it described "cardinal neighbors" (the gate checks 8) and called itself a prerequisite for "a
mesh generation job" (that is `AreNeighborsMeshReady`; this gate serves the edge-check arm). Gate anchors
also moved `2850–3010` → `3014–3175`.

### LP-3 — Retire `IsAwaitingMainThreadProcess` (🟡, evidence-gated)

**Delivers:** one dead axis removed from the state machine, gates and the completion driver simplified.

- **Precondition (hard):** LP-1 probe 1 recorded silent over a real soak. If it ever fired, STOP — the flag is load-bearing somewhere this analysis missed; file the finding and re-plan.
- **Scope:** delete the field (`ChunkData.cs:161–167`) + `Reset()` line (`:265`); delete set/clear (`WJM:1668`, the `ReleaseJob` clear at `WJM:1629` — the container release and MP-5's `_curLightJob = default;` at `:1637` both stay); remove the gate terms (`World.cs:2887`/`:2910`, `UnloadChunks` W:3465) and the `NeighborFacts` member (LP-2 landed first). **Also delete the harness- and console-side readers the v1.0 scope missed:** `LightingTestWorld.cs:478` (gate analog) and its set/clear at `:805`/`:916`, `LightingAssert.cs:299`/`:328` (stale-flag list), `ChunkPipelineSimulator.cs:207`/`:218`/`:394` and `PipelineAssert.cs:131`/`:140` (NS-3's model — **new since v1.0**, and NS-3's `B5`/`B6` prove-red is documented as "skip the `IsAwaitingMainThreadProcess` clear", so those two baselines change shape and their docstrings at `ChunkPipelineValidationSuite.Baseline.cs:19`/`:297–300` must be rewritten), `ChunkInfoCommand.cs:53` (console display), and `ChunkUnloadDecision.cs:60`/`JobCompletionPass.cs:44` (docstrings). B34's reflection backstop adapts automatically — field gone.
- **Why safe:** the whole flight window is guarded by `LightingJobs.ContainsKey` in the same gates; merge atomicity is main-thread-guaranteed; the per-job `finally` pairing (HF-2) becomes vacuous for this flag while container release keeps its own `finally`.
- **Prove-red:** n/a (a deletion has no red to prove). Regression carries the weight:
  universal gate, **B65 specifically** (fault-isolation semantics of `ReleaseJob` change shape), plus an in-game streaming soak with unload/reload cycles (UnloadChunks touched) watching for stuck chunks and fail-safe promotion counts.
- **Testability gain:** the state machine loses an axis no test could ever exercise (zero observable window ⇒ untestable by construction); the §2.3 census shrinks.
- **Doc-sync (same commit):** `CHUNK_LIFECYCLE_PIPELINE.md` §2 (row delete) + §3 gate tables + §9.6 code excerpt, `LIGHTING_SYSTEM_OVERVIEW.md` (§3.4 mentions), fidelity doc (B4/B7 entries mention the flag), `.agents/rules/chunk-pipeline.md` + `pool-reset-safety.md` flag lists, and the `chunk-lifecycle` skill's flag enumeration. **Serialization:** none (`[NonSerialized]`).

### LP-4 — `LightingWork` byte + transition API (🔴, the headline)

**Delivers:** §4.1 in full. Every §2.3 census row becomes a named method; partial transitions become unrepresentable; transitions become directly baselinable.

- **Scope:** `ChunkData.cs` (bits + funnel + methods; three bool properties kept as thin adapters *during* the migration); call-site migration —
  `WorldJobManager.cs` (`:794→FlagLightWork`, `:916/923→` read `NeedsEdgeCheck` **twice** (`PerformEdgeCheck` and LI-2's `DeriveBandHeight` at `:868`) then
  `OnLightingJobScheduled()` after `job.Schedule()` succeeds (preserve the current clear-after-schedule ordering — on a schedule throw the flags stay set, as today),
  `:1240→FlagInitialLighting`, `:1620/1823→FlagLightWork`, `:1228→FlagLightWork`,
  `:1806–1818→SpendEdgeCheckRound(rearm:)` driven by `EdgeCheckCascadeDecision`'s outcome (+ keep `LastEdgeRecycleJobCount`),
  `:2209–2210→FlagEdgeCheck+FlagLightWork` — or a dedicated `FlagNeighborEdgeCheck()` setting both, executor's call);
  `World.cs` (`:1210→FlagLightWork`, `:1268/1410/2540→ClearInitialLighting`,
  `:1282→FlagEdgeCheck`, `:1482–1484→ClearAllLightingWork`, `:2535→FlagLightWork` (edge-arm pre-set — name the intent in a comment), `:3562→FlagInitialLighting` (P-4 rec 3, census row 16));
  `ChunkData.ModifyVoxel:581→RegrantBorderEditEdgeRound`, `ChunkData:1357/1371` (BFS enqueues), `ChunkData:449–450` (loaded-data adoption, census row 3b);
  `WorldData.cs:471→FlagLightWork` (census row 4b);
  `ChunkSerializer.cs:142/226` (bit↔bool mapping), `:266→FlagLightWork`;
  `ChunkStorageManager.cs:813` (snapshot reads the bit); harness (`LightingTestWorld`/`TestChunk` route their real-`ChunkData` writes through the same methods). `Migration_v2_to_v3_RestoreLighting` is untouched (it writes stream bytes, not `ChunkData`).
- **Callback-delta check (the one behavioral micro-delta, §4.1):** combined transitions fire
  `OnLightWorkFlagged` once where two property writes fired twice. Verify equivalence explicitly:
  the LightScheduler suite green + a scheduler-mode suite run + reasoning note in the commit (staging dedupes at drain).
- **Editor-only transition assertions** (HF-1 pattern, zero IL2CPP cost): e.g.
  `OnLightingJobScheduled` asserts a job was actually registered by the caller;
  `ArmEdgeCheckRoundIfAvailable` asserts main-thread. Keep light — assertions document the contract, they don't re-implement the scheduler.
- **New baselines (B71+, same commit):** a transition-census baseline family in the lighting suite (oracle-free, the B34/B47 style): for each transition method assert before-bits → after-bits, rounds-counter effect, and callback fire-count (installable sink — the harness already owns `OnLightWorkFlagged` save/restore). This is the NS-3 flag-pairing family's second concrete member.
- **Prove-red:** sabotage `SpendEdgeCheckRound` to skip `C:=1` on the re-arm path (arm E without C) → the edge-round-dependent baselines (B8 initial-wave family / B70 border-fuzz reconcile) must red; restore → green. Add a **second** prove-red for the cascade split: force `SpendOnly` to re-arm → the P9-2 cascade baselines (`Lighting/Baselines/LightingValidationSuite.Baseline.P92Cascade.cs`) must red. Also run the B34 reflection backstop unmodified — it must still pass with the byte field (it walks `[NonSerialized]` primitives; an enum-typed byte qualifies — verify, and extend the backstop if enum fields are skipped).
- **Acceptance / regression:** universal gate + full in-game session (streaming + edits + border edits + reload — the Bug-05 re-grant path and the serializer path both need live confirmation).
- **Testability gain:** transitions unit-baselinable; illegal partial transitions unrepresentable; the frame simulator and production share the *mutation* layer on top of the already-shared decision/completion layers — the full scheduling stack is now one code path.
- **Doc-sync (same commit):** `CHUNK_LIFECYCLE_PIPELINE.md` §2 (rewrite the flag table around bits + transition methods; note F9's `IsLoading` status honestly), §4 pseudocode names the transition methods; `LIGHTING_SYSTEM_OVERVIEW.md` §3.2/§3.4 mentions;
  `pool-reset-safety.md` "property setter subtlety" section (funnel replaces per-property setters); `chunk-lifecycle` skill flag list; fidelity doc B4 note. **Serialization:** mapping-only; layout unchanged (§5 tripwire applies).

### LP-5 — Explicit scheduling contract + startup-coroutine unification (🟡)

**Delivers:** F2 + F4 closed — the silent `NeedsEdgeCheck` read/clear becomes an explicit, baselined contract, and the startup coroutine stops hand-mirroring the scan arms.

- **Scope:**
    1. `WorldJobManager.ScheduleLightingUpdate` (`:781–935`): the job's `PerformEdgeCheck` is populated from an explicit read (`chunkData.NeedsEdgeCheck` at `:916` — unchanged) but the clear moves into
       `OnLightingJobScheduled()` (done in LP-4); ADD an XML-doc'd statement of the weak-gate fallback contract on the method (edge work rides ANY successful schedule) — the §7 pipeline behavior, now visible at the signature. **The contract must name both readers** (F4 as re-audited): `PerformEdgeCheck` *and* LI-2's `DeriveBandHeight` (`:868`), which uses the same flag to force a full-height band. A contract statement that mentions only the first would be freshly wrong.
    2. `World.cs:1398–1440` (coroutine Steps 2a/2b): replace the hand-mirrored arms with
       `LightingScanDecision.EvaluateReadyChunk` + the same switch the Update scan runs, preserving the coroutine's specifics: `Allocator.TempJob`, sweep-until-quiescent structure,
       `CompleteAndProcessLightingJobs()` between sweeps, safety-break diagnostics, and the `enableLighting`-off else-arm at `:1477–1486`. The arm *decision* becomes shared; the sweep *driver* stays coroutine-specific.
    3. *(new in the re-audit)* The coroutine's three `HasPending*` loop conditions (`World.cs:1651–1700`) are a **second** hand-mirror of the same gate logic and drive the sweep loop's termination. Fold them in or explicitly scope them out — leaving them is how the coroutine keeps a private opinion of readiness after step 2 removes the first mirror.
- **New baseline (B7x, same commit — closes F4's coverage gap):** the §7 weak-gate fallback has NO dedicated baseline today. Scheduler-mode scenario: chunk with `E=1, C=1`, neighbors data-ready but NOT lit (in-flight neighbor) → assert the regular arm schedules with
  `PerformEdgeCheck = true` and both flags clear. Prove-red: neuter the fallback (make the regular-arm schedule drop `E` without passing it to the job) → new baseline reds; restore.
- **Acceptance / regression:** universal gate + **world-load in-game checks** (the coroutine is the startup path): load an existing world AND create a new one; confirm the
  "exceeded max iterations" safety-break never fires and load-time lighting converges as before.
- **Testability gain:** the startup path's arm selection is now the same shared, sim-guarded decision as the steady-state scan — a whole hand-mirrored surface deleted.
- **Doc-sync (same commit):** `CHUNK_LIFECYCLE_PIPELINE.md` §4 "Critical Scheduling Detail" + §7 fallback section (rewrite as explicit contract + baseline pointer);
  `LIGHTING_SYSTEM_OVERVIEW.md` §3.6 step 3. **Serialization:** none.

### LP-6 — Lazy strict-gate evaluation (🟢, optional, SECONDARY perf)

**Delivers:** F7 — the scan computes `AreNeighborsReadyAndLit` only when the edge arm needs it.

- **Scope:** add an `EvaluateReadyChunk` overload taking a small gate-provider interface (`INeighborGates { bool DataReady(); bool ReadyAndLit(); }`) implemented by a **cached** adapter on `World` and on the sim (zero alloc, no per-call delegates); the laziness lives inside the shared function so both callers stay identical. Delete the old always-eager call pattern at
  `World.cs:2518–2519` and the sim's mirror (`LightingFrameSimulator.cs:439`) in the same commit (both callers move atomically — the shared-code invariant).
- **Gate:** universal gate + a before/after measurement of the `WorldFrameProfiler` Light phase under a streaming load. **Ship only on a measured win** (perf-benchmark discipline); otherwise record NO-GO here and close the phase — the clarity value alone does not justify signature churn.
- **Doc-sync:** pipeline §4 pseudocode note. **Serialization:** none.

### LP-7 — Naming & doc hygiene (🟢)

- **Scope:** `RecalculateSunLightLight()` → `RecalculateSunlight()` via the `refactor-safely`
  skill (declaration `ChunkData.cs:1434`; callers `World.cs:1262`, `:1407`, `:2533` — three, all in `World.cs`; plus the harness docstring at `LightingTestWorld.Builder.cs:225`); verify no serialized name is touched (method — safe). Residual doc alignment (anything §2 of the pipeline doc still footnotes that LP-3/LP-4 made false). Explicitly does NOT start the Sun→Sky rename (Phase B).
- **Gate:** universal gate. **Doc-sync:** pipeline/lighting docs mention the method by name in pseudocode — update in the same commit. **Serialization:** none.

---

## 8. Open questions

1. **LP-1 probe results** — does `IsAwaitingMainThreadProcess` ever read true at a gate in a real soak? Resolves LP-3's go/no-go; the answer lands here as an Amended line + a checkbox in LP-3.
2. **Keep or remove the three bool adapter properties after LP-4?** Decide by call-site count at execution time: if ≤ a handful of readers remain (gates read via LP-2 facts, scan reads via the decision inputs), remove them and read bits directly; otherwise keep the adapters permanently as the read API. Either way, *writes* go through transition methods only.
3. **LP-6 worth it?** Only a measurement answers it; the phase carries its own GO/NO-GO gate and a NO-GO is a valid close-out.
4. *(new 2026-08-23)* **How far does LP-4's transition API absorb `EdgeCheckCascadeDecision`?** The pure decision and the transition method now meet on the same three lines (`WJM:1800–1818`). Options: leave the decision untouched and have `SpendEdgeCheckRound(rearm:)` take its outcome as a parameter (least churn, keeps P9-2's rollback flag intact), or fold the effect application into a method that takes the outcome enum directly. Decide at LP-4 execution; **do not** collapse the three outcomes back into two (F11).
5. *(new 2026-08-23)* **Does LP-3 owe NS-3 a replacement assertion?** NS-3's `B5`/`B6` currently prove-red on "skip the `IsAwaitingMainThreadProcess` clear". Deleting the flag removes that mutation target, so either those baselines get a new prove-red against the remaining `LightingJobs` in-flight guard, or the suite loses two of its six teeth. Resolve *before* LP-3 lands, not after.

---

## Document History

* **v1.0** - Initial design (analysis + LP-1…LP-7 phased plan; flag/gate census at `4cb80e4`)
* **v1.1** - Full §2 re-audit at `6b899481`, 631 commits after v1.0's census. F9 struck (CP-3 gave `IsLoading` two
  clear sites); F1 gained a fourth reader; F6's enqueue surface re-derived as three paths, two bypassing the
  `WorldData` API; census row 10 split into 10a/10b/10c for P9-2's `EdgeCheckCascadeDecision`, which retires
  v1.0's `ArmEdgeCheckRoundIfAvailable()` sketch (§4.1 cascade note) and adds F11; three new census rows (16, 17,
  3b/4b); F4 widened by LI-2's second `NeedsEdgeCheck` reader. LP-1 rewritten onto CP-1's shipped probe pattern
  with a mandatory positive control and an explicit statement of what a silent soak does *not* prove. All line
  anchors and baseline counts re-derived (lighting 62 → **106**; NS-3's 6-baseline suite added to the universal
  gate). §3's decision and §4.2/§4.3 re-checked and unchanged.
* **v1.2** - **LP-1 executed and shipped.** §7's LP-1 packet gains an Amended line recording a silent soak, the
  post-soak injection that proves both probes were live, and six calibrated limits on that evidence. Two
  corrections to the packet's own text, both found while executing it: the ownerless-key decision was justified
  by the unload drain, which provably does not produce that state (the real source is
  `WorldData.QueueSunlightRecalculation`'s unconditional key write — decision unchanged, reason replaced), and
  LP-1's scope row named `WorldJobManager.cs`, which it never touched (actual: `World.cs`, `DebugScreen.cs`,
  `WorldFrameProfiler.cs`). Probe 2 gained a third classification — resident-but-unpopulated — after a review
  found it silently counted such owners as clean. **LP-3's hard precondition is now satisfied.**
* **v1.3** - **LP-2 executed and shipped.** §7's LP-2 packet gains an Amended line. Four corrections to the
  packet's own text, all found while executing it: the predicate ships returning a `BlockReason` rather than
  §4.2's sketched `bool`, because a bool cannot coexist with LP-1's in-gate probe (and the enum makes LP-3's
  deletion a one-member change); the harness has only ONE routable gate analog, not two —
  `LightingTestWorld.AreNeighborsDataReady` is a coarse per-chunk bool with no loop to route; the acceptance
  line named the meshing suite as a gate for `AreNeighborsMeshReady`, which it cannot be (that suite takes
  `neighborsMeshReady` as an input bool — NS-3 is the only suite reaching the production computation); and the
  predicted prove-red was wrong — inverting `lightingInFlight` reds **B7 alone**, with all 106 lighting
  baselines and NS-3's own B1–B6 staying green, which makes the new B7 census the sole guard on the gate-term
  matrix. The orphaned docstring was **moved and corrected** rather than deleted (it was
  `AreNeighborsReadyAndLit`'s own detached docstring, and stale in two ways). Gate anchors re-derived
  `2850–3010` → `3014–3175`.

---

**Last Updated:** 2026-08-23 (**v1.3 — LP-2 shipped**; the shared gate predicate backs all three `World` gates and the harness analog, NS-3 baseline B7 pins the gate-term matrix, and four corrections to the LP-2 packet are recorded in its Amended line — see Document History) **Next Review:** when LP-3 starts (LP-3's precondition is met and LP-2, its second dependency, has landed — but read LP-1's Amended-line limits first; the zeros are calibrated, not absolute, and LP-2's measured prove-red shows how little the lighting suite observes about gate terms)
