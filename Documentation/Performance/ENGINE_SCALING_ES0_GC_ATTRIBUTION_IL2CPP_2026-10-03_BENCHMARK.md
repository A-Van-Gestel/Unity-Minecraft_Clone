# ES-0 — GC.Alloc Call-Stack Attribution of the ~130 KB per Generated Chunk at 200 m/s

| Field           | Value                                                                                                                                                                                                                                                                                                                                                                                  |
|-----------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-10-03                                                                                                                                                                                                                                                                                                                                                                             |
| **Branch**      | `main`                                                                                                                                                                                                                                                                                                                                                                                 |
| **Commit**      | `6d0484cb` + uncommitted capture tooling: the `Benchmark.Generation.<speed>mps` phase marker in `BenchmarkController`, `ProfilerQueries.GcCallstacks`, `ProfilerCapture` (no engine behavior change)                                                                                                                                                                                   |
| **Captured by** | Benchmark route, Gen = `200` only, 30 s phase, vd 10 — **IL2CPP player, Development build, IL2CPP configuration Master, Release managed-code variant, no deep profiling**, Burst AOT, D3D11, Unity 6000.6.4f1, i9-9900K / RTX 4070 Ti; Editor Profiler attached with **GC.Alloc call stacks**; 1 attributed capture + 1 reproduction run; Editor (Mono) screening pass for the tooling |
| **Verdict**     | **ATTRIBUTED (instrumentation capture, no behavior change)** — 127.4 KB of GC.Alloc per generated chunk, **100 % resolved to call sites**; **77.3 % is one line pattern**: `BinaryWriter.Write(ReadOnlySpan<byte>)` copying every section array through `ReadOnlySpan<T>.ToArray()` on the ThreadPool save path (new item **ES-26**). The save path as a whole is 81.8 %.              |

> Executes ES-0's attribution step in
> [`ENGINE_SCALING_PERFORMANCE_ROADMAP.md`](../Design/ENGINE_SCALING_PERFORMANCE_ROADMAP.md) §4 Tier 0 — "one
> Development-build (not deep-profiling) Profiler capture of a 200 m/s generation flight with GC.Alloc callstacks".
> It follows the refutation of hypothesis H-1 (§2.2.1, 2026-10-03: zero `ChunkSection` pool misses at 200 m/s in
> two Master builds), which left ~129–132 KB of managed allocation per generated chunk unattributed. PM-0
> ([`PERFORMANCE_MONITOR_AND_LOGGER_OVERHAUL.md`](../Design/PERFORMANCE_MONITOR_AND_LOGGER_OVERHAUL.md) §8 q1–q2)
> established that no live per-thread allocation counter exists in a Master player, so a Profiler call-stack capture
> is the only instrument that can name the allocating code.

## What this measures (and what it does NOT)

**Measures:** every `GC.Alloc` sample, on **every thread** the Profiler records, in the frames that carry the
benchmark's `Benchmark.Generation.200mps` marker — exactly the 200 m/s phase. Each sample's bytes (metadata 0)
and recorded call stack are read from `RawFrameDataView`; the stack's first `Assembly-CSharp` frame is the call
site. Per generated chunk = window bytes ÷ the phase's `Chunks populated: N generated` from the same run's report.

**Coverage guard:** per window, GC.Alloc bytes + bytes recorded on non-`GC.Alloc` samples must equal the summed
thread totals of the hierarchy view (they did, exactly: 1 125 139.6 KB = 1 125 139.6 KB, 0.0 KB outside GC.Alloc),
and the share of bytes without a call stack is reported (0.0 %).

**Does NOT measure:**

- **Allocation in the 10–100 m/s phases or the loading phases.** The 200 m/s phase generated 8 831 chunks and
  loaded 0 from disk, so the load path appears only as the disk-probe misses on fresh terrain.
- **Startup.** The initial-load coroutine was not captured (see "Implications" for what the result suggests).
- **GC pause time or spikes.** This is *who allocates*, not what a collection costs.
- **The Master (non-development) player directly.** The capture build is Development + Master IL2CPP; the
  reproduction check below ties its heap-delta figure to the two Master reference runs.
- **Fixes.** No allocation site was changed.

## Methodology

1. **Phase marker.** `BenchmarkController` records one `ProfilerMarker` sample per generation-phase frame, named
   `Benchmark.Generation.<speed>mps`, so the 200 m/s window is selectable exactly (no-op without a profiler).
