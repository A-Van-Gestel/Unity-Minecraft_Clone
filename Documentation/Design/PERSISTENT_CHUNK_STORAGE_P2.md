# P-2 — Worker-Thread Gather & (optional) Persistent Native Chunk Storage

> **Status: Layer 1 (Phase 1) — IMPLEMENTED & SHIPPED (2026-06-22, commit `e3e1635`). Layer 2 — Design
> (proposed), profiler-gated.** Promotes the backlog finding `PERFORMANCE_IMPROVEMENTS_REPORT.md` → P-2
> (and `CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md` §1.3) into a full design, and is the home for the validated
> **LI-1** halo-padded lighting layout —
> [`Performance/LIGHTING_LI1_2026_06_22_BENCHMARK.md`](../Performance/LIGHTING_LI1_2026_06_22_BENCHMARK.md):
> LI-1's branch-free BFS is a validated **2.4–3× in-job win**, but its **on-demand gather ran on the main
> thread** (~305 µs/job), making it net-negative standalone. **Phase 1 moved that gather onto the worker
> thread and the layout shipped net-positive** (−34 % to −50 % vs LI-1 POST full-timing) —
> [`Performance/LIGHTING_P2_PHASE1_2026_06_22_BENCHMARK.md`](../Performance/LIGHTING_P2_PHASE1_2026_06_22_BENCHMARK.md).

> **This design is TWO layers, shipped and decided independently:**
> - **Layer 1 — Worker-thread gather (✅ SHIPPED 2026-06-22, commit `e3e1635`).** Move the LI-1 gather off
    > the main thread into the job, fed by the **existing snapshot maps**. Delivered the net-positive LI-1
    > win with **no new concurrency surface** and **no storage change**. This was the primary deliverable —
    > now done; result in
    > [`Performance/LIGHTING_P2_PHASE1_2026_06_22_BENCHMARK.md`](../Performance/LIGHTING_P2_PHASE1_2026_06_22_BENCHMARK.md).
> - **Layer 2 — Persistent native cores + zero-copy reads (OPTIONAL, profiler-gated, 🔴 HIGH risk).**
    > Additionally removes the schedule-time *fill* copy by letting jobs read chunk storage in place. This
    > reopens a documented architectural constraint (`LIGHTING_SYSTEM_OVERVIEW.md` §4.3) and a large
    > lifetime/consistency surface (§6). **Do not start Layer 2 until a profiler capture proves Layer 1's
    > remaining copy is the bottleneck.**

> ⚠️ **Touches the chunk generation → lighting → meshing pipeline (three historical deadlocks).** Consult
> the `chunk-lifecycle` skill and `Architecture/CHUNK_LIFECYCLE_PIPELINE.md`. Layer 1 does **not** change
> the pipeline's lifetime/gate model; **Layer 2 does** (§6 is load-bearing).

---

## 1. Problem — the copy tax

Canonical voxel and light data live in **managed arrays inside pooled `ChunkSection` objects** (`uint[]
voxels` 16 KB, `ushort[] LightData` 8 KB; a null section slot == all-air; `byte[] SectionUniformSkyLevel`
compacts a uniform-sky section to 1 byte). See `Architecture/DATA_STRUCTURES.md` §2.1 and
`Architecture/LIGHTING_SYSTEM_OVERVIEW.md` §1.1. Burst cannot read managed memory, so every lighting job
**copies** data into native buffers at schedule time, on the main thread. Post-LI-1 the lighting path pays
this **twice**:

1. **Fill** (`WorldData.FillChunkMapForJob`/`FillChunkLightMapForJob` → `ChunkData.FillJobVoxelMap`/
   `FillJobLightMap`): managed sections → **9 full-chunk NativeArrays** (own + 8 neighbors), voxel + light
   ≈ **1.7 MB / job** (`CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md` §1).
2. **Gather** (LI-1, `ChunkMath.GatherPaddedVoxels`/`GatherPaddedLight`): the 9 maps → one
   `PADDED_LIGHTING_VOLUME` (20×128×20) halo-padded buffer. Measured ≈ **305 µs/job on the main thread**.

