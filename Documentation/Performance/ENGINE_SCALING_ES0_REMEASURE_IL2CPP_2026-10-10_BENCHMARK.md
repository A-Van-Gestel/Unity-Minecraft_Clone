# ES-0 — Re-measurement with Every Monitor Layer: Traversal, Workers, I/O, Behavior Tick, GC and vd 32

| Field           | Value                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |
|-----------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-10-10                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                             |
| **Branch**      | `main`                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 |
| **Commit**      | `c48cc578` + uncommitted ES-0 drain stamp and the benchmark report's phase frame ranges (no engine behavior change)                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |
| **Captured by** | Benchmark route, default speeds (generation 10/20/50/100/200, loading 50/100/200 m/s), 30 s phases, `-mc-set perfMonitorTier=Capture` — **IL2CPP player, Master configuration, non-development**, Burst AOT, D3D11, Unity 6000.6.5f1, i9-9900K / RTX 4070 Ti; **3 runs at vd 10** plus **vd 32** (`-mc-set viewDistance=32`); every run's session file read per phase with `Tools/Python/summarize_perf_session.py`. GC.Alloc call stacks: one **IL2CPP Development (Master)** player, 200 m/s phase only, Editor Profiler attached                                                                                                                                    |
| **Verdict**     | **BASELINE (instrumentation capture, no behavior change)** — at 200 m/s the frame is **lighting-merge-bound** (LightMerge 16.3 ms + LightSchedule 5.6 ms of a ~40 ms frame, ≈ 0.4 ms of main-thread merge per lighting job). The loading pass's hitches are the **behavior tick** (fluid prepare + wait at 100–278 fluid chunks, or the managed grass tick at 45–75 k voxels), so it gets **ES-29**. Garbage is unchanged at **127.4 KB per generated chunk, 76.9 % `WriteSection`** (ES-26), and ES-28's UI passes are **0.78 KB per frame in a player**. **vd 32 saturates from 50 m/s**, peaks at 7.0 GB, and livelocked lighting in 1 of 2 runs (Lighting Bug 23). |

> The re-measurement half of ES-0 in
> [`ENGINE_SCALING_PERFORMANCE_ROADMAP.md`](../Design/ENGINE_SCALING_PERFORMANCE_ROADMAP.md) §4 Tier 0. It is the first
> capture taken with every layer of
> [`PERFORMANCE_MONITOR_AND_LOGGER_OVERHAUL.md`](../Design/PERFORMANCE_MONITOR_AND_LOGGER_OVERHAUL.md) in place at once
> (PM-1…PM-4, PM-6, PM-8): every frame of every phase, with every slot and counter. It covers systems ES-0 never listed —
> worker jobs, disk I/O, the behavior tick, pools — as the baseline future `ES-*` items are scored against. The startup
> half is [`ENGINE_SCALING_ES0_TIME_TO_STABLE_IL2CPP_2026-10-10_BENCHMARK.md`](ENGINE_SCALING_ES0_TIME_TO_STABLE_IL2CPP_2026-10-10_BENCHMARK.md).
> It repeats the GC attribution of
> [`ENGINE_SCALING_ES0_GC_ATTRIBUTION_IL2CPP_2026-10-03_BENCHMARK.md`](ENGINE_SCALING_ES0_GC_ATTRIBUTION_IL2CPP_2026-10-03_BENCHMARK.md).

## What this measures (and what it does NOT)

**Measures:** every frame of each benchmark phase — wall, CPU, GPU, present wait, managed allocation, collections —
and per frame every main-thread slot, gauge and per-frame counter of the performance monitor (31 slots, 56 counters).
Phases are cut from the session file by the frame range each report's Frame Health table now prints (`First frame` /
`End frame`). The analyzer's frame count and wall p50/p99/worst matched the report's own row **in every phase of all
three vd-10 runs**, so no phase is cut from the wrong rows.

**Does NOT measure:**

- **Per-pass GPU time, draw calls or SetPass.** No in-game rendering baseline exists; that is `ES-25`'s first step.
- **Per-frame memory.** The memory columns are the report's 20 Hz averaged snapshots.
- **Worker utilization as a share of the machine.** Busy time counts the five timed job types only (PM-4); Unity's own
  jobs are invisible.
- **Pause time of a collection.** Boehm reports none; collections are counted per frame.
- **Comparison with older captures as an A/B.** Unity moved from 6000.6.4f1 to 6000.6.5f1 since the 2026-10-03…06
  captures, so differences against them are observations, not attributions.

