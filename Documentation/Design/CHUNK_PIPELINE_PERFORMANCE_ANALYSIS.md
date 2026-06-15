# Chunk Pipeline Performance Analysis

> Findings from a code-level performance review of the chunk generation → lighting → meshing pipeline,
> focused on two observed symptoms: (1) a cascading memory/FPS failure when the player outruns
> generation, and (2) slow initial world *loading* (not creation) of already-generated chunks.
>
> Status: **Partially implemented.** §1.1 (job NativeArray pooling) shipped 2026-06-11 — see the
> "Implemented" note in §1. All other findings remain open. Each finding includes a recommendation
> and an Impact Analysis in the style of `CODEBASE_IMPROVEMENTS.md`.

**Analyzed:** 2026-06-11, at commit `8f90450` (branch `feat/Modular-World-Generation-&-World-Types`).
**Analysis is static (code reading), not yet confirmed by profiler capture.** See §7 for the
recommended verification steps before implementing fixes.

Related docs: `Architecture/CHUNK_LIFECYCLE_PIPELINE.md`, `Architecture/LIGHTING_SYSTEM_OVERVIEW.md`,
`Guides/GENERAL_OPTIMIZATION_GUIDE.md`, `Bugs/JOB_SYSTEM_BUGS.md`, `Bugs/LIGHTING_BUGS.md`.

> **Master backlog:** This document is the *deep-dive* for the chunk pipeline. The single
> at-a-glance backlog of **all** open performance items (including this document's open findings,
> tracked there as `P-1`…`P-6`, plus meshing/rendering/lighting/tick findings from the June 2026
> audit) is `PERFORMANCE_IMPROVEMENTS_REPORT.md`. Note: the report's LI-1 (padded lighting volume)
> trades *against* §1.2 below — benchmark both before committing to either.

---

## Summary (TL;DR)

The single biggest structural cost: **every lighting and meshing job allocates and memcpys ~1.7 MB
of full-chunk snapshots on the main thread at schedule time, and merges results back with a full
32,768-voxel main-thread scan at completion time** (§1, §2).

Both observed symptoms are downstream of design gaps rather than tuning problems:

- The **cascading failure** is *unbounded production + fixed-per-frame consumption +
  unload-blocking lighting flags* (§3).
- The **slow initial load** is *every disk-loaded chunk unconditionally re-running full
  neighborhood lighting via `NeedsEdgeCheck`, amplified by the 2-round edge-check cascade that
  re-dirties itself and 4 neighbors* (§4). A never-stabilizing RGB lighting bug is plausible but
  unproven — instrumentation to separate the two is described in §4.3.

---

## 1. Per-job full-volume copies (biggest available win)

### Observed

`WorldJobManager.ScheduleLightingUpdate` (`WorldJobManager.cs`, ~line 292) allocates **17
NativeArrays per job**:

- 9 full `uint` voxel maps (own + 8 neighbors) at 16×128×16 = 32,768 entries = 128 KB each → 1.125 MB
- 8 full `ushort` light maps at 64 KB each → 0.5 MB
- plus heightmap, queues, mods list

≈ **1.7 MB allocated, zeroed, and section-copied on the main thread per lighting job**.
`ScheduleMeshing` does the same with 19 arrays (9 voxel maps + 9 light maps + section data + output).

With `maxLightJobsPerFrame = 32` and `maxMeshRebuildsPerFrame = 10`, a single frame can legally
schedule **~70 MB of native allocation + memcpy on the main thread**, all funneled through
`WorldData.GetChunkMapForJob` / `GetChunkLightMapForJob` (`WorldData.cs:234`/`266`). Everything is
disposed one frame later — this is simultaneously the frame-time cost and the memory-churn source.

### Recommendations (in increasing effort)

1. **Pool the job arrays.** All arrays are fixed-size (32,768). A pool of
   `NativeArray<uint>` / `NativeArray<ushort>` (Persistent, reused, cleared on rent) removes the
   alloc/dispose churn with no architecture change. Note: the disposal-chain pattern
   (`disposalHandles` in `ScheduleMeshing`) must become "return to pool on completion" instead of
   `Dispose(handle)`.
2. **Copy only what jobs need.** Meshing needs a 1-voxel shell from each neighbor, not 8 full
   volumes — border slabs are ~1/16th the data. Lighting BFS legitimately reads deeper into
   cardinal neighbors, but corner neighbors (NE/SE/SW/NW) are only touched along a 1×128 column.
3. **Long term:** store canonical voxel/light data in persistent native memory per chunk so jobs
   read it directly (with a generation/version guard), eliminating schedule-time copies entirely.
   This touches the whole pipeline — consult `chunk-lifecycle` invariants before attempting.

