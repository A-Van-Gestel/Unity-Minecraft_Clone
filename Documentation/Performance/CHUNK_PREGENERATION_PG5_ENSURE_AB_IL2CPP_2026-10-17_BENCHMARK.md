# PG-5 — Benchmark Ensure Pass: 50 m/s Flight vs Station-Walk Pre-Generation, Before and After

| Field           | Value                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
|-----------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-10-17                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     |
| **Branch**      | `main`                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                         |
| **Commit**      | after (B): `f0564f1c` (PG-4); before (A): a fresh Master build of its parent `cd367094` from a clean detached checkout. Both reports read their commit with no `-dirty`                                                                                                                                                                                                                                                                                                                                                                                                                                                        |
| **Captured by** | `BenchmarkController` (`-mc-run benchmark`) — **IL2CPP player, Master configuration, non-development**, Managed Code Variant `Release`, Burst AOT (safety checks off), D3D11, Unity 6000.6.6f1, i9-9900K (16 threads), 64 GB, Windows 10. The build's default speeds; legs alternated. Set 1: vd 10 × 3 and vd 32 × 2 per leg, each build at its own first-launch calibration, light background load (one video stream). Set 2: vd 32 × 2 per leg with the calibrated budgets pinned to their defaults, idle machine                                                                                                           |
| **Verdict**     | **GO.** Total runtime **441 → 245 s at vd 10 (−44.4 %)** and **479 → 312 s at vd 32 (−34.9 %, matched budgets)**; coverage 100.0 % on every run of both legs; every B run `0 stalled`, `0 settle timeouts`, loading pass `0 generated`. Loading-pass averages are inside the noise at both view distances, but **vd 32's tail is not**: B's loading pass runs ≈ 17 % more lighting and mesh schedules at 50 m/s (3× the post-delivery lighting corrections), hits an empty `ChunkSection` pool, and records more hitches at every speed. vd-32 loading-pass hitch counts are therefore not comparable across the PG-4 boundary |

> Scores PG-5 of [`CHUNK_PREGENERATION.md`](../Design/CHUNK_PREGENERATION.md) (§7) and answers its §8 Q3. PG-4 deleted
> the 50 m/s ensure flight with no fallback flag (design §3.4), so the legs are separate builds of the change and its
> exact base, run alternately in one session (the separate-build rule in [`README.md`](README.md)). The A leg's
> ensure-pass costs reproduce the 2026-10-10 default-speed rows of design §2.1.

## The change

`BenchmarkController.RunEnsureGeneratedPass` no longer flies the closed 12-point loading tour at 50 m/s (a fixed
9 889 m / 197.8 s). It yields `World.PregenerateRegion(BenchmarkRouteGeometry.TourChunkBounds(loadDistance))`: a
station walk that checks the tour's chunk rectangle against disk and the resident set, walks only the stations whose
cover holds a missing chunk, and waits at each until its targets are populated and lit (design §4). The report's
`Ensure sweep:` line becomes `Pre-generation:`. Nothing else in the route changes: the generation pass, the transition
and the loading pass run the same code in both legs.

## What this measures (and what it does NOT)

- **Measures:** the benchmark's total runtime, the ensure phase's duration and work, and whether the terrain it leaves
  on disk changes the loading pass (`Loading Pass` performance, frame health, FPS and pipeline telemetry rows).
- **Does NOT measure:** `/chunk generate` in normal play (same driver, a different caller), or anything about the
  pre-generator at a view distance other than 10 and 32.
- **Correctness gates** (doc §7 PG-5), read from code paths the pre-generator does not own: tour coverage
  `100.0 %` when the loading pass starts, and `0 generated` with a non-zero loaded-from-disk count in every loading
  phase.

## Methodology

- **Builds:** `Windows - Production` profile, built through `player-runner` 2026-10-17: A from a detached checkout of
  `cd367094` (the Editor recompiled and was confirmed to hold the A code), then B from `main` at `f0564f1c`. The A
  build stamped `bundleVersion`, which was restored before B so B's stamp stayed clean.
- **Arguments:** `-force-d3d11 -mc-run benchmark -mc-set viewDistance=<vd> -mc-mute -mc-quit`; set 2 adds
  `-mc-set maxMeshRebuildsPerFrame=10 -mc-set maxLightJobsPerFrame=32`. Each run loads a fresh deterministic world.
