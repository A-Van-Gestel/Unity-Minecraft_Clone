# ES-29 — Behavior Tick Spikes: Shared Section Arena + Burst Grass, Before and After

| Field           | Value                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                         |
|-----------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-10-11                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |
| **Branch**      | `main`                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        |
| **Commit**      | after: `30144721` (step 1, the shared section arena) + uncommitted step 2 (Burst grass tick); before: the kept `2026-10-10 - ES-27 Master [IL2CPP]` build (`442ad4ef` + the then-uncommitted ES-27 work)                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
| **Captured by** | **IL2CPP player, Master configuration, non-development**, Burst AOT, D3D11, Unity 6000.6.5f1, i9-9900K / RTX 4070 Ti, `-mc-set perfMonitorTier=Capture`, legs alternated before/after in one session: `-mc-run startup-new` × 5 per leg; benchmark route Gen = `200`, loading pass `50` m/s, vd 10 × 3 per leg; the same at vd 32 × 1 per leg. Session files read per phase with `Tools/Python/summarize_perf_session.py`, run-end hitch records with `Tools/Python/tabulate_tick_hitches.py`                                                                                                                                                                                                                                 |
| **Verdict**     | **GO for ES-29's prepare and grass levers.** Tick-led hitch records fall **9 / 11 / 10 → 2 / 1 / 0** per vd-10 run (and 15 → 10 at vd 32, now led by the wait instead of the prepare); copies fall **2.0 → 0.16 MB per frame** at 200 m/s and the grass tick **10–29 ms → ≈ 0.1 ms** in the hitch records; the post-drain tick frame halves (median Tick 4.05 → 1.62 ms) and drained → stable has its median cut **19.2 → 10.2 s**; total memory drops **≈ 400 MB at vd 10 and 720 MB (peak) at vd 32**. Left: the wait and the replay of the first tick on new chunks (→ `ES-31`), and an **unexplained** +5 % `LightMerge` per lighting job in the 200 m/s phase that ES-29's code does not touch — see "Open observation". |

> Scores [`ES-29`](../Design/ENGINE_SCALING_PERFORMANCE_ROADMAP.md) against the 2026-10-10 re-measurement
> ([`ENGINE_SCALING_ES0_REMEASURE_IL2CPP_2026-10-10`](ENGINE_SCALING_ES0_REMEASURE_IL2CPP_2026-10-10_BENCHMARK.md)) that
> filed it, through a same-session control of the previous build.

## The change

- **Step 1 — one shared section arena.** Each fluid ticker used to copy nine full 128 KB voxel maps (its chunk and every
  populated neighbor) on the main thread, whatever its active Y-band. Now every prepare asks the tick's
  `TickSnapshotArena` for its band's sections; the arena copies each (chunk, section) once per tick into one packed
  native list that every job reads, and empty sections point at a shared zeroed one. All prepares run before any job is
  scheduled. The tickers no longer hold snapshot buffers.
- **Step 2 — Burst grass.** `GrassTickJob` replaces the managed grass loop, reading the same arena (center + four
  cardinal neighbors); the drain replays its output in bucket order.
- Exactness is proven in the Editor, not here: BH-D1[L|HB] (21 fixtures incl. the new grass and band-top ones), the
  shared-arena World-tick determinism stress, and prove-red mutants for each new fixture.

## Before measuring — the shape of the work (Editor probe, seed 4242, vd 10)

| Moment                     | Fluid chunks / voxels | Full maps copied | Band only | Band + one copy per chunk | Grass voxels         |
|----------------------------|-----------------------|------------------|-----------|---------------------------|----------------------|
| Settled spawn              | 15 / 395              | 16 MB            | 3 MB      | 2 MB                      | 0                    |
| Land teleport, first tick  | 11 / 1 476            | 12 MB            | 3 MB      | 1 MB                      | 17 806 (→ 459 → 106) |
| Ocean teleport, first tick | 181 / 702 728         | 187 MB           | 95 MB     | 15 MB                     | —                    |
| Same ocean, next tick      | 58 / 3 310            | 63 MB            | 14 MB     | 6 MB                      | —                    |

The loading-pass spikes are the first tick over generation's blanket registration: every water and grass voxel of a
new chunk is registered and the first tick drops almost all of them. Banding alone halves an ocean burst's copies
(whole columns are registered, so bands are 4–5 sections deep); one copy per chunk is the larger lever.

