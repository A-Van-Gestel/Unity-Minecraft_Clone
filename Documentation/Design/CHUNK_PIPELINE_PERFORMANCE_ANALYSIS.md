# Chunk Pipeline Performance Analysis

> Findings from a code-level performance review of the chunk generation → lighting → meshing pipeline,
> focused on two observed symptoms: (1) a cascading memory/FPS failure when the player outruns
> generation, and (2) slow initial world *loading* (not creation) of already-generated chunks.
>
> Status: **Partially implemented.** §1.1 (job NativeArray pooling) shipped 2026-06-11 — see the
> "Implemented" note in §1. §3.1 + §3.2 (generation in-flight cap + out-of-range discard) and §3
> **recommendation 3** (unload light-pending out-of-range chunks via persistence, with CP-5's
> `ChunkUnloadDecision` extraction) shipped 2026-07-21 — see the "Implemented" notes in §3.
> §3.4 (time budgets) + §3.5 (panic gate) — including the §5.3 draw-queue rider — shipped
> 2026-07-23, completing the §3 backpressure family; see the final "Implemented" note in §3.
> §2 and §4 remain open. Each finding includes a recommendation and an Impact Analysis in the
> style of `CODEBASE_IMPROVEMENTS.md`.

**Analyzed:** 2026-06-11, at commit `8f90450` (branch `feat/Modular-World-Generation-&-World-Types`). **Analysis is static (code reading), not yet confirmed by profiler capture.** See §7 for the recommended verification steps before implementing fixes.

Related docs: `Architecture/CHUNK_LIFECYCLE_PIPELINE.md`, `Architecture/LIGHTING_SYSTEM_OVERVIEW.md`,
`Guides/GENERAL_OPTIMIZATION_GUIDE.md`, `Bugs/JOB_SYSTEM_BUGS.md`, `Bugs/LIGHTING_BUGS.md`. Clarity/testability complement: [`CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md`](CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md)
(CP-*, 2026-07-06) — its CP-1 deferral counters instrument this document's §3.3 pinned-trail mechanism, and its CP-5 `ChunkUnloadDecision` extraction is the seam §3's recommendation 3 (unload light-pending chunks via persistence, P-4) will land on.

> **Master backlog:** This document is the *deep-dive* for the chunk pipeline. The single
> at-a-glance backlog of **all** open performance items (including this document's open findings,
> tracked there as `P-1`…`P-6`, plus meshing/rendering/lighting/tick findings from the June 2026
> audit) is `PERFORMANCE_IMPROVEMENTS_REPORT.md`. Note: the report's LI-1 (padded lighting volume)
> trades *against* §1.2 below — benchmark both before committing to either.

---

## Summary (TL;DR)

The single biggest structural cost: **every lighting and meshing job allocates and memcpys ~1.7 MB of full-chunk snapshots on the main thread at schedule time, and merges results back with a full 32,768-voxel main-thread scan at completion time** (§1, §2).

Both observed symptoms are downstream of design gaps rather than tuning problems:

- The **cascading failure** is *unbounded production + fixed-per-frame consumption + unload-blocking lighting flags* (§3).
- The **slow initial load** is *every disk-loaded chunk unconditionally re-running full neighborhood lighting via `NeedsEdgeCheck`, amplified by the 2-round edge-check cascade that re-dirties itself and 4 neighbors* (§4). A never-stabilizing RGB lighting bug is plausible but unproven — instrumentation to separate the two is described in §4.3.

---

## 1. Per-job full-volume copies (biggest available win)

### Observed

`WorldJobManager.ScheduleLightingUpdate` (`WorldJobManager.cs`, ~line 292) allocates **17 NativeArrays per job**:

- 9 full `uint` voxel maps (own + 8 neighbors) at 16×128×16 = 32,768 entries = 128 KB each → 1.125 MB
- 8 full `ushort` light maps at 64 KB each → 0.5 MB
- plus heightmap, queues, mods list

≈ **1.7 MB allocated, zeroed, and section-copied on the main thread per lighting job**.
`ScheduleMeshing` does the same with 19 arrays (9 voxel maps + 9 light maps + section data + output).

With `maxLightJobsPerFrame = 32` and `maxMeshRebuildsPerFrame = 10`, a single frame can legally schedule **~70 MB of native allocation + memcpy on the main thread**, all funneled through
`WorldData.GetChunkMapForJob` / `GetChunkLightMapForJob` (`WorldData.cs:234`/`266`). Everything is disposed one frame later — this is simultaneously the frame-time cost and the memory-churn source.

### Recommendations (in increasing effort)