- **Settings blocks differ in set 1.** Each fresh build ran its own first-launch device calibration (OM-1), under the
  video load, and they settled differently:

  | Build | `maxMeshRebuildsPerFrame` | `maxLightJobsPerFrame` |
  |-------|--------------------------:|-----------------------:|
  | A     | **9**                     | 32                     |
  | B     | 10                        | **30**                 |

  Both are budgets the loading pass consumes, so set 1's vd-32 loading rows compare two configurations. Set 2 pins
  both to the code defaults on both legs (at the default, the Settings block lists neither; `Player.log` records
  `Session settings overrides (command line): viewDistance=32, maxMeshRebuildsPerFrame=10, maxLightJobsPerFrame=32,
  masterVolume=0` on all four runs). vd 10 was not re-run: its set-1 loading rows are already inside the noise.
- **Load:** set 1 ran with one video stream on another monitor, a steady light load common to both alternated legs,
  accepted because the gate's decisive measure is a large runtime delta. Load can only lengthen B's work-bound walk, never A's fixed-speed flight, so it biases the
  runtime result against B. Set 2 ran on an idle machine. The two sets are not compared with each other.
- **Order:** set 1 — vd 10 A B A B A B, then vd 32 A B A B (runs 01–10); set 2 — vd 32 A B A B (runs 11–14).
- **Stat semantics:** per phase, `Avg CPU` / `Avg Wall` are frame means over the 30 s phase; `Wall p50` / `p99`
  are every-frame percentiles; `Hitches` is the report's hitch-frame count; "noise" is the spread between same-leg runs
  of the same view distance.

## Result — gates

| Run | Leg | vd   | Missing found | Stations walked / grid | Ensure phase | Populate + settle | Slowest station | Stalled | Settle timeouts | Coverage at loading start |
|-----|-----|------|--------------:|-----------------------:|--------------|-------------------|-----------------|--------:|----------------:|---------------------------|
| 01  | A   | 10   | —             | —                      | 3m 17s       | —                 | —               | —       | —               | 8 249 / 8 249 (100.0 %)   |
| 02  | B   | 10   | 174           | 6 / 16                 | 3.6 s        | 2.3 s + 1.2 s     | 0.9 s           | 0       | 0               | 8 249 / 8 249 (100.0 %)   |
| 03  | A   | 10   | —             | —                      | 3m 17s       | —                 | —               | —       | —               | 8 249 / 8 249 (100.0 %)   |
| 04  | B   | 10   | 104           | 3 / 16                 | 2.2 s        | 1.3 s + 0.8 s     | 0.8 s           | 0       | 0               | 8 249 / 8 249 (100.0 %)   |
| 05  | A   | 10   | —             | —                      | 3m 17s       | —                 | —               | —       | —               | 8 249 / 8 249 (100.0 %)   |
| 06  | B   | 10   | 91            | 3 / 16                 | 2.1 s        | 1.4 s + 0.6 s     | 0.7 s           | 0       | 0               | 8 249 / 8 249 (100.0 %)   |
| 07  | A   | 32   | —             | —                      | 3m 17s       | —                 | —               | —       | —               | 18 225 / 18 225 (100.0 %) |
| 08  | B   | 32   | 6 648         | 5 / 9                  | 46.3 s       | 42.2 s + 4.0 s    | 12.1 s          | 0       | 0               | 18 225 / 18 225 (100.0 %) |
| 09  | A   | 32   | —             | —                      | 3m 17s       | —                 | —               | —       | —               | 18 225 / 18 225 (100.0 %) |
| 10  | B   | 32   | 6 510         | 5 / 9                  | 45.5 s       | 41.0 s + 4.5 s    | 11.9 s          | 0       | 0               | 18 225 / 18 225 (100.0 %) |
| 11  | A   | 32 m | —             | —                      | 3m 17s       | —                 | —               | —       | —               | 18 225 / 18 225 (100.0 %) |
| 12  | B   | 32 m | 5 945         | 5 / 9                  | 38.3 s       | 34.2 s + 4.0 s    | 10.2 s          | 0       | 0               | 18 225 / 18 225 (100.0 %) |
| 13  | A   | 32 m | —             | —                      | 3m 17s       | —                 | —               | —       | —               | 18 225 / 18 225 (100.0 %) |
| 14  | B   | 32 m | 5 914         | 5 / 9                  | 39.5 s       | 36.6 s + 2.9 s    | 10.0 s          | 0       | 0               | 18 225 / 18 225 (100.0 %) |