## Result — run-end hitch records (vd 10, 16 held per run)

| Leg              | Tick-led records | Led by                       | Prepare at a ~114-chunk tick | Snapshot at that tick | Grass in grass-led records     |
|------------------|------------------|------------------------------|------------------------------|-----------------------|--------------------------------|
| Before 1 / 2 / 3 | **9 / 11 / 10**  | Prepare 14, Grass 11, Wait 5 | 12.8–14.8 ms                 | 128 MB                | 10.8–28.9 ms at 28–69 k voxels |
| After 1 / 2 / 3  | **2 / 1 / 0**    | Wait 3                       | 6.6–8.2 ms                   | 9.4–9.8 MB            | — (0.01–0.12 ms in any record) |

The after-records that remain are fluid-led by the **wait** at 106–261 fluid chunks carrying 338–580 k fluid voxels —
the blanket first tick. Their prepare (6.6–9.6 ms) is now mostly the bucket partition of those voxels, not copying.

## Result — Behavior tick per phase (ms per frame, mean over every frame)

| Phase (vd 10)      | Leg    | Tick        | Prepare   | Wait      | Grass     | Replay | Snapshot MB/frame | Tick-led hitch files |
|--------------------|--------|-------------|-----------|-----------|-----------|--------|-------------------|----------------------|
| Generation 200 m/s | Before | 0.58–0.63   | 0.19–0.21 | 0.15–0.16 | 0.19–0.22 | 0.04   | 1.95–2.16         | 6 / 7 / 7            |
| Generation 200 m/s | After  | 0.32–0.36   | 0.09–0.10 | 0.19–0.21 | 0.005     | 0.03   | 0.15–0.17         | 1 / 0 / 0            |
| Ensure sweep       | Before | 0.023–0.024 | 0.010     | 0.005     | 0.007     | 0.001  | 0.10–0.11         | 2 / 3 / 3            |
| Ensure sweep       | After  | 0.011       | 0.004     | 0.005     | 0.000     | 0.001  | 0.01              | 1 / 0 / 0            |

The wait grows slightly: the grass rules now run on the workers, inside it. In the 50 m/s loading pass neither leg had
much tick work (0.01–0.02 ms per frame); one after-run recorded a single wait-led hitch file there (37.5 ms).

| Phase (vd 32)      | Leg    | Tick | Prepare | Wait | Grass | Snapshot MB/frame | p99 ms | Worst ms | Tick-led hitch files |
|--------------------|--------|------|---------|------|-------|-------------------|--------|----------|----------------------|
| Generation 200 m/s | Before | 1.89 | 0.78    | 0.56 | 0.36  | 7.72              | 111.9  | 154.8    | 8                    |
| Generation 200 m/s | After  | 1.11 | 0.33    | 0.61 | 0.009 | 0.60              | 86.0   | 147.7    | 3                    |
| Ensure sweep       | Before | 0.58 | 0.32    | 0.09 | 0.12  | 3.45              | 58.0   | 165.2    | 155                  |
| Ensure sweep       | After  | 0.24 | 0.10    | 0.10 | 0.003 | 0.37              | 53.9   | 155.0    | 48                   |
| Loading 50 m/s     | Before | 0.62 | 0.30    | 0.11 | 0.17  | 3.19              | 46.4   | 194.5    | 28                   |
| Loading 50 m/s     | After  | 0.24 | 0.10    | 0.10 | 0.003 | 0.32              | 34.4   | 98.2     | 9                    |

At vd 32 the before leg's Tick-led records were prepare-led (14 of 15); the after leg's are
wait-led (9 of 10). The before leg copied 213–390 MB per held tick record. The after leg's worst tick — 525 fluid chunks, **2.7 M fluid voxels**, 36 MB copied — took 132.7 ms: prepare 49.7,
wait 67.1, replay 13.6. That is the blanket first tick at scale, `ES-31`'s target.

## Result — time to stable (`startup-new`, 5 runs per leg)