## Methodology

One `Windows - Production` build (`2026-10-10 - ES-0 remeasure Master [IL2CPP]`, settings file created on first
launch with the calibrated defaults: 32 light / 10 mesh jobs per frame). Runs B1–B3 at vd 10, back to back, each ≈ 7.5
min, all exit 0. The GC call-stack capture used a separate `Windows - Development` build (Master IL2CPP, Development
flag, no deep profiling), with the generation pass cut to 200 m/s and `ProfilerCapture.ArmAutoStop` on
`Benchmark.Generation.200mps` (`perf-benchmark` player-capture Flow B). The Editor sat idle during every run.

Per-phase tables below are B1 unless marked. B2 and B3 agree within the route's noise. 200 m/s: wall p50 39.2 / 41.6 ms,
p99 74.4 / 74.9 ms, worst 118 / 132 ms, GC 1 446 / 1 540 KB per frame.

## Result — IL2CPP Master, vd 10

### Frame health per phase (B1; all three runs in the raw section)

| Phase                  | Frames | Wall p50  | Wall p99  | Worst      | CPU p50 | CPU p99 | GC KB/frame | GCs | GPU p50 | GPU p99 |
|------------------------|-------:|----------:|----------:|-----------:|--------:|--------:|------------:|----:|--------:|--------:|
| Generation 10 m/s      | 16 024 | 1.15      | 7.09      | 27.46      | 0.65    | 5.63    | 4.3         | 9   | 1.09    | 6.15    |
| Generation 20 m/s      | 17 462 | 0.94      | 8.32      | 25.75      | 0.71    | 7.85    | 8.5         | 15  | 0.73    | 6.40    |
| Generation 50 m/s      | 11 771 | 1.22      | 13.90     | 58.83      | 0.81    | 13.51   | 26.4        | 30  | 0.98    | 6.66    |
| Generation 100 m/s     | 7 417  | 1.89      | 18.19     | 60.46      | 0.91    | 17.60   | 81.2        | 52  | 1.25    | 6.32    |
| **Generation 200 m/s** | 832    | **40.04** | **78.08** | **123.51** | 39.73   | 77.73   | 1 452.3     | 85  | 1.20    | 15.29   |
| Ensure Generated       | 96 254 | 1.27      | 7.36      | 40.53      | 0.83    | 6.65    | 3.8         | 17  | 0.96    | 6.36    |
| Loading 50 m/s         | 16 528 | 1.05      | 7.09      | 27.85      | 0.71    | 6.03    | 3.0         | 2   | 0.86    | 6.26    |
| Loading 100 m/s        | 13 353 | 1.34      | 8.46      | 47.63      | 0.81    | 7.80    | 5.4         | 3   | 1.06    | 6.16    |
| Loading 200 m/s        | 10 010 | 1.92      | 10.89     | 83.79      | 1.03    | 9.89    | 12.5        | 6   | 1.21    | 6.19    |

(ms unless stated.) Memory, B1 report: total 1.73–1.86 GB average in the generation pass (peak 1.99 GB), managed heap
231–335 MB average (peak 352 MB at 200 m/s).

### Main-thread slots at 200 m/s (B1, mean over every frame of the phase)

| Slot                | Mean ms | % of CPU | p99 ms | Worst ms |
|---------------------|--------:|---------:|-------:|---------:|
| LightMerge          | 16.291  | 45.7     | 40.16  | 49.02    |
| LightSchedule       | 5.633   | 15.8     | 8.20   | 8.32     |
| GenerationProcess   | 3.266   | 9.2      | 7.64   | 12.28    |
| MeshSchedule        | 2.387   | 6.7      | 4.23   | 5.87     |
| MeshProcess         | 1.978   | 5.5      | 4.24   | 5.15     |
| Apply               | 1.389   | 3.9      | 3.84   | 22.55    |
| Unload              | 1.160   | 3.3      | 8.01   | 32.53    |
| Tick                | 0.690   | 1.9      | 20.30  | 69.12    |
| ViewDistance        | 0.327   | 0.9      | 1.12   | 1.80     |
| GenerationAdmission | 0.246   | 0.7      | 2.85   | 6.51     |
| DiskLoadApply       | 0.202   | 0.6      | 1.14   | 3.52     |

Every system outside `World.Update` stays under 0.03 ms per frame in every phase: `AudioDirectors` 0.021, `Clouds`
0.015, `Environment`, `Ui`, `Physics`, `Player` ≤ 0.003, and `WorldUnattributed` 0.002 ms.