The merge back (`ChunkData.ApplyJobLightMap`) is a full 32,768-voxel main-thread scan per completed job
(`CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md` §2; P-3).

**The two copies are separable wins at very different risk.** Copy #2 (the gather) is what made LI-1
net-negative, and it is cheap to relocate — **Layer 1**. Copy #1 (the fill) is the larger churn but
removing it requires jobs to read storage in place — **Layer 2**, which carries real concurrency risk.

---

## 2. Goals & non-goals

**Goals**

- **Layer 1:** make LI-1 net-positive by moving the gather to a worker thread; keep **bit-identical**
  output (same hard bar as LI-1 — any divergence re-dirties the edge-check cascade on old saves).
- **Layer 2 (if pursued):** additionally eliminate the schedule-time fill; jobify the merge (overlaps
  **P-3**); stay bit-identical; be 3D-key / halo ready for cubic chunks (`WORLD_SCALING_ANALYSIS.md` §4.3).

**Non-goals**

- **No save-format change** as a *goal* — but see §6.4: Layer 2's native storage **does** touch the
  serializer code path even if the on-wire bytes are unchanged. Layer 1 changes nothing here.
- Not the backpressure/cascade fix (P-4 / §3 of the pipeline doc). Layer 1 is independent of P-4; only
  Layer 2 interacts with it (§7).
- Not palettes (`CHUNK_PALETTE_MAPPING`) — complementary; §6.5.

---

## 3. Layer 1 — Worker-thread gather (recommended)

