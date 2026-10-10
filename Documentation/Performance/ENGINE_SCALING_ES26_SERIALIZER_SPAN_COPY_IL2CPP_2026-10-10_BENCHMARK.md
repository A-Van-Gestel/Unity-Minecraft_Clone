# ES-26 — Serializer Span Copy Removed: GC.Alloc per Generated Chunk and Frame Health, Before and After

| Field           | Value                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            |
|-----------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-10-10                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       |
| **Branch**      | `main`                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           |
| **Commit**      | `9da61a56` + uncommitted ES-26 (`ChunkSerializer.WriteBulk` + `CountingWriteStream` / `DiskSaveRawBytes`)                                                                                                                                                                                                                                                                                                                                                                                                                                                                        |
| **Captured by** | Benchmark route, Gen = `200` only (loading pass 50 m/s, not read), 30 s phase, vd 10 — **IL2CPP player, Development build, IL2CPP configuration Master**, Burst AOT, D3D11, Unity 6000.6.5f1, i9-9900K / RTX 4070 Ti; Editor Profiler attached with **GC.Alloc call stacks**; 1 player capture + 1 Editor (Mono) screening capture, read with the same `GcCallstacks` query as ES-0; then **3 IL2CPP Master (non-development) runs** at default speeds with `-mc-set perfMonitorTier=Capture`, read per phase with `summarize_perf_session.py` against the 10-10 Master baseline |
| **Verdict**     | **GO (GC)** — **127.4 → 30.7 KB of GC.Alloc per generated chunk (−75.9 %)**; the `WriteSection` site (98.0 KB per chunk) is gone and the save path falls from 104.2 to 6.0 KB per chunk. On-disk bytes unchanged (golden-hash guard B6). Master, 200 m/s: **85 → 18–19 collections per phase**, GC 1 452 → 283–287 KB per frame, wall p99 74.4–78.1 → 69.2–72.0 ms; p50 unchanged (the frame is lighting-merge-bound).                                                                                                                                                           |

> Scores [`ES-26`](../Design/ENGINE_SCALING_PERFORMANCE_ROADMAP.md) against the ES-0 attribution capture
> [`ENGINE_SCALING_ES0_GC_ATTRIBUTION_IL2CPP_2026-10-03`](ENGINE_SCALING_ES0_GC_ATTRIBUTION_IL2CPP_2026-10-03_BENCHMARK.md),
> re-confirmed unchanged by [`ENGINE_SCALING_ES0_REMEASURE_IL2CPP_2026-10-10`](ENGINE_SCALING_ES0_REMEASURE_IL2CPP_2026-10-10_BENCHMARK.md).
> Same route, build configuration, phase marker, capture control and query; only the ES-26 code differs.

## The change

`ChunkSerializer` wrote each voxel, light and heightmap array with `BinaryWriter.Write(MemoryMarshal.AsBytes(span))`.
In Unity's class libraries that overload copies the span into a new array on every call. All five call sites now go
through `WriteBulk`, which copies into a 16 KB `[ThreadStatic]` scratch buffer and calls `Write(byte[], int, int)`.

Isolated Editor measurement (heap delta, min of 7 × 10 calls; a `new byte[16384]` control reads 20 480 B):

| 16 KB write via                                   | MemoryStream (None) | LZ4Stream | DeflateStream |
|---------------------------------------------------|--------------------:|----------:|--------------:|
| `writer.Write(span)` (before)                     | 20 480 B            | 20 480 B  | 20 480 B      |
| scratch copy + `writer.Write(byte[], 0, n)` (now) | 0 B                 | 0 B       | 0 B           |
| `writer.BaseStream.Write(span)` (rejected)        | 0 B                 | 0 B       | 2 048 B       |

The same commit series adds `CountingWriteStream` between the writer and the compression stream, so a save reports its
uncompressed size (`DiskSaveRawBytes`, the compression ratio in `/perf stats`). It forwards every call unchanged.

## What this measures (and what it does NOT)

**Measures:** every `GC.Alloc` sample on every thread in the frames carrying `Benchmark.Generation.200mps`, as in ES-0.
Per generated chunk = window bytes ÷ the phase's `Chunks populated: N generated`.

**Does NOT measure:**

- **Frame time from the Development capture.** That player has the Profiler attached, and the 10-03 report predates
  the Frame Health table. Its avg wall (43.6 → 46.7 ms) is not read; the frame-level effect comes from the Master runs
  (§ Result — IL2CPP Master).
