# ES-27 — Pooled BFS Light Queues: GC.Alloc per Generated Chunk, Managed Heap and Frame Health, Before and After

| Field           | Value                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                |
|-----------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-10-10                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           |
| **Branch**      | `main`                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                               |
| **Commit**      | `442ad4ef` + uncommitted ES-27 (`ChunkPoolManager` light-queue pools, `ChunkData` rented queues, serializer/snapshot reads that never rent)                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| **Captured by** | Benchmark route, Gen = `200` only (loading pass 50 m/s, not read), 30 s phase, vd 10 — **IL2CPP player, Development build, IL2CPP configuration Master**, Burst AOT, D3D11, Unity 6000.6.5f1, i9-9900K / RTX 4070 Ti; Editor Profiler attached with **GC.Alloc call stacks**; 1 player capture + 2 Editor (Mono) screening captures, read with the same `GcCallstacks` query as ES-0/ES-26; then **3 IL2CPP Master (non-development) runs** of the ES-27 build and **3 drift-control runs of the ES-26 Master build** in the same session, at default speeds with `-mc-set perfMonitorTier=Capture`, read per phase with `summarize_perf_session.py` |
| **Verdict**     | **GO (GC + memory)** — the queue sites fall **10.49 → 0.78 KB per generated chunk (−92.6 %)**, and GC.Alloc per generated chunk **30.7 → 20.3 KB (−33.8 %)**. Master, against the same-session control: **managed heap −59 MB at 200 m/s** (avg 333–334 → 275–276 MB) and −20 MB at 10 m/s; GC per frame 289.6–295.4 → 258.4–270.0 KB at 200 m/s and halved at 10–20 m/s; frame time neutral (wall p99 84.3–86.9 vs 83.3–89.6 ms).                                                                                                                                                                                                                   |

> Scores [`ES-27`](../Design/ENGINE_SCALING_PERFORMANCE_ROADMAP.md) against the ES-26 capture
> [`ENGINE_SCALING_ES26_SERIALIZER_SPAN_COPY_IL2CPP_2026-10-10`](ENGINE_SCALING_ES26_SERIALIZER_SPAN_COPY_IL2CPP_2026-10-10_BENCHMARK.md),
> the post-ES-26 attribution the item was ranked from. Same route, build configuration, phase marker, capture control and
> query; only the ES-27 code differs. The Master comparison does **not** use ES-26's E1–E3 rows: see "Drift".

## The change

Each `ChunkData` owned two managed `Queue<LightQueueNode>` (16 B per node) that `Reset` only cleared, so every pooled
chunk — about 1 530 of them at vd 10 — grew its own queues, doubling past its own high-water mark. `Queue<T>` in Unity's
class libraries has no `EnsureCapacity` or `Capacity` (checked by reflection in the Editor: grow factor 200 %, minimum
step 4), so a queue can only be pre-sized at construction.

The queues now come from two `ConcurrentDynamicPool<Queue<LightQueueNode>>` in `ChunkPoolManager`, one per channel.
A chunk rents one on its first enqueue and returns it when its nodes are flushed to a lighting job or the chunk is
reset. A new pooled queue starts at the p99 flush size measured below (3 800 sky, 1 400 block nodes). Reads on the save
path go through `TryGetSkylightQueue` / `TryGetBlocklightQueue`, which never rent, so a chunk with nothing pending
writes a bare 0 count (bytes unchanged — golden-hash guard B6) and loads without renting. The idle-pool caps are rows of
the unload boundary: 8 rows while there is demand (248 at vd 10), 1 row after 90 s without.

Guard: Serialization Round-Trip **B17** (rent only while pending, return on flush and reset, warm reuse, 0 `GC.Alloc`
samples filling a pooled queue to capacity — red with capacity 0), plus light-queue leak balance in the round-trip and
deserialization-robustness suites. Validate All: 814/814 baselines across 32 suites.

## Sizing — what the queues hold (Editor probe, 2 runs at 200 m/s)