2. **Capture control** (`Tools/UnityCli/Profiler/ProfilerCapture.cs`, `ArmAutoStop`): clears the Profiler, sets
   `ProfilerDriver.memoryRecordMode = GCAlloc`, records, and an `EditorApplication.update` hook stops and saves
   60 frames after the marker's last frame. The Profiler holds at most 2000 frames and the player renders several
   hundred fps after the phase, so a stop by hand or a 5 s shell poll lost the window twice (see "Capture history").
3. **Attribution** (`Tools/UnityCli/Profiler/ProfilerQueries.cs`, `GcCallstacks`): window frames by marker,
   all threads, call stacks from `RawFrameDataView.GetSampleCallstack` (the merged-hierarchy callstack API returned
   empty stacks for 99.9 % of bytes in the Editor pass), native frames skipped.
4. **Acceptance checks** (decided before the capture):
   - **(a) reproduction:** this run's benchmark heap-delta per generated chunk within ±25 % of the Master figure
     (~129–132 KB, `BenchmarkRun_2026-10-03_14-00-16` / `…14-34-59`) — the same method on both sides, because the
     heap-delta mean records 0 on collection frames and is therefore not directly comparable to exact GC.Alloc bytes;
   - **(b) coverage:** GC.Alloc total ≥ 0.75 × this run's heap-delta total — below that, allocation would be
     happening where the Profiler cannot see it;
   - **(c) attribution:** listed sites ≥ 80 % of the GC.Alloc bytes, remainder stated.

## Acceptance checks

| Run                                                          | Build              | Phase frames | Chunks generated | Avg GC/frame (heap-delta) | Heap-delta per chunk | vs Master ~130 KB | GC.Alloc per chunk | GC.Alloc ÷ heap-delta |
|--------------------------------------------------------------|--------------------|-------------:|-----------------:|--------------------------:|---------------------:|------------------:|-------------------:|----------------------:|
| `BenchmarkRun_2026-10-03_16-20-22` (**attributed capture**)  | IL2CPP Dev, Master | 825          | 8 831            | 1 406.8 KB                | **131.4 KB**         | **+1 %** ✅       | **127.4 KB**       | **96.9 %** ✅         |
| `BenchmarkRun_2026-10-03_16-14-08` (capture lost, see below) | IL2CPP Dev, Master | 880          | 8 952            | 1 407.9 KB                | 138.4 KB             | +6 % ✅           | —                  | —                     |
| `BenchmarkRun_2026-10-03_15-47-09` (Editor screening)        | Editor Mono        | 598          | 5 882            | 1 560.7 KB                | 158.7 KB             | +22 %             | 138.7 KB           | 87.4 %                |

The Profiler window held exactly the phase's frames in both attributed runs (825 = 825, 598 = 598 against the
reports' "Frames sampled"). **(c):** the 45 listed call sites cover **100.0 %** of the GC.Alloc bytes; the 30 below
the top 15 add 9 869.1 KB (1.12 KB per chunk, 0.9 %). Section-pool misses in the attributed run: 1 137 (0.13 per
generated chunk), all in 16 frames.

## Result — IL2CPP, 200 m/s window (825 frames, 8 831 generated chunks)

**By thread:** ThreadPool (`Scripting Threads / #0`) **896 502.1 KB (79.7 %, 101.5 KB per chunk)**; main thread
228 637.5 KB (20.3 %, 25.9 KB per chunk). No other thread allocated in the window.

**Top 15 call sites** (of 45; per frame = ÷ 825):

