# Engine Scaling Performance Roadmap

**Version:** 1.14  
**Date:** 2026-10-02  
**Status:** In progress — `ES-1`, `ES-2` and `ES-6.1` shipped (2026-10-02); ES-0's GC.Alloc attribution captured
(2026-10-03, §2.2.1 — added `ES-26`/`ES-27`; PM-1's smoke capture added the `ES-28` quick win); the rest is a near-to-far horizon, not
scheduled. Tier 1 items are execution-sized; Tier 2/3
items each need their own design or implementation plan. Re-verify the anchors named per item before
starting (§8).  
**Target:** Unity 6.6 (Mono for dev; IL2CPP for production)

> A whole-pipeline plan (chunk request → generation/load → lighting → meshing → rendering) for three
> observed problems: **30+ s from world launch to stable frame time**, **large frame-time spikes
> while traversing (suspected GC)**, and **headroom for taller worlds and view distance 32+**.
>
> The pivotal findings: **(1) generation is not what makes loading slow** — a requested chunk is
> populated in ~18.5 ms p50; the time goes to frame-paced waits, hard stage barriers, a mis-calibrated
> lighting budget and main-thread per-item bookkeeping (≈0.33 ms per lighting job, measured);
> **(2) no existing capture can see a spike** — every benchmark number is a moving average, and GC
> pauses have never been measured; **(3) what blocks scaling is per-volume cost on the main thread and
> per-object cost in the engine** (full-column copies, a managed voxel heap reaching ~2 GB at vd 32,
> one GameObject per 16³ section). **The decision: measure first (ES-0), take the measured quick wins
> (Tier 1), then run two independent architecture tracks — a native-resident chunk store (reviving
> the shelved P-2 Layer 2 with the demand case it lacked) and a GPU-driven terrain renderer (paged
> shared buffers, hierarchical culling, indirect draws).**

**Audited:** 2026-10-02, at commit `376778fe` (branch `main`).
Six parallel read-only sweeps covered the startup path (`World.StartWorld`, `ForceCompleteDataJobsCoroutine`,
`DeviceCalibration`), generation/storage (`StandardChunkGenerator`, `StandardChunkGenerationJob`,
`StandardWormCarverJob`, `ChunkData`, `ChunkStorageManager`, `RegionFile`, `ChunkSerializer`), lighting
(`WorldJobManager.ScheduleLightingUpdate`/`ProcessLightingJobs`, `NeighborhoodLightingJob`,
`ChunkData.ApplyJobLightMap`), the frame loop and GC (`World.Update`, `PerformanceMonitor`,
`ProjectSettings.asset`), meshing/rendering (`SectionRenderer`, `Chunk.ApplyMeshData`,
`MeshGenerationJob` sub-quad tessellation, the three block shaders, the URP asset/renderer and
`ProjectSettings`/`QualitySettings` as text), and external research (the
Incandescent Games video transcript + author replies, Unity 6000.6 API via the `unity-api` MCP). The
headline quick wins were spot-checked in code by hand (`World.cs:1064`, `Chunk.cs:151–154`,
`ChunkData.cs:395`, `LightingHelper.cs:46–51`, `WorldJobManager.cs:1810–1813`, `World.cs:4171`) and the
calibration result read from the development machine's `Player.log` (2026-09-22, IL2CPP). Also read: the existing
performance design docs listed below and the IL2CPP captures they cite. **No new capture was taken** —
quoted numbers are from those captures and the Player.log; anything else is tagged as inferred.

**Relationship to other documents:**

- [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) — master backlog. This
  roadmap **orders and bundles** its open items (`SU-*`, `SL-*`, `OM-2/3`, `WG-*`, `P-1/3/5/7`, `GS-5/6`,
  `MR-8`, `DT-3`) against the three symptoms; their detail stays there. New items get `ES-*` IDs here.
- [`CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md`](CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md) /
  [`CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md`](CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md) — the
  measured pipeline economics this plan builds on; P-9 §6 Option D (off-main-thread scheduling) is the
  rejection ES-18 answers.
- [`../Archived/PERSISTENT_CHUNK_STORAGE_P2.md`](../Archived/PERSISTENT_CHUNK_STORAGE_P2.md) — P-2
  Layer 2, shelved 2026-07-26 for lack of a consumer. ES-18 supplies that consumer; its §5
  lifetime/consistency rules are ES-18b's design input.
- [`WORLD_SCALING_ANALYSIS.md`](WORLD_SCALING_ANALYSIS.md) — Tier A (height) / Tier C (cubic) breakage
  lists; ES-13, ES-19 and ES-24 are their performance prerequisites.
- [`VISIBILITY_CULLING_ARCHITECTURE.md`](VISIBILITY_CULLING_ARCHITECTURE.md) — `GS-5`; becomes ES-21 and
  must output a visible-section set the ES-20 renderer consumes (its §8 already says so).
- [`OM1_DEVICE_CALIBRATION.md`](OM1_DEVICE_CALIBRATION.md) — the calibration ES-2 hardens.
- [`VOLUMETRIC_AND_RAYTRACED_EFFECTS_REPORT.md`](VOLUMETRIC_AND_RAYTRACED_EFFECTS_REPORT.md) — `VX-1`/`VX-8`
  per-pixel light/AO volumes: the escape hatch that lets ES-22 (greedy) and ES-20's packed face drop
  per-vertex light.
- [`../Architecture/CHUNK_LIFECYCLE_PIPELINE.md`](../Architecture/CHUNK_LIFECYCLE_PIPELINE.md) — the
  invariants every ES item touching scheduling must keep; the `chunk-lifecycle` skill is mandatory for
  those items.
- [`PERFORMANCE_MONITOR_AND_LOGGER_OVERHAUL.md`](PERFORMANCE_MONITOR_AND_LOGGER_OVERHAUL.md) (`PM-*`) —
  the opt-in monitor/logger that delivers ES-0 and on which every ES verdict is scored.
- [`DOTS_ECS_ADOPTION_ANALYSIS.md`](DOTS_ECS_ADOPTION_ANALYSIS.md) (`EC-*`) — the full reasoning behind
  §3 Option C's rejection, and the deferred dynamic-entities fit.

---

## 1. Goals & non-goals

### Goals

1. **Time-to-stable ≤ 8 s** at the default view distance on the development machine — "Play" to a drained
   pipeline (no lighting/mesh work in the resident square) with frame time back at its median —
   measured by ES-0.
2. **No traversal frame > 2× the median** in straight-line flight at 50 m/s, vd 10–15, and managed
   allocation during streaming near 0 B/frame outside debug UI.
3. **vd 32 at 60 FPS** with the FP visibility criterion `latency ≤ vd × 16 ÷ speed` still met, and
   resident memory not above the recorded ~5.7 GB vd-32 peak (P9-2 Q4).
4. **Height headroom:** per-chunk main-thread cost and memory track *content* (non-uniform sections),
   not column height, so Tier A (384–512 high) costs ≈×1.2–2 rather than ×3–4.
5. **Constraints unchanged** — packed-`uint` voxels, Burst-only jobs, bit-identical lighting (§6).

### Non-goals (v1)

- Hardware ray tracing — rejected (`VX-*`: URP has no RT support).
- Cubic chunks (Tier C) — kept *possible* by keying ES-18 storage per 16³ section; not built here.
- Changing lighting semantics — every lighting item is gated on bit-identity (oracle + seam baselines).
- Mobile tuning beyond what OM-1 already scales — stays in the master report.

---

## 2. Current state — what the sweeps found

### 2.1 Startup (verified in code + the development machine's IL2CPP `Player.log`, 240 Hz vSync)

| # | Phase | Anchor | What bounds it |
|---|---|---|---|
| 1 | Placeholders + `LoadOrGenerateChunk` for **529** chunks (radius `min(LoadDistance, maxInitialLoadRadius 10) + 1`), 441 in the wait set | `World.cs:1030–1061` | **No in-flight cap** (`SU-2`): 529 `Task.Run` disk probes + up to 529 generation jobs at once |
| 2 | `foreach (Awaitable task in loadTasks) yield return task;` | `World.cs:1064` | **≈ one frame per task.** Pre-coroutine time is 2 314 ms (loaded world) / 2 279 ms (new world) — 4.37 / 4.31 ms per yield, i.e. one 240 Hz frame each, equal for disk hits and misses. ≈ 8.8 s at 60 Hz (inferred) |
| 3 | Phase 1: drain generation | `World.cs:1566–1580` | No time window, but the **gameplay** `maxStructureModsPerFrame` (5 000) still applies → 32 frames on a new world |
| 4 | Phase 2: lighting fixpoint sweeps over 441 chunks — schedule all (TempJob, **unpooled, full height**), `Complete()` all, merge, yield | `World.cs:1605–1723`, `WorldJobManager.cs:840–897` | One frame per sweep, but each sweep is a **~500 ms frozen frame** (new world: 238 ms schedule + 265 ms completion). ≈1.7 MB copied per job → ~750 MB of main-thread copying in sweep 1 (inferred) |
| 5 | `_isWorldLoaded = true`; first `Update` | `World.cs:1093–1116`, `:2544` | **One-frame burst:** 441 `Chunk` objects from an empty pool (9 GOs + 8 renderers + 8 meshes each), **441 border prefabs instantiated even with borders hidden** (`World.cs:4171`), 441 mesh requests, 200 generation requests |
| 6 | Rings 12–13 stream in under **gameplay** caps | `World.cs:2602–2943` | Calibrated `maxLightJobsPerFrame` — **15 on the development machine** (`Player.log:45`: probe `light=1.308 ms` vs reference `0.604 ms`, on the reference-class i9-9900K/4070 Ti that should reproduce 32; earlier captures read 24). Ring-11 lighting re-arms ring-10 edge checks; `immediate` re-mesh requests jump the queue, so inner rings mesh twice and the outer view ring appears last |

Logged totals: loaded world **2 719 ms** to handoff (coroutine 405 ms, 7 iterations); new world
**4 623 ms** (coroutine 2 344 ms, 36 iterations). **The full 30 s is not visible in the IL2CPP log.**
The remainder is after the handoff and/or Editor-specific (Mono main-thread cost 2–5×, lower Editor
frame rate inflating phase 2's frame-paced waits, asynchronous Burst compilation of first jobs).
ES-0's drain stamp splits it; until then the ranking in §4 is by verified mechanism, not attributed time.

No shader warmup exists (`GraphicsSettings.asset:41–45` `m_PreloadedShaders: []`, no
`GraphicsStateCollection`/`ShaderVariantCollection`).

### 2.2 Traversal spikes

| Finding | Anchor / source | Quality |
|---|---|---|
| **The benchmark cannot see a spike.** CPU/wall = 30-frame moving average; GC/frame = 60-frame moving average of positive `GC.GetTotalMemory` deltas — a frame where a collection ran records **0**; samples every 50 ms. No worst-frame, p99 or collection-count column exists | `PerformanceMonitor.cs:30–31, 87, 314–346` | verified |
| Crossing work (`CheckViewDistance`, `UnloadChunks`) runs **outside every `WorldFrameProfiler` region** | `World.cs:2571–2575` | verified |
| Generated chunks allocate **~142 KB managed per started chunk** at 200 m/s (47 MB/s); loaded chunks ~13 KB. Only ~1–3 KB/chunk is attributed in code — **the rest is unattributed** | `BenchmarkRun_2026-08-25` (IL2CPP Master, vd 10) | measured / inferred |
| **Attributed 2026-10-03 (§2.2.1):** 127.4 KB of GC.Alloc per generated chunk at 200 m/s, 100 % resolved by call stack; **79.7 % on ThreadPool threads**. **77.3 % is one pattern** — `ChunkSerializer.WriteSection` passes each section array as `ReadOnlySpan<byte>` to `BinaryWriter.Write`, which copies it with `ToArray()` (ES-26); the unload save path as a whole is 81.8 % | [`ENGINE_SCALING_ES0_GC_ATTRIBUTION_IL2CPP_2026-10-03_BENCHMARK.md`](../Performance/ENGINE_SCALING_ES0_GC_ATTRIBUTION_IL2CPP_2026-10-03_BENCHMARK.md) | measured |
| 200 m/s generation phase: avg GC 375.9 KB/frame, peak 1.2 MB, peak CPU 48.3 ms (smoothed), min FPS 20.4 | same | measured |
| Managed heap 165–230 MB at vd 10 (512 MB peak), **~1.95 GB at vd 32**; roughly half is voxel arrays (24 KB per `ChunkSection`) | FP-4, FP-10 F6; `ChunkSection.cs:57–62` | measured / verified |
| `Chunk.Reset` runs a full managed `OnDataPopulated` voxel scan for **every chunk entering view whose data is already populated** — the common case when LoadDistance > viewDistance — redundant, since the active buckets live on `ChunkData` | `Chunk.cs:151–154`, `:230–260` | verified |
| Chunk borders created on every visual activation even when hidden (4 225 live at vd 32) | `World.cs:4171` | verified |
| Unbudgeted main-thread passes: `ApplyModifications` (whole queue), lighting merge (2.705 ms/frame at 200 m/s), disk-hit populate continuation (`SL-2`), unload row + save snapshots (`SL-3`, `OM-3`) | `World.cs:3322`, `WorldJobManager.cs:1543`, `World.cs:1336`, `:3689` | verified / measured |
| Every generated chunk is added to `ModifiedChunks`, so every generated chunk is serialized + saved on unload (5 333 chunks at quit) | `ChunkData.cs:395` | verified |
| I/O path garbage: `Task.Run` closure + state machine per disk probe — **including misses on fresh terrain**, which also pay ≥1 frame of latency before generation is scheduled; `byte[]` payloads, stream wrappers, `ReadBytes(512)`, `ContinueWith` closure, `BitConverter.GetBytes`, padding `byte[]`. *Measured 2026-10-03:* the async/`Task` part ~5 KB per generated chunk, `RegionFile.SaveChunkData` 1.95 KB | `ChunkStorageManager.cs:194`, `RegionFile.cs:137,167,246–266`, `ChunkSerializer.cs:102–112,233`, `World.cs:3839` | verified |
| Smaller sources: `ExpandStructure` `yield` iterator per structure; `new List<VoxelMod>` per spill target; `new SpiralLoop()` per crossing; `_chunksToUpdateVisualization` grows unbounded while the visualizer is off (`DT-3`); ungated `[LIGHTING RESCUE]` log per unloaded chunk; `ProjectSettings` captures a script stack trace for `Debug.Log`. *Measured 2026-10-03:* `ExpandStructure` is the **second-largest** site, 8.0 KB per generated chunk (one ~208 B iterator per structure marker, ~39 per chunk); the `[LIGHTING RESCUE]` log did not allocate in the IL2CPP window | `StandardChunkGenerator.cs:867`, `ModificationManager.cs:38`, `World.cs:4201,3813`, `ProjectSettings.asset:57` | verified |
| Incremental GC is on (`gcIncremental: 1`); no `GC.Collect`/`UnloadUnusedAssets` anywhere; Standalone = IL2CPP Master | `ProjectSettings.asset:714,719,748` | verified |

#### 2.2.1 Hypothesis H-1 — the unattributed ~142 KB per generated chunk is `ChunkSection` pool misses

*Status: **REFUTED 2026-10-03** (measurement at the end of this section); the reasoning below is kept as
the record of what was tested.* A `ChunkSection` pool miss
allocates **24 KB of managed arrays** (`uint[4096]` + `ushort[4096]`, `ChunkSection.cs:57–62`), and a
typical surface chunk holds ~6 non-empty sections: 6 × 24 KB ≈ **144 KB** — the measured per-chunk figure.
Three code paths make generated chunks miss the pool where loaded ones do not:

1. `PopulateFromFlattened` rents **all 8** sections for every generated chunk before pruning the empty
   ones (`ChunkData.cs:412`).
2. **Every generated chunk is in `ModifiedChunks`** (`ChunkData.cs:395`), so its unload takes a save
   snapshot, and `CreateSerializationSnapshot` **rents a second set of pooled sections**
   (`ChunkStorageManager.cs:836`) that stay checked out until the ThreadPool save finishes. Unedited chunks
   loaded from disk are not in `ModifiedChunks` (`World.cs:3823`), take no snapshot, and keep the pool
   balanced — consistent with the ~13 KB per *loaded* chunk.
3. During a 30 s fast-flight phase the resident set and the in-flight save count are both transient, so
   the pool keeps growing by allocation instead of reaching a steady state.

**If true:** the GC fix is on the storage side, not GC tuning — ES-9's "save takes ownership of the
sections instead of snapshot-copying" and ES-10's persistence decision remove path 2 outright, renting only
non-empty sections removes path 1, and ES-18a removes the managed arrays entirely.  
**Test (first PM-3/ES-0 capture):** a `TotalCreated` counter on the section pool (`ConcurrentDynamicPool`
exposes `TotalGets`/`TotalDestroyed` but not misses) per generated vs loaded chunk, and the same flight
A/B'd with ES-10's "do not save unmodified terrain" leg. Refuted if misses per generated chunk stay ≪ 6.
Count `SerializationBufferPool` misses in the same capture: each in-flight save also rents a 256 KB buffer
before its await (AC-9 static pass), and a rising in-flight peak allocates more of them — a second,
non-per-chunk contributor that could otherwise be mistaken for H-1. The counter edit is specified in
`PERFORMANCE_MONITOR_AND_LOGGER_OVERHAUL.md` §7.1 (PM-0 packet). *Instrumented 2026-10-03:* every benchmark
report's Pipeline section now carries a per-phase "Pool misses" block (section / data / save-buffer misses beside
chunks generated vs loaded).

**Verdict — refuted.** `BenchmarkRun_2026-10-03_14-00-16` (IL2CPP Master, default route, 30 s phases):

| Generation phase | Chunks generated | Section misses | Per generated chunk | Avg GC/frame |
|---|---:|---:|---:|---:|
| 10 m/s | 394 | 123 | 0.31 | 7.1 KB |
| 20 m/s | 1 026 | 0 | 0.00 | 12.6 KB |
| 50 m/s | 2 523 | 139 | 0.06 | 30.6 KB |
| 100 m/s | 4 780 | 1 330 | 0.28 | 130.1 KB |
| 200 m/s | 8 668 | **0** | **0.00** | 1 419.0 KB |

`ChunkData` and save-buffer misses are **0 in every phase**, loading phases included. A second Master build
(`BenchmarkRun_2026-10-03_14-34-59`) reproduces it: 0 section misses at 200 m/s over 9 306 generated chunks
(1 436 KB/frame, ≈ 132 KB per generated chunk), and the same 1 330 misses (0.28 per chunk) at 100 m/s. The garbage H-1 set out to
explain is still there: at 200 m/s, 1 419 KB/frame × ~26 fps × 30 s ≈ 1.1 GB, **~129 KB per generated chunk**
(about 120 KB at 100 m/s). The section pool covers it with **zero** misses at 200 m/s, and with 1 330 × 24 KB ≈
32 MB (~6%) at 100 m/s. The pool reaches steady state during the initial load, and CP-7's linger window keeps
it warm, so neither the rent-all-8 path nor the save-snapshot path allocates in flight. **The ~130 KB per
generated chunk is still unattributed and lies elsewhere.** ES-0's per-frame GC capture — under the heap-delta
fallback, since PM-0 found no live allocation counter in Master — and a GC.Alloc callstack capture are the next
instruments. ES-9 and ES-10 lose H-1 as their GC justification; their own cases (save ownership, persistence
policy) stand or fall separately.

**Attribution — 2026-10-03, ES-0 call-stack capture.** An IL2CPP Development (Master configuration) player, GC.Alloc
call stacks on, the 200 m/s phase alone (`BenchmarkRun_2026-10-03_16-20-22`: 825 frames, 8 831 chunks generated).
The run reproduces the problem — 131.4 KB per generated chunk by the same heap-delta method, +1 % on the Master runs —
and GC.Alloc accounts for 96.9 % of that heap delta. **127.4 KB of GC.Alloc per generated chunk, every byte resolved
to a call site:**

| Call site | KB / generated chunk | Share | Owner |
|---|---:|---:|---|
| `ChunkSerializer.WriteSection` → `BinaryWriter.Write(ReadOnlySpan<byte>)` → `ReadOnlySpan<T>.ToArray()` (ThreadPool save) | **98.0** | **76.9 %** | **ES-26** (new) |
| `StandardChunkGenerator.ExpandStructure` — one iterator per structure marker | 8.0 | 6.3 % | ES-9 |
| `ChunkData.AddToSkylightQueue` / `AddToBlocklightQueue` — `Queue<T>` regrowth (merge, `ApplyModifications`) | 8.9 | 7.0 % | **ES-27** (new) |
| `ChunkSection` constructor — section-pool misses, all in 16 frames | 3.1 | 2.4 % | — (pool growth bursts) |
| Async I/O: `LoadChunkAsync` / `SaveChunkAsync` state machines, `Task.Run`, `UnloadChunks`' `ContinueWith`, `GetRegion`, `Serialize` | 4.7 | 3.7 % | ES-9 |
| `RegionFile.SaveChunkData` | 1.95 | 1.5 % | ES-9 |
| 34 further sites (rest of the save path, lighting-merge `List`/`HashSet` growth, per-frame UI/render) | 2.7 | 2.2 % | — |

The **unload save path is 81.8 %** (104.2 KB per chunk) and 79.7 % of all bytes are allocated on ThreadPool threads —
which is why every main-thread instrument missed it. The Editor (Mono) pass ranks the sites identically (138.7 KB per
chunk, `WriteSection` 76.4 %). Full table, method and acceptance checks:
[`ENGINE_SCALING_ES0_GC_ATTRIBUTION_IL2CPP_2026-10-03_BENCHMARK.md`](../Performance/ENGINE_SCALING_ES0_GC_ATTRIBUTION_IL2CPP_2026-10-03_BENCHMARK.md).

### 2.3 Per-item pipeline costs

| Area | State | Source |
|---|---|---|
| Lighting job, main thread | **0.15 ms schedule + 0.18 ms merge**; merge unbudgeted, largest single slot pre-P9-2 (261–288 ms/s) | P9-1 |
| Lighting job, worker | ~110–456 µs (banded no-op/edge check up to full-height edge check) — **cheaper than its main-thread bookkeeping** | LI-2 screening |
| Schedule internals | 9 voxel + 9 light maps = **1.73 MB managed→native per job** (`NeighborMapAssembler.cs:54–75`); uniform-sky sections filled by a **per-element loop** (~180 k writes/job, `LightingHelper.cs:46–51`) | verified |
| Merge internals | `ApplyJobLightMap` walks **all 8 sections regardless of the LI-2 band**; each uniform-sky air section is rented, written 4 096×, classified, re-compacted and returned only to rediscover it is uniform; `RecalculateCounts` runs per section although the lighting job never changes voxels | `ChunkData.cs:1244–1338`, `ChunkSection.cs:176` | verified (redundancy inferred) |
| Remesh requests | Every *stable* merge requests self + 4 cardinal neighbors **even when `lightChanged` is false** (P9-2 already computes it) — pre-delivery this collapses to 1.00 build, post-delivery it re-meshes on no-op edge rounds (unmeasured since P9-2) | `WorldJobManager.cs:1810–1813` | verified |
| Mesh job | Same 1.73 MB fill per job; ≥1 per chunk; plus re-mesh on every view re-entry (visual pooled, mesh dropped) | `WorldJobManager.cs:579–582`, `World.cs:4137–4156` | verified |
| Generation | 1.54–1.60 ms wall per chunk at saturation (~650 chunks/s); `StandardWormCarverJob` is a **single-threaded `IJob` re-simulating worms from up to 33×33 cells per chunk**, ahead of the parallel terrain `IJobFor`; full-resolution 3D noise (no coarse lattice); ~228 KB of unpooled `Persistent` buffers per chunk (`WG-1`) | ChunkGenerationBenchmark 2026-06-27; `StandardWormCarverJob.cs:155–163`; `StandardChunkGenerator.cs:352` | measured / verified |
| Renderer | One pooled parent + 8 section GameObjects (MeshFilter + MeshRenderer) per chunk; up to 3 draws per visible section; 32 B/vertex, 4 verts + 6 indices per quad; no occlusion culling. vd 10: 441 × 8 = **3 528** section renderers; vd 32: **33 800**; at 384 height **~101 000** | `Chunk.cs:67–91`, `SectionRenderer.cs:123–143`; derived | verified / derived |
| Upload path | `SetVertexBufferParams` on **every** update (realloc risk), 4 `SetVertexBufferData` streams, **always 32-bit indices**, `MarkDynamic` meshes; `Chunk.ApplyMeshData` re-uploads **all 8 sections** on every apply (no per-section dirty skip); no `Mesh.MeshDataArray` anywhere. Upload 676 µs/chunk is the Checkerboard worst case (278 k verts); MixedTerrain ≈ 450 µs *(inferred)* | `SectionRenderer.cs:158–248`, `Chunk.cs:506–528`; MR-2 after-baseline | verified / measured |
| Sub-quad tessellation | VO-9b/SS faces emit N² **independent** quads (no shared vertices): 16× vertices per face at N = 4, 4× at N = 2 — world-level 1.4–1.7× vertices with SS-3 at N = 2 | `MeshGenerationJob.cs:874–922`; SS §8 | verified / measured |
| Render settings | SRP Batcher **on**; GPU Resident Drawer **off** (and unusable as configured: renderer is **Forward**, not Forward+, and no DOTS-instancing variants); depth + opaque textures required; MSAA 2× in the asset; shadows off; graphics jobs on; D3D11 primary. Block shaders have **only a forward pass** — no DepthOnly/ShadowCaster/MotionVectors. Transparent blocks are **alpha-cutout** (no sorting); only fluids blend | `VoxelEngine-URP-Asset.asset:22–88`, `VoxelEngine-URP-Renderer.asset:68–70`, `ProjectSettings.asset:372–402`, the three `.shader` files | verified |
| Rendering baseline | **None in game** — no draw-call, SetPass, vertex/frame or GPU-ms capture exists at any view distance | — | gap |

### 2.4 What breaks as height and view distance grow

- **Height ×3 (128 → 384):** every full-column cost ×3 — 5.2 MB per job fill, 900 KB padded buffers,
  384 KB generation output, 576 KB save snapshot, the job-array pool cap able to hold >700 MB.
- **Height hazard — the BFS cap.** Initial `RecalculateSkylightForColumn`
  enqueues every air cell above the heightmap —
  ~16 k nodes at 128, ~70 k at 384, ~140 k+ at 640 (inferred) — approaching the **permanent** 200 k
  `MAX_BFS_NODES_PER_PASS` cap, where a pass aborts and retries forever. ES-13 removes it.
- **vd 32 (71² = 5 041 resident):** every crossing walks 4–5 k placeholders + unload candidates; the
  ~1 s fail-safe scan grows with residency; ~2 GB managed heap for the GC to trace; 33 800 renderers.

### 2.5 Systems outside this roadmap's sweep

The sweeps above covered the chunk pipeline, rendering, the `World.Update` frame loop and
instrumentation. The remaining systems — audio, clouds, underwater, UI blur, fluid-entity physics,
sky/bloom, contact shadows + sway, asset/GPU memory, the quit/save path and the cold UI/tooling tail —
have never had a performance pass. They are recorded as audit tasks `AC-1`…`AC-10` in
[`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) § Audit coverage gaps (the
master backlog owns audit coverage), each with the facts verified so far and what to measure first.
One of them touches this roadmap's own target: `AC-2`'s `Clouds.UpdateClouds()` runs inside every
chunk-crossing frame. Its 2026-10-02 static pass found that call to be a near-no-op (no allocation,
~242 unchanged transform writes at vd 10), so dropping it is only a small ES-6 rider; the larger cloud
costs (a full rebuild on every settings-menu close, ~35 % of tiles drawn fully faded, the root moved every
frame) are recorded there.

---

## 3. Decision: where the architecture must move

The fixed costs are per *item* on the main thread and per *object* in the engine; tuning caps does not
move them — P-8 and P9-0a both proved that loosening a limit just spends the frame.

### Option A — Keep the architecture, budget and tune (rejected as the end state)

- ✅ Cheap, and Tier 1 below does exactly this first — most of the startup and spike items need no
  architecture change at all.
- ❌ **Leaves the ~0.33 ms/lighting-job main-thread floor, the managed voxel heap and the per-renderer
  cost in place**, all of which multiply with height × vd². No budget holds 60 FPS at vd 32 × 384.

### Option B — Native-resident chunk store + GPU-driven renderer ✅ **preferred direction**

Two independent tracks after the quick wins:

- **Data track (ES-18).** Canonical voxel + light data in persistent native memory per section.
  **ES-18a** moves the arrays native but keeps today's per-job snapshot (now a native `MemCpy`): no
  concurrency change, and the ~2 GB managed heap the GC must trace at vd 32 disappears. **ES-18b**
  double-buffers published section versions so jobs read them without copying: schedule-time fills
  vanish, the merge becomes a publish (P-3 subsumed), and the scheduling decision can run in Burst over
  native state. P-9 rejected off-main-thread scheduling (Option D) *because the gathers touch live
  managed `ChunkData`*; double-buffered native storage removes exactly that objection.
- **Render track (ES-20).** Terrain leaves the GameObject world: paged shared GraphicsBuffers of packed
  faces, hierarchical culling, a few indirect draws (§4 ES-20). Floating-origin shifts become one
  uniform update. Independent of the data track.

### Option C — ECS / Entities Graphics (rejected)

- ✅ Unity-maintained BRG path; Entities, Entities Graphics and Unity Physics ship with 6000.6.4f1 as
  core packages (6.6.0).
- ❌ **Entities Graphics requires URP Forward+ (the renderer is Forward) and DOTS-instancing shader
  variants, yet gives no batching win for unique per-section meshes** — Unity documents it can be slower
  than SRP-batched GameObjects there, and it offers no procedural/indirect path ES-20 needs.
- ❌ ECS job safety is per component *type*, so it cannot express per-section concurrency (ES-18b);
  streaming becomes constant main-thread structural changes; the package re-adds three modules the
  lean-package decision removed. Full analysis and the one deferred fit (crowds of dynamic entities,
  `EC-6`): [`DOTS_ECS_ADOPTION_ANALYSIS.md`](DOTS_ECS_ADOPTION_ANALYSIS.md).

---

## 4. Work items (`ES-*`)

Effort 🟢/🟡/🔴 and risk use the master report's legend. "→ `XX-n`" means the item *executes* an existing
backlog entry whose detail lives there. Expected effects marked *(inferred)* are not yet measured.

### Tier 0 — measure first

**ES-0 — Spike-visible capture + time-to-stable stamp.** 🟢 / 🟢. Delivered by
[`PERFORMANCE_MONITOR_AND_LOGGER_OVERHAUL.md`](PERFORMANCE_MONITOR_AND_LOGGER_OVERHAUL.md) PM-0…PM-3 +
PM-6; the bullets below are what ES needs from it.
- Per-frame raw max and p99 frame time, per-frame GC allocation that survives collection frames, and
  per-frame collection flags per benchmark phase — alongside, not instead of, `PerformanceMonitor`'s
  smoothed history (always-on by design). (PM-0, 2026-10-03: `ProfilerRecorder`'s "GC Allocated In Frame"
  does not exist in Master players, and `GC.GetAllocatedBytesForCurrentThread` is not live under IL2CPP — the
  per-frame figure comes from the heap-delta method plus a collection flag.) (PM-1, 2026-10-03: the raw per-frame
  wall/CPU ring and exact worst/p99 over its last 2 048 frames exist — read with `/perf stats`; per-phase benchmark
  columns arrive with PM-6.) (PM-2, 2026-10-04, confirmed in an IL2CPP Master build: every frame row
  carries its heap-delta allocation — collection frames flagged and left out of the statistics — and its collection
  count, and at the Frame tier its GPU/render-thread/present-wait time and hitch records marked GC-correlated; read
  with `/perf stats` / `/perf hitches`, and logged to Player.log when an automated run ends. First Master reading, one
  Systems-tier run: the 200 m/s traversal's hitch frames (34–52 ms) are led by the `Tick` slot at 26–38 ms, the initial
  load's by `LightMerge` and `Tick` — `PERFORMANCE_MONITOR_AND_LOGGER_OVERHAUL.md` §7.3; one run, not yet a ranking.)
  (PM-6, 2026-10-06, confirmed in an IL2CPP Master build: **the benchmark report now carries this per phase** — a
  "Frame Health (every frame)" table with exact wall p50/p99/worst, CPU p50/p99, GC p99, collections, hitch frames and
  GPU p99 over every frame of each phase, beside the averaged columns. Benchmarks run at the Frame tier at least; with
  `-mc-set perfMonitorTier=Capture` every frame and every hitch window is also written to `persistentDataPath/PerfLogs`.
  First Master reading, four runs: the 200 m/s phase's worst frame is 144–154 ms and its p99 78–93 ms, where the
  averaged Peak Wall column reads 56–59 ms — §7.7.)
- Slots for `CheckViewDistance`, `UnloadChunks`, `ApplyModifications`, the disk-hit populate
  continuation and every other untimed `World.Update` region, plus an unattributed remainder. (PM-3, 2026-10-04, confirmed
  in an IL2CPP Master build: every `World.Update` region has a slot — `ViewDistance`, `Unload` and nine more —
  with a `WorldUnattributed` remainder, plus slots for the disk-hit continuation (`DiskLoadApply`) and the other systems
  outside it, and per-frame counters for
  queue depths, jobs in flight, resident chunks and pool misses; `ApplyModifications` already was the `Apply` slot. First
  Editor reading: a 1 500-block teleport's crossing frame spent 496 ms in `Unload` and 16 ms in `ViewDistance`. First Master
  reading, one Systems benchmark run: the 200 m/s phase's hitches are led by `Tick` (32–104 ms) and `LightMerge`
  (15–36 ms), and a 135 ms frame after it by `Unload` at 96.5 ms — `PERFORMANCE_MONITOR_AND_LOGGER_OVERHAUL.md` §7.4; one
  build, not yet a ranking.) (PM-4, 2026-10-05, confirmed in an IL2CPP Master build: the `Tick`-led 200 m/s hitches track
  the fluid tick's job count — every hitch record with more than 200 fluid chunk jobs had a 46–160 ms `Tick` and 170–399 ms
  of fluid worker time, three Systems runs — with the main-thread prepare and drain the inferred larger share; §7.5. No ES
  item owns the fluid tick's fan-out yet; PM-8 measures that main-thread split first.) (PM-8, 2026-10-05, confirmed in an
  IL2CPP Master build: `Tick` is split into six timed parts that sum to it within 0.31 ms. In the 229–273-fluid-chunk
  records the fluid prepare's full-chunk voxel-map copies (258–307 MB per tick, 26–39 ms) and the wait for the fluid jobs
  (12–46 ms) share the time, with the replay at 4–10 ms; a third of the `Tick`-led records are the managed grass tick
  instead, 19–27 ms at 44–69 k active grass voxels — §7.6. No ES item owns either fix yet.)
- A once-per-launch **drain stamp** (P-4's tail-inclusive drain predicate): ms to `_isWorldLoaded`, to
  drained, to frame time within 1.25× median for 2 s — splits the 30 s into before/after handoff. *(2026-10-06: the
  remaining ES-0 item. No PM phase owns it — PM-6 covered export only — and it touches the startup pipeline, so it
  needs the `chunk-lifecycle` skill. Not started.)*
- One Development-build (not deep-profiling) Profiler capture of a 200 m/s generation flight with
  GC.Alloc callstacks, to attribute the ~142 KB/chunk. ✅ 2026-10-03 — 127.4 KB per generated chunk, 77 % one
  span copy in the save serializer (§2.2.1, ES-26). Re-run with `ProfilerCapture.ArmAutoStop` +
  `ProfilerQueries.GcCallstacks` (`unity-editor` skill, `references/profiler.md`) to score ES-26/ES-27/ES-9 —
  end to end, no clicks, via the `perf-benchmark` skill's `references/player-capture.md` (Flow B).
- Must report in Master IL2CPP (not dev-gated). Every other verdict here is scored against it.

### Tier 1a — startup quick wins

**ES-1 — Stop frame-pacing the initial load.** 🟢 / 🟢. Replace the per-task `yield` at
`World.cs:1064` with a completion counter (`while (pending > 0) yield return null;`), and start the
Phase-1 generation drain while loads are still arriving. Expected −2.2 s at 240 Hz, −8.8 s at 60 Hz
*(inferred from the log arithmetic in §2.1)*. Keep the CP-3 fault contract inside `LoadOrGenerateChunk`.

**ES-2 — Make OM-1's lighting calibration robust.** 🟢 / 🟢. The probe resolved 15 jobs/frame on the
hardware the reference constants were anchored on (32 expected; 24 in earlier captures): seven samples
in scene `Awake`, persisted once. Use more warm-up and a trimmed median, re-probe after the scene has
loaded, and floor desktop-class results at the default unless the probe is *consistently* slow. Up to
×2.1 post-handoff lighting throughput on the development machine. Immediate check: set
`maxLightJobsPerFrame` back to 32 in `settings.json`. **P-9 §4 caveat:** this restores the tuned rate,
it does not raise it.

**ES-3 — Loading mode.** → `SU-1` + `SU-2`. 🟡 / 🟡. Under a loading overlay, until the **resident
square has drained** (not merely `_isWorldLoaded`):
- lift the structure-mod budget in Phase 1;
- run startup lighting on **pooled, banded** buffers like streaming does (the TempJob path also skips
  LI-2 banding, `WorldJobManager.cs:889–897`), and **share one snapshot per chunk per sweep** across its
  nine readers — nothing mutates chunk data inside a sweep (~750 MB → ~90 MB copied in sweep 1,
  *inferred*); the pool must cover a whole sweep's demand (the 2026-06-11 incident);
- admit rings nearest-first with an in-flight cap; widen the wait radius to `LoadDistance + 1` so the
  outer rings and the inward edge-check/re-mesh wave run in the batch loop rather than under gameplay caps;
- after handoff, scale quotas/ceilings ×4–8 (OM-1-bounded, memory in-flight caps kept).
Panic-gate thresholds stay untouched (P-8). Longer-term: pipeline the coroutine (light/mesh a ring as
soon as its neighbors are ready) instead of gen → light → mesh barriers — 🔴, `chunk-lifecycle`.

**ES-4 — De-burst the first `Update`.** 🟢 / 🟢. Prewarm the `Chunk` pool during the coroutine's idle
frames; create chunk borders only while `ShowChunkBorders` is on (build on toggle); load the world scene
with `LoadSceneAsync` behind a loading screen.

**ES-5 — Shader/PSO warmup.** 🟢 / 🟢. Warm the block, transparent and liquid shader variants during
loading (`GraphicsStateCollection` in Unity 6, or a `ShaderVariantCollection`) — verify the API first.
Benefit unmeasured.

### Tier 1b — traversal quick wins

**ES-6 — Shrink the crossing frame.** 🟢–🟡 / 🟡. Skip `OnDataPopulated` in `Chunk.Reset` when the
`ChunkData`'s active buckets are already valid (a `ChunkData` flag, cleared in `Reset` per
pool-reset-safety); spread visual activations and the unload row over a few frames; `DT-3` (gate the
visualization set on `visualizationMode != None`).

**ES-7 — Cut lighting main-thread work without changing output.** 🟢 / 🟡. In the order the lighting
sweep recommended:
1. `FillUniformSkylight` → memcpy from a static per-level slab (helps lighting *and* meshing fills);
2. drop `RecalculateCounts` from `ApplyJobLightMap`, after a dev-build probe proves counts never drift
   across a merge;
3. band-clamp the merge — skip sections outside `[BandMinY, BandHeight)` and stop the air-section
   rent/classify/return churn (gates: B75–B78 band differentials, B54/B55);
4. gate merge-triggered re-meshes on `lightChanged`, and neighbor requests on a border-changed mask
   computed in the same loop (measure post-delivery mesh amplification first; check whether diagonal
   neighbors can be left with stale corner light — currently never re-meshed by a merge).

**ES-8 — Budget the unbudgeted passes.** → `SL-2`. 🟡 / 🟡. Give `ApplyModifications` a ms ceiling
with the retry contract the other passes use; move the disk-hit populate continuation into a budgeted
pump, and move its `RecalculateCounts` to the deserialize thread via a flat opacity table (the
`World.IsActiveById` pattern); a merge ceiling until ES-12 makes the merge cheap.

**ES-9 — Allocation-free I/O path.** → `SL-1`, `SL-3`, `OM-3`. 🟡 / 🟡.
- Answer "is this chunk on disk?" from the open region's offset table on the main thread — a miss then
  schedules generation the same frame with no `Task` (CP-3: a fault must still throw, never read as
  "absent").
- Pooled read payloads; static zero padding; stackalloc headers; one dedicated I/O worker with bounded
  queues instead of a `Task.Run` per chunk; static `ContinueWith`.
- On unload the `ChunkData` is returned to the pool right after the save fires, so the save can **take
  ownership of the sections** instead of copying up to 192 KB (respect CP-6's retained-snapshot retry).
- `ExpandStructure` iterator → pooled list fill; `ListPool` for pending-mod lists; gate the
  `[LIGHTING RESCUE]` log on `enableDiagnosticLogs`; confirm the shipping build profile's `Debug.Log`
  stack-trace setting (`ProjectSettings` says ScriptOnly; the 2026-08-15 lean build used MethodOnly).
- *Measured 2026-10-03 (§2.2.1):* the sites this item owns total ~15 KB per generated chunk — `ExpandStructure`
  8.0 KB, async/`Task` I/O 4.7 KB, `RegionFile.SaveChunkData` 1.95 KB. The section-ownership save is worth
  0.45 KB of `Queue` growth in `CreateSerializationSnapshot` as garbage; its case is copying, not GC. The dominant
  save-path cost is ES-26's, not this item's.

**ES-10 — Decide what to persist (open decision).** 🟢 / 🟡. `ChunkData.Populate` marks every generated
chunk modified (`ChunkData.cs:395`), so all generated terrain is serialized and written. Keeping it is a
disk cache (re-entry skips generation and re-lighting); dropping it halves unload I/O and allocation and
regenerates on return (~1.5 ms worker time + lighting). Also fixes the comment drift at
`World.cs:3825–3829`, which assumes unmodified chunks exist on the persist-light-pending arm.
*GC, measured 2026-10-03:* the unload save path is 104.2 KB of the 127.4 KB per generated chunk at 200 m/s, so
dropping unmodified saves would remove ~82 % of traversal garbage — but ES-26 removes ~77 % without the persistence
trade-off. Decide this item after ES-26, on disk I/O and re-entry cost.

**ES-11 — Skip lighting on stable disk loads.** → `P-5` (⚠️ format bump + AOT step). 🟡 / 🟡.

**ES-25 — Mesh upload and vertex-count cuts on today's renderer.** 🟢–🟡 / 🟢–🟡. Numbered last, but
belongs here in Tier 1b. Take a Frame Debugger + profiler rendering baseline at vd 10 first (draws,
SetPass, vertices/frame, GPU ms, and the shaders' "SRP Batcher: compatible" line), since none exists.
1. **16-bit indices** when a section has ≤ 65 535 vertices, UInt32 fallback (indices are ~16 % of upload
   bytes and already section-local).
2. **Bucketed vertex capacity** so `SetVertexBufferParams` stops reallocating on every edit, and a
   GPU-timed A/B of `MarkDynamic` on rarely-changing meshes.
3. **Per-section dirty mask** in `ApplyMeshData` (e.g. a per-section output hash from
   `MeshPostProcessJob`): a single block edit re-uploads all 8 sections today. A missed section leaves a
   stale mesh, so it needs a meshing-suite guard.
4. **Shared-vertex sub-quad grids** — (N+1)² vertices instead of 4N² for VO-9b/SS faces: −44 % at N = 2,
   −61 % at N = 4, lossless (each sub-vertex is already shaded at its own position; the diagonal flip only
   reorders indices). Turns SS-3's 1.4–1.7× into ≈1.2–1.4× *(inferred)*.
5. A **DepthOnly pass** on the block shaders — the prerequisite for any depth prepass, SSAO or GPU
   occlusion; do not enable depth priming unless a capture shows the frame fragment-bound.

**ES-26 — Stop copying section arrays in the save serializer.** 🟢 / 🟢. Added 2026-10-03 from the ES-0 capture
(§2.2.1). `ChunkSerializer.WriteSection` (and one site in `WriteChunkInternal`) hands each voxel/light array to
`BinaryWriter.Write(MemoryMarshal.AsBytes(span))`; in Unity's class libraries that overload copies the span with
`ToArray()` — 20 480 B allocated per 16 KB write in an isolated Editor test, against 0 B for `Write(byte[], int, int)`.
**98.5 KB of the 127.4 KB per generated chunk (77.3 %)**, all on ThreadPool save threads. Write the same bytes without
the copy (e.g. through a reused per-thread scratch `byte[]` and the `byte[]` overload); whether
`BaseStream.Write(ReadOnlySpan<byte>)` on the LZ4/GZip streams is allocation-free is unverified. The bytes on disk
must not change: gate on the serialization round-trip and deserialization-robustness suites, then re-run the ES-0
capture.

**ES-27 — Stop the lighting BFS queues regrowing.** 🟢 / 🟢. Added 2026-10-03 from the ES-0 capture (§2.2.1).
`ChunkData`'s managed `Queue<LightQueueNode>` skylight/blocklight queues grow through `Queue<T>.SetCapacity` ~5 700
times in a 30 s 200 m/s window — 8.9 KB per generated chunk (7.0 %), from `ApplyCrossChunkLightMod` in the lighting
merge and `ModifyVoxel` in `ApplyModifications`. `Reset` only `Clear()`s them (capacity kept) and the `ChunkData` pool
had 0 misses, so the regrowth is pooled instances passing their own high-water mark *(inferred, not instrumented)*.
Candidates: an initial capacity sized to the observed peak, or moving the queues native (ES-18a territory). The
lighting-merge `List`/`HashSet` growth (0.5 KB per chunk) rides along.

**ES-28 — Stop the UI band/blur passes allocating every frame.** 🟢 / 🟢. Added 2026-10-03 from the PM-1 Play-mode
smoke capture (Editor, 855 frames, GC.Alloc call stacks, a streaming world with the HUD up): **643.8 KB of the
651.7 KB main-thread allocation (98.8 %, ≈ 0.75 KB/frame — ≈ 180 KB/s at 240 fps) is the UI band composite recording
its Render Graph passes**, every frame, whether or not anything changed:
- `UIBlurChain.Record` / `PublishGlobal` (253.5 + 55.0 KB) build pass names by concatenation —
  `passLabel + " Iter " + i`, `passLabel + " Set Global"`.
- `UIBandCompositeRendererFeature` (70.1 + 45.0 KB) builds `"UI Band " + band` (enum boxing + concat) and
  `label + " Draw"` per band.
- `UIBandLayers.SortingValueOf` (220.2 KB, ~6 calls/frame) scans `SortingLayer.layers`, which returns a fresh array,
  and reads each `layer.name`, a marshalled string.

Fix: cache the pass names per band and iteration count (rebuilt only when the blur settings change) and the sorting
values (resolved once; sorting layers change only in the Editor). Gate on the UI Band Layers and UI Blur Render
suites, then a repeat GC.Alloc capture showing none of these sites. Recorded in Editor Play mode with one camera
blurring per frame (`PublishGlobal` 854 calls in 855 frames); the same code runs in players, but the per-frame figure
there is unmeasured.

### Tier 2 — pipeline work on today's data model

**ES-12 — Jobified lighting merge.** → `P-3`. 🟡 / 🟡. After ES-7 shrinks it, measure the internal split
(P-3's first step), then emit a per-section dirty mask from the job and swap whole sections.

**ES-13 — Initial skylight from the heightmap.** 🟡 / 🟡. The generation job stamps sky 15 above the
heightmap and marks the uniform sections; initial lighting seeds only the frontier cells (y ≤ the
neighbors' max heightmap, openings). Expected ~10× fewer initial BFS nodes and most of the initial
cross-seam mod flood removed *(inferred)*, and **it removes the 200 k-cap hazard that taller worlds
hit** (§2.4). Same family as `LIGHTING_SYSTEM_OVERVIEW.md` §5.3/§5.4. Gate: bit-identity vs today's
initial lighting on the oracle + seam baselines.

**ES-14 — Generation critical path.** 🟡 / 🟡. Cache worm paths per cell instead of re-simulating up to
33×33 cells for every chunk in a single-threaded job; → `WG-1` (the terrain job writes every element of
`outputMap`, so it can rent from `ChunkJobArrayPool` without clearing; masks still clear) + `WG-2`.
Determinism gate: byte-identical generation output for fixed seeds.

**ES-15 — Coarse-lattice density noise.** 🟡 / 🟡. ⚠️ Seed-breaking → a world-version-gated generator
profile only (4×8×4 lattice + trilinear interpolation; 8–30× fewer 3D samples *(inferred)*). Generation
is not the load-time cause, so this is a height-scaling and worker-headroom item; re-tune caves with
the `cave-tuning` workflow.

**ES-16 — Ordering.** → `P-7`. 🔴 / 🔴. Predictive priority `p + v·t_lead` shared by the generation
queue, the (hash-ordered) lighting ready set and the FIFO mesh queue.

**ES-17 — Keep meshes for the load ring.** 🟡 / 🟡. Chunks inside LoadDistance but outside view hide
their renderers instead of pooling the visual, so re-entry costs neither a rescan nor a re-mesh. Costs
mesh memory; becomes free under ES-20.

### Tier 3 — architecture

**ES-18 — Native-resident chunk store (P-2 Layer 2, revived).** 🔴 / 🔴.
- **18a:** per-section native blocks (voxels + light), 3D-keyed, sections still pooled; jobs keep their
  per-job snapshot as native `MemCpy`. Removes the managed voxel heap and its GC tracing cost.
- **18b:** double-buffered publish (jobs read the published version, main-thread edits write the back
  version, publish at a frame boundary) → zero-copy jobs, merge = publish, Burst-side scheduling. The
  P-2 archive's §5 rules (read pins, deferred edits, aliasing) are the design input; the snapshot model
  in `LIGHTING_SYSTEM_OVERVIEW.md` §4.3 is what 18b replaces and must re-justify. Prerequisites: ES-12,
  `LP-8` (production-scheduler harness).

**ES-19 — Uniform / palette sections in memory.** 🔴 / 🟡. First, sections made of one block (all stone,
all air) store one value, mirroring `SectionUniformSkyLevel` for light; later, per-section palettes with
bit-packed indices (runtime first; `CHUNK_PALETTE_MAPPING.md`'s disk palette can ride the same format
bump). Turns Tier A memory from ×3–5 into ≈×1.2–2 (`WORLD_SCALING_ANALYSIS.md` §2.2).

**ES-20 — GPU-driven terrain renderer.** 🔴 / 🔴. Replaces `GS-6`'s BatchRendererGroup idea with a
buffer renderer, informed by the Incandescent Games pipeline (PlanetSmith, ~16³ chunks, the closest
public analogue): one GameObject per chunk → shared buffers took it from 25–40 fps to ~50 fps; **GPU
hierarchical (octree) frustum culling was the biggest step (→ 170+ fps)** — per-chunk culling alone gave
nothing; **paged fixed-capacity pools fixed the remaining spikes** (they came from free-slot search in
one giant buffer, not GC); packing 48 → 16 B/vertex gave 200+ fps with no spikes, at 120 chunks of view
distance (~1.8 M chunks, ~1 B vertices).

| Piece | Design |
|---|---|
| Face record | **~16 B per face**: section-local integer/fixed-point position, face direction (normal rebuilt in the shader), texture-array layer, flags (foliage sway, emissive) in 4–8 B, plus **per-corner RGB + sky light at 4 bits/channel (8 B)** — today's shader consumes per-corner smooth light, which 8 B cannot carry. Drops to **8 B** once light is sampled from a `VX-1` volume. Vertex pulling from `SV_VertexID`; atlas UVs → a texture array (migration) |
| Storage | Paged `GraphicsBuffer`s (fixed-capacity pages or Sodium-style regions of 8×4×8 sections) with a per-page free list; writes via `LockBufferForWrite`/`UnlockBufferAfterWrite` (ring-buffered over 2–3 frames so in-flight GPU reads are never overwritten) or `SetData(NativeArray, …)` straight from the meshing job — no `Mesh` objects |
| Section records | Structured buffer: page slice, 6 per-face-direction sub-ranges, integer section origin, flags |
| Culling | Hierarchical region/octree frustum test in a **Burst job first** (enough at ~26–34 k sections), compute shader later; consumes ES-21's visible set; face-direction culling skips the back-facing groups |
| Draw | `Graphics.RenderPrimitivesIndexedIndirect` / `RenderPrimitivesIndirect` (verified in 6000.6, need `UNITY_INDIRECT_DRAW_ARGS` + `UnityIndirect.cginc`), **or** BRG `BatchDrawCommandProcedural(Indirect)` (verified in 6000.6), which rides URP's renderer lists and may give shadow/depth/motion passes for free — **prototype both**. Multi-draw `commandCount = N` is reportedly emulated as N API draws, so issue **few large commands per page/region** with a per-face section index rather than one command per section |
| Origin | Section origins stay integer; a floating-origin shift is one global uniform |
| Special paths | Custom/cross meshes (FL-4 lets them escape the cell), sloped fluid surfaces with per-vertex flow + shore data, and VO-9b sub-quads (N² sub-records with a fixed-point sub-cell offset/size — moving that shading to the fragment is already rejected, it reds B58) keep a mesh path first. Leaves/glass need **no** sorting (cutout + ZWrite); only the fluid queue blends |
| URP ordering | Terrain must draw **in the opaque queue, before the opaque-texture copy** (water refraction samples it), with depth in the active buffer before `CloudPrepassRendererFeature` (AfterRenderingSkybox, depth ReadWrite) and in the after-transparents depth copy `UnderwaterOverlayRendererFeature` reads; MSAA 2× must work with the new draw. Bloom reads color only; no SSAO/DepthNormals consumer exists |
| Middle path | **BRG + `BatchDrawCommandProceduralIndirect` + vertex pulling** (verified in 6000.6): URP calls the BRG culling callback per view and runs whatever LightMode passes the shader has, so most URP integration comes free, and the callback is the natural consumer of ES-21's visible set. Needs DOTS-instancing shader variants |

Memory per quad: 4 × 32 B + 6 × 4 B = **152 B** today vs **~16 B** packed with per-corner light (~9.5×),
**8 B** with volume light (~19×), before any greedy merge.
Staging: **G0** — vertex compression inside today's `Mesh` path (works with `UInt32` attributes, no
renderer change; see §8 item 4 on positions); **G1** opaque cubes in paged buffers behind a settings
toggle beside today's renderer; **G2** hierarchical culling; **G3** transparents/fluids/custom meshes;
**G4** retire per-section GameObjects.

**ES-21 — Cave/section visibility culling.** → `GS-5` Phases 1–3. 🔴 / 🟡. Outputs a visible-section set
consumed by ES-20 (and by `SetOcclusionCulled` until ES-20 lands). PlanetSmith's author found occlusion
culling slower on his first attempt — the connectivity-graph BFS (Checchi) is CPU-side and position-only
per GS-5 §7.6, so it does not share that cost model; measure anyway.

**ES-22 — Greedy meshing.** → `MR-8`. 🔴 / 🔴. Binary/bitmask greedy per section, easiest on ES-20's face
record (size fields) with texture arrays and per-pixel light/AO from `VX-1`/`VX-8`.

**ES-23 — Far-field LOD.** 🔴 / 🟡. Beyond ~vd 16, mesh 2×/4× downsampled sections (or merged column
impostors) into the same ES-20 pages; full detail near the player only. Required for vd 32+ at taller
heights.

**ES-24 — Section as the pipeline unit.** 🔴 / 🔴. Generation output, lighting and meshing jobs operate on
section ranges (LI-2's band generalized), so per-chunk cost tracks content and Tier A becomes a
constants + migration change. Prerequisites: ES-13, ES-18, ES-19.

### 4.1 Expected impact by symptom

| Symptom | Biggest levers (in order) |
|---|---|
| Time-to-stable | ES-0 (attribute) → ES-1, ES-2, ES-3 → ES-4, ES-11, ES-13 |
| Traversal spikes | ES-0 (see them; H-1 §2.2.1 refuted, **garbage attributed 2026-10-03**) → **ES-26** (77 % of per-chunk garbage), ES-27, ES-9 (~12 %) → ES-6, ES-8, ES-7, ES-25 → ES-18a (heap); ES-10 after ES-26; ES-28 (steady-state UI garbage, every frame) |
| Height / vd 32 | ES-13 (200 k cap), ES-25, ES-19, ES-18, ES-20 + ES-21 + ES-23, ES-24 |

---

## 5. Prerequisites & integration points

- ⚠️ **ES-0 gates every verdict.** P-4/P-9 history shows editor numbers mislead; GO/NO-GO calls are
  same-build Master IL2CPP A/Bs per `perf-benchmark` (rollback flags listed in
  `SettingsManager.OverlayBenchmarkSettingsFromDisk`).
- Pipeline-touching items (ES-3, ES-6, ES-7.4, ES-8, ES-12, ES-13, ES-16, ES-18) require the
  `chunk-lifecycle` skill. Meshing is gated on `AreNeighborsMeshReady` (`CHUNK_LIFECYCLE_PIPELINE.md`
  §3.3); the rule and skills that said `AreNeighborsReadyAndLit` were corrected 2026-10-03.
- ES-20 must follow `SHADER_CONVENTIONS.md` (`#pragma target 4.5`, centroid varyings) and keep GS-5's
  set-output contract.
- ES-11 and ES-19's on-disk half are format changes → `serialization-migration` skill.

---

## 6. Constraint compliance checklist

| Project constraint | How this design complies |
|---|---|
| Voxels are packed `uint`s, no per-voxel objects | ES-18/ES-19 change *where* the `uint`s live (native, uniform/palette sections), never their packing |
| Burst jobs 100 % Burst-compatible | New jobs (merge, culling, face packing, sky stamp, worm cache) are native + `Unity.Mathematics` only |
| No GC / LINQ in hot paths | ES-6/ES-8/ES-9 target ~0 B/frame streaming; ES-18a removes the managed voxel heap; ES-20 removes per-section `Mesh` churn |
| Pooling conventions | ES-3/ES-14 extend `ChunkJobArrayPool`-style pools; ES-20's paged free lists are the GPU-side pool |
| No BinaryFormatter/JSON for terrain | ES-11/ES-19 extend the region binary format through AOT migration steps |
| BlockIDs constants, no raw IDs | No item introduces block IDs |

---

## 7. Phased implementation plan

| Phase | Scope | Effort | Depends on | Status |
|---|---|:---:|---|---|
| **ES-0 — Measure** | Spike-visible capture, drain stamp, crossing slots, GC.Alloc capture | 🟢 | — | GC.Alloc capture ✅ 2026-10-03 (§2.2.1); PM-1 store ✅ 2026-10-03; PM-2 frame tier ✅ 2026-10-04 (Master-confirmed); PM-3 crossing slots + remainder + counters ✅ 2026-10-04 (Master-confirmed); PM-4 job/I-O counters ✅ 2026-10-05 (Master-confirmed); PM-8 tick breakdown ✅ 2026-10-05 (Master-confirmed); PM-6 per-phase frame stats + Capture export ✅ 2026-10-06 (Master-confirmed); drain stamp open, unowned |
| **ES-1 — No frame-paced load** | Completion counter at `World.cs:1064` — execution packet §7.1 | 🟢 | — | ✅ 2026-10-02 (in-game) |
| **ES-2 — Calibration** | Robust OM-1 lighting probe — execution packet §7.1 | 🟢 | — | ✅ 2026-10-02 (in-game) |
| **ES-3 — Loading mode** | SU-1 + SU-2, pooled/banded/shared startup snapshots | 🟡 | ES-0 | — |
| **ES-4 — First-Update burst** | Pool prewarm, lazy borders, async scene load | 🟢 | — | — |
| **ES-5 — PSO warmup** | Shader state prewarm | 🟢 | — | — |
| **ES-6 — Crossing frame** | `OnDataPopulated` skip (price first — packet §7.1), spread activations, DT-3 | 🟡 | ES-0 | ES-6.1 ✅ 2026-10-02 (in-game); rest open |
| **ES-7 — Lighting cuts** | Slab fill, counts, band-clamped merge, remesh gating | 🟢 | ES-0 | — |
| **ES-8 — Budgets** | `ApplyModifications`, SL-2 pump, merge ceiling | 🟡 | ES-0 | — |
| **ES-9 — I/O path** | Offset-table probe, SL-1, section-ownership save, OM-3 | 🟡 | ES-0 | — |
| **ES-10 — Persistence policy** | Open decision: keep saving unmodified terrain, or only edits | 🟢 | — | — |
| **ES-11 — Stable-load bit** | P-5 (⚠️ format) | 🟡 | ES-0 | — |
| **ES-25 — Upload/vertex cuts** | Rendering baseline, 16-bit indices, buffer capacity, per-section dirty upload, shared sub-quad grids, DepthOnly pass | 🟡 | ES-0 | — |
| **ES-26 — Serializer span copy** | Write section arrays without `ReadOnlySpan<T>.ToArray()` — byte-identical output | 🟢 | — | — |
| **ES-27 — BFS queue regrowth** | Presize or nativize `ChunkData`'s lighting BFS queues | 🟢 | — | — |
| **ES-28 — UI pass garbage** | Cache the UI band/blur Render Graph pass names and sorting values — ≈ 0.75 KB/frame of steady-state garbage | 🟢 | — | — |
| **ES-12 — Jobified merge** | P-3 | 🟡 | ES-7 | — |
| **ES-13 — Sky from heightmap** | Frontier-seeded initial lighting | 🟡 | ES-12 | — |
| **ES-14 — Gen critical path** | Worm cache, WG-1, WG-2 | 🟡 | ES-0 | — |
| **ES-15 — Lattice noise** | Versioned generator profile | 🟡 | ES-14 | — |
| **ES-16 — Ordering** | P-7 | 🔴 | ES-0 | — |
| **ES-17 — Load-ring meshes** | Hide instead of pool | 🟡 | ES-0 | — |
| **ES-18 — Native store** | 18a native arrays → 18b double-buffered zero-copy | 🔴 | ES-12, LP-8 | — |
| **ES-19 — Uniform/palette** | Single-block sections → palettes | 🔴 | ES-18a | — |
| **ES-20 — GPU renderer** | G0–G4 | 🔴 | ES-0 | — |
| **ES-21 — Cave culling** | GS-5 Phases 1–3 | 🔴 | — | — |
| **ES-22 — Greedy** | MR-8 on ES-20 faces | 🔴 | ES-20, VX-1 | — |
| **ES-23 — Far LOD** | Downsampled far sections | 🔴 | ES-20 | — |
| **ES-24 — Section unit** | Section-ranged jobs → Tier A | 🔴 | ES-13, ES-18, ES-19 | — |

**Minimal set with standalone value:** ES-0 + ES-1 + ES-2 (hours each) attack load time directly;
ES-6 + ES-7 + ES-8 + ES-9 attack the spikes. None needs an architecture change. ES-18 and ES-20 are
independent tracks: ES-20 is the larger visible win at high vd, ES-18 the prerequisite for height.

**Validation is built alongside, not after:** ES-1/ES-3/ES-8 extend `Validate Pipeline Backpressure`;
ES-7/ES-12/ES-13/ES-18 keep the lighting suite bit-identical (oracle, seam baselines B54/B55, band
differentials B75–B85); ES-14/ES-19 need a generation-determinism differential (`NS-*` roadmap);
ES-6 needs a pool-reset baseline (B34 pattern) for its new flag; ES-20 needs a face-packing round-trip
suite plus in-game visual confirmation, since no suite executes the GPU.

### Extension roadmap

| Version | Extension |
|---|---|
| **v2** | Compute-shader culling with Hi-Z occlusion; GPU translucency sort |
| **v2** | Closed-loop frame-time budget replacing `cap × 60` (P-9's extension roadmap) once ES-18 removes the main-thread item cost |
| **v3+** | Cubic chunks (Tier C) on ES-18's 3D-keyed sections — own design doc |

### 7.1 Execution packets — ES-1, ES-2, ES-6.1 (planned 2026-10-02; ES-1 and ES-2 shipped 2026-10-02; ES-6.1 priced GO and shipped 2026-10-02)

Written with the `create-implementation-plan` protocol (fact sweep → draft → adversarial review →
decision menu, settled 2026-10-02) so a future session can execute them directly. **Re-verify
every anchor first** — they were read at `376778fe`. Each packet is independent and is its own commit
(`Optimized:` / `Fixed:` single-line messages per the commit-style rule). The `chunk-lifecycle` skill is
mandatory for ES-1 and ES-6.1.

#### ES-1 — counter barrier for the initial load

**Goal.** The startup coroutine waits for its initial loads by *completion*, not by spending one frame per
load (≈2.2 s at 240 Hz, ≈8.8 s at 60 Hz today, inferred from the Player.log arithmetic in §2.1).

**Verified (at `376778fe`).**
- `World.cs:1051–1064`: `List<Awaitable> loadTasks`, filled by `LoadOrGenerateChunk(chunkCoord)` for every
  coord, then `foreach (Awaitable task in loadTasks) yield return task;`. `loadTasks` has no other readers.
- `LoadOrGenerateChunk` (`World.cs:1155–1183`) is `async Awaitable`, the CP-3 wrapper: captures the admitted
  instance + `LifecycleEpoch`, rethrows `OperationCanceledException`, catches everything else (count →
  `LogError` → identity-guarded `IsLoading = false`). `LoadOrGenerateChunkInner` (`:1316`) completes
  **synchronously** when the data is already populated (`:1326`) or persistence is off (`:1521`);
  otherwise it awaits `StorageManager.LoadChunkAsync` (`Task.Run`, `ChunkStorageManager.cs:184,194`) and
  resumes on the main thread via the sync context.
- The second caller, `CheckViewDistance` (`World.cs:4053`, `_ = LoadOrGenerateChunk(...)`), is untouched.
- `Update` returns early until `_isWorldLoaded` (`World.cs:2544`); no load path takes a cancellation token.

**Decided 2026-10-02: counter barrier only.** It is the smallest change that removes the frame pacing;
overlapping Phase 1 with the loads buys a little more time at the cost of new interleaving inside the
startup coroutine.

**Plan.**
1. `World.cs`: add `private int _startupLoadsPending;` (instance field, so no static/domain-reload rules) and
   a private `async Awaitable LoadOrGenerateStartupChunk(ChunkCoord c)` that does
   `try { await LoadOrGenerateChunk(c); } finally { _startupLoadsPending--; }` — the `finally` covers
   success, the CP-3 fault arm (swallowed inside) and the OCE rethrow.
2. Startup loop: set `_startupLoadsPending = 0` before it; per coord **increment before the call**
   (a synchronous completion decrements immediately), then `_ = LoadOrGenerateStartupChunk(chunkCoord);`.
   Delete `loadTasks`.
3. Replace the `foreach … yield return task` with
   `while (_startupLoadsPending > 0 && elapsed < STARTUP_LOAD_TIMEOUT_SECONDS) yield return null;` plus a
   `Debug.Assert(_startupLoadsPending >= 0)`. On timeout: one `LogError` naming the pending count, then
   continue — matching Phase 1's existing "Forcing exit" precedent. The timeout is a named `private const`
   (the old loop could never hang; the counter can, if a continuation never runs).  
   **Gate:** `unity recompile --format json` clean.
4. In-game gate (no suite can reach `StartWorld`, a play-mode coroutine): new world and existing world,
   each launched twice — compare the `Initial data load / generation took X ms` line (`World.cs:1074`)
   before vs after, and confirm no timeout error and a normal startup report (same iteration counts).  
   **Prove-red:** with the dev-only CP-3 load-fault seam (`ChunkStorageManager.ThrowIfInjectedLoadFault`,
   `:198`) armed for one startup chunk, startup must still complete; temporarily moving the decrement out
   of the `finally` must reproduce the timeout.

**Assumptions.**
- *The old wait was frame-paced, not I/O-bound.* Inferred from ~4.3 ms/yield on both new (disk-miss) and
  loaded (disk-hit) worlds. If loads really take ~2.3 s of I/O, the counter saves little — step 4 measures it.
- *Unity's `Awaitable` is never re-awaited here* — the counter holds no `Awaitable` references at all, which
  sidesteps the unverified pooling rules.

**Not doing.** Overlapping Phase 1 with the loads (rejected above); the in-flight cap and
loading-mode budgets (ES-3); anything in `CheckViewDistance`.

**Executed.** As planned: `_startupLoadsPending` + `LoadOrGenerateStartupChunk` + the bounded wait
(`STARTUP_LOAD_TIMEOUT_SECONDS = 60`). The prove-red in step 4 needed one correction. The seam is armed with
`ChunkStorageManager.InjectLoadFaults(1)` from the main menu in play mode, because its count resets on
play-mode entry. **Moving the decrement out of the `finally` is a no-op under that seam:** the CP-3 wrapper
swallows the injected fault, so the await returns normally and a decrement after it still runs (the
`finally` is load-bearing only for the rethrown cancellation). Measured in the Editor at an initial radius
of 3 (81 chunks): seam armed → fault logged on one chunk, startup completes, no timeout; the planned
mutation → the same; dropping the decrement → `timed out after 60 s with 81 of 81 still pending`, startup
continues to completion.

**In-game gate (Editor/Mono, seed 0, 1 089 chunks, initial radius 15).** The load wait (total − startup
coroutine) fell from 5 132 → 482 ms on a settled existing world (one run each) and from a mean 5 833 →
1 048 ms on a new one (two runs each), i.e. from ≈5 ms to <1 ms per chunk — the frame pacing assumed in
§2.1 is confirmed and the I/O-bound assumption refuted. Totals: existing 6 455 → 1 993 ms, new mean
18 251 → 15 563 ms.  
**Open finding — the new-world coroutine got slower, reproducibly:** mean 12 418 → 14 515 ms (+2.1 s, both
runs), mostly lighting completion (7 435 → 9 004 ms); the existing world moved 1 323 → 1 511 ms. Mechanism
not attributed. Leading candidate: GC debt from generation, which the old ~1 000 near-idle frames let the
incremental collector absorb, now lands inside Phase 2's long frames (consistent with new ≫ existing, and
with H-1's ~142 KB per generated chunk). Editor asynchronous Burst compilation is a weaker candidate, since
compiled jobs are cached across play sessions. ES-0's per-frame GC capture is the instrument that
settles it.
*2026-10-03, ES-0 attribution (§2.2.1):* the GC-debt lead is weaker than assumed. 81.8 % of the per-chunk garbage is
the unload save path, and no runtime save caller (`UnloadChunks`, quit, pause-menu save, the save key, benchmark end)
runs during the initial-load coroutine; the generation-side remainder is ~20 KB per chunk, ~11 MB over 529 chunks
*(inferred — startup was not captured)*. Still unattributed; a startup capture with the same tooling settles it.

#### ES-2 — robust OM-1 lighting calibration

**Goal.** The reference machine calibrates to its tuned budgets (32 light / 10 mesh jobs per frame) on
every cold launch, instead of 15, without raising the rate on any machine (P-9's rule).

**Verified (at `376778fe`).**
- `StartupCalibrationProbe.cs:41–47`: `WARMUP_ITERATIONS` 2, `MEASURE_ITERATIONS` 7 (8 / 99 in
  `BASELINE_CALIBRATION` mode); statistic = median (`:363`, `:122`); the light timer (`:275–278`) covers
  `Schedule()` + `Complete()`; the mesh leg runs first (`:135–136`); spreads are logged only in BASELINE mode.
- `DeviceCalibration.cs:70` `CalibrationVersion = 1`; `:98–105` defaults 10 / 32, references 0.952 / 0.604 ms,
  light floor 4 / ceiling 128; `:202–207` `budget = clamp(round(default × ref / median))`. Log check:
  32 × 0.604 / 1.308 = 14.78 → **15**. Mesh came in *faster* than its reference (0.868 vs 0.952 ms) while
  light was 2.17× slower, which rules out a whole-machine clock effect.
- `SettingsManager.cs:1063` re-calibrates only when `calibrationVersion < CalibrationVersion`;
  `ApplyCalibration` (`:1100–1124`) overwrites all five calibrated fields and stamps the version;
  `RecalibrateDevice()` (`:1140`) has **zero callers**. Benchmark mode never calibrates.
- In an IL2CPP player Burst is AOT, so the logged 1.308 ms contains no compile cost; in the Editor, async
  Burst compilation makes calibration unreliable.

**Decided 2026-10-02:** anchor = **minimum of batch medians**, with the reference constants
**re-measured under the same statistic**, so the reference machine still maps to exactly its tuned budgets
and no machine's rate rises; rollout = **bump `CalibrationVersion` 1 → 2, never lowering a value already
set higher by hand**, because a hand-tuned budget is intent the re-probe cannot see.

**Plan.**
1. `StartupCalibrationProbe.cs`: ≥ 8 warm-up iterations (and ≥ 20 ms of warm-up per leg), then 3 batches ×
   11 samples, **legs interleaved batch by batch** (mesh, light, mesh, light …) so one contention window
   cannot land on a single leg; anchor = min of the 3 batch medians. Log one line per leg —
   min / median / max / batch medians — in every build, so future logs can be attributed.
2. Pure helpers (`internal static`, testable): `ComputeAnchor(batchMedians)` and the existing
   `MapThroughputBudget`.
3. **Re-anchor the references:** one `BASELINE_CALIBRATION = true` capture in an IL2CPP player on the
   reference machine, using the new statistic → new `DeviceCalibration` reference constants (and fix the
   `DeviceCalibration.cs:139–141` comment, which says "editor-measured" while `:95–97` says IL2CPP).
   Manual step (a player build on the reference machine); without it the min-anchor reads faster
   than the old median references and would
   **raise** caps on fast machines.
4. `CalibrationVersion = 2`; in `ApplyCalibration`, during a version-upgrade re-probe only, write each field
   as `max(existing, calibrated)`. Fresh installs (no prior file) take the probe value as today.
5. Validation (new scenarios beside the backpressure budget-math ones, or a small calibration suite):
   identity (reference anchor → exactly 10 / 32); log-like batches {1.308, 0.62, 0.61} → anchor 0.61 →
   32 (**prove-red:** revert the anchor to a pooled median → the slow value returns); clamp and `≤ 0` cases;
   the upgrade `max` rule (existing 32, calibrated 15 → 32; existing 15, calibrated 32 → 32).  
   **In-game gate:** 5 cold launches on the reference machine resolve 32 ± 1 light and 10 ± 1 mesh (the OM-1
   §8 criterion).

**Assumptions.**
- *The 15 comes from a warm-up tail, not real slowness* (queue block growth, page commits, worker wake-up):
  unverified, because shipping logs only print medians. Step 1's always-on spread logging tests it.
- *No record distinguishes hand-edited fields from an earlier calibration*, so step 4's `max` also keeps an
  earlier noisy-**high** calibration. Noise biases slow, so this should be rare; accepted.

**Limitation.** Editor calibration stays unreliable while Burst compiles asynchronously. **Not doing:**
a desktop floor (only if step 5's cold-launch gate still fails), a re-probe at world handoff, or a settings
button for the unused `RecalibrateDevice()`.

**Executed — and the first assumption was refuted.** Steps 1–5 as planned, with three deviations: the
helpers are `public static` (the suites compile into `Assembly-CSharp-Editor`, with no
`InternalsVisibleTo`); the keep-higher merge applies only to files calibrated before (version ≥ 1), so a
never-calibrated file still takes the probe; and `MapThroughputBudget` now clamps before its `int` cast
(B24 found a tiny positive anchor wrapping to the floor). B23/B24 live in `Validate Pipeline Backpressure`;
each of three prove-red mutations (pooled-median anchor, unconditional probe in the merge, a swapped merge
field) reds only its own assertion.  
The step-3 capture (IL2CPP Master, 5 cold launches × 15 repetitions) put the light anchor at
**1.310–1.317 ms in every launch** (±0.5 %), the old 7-sample median (1.308) included — **there was no
warm-up tail.** The June reference (0.604 ms) was stale: the lighting job grew ~2.17× on this machine since
it was measured (Bug 13–18 fixes, LI-2 band plumbing, VO-3/VO-4), while meshing got ~10 % faster
(0.952 → 0.855 ms). The references were re-anchored to **0.855 / 1.310 ms** (median of the 75 anchors), so
this machine maps back to exactly 10 / 32; versus the stale state, every device's light budget rises ~2.17×
and its mesh budget falls ~10 % — OM-1's June relative mapping restored, not a new rate. The new statistic
still earned its place: it discarded two contended mesh batches (1.46–1.49 ms) in that capture.

**In-game gate passed (2026-10-02).** On the re-anchored IL2CPP Master build, fresh cold launches resolved
**32 light / 10 mesh** every time; the four launches whose `Player.log` survived read light anchors
1.304–1.322 ms and mesh anchors 0.854–0.869 ms (light rounds down to 31 only above ≈1.331 ms), and the fresh
`settings.json` was stamped `calibrationVersion 2` with the probe values unchanged.

#### ES-6.1 — price the redundant `OnDataPopulated` rescan before touching it

**Goal.** Decide with numbers whether skipping the rescan on visual re-attach is worth removing the safety
net it provides.

**Verified (at `376778fe`).**
- `Chunk.Reset` (`Chunk.cs:145–155`) runs `OnDataPopulated()` whenever the re-linked `ChunkData.IsPopulated`.
  `OnDataPopulated` (`:230–260`) does **only** active-voxel registration (`ChunkData.AddActiveVoxel` per active
  id) — no lighting, mesh or wake side effects.
- Both population-time registrations use `Chunk?.` (`WorldJobManager.cs:1168,1170`, `World.cs:1369`), so they
  are **skipped for data-only chunks** — every startup chunk and the whole LoadDistance > viewDistance ring.
  For those, the generation job's active-voxel list is discarded and the `Reset` scan is the **only** full
  registration. A "buckets valid" flag alone therefore saves work only on **re-entry**, not on forward travel.
- Ticking runs only for chunks with a visual (`World.cs:2452–2468`, `Chunk.cs:369–377`), so voxels quiesce
  only while visible; today's rescan re-wakes every quiesced voxel on re-entry, which is also the safety net
  that would hide any missing-wake bug (the class behind Fluid #17 and Behavior #05).

**Decided 2026-10-02: price it first, then decide.** The skip removes a rescan that currently masks any
missing-wake bug, so it has to earn that risk with a measured cost.

**Plan.**
1. Price the scan with `Assets/Editor/Benchmarking/ActiveVoxelScanBenchmark.cs` (its `T_bitmask` replica is
   the post-TG-2 managed scan): µs per chunk for terrain, ocean and cave shapes; per-crossing cost =
   µs/chunk × chunks entering view per crossing (2·vd + 1: 21 at vd 10, 65 at vd 32). Record it in
   `Documentation/Performance/` per `perf-benchmark`.
2. Present the number for a go/no-go against a proposed materiality bar (suggested: > 0.5 ms per crossing at vd 32
   in IL2CPP — **a proposal, not a decision**). Below it → mark ES-6.1 ⏸️ with the measurement.
3. Only if material, implement the design the 2026-10-02 sweep drafted:
   - **1a:** `[NonSerialized] bool _activeVoxelsRegistered` on `ChunkData`, cleared in `Reset` beside the
     bucket clears (B34's reflection sweep picks it up — prove-red by omitting the reset line);
     `NeedsActiveVoxelRescan => IsPopulated && !_activeVoxelsRegistered`; `Chunk.Reset` tests it; both
     registration paths set the flag at the **end** (so a mid-scan exception leaves it false).
   - **1b:** move `RegisterActiveVoxelsFromJob` onto `ChunkData` and call it unconditionally at
     `WorldJobManager.cs:1168`, so data-only generated chunks register from the job's list. Leave the disk
     and legacy paths on `Chunk?.` (their flag stays false and `Reset` still scans them).
   - New **BH-B13** on the pure seam: AddActiveVoxel-only seeding → rescan needed; after job registration →
     not needed; after `Reset` → flag false, buckets empty; parity of job list + `ModifyVoxel` edits against a
     managed bitmask replica (prove-red: drop `ModifyVoxel`'s bucket add).
   - In-game soak focused on fluids and grass at chunk re-entry (flowing water, lava, grass spread) — the
     risk is a sleeping voxel that should have woken.
   - Docs in the same commit: `CHUNK_LIFECYCLE_PIPELINE.md` and the `OnDataPopulated` remarks.

**Not doing.** Any change before step 1's number exists; the disk-load path (its scan would just move into
the unbudgeted SL-2 continuation); the rest of ES-6 (spreading activations, `DT-3`).

**Correction to step 1 (re-verified at `491747ee`, not yet executed).** `ActiveVoxelScanBenchmark`'s
`T_bitmask` is **not** today's scan. It walks the full flat map, while `OnDataPopulated` (`Chunk.cs:236–239`)
skips null and empty sections; and it inserts into a managed `HashSet<Vector3Int>`, while the shipped path
calls `ChunkData.AddActiveVoxel(pos, id)` — `ClassifyFamily` (a `World.Instance.BlockTypes` read), a
cross-bucket remove and a lazily created native bucket (TG-4). It also runs only two shapes, Land and
Flooded; there is no cave-heavy scenario. Price on a new `T_current` leg in the benchmark (editor code only)
that replays the shipped loop over a sectioned `ChunkData` (`PopulateFromFlattened`) with a stubbed
`World.Instance` (the `BehaviorTestWorld` pattern), keeping `T_bitmask` for continuity. The benchmark runs in
the Editor (Mono), so it can only clear the IL2CPP bar one way: a number below 0.5 ms per crossing is a safe
"not material", one above it does not show IL2CPP crosses it.

**Step 1 executed — GO for steps 2–3 (2026-10-02).** The `T_current` legs time the shipped
`Chunk.OnDataPopulated` itself (an uninitialized `Chunk` plus a stub `World.Instance` carrying the real block
database), with a bucket-count parity check against the job's list; the cave-heavy shape was deliberately
skipped (Land and Flooded already span the scan's two cost drivers). Per vd-32 crossing:
Land **1.56 ms** first view / **1.76 ms** re-entry, Flooded **42.8 / 20.1 ms** — above the 0.5 ms bar on every
leg, in the Editor. The water-heavy case would need an 85× / 40× backend speedup to fall under it, so GO was
taken without an IL2CPP confirmation. Report:
[`CHUNK_LIFECYCLE_ES6_1_RESCAN_PRICE_2026-10-02_BENCHMARK.md`](../Performance/CHUNK_LIFECYCLE_ES6_1_RESCAN_PRICE_2026-10-02_BENCHMARK.md).
Step 3 (1a + 1b, BH-B13, the re-entry soak, docs) is next, in its own session.

**Step 3 executed and in-game confirmed (2026-10-02).** Re-verified at `19d7f998`: the packet's Chunk.cs and
`WorldJobManager.cs:1168,1170` anchors held; the disk arm had moved to `World.cs:1411` (ES-1) and the tick
snapshot to `~:2503`. Shipped as planned, with two additions:
- **1a + 1b.** `_activeVoxelsRegistered` / `NeedsActiveVoxelRescan` on `ChunkData`, cleared in `Reset`;
  `Chunk.Reset` tests it. The generation arm calls `ChunkData.RegisterActiveVoxelsFromJob` unconditionally;
  the disk and legacy arms keep `Chunk?.` and their rescan at attach. **Addition:** the scan moved onto
  `ChunkData` as `RescanActiveVoxels` (`Chunk.OnDataPopulated` delegates), so only the two registration
  methods can set the flag, each as its last statement — a public setter would let any runtime code mark the
  buckets valid.
- **Addition — the step-4 wake, folded in because 1a is what exposes it.** Planning found that `ApplyModifications` step 4 woke
  neighbors through `GetChunkFromVector3` — the **visual** `Chunk` — so it skipped data-only neighbors. The
  re-entry rescan had masked that; with 1a it would leave a quiesced seam voxel asleep after re-entry. Step 4 is
  now `World.WakeActiveNeighbors`, which registers on the neighbor's `ChunkData`; `Chunk.AddActiveVoxel` lost
  its only caller and was deleted.
- **Validation.** Lighting B34 picks the flag up through its reflection sweep (prove-red: dropping the reset
  line reds it, naming `_activeVoxelsRegistered`). New **BH-B13** (six legs, parity against an independent
  full-walk replica, the real `ActiveVoxelScanJob` for the job list, production `ModifyVoxel` via an opt-in
  `JobDataManager` on `BehaviorTestWorld`): each of four prove-reds reds the leg it targets — drop
  `ModifyVoxel`'s bucket add, skip the job-list flag write, restore the visual lookup in step 4, omit the
  `Reset` clear. One spill-over: the job-list mutation also reds leg 3's "edits do not re-arm" check, which
  inherits the flag.
- **Review follow-up — the disk-load seam wake.** The load-from-save arm ran `WakeSeamBehaviorNeighborhood`
  *before* replaying the chunk's pending mods, so a replayed mod that opened a border cell (`/setblock … air`
  into an unloaded chunk) never woke the neighbor resting against it — a gap the old re-entry rescan had hidden
  for data-only neighbors. It now runs after the replay, the generation arm's order. Accepted on code reading and
  the suites (no suite reaches the disk-load sequence); not reproduced in game.

**In-game soak passed (2026-10-02, Editor play mode) — no regression seen.** Two checks: (1) a water source on a
tree top, flown away from past the **load** distance and back — the water spread normally (this exercises the
unload → reload path, which keeps its rescan, not the 1a skip); (2) water flowing at the edge of the render
distance kept flowing, where chunks cross the view boundary while their data stays resident — the 1a skip and
the step-4 data-only wake; (3) a targeted pass for the skip path — flowing water left just outside the view
distance (data still resident) for a few seconds, then re-entered — kept flowing correctly. BH-B13 is the
deterministic guard. The extra generation-pass bucket
insertion is **unmeasured**. `ChunkGenerationBenchmark` cannot see it: it times schedule → complete → release
and never runs `ProcessGenerationJobs` (an A/B with and without this change on 2026-10-02 differed by −0.3 to
+0.6 % total, mixed-sign noise). `ActiveVoxelScanBenchmark`'s `T_register` is a managed-`HashSet` replica, not
the shipped `ChunkData.RegisterActiveVoxelsFromJob`, so the flooded-chunk cost is bounded only loosely, between
that replica (~390 µs) and the full first-view rescan (~658 µs, Editor). A shipped-path register leg is the
instrument that would price it. `ActiveVoxelScanBenchmark` times `OnDataPopulated` directly and cannot show the 1a skip; the
saving is inferred from step 1's re-entry leg, not re-measured.

---

## 8. Verification checklist and open questions

1. **No in-game rendering baseline exists** — ES-25's first step (Frame Debugger + profiler at vd 10/32:
   draws, SetPass, vertices/frame, GPU ms) gates every rendering verdict, ES-20 included. Also confirm
   the block shaders' "SRP Batcher: compatible" status in the inspector (inferred from code, never
   recorded), and that no real section exceeds 65 535 vertices more often than ES-25.1's fallback assumes.
2. **Where the rest of the 30 s goes** — ES-0's drain stamp, captured in both Editor and Master IL2CPP.
3. **What the ~142 KB/chunk managed allocation is** — ES-0's GC.Alloc capture.
4. **Packed positions (open decision).** MR-2 rejected Float16 positions over crack risk. ES-20/G0
   proposes **integer / UNorm16 fixed-point section-local positions**, which are exact on the voxel
   lattice and its power-of-two sub-quads (VO-9b) — a different trade from Float16 — but sloped fluid
   heights quantize to 1/4096 block. Never `half` for sub-quad fractions. Needs an explicit decision
   before G0.
5. **ES-10's persistence policy** — open decision; inputs in the master report's § AC-9 static pass.
6. **Unity API checks before code** (unity-api MCP): `GraphicsStateCollection`; the `SetData(NativeArray,
   int, int, int)` parameter order (the MCP listing is ambiguous); whether `RenderPrimitives*` draws run
   the material's ShadowCaster/DepthOnly passes under URP; whether DX12/Vulkan in 6000.6 issue real
   multi-draw.
7. **Doc and comment drift found by the 2026-10-02 sweeps** — all fixed 2026-10-03 except where noted:
   - ~~`PERFORMANCE_IMPROVEMENTS_REPORT.md`'s P-1 re-scope note says P-1's win is now worker-side, but the
     9-map fill is still main-thread (`WorldJobManager.cs:859–862`).~~ Dated correction appended.
   - ~~"Unmodified persist-arm chunks regenerate from seed" in the `World.cs:3825–3829` comment and
     `CHUNK_LIFECYCLE_PIPELINE.md`'s persist-and-unload bullet ("fresh regeneration for an unmodified
     chunk") — every generated chunk is in `ModifiedChunks`, and an unedited disk-loaded chunk reloads its
     earlier save (ES-10, AC-9).~~ Both now describe the reload path, and `DATA_STRUCTURES.md`'s
     `ModifiedChunks` entry says every generated chunk is in it. (`CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md`
     §3.2's "seed-regenerable" is correct: it describes a discarded, never-populated generation result.)
   - ~~`.agents/rules/chunk-pipeline.md` says meshing passes `AreNeighborsReadyAndLit`;
     `CHUNK_LIFECYCLE_PIPELINE.md` §3.3 says `AreNeighborsMeshReady` (§5).~~ The code gates meshing on
     `AreNeighborsMeshReady`; the rule, the `chunk-lifecycle` skill and the `review-changes` pipeline
     gates now say so.
   - ~~`ChunkStorageManager.cs:21` says the save continuation resumes on a ThreadPool thread; under
     Unity's synchronization context it resumes on the main thread (`SERIALIZATION_BUGS.md` §15).~~
   - ~~`World.cs` `SaveAllModifiedChunks` docstring mentions an "Auto-Save" that does not exist (AC-9).~~
   - ~~`DeviceCalibration.cs:139–141` calls the reference constants "editor-measured"; `:95–97` says IL2CPP
     player, 99-sample median (ES-2 packet).~~ Fixed by ES-2.
   - ~~`CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md` §7.1 says a `CalibrationVersion` bump clobbers hand-edited
     caps; since ES-2 a bump of an already-calibrated file only raises a field (keep-higher), so a
     hand-*lowered* cap is the one that gets overwritten.~~ Dated note added.
   - ~~`World.cs:1185, 1190, 3471` describe the CP-1/LP-1/MP-1 probes as "dev/editor builds only"; they are
     gated on `UNITY_INCLUDE_INSTRUMENTATION`, so Development builds (Release code variant) compile them
     out.~~ Every such comment in `Assets/Scripts` now names its real gate ("instrumented builds" or, for
     `UNITY_ENABLE_CHECKS`, "checked builds"). **Still open, owned by PM-7:** the DebugScreen "(dev)" rows
     read 0 in a Release-variant Development build and the save-diagnostics toggle is shown but inert there
     (`PERFORMANCE_MONITOR_AND_LOGGER_OVERHAUL.md` §2).
   - `OPEN_WORK_INDEX.md`'s ES row still read "Draft, unscheduled" after ES-1/2/6.1 shipped — fixed 2026-10-03.

---

## 9. Rejected alternatives

| Alternative | Why rejected | Date |
|---|---|---|
| Raise `maxLightJobsPerFrame` / mesh caps | P9-0a: ×4.79 CPU for +21 % delivery; ceiling binds past ×2 (ES-2 restores the tuned rate, it does not raise it) | 2026-08-02 |
| Scale the panic-gate thresholds (P-8) | Built and measured NO-GO — the backlog grows to meet any threshold | 2026-08-01 |
| Deliver-then-refine as a throughput lever | P9-1: pre-delivery mesh amplification is 1.00 | 2026-08-02 |
| Off-main-thread scheduling on managed `ChunkData` | P-9 Option D — data race on live managed state; legal only after ES-18b | 2026-08-01 |
| Batched `IJobParallelFor` lighting schedule | `Schedule()` and gate walks are <1 % of the cost; the fill and merge are — needs ES-18 instead | 2026-10-02 |
| GPU compute lighting | Meshing needs light on the CPU (mandatory readback); removal/darkness waves fit iterative relaxation poorly | 2026-10-02 |
| GPU terrain generation | Precise64 needs doubles; readback latency; serial worm carver; no bit-identity with the CPU path | 2026-10-02 |
| One indirect command per section | Multi-draw is reportedly emulated as N API draws in Unity; use few large commands per page/region | 2026-10-02 |
| Hardware ray tracing | URP has no RT support (`VX-*`) | 2026-07-20 |
| ECS / Entities Graphics for terrain | §3 Option C — Forward+ and DOTS-instancing tax with no batching win, per-type job safety, structural churn; see `DOTS_ECS_ADOPTION_ANALYSIS.md` | 2026-10-02 |
| GPU Resident Drawer for section meshes | Needs Forward+ (renderer is Forward) and DOTS-instancing variants (none); every section mesh is unique, so its batching gain is small | 2026-10-02 |
| `Mesh.MeshDataArray` as the upload fix | Only trims main-thread API-call overhead; the copy to the GPU remains — lowest-value rendering item | 2026-10-02 |
| Count/heuristic occlusion culling | Corrupted rendering before; topology needs connectivity masks (GS-5 §7.1) | 2026-06-12 |
| Deferring lighting for blocklight-free chunks | Negligible — a job without emitters pays only the emission scan | 2026-10-02 |

---

## Document History

* **v1.14** - ES-0 bullet + plan row: PM-6 confirmed in an IL2CPP Master build (2026-10-06) — benchmark reports carry
  exact per-phase frame statistics (worst / p99 / hitches / collections / GPU) and Capture writes session and hitch
  files. The plan row no longer sends "the rest" of ES-0 to PM-6: the drain stamp was never in PM-6's scope and is the
  remaining, unowned ES-0 item.
* **v1.13** - ES-0 slot bullet + plan row: PM-8 confirmed in an IL2CPP Master build (2026-10-05) — the 200 m/s `Tick`
  hitches are the fluid prepare's voxel-map copies and the wait for the fluid jobs, plus the managed grass tick; no ES item
  owns either fix yet.
* **v1.12** - ES-0 slot bullet + plan row: PM-4 confirmed in an IL2CPP Master build (2026-10-05) — the `Tick`-led 200 m/s
  hitches track the fluid tick's job count; no ES item owns that fan-out yet (PM-8 measures its main-thread split).
* **v1.11** - ES-0 slot bullet + plan row: PM-3 confirmed in an IL2CPP Master build (2026-10-04), with the first Master
  reading of the 200 m/s (`Tick`- and `LightMerge`-led) and post-phase (`Unload`-led) hitches.
* **v1.10** - ES-0 slot bullet + plan row: PM-3's coverage code landed (2026-10-04, Master build check pending) — a slot
  for every `World.Update` region incl. the crossing work, the unattributed remainder, and per-frame counters; the
  bullet's `ApplyModifications` slot already existed as `Apply`.
* **v1.9** - ES-0 bullet + plan row: PM-2's Frame tier complete (2026-10-04, confirmed in an IL2CPP Master build) — per-frame
  allocation that leaves collection frames out, collection counts, GPU/render-thread/present-wait time and
  GC-correlated hitch records.
* **v1.8** - New `ES-28` (2026-10-03): the UI band/blur Render Graph passes allocate ≈ 0.75 KB every frame (98.8 % of
  main-thread garbage in the PM-1 Play-mode smoke capture); Tier 1b entry, plan row, §4.1 traversal row, status line.
* **v1.7** - ES-0 bullet + plan row: PM-1's store landed (2026-10-03) — raw per-frame ring with exact worst/p99
  (`/perf stats`); the rest of ES-0 rides PM-2, PM-3 and PM-6.
* **v1.6** - **ES-0 GC attribution** (2026-10-03, IL2CPP Development/Master player, GC.Alloc call stacks, 200 m/s
  phase): 127.4 KB per generated chunk, fully attributed — 77 % one span copy in `ChunkSerializer.WriteSection`,
  82 % the unload save path, 80 % on ThreadPool threads. §2.2 rows and §2.2.1 attribution table; new `ES-26`
  (serializer span copy) and `ES-27` (BFS queue regrowth); ES-9/ES-10 re-ranked; §4.1 traversal row; ES-0 row;
  ES-1 open-finding note. Report: `Performance/ENGINE_SCALING_ES0_GC_ATTRIBUTION_IL2CPP_2026-10-03_BENCHMARK.md`.
* **v1.5** - **H-1 refuted** (2026-10-03, IL2CPP Master benchmark with the PM-0 pool-miss counters): 0 section
  misses per generated chunk at 200 m/s against ~129 KB of garbage per chunk, so the per-chunk garbage is still
  unattributed. §2.2.1 carries the verdict table, and the §4.1 traversal row drops ES-9/ES-10's GC rationale.
* **v1.4** - §8 item 7 closed (2026-10-03): every drift entry fixed in its doc or comment, plus the stale
  `OPEN_WORK_INDEX` ES row; the DebugScreen "(dev)" labels stay with PM-7. ES-6.1's harness follow-ups
  (BH-9 in the Behavior fidelity doc) closed the same day.
* **v1.3** - `ES-6.1` step 3 executed and in-game confirmed (2026-10-02): the registration flag (1a), job-list
  registration for data-only chunks (1b), the scan moved onto `ChunkData`, and the step-4 wake moved off the
  visual `Chunk` (a gap the skip would have exposed); BH-B13 + B34 prove-reds; soak evidence recorded in §7.1;
  review follow-up: the disk-load seam wake now runs after the pending-mod replay.
  Status line, ES-6 row (ES-6.1 ✅, rest open) and §7.1 header updated.
* **v1.2** - `ES-6.1` step 1 executed (2026-10-02): the shipped rescan priced in the Editor at 1.56–1.76 ms per
  vd-32 crossing on land and 20–43 ms on flooded chunks, above the 0.5 ms bar on every leg — GO for steps 2–3,
  cave-heavy shape skipped by decision. ES-6 row and §7.1 header updated.
* **v1.1** - `ES-1` and `ES-2` executed and in-game confirmed (2026-10-02). ES-1: the startup load wait falls
  from ≈5 to <1 ms per chunk, with an open finding that the new-world coroutine got ≈2 s slower (attribution
  owed to ES-0). ES-2: the packet's warm-up-tail premise refuted — the 15 light jobs came from a stale OM-1
  reference — and the references re-anchored; the cold-launch gate resolves 32 / 10. ES-6.1's benchmark
  found not to replicate today's scan (correction in its packet). §8 item 7 updated.
* **v1.0** - Initial draft: six-area code sweep (startup, generation/storage, lighting, frame loop + GC,
  meshing/rendering, external research incl. the Incandescent Games pipeline), `ES-0`…`ES-25` in four
  tiers, two-track architecture decision (native chunk store + GPU-driven renderer), hypothesis H-1
  (section pool misses as the GC source), execution packets for ES-1 / ES-2 / ES-6.1 with their
  decisions settled (§7.1), verification checklist.

---

**Last Updated:** 2026-10-06  
**Next Review:** when ES-26 is scored against the ES-0 capture or ES-25's rendering baseline lands, or before any ES phase starts