A temporary probe recorded each queue's count when it was flushed to a lighting job, and when a chunk was reset with
nodes still queued (removed before the change). Two runs agreed to within 1 %; the second:

| Queue               | Samples | Mean  | p50   | p90   | p99       | p99.9 | Max   |
|---------------------|--------:|------:|------:|------:|----------:|------:|------:|
| Skylight at flush   | 20 869  | 704   | 28    | 2 397 | **3 761** | 4 860 | 4 891 |
| Blocklight at flush | 8 552   | 142   | 35    | 306   | **1 358** | 1 457 | 1 512 |
| Skylight at reset   | 9 399   | 1 158 | 1 176 | 2 504 | 4 037     | 4 860 | 4 891 |
| Blocklight at reset | 4 721   | 178   | 141   | 320   | 1 306     | 1 436 | 1 442 |

Queues holding nodes on loaded chunks at once: mean 44, peak 519 (the startup burst). `ChunkData` pool population:
1 530. Sized per instance at p99 the queues would hold ≈ 125 MB; pooled, only queues in use plus the idle cap exist.

During one run the pool counters showed ~480 queues per channel created by the startup burst, pruned back to the idle
cap, and **236 sky / 225 block in use at the 200 m/s peak**. A first cap of 4 rows (124) pruned queues the phase then
re-created (96 sky + 163 block pool misses in the Editor window), so the cap was raised to 8 rows; with it the sky pool
created nothing after startup.

## Result — IL2CPP, 200 m/s window (870 frames, 8 818 generated chunks)

Window = `Frames sampled` (870); 0.0 % of bytes without a call stack; GC.Alloc equals the thread totals. Heap-delta
figure: 255.0 KB/frame × 870 ÷ 8 818 = 25.2 KB per chunk (GC.Alloc covers 81 % of it).

**By thread:** main thread 150 434.5 KB (17.1 KB per chunk); ThreadPool (`Scripting Threads / #0`) 28 990.5 KB
(3.3 KB per chunk).

| Call site                                                          | Thread | KB/chunk  | ES-26 KB/chunk | Calls   | Frames | Note                                               |
|--------------------------------------------------------------------|--------|----------:|---------------:|--------:|-------:|----------------------------------------------------|
| `StandardChunkGenerator.ExpandStructure`                           | Main   | 7.96      | 7.97           | 345 397 | 559    | ES-9                                               |
| `ChunkSection..ctor` (section pool misses)                         | Main   | 2.86      | 3.37           | 2 096   | 21     | bursty pool growth                                 |
| `RegionFile.SaveChunkData`                                         | Pool   | 1.95      | 1.95           | 26 692  | 350    | ES-9                                               |
| `LoadChunkAsync` state machine                                     | Main   | 1.57      | 1.57           | 146 313 | 287    | ES-9                                               |
| `SaveChunkAsync` state machine                                     | Main   | 1.39      | 1.39           | 114 372 | 350    | ES-9 / ES-10                                       |
| `World.UnloadChunks`                                               | Main   | 1.25      | 1.26           | 110 696 | 370    | ES-9 / ES-10                                       |
| **`ChunkData.AddToSkylightQueue`** (`Queue<T>` growth)             | Main   | **0.67**  | **8.47**       | 50      | 32     | p99 overflow: a 3 800 queue doubling once          |
| `WorldJobManager.MergeCompletedLightingJob` (`List<T>` growth)     | Main   | 0.38      | 0.60           | 15      | 12     | `ListPool<DeferredLightMod>` warm-up; not in scope |
| `CountingWriteStream.WriteByte` → `LZ4Stream` rent                 | Pool   | 0.38      | 0.29           | 26      | 5      | `ArrayPool` misses, run-to-run burst               |
| `ChunkSerializer.Serialize`                                        | Pool   | 0.26      | 0.26           | 35 611  | 350    | ES-9                                               |
| `ChunkStorageManager.GetRegion`                                    | Pool   | 0.25      | 0.25           | 17 775  | 456    | ES-9                                               |
| `CompressionFactory.CreateOutputStream`                            | Pool   | 0.20      | 0.20           | 17 796  | 350    | ES-9                                               |
| `WorldData.QueueSkylightRecalculation` (`HashSet<T>` growth)       | Main   | 0.16      | —              | 1 350   | 33     | `HashSetPool` warm-up; not in scope                |
| **`ChunkData.AddToBlocklightQueue`** (`Queue<T>` growth)           | Main   | **0.08**  | **1.46**       | 17      | 16     | p99 overflow                                       |
| **`ChunkStorageManager.CreateSerializationSnapshot`** (`Queue<T>`) | Main   | **0.03**  | **0.56**       | 4       | 4      | same queue mechanism; ES-26 listed it under ES-9   |
|                                                                    |        | **20.35** | **30.73**      |         |        | **Total**                                          |