`32 m` = set 2 (matched budgets). Every B run reads `0 re-marked for saving`. The rectangle is `(5, 3)..(95, 93)`
(8 281 chunks) at vd 10 and `(−17, −14)..(117, 120)` (18 225) at vd 32. The A leg also reached 100.0 % at vd 32 on all
four runs: the documented flight gap (design §2.1, 99.7 %) did not occur in this session. The slowest vd-32 station is
(16, 86) in all four B runs.

**Ensure phase `Chunks populated`** (the whole phase, which includes chunks the walk's stations stream beyond the
rectangle):

| Run | Leg | vd   | Generated | Loaded from disk |
|-----|-----|------|----------:|-----------------:|
| 01  | A   | 10   | 120       | 20 192           |
| 02  | B   | 10   | 182       | 3 060            |
| 03  | A   | 10   | 171       | 20 139           |
| 04  | B   | 10   | 107       | 1 786            |
| 05  | A   | 10   | 84        | 20 226           |
| 06  | B   | 10   | 98        | 1 786            |
| 07  | A   | 32   | 5 384     | 47 335           |
| 08  | B   | 32   | 8 223     | 11 957           |
| 09  | A   | 32   | 5 410     | 47 289           |
| 10  | B   | 32   | 8 093     | 12 562           |
| 11  | A   | 32 m | 4 988     | 47 739           |
| 12  | B   | 32 m | 7 243     | 12 517           |
| 13  | A   | 32 m | 4 985     | 47 756           |
| 14  | B   | 32 m | 7 407     | 12 662           |

At vd 32 the B phase generates 1 298 / 1 493 (set 2) more chunks than the `Pre-generation:` line counts: terrain past
the rectangle's edge that the edge stations stream to `LoadDistance`. They are not targets, so nothing waits for them
to settle.

## Result — total runtime

| vd   | A runs            | B runs              | A mean | B mean | Change               | Design §2.2 projection |
|------|-------------------|---------------------|-------:|-------:|----------------------|------------------------|
| 10   | 7m 21s ×3 (441 s) | 4m 6s, 4m 5s, 4m 4s | 441 s  | 245 s  | **−196 s (−44.4 %)** | 440 → 260 s (−40 %)    |
| 32   | 8m 21s, 8m 3s     | 5m 21s, 5m 19s      | 492 s  | 320 s  | −172 s (−35.0 %)     | 480 → 300–340 s        |
| 32 m | 7m 59s ×2 (479 s) | 5m 11s, 5m 13s      | 479 s  | 312 s  | **−167 s (−34.9 %)** | 480 → 300–340 s        |