### Worker jobs (B1)

| Phase              | Job        | Completed | Mean latency ms | Mean busy ms/job | Busy ms per wall s |
|--------------------|------------|----------:|----------------:|-----------------:|-------------------:|
| Generation 200 m/s | Generation | 9 449     | 87.08           | 15.965           | 5 028.1            |
| Generation 200 m/s | Light      | 33 060    | 41.52           | 1.269            | 1 398.1            |
| Generation 200 m/s | Mesh       | 12 887    | 41.53           | 2.916            | 1 252.7            |
| Generation 200 m/s | Fluid      | 1 272     | 21.22           | 0.573            | 24.3               |
| Generation 100 m/s | Generation | 4 780     | 37.50           | 20.705           | 3 298.2            |
| Generation 100 m/s | Light      | 17 127    | 11.61           | 1.200            | 684.8              |
| Generation 100 m/s | Mesh       | 8 675     | 16.11           | 5.574            | 1 611.5            |
| Ensure Generated   | Light      | 23 830    | 6.88            | 0.434            | 52.3               |
| Ensure Generated   | Mesh       | 29 247    | 9.20            | 6.057            | 895.6              |
| Loading 200 m/s    | Light      | 12 322    | 5.99            | 0.433            | 177.8              |
| Loading 200 m/s    | Mesh       | 12 739    | 7.73            | 4.385            | 1 861.9            |
| Loading 200 m/s    | Fluid      | 2 634     | 32.93           | 1.042            | 91.5               |

At 200 m/s the timed jobs keep ≈ 7.7 worker-seconds busy per second of 15 job threads, while the main thread is at
≈ 40 ms per frame: the phase is main-thread-bound, and lighting jobs complete at ≈ 40 per frame.

### Disk I/O (B1)

| Phase              | Load hits | Misses | Read ms/hit | Deserialize ms/hit | Saves | Serialize ms/save | Write ms/save | MB saved |
|--------------------|----------:|-------:|------------:|-------------------:|------:|------------------:|--------------:|---------:|
| Generation 50 m/s  | 12        | 2 523  | 7.699       | 0.817              | 2 504 | 0.356             | 0.845         | 8.2      |
| Generation 100 m/s | 266       | 4 780  | 0.409       | 0.318              | 4 843 | 0.227             | 1.249         | 18.9     |
| Generation 200 m/s | 180       | 9 503  | 7.913       | 0.455              | 9 577 | 0.232             | 1.188         | 33.2     |
| Ensure Generated   | 20 289    | 23     | 0.261       | 0.065              | 517   | 0.108             | 0.109         | 1.9      |
| Loading 200 m/s    | 12 210    | 0      | 0.239       | 0.064              | 100   | 0.083             | 0.043         | 0.4      |

### Behavior tick (B1, ms per frame) and pools

| Phase              | Tick  | Prepare | Wait  | Grass | Replay | Fluid chunks/frame | Grass voxels/frame |
|--------------------|------:|--------:|------:|------:|-------:|-------------------:|-------------------:|
| Generation 200 m/s | 0.690 | 0.160   | 0.121 | 0.378 | 0.024  | 1.5                | 904                |
| Loading 200 m/s    | 0.084 | 0.031   | 0.021 | 0.024 | 0.007  | 0.3                | 61                 |

Per-frame averages hide the shape: the tick runs at its own cadence. **Tick-led hitch records** (worst frame of each
record, all three runs, session hitch files) split into two kinds:

| Kind      | Records (examples)                                                | Tick ms   | Prepare / Wait / Grass / Replay ms | Fluid chunks | Snapshot MB | Grass voxels  |
|-----------|-------------------------------------------------------------------|-----------|------------------------------------|--------------|-------------|---------------|
| Fluid-led | B1 frames 174 772, 180 820, 183 964, 184 947; B2 174 619, 180 975 | 44.6–81.6 | 21–42 / 15–26 / 0–6 / 5–9          | 172–278      | 193–312     | 0–15 911      |
| Grass-led | B1 frames 181 505, 185 505; B2 53 518; B3 53 328                  | 23.2–31.3 | 1–3 / 0 / 21–29 / 0                | 12–35        | 13–39       | 51 275–75 412 |

Pool misses per phase (B1): `JobArrayMisses` **22 796** at 200 m/s, 1 754 at 100 m/s, 456 at 50 m/s, 770 in the
ensure sweep; section-pool misses 1 330 at 100 m/s and 0 at 200 m/s (as on 2026-10-03); the chunk-data, mesh-output
and save-buffer pools 0; `FluidTickerPoolMisses` 53 in the 200 m/s loading phase; `UntimedJobs` 0 everywhere.