No `Queue<T>` constructor (pool miss) appears among the attributed sites in the window.

## Result — Editor (Mono) screening

| Capture                         | Frames (window / phase) | Generated | GC.Alloc KB/chunk | Queue sites KB/chunk                                                      |
|---------------------------------|------------------------:|----------:|------------------:|---------------------------------------------------------------------------|
| ES-26 Editor screening (before) | —                       | —         | 34.1              | `AddToSkylightQueue` ≈ 11.3 (ES-0 Editor pass)                            |
| 4-row idle cap                  | 549 / 549               | 5 463     | 21.2              | 3.5 (overflow 1.39 + sky misses 1.04 + block misses 0.65 + snapshot 0.42) |
| **8-row idle cap (shipped)**    | 606 / 680               | 6 363     | 18.1              | 1.86 (overflow 1.19 + snapshot 0.43 + block misses 0.24)                  |

The shipped-cap capture lost the first 74 frames of the phase to the profiler's 2 000-frame buffer, so its per-chunk
figures read slightly low. Editor numbers are screening only.

## Result — IL2CPP Master, frame health (3 runs + 3 same-session controls)

One `Windows - Production` build of the ES-27 code (e1–e3), then the existing ES-26 Master build re-run as a drift
control (c1–c3), back to back, 450 s each, all exit 0. Default speeds, vd 10, Capture tier.

| Generation 200 m/s | Frames | Wall p50 | Wall p99 | Worst | CPU p99 | GC KB/frame | GCs | Hitches |
|--------------------|-------:|---------:|---------:|------:|--------:|------------:|----:|--------:|
| c1 (ES-26 build)   | 711    | 42.4     | 84.3     | 123.6 | 83.5    | 292.6       | 17  | 92      |
| c2 (ES-26 build)   | 724    | 42.1     | 85.4     | 110.7 | 80.1    | 289.6       | 17  | 67      |
| c3 (ES-26 build)   | 750    | 41.6     | 86.9     | 150.6 | 86.6    | 295.4       | 18  | 81      |
| **e1**             | 748    | 40.7     | 89.6     | 128.6 | 89.3    | **258.4**   | 21  | 91      |
| **e2**             | 751    | 40.8     | 84.5     | 115.3 | 82.6    | **259.1**   | 20  | 95      |
| **e3**             | 764    | 41.5     | 83.3     | 113.6 | 83.1    | **270.0**   | 21  | 89      |

Slower phases (GC KB/frame and collections, c1–c3 → e1–e3):

| Phase              | GC KB/frame, control | GC KB/frame, ES-27 | GCs, control | GCs, ES-27 |
|--------------------|---------------------:|-------------------:|-------------:|-----------:|
| Generation 10 m/s  | 2.5–3.0              | 1.1–1.3            | 3–4          | 1          |
| Generation 20 m/s  | 5.3–5.8              | 2.0–2.3            | 6–7          | 3          |
| Generation 50 m/s  | 9.9–11.1             | 6.6–7.2            | 9            | 6–7        |
| Generation 100 m/s | 22.8–33.7            | 24.9–30.1          | 12           | 12–13      |
| Loading 200 m/s    | 12.2–18.1            | 10.5–13.8          | 5–6          | 6          |

Managed heap per generation phase (avg / peak, MB):