1. **Pool the job arrays.** All arrays are fixed-size (32,768). A pool of
   `NativeArray<uint>` / `NativeArray<ushort>` (Persistent, reused, cleared on rent) removes the alloc/dispose churn with no architecture change. Note: the disposal-chain pattern (`disposalHandles` in `ScheduleMeshing`) must become "return to pool on completion" instead of
   `Dispose(handle)`.
2. **Copy only what jobs need.** Meshing needs a 1-voxel shell from each neighbor, not 8 full volumes — border slabs are ~1/16th the data. Lighting BFS legitimately reads deeper into cardinal neighbors, but corner neighbors (NE/SE/SW/NW) are only touched along a 1×128 column.
3. **Long term:** store canonical voxel/light data in persistent native memory per chunk so jobs read it directly (with a generation/version guard), eliminating schedule-time copies entirely. This touches the whole pipeline — consult `chunk-lifecycle` invariants before attempting. **Now designed as P-2 — see [`PERSISTENT_CHUNK_STORAGE_P2.md`](PERSISTENT_CHUNK_STORAGE_P2.md)**
   (Layer 1 = move the LI-1 gather to a worker thread over the existing snapshots — ✅ **SHIPPED 2026-06-22**, net-positive, banks the win, see [`Performance/LIGHTING_P2_PHASE1_2026_06_22_BENCHMARK.md`](../Performance/LIGHTING_P2_PHASE1_2026_06_22_BENCHMARK.md); Layer 2 = this rec's zero-copy persistent storage — eliminates the schedule-time *fill* (copy #1) this §1.3 targets, optional/profiler-gated (gate not yet triggered), subsumes P-1, overlaps §2/P-3).

> **Validation prerequisite for recs 2–3 (border slabs = P-1, persistent halo = P-2).** Both change
> *what neighbor data each job receives*, so their "output-preserving" claim hinges on the seam being
> guarded on both consumer paths:
> - **Lighting:** the fill path is already exercised (A1), cross-chunk *brightening* is covered (C1/C2),
>   and cross-chunk *darkening* is now covered by
>   [LIGHTING_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/LIGHTING_VALIDATION_HARNESS_FIDELITY.md)
>   **C3 (B54/B55, CLOSED 2026-06-21)** — keep green.
> - **Meshing:** border-face culling is now covered by
>   [MESHING_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md)
>   **MH-10/MH-11 (B18–B21, CLOSED 2026-06-21)** — keep green; MH-11 (B21) routes the harness through the
>   production `ChunkData.FillJobVoxelMap` so a slab/halo under-copy of the border plane flips it red.

> **Impact Analysis:**
> - **Effort:** 🟢 Low (pooling) → 🔴 High (persistent native storage).
> - **Risk:** 🟡 Medium — pooled arrays must be correctly cleared/returned; disposal-chain rework.
> - **Benefit:** 🟢 High — directly attacks the largest per-frame main-thread cost and the
>   native-memory churn feeding the cascading failure.

### ✅ Implemented (2026-06-11): Recommendation 1 — job array pooling

- **`Helpers/ChunkJobArrayPool.cs`** (new): pools the fixed-size full-chunk buffers (`NativeArray<uint>` voxel maps, `NativeArray<ushort>` light maps), Persistent allocator, uninitialized on rent. Retention is capped at 64 buffers per type (≈ 12 MB steady-state) so a backlog spike does not permanently pin its peak memory. **Contract:** rented buffers are NOT cleared — fill methods must write every element.
- **`WorldData.FillChunkMapForJob` / `FillChunkLightMapForJob`** (new): write-every-element fill variants that explicitly zero null-section slices (stale pooled data safe). The original allocating getters delegate to them and remain for non-pooled callers.
- **`Jobs/Data/MeshingJobData.cs`** (new): replaces the `(JobHandle, MeshDataJobOutput)` tuple in
  `WorldJobManager.MeshJobs` so input buffers survive until completion. The former
  `Dispose(JobHandle)` chain (19 chained disposal jobs per mesh job) is gone; buffers return to the pool in `ProcessMeshJobs` after `Handle.Complete()`.
- **`WorldJobManager`**: `ScheduleMeshing` / `ScheduleLightingUpdate` rent+fill instead of allocate; `ReleaseMeshingJobInputs` / `ReleaseLightingJobData` return buffers post-completion.
- **⚠ Startup-path exception (post-incident fix):** `ScheduleLightingUpdate` only uses pooled buffers for `Allocator.Persistent` callers (`LightingJobData.UsesPooledBuffers`). The startup coroutine (`ForceCompleteDataJobsCoroutine`) passes `TempJob` and schedules lighting for the entire load area in one sweep (hundreds of jobs × 18 buffers) — far past any sane retention cap, so pooling there degraded into a Persistent alloc/free storm that made disk loads appear to hang at 100% CPU (first regression report, 2026-06-11). TempJob callers keep the original
  allocate-per-job behavior; `ReleaseLightingJobData` falls back to `Dispose()` for them.
- **Retention cap sizing:** initially 64 buffers/type — far below peak steady-state in-flight demand (≤ 32 lighting jobs × 9 + ≤ 20 mesh jobs × 9 ≈ 468 per type), which would also thrash. Raised to 512/type (≈ 96 MB absolute worst case; actual retention only reaches the observed concurrent peak). Lesson: a job-buffer pool's cap must cover *concurrent in-flight demand*, not "a reasonable-looking number".
- **Unchanged:** `LightingJobData.Dispose()` remains for the editor pipeline (`EditorChunkPipelineRunner`) and `LightingJobBenchmark`, which manage their own non-pooled data.
- **Behavioral note:** mesh-job input buffers are now held until the main thread processes the completed job (previously freed by worker-thread disposal jobs immediately on completion). In-flight mesh jobs are capped at 20 by `World.Update`, bounding the additional retention.
- Remaining from §1: recommendation 2 (border-slab copies) and 3 (persistent native storage).

---

## 2. `ApplyLightingJobResult` — full main-thread merge scan per completed job

### Observed

`WorldJobManager.ApplyLightingJobResult` (`WorldJobManager.cs`, ~line 793) runs a full 32,768-iteration loop per completed lighting job (overwriting `section.LightData`, checking emptiness), **plus** `section.RecalculateCounts(...)` per non-new section. At 32 completions per frame this exceeds 1M iterations inside `Update()`.

Additionally, the merge *overwrites* the live light map with results computed from a pre-job snapshot; cross-chunk mods applied to the live data during the job window are knowingly sacrificed (comment at ~line 836) and rely on edge-check convergence to repair — see §4.

### Recommendation

Move the merge into a Burst job (write into persistent or pooled native staging, then a cheap swap/merge), or at minimum replace per-voxel loops with `NativeArray<T>.Copy` per section plus a dirty-section mask emitted by the lighting job so untouched sections are skipped.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium.
> - **Risk:** 🟡 Medium — merge semantics (bit-mask merge to avoid TOCTOU with player block edits)
>   must be preserved exactly.
> - **Benefit:** 🟢 High — removes a large fixed main-thread cost paid per lighting completion.

---

## 3. Cascading failure under load (Symptom 1) — missing backpressure

### Observed

Production is unbounded while consumption is fixed-per-frame:

1. **Unbounded scheduling.** `World.CheckViewDistance` (`World.cs:2864`) fire-and-forgets
   `LoadOrGenerateChunk` for *every* missing chunk in the load square on every chunk-boundary crossing. No cap on in-flight generation jobs, no cancellation when a chunk leaves the radius. *(Addressed by the §3.1 fix below.)*
2. **Backlog holds memory.** Completed-but-unprocessed `GenerationJobs` keep their `Map`/`HeightMap`
   native arrays alive until `ProcessGenerationJobs` reaches them, which is budget-limited (`maxStructureModsPerFrame`). At sub-10 FPS the backlog physically pins memory.
3. **Chunks behind the player can't unload.** `World.UnloadChunks` (`World.cs:2702`) skips any chunk with a running job or `HasLightChangesToProcess` / `IsAwaitingMainThreadProcess`, and the
   `wouldStrandNeighbor` check additionally pins neighbors of pending chunks. Freshly generated chunks all carry `NeedsInitialLighting` / `HasLightChangesToProcess`; lighting drains at ≤ 32 jobs/frame. A fast player therefore leaves a contiguous trail of fully populated, pinned chunks the unloader is forbidden to touch. Memory climbs until the run ends.
4. **Fixed per-frame caps invert under load** (as observed in benchmark runs): at 60 FPS, 32 light jobs/frame = 1,920/s; at 8 FPS it collapses to 256/s — throughput is lowest exactly when the backlog is largest. The death spiral is self-reinforcing.
5. **Draw queue trickle.** `Update()` dequeues only **one** chunk from `ChunksToDraw` per frame (`World.cs:2087`) while up to 10 mesh jobs/frame can complete. If this is deliberate GPU-upload spreading, it should also be time-budgeted; at low FPS it backs up.

### Recommendations

1. **Cap in-flight generation** (e.g. `2 × JobsUtility.JobWorkerCount`). The spiral iteration order already feeds nearest-first; just stop scheduling until completions drain.
2. **Discard at completion for out-of-range chunks.** When a generation job completes for a chunk now beyond `unloadDistance`, dispose the result (optionally save) instead of populating it and feeding it into the lighting pipeline.
3. **Allow unload of light-pending out-of-range chunks** by persisting their pending columns — the machinery already exists (`LightingStateManager.AddPending`,
   `World.PersistOrphanedSunlightColumns`); it is simply not used on the unload-blocked path. ⚠ Must respect the flag-pairing and gate-ordering invariants in
   `Architecture/CHUNK_LIFECYCLE_PIPELINE.md`.
4. **Time-based budgets instead of count-based.** Give `ProcessGenerationJobs` / lighting scheduling / mesh applies a millisecond budget (Stopwatch) so throughput per *second* stays roughly constant regardless of FPS.
5. **Panic gate.** When `GenerationJobs.Count` (or the light scheduler's
   `ReadyCount + WaitingCount`, see `LightWorkScheduler`) exceeds a threshold, stop scheduling new generation entirely until the backlog drains.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — items 1, 2, 4, 5 are localized; item 3 touches pipeline invariants.
> - **Risk:** 🟡 Medium→🔴 High for item 3 (deadlock history; consult `chunk-lifecycle` skill).
> - **Benefit:** 🟢 High — directly eliminates the cascading memory failure mode.

### ✅ Implemented (2026-07-21): Recommendations 1 (in-flight cap) + 2 (out-of-range discard)

The two production-side backpressure items that do **not** touch the deadlock-prone unload path. Recommendation 3 shipped 2026-07-21 and recommendations 4 + 5 shipped 2026-07-23 — see the
"Implemented" notes below. On-disk format unchanged (no migration).

- **§3.1 in-flight generation cap.** Generation is now driven by a per-frame drain instead of edge-triggered fire-and-forget, mirroring the existing mesh in-flight cap:
    - `World.CheckViewDistance` no longer fires `LoadOrGenerateChunk` directly — it rebuilds a nearest-first `_generationRequestQueue` (+ `_pendingGenerationRequests` dedup set) each boundary crossing.
    - New `World.DrainGenerationRequests()` (called each frame right after `ProcessGenerationJobs`)
      admits queued requests only while `GenerationJobs.Count + admittedThisFrame <
    settings.maxInFlightGenerationJobs`. `IsLoading` moved to admission time. The per-frame drain (not a `break` inside the edge-triggered spiral) is required: a naive spiral cap would leave permanent holes when the player stops after a crossing, since `CheckViewDistance` does not re-run until the next crossing.
    - **Cap value (OM-1):** new `settings.maxInFlightGenerationJobs` (default 32), RAM-scaled by
      `DeviceCalibration` in the same memory-cap taxonomy as `maxInFlightMeshJobs`.
    - **Soft cap:** the gate counts tracked jobs + this frame's admissions only; a disk- *miss* chunk can briefly overshoot while awaiting its (sub-frame) disk probe. Overshoot is bounded, holds no job buffers, and disk- *hit* chunks never become generation jobs so they never count. The startup path (`ForceCompleteDataJobsCoroutine`) is unaffected — `Update` early-returns until
      `_isWorldLoaded`, so it schedules directly, bypassing the cap (avoids the §1.1 pooling incident).
- **§3.2 out-of-range discard.** `WorldJobManager.ProcessGenerationJobs` discards a completed job whose chunk is now beyond the unload boundary (`LoadDistance + World.UnloadDistanceBuffer`, a shared constant so the discard boundary can never drift inside the unload boundary):
  `ReleaseGenerationJobData` + skip populate/structures/lighting. No save — unmodified generation output is seed-regenerable. The unpopulated placeholder is reclaimed by `UnloadChunks`.
    - **Runs inside the HF-2 fault-isolation `try`** so a release fault can't abort the whole pass.
    - **Gated on `EnablePersistence`:** when unloading is disabled (`keepChunksInMemory`) `UnloadChunks`
      never reclaims the placeholder, so the chunk is populated normally instead of stranding a hole.
    - **Clears `IsLoading`:** otherwise a chunk that re-enters load range before `UnloadChunks` reclaims it would be blocked from re-enqueue by `CheckViewDistance`'s `!IsLoading` guard — a permanent hole.
    - **Disk-load decoupling (§3.1 drain):** the cap uses two separate bounds (`GenerationJobs.Count < cap` **and** `admittedThisFrame < cap`, not their sum) so saved-region disk loads are not throttled behind new-terrain generation. Because admitted disk-miss chunks call
      `ScheduleGeneration` only in a later-frame continuation, each frame admits up to `cap` new requests regardless of how many prior admissions are still resolving — so the worst-case tracked-job overshoot is **disk-latency-dependent** (≈ `cap × disk-miss-probe-latency-in-frames`; ~2×cap on fast storage, higher on slow flash). A latency-independent hard ceiling would require a persistent in-flight counter, deliberately declined in favor of this soft cap (overlaps SU-2).

### ✅ Implemented (2026-07-21): Recommendation 3 — unload light-pending out-of-range chunks via persistence

The 🔴-rated unload-path item (§3.3 pinned-trail fix), landed on **CP-5's `ChunkUnloadDecision` seam** as the design anticipated. On-disk format unchanged (no migration — `NeedsInitialLighting` is already in the chunk format and `pending_lighting.bin` already exists). Recommendations 4 (time budgets) and 5 (panic gate)
shipped 2026-07-23 — see the next "Implemented" note.

- **CP-5 extraction (prerequisite).** The monolithic deferral block in `World.UnloadChunks` became the pure, truth-table-baselined `Helpers/ChunkUnloadDecision.Evaluate(in ChunkUnloadFacts)`; `UnloadChunks` gathers facts and switches on the result. New suite `Minecraft Clone/Dev/Validate Chunk Unload Decision` (9 baselines).
- **The fix (rec 3).** Two coordinated changes:
    - **Strand guard narrowed to in-range neighbors.** The §9.6 strand scan now ignores a would-be-stranded neighbor that is *itself* beyond the unload distance (it is being reclaimed too, so stranding it is harmless). The guard still defers for in-range neighbors — the deadlock stays closed (`Architecture/CHUNK_LIFECYCLE_PIPELINE.md` §9.6).
    - **`UnloadPersistLightPending` arm.** An out-of-range chunk pinned only by its own pending/initial lighting (which can never complete — missing-neighbor gate) forces `NeedsInitialLighting = true` (full re-light on reload, captured by the synchronous save snapshot), persists its pending sunlight columns via the existing
      `LightingStateManager.AddPending` / `World.PersistOrphanedSunlightColumns`, and unloads instead of deferring forever. Precedence `job → in-range-strand → persist-light → unload` keeps strand above persist so a chunk an in-range neighbor needs always defers.
- **Measured (CP-1 counters, before → after, fly-out soak):** total loaded **1096 → 363**; beyond-unload *unreclaimable* **743 → ~0–2**; `Deferred — light` **308 → 0**; `Deferred — strand` **395 → 0–2** (the residual is a bounded, self-resolving boundary shell around a stuck buffer-band chunk — see the pipeline doc §9.6). No artifacts; durability (edit → unload → reload) confirmed. Full evidence: CP-5 Amended block in
  [`CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md`](CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md) §7.

### ✅ Implemented (2026-07-23): Recommendations 4 (time-based budgets) + 5 (panic gate) + the §5.3 draw-queue rider

The final two items of the §3 backpressure family. Both ship default-ON behind rollback flags (`Settings.enablePipelineTimeBudgets` / `enableGenerationPanicGate`; flag off restores the exact legacy behavior by construction). Runtime scheduling only — on-disk format unchanged.

- **§3.4 time budgets — rate quota + ms ceiling, not a pure Stopwatch budget.** A Stopwatch-only budget keeps throughput ∝ FPS (fixed work/frame × frames/sec), so it cannot deliver this recommendation's stated goal. Each budgeted pass instead gets **two** cooperating limits (`Helpers/PipelinePassBudget`, pure + suite-pinned):
    - **Rate quota** — `ceil(existing per-frame cap × unscaledDeltaTime × 60)`, clamped [1, 8×cap]:
      the historical caps become per-second rates anchored at their 60 FPS tuning point (at exactly 60 FPS the quota IS the cap; an epsilon guards float ceil noise — load-bearing for 104 of the 128 in-range cap values). This is what undoes observed item 4's inversion.
    - **Stopwatch ms ceiling** — bounds the pass's main-thread slice per frame (hitch guard). Ceilings are Settings fields (light schedule 8 ms, mesh schedule 6 ms, gen process 6 ms, mesh apply 4 ms, draw 2 ms), deliberately **not** DeviceCalibration-scaled: frame-time targets are device-independent, and device scaling already flows in through the calibrated count caps that anchor the quotas.
    - **Budgeted passes:** the lighting ready-set scan and mesh scheduling loop (quota + ceiling — the break keeps the §9.1 semantics exactly: the un-served remainder stays READY / stays queued), `ProcessGenerationJobs` and `ProcessMeshJobs` (ceiling only, checked between jobs; deferred completions stay enrolled — the pre-existing budget-retry contract). The window is parameter-passed with `default` = unbudgeted, so the startup coroutine (SU-1/SU-2 territory)
      is untouched. The **§5.3 rider** replaces the one-`ChunksToDraw`-dequeue-per-frame trickle with a ceiling-bounded drain (min 1/frame preserved). `ProcessLightingJobs`' merge pass is deliberately NOT budgeted (§2/P-3 owns it).
- **§3.5 panic gate — on the §3.1 admission seam, signal updated.** This document's
  `GenerationJobs.Count` signal predates §3.1, which already caps it — the live overload signal is the downstream lighting backlog. `Helpers/GenerationPanicGate` (pure hysteresis decision, suite-pinned) evaluates **`LightWorkScheduler.ReadyCount`** (ready only: the parked frontier ring — measured 104 ≈ 8×LoadDistance+4 at steady state — would poison a ready+waiting threshold)
  against close ≥ 256 / reopen ≤ 128 (Settings fields) inside `World.DrainGenerationRequests`, pausing **admissions only** — the request queue and `CheckViewDistance` spiral are untouched (the §3.1 permanent-holes lesson), so a closed gate can never strand holes. Transitions log unconditionally; the CP-1 HUD block gained gate state / close count / closed frames / ready + waiting backlog / gen queue + in-flight rows.
- **Guard suite:** `Minecraft Clone/Dev/Validate Pipeline Backpressure` (B1–B6: quota identity at the 60 FPS reference, dt scaling, clamps, unbudgeted-default window contract, gate truth table, hysteresis walk; prove-red by temporary mutation on the epsilon and the closed-arm threshold). Registry 16 suites / Validate All 335.
- **Review-round hardening (same-day `/code-review`, 7 of 8 findings fixed, 1 accepted):**
  (1) new `Settings.maxInFlightLightingJobs` (64, non-UI) re-checked every scan iteration — the engine previously had NO in-flight lighting bound, so a hitch-scaled quota (8×cap) with the ms ceiling slider at 0 could rent ~8× peak pooled buffers in one frame (the §1.1 incident class); (2) rotating-start snapshot iteration in `ProcessGenerationJobs`/`ProcessMeshJobs` — a budget break over raw Dictionary order + free-slot reuse could starve an old completed job indefinitely, now bounded to ≤ in-flight-count frames; (3) panic thresholds sanitized
  (`closeAt ≥ 1`, `reopenAt < closeAt`) so a degenerate persisted band cannot flip the gate (and log) every frame; (4) the light-schedule window now starts AFTER the ~1s fail-safe scan (was: scan frames ate the ceiling → 1 Hz throughput dip); (5) the draw drain counts only real draws toward its min-1 guarantee, and its 0 setting now means "no ceiling" like every other budget slider (the legacy 1/frame trickle is the master flag's off state). Accepted (no change): stale ready-set entries can phantom-close the gate for ~1–2 frames after a mass unload —
  self-resolving (stale laundering runs at ~µs/entry inside the scan window) and witnessed benign in the acceptance session.
- **Review round 2 (planned pass, 9 findings — 8 fixed, 1 provenance-corrected):**
  (1) reopen threshold floored at 0 (`Mathf.Clamp(reopen, 0, closeAt−1)`) — a negative persisted value could wedge a closed gate shut forever; (2) the gate's signal is now `ReadyCount` **sampled at the end of the previous frame's lighting scan** — the live count is spiked ~1×/s by the fail-safe `PromoteAll` (whole frontier ring → ready for one scan), which at LoadDistance ≥ ~32 exceeds the close threshold and would oscillate the gate at 1 Hz; (3) rotation cursors reduced modulo the key count per pass (the `cursor + index` sum could overflow int before
  the old guard fired — latent, ~414 days uptime); (4) the in-flight lighting cap and (5) the pass rotation now apply **only when a budget is live** (`enablePipelineTimeBudgets` / `window.HasBudget`) — worker saturation can exceed any fixed in-flight value even under legacy, and rotation reordered legacy's structure-mods deferral, so both contaminated the flag-off rollback/A-B leg; (6) `Window.Expired` short-circuits on a zero budget before reading the timestamp (unbudgeted hot loops paid a per-iteration QPC call); (7) the five budget sliders' UI ranges
  floor at 0.5 ms — the 0 = no-ceiling discontinuity contradicted "lower = smoother" and is now a settings-file-only expert value; (8) HUD gate state resets to OPEN when the gate feature (or lighting) is toggled off mid-session (was: stale CLOSED forever). Provenance-corrected: `World.prefab`'s stale budget-field block was Unity's own serialization from a pre-reorder compile, not a hand edit — re-serialized canonically via the Editor.
- **Review round 3 (planned pass, 7 findings — 6 fixed, 1 claim-softened):**
  (1) the post-scan gate sample can still catch the PromoteAll spike when a §3.4 budget break ends the scan before the promoted ring is re-parked → the close arm now additionally requires the sample to stay high for **3 consecutive frames** (composes with round 2's sample; a 1–2 frame spike never closes, sustained-high = genuine saturation; reopen stays immediate); (2) `ComputeQuota` clamps the cap below `int.MaxValue / 8` (an absurd persisted cap flipped the quota clamp ceiling negative — scheduling would halt; suite-pinned); (3) `StartWindow` floors
  tiny positive budgets to `MinBudgetMs` 0.5 ms via `SanitizeBudgetMs` (a hand-edited 0.001 ms file value could expire the window before a pass's first check every frame and wedge the mesh-apply pass at its in-flight cap; suite-pinned); (4) the rotation fairness claim softened to probabilistic (Dictionary key-churn defeats a hard "≤ count frames" bound; the deterministic-starvation fix stands — a strict bound would need FIFO service order, deliberately not built); (5–7, temp harness) fill predicate now includes the lighting/mesh in-flight jobs and draw
  queue (omitting the tail the budgets defer biased the A/B toward ON), all metrics arm at teleport **arrival** (the hold hitch contaminated max-frame/hitch counts), and coordinate conversion goes through `ChunkMath.VoxelToChunk`/`CHUNK_WIDTH`.
- **Measured (editor screening A/B, same build, flag-switched;** see
  [`Performance/CHUNK_PIPELINE_P4_BACKPRESSURE_2026-07-23_BENCHMARK.md`](../Performance/CHUNK_PIPELINE_P4_BACKPRESSURE_2026-07-23_BENCHMARK.md)**):**
  legacy's 729-chunk teleport fill runs at 13.3 FPS with 67% hitch frames (its passes ARE the frame); budgets hold 29.1 FPS / 11% hitch frames for the same fill at a ×1.69 fill-latency cost (knob-tunable). Externally-imposed ÷1.94 FPS slows a budgeted fill only ×1.53 (quota compensating) vs legacy's fully proportional collapse; at deep caps (5 FPS) the absolute-ms ceilings bind before the quota and scaling reverts to proportional — known limitation, a frame-fraction ceiling is the natural refinement if that regime ever matters. Panic gate:
  16 close/drain/reopen cycles witnessed across startup + teleport spam, zero overhead when open, and a 729/729 no-holes audit on returning to a previously gated area. Verdict: **GO (screening)**
  — IL2CPP capture recommended before the rollback flags are retired.

---

## 4. Slow initial world *load* (Symptom 2) — edge-check cascade

### 4.1 Structural cause (confirmed by code reading)

- Every chunk loaded from disk with stable lighting gets `NeedsEdgeCheck = true` **unconditionally**
  (`World.LoadOrGenerateChunk`, `World.cs:772`). A "load" therefore still runs **at least one full neighborhood lighting job per chunk** — each paying the §1 copy cost plus a full BFS edge scan — for data that was saved in a stable state.
- When such a job completes stable, `ChunkData.RemainingEdgeCheckRounds` (initialized to **2**,
  `ChunkData.cs:139`) re-dirties the chunk *and its 4 cardinal neighbors*
  (`WorldJobManager.TriggerNeighborEdgeChecks`). Each chunk realistically runs ~3+ lighting passes, and neighbors ping-pong each other's `HasLightChangesToProcess`.
- `ForceCompleteDataJobsCoroutine` (`World.cs:802`) yields one frame per sweep, so convergence takes many sweeps over the whole load area even when everything behaves.

### 4.2 Confirmed stability bug — sunlight, not RGB (Bug 11, fixed June 2026)

The reported symptom — churn until `safetyBreak` triggers — fits a chunk (or set of chunks) whose lighting job persistently reports `IsStable = false`. This was **confirmed via the `[LightingDiag]`
instrumentation** (§4.3) on a stuck reload: every sweep showed `unstable = <clusterSize>`,
`edgeRecycle = 0`, and a perfectly balanced `eff[sunPl=K, sunRm=K]` — a **sunlight** (not RGB)
removal/re-placement 2-cycle across chunk seams. See [LIGHTING_BUGS.md](../Bugs/LIGHTING_BUGS.md)
Bug 11 for the full mechanism.

- Root cause: a cross-chunk sunlight **removal** mod (`CrossChunkLightModApplier.ComputeSunlight`, level 0) was applied unconditionally, force-clearing a seam voxel to 0. When two adjacent chunks reloaded mid-darkness-wave remove each other's shared, mutually-supported seam column in the same wave, each clobbers the other's freshly re-lit value against a stale snapshot and the pair never converges (settling one level below the oracle).
- Fix: `ComputeSunlight` now skips a removal when an in-chunk neighbor independently supports the current value (`InChunkSunlightSupport`). Reproduced + guarded by lighting suite scenario **K11a**.
- (The earlier suspicion pointed at the per-channel RGB MAX guards / sunlight uplift guard; the instrumentation ruled RGB out — blocklight never participated.)

### 4.3 How to separate 4.1 from 4.2 (do this first)

The timeout path already logs flag counts (`World.cs:920`). Add a cheap per-N-iterations log inside the Phase 2 loop of `ForceCompleteDataJobsCoroutine`:

- jobs scheduled this sweep,
- count of `IsStable == false` completions,
- the set of coords that keep recycling.

Interpretation: if the **same handful of coords** reschedules every sweep with `IsStable = false`, it is the RGB stability bug (→ `voxel-debugging` skill workflow). If counts **decay slowly but monotonically**, it is "just" the edge-check cascade being expensive (§4.1).

### 4.4 Quick win independent of the diagnosis

**Persist a "borders reconciled / lighting stable" bit in the chunk save format** (version bump via the AOT World Migration system — see `serialization-migration` skill) so that loading a world saved in a stable state skips edge checks entirely. This turns loads from O (chunks × lighting passes) into near-pure deserialization.

> **Impact Analysis:**
> - **Effort:** 🟢 Low (instrumentation) / 🟡 Medium (save-format bit + migration).
> - **Risk:** 🟢 Low (instrumentation) / 🟡 Medium (must not skip edge checks for chunks saved
>   *unstable*, e.g. mid-BFS quit; the persisted lighting queues already cover that case).
> - **Benefit:** 🟢 High — initial load time for existing worlds; also reduces total lighting jobs
>   during normal streaming of saved chunks.

---

## 5. Smaller observations

| #      | Finding                                                                     | Location                      | Note                                                                                                                                                                                                   |
|--------|-----------------------------------------------------------------------------|-------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| 5.1 ✅ | ~~`_chunksToBuildMesh.Remove(chunk)` is O(n) list removal called in loops~~ | `World.cs` (and unload paths) | **DONE (MT-1, 2026-07-01):** replaced with `Helpers/MeshBuildQueue.cs` (pooled intrusive linked list + coord→slot map) — enqueue/remove/drain all O(1). See `PERFORMANCE_IMPROVEMENTS_REPORT.md` MT-1. |
| 5.2    | 1-second full fail-safe scan over `worldData.Chunks`                        | `World.cs:1158`               | Fine today; scales linearly with loaded chunk count. Add a counter for how many chunks it *rescues* — a non-zero rate indicates a dirty-set registration bug being masked.                             |
| 5.3 ✅ | ~~One `ChunksToDraw` dequeue per frame~~                                    | `World.cs` (Update, step 8)   | **DONE (2026-07-23):** ceiling-bounded drain (`drawApplyBudgetMs`, min 1/frame preserved) — shipped with the §3.4 budgets, see the final "Implemented" note in §3.                                     |
| 5.4    | `WorldTypeRegistry.GetWorldType` uses LINQ `FirstOrDefault`                 | `WorldTypeRegistry.cs:24`     | Not a hot path; style-guide consistency only.                                                                                                                                                          |

---

## 6. Suggested implementation order

1. **§4.3 instrumentation** — confirm or kill the RGB-stability theory before touching lighting code.
2. ✅ ~~**§1.1 pool the job NativeArrays** — low effort, large win, no architecture change.~~ (Done 2026-06-11.)
3. ✅ ~~**§3.1 + §3.2 generation in-flight cap + out-of-range discard** — stops the memory spiral.~~ (Done 2026-07-21 — see the "Implemented" note in §3.)
4. ✅ ~~**§3.4 time-based budgets** (+ §3.5 panic gate).~~ (Done 2026-07-23 — see the final "Implemented" note in §3.)
5. **§4.4 "lighting stable" save bit** (serialization migration).
6. **§2 jobified lighting merge**, then **§1.2/§1.3** deeper copy reductions.

---

## 7. Verification

- Before fixes: capture a profiler session (benchmark run at high speed + an initial load of an existing world) and record schedule-time copy cost (`GetChunkMapForJob`), `ApplyLightingJobResult`
  self-time, and native memory growth. Store as an ad-hoc baseline per
  `Documentation/Performance/README.md` conventions.
- After each fix: re-run the same scenarios; the cascading-failure scenario should show bounded
  `GenerationJobs.Count` and flat native memory; the load scenario should show near-zero lighting jobs for stable saved chunks.