| Leg    | Drained (s) | Drained → stable (s)        | Median   | Tick-led slow frames after the drain | Median post-drain tick: Tick / Prepare / copied |
|--------|-------------|-----------------------------|----------|--------------------------------------|-------------------------------------------------|
| Before | 4.90–5.44   | 12.4, 20.7, 19.2, 26.2, 6.2 | **19.2** | 13, 22, 20, 27, 7 (worst 5.2 ms)     | 4.05 / 3.30 ms / 44.9 MB at ~39 fluid chunks    |
| After  | 4.84–4.97   | 26.8, 6.3, 10.2, 21.3, 8.3  | **10.2** | 4, 3, 3, 3, 3 (worst 3.1 ms)         | 1.62 / 0.97 ms / 4.9 MB at ~38 fluid chunks     |

"Slow" is a frame over the stable rule's threshold, max(1.25 × median, median + 2 ms) = 6.2 ms at 240 Hz. The tick no
longer leads the tail: what still resets the 2 s stable window is `LightMerge`, `MeshSchedule` and frames where no slot
exceeds 0.5 ms (GPU, present or OS). That explains the remaining spread (6–27 s in both legs), and corrects the
roadmap's 2026-10-10 reading that the tail was held back *only* by the tick's prepare — the tick led 35–40 % of the
before leg's slow frames.

## Result — memory

| Leg    | vd 10 average total (3 runs) | vd 10 peak total | vd 32 average | vd 32 peak  |
|--------|------------------------------|------------------|---------------|-------------|
| Before | 2026–2042 MB                 | 2219–2240 MB     | 5467 MB       | 6372 MB     |
| After  | 1630–1639 MB                 | 1803–1827 MB     | 4825 MB       | **5649 MB** |

The fluid ticker pool is never pruned and each ticker held 9 × 128 KB of snapshot buffers, so a burst of ~270 tickers
kept ≈ 300 MB of it *(inferred from the pool's peak, not measured per allocation)*. A ticker now holds only its ≈ 290 KB padded volume, and the arena is trimmed after each tick. The vd-32 peak
lands under the roadmap's ≈ 5.7 GB goal-3 figure.

## Open observation — 200 m/s frames are longer in the after leg

At 200 m/s the after leg recorded fewer, longer frames: 1 023 / 1 067 / 1 154 against 1 139 / 1 267 / 1 224 (p50 18.8–29.8
against 14.3–24.1 ms), while its p99 and worst were lower (69.8–73.9 / 105.8–122.7 against 74.4–80.0 / 124.7–160.7 ms).
Throughput is identical — 31.2–31.5 k lighting and 9.65–9.76 k generation jobs per phase in both legs — and the other
large slots' totals stay close (LightSchedule −3 %, MeshSchedule −2 %, GenerationProcess +1.5 %, Apply −6 %), except two:

| Slot (200 m/s, total over the phase) | Before         | After                 |
|--------------------------------------|----------------|-----------------------|
| `LightMerge` per merged lighting job | 0.419–0.427 ms | 0.442–0.445 ms (+5 %) |
| `Unload`                             | 0.71–1.01 s    | 1.02–1.17 s           |

ES-29 does not touch the lighting merge or the unload path (the lighting gather is byte-identical to its base), the
tick's own main-thread time per frame fell, and the 200 m/s phase is merge-bound and self-scaling: a longer frame admits
more lighting jobs into the next. The before build is the ES-27 build, made from `442ad4ef` plus the ES-27 work before
it was committed, so it is not provably identical to ES-29's base. **Not attributed.** The control that would settle
it is a Master build of ES-29's base (`2cf61044`), run against the after build in reverse order. Until then the 200 m/s
mean is not claimed as either a regression or a non-change.

## Verdict details

GO, shipped default-on with no flag: the tick's output is unchanged by construction and the parity gates prove it in the
Editor. Targeted metrics all moved the expected way in every pair: the prepare's copies (−92 %), the grass tick (gone
from the main thread), Tick-led hitches, the post-drain tick frame and memory. The levers ES-29 reserved but did not take
— the wait and the replay — are the first tick over the blanket registration and are filed as `ES-31`.

**Reproduce:** `"Minecraft Clone.exe" -force-d3d11 -mc-run benchmark -mc-set benchmarkGenerationSpeeds=200
-mc-set benchmarkLoadingSpeeds=50 -mc-set perfMonitorTier=Capture [-mc-set viewDistance=32] -mc-mute -mc-quit`, and
`-mc-run startup-new -mc-set perfMonitorTier=Capture`, through `Tools/Python/run_player.py`.