| Phase   | Control c1–c3             | ES-27 e1–e3               | Δ avg     |
|---------|---------------------------|---------------------------|----------:|
| 10 m/s  | 228.1–229.7 / 235.3–237.9 | 207.3–210.6 / 210.4–214.8 | −19.9     |
| 20 m/s  | 241.4–243.6 / 252.0–254.1 | 211.0–214.5 / 215.4–218.9 | −30.3     |
| 50 m/s  | 261.7–263.3 / 274.2–275.1 | 219.2–221.0 / 224.6–229.6 | −42.3     |
| 100 m/s | 295.0–297.2 / 324.4–325.1 | 246.1–247.7 / 269.0–270.2 | −49.1     |
| 200 m/s | 333.2–334.4 / 348.3–350.0 | 274.8–275.7 / 287.8–290.4 | **−58.6** |

Whole run: average total memory 1 948.3–1 955.6 → 1 887.4–1 888.7 MB; peak 2 246.8–2 346.4 → 2 177.4–2 183.9 MB.

Disk I/O per save at 200 m/s: serialize 0.163–0.184 → 0.174–0.206 ms, write 1.466–1.851 → 1.449–1.802 ms (unchanged
within run-to-run spread).

## Drift

The ES-27 Master runs were first read against ES-26's E1–E3 (15:xx the same day): wall p99 69.2–72.0 → 83.3–89.6 ms,
with **every** phase slower, including 10 m/s (CPU p50 0.7 → 0.9 ms), where the light queues are nearly idle. Re-running
the unchanged ES-26 build at 22:0x gave 84.3–86.9 ms: the machine had drifted by ≈ 14 ms of p99 over the afternoon.
Every Master comparison above is therefore against the same-session control, not against the ES-26 report.

## Analysis

- **The regrowth is gone at its source.** The three queue sites fall from 10.49 to 0.78 KB per chunk; what remains is
  the ~1 % of queues that outgrow the p99 capacity and double once. A doubled queue goes back to the pool at its new
  size, so this overflow should fade over a long session (inferred; this capture starts with the 200 m/s phase).
- **Memory is the larger effect.** The control's managed heap grows 106 MB across the five generation phases as each
  of ~1 530 pooled chunks warms its own queues; with the pool it grows 66 MB. The gap is 19.9 MB at 10 m/s and
  58.6 MB at 200 m/s — and the per-chunk design would have scaled with the area of the load radius, the pool with its
  rows.
- **Collections fall where the queues dominated, and rise slightly at 200 m/s.** 10–50 m/s collect a third to two
  thirds as often. At 200 m/s the phase collects 20–21 times against 17–18 while allocating ≈ 10 % less per frame;
  Boehm triggers a collection after allocating a fraction of the heap, and the heap is 59 MB smaller *(inferred, not
  instrumented)*. Collections in that phase are not frame-bound: wall p99 ranges overlap and p50 is ≈ 1 ms lower.
- **The frame is still the lighting merge's.** p50 ≈ 41 ms at 200 m/s in both builds, as in ES-26.
- **Not fixed here:** `ListPool<DeferredLightMod>` growth in the merge (0.38 KB) and `HashSetPool` growth in
  `QueueSkylightRecalculation` (0.16 KB) — shared Unity pools whose lists warm up in bursts, which a per-instance
  capacity cannot reach.

## Finding — unloaded chunks carry large pending queues

The probe saw about **9 400 `ChunkData` resets per Editor run with skylight nodes still queued (median 1 176)**. That
count includes the save snapshots and load shells that copy those nodes, so fewer distinct chunks are involved (the probe
did not separate them). The source is chunks unloaded at the frontier with skylight nodes pending — woken by neighbors'
merges before their own lighting ran *(inferred)*. Their nodes are copied into the save snapshot, written with the chunk
and replayed on load. Out of scope for ES-27; filed under ES-7 in the roadmap.

## Verdict details

GO, shipped default-on with no flag: the change moves where the queues live, not what they hold, and B6's golden bytes
prove the save format did not move. Follow-ups are named above; none is a regression of this change.