### Hitch records per phase (B1)

| Phase              | Hitch files | Worst ms | GC-correlated | Led by                 |
|--------------------|------------:|---------:|--------------:|------------------------|
| Generation 50 m/s  | 4           | 58.8     | 1             | Tick 3, MeshSchedule 1 |
| Generation 100 m/s | 7           | 60.5     | 1             | Tick 5, LightMerge 2   |
| Generation 200 m/s | 8           | 123.5    | 5             | LightMerge 7, Tick 1   |
| Ensure Generated   | 5           | 40.5     | 0             | Tick 3, LightMerge 2   |
| Loading 100 m/s    | 7           | 47.6     | 0             | Tick 6, MeshSchedule 1 |
| Loading 200 m/s    | 15          | 83.8     | 0             | Tick 15                |

## Result — GC.Alloc call stacks, 200 m/s (IL2CPP Development, Master)

Acceptance checks: the capture window is 758 frames = the report's `Frames sampled` (758); 0.0 % of bytes without a
call stack; GC.Alloc + outside-GC.Alloc = the thread totals (1 129 460.0 KB both); coverage = 1 129 460 KB ÷ (1 464.3
KB × 758) = **1.02**. 8 867 chunks generated, 0 loaded.

| Call site                                                                                   | KB / generated chunk | Share  | 2026-10-03    |
|---------------------------------------------------------------------------------------------|---------------------:|-------:|--------------:|
| `ChunkSerializer.WriteSection` → `ReadOnlySpan<T>.ToArray()` (ThreadPool save)              | 98.0                 | 76.9 % | 98.0 / 76.9 % |
| `StandardChunkGenerator.ExpandStructure`                                                    | 7.9                  | 6.2 %  | 8.0 / 6.3 %   |
| `ChunkData.AddToSkylightQueue` / `AddToBlocklightQueue` (`Queue<T>` regrowth)               | 8.5                  | 6.7 %  | 8.9 / 7.0 %   |
| `ChunkSection` constructor (pool growth, 24 frames)                                         | 3.2                  | 2.5 %  | 3.1 / 2.4 %   |
| `RegionFile.SaveChunkData`                                                                  | 1.96                 | 1.5 %  | 1.95 / 1.5 %  |
| Async I/O (`LoadChunkAsync`, `SaveChunkAsync`, `UnloadChunks`' continuations)               | 4.2                  | 3.3 %  | 4.7 / 3.7 %   |
| Other sites (the rest of the save path, merge `List`/`HashSet` growth, per-frame UI/render) | 3.6                  | 2.9 %  | 2.7 / 2.2 %   |
| **Total**                                                                                   | **127.4**            | 100 %  | **127.4**     |

By thread: 900 787 KB on the ThreadPool save threads (79.8 %), 228 673 KB on the main thread. **ES-28 in a player:** the
UI band composite and blur passes (`UIBlurChain.Record` / `PublishGlobal`, `UIBandCompositePass`,
`UIBandLayers.SortingValueOf`) allocate **592 KB over 758 frames = 0.78 KB per frame**, the same as the Editor's 0.75.
Unity's render loop adds 2.0 KB per frame with no project frame on the stack.

## Result — vd 32

**First run (B4): did not complete.** The generation pass ran. The ensure sweep then sat at "settling…" for 23
minutes: from ≈ 240 s in, **two lighting jobs completed and two were scheduled every frame**, with every other queue
empty and the lighting waiting set pinned at 387. `HasActiveJobs` never turned false, so the run never left the settle
wait (filed as [Lighting Bug 23](../Bugs/LIGHTING_BUGS.md)). The livelock cost 0.53 ms (`LightMerge`) + 0.27 ms
(`LightSchedule`) of main-thread time every frame. No report was written. The generation phases below are cut from the
session file instead of a report: the first phase starts 61 frames after the first frame with no job in flight, and
each phase runs 30 000 ms of summed wall time. **Treat the boundaries as approximate (± a few frames).**

| Phase (approximate) | Frames | Wall p50 | Wall p99 | Worst | CPU p50 | GC KB/frame | GCs | Resident chunks | Top slots (mean ms)                                          |
|---------------------|-------:|---------:|---------:|------:|--------:|------------:|----:|----------------:|--------------------------------------------------------------|
| 10 m/s              | 3 754  | 6.26     | 38.40    | 72.0  | 2.87    | 52.1        | 5   | 5 299           | LightMerge 0.59, MeshSchedule 0.37, LightSchedule 0.25       |
| 20 m/s              | 2 904  | 6.94     | 45.27    | 81.8  | 3.01    | 156.3       | 10  | 5 299           | LightMerge 1.26, LightSchedule 0.52, MeshSchedule 0.38       |
| 50 m/s              | 723    | 41.15    | 85.59    | 120.1 | 40.64   | 1 274.3     | 18  | 5 333           | LightMerge 15.25, LightSchedule 5.81, GenerationProcess 2.90 |
| 100 m/s             | 650    | 47.09    | 106.80   | 125.4 | 46.54   | 2 163.0     | 26  | 5 346           | LightMerge 18.74, LightSchedule 6.87, GenerationProcess 3.90 |
| 200 m/s             | 872    | 32.95    | 91.36    | 150.7 | 32.26   | 1 609.2     | 23  | 5 336           | LightMerge 13.11, LightSchedule 4.74, GenerationProcess 2.99 |

The HUD showed 5 446 MB total (3 893 MB native + 1 553 MB managed) during the stuck ensure sweep.

**Second run (B5): completed** (499 s, exit 0, report written). The livelock is therefore intermittent (1 of 2 runs).
Every phase cross-checked against the report. The generation phases agree with B4's approximate cuts (10 m/s p50 6.37 vs
6.26 ms, 50 m/s 41.8 vs 41.2 ms).