| #   | Call site (allocating frame)                                                                                        | Thread     | KB/chunk   | KB/frame | Share      | Calls   | Frames | Maps to                                                |
|----:|---------------------------------------------------------------------------------------------------------------------|------------|-----------:|---------:|-----------:|--------:|-------:|--------------------------------------------------------|
| 1   | `ChunkSerializer.WriteSection` (`BinaryWriter.Write` → `ReadOnlySpan<T>.ToArray()`)                                 | ThreadPool | **98.01**  | 1 049.1  | **76.9 %** | 62 967  | 345    | **ES-26 (new)**; removed with saves by ES-10           |
| 2   | `StandardChunkGenerator.ExpandStructure` (`new` — iterator per structure marker)                                    | Main       | 7.99       | 85.5     | 6.3 %      | 347 225 | 534    | ES-9 ("`ExpandStructure` iterator → pooled list fill") |
| 3   | `ChunkData.AddToSkylightQueue` ← `ApplyCrossChunkLightMod` ← lighting merge (`Queue<T>.SetCapacity`)                | Main       | 7.59       | 81.3     | 6.0 %      | 2 863   | 386    | **ES-27 (new)**                                        |
| 4   | `ChunkSection..ctor` ← `ConcurrentDynamicPool.Get` (section pool misses)                                            | Main       | 3.10       | 33.2     | 2.4 %      | 2 274   | 16     | none — H-1's residue, bursty                           |
| 5   | `RegionFile.SaveChunkData` (`new`)                                                                                  | ThreadPool | 1.95       | 20.8     | 1.5 %      | 26 698  | 345    | ES-9 (padding `byte[]`, `BitConverter.GetBytes`)       |
| 6   | `ChunkStorageManager.LoadChunkAsync` state machine (`Task.Run`, closures, sync-context copies)                      | Main       | 1.56       | 16.7     | 1.2 %      | 147 135 | 290    | ES-9 (offset-table probe — misses on fresh terrain)    |
| 7   | `ChunkStorageManager.SaveChunkAsync` state machine (`Task.Run`, closures)                                           | Main       | 1.36       | 14.5     | 1.1 %      | 111 192 | 345    | ES-9 / ES-10                                           |
| 8   | `ChunkData.AddToBlocklightQueue` ← `ModifyVoxel` ← `ApplyModifications` (`Queue<T>.SetCapacity`)                    | Main       | 1.33       | 14.2     | 1.0 %      | 2 874   | 353    | **ES-27 (new)**                                        |
| 9   | `World.UnloadChunks` (`ContinueWith`, closures)                                                                     | Main       | 1.27       | 13.6     | 1.0 %      | 112 608 | 367    | ES-9 ("static `ContinueWith`") / ES-10                 |
| 10  | `ChunkSerializer.WriteChunkInternal` (incl. 4 728.1 KB of the same span `ToArray`)                                  | ThreadPool | 0.72       | 7.7      | 0.6 %      | 17 806  | 345    | ES-26 (span part), ES-9                                |
| 11  | `ChunkStorageManager.CreateSerializationSnapshot` (`Queue<T>` growth)                                               | Main       | 0.45       | 4.8      | 0.4 %      | 385     | 94     | ES-9 (section-ownership save) / ES-10                  |
| 12  | `ChunkSerializer.Serialize` (`BinaryWriter` + encoder per save)                                                     | ThreadPool | 0.26       | 2.8      | 0.2 %      | 35 607  | 345    | ES-9                                                   |
| 13  | `WorldJobManager.MergeCompletedLightingJob` (`List<T>` growth)                                                      | Main       | 0.25       | 2.7      | 0.2 %      | 10      | 10     | ES-27                                                  |
| 14  | `ChunkStorageManager.GetRegion` (save + load)                                                                       | ThreadPool | 0.25       | 2.7      | 0.2 %      | 17 828  | 440    | ES-9                                                   |
| 15  | `WorldData.QueueSkylightRecalculation` (`HashSet<T>` growth)                                                        | Main       | 0.22       | 2.3      | 0.2 %      | 1 873   | 44     | ES-27                                                  |
|     | **30 further sites** (per-frame UI/render 0.32, `CreateOutputStream` 0.20, `AddPendingMod` 0.13, telemetry 0.10, …) |            | 1.12       | 12.0     | 0.9 %      |         |        |                                                        |
|     | **Total**                                                                                                           |            | **127.41** | 1 363.8  | 100 %      |         |        |                                                        |

**Grouped:** the **save path** (sites 1, 5, 7, 9, 10, 11, 12, `CreateOutputStream`) is **104.2 KB per chunk
(81.8 %)**; lighting-queue growth (3, 8, 13, 15) 9.4 KB (7.4 %); `ExpandStructure` 8.0 KB (6.3 %); section-pool
misses 3.1 KB (2.4 %); the disk-probe/load path 2.0 KB (1.6 %); per-frame UI/render garbage 0.3 KB (0.3 %).
Groupings follow the call site; `GetRegion` is counted with the load path, though roughly half of its calls are saves.

## Analysis