Both projections hold: vd 10 beats its estimate, vd 32 lands inside its range. B's transition (`Drain + Save +
Unload`) lasts 0.5 s at vd 10 and 6.2–7.3 s at vd 32, against A's 0.0–0.1 s: the walk ends at its last station, away
from the loading tour's start, so the transition re-streams. That cost is inside the totals above. Peak total memory at
vd 32: A 3 969–4 036 MB, B 4 490–4 555 MB.

## Result — loading pass, set 1 (vd 10, unmatched budgets, light load)

| Run | Leg | Phase   | Frames | Avg CPU | Avg Wall | Wall p50 | Wall p99 | Worst   | Hitches | Avg Wall FPS | Min Wall FPS | Loaded from disk |
|-----|-----|---------|-------:|--------:|---------:|---------:|---------:|--------:|--------:|-------------:|-------------:|-----------------:|
| 01  | A   | 50 m/s  | 13 353 | 1.5 ms  | 2.3 ms   | 1.6 ms   | 8.2 ms   | 25.7 ms | 0       | 447.5        | 172.4        | 3 509            |
| 03  | A   | 50 m/s  | 13 484 | 1.3 ms  | 2.3 ms   | 1.5 ms   | 7.9 ms   | 18.1 ms | 0       | 452.3        | 217.4        | 3 511            |
| 05  | A   | 50 m/s  | 13 824 | 1.3 ms  | 2.3 ms   | 1.4 ms   | 7.8 ms   | 19.5 ms | 0       | 464.4        | 222.1        | 3 509            |
| 02  | B   | 50 m/s  | 13 801 | 1.4 ms  | 2.3 ms   | 1.4 ms   | 8.1 ms   | 21.0 ms | 0       | 464.6        | 162.4        | 3 509            |
| 04  | B   | 50 m/s  | 14 413 | 1.4 ms  | 2.2 ms   | 1.4 ms   | 7.8 ms   | 20.8 ms | 0       | 487.3        | 195.1        | 3 509            |
| 06  | B   | 50 m/s  | 13 956 | 1.4 ms  | 2.3 ms   | 1.4 ms   | 7.7 ms   | 22.1 ms | 0       | 469.4        | 158.6        | 3 509            |
| 01  | A   | 100 m/s | 11 150 | 1.9 ms  | 2.8 ms   | 1.8 ms   | 10.6 ms  | 24.0 ms | 0       | 376.6        | 140.7        | 5 942            |
| 03  | A   | 100 m/s | 10 761 | 1.9 ms  | 2.9 ms   | 1.8 ms   | 10.1 ms  | 23.7 ms | 0       | 363.8        | 135.9        | 5 942            |
| 05  | A   | 100 m/s | 11 782 | 1.7 ms  | 2.7 ms   | 1.7 ms   | 8.9 ms   | 23.0 ms | 0       | 396.9        | 160.0        | 5 940            |
| 02  | B   | 100 m/s | 11 250 | 1.9 ms  | 2.8 ms   | 1.7 ms   | 10.2 ms  | 26.9 ms | 0       | 378.0        | 135.9        | 5 941            |
| 04  | B   | 100 m/s | 11 492 | 2.0 ms  | 2.8 ms   | 1.7 ms   | 10.1 ms  | 28.6 ms | 0       | 388.5        | 114.1        | 5 940            |
| 06  | B   | 100 m/s | 11 315 | 1.9 ms  | 2.8 ms   | 1.7 ms   | 10.3 ms  | 24.8 ms | 0       | 381.9        | 123.1        | 5 943            |
| 01  | A   | 200 m/s | 8 509  | 2.9 ms  | 3.8 ms   | 2.6 ms   | 12.5 ms  | 22.0 ms | 0       | 283.0        | 97.1         | 12 239           |
| 03  | A   | 200 m/s | 8 633  | 2.8 ms  | 3.7 ms   | 2.5 ms   | 12.0 ms  | 22.5 ms | 0       | 286.7        | 116.0        | 12 237           |
| 05  | A   | 200 m/s | 8 636  | 2.8 ms  | 3.7 ms   | 2.5 ms   | 11.7 ms  | 22.1 ms | 0       | 287.6        | 116.4        | 12 237           |
| 02  | B   | 200 m/s | 7 751  | 3.4 ms  | 4.2 ms   | 2.8 ms   | 13.9 ms  | 39.3 ms | 6       | 259.8        | 62.1         | 12 235           |
| 04  | B   | 200 m/s | 7 972  | 3.3 ms  | 4.1 ms   | 2.7 ms   | 13.2 ms  | 22.0 ms | 0       | 266.9        | 79.2         | 12 236           |
| 06  | B   | 200 m/s | 8 268  | 3.0 ms  | 3.8 ms   | 2.7 ms   | 12.1 ms  | 28.7 ms | 0       | 276.3        | 94.3         | 12 239           |

Every loading phase: `0 generated`. 50 and 100 m/s are inside the same-leg spread. 200 m/s reads 0.2–0.6 ms higher
average CPU on B (A 2.8–2.9 ms, B 3.0–3.4 ms) with one run's 6 hitches, under unmatched budgets and a light load: a
lean, not a resolved delta.

## Result — loading pass, vd 32

### Set 1 (unmatched budgets, light load)

| Run | Leg | Phase   | Frames | Avg CPU | Avg Wall | Wall p50 | Wall p99 | Worst    | Hitches | Avg Wall FPS | Min Wall FPS | Loaded from disk |
|-----|-----|---------|-------:|--------:|---------:|---------:|---------:|---------:|--------:|-------------:|-------------:|-----------------:|
| 07  | A   | 50 m/s  | 2 505  | 10.4 ms | 13.8 ms  | 8.8 ms   | 38.3 ms  | 187.8 ms | 73      | 86.7         | 27.0         | 11 232           |
| 09  | A   | 50 m/s  | 2 474  | 10.6 ms | 13.9 ms  | 8.9 ms   | 39.5 ms  | 187.5 ms | 49      | 85.4         | 26.1         | 11 232           |
| 08  | B   | 50 m/s  | 2 389  | 12.1 ms | 14.7 ms  | 8.6 ms   | 43.6 ms  | 163.3 ms | 89      | 83.7         | 24.1         | 11 150           |
| 10  | B   | 50 m/s  | 2 455  | 12.0 ms | 14.3 ms  | 8.3 ms   | 44.9 ms  | 157.9 ms | 115     | 86.2         | 24.1         | 11 181           |
| 07  | A   | 100 m/s | 1 403  | 19.8 ms | 22.0 ms  | 20.8 ms  | 41.9 ms  | 86.6 ms  | 24      | 47.2         | 29.7         | 15 348           |
| 09  | A   | 100 m/s | 1 524  | 17.3 ms | 20.0 ms  | 18.9 ms  | 37.4 ms  | 110.7 ms | 10      | 51.0         | 36.8         | 15 413           |
| 08  | B   | 100 m/s | 1 405  | 20.3 ms | 22.2 ms  | 20.4 ms  | 45.3 ms  | 120.3 ms | 20      | 47.5         | 26.5         | 15 382           |
| 10  | B   | 100 m/s | 1 528  | 18.3 ms | 20.3 ms  | 19.3 ms  | 41.4 ms  | 114.1 ms | 29      | 51.4         | 31.8         | 15 411           |
| 07  | A   | 200 m/s | 792    | 36.9 ms | 37.7 ms  | 37.5 ms  | 59.6 ms  | 71.5 ms  | 3       | 27.0         | 22.8         | 25 111           |
| 09  | A   | 200 m/s | 977    | 31.7 ms | 32.5 ms  | 32.2 ms  | 56.9 ms  | 79.3 ms  | 33      | 34.1         | 24.7         | 26 697           |
| 08  | B   | 200 m/s | 864    | 33.8 ms | 34.6 ms  | 34.4 ms  | 54.0 ms  | 109.6 ms | 2       | 29.4         | 24.6         | 26 428           |
| 10  | B   | 200 m/s | 856    | 34.1 ms | 34.9 ms  | 34.6 ms  | 54.4 ms  | 64.8 ms  | 3       | 29.3         | 23.7         | 26 681           |

### Set 2 (budgets pinned to 10 / 32 on both legs, idle)

| Run | Leg | Phase   | Frames | Avg CPU | Avg Wall | Wall p50 | Wall p99 | Worst    | Hitches | Avg Wall FPS | Min Wall FPS | Loaded from disk |
|-----|-----|---------|-------:|--------:|---------:|---------:|---------:|---------:|--------:|-------------:|-------------:|-----------------:|
| 11  | A   | 50 m/s  | 2 703  | 9.5 ms  | 12.8 ms  | 8.2 ms   | 36.1 ms  | 188.9 ms | 40      | 93.0         | 27.2         | 11 260           |
| 13  | A   | 50 m/s  | 2 692  | 9.6 ms  | 12.9 ms  | 8.3 ms   | 36.3 ms  | 165.6 ms | 34      | 92.8         | 27.7         | 11 230           |
| 12  | B   | 50 m/s  | 2 774  | 10.5 ms | 12.9 ms  | 7.6 ms   | 38.9 ms  | 159.7 ms | 76      | 96.7         | 26.7         | 11 261           |
| 14  | B   | 50 m/s  | 2 823  | 10.1 ms | 12.6 ms  | 7.6 ms   | 40.0 ms  | 149.7 ms | 64      | 98.8         | 26.5         | 11 262           |
| 11  | A   | 100 m/s | 1 655  | 15.7 ms | 18.4 ms  | 16.9 ms  | 35.3 ms  | 77.9 ms  | 5       | 55.4         | 40.0         | 15 411           |
| 13  | A   | 100 m/s | 1 699  | 15.2 ms | 18.0 ms  | 16.3 ms  | 35.0 ms  | 87.1 ms  | 9       | 56.9         | 36.1         | 15 411           |
| 12  | B   | 100 m/s | 1 828  | 14.4 ms | 16.7 ms  | 15.4 ms  | 34.7 ms  | 104.4 ms | 18      | 61.5         | 38.1         | 15 380           |
| 14  | B   | 100 m/s | 1 816  | 14.6 ms | 16.8 ms  | 15.2 ms  | 35.9 ms  | 100.3 ms | 19      | 60.9         | 36.0         | 15 349           |
| 11  | A   | 200 m/s | 1 054  | 29.2 ms | 30.0 ms  | 29.8 ms  | 50.1 ms  | 74.3 ms  | 26      | 36.7         | 25.4         | 27 894           |
| 13  | A   | 200 m/s | 1 040  | 29.6 ms | 30.3 ms  | 29.9 ms  | 51.2 ms  | 73.7 ms  | 8       | 36.0         | 25.2         | 27 876           |
| 12  | B   | 200 m/s | 1 082  | 28.9 ms | 29.7 ms  | 29.3 ms  | 50.6 ms  | 87.4 ms  | 50      | 37.6         | 26.0         | 28 175           |
| 14  | B   | 200 m/s | 1 083  | 29.1 ms | 29.9 ms  | 29.6 ms  | 51.8 ms  | 64.6 ms  | 54      | 37.6         | 26.5         | 28 175           |

Every loading phase in both sets: `0 generated`. Group total (set 2): Avg CPU 17.7 / 17.7 ms (A) vs 17.5 / 17.5 ms
(B); Avg Wall FPS 62.9 / 63.0 vs 66.3 / 67.0.

### Pipeline telemetry, vd 32 loading pass (`Work amplification` and `Pool misses`)

"Corrections" is the `no live trace` column: quota units spent after a chunk's first delivery, dominated by
post-delivery re-lights and re-meshes (an upper bound). "Served" is the pass's total.

| Run | Leg | Phase   | Light served | Light corrections | Mesh served | Mesh corrections | `ChunkSection` pool misses |
|-----|-----|---------|-------------:|------------------:|------------:|-----------------:|---------------------------:|
| 07  | A   | 50 m/s  | 12 087       | 838               | 12 859      | 3 964            | 0                          |
| 09  | A   | 50 m/s  | 12 005       | 823               | 12 865      | 4 000            | 0                          |
| 08  | B   | 50 m/s  | 13 906       | 2 690             | 15 228      | 6 475            | 1 303                      |
| 10  | B   | 50 m/s  | 14 005       | 2 654             | 15 206      | 6 446            | 1 349                      |
| 11  | A   | 50 m/s  | 11 769       | 851               | 13 170      | 4 157            | 0                          |
| 13  | A   | 50 m/s  | 11 927       | 866               | 13 211      | 4 215            | 0                          |
| 12  | B   | 50 m/s  | 13 792       | 2 765             | 15 428      | 6 653            | 2 029                      |
| 14  | B   | 50 m/s  | 14 098       | 2 698             | 15 496      | 6 683            | 2 012                      |
| 07  | A   | 100 m/s | 16 693       | 441               | 16 436      | 3 291            | 0                          |
| 09  | A   | 100 m/s | 16 678       | 458               | 16 601      | 3 406            | 8                          |
| 08  | B   | 100 m/s | 17 269       | 1 438             | 17 671      | 5 115            | 525                        |
| 10  | B   | 100 m/s | 17 458       | 1 471             | 17 991      | 5 247            | 544                        |
| 11  | A   | 100 m/s | 16 541       | 480               | 17 778      | 4 424            | 0                          |
| 13  | A   | 100 m/s | 16 649       | 501               | 17 811      | 4 458            | 0                          |
| 12  | B   | 100 m/s | 17 311       | 1 511             | 18 332      | 5 513            | 680                        |
| 14  | B   | 100 m/s | 17 432       | 1 521             | 18 255      | 5 442            | 626                        |
| 07  | A   | 200 m/s | 26 179       | 444               | 14 707      | 701              | 0                          |
| 09  | A   | 200 m/s | 27 684       | 370               | 15 467      | 1 028            | 0                          |
| 08  | B   | 200 m/s | 27 499       | 683               | 16 163      | 1 397            | 0                          |
| 10  | B   | 200 m/s | 28 031       | 681               | 16 019      | 1 044            | 0                          |
| 11  | A   | 200 m/s | 28 919       | 324               | 16 923      | 734              | 0                          |
| 13  | A   | 200 m/s | 29 265       | 404               | 16 899      | 798              | 0                          |
| 12  | B   | 200 m/s | 29 109       | 651               | 16 919      | 1 023            | 0                          |
| 14  | B   | 200 m/s | 29 545       | 756               | 16 989      | 1 211            | 0                          |

The ensure phase's own `ChunkSection` pool misses: A 1 869–1 917 (0.35–0.38 per generated chunk), B 40–910
(0.01–0.11).

## Analysis

**Total runtime.** The flight cost a fixed 197.8 s whatever was missing; the walk costs 2.1–3.6 s at vd 10 (91–174
missing chunks of its 8 281-chunk rectangle, which the generation pass had already 97.9–98.9 % covered) and 38.3–46.3 s
at vd 32 (5 914–6 648 missing). The saving, 196 s at vd 10 and 167 s at vd 32 (set 2), is the whole flight minus the
walk and B's longer transition. The walk's vd-32 time is 89–93 % populate, as in the PG-4 Editor run (87 %).

**The calibration confound.** Set 1's vd-32 50 m/s row read B +14.8 % average CPU and +13.8 % wall p99. With the
budgets matched (set 2) that falls to +7.9 % and +9.0 %: about half the set-1 gap was the two builds' different
first-launch calibration (A's mesh budget of 9 is 10 % below B's).

**What remains is real, consistent across all four B runs at vd 32, and concentrated in the tail:**

- At 50 m/s B serves ≈ 17 % more lighting and mesh schedules (light 13 792–14 098 vs 11 769–11 927 in set 2) for
  the same chunks loaded (11 230–11 262 both legs). The excess is post-delivery work: lighting corrections rise 3.2×
  (≈ 850 → ≈ 2 700) and mesh corrections 1.6×; at 100 m/s lighting corrections are still 3× and at 200 m/s ≈ 1.8×.
- B's loading pass allocates `ChunkSection` buffers the pool cannot supply (≈ 2 000 at 50 m/s, ≈ 650 at 100 m/s,
  0 at 200 m/s); A's never does. The flight streamed ≈ 52 000 chunks through the pipeline just before the loading
  pass and grew the pool on the way (1 869–1 917 misses during its own phase); the walk streams ≈ 20 000. This one
  has a mechanism: the A leg warmed the pool for the loading pass, and B's loading pass pays the growth itself.
- Frame level, set 2: averages are equal or better on B (50 m/s Avg Wall 12.8–12.9 vs 12.6–12.9 ms, p50 8.2–8.3 vs
  7.6 ms, Avg Wall FPS 92.8–93.0 vs 96.7–98.8; 100 m/s B ≈ 6 % lower average CPU), but the tail is worse: 50 m/s
  wall p99 36.1–36.3 → 38.9–40.0 ms and hitches 34–40 → 64–76; hitches are also higher at 100 m/s (5–9 → 18–19) and
  200 m/s (8–26 → 50–54).
- **Not attributed:** the source of the extra post-delivery lighting. Lighting never re-marks a chunk for saving
  (`ModifiedChunks` is added to only by `Populate`, `ModifyVoxel` and the walk's re-mark, which counted 0 here), so the
  flight's 47 000 reloads did not rewrite the A leg's disk state. Candidates: the 1 298–1 493 chunks the edge stations
  generate past the rectangle without waiting on their lighting, and the different place each leg's transition starts
  from. Neither was instrumented.

vd 10 shows none of this at 50 and 100 m/s; its 200 m/s row leans the same way (+0.2–0.6 ms average CPU) inside a
set run with unmatched budgets and a light load.

## Verdict details

- **GO for PG-4's pre-generator in the benchmark.** The acceptance's runtime half is met with margin at both view
  distances, every B gate passed, and coverage is 100.0 % by construction rather than by luck.
- **The loading-pass half is met on averages and missed on vd 32's tail.** The terrain the walk leaves behind, together
  with a smaller pool, makes the vd-32 loading pass do more post-delivery lighting and record about twice the hitches.
  A vd-32 hitch count or p99 from a build before `f0564f1c` is therefore **not comparable** with one after it;
  averages and FPS are. vd 10 is comparable.
- **Follow-ups filed in the design doc (§8), not done here:** attribute the extra post-delivery lighting, and decide
  whether the pool difference should be closed on the benchmark side. Any change to the pipeline, its flags, gates or
  unload arms is outside v1 (design §7.2).
- Flight-gap note: the A leg reached 100.0 % at vd 32 in all four runs, so this session does not reproduce the
  2026-10-11 99.7 % capture. The gap was probabilistic; the walk removes it by construction.