| Phase              | Frames | Wall p50 | Wall p99 | Worst  | CPU p50 | GC KB/frame | GCs | GPU p99 | Hitch records (led by)                  |
|--------------------|-------:|---------:|---------:|-------:|--------:|------------:|----:|--------:|-----------------------------------------|
| Generation 10 m/s  | 3 700  | 6.37     | 40.77    | 59.52  | 2.93    | 63.9        | 5   | 9.30    | 27 (LightMerge 16, Tick 11)             |
| Generation 20 m/s  | 2 850  | 7.03     | 46.45    | 71.03  | 2.99    | 167.6       | 10  | 10.90   | 39 (LightMerge 29, Tick 9, Unload 1)    |
| Generation 50 m/s  | 720    | 41.78    | 89.71    | 115.76 | 41.39   | 1 310.3     | 19  | 23.08   | 6 (LightMerge 5, Tick 1)                |
| Generation 100 m/s | 644    | 46.49    | 107.70   | 121.86 | 46.09   | 2 152.2     | 25  | 13.50   | 2 (Tick 2)                              |
| Generation 200 m/s | 863    | 32.37    | 85.84    | 154.38 | 31.95   | 1 625.3     | 23  | 4.29    | 5 (Tick 5)                              |
| Ensure Generated   | 15 319 | 9.52     | 49.72    | 150.47 | 4.64    | 113.7       | 23  | 13.88   | 176 (Tick 145, LightMerge 20, other 11) |
| Loading 50 m/s     | 3 014  | 7.15     | 36.52    | 234.19 | 3.48    | 40.0        | 1   | 10.41   | 16 (Tick 15, ViewDistance 1)            |
| Loading 100 m/s    | 1 925  | 13.37    | 59.41    | 99.39  | 10.87   | 82.8        | 2   | 16.10   | 28 (Tick 27, ViewDistance 1)            |
| Loading 200 m/s    | 1 087  | 26.25    | 123.47   | 236.98 | 25.70   | 242.2       | 4   | 16.91   | 19 (Tick 18, LightMerge 1)              |

- **Slots.** Generation 50 m/s: `LightMerge` 15.2, `LightSchedule` 5.7, `GenerationProcess` 2.9 ms per frame. Loading
  200 m/s: `LightMerge` 6.8, `LightSchedule` 3.9, `DiskLoadApply` 3.9, `Tick` 2.3 (p99 96.5, worst 213.6),
  `ViewDistance` 2.0 ms.
- **Behavior tick at vd 32.** The worst loading-pass tick frames reach 130–214 ms. The 237 ms frame had prepare 111,
  wait 41, grass 44 and replay 14 ms, for **704 fluid chunks and 780 MB of snapshot copies in one tick**, 110 793 grass
  voxels and 118 fluid-ticker pool misses. Per frame the loading pass at 200 m/s averages 9.2 fluid chunks and 10.2 MB
  copied.