- **The load path.** 0 chunks loaded from disk in the window, as in ES-0.
- **Startup**, as in ES-0.

## Acceptance checks

| Run                                          | Build              | Phase frames | Window frames | Chunks generated | Heap delta per chunk | GC.Alloc per chunk | GC.Alloc ÷ heap delta |
|----------------------------------------------|--------------------|-------------:|--------------:|-----------------:|---------------------:|-------------------:|----------------------:|
| ES-0 `BenchmarkRun_2026-10-03_16-20-22`      | IL2CPP Dev, Master | 825          | 825           | 8 831            | 131.4 KB             | 127.4 KB           | 96.9 %                |
| **ES-26 `BenchmarkRun_2026-10-10_15-34-09`** | IL2CPP Dev, Master | 642          | 642           | 8 873            | **32.5 KB**          | **30.7 KB**        | 94.5 %                |
| ES-0 `BenchmarkRun_2026-10-03_15-47-09`      | Editor Mono        | 598          | 598           | 5 882            | 158.7 KB             | 138.7 KB           | 87.4 %                |
| **ES-26 `BenchmarkRun_2026-10-10_15-19-06`** | Editor Mono        | 656          | 656           | 5 773            | **40.7 KB**          | **34.1 KB**        | 83.8 %                |

Both new windows were held whole (window frames = `Frames sampled`), 0.0 % of bytes had no call stack, and GC.Alloc
plus the bytes outside it equal the thread totals exactly. The ES-26 player generated 0.5 % more chunks than ES-0, so the
per-chunk figures compare like with like.

## Result — IL2CPP, 200 m/s window (642 frames, 8 873 generated chunks)

**By thread:** main thread 244 448.3 KB (27.5 KB per chunk); ThreadPool (`Scripting Threads / #0`) **28 230.5 KB
(3.2 KB per chunk, 10.4 %)** — ES-0: 101.5 KB per chunk, 79.7 %.