**Site 1 — the span copy (ES-26).** `WriteSection` writes each non-empty section with
`writer.Write(MemoryMarshal.AsBytes(section.voxels.AsSpan()))` and the same for `LightData`. In Unity's class
libraries `BinaryWriter.Write(ReadOnlySpan<byte>)` copies the span into a new array; the IL2CPP stack names it
(`System.ReadOnlySpan`1.ToArray()` inside `BinaryWriter.Write`). An isolated Editor test (Mono, same class
libraries) measured **20 480 B allocated per 16 KB write through the span overload versus 0 B through
`Write(byte[], int, int)`**. 62 967 calls ÷ 8 900 saves ≈ 7.1 per saved chunk at 13.7 KB average — one copy per
voxel or light array of every saved section. The output bytes do not depend on the overload, so a fix need not
touch the on-disk format.

**Sites 3 and 8 — lighting BFS queue growth (ES-27).** `ChunkData`'s `_skylightBfsQueue` / `_blocklightBfsQueue`
are `readonly` managed `Queue<LightQueueNode>` instances that `Reset` only `Clear()`s, which keeps capacity. With
0 `ChunkData` pool misses, the ~2 900 + 2 900 regrowths (23 KB and 4 KB average) must be pooled instances growing
past their own high-water mark — every pooled `ChunkData` warms its own queues, and cross-chunk light
modifications from the merge decide how far. *Inferred from the code and the call counts; not instrumented.*

**Site 2 — `ExpandStructure`.** One compiler-generated iterator object (~208 B) per structure marker, 347 225 in
the window (~39 per generated chunk) — the §2.2 "smaller source", now measured as the second-largest site.

**Async I/O garbage (sites 6, 7, 9, 12, 14).** `Task.Run`, `ContinueWith`, async state machines, captured
`ExecutionContext` and `UnitySynchronizationContext.CreateCopy` per probe and per save — ~5 KB per chunk in total,
exactly the §2.2 "I/O path garbage" row; ES-9's dedicated I/O worker and offset-table probe cover it.

**Site 4 — pool misses.** 1 137 section misses ≈ 27 MB, entirely in 16 frames: the pool growing in bursts, not a
per-chunk cost. Consistent with H-1's refutation (0.13 misses per generated chunk here; 0.00 in the Master runs).

**Editor (Mono) vs IL2CPP.** Same ranking in the Editor pass: `WriteSection` 106.0 KB/chunk (76.4 %),
`AddToSkylightQueue` 11.3, `ExpandStructure` 6.0, total 138.7 KB/chunk, ThreadPool 80.5 %. Editor-only sites absent
from the player: `Chunk.Reset` / `ChunkPoolManager.GetBorder` GameObject-name strings and the `[LIGHTING RESCUE]`
`Debug.Log` from `UnloadChunks`.

### Capture history

Two capture attempts lost the window and are not used: an Editor recording stopped by hand at the end of the run
(it held only the results screen), and the first player run, where a 5 s shell poll stopped the recording 3 300
frames after the phase. The first player build came out non-development (the CLI `build --profileName` ignores the
profile's Development flag) and could not be profiled; it was rebuilt with `--options ["Development"]`. Run
`16-14-08` is the lost-capture player run; its benchmark report is kept above as a second reproduction data point.

## Implications

- **The traversal garbage is a save-path problem.** 81.8 % comes from saving each generated chunk on unload, and
  77.3 % from one avoidable copy. ES-26 is the first ES-0-scored candidate: 🟢 effort, ~98.5 KB of the 127.4 KB per
  chunk, no format change.
- **ES-10 regains a GC case.** Not saving unmodified generated terrain removes the whole save path (~104 KB per
  chunk) — but ES-26 takes most of that without the persistence trade-off, so ES-10 should be decided on its own
  merits after ES-26.
- **ES-1's "GC debt" lead weakens (inference).** The runtime save callers are `UnloadChunks`, quit, the pause-menu
  save, the save key and the benchmark's end; none runs during the initial-load coroutine. So ~80 % of this
  per-chunk garbage does not exist at startup, and the generation-side remainder (~20 KB per chunk: sites 2, 3, 4, 6 and 8) over 529 chunks is ~11 MB. Startup was not captured; a startup capture would settle it.
- **Fixes are scored later against a re-capture of this window** with the same tooling, same build profile and
  same route.

## Raw data (local only, not in git)

- Captures (`ProfilerCaptures/`): `ES0_IL2CPP_Dev_200mps_2026-10-03_b.data` (attributed), `ES0_Editor_200mps_2026-10-03.data`
  (Editor screening).
- Benchmark reports (`%USERPROFILE%/AppData/LocalLow/johanaxel007/Minecraft Clone/Benchmarks/`):
  `BenchmarkRun_2026-10-03_16-20-22.log`, `…16-14-08.log`, `…15-47-09.log` (Editor).
- Query: `unity command run_script --file Tools/UnityCli/Profiler/ProfilerQueries.cs --entry UnityCli.ProfilerQueries.GcCallstacks --args '[-1,-1,45,4,"*","Benchmark.Generation.200mps","<tsv>"]'`
  after `Load`ing the capture.