> **Impact Analysis:**
> - **Effort:** 🟢 Low (pooling) → 🔴 High (persistent native storage).
> - **Risk:** 🟡 Medium — pooled arrays must be correctly cleared/returned; disposal-chain rework.
> - **Benefit:** 🟢 High — directly attacks the largest per-frame main-thread cost and the
    > native-memory churn feeding the cascading failure.

### ✅ Implemented (2026-06-11): Recommendation 1 — job array pooling

- **`Helpers/ChunkJobArrayPool.cs`** (new): pools the fixed-size full-chunk buffers
  (`NativeArray<uint>` voxel maps, `NativeArray<ushort>` light maps), Persistent allocator,
  uninitialized on rent. Retention is capped at 64 buffers per type (≈ 12 MB steady-state) so a
  backlog spike does not permanently pin its peak memory. **Contract:** rented buffers are NOT
  cleared — fill methods must write every element.
- **`WorldData.FillChunkMapForJob` / `FillChunkLightMapForJob`** (new): write-every-element fill
  variants that explicitly zero null-section slices (stale pooled data safe). The original
  allocating getters delegate to them and remain for non-pooled callers.
- **`Jobs/Data/MeshingJobData.cs`** (new): replaces the `(JobHandle, MeshDataJobOutput)` tuple in
  `WorldJobManager.MeshJobs` so input buffers survive until completion. The former
  `Dispose(JobHandle)` chain (19 chained disposal jobs per mesh job) is gone; buffers return to
  the pool in `ProcessMeshJobs` after `Handle.Complete()`.
- **`WorldJobManager`**: `ScheduleMeshing` / `ScheduleLightingUpdate` rent+fill instead of
  allocate; `ReleaseMeshingJobInputs` / `ReleaseLightingJobData` return buffers post-completion.
- **⚠ Startup-path exception (post-incident fix):** `ScheduleLightingUpdate` only uses pooled
  buffers for `Allocator.Persistent` callers (`LightingJobData.UsesPooledBuffers`). The startup
  coroutine (`ForceCompleteDataJobsCoroutine`) passes `TempJob` and schedules lighting for the
  entire load area in one sweep (hundreds of jobs × 18 buffers) — far past any sane retention
  cap, so pooling there degraded into a Persistent alloc/free storm that made disk loads appear
  to hang at 100% CPU (first regression report, 2026-06-11). TempJob callers keep the original
  allocate-per-job behavior; `ReleaseLightingJobData` falls back to `Dispose()` for them.
- **Retention cap sizing:** initially 64 buffers/type — far below peak steady-state in-flight
  demand (≤ 32 lighting jobs × 9 + ≤ 20 mesh jobs × 9 ≈ 468 per type), which would also thrash.
  Raised to 512/type (≈ 96 MB absolute worst case; actual retention only reaches the observed
  concurrent peak). Lesson: a job-buffer pool's cap must cover *concurrent in-flight demand*,
  not "a reasonable-looking number".
- **Unchanged:** `LightingJobData.Dispose()` remains for the editor pipeline
  (`EditorChunkPipelineRunner`) and `LightingJobBenchmark`, which manage their own non-pooled data.
- **Behavioral note:** mesh-job input buffers are now held until the main thread processes the
  completed job (previously freed by worker-thread disposal jobs immediately on completion).
  In-flight mesh jobs are capped at 20 by `World.Update`, bounding the additional retention.
- Remaining from §1: recommendation 2 (border-slab copies) and 3 (persistent native storage).

---

## 2. `ApplyLightingJobResult` — full main-thread merge scan per completed job

### Observed

`WorldJobManager.ApplyLightingJobResult` (`WorldJobManager.cs`, ~line 793) runs a full
32,768-iteration loop per completed lighting job (overwriting `section.LightData`, checking
emptiness), **plus** `section.RecalculateCounts(...)` per non-new section. At 32 completions per
frame this exceeds 1M iterations inside `Update()`.

Additionally, the merge *overwrites* the live light map with results computed from a pre-job
snapshot; cross-chunk mods applied to the live data during the job window are knowingly sacrificed
(comment at ~line 836) and rely on edge-check convergence to repair — see §4.

### Recommendation

Move the merge into a Burst job (write into persistent or pooled native staging, then a cheap
swap/merge), or at minimum replace per-voxel loops with `NativeArray<T>.Copy` per section plus a
dirty-section mask emitted by the lighting job so untouched sections are skipped.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium.
> - **Risk:** 🟡 Medium — merge semantics (bit-mask merge to avoid TOCTOU with player block edits)
    > must be preserved exactly.
> - **Benefit:** 🟢 High — removes a large fixed main-thread cost paid per lighting completion.

---

## 3. Cascading failure under load (Symptom 1) — missing backpressure

### Observed

Production is unbounded while consumption is fixed-per-frame:

1. **Unbounded scheduling.** `World.CheckViewDistance` (`World.cs`, ~line 2111) fire-and-forgets
   `LoadOrGenerateChunk` for *every* missing chunk in the load square on every chunk-boundary
   crossing. No cap on in-flight generation jobs, no cancellation when a chunk leaves the radius.
2. **Backlog holds memory.** Completed-but-unprocessed `GenerationJobs` keep their `Map`/`HeightMap`
   native arrays alive until `ProcessGenerationJobs` reaches them, which is budget-limited
   (`maxStructureModsPerFrame`). At sub-10 FPS the backlog physically pins memory.
3. **Chunks behind the player can't unload.** `World.UnloadChunks` (~line 1937) skips any chunk
   with a running job or `HasLightChangesToProcess` / `IsAwaitingMainThreadProcess`, and the
   `wouldStrandNeighbor` check additionally pins neighbors of pending chunks. Freshly generated
   chunks all carry `NeedsInitialLighting` / `HasLightChangesToProcess`; lighting drains at
   ≤ 32 jobs/frame. A fast player therefore leaves a contiguous trail of fully populated, pinned
   chunks the unloader is forbidden to touch. Memory climbs until the run ends.
4. **Fixed per-frame caps invert under load** (as observed in benchmark runs): at 60 FPS,
   32 light jobs/frame = 1,920/s; at 8 FPS it collapses to 256/s — throughput is lowest exactly
   when the backlog is largest. The death spiral is self-reinforcing.
5. **Draw queue trickle.** `Update()` dequeues only **one** chunk from `ChunksToDraw` per frame
   (`World.cs`, ~line 1302) while up to 10 mesh jobs/frame can complete. If this is deliberate
   GPU-upload spreading, it should also be time-budgeted; at low FPS it backs up.

### Recommendations

1. **Cap in-flight generation** (e.g. `2 × JobsUtility.JobWorkerCount`). The spiral iteration order
   already feeds nearest-first; just stop scheduling until completions drain.
2. **Discard at completion for out-of-range chunks.** When a generation job completes for a chunk
   now beyond `unloadDistance`, dispose the result (optionally save) instead of populating it and
   feeding it into the lighting pipeline.
3. **Allow unload of light-pending out-of-range chunks** by persisting their pending columns —
   the machinery already exists (`LightingStateManager.AddPending`,
   `World.PersistOrphanedSunlightColumns`); it is simply not used on the unload-blocked path.
   ⚠ Must respect the flag-pairing and gate-ordering invariants in
   `Architecture/CHUNK_LIFECYCLE_PIPELINE.md`.
4. **Time-based budgets instead of count-based.** Give `ProcessGenerationJobs` / lighting
   scheduling / mesh applies a millisecond budget (Stopwatch) so throughput per *second* stays
   roughly constant regardless of FPS.
5. **Panic gate.** When `GenerationJobs.Count` (or `_chunksNeedingLightWork.Count`) exceeds a
   threshold, stop scheduling new generation entirely until the backlog drains.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — items 1, 2, 4, 5 are localized; item 3 touches pipeline invariants.
> - **Risk:** 🟡 Medium→🔴 High for item 3 (deadlock history; consult `chunk-lifecycle` skill).
> - **Benefit:** 🟢 High — directly eliminates the cascading memory failure mode.

---

## 4. Slow initial world *load* (Symptom 2) — edge-check cascade

### 4.1 Structural cause (confirmed by code reading)

- Every chunk loaded from disk with stable lighting gets `NeedsEdgeCheck = true` **unconditionally**
  (`World.LoadOrGenerateChunk`, `World.cs:772`). A "load" therefore still runs **at least one full
  neighborhood lighting job per chunk** — each paying the §1 copy cost plus a full BFS edge scan —
  for data that was saved in a stable state.
- When such a job completes stable, `ChunkData.RemainingEdgeCheckRounds` (initialized to **2**,
  `ChunkData.cs:139`) re-dirties the chunk *and its 4 cardinal neighbors*
  (`WorldJobManager.TriggerNeighborEdgeChecks`). Each chunk realistically runs ~3+ lighting passes,
  and neighbors ping-pong each other's `HasLightChangesToProcess`.
- `ForceCompleteDataJobsCoroutine` (`World.cs:802`) yields one frame per sweep, so convergence takes
  many sweeps over the whole load area even when everything behaves.

### 4.2 Confirmed stability bug — sunlight, not RGB (Bug 11, fixed June 2026)

The reported symptom — churn until `safetyBreak` triggers — fits a chunk (or set of chunks) whose
lighting job persistently reports `IsStable = false`. This was **confirmed via the `[LightingDiag]`
instrumentation** (§4.3) on a stuck reload: every sweep showed `unstable = <clusterSize>`,
`edgeRecycle = 0`, and a perfectly balanced `eff[sunPl=K, sunRm=K]` — a **sunlight** (not RGB)
removal/re-placement 2-cycle across chunk seams. See [LIGHTING_BUGS.md](../Bugs/LIGHTING_BUGS.md)
Bug 11 for the full mechanism.