> **✅ IMPLEMENTED & SHIPPED (2026-06-22, commit `e3e1635`). Authoritative implemented description:
> [`Architecture/LIGHTING_SYSTEM_OVERVIEW.md`](../Architecture/LIGHTING_SYSTEM_OVERVIEW.md) §3.3** (the
> worker-thread gather is now part of the lighting pipeline's documented behavior). This section is retained
> as the *design rationale* for that change; for "how it works now," read §3.3. Benchmark:
> [`Performance/LIGHTING_P2_PHASE1_2026_06_22_BENCHMARK.md`](../Performance/LIGHTING_P2_PHASE1_2026_06_22_BENCHMARK.md).

**Root cause restated:** LI-1's gather is not slow — it is slow *serialized on the main thread*. The BFS
that consumes it is 2.4–3× faster. Move the gather into the job and the main thread pays only what PRE-LI-1
paid (the fill), while the worker gets the faster BFS. This **is** the LI-1 payoff, at near-zero risk.

### 3.1 Mechanism

Today the main thread rents a padded buffer from `ChunkJobArrayPool.RentPaddedVoxels/RentPaddedLight`
(length `PADDED_LIGHTING_VOLUME`) **and** fills it via `GatherPaddedVoxels/Light` before scheduling. The
change:

- The main thread still rents the 9 snapshot maps (unchanged fill, copy #1) **and** the padded buffers —
  but leaves the padded buffers **unfilled**.
- It passes into `NeighborhoodLightingJob`: the 9 `[ReadOnly]` snapshot maps (center + 8 neighbors) **plus**
  the writable padded buffers as the job's scratch/output.
- `Execute()` **first** runs the gather (center + 8 neighbors → padded volume), **then** the existing
  BFS over that padded volume — exactly as LI-1 does today, only the gather call site moves from the main
  thread into `Execute()`.

Everything downstream is unchanged: the BFS does its RMW read-back through the padded halo cells (the
writable padded buffer is the scratch that read-back requires — see §3.3), the center result is merged via
`ApplyJobLightMap`, and cross-chunk halo writes harvest into `CrossChunkLightMods` as before.

### 3.2 Why this is race-free and bit-identical

The 9 maps are still **point-in-time snapshots** copied before the job runs, so there is **no** live-read
race, no lifetime pin, no defer — none of §6. The padded buffer is owned by the job (rented on the main
thread, returned on completion, as today). The only thing that moved is *where the gather runs*; the inputs
and the gather logic are identical, so the output is bit-identical by construction. Validate with the LI-1
prove-red (corrupt the in-`Execute` gather → exactly the 11 seam baselines redden → revert → green).

This also stays inside the documented Unity constraint: `LIGHTING_SYSTEM_OVERVIEW.md` §4.3 keeps the
snapshot+merge model precisely because Unity job-safety forbids reading a neighbor's live array while
another job writes it. Layer 1 keeps snapshots, so §4.3 is honored.

### 3.3 Implementation notes (do not under-scope)

- **`GatherPadded<T>` is NOT Burst-compatible as written.** It is a managed `Helpers.ChunkMath` open
  generic that calls `NativeArray<T>.Copy` (its own doc-comment notes it is main-thread, non-Burst). Moving
  it into a Burst `Execute()` requires **monomorphizing** to concrete `uint`/`ushort` routines and
  replacing `NativeArray<T>.Copy` with `UnsafeUtility.MemCpy` / element loops. Keep the validated
  3-runs-per-row structure (2-wide West halo + 16-wide center span + 2-wide East halo, pz-band dispatch
  hoisted) — it ports directly because X is the fastest axis in both layouts. This is a real (small)
  rewrite, not a verbatim move; budget for it and re-run the prove-red after.
- **Reuse `ChunkJobArrayPool.RentPaddedVoxels/RentPaddedLight`** as the scratch buffer source (they already
  pool exactly `PADDED_LIGHTING_VOLUME` buffers). Do **not** introduce a parallel `Allocator.Temp` path;
  the existing pool is the home, and the buffer is already rented today.
- **Editor consumers** (`EditorChunkPipelineRunner`, `ChunkPreview3DWindow`, `LightingTestWorld`) and the
  `LightingJobBenchmark` call the gather directly — update them to the in-job form so production and harness
  stay identical.

### 3.4 Expected result

Main-thread schedule cost drops back to ≈ the PRE-LI-1 fill (the 305 µs gather floor leaves the main
thread); the worker pays gather + the 2.4–3× faster BFS. Re-capture `LightingJobBenchmark` **full-timing**
PRE-vs-POST — expect the gather floor gone and standalone net-positive. (If a memory-bandwidth-bound
machine shows the worker gather not netting positive, that is the signal to consider Layer 2 — or to accept
that the fill, not the gather, is the real cost and go to P-1/P-3 instead.)

---

## 4. Rejected alternative — materialized persistent halo

Storing each chunk's own 20×128×20 padded volume (halo included) at rest would zero the gather everywhere,
but every border voxel is duplicated into up to 8 neighbors and **every player edit / neighbor load must
fan out to repaint halos** — trading a per-job gather for a per-edit + per-load invalidation surface, plus
~1.56× core memory permanently. Not pursued.

---

## 5. Layer 2 — Persistent native cores + zero-copy reads (OPTIONAL, profiler-gated, 🔴)

> **Only pursue Layer 2 if a profiler capture proves the schedule-time *fill* (copy #1) is the bottleneck
> after Layer 1.** Layer 2 removes copy #1 by letting jobs read chunk storage in place, but it reopens
> `LIGHTING_SYSTEM_OVERVIEW.md` §4.3 (which deliberately rejected zero-copy neighbor reads under Unity
> job-safety) and introduces the entire lifetime/consistency surface in §6. The win over Layer 1 is *only*
> copy #1; weigh that against the risk before starting.

### 5.1 Storage layout

Replace `ChunkSection`'s managed arrays with chunk-owned persistent native storage:

```
ChunkNativeStore (per ChunkData, Allocator.Persistent, pooled by resident-chunk count)
 ├─ NativeArray<uint>   Voxels   // CHUNK_VOLUME (32,768), section-contiguous GetFlattenedIndexInChunk layout
 └─ NativeArray<ushort> Light    // CHUNK_VOLUME, same layout
```

Keep the **section-contiguous** layout (`ChunkMath.GetFlattenedIndexInChunk`) so the gather's contiguous
X-run still holds and meshing's section iteration is unchanged. This store **supersedes** (does not sit
beside) the full-chunk stacks in `ChunkJobArrayPool` (`RentVoxelMap`/`RentLightMap`) and the
`NeighborMapSet` fill that drives them — retire those when the store lands, or the codebase carries two
full-chunk layouts to keep bit-identical.

Jobs receive the 9 neighbor stores `[ReadOnly]` and gather into the padded scratch (the Layer-1 in-job
gather, now reading persistent cores instead of snapshot maps). Meshing reads the same cores for its
1-voxel shell (subsumes **P-1**). The merge becomes a native copy/swap into the center store (overlaps
**P-3**).

### 5.2 `ChunkSection` becomes views — and what that breaks

Layout (i) makes `ChunkSection.voxels`/`LightData` `GetSubArray` views into the chunk store (one allocation
per chunk); layout (ii) gives each section its own small `NativeArray`. **Layout (i) conflicts with the
existing independent `ChunkSection` pooling** (sections are pooled separately from `ChunkData`): a section
returned to the section pool and reused in another chunk would carry a dangling view into the previous,
recycled store → cross-chunk corruption. This must be resolved before choosing (i) — likely by making the
store own section residency directly (sections stop being independently pooled), or by choosing (ii).

Either way, the following current mechanics must be **redesigned, not merely "routed through the store":**

- **Serialization (§6.4):** `ChunkSerializer` uses `MemoryMarshal.AsBytes(section.voxels.AsSpan())`,
  managed `.Length` guards, and `foreach (ushort … in section.LightData)`; `ChunkStorageManager` uses
  `Array.Copy`. None compile against a `NativeArray` view — the serializer read/write path is a required
  rewrite even though the on-wire bytes are identical.
- **`PopulateFromSave`** "steals section objects" (reassigns `ChunkSection` instances between chunks) and
  **`PopulateFromFlattened`/`Reset`** return empty sections to the pool to reclaim their arrays — both
  become no-ops or double-free hazards under a single per-chunk store; they need new store-copy/transfer
  logic.
- **Count recompute** (`RecalculateCounts`/`RecalculateNonAirCount`) uses `fixed (uint* = voxels)` and
  `Reset` uses `Array.Clear(voxels,…)` — both **illegal on a NativeArray**. Port to `GetUnsafeReadOnlyPtr`
  / native clear.
- **Pool-reset-safety:** the persistent store must `Dispose` in a `Release` hook (native containers are
  *not* `Array.Clear`'d in `Reset`) — but these pooled types have **no `Release` hook today**, so one must
  be added. The new read-pin/refcount field (§6.1) needs a `Reset` entry per `.agents/rules/pool-reset-safety.md`.

---

## 6. Layer 2 lifetime & consistency — the hard part (🔴)

The snapshot model is immune to concurrency by construction. Zero-copy reads give that up: a `[ReadOnly]`
neighbor core read by a Burst worker while the main thread edits/frees it is a **data race + use-after-free**.
This is why Layer 2 is 🔴 and gated. Five problems:

### 6.1 Lifetime — a read neighbor must not be freed/recycled

A job pins its center + 9 neighbors. The existing job-tracking dicts (`GenerationJobs`/`MeshJobs`/
`LightingJobs`) are keyed by the job-**owner** chunk and **cannot be reused** for this — a chunk read as a
*neighbor* appears in none of them, so the current `World.UnloadChunks` `ContainsKey` guard would recycle a
neighbor mid-read. Needs a **new additive per-chunk read-refcount** (a set cannot represent "pinned by N
overlapping jobs" — adjacent chunks scheduled the same frame share 6+ neighbors). Decrement per
job-per-neighbor on completion.

### 6.2 Consistency — edits during the job window

A player edit / cross-chunk mod to a pinned core races the read. **Defer** such writes until the reading
jobs complete, then apply + re-dirty. ⚠️ The Bug 08 path-2 defer/drain queue **only** holds
`LightModification` objects keyed to the target's own light-job completion — it does **not** cover arbitrary
voxel edits, so this is a **new deferred-write subsystem modeled on** that queue, not a reuse of it.
Alternative: **double-buffer** the core (immutable back-buffer read by jobs; main thread writes the front;
swap on completion) — this **eliminates §6.1 + §6.2 entirely** (race-free by construction) at the cost of
2× core memory + a swap copy. Given this system's deadlock history, **double-buffer is the lower-risk
default and should be priced first**; defer is the memory-optimal but higher-risk option.

### 6.3 Cross-neighbor read tearing vs the bit-identical bar

Even with §6.2, zero-copy reads of 9 cores are **not a consistent point-in-time snapshot**: the in-job
gather reads cores sequentially, so an edit landing between the gather of neighbor A and neighbor B is seen
by one and not the other. A snapshot makes this impossible. This is a real tension with the hard
bit-identical bar; double-buffering all 9 read cores (§6.2) restores point-in-time consistency and is the
clean resolution.

### 6.4 Serialization, §5.2 rework — see §5.2.

### 6.5 Burst aliasing & the documented constraint

Passing a core `[ReadOnly]` to one job while the main thread (or another job) writes it makes Unity
job-safety **throw**; the only silencer, `[NativeDisableContainerSafetyRestriction]`, also **disables
detection of the §6.2 race** — i.e. the "fix" removes the guardrail. This is exactly why
`LIGHTING_SYSTEM_OVERVIEW.md` §4.3 rejected zero-copy (SWMR) reads and chose snapshot+merge. Layer 2 must
*supersede* that documented decision with an explicit, reviewed justification (double-buffer is the most
defensible one, since it keeps reads pointing at an immutable buffer).

### 6.6 Generation/version guard

`CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md` §1.3 called for one and Layer 2 needs it: a per-store generation
stamp captured at schedule and re-validated in `Execute()`, so a refcount bug (store recycled into a
different chunk coordinate, ABA) surfaces as a caught assertion rather than silent wrong-output.

---

## 7. Reconciling sparsity, compaction, palettes, 3D (Layer 2)

- **Per-chunk footprint must be decided, not left ambiguous.** A full-dense core is 192 KB
  (`CHUNK_VOLUME`×(4+2) B); today null sections + uniform-sky compaction make most chunks far smaller.
  Layer 2 must pick **one** model up front (it is the number the §9 memory gate measures): either
  full-dense (simplest; rely on palettes to claw back memory) **or** a non-resident-section scheme
  (layout (ii) / an `IsResident` mask). These are mutually exclusive — do not describe both as "the design."
- **Uniform-sky** is the biggest saver; keep it at rest. If a section is non-resident/uniform-sky, the
  **in-job gather needs an explicit source** for it — pass the `SectionUniformSkyLevel` byte array into the
  job and have the gather synthesize the uniform value for those sections (as `FillJobLightMap` does today),
  rather than reading a dense core that isn't there.
- **Palettes** (`CHUNK_PALETTE_MAPPING`) are the natural wrapper around the native core (store palette
  indices, expand to `uint` in the gather); ship Layer 2 against raw `uint` first.
- **3D keys:** key `ChunkNativeStore` by a 3D coord from the start so cubic chunks
  (`WORLD_SCALING_ANALYSIS.md` §4.3, `SUB_CHUNK_MESHING_ARCHITECTURE.md` §6) don't force a rewrite.

---

## 8. Phasing

1. **Phase 1 — Worker-thread gather (Layer 1). ✅ DONE (2026-06-22, commit `e3e1635`).** Made the gather
   Burst-safe in `NeighborhoodLightingJob.Execute()` (the existing `ChunkMath.GatherPadded<T>`/`CopyRun<T>`
   kept generic — Burst monomorphizes per concrete `T` — with `NativeArray<T>.Copy` swapped for
   `UnsafeUtility.MemCpy`); passed the 9 snapshot maps + rented (unfilled) padded scratch into the job;
   deleted the main-thread gather call; routed all four schedulers through a single
   `NeighborhoodLightingJob.SetGatherSources(in NeighborMapSet, …)` setter so the compass→field mapping
   lives in one place. Updated editor/benchmark consumers. No P-4 dependency, no §6. **Acceptance MET:**
   bit-identical (suite 47/47 incl. C3 B54/B55 + the 11-seam prove-red) AND `LightingJobBenchmark`
   full-timing net-positive (−34 % to −50 % vs LI-1 POST) —
   [`Performance/LIGHTING_P2_PHASE1_2026_06_22_BENCHMARK.md`](../Performance/LIGHTING_P2_PHASE1_2026_06_22_BENCHMARK.md).
   **The LI-1 win is banked.** The Layer-2 profiler gate (below) was **not** triggered — the residual cost
   is the gather (copy #2), not the fill (copy #1).
2. **— decision gate —** Profiler capture during streaming. Only if copy #1 (the fill) is shown to be the
   remaining bottleneck do Layer 2 phases proceed.
3. **Phase 2 — Persistent cores, still buffer-filled (Layer 2a).** Introduce `ChunkNativeStore`; resolve
   the section-pooling conflict (§5.2); rewrite serializer/snapshot/steal/count/Reset paths; **still fill
   per-job buffers** (no zero-copy yet). Bit-identical; both suites green.
4. **Phase 3 — Zero-copy lighting (Layer 2b).** Pass neighbor stores `[ReadOnly]`; add the §6 model
   (double-buffer preferred); needs **P-4 rec-1** (in-flight cap). Bit-identical prove-red.
5. **Phase 4 — Native merge (≡ P-3)** then **Phase 5 — Zero-copy meshing (≡ P-1)**.

---

## 9. Sequencing vs the backlog

```
Phase 1 (Layer 1 gather)  ──►  [profiler gate]  ──►  Phase 2 (cores)  ──►  Phase 3 (zero-copy, needs P-4 rec-1)  ──►  P-3 / P-1
   no P-4 dep, no §6                                  no §6 yet              ▲
   banks the LI-1 win                                                        └─ double-buffer (§6.2) preferred over defer
```

- **Layer 1 has no P-4 dependency** and no concurrency surface — ship it independently.
- **Layer 2 Phase 3** is the only part needing backpressure, and only **P-4 rec-1** (in-flight cap, a
  counter — not the 🔴 rec-3 unload-of-light-pending item). Existing per-frame caps
  (`maxLightJobsPerFrame = 32`, `maxMeshRebuildsPerFrame = 10` in `SettingsManager`; the `< 20` in-flight
  mesh literal in `World.Update` — note all are user-tunable, not fixed) already partially bound the pins.
  Full P-4 / OM-* stays deprioritized; Layer 2 *raises* their eventual impact.
- Layer 2 **subsumes P-1 and the input half of §1.3, overlaps P-3**; update those rows when phases land.

---

## 10. Validation

- **Bit-identity (hard bar):** lighting suite `Minecraft Clone/Dev/Validate Lighting Engine` 47/47 incl.
  C3 seam B54/B55; meshing suite B18–B21. Per phase, run the **three-state prove-red** that validated LI-1
  (clean green → corrupt only the new path → confirm exactly the seam baselines redden → revert → green);
  a `Unity_RunCommand` wave is the reliable ground truth over the (stale-prone) menu suite — see
  `feedback_editor_validation_workflow`.
- **Phase 1 specifically:** because inputs are unchanged snapshots, bit-identity is by construction — the
  prove-red just confirms the in-job gather is live and the monomorphization didn't drift.
- **Layer 2 new coverage (fidelity gap):** a scenario that **edits/double-buffers a neighbor core
  mid-flight** and asserts the deferred/back-buffered write lands post-merge (exercises §6.2/§6.3). C3
  covers the cross-chunk darkening race but not write-during-pin — track in
  `Architecture/Testing Framework/LIGHTING_VALIDATION_HARNESS_FIDELITY.md`.
- **Benchmarks:** `LightingJobBenchmark` full-timing PRE-vs-POST (the LI-1 harness/toggle) for Phase 1;
  `MeshGenerationBenchmark` for Phase 5; `ChunkGenerationBenchmark` canary. Capture per
  `Performance/README.md`; this design's "before" is the LI-1 POST build.
- **Determinism / memory:** fixed-seed light-map byte-diff per phase; Profiler native-memory capture during
  streaming (Layer 2 should lower per-job churn despite larger resident cores; verify the read-pin trail is
  bounded — depends on P-4 rec-1).

---

## 11. Risks & open questions

| #  | Risk / question                                                                | Disposition                                                                                                |
|----|--------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------|
| R1 | **Layer 1:** `GatherPadded<T>` not Burst-safe → can't move verbatim            | Monomorphize to `uint`/`ushort` + `UnsafeUtility.MemCpy`; re-run prove-red (§3.3)                          |
| R2 | **Layer 1:** worker gather still costs (bandwidth-bound machine)               | Re-measure (§3.4); if not net-positive, the fill — not the gather — is the cost → go P-1/P-3, skip Layer 2 |
| R3 | **Layer 2:** zero-copy vs Unity job-safety / §4.3 documented rejection         | Double-buffer (§6.2/§6.5) keeps reads on an immutable buffer; supersede §4.3 with explicit justification   |
| R4 | **Layer 2:** mid-flight edit / cross-neighbor read tearing → non-bit-identical | Double-buffer all 9 read cores (§6.2/§6.3); defer is higher-risk fallback                                  |
| R5 | **Layer 2:** read-pin lifetime → UAF / widened unload-pin                      | New additive refcount (NOT the existing job sets); generation guard (§6.6); P-4 rec-1 bounds it            |
| R6 | **Layer 2:** "no save-format change" hides serializer rewrite                  | §5.2/§6.4 — serializer/snapshot/steal/count/Reset all reworked; on-wire bytes still unchanged              |
| R7 | **Layer 2:** section-as-view vs independent section pooling                    | §5.2 — resolve before choosing layout (i); else choose (ii)                                                |
| Q1 | Layer 2 footprint: full-dense vs non-resident sections?                        | §7 — pick ONE up front (it's the §10 memory-gate number)                                                   |
| Q2 | Layer 2 consistency: double-buffer vs defer?                                   | Prefer double-buffer (race-free, collapses §6.1+§6.2); defer only if memory forces it                      |

---

## 12. Cross-references

- **Motivation / decision:** [`Performance/LIGHTING_LI1_2026_06_22_BENCHMARK.md`](../Performance/LIGHTING_LI1_2026_06_22_BENCHMARK.md).
- **Backlog rows:** [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) — P-1, **P-2**, P-3, P-4, LI-1.
- **Deep dive:** [`CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md`](CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md) §1.3, §2, §3.
- **Architecture SoT (the systems this design changes):**
  [`DATA_STRUCTURES.md`](../Architecture/DATA_STRUCTURES.md) §2 (chunk/section storage),
  [`LIGHTING_SYSTEM_OVERVIEW.md`](../Architecture/LIGHTING_SYSTEM_OVERVIEW.md) §3.3 + **§4.3 (the zero-copy
  rejection Layer 2 must supersede)**,
  [`SUB_CHUNK_MESHING_ARCHITECTURE.md`](../Architecture/SUB_CHUNK_MESHING_ARCHITECTURE.md) §4.2/§6.
- **Scaling:** [`WORLD_SCALING_ANALYSIS.md`](WORLD_SCALING_ANALYSIS.md) §4.3, §6.
- **Invariants:** `chunk-lifecycle` skill + `Architecture/CHUNK_LIFECYCLE_PIPELINE.md`;
  `.agents/rules/pool-reset-safety.md`; `.agents/rules/serialization-safety.md`.
- **Constants:** `ChunkMath` — `PADDED_LIGHTING_VOLUME` (51,200), `PADDED_CHUNK_WIDTH` (20),
  `LIGHTING_HALO` (2), `CHUNK_VOLUME` (32,768), `SECTION_VOLUME` (4,096). Use these, not literals.
- **Suite guards:** `LIGHTING_VALIDATION_HARNESS_FIDELITY.md` (C3 / B54-B55),
  `MESHING_VALIDATION_HARNESS_FIDELITY.md` (MH-10/MH-11 / B18-B21).