| #   | Call site                                                             | Thread     | KB/chunk  | ES-0 KB/chunk | Share  | Calls   | Frames | Owner                          |
|----:|-----------------------------------------------------------------------|------------|----------:|--------------:|-------:|--------:|-------:|--------------------------------|
| —   | `ChunkSerializer.WriteSection` (span `ToArray`)                       | ThreadPool | **0**     | 98.01         | 0 %    | 0       | 0      | **ES-26 (removed)**            |
| 1   | `ChunkData.AddToSkylightQueue` (`Queue<T>` growth)                    | Main       | 8.47      | 7.59          | 27.6 % | 3 645   | 353    | ES-27                          |
| 2   | `StandardChunkGenerator.ExpandStructure`                              | Main       | 7.97      | 7.99          | 25.9 % | 348 110 | 496    | ES-9                           |
| 3   | `ChunkSection..ctor` (section pool misses, 1 149 = 0.13 per chunk)    | Main       | 3.37      | 3.10          | 11.0 % | 2 488   | 25     | none — bursty pool growth      |
| 4   | `RegionFile.SaveChunkData`                                            | ThreadPool | 1.95      | 1.95          | 6.3 %  | 26 811  | 351    | ES-9                           |
| 5   | `ChunkStorageManager.LoadChunkAsync` state machine                    | Main       | 1.57      | 1.56          | 5.1 %  | 147 626 | 289    | ES-9                           |
| 6   | `ChunkData.AddToBlocklightQueue` (`Queue<T>` growth)                  | Main       | 1.46      | 1.33          | 4.8 %  | 3 100   | 334    | ES-27                          |
| 7   | `ChunkStorageManager.SaveChunkAsync` state machine                    | Main       | 1.39      | 1.36          | 4.5 %  | 114 608 | 351    | ES-9 / ES-10                   |
| 8   | `World.UnloadChunks`                                                  | Main       | 1.26      | 1.27          | 4.1 %  | 111 809 | 372    | ES-9 / ES-10                   |
| 9   | `WorldJobManager.MergeCompletedLightingJob` (`List<T>` growth)        | Main       | 0.60      | 0.25          | 2.0 %  | 97      | 18     | ES-27                          |
| 10  | `ChunkStorageManager.CreateSerializationSnapshot` (`Queue<T>` growth) | Main       | 0.56      | 0.45          | 1.8 %  | 408     | 94     | ES-9                           |
| 11  | `CountingWriteStream.WriteByte` → `LZ4Stream` buffer rent (see below) | ThreadPool | 0.29      | 0.09 (in #15) | 0.9 %  | 20      | 4      | none — `ArrayPool` rent misses |
| 12  | `ChunkSerializer.Serialize` (writer + encoder per save)               | ThreadPool | 0.26      | 0.26          | 0.8 %  | 35 758  | 351    | ES-9                           |
| 13  | `ChunkStorageManager.GetRegion`                                       | ThreadPool | 0.25      | 0.25          | 0.8 %  | 17 871  | 461    | ES-9                           |
| 14  | `CompressionFactory.CreateOutputStream`                               | ThreadPool | 0.20      | 0.20          | 0.6 %  | 17 876  | 351    | ES-9                           |
| 15  | `ChunkSerializer.WriteChunkInternal` (`new ChunkSection[]` per save)  | ThreadPool | 0.09      | 0.72          | 0.3 %  | 8 938   | 351    | ES-9                           |
|     | **Total**                                                             |            | **30.73** | **127.41**    | 100 %  |         |        |                                |

**Save path** (sites 4, 7, 8, 10, 11, 12, 14, 15): **6.0 KB per chunk** (ES-0: 104.2 KB).

## Result — IL2CPP Master, frame health (3 runs against the 10-10 baseline)

Same route, settings and tool as the baseline (`ENGINE_SCALING_ES0_REMEASURE_IL2CPP_2026-10-10`, runs B1–B3, one
`Windows - Production` build): default speeds, vd 10, Capture tier, each phase cut from the session file by
`summarize_perf_session.py`. Runs E1–E3 are one `Windows - Production` build of the ES-26 code, back to back, 450 s each,
all exit 0. The baseline report gives B1's full row and B2/B3's p50, p99, worst and GC only.

| Generation 200 m/s | Frames | Wall p50    | Wall p99    | Worst     | CPU p99 | GC KB/frame   | GCs    |
|--------------------|-------:|------------:|------------:|----------:|--------:|--------------:|-------:|
| B1 (baseline)      | 832    | 40.04       | 78.08       | 123.51    | 77.73   | 1 452.3       | 85     |
| B2 / B3 (baseline) |        | 39.2 / 41.6 | 74.4 / 74.9 | 118 / 132 |         | 1 446 / 1 540 |        |
| **E1**             | 825    | 39.73       | **70.46**   | 115.31    | 70.11   | **283.0**     | **18** |
| **E2**             | 808    | 40.43       | **72.04**   | 125.88    | 70.09   | **287.1**     | **19** |
| **E3**             | 816    | 40.48       | **69.22**   | 113.78    | 68.95   | **282.5**     | **19** |

Slower phases (GC KB/frame and collections, B1 → E1–E3):

| Phase              | GC KB/frame, B1 | GC KB/frame, E1–E3 | GCs, B1 | GCs, E1–E3 |
|--------------------|----------------:|-------------------:|--------:|-----------:|
| Generation 10 m/s  | 4.3             | 2.1–3.0            | 9       | 3–5        |
| Generation 20 m/s  | 8.5             | 3.8–3.9            | 15      | 6–7        |
| Generation 50 m/s  | 26.4            | 7.3–8.3            | 30      | 9          |
| Generation 100 m/s | 81.2            | 17.4–17.6          | 52      | 12         |
| Loading 200 m/s    | 12.5            | 11.1–11.4          | 6       | 5–6        |

Disk I/O per save at 200 m/s (session counters): serialize **0.232 → 0.120–0.135 ms** (100 m/s: 0.227 → 0.114–0.119),
write 1.188 → 0.51–0.60 ms. The new `DiskSaveRawBytes` counter gives a whole-session compression ratio of
**25.7–26.0 : 1** (≈ 1.96 GB serialized into ≈ 76 MB written per run).

## Analysis

- **The removed copy accounts for the whole drop.** ES-0's site 1 (98.01 KB) is gone, and site 15 falls from 0.72 to
  0.09 KB per chunk: its 0.53 KB of heightmap span copy is gone too, and its 0.09 KB `LZ4Stream` rent now shows as
  site 11. The 98.5 KB removed against a 96.7 KB measured drop leaves +1.9 KB of drift on unchanged sites, mostly the
  lighting queues (sites 1, 6, 9), which vary with how the merge batches cross-chunk light.
- **Site 11 is not new.** `LZ4Stream.WriteByte` rents its block buffer from `ArrayPool<byte>.Shared`, and a rent that
  misses the per-core pool allocates — a burst count that varies run to run (20 calls in 4 frames here). The re-queried
  ES-0 captures hold the same allocation under `WriteChunkInternal`: IL2CPP 786.6 KB in 6 calls and 1 frame (0.09 KB
  per chunk), Editor 7.9 MB in 60 calls and 9 frames. The counting stream is now the first project frame on that stack.
- **Editor screening agrees:** 138.7 → 34.1 KB per chunk (−75.4 %); `WriteSection` (106.0 KB per chunk) gone;
  ThreadPool share 80.5 % → 18.8 %.
- **What is left is main-thread.** 27.5 of 30.7 KB per chunk: ES-27's queue regrowth (10.5 KB with sites 6 and 9),
  ES-9's `ExpandStructure` (8.0 KB) and section-pool bursts (3.4 KB).
- **Collections fall with the garbage; the median frame does not move.** At 200 m/s the phase runs 18–19 collections
  instead of 85, and p99 moves from 74.4–78.1 to 69.2–72.0 ms — ranges that do not overlap across three runs each.
  p50 stays at ≈ 40 ms because the frame is the lighting merge's (10-10 baseline), not the collector's.
- **The loading pass is the control.** It saves ≈ 100 chunks per phase, so ES-26 has almost nothing to remove there, and
  its garbage and collections are unchanged (12.5 → 11.1–11.4 KB per frame, 6 → 5–6).
- **Serialization got cheaper, not just leaner.** Per-save serialize time roughly halves: the copy into a fresh array is
  gone, and the counting stream's extra call per write does not show. The write time halving too is *inferred* to be
  fewer collections stopping a ThreadPool thread mid-write; it is not instrumented.

## Verification

- **Bytes on disk unchanged:** Serialization Round-Trip B6 (SHA-256 + length of the uncompressed reference payload,
  which covers all four section flags and a varied heightmap) passes unchanged; B7 round-trips None / Deflate / LZ4.
- **Concurrency:** 16 different chunks serialized on 8 threads at once, 4 rounds, under each algorithm: 512 / 512
  payloads byte-identical to the serial ones.
- **Raw-byte count:** B7 checks the reported uncompressed length equals the `None` payload length for every algorithm
  (49 856 B); B28 checks a real save counts it exactly. Prove-red: dropping `WriteByte` from the count reddened B7
  (49 809 vs 49 856 on all three arms); restored with an identical file checksum.
- **Validate All:** 811 baselines across 32 suites, 2 known-bug repros (Serialization K04, K08) as expected.

## Verdict details

- **ES-26 ships.** The serializer change removes 98.5 KB of per-chunk garbage without touching the format.
- **ES-10 can now be decided on its merits**, as the roadmap planned: the save path left to remove is 6.0 KB per chunk.
- **Frame level:** 78 % fewer collections at 200 m/s and a p99 ≈ 6 ms lower in Master; the median frame stays
  lighting-merge-bound, which is ES-7 / ES-12's, not this item's.

## Raw data (local only, not in git)

- Captures (`ProfilerCaptures/`): `ES26_IL2CPP_Dev_200mps_2026-10-10.data` (+ `.tsv`), `ES26_Editor_200mps_2026-10-10.data`
  (+ `.tsv`); ES-0 re-queries `ES0_IL2CPP_Dev_200mps_2026-10-03_b_requery.tsv` and `ES0_Editor_200mps_2026-10-03_requery.tsv`.
- Benchmark reports (`%USERPROFILE%/AppData/LocalLow/johanaxel007/Minecraft Clone/Benchmarks/`):
  `BenchmarkRun_2026-10-10_15-34-09.log` (Development player), `…15-19-06.log` (Editor); Master E1–E3
  `…16-20-47.log`, `…16-28-18.log`, `…16-35-49.log`.
- Master session files (`…/PerfLogs/`): `PerfSession_2026-10-10_16-13-22-920`, `…16-20-53-125`, `…16-28-23-699` (each
  with `_part02`). Build kept: `2026-10-10 - ES-26 Master [IL2CPP]` beside the repo.
- Query: `unity command run_script --file Tools/UnityCli/Profiler/ProfilerQueries.cs --entry UnityCli.ProfilerQueries.GcCallstacks --args '[-1,-1,20,4,"*","Benchmark.Generation.200mps","<tsv>"]'`
  after `Load`ing the capture.