- Root cause: a cross-chunk sunlight **removal** mod (`CrossChunkLightModApplier.ComputeSunlight`,
  level 0) was applied unconditionally, force-clearing a seam voxel to 0. When two adjacent chunks
  reloaded mid-darkness-wave remove each other's shared, mutually-supported seam column in the same
  wave, each clobbers the other's freshly re-lit value against a stale snapshot and the pair never
  converges (settling one level below the oracle).
- Fix: `ComputeSunlight` now skips a removal when an in-chunk neighbor independently supports the
  current value (`InChunkSunlightSupport`). Reproduced + guarded by lighting suite scenario **K11a**.
- (The earlier suspicion pointed at the per-channel RGB MAX guards / sunlight uplift guard; the
  instrumentation ruled RGB out — blocklight never participated.)

### 4.3 How to separate 4.1 from 4.2 (do this first)

The timeout path already logs flag counts (`World.cs:920`). Add a cheap per-N-iterations log inside
the Phase 2 loop of `ForceCompleteDataJobsCoroutine`:

- jobs scheduled this sweep,
- count of `IsStable == false` completions,
- the set of coords that keep recycling.

Interpretation: if the **same handful of coords** reschedules every sweep with `IsStable = false`,
it is the RGB stability bug (→ `voxel-debugging` skill workflow). If counts **decay slowly but
monotonically**, it is "just" the edge-check cascade being expensive (§4.1).

### 4.4 Quick win independent of the diagnosis

**Persist a "borders reconciled / lighting stable" bit in the chunk save format** (version bump via
the AOT World Migration system — see `serialization-migration` skill) so that loading a world saved
in a stable state skips edge checks entirely. This turns loads from
O(chunks × lighting passes) into near-pure deserialization.

> **Impact Analysis:**
> - **Effort:** 🟢 Low (instrumentation) / 🟡 Medium (save-format bit + migration).
> - **Risk:** 🟢 Low (instrumentation) / 🟡 Medium (must not skip edge checks for chunks saved
    > *unstable*, e.g. mid-BFS quit; the persisted lighting queues already cover that case).
> - **Benefit:** 🟢 High — initial load time for existing worlds; also reduces total lighting jobs
    > during normal streaming of saved chunks.

---

## 5. Smaller observations

| #   | Finding                                                                 | Location                           | Note                                                                                                                                                                       |
|-----|-------------------------------------------------------------------------|------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| 5.1 | `_chunksToBuildMesh.Remove(chunk)` is O(n) list removal called in loops | `World.cs:2156` (and unload paths) | Quadratic with a large backlog; use swap-back removal or an ordered set keyed by priority.                                                                                 |
| 5.2 | 1-second full fail-safe scan over `worldData.Chunks`                    | `World.cs:1158`                    | Fine today; scales linearly with loaded chunk count. Add a counter for how many chunks it *rescues* — a non-zero rate indicates a dirty-set registration bug being masked. |
| 5.3 | One `ChunksToDraw` dequeue per frame                                    | `World.cs:1302`                    | See §3.5 — make time-budgeted.                                                                                                                                             |
| 5.4 | `WorldTypeRegistry.GetWorldType` uses LINQ `FirstOrDefault`             | `WorldTypeRegistry.cs:24`          | Not a hot path; style-guide consistency only.                                                                                                                              |

---

## 6. Suggested implementation order

1. **§4.3 instrumentation** — confirm or kill the RGB-stability theory before touching lighting code.
2. ✅ ~~**§1.1 pool the job NativeArrays** — low effort, large win, no architecture change.~~ (Done 2026-06-11.)
3. **§3.1 + §3.2 generation in-flight cap + out-of-range discard** — stops the memory spiral.
4. **§3.4 time-based budgets** (+ §3.5 panic gate).
5. **§4.4 "lighting stable" save bit** (serialization migration).
6. **§2 jobified lighting merge**, then **§1.2/§1.3** deeper copy reductions.

---

## 7. Verification

- Before fixes: capture a profiler session (benchmark run at high speed + an initial load of an
  existing world) and record schedule-time copy cost (`GetChunkMapForJob`), `ApplyLightingJobResult`
  self-time, and native memory growth. Store as an ad-hoc baseline per
  `Documentation/Performance/README.md` conventions.
- After each fix: re-run the same scenarios; the cascading-failure scenario should show bounded
  `GenerationJobs.Count` and flat native memory; the load scenario should show near-zero lighting
  jobs for stable saved chunks.