- **Memory** (report, 20 Hz snapshots): total 4.2–4.6 GB average in the generation pass (peak 5.1 GB), **6.4–6.8 GB in
  the loading pass (peak 7.0 GB)**; native peak 5.2 GB, managed heap 1.85–1.88 GB average (peak 1.93 GB). Roadmap goal 3
  caps vd 32 at the recorded ≈ 5.7 GB peak (P9-2 Q4).
- **Pools.** `JobArrayMisses` 14 048 at 200 m/s and 35 880 in the ensure sweep; section-pool misses 2 046 in the ensure
  sweep; 36 save-buffer misses at 200 m/s (0 at vd 10).

## Analysis

- **200 m/s is lighting-merge-bound, not tick-bound.** Over whole phases, `LightMerge` (16.3 ms) and `LightSchedule`
  (5.6 ms) are 62 % of the frame. At ≈ 40 lighting jobs per frame that is ≈ 0.41 ms of merge and ≈ 0.14 ms of schedule per
  job on the main thread, against P9-1's 0.18 / 0.15 ms. The schedule matches; the merge has more than doubled since
  P9-1 (cause not attributed). LightMerge leads 7 of 8 hitch records at 200 m/s. The PM-2…PM-8 Master readings ranked
  `Tick` first from the 16 hitch records held at run end, on shorter runs (generation 200 + loading 50 m/s); over every
  frame of the phase the merge dominates both the mean and the hitches. This is ES-7 and ES-12's case, now measured per
  job.
- **The behavior tick owns the loading pass's hitches, the ensure sweep's and the startup tail.** Fluid-led records copy
  172–278 chunk maps per tick (193–312 MB) and wait 15–26 ms for the fluid jobs. Grass-led ones run the managed grass
  tick at 51–75 k voxels for 21–29 ms. It is the same mechanism PM-8 found in the generation pass, now in every phase
  that touches water or grass. Filed as **ES-29**.
- **Garbage is unchanged.** 127.4 KB per generated chunk with the same ranking, so ES-26 / ES-27 / ES-9 are scored
  against either capture. 200 m/s garbage per frame (1.45–1.54 MB) and collections (85 per phase) match PM-6's runs.
- **ES-28 holds in a player:** 0.78 KB per frame of UI-pass garbage at every frame rate.
- **Disk reads during fast generation are slow per hit** (7.7–7.9 ms at 50 and 200 m/s against 0.24–0.41 ms elsewhere).
  Hits are rare there (12 and 180), and the saves of 2 500–9 600 chunks run in the same window. Contention with the
  save stream is the likely cause *(inferred)*. ES-9 territory; not filed.
- **The job-array pool churns at speed.** `ChunkJobArrayPool.Return` disposes buffers past the device-calibrated
  retention cap, and 200 m/s demand exceeds it: 22 796 native `Persistent` allocations of 64–128 KB in 30 s (≈ 27 per
  frame). Cost unpriced; listed as a candidate, not filed.
- **vd 32 saturates at 50 m/s,** with the same lighting-merge-bound profile as vd 10 at 200 m/s: p50 41.8–46.5 ms at
  50–100 m/s. Below saturation the vd-32 frame is ≈ 6.4–7 ms p50 and GPU-heavier (GPU p50 6.2–6.7 ms), and its hitches
  are the lighting merge and the behavior tick. The tick scales with residency: up to 704 fluid chunks per tick, ≈ 2.5×
  vd 10's worst. Memory peaks at 7.0 GB, above goal 3's ≈ 5.7 GB. This is goal 3's first per-slot baseline.

## Verdict details

Baseline only. New and updated roadmap entries:

- **ES-29** (new, filed from this capture and the time-to-stable report): the behavior tick's fluid prepare and wait,
  and the managed grass tick.
- **ES-7 / ES-12:** the merge's per-job main-thread cost (≈ 0.41 ms) and its share of the 200 m/s and vd-32 frames.
- **ES-26 / ES-27 / ES-28:** before-numbers re-confirmed, ES-28's for the first time in a player.
- **Candidates, not filed:** disk-read contention during fast generation; job-array pool churn; Unity's 2.0 KB per
  frame render-loop allocation.
- **Lighting Bug 23** (vd-32 livelock, 1 of 2 runs) can stop a vd-32 benchmark in a settle wait.

**Reproduce:** `"Minecraft Clone.exe" -force-d3d11 -mc-run benchmark -mc-set perfMonitorTier=Capture -mc-mute -mc-quit`,
then `python Tools/Python/summarize_perf_session.py --report <BenchmarkRun_*.log> --session <PerfSession_*>`.
