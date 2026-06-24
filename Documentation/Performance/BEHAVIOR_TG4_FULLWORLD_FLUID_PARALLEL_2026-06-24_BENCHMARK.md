# TG-4 Phase 4a — Parallel vs Serial Interior-Fluid Tick (realized-win A/B)

| Field            | Value                                                                                                                                                                                                                                                                                                                                                                            |
|------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**     | 2026-06-24 (local)                                                                                                                                                                                                                                                                                                                                                               |
| **Branch**       | `feat/Modular-World-Generation-&-World-Types`                                                                                                                                                                                                                                                                                                                                    |
| **Builds (A/B)** | **Serial Phase-3** (RC 77-4, GUID `b946f0e5…`) vs **Parallel Phase-4a** (RC 77-5, GUID `e55c9a92…`) — identical except the `EnableParallelFluidTick` flag + the Phase-4a code (C1–C4)                                                                                                                                                                                            |
| **Captured by**  | `Assets/Scripts/Benchmarks/FluidStressController.cs` — **IL2CPP player**, 5×5 = 25-chunk suspended-basin flood (same harness/scenario as the 2026-06-23 attribution pass)                                                                                                                                                                                                        |
| **Samples**      | **4 runs each** (serial + parallel), ~1,650 flood frames/run                                                                                                                                                                                                                                                                                                                     |
| **Verdict**      | **P4a is correct and delivers a real but marginal win.** It shaves a repeatable **~6.6 ms (~4.6 %) off the dam-break tick spike** and leaves the **sustained tick unchanged (~2 % of frame)**. The frame is **not tick-bound** — the spike is managed-border-dominated and the sustained frame is lighting-dominated. Decision: **ship default-on (worker-guarded); defer P4b.** |

> Follows [`BEHAVIOR_TG4_FULLWORLD_FLUID_2026_06_23_BENCHMARK.md`](BEHAVIOR_TG4_FULLWORLD_FLUID_2026_06_23_BENCHMARK.md)
> (the attribution gate, captured against the **managed** tick). This A/B isolates the realized benefit of the
> Phase-4a parallelization on top of the already-shipped Phase-3 interior Burst.

## What this measures

The only difference between the two builds is the fluid tick path: **serial** runs the Phase-3 hybrid (interior
`FluidTickJob.Run()` per chunk, border managed); **parallel** schedules the interior jobs across chunks
(`World.ProcessTickUpdatesParallel`) and drains serially. Same 25-chunk flood, same `WorldFrameProfiler`
Tick/Apply/Mesh/Light split. The **Tick** phase wraps `ProcessTickUpdates`, so the parallel tick's main-thread cost
— serial snapshot-prep + the blocking `Complete` wait + serial drain — is captured honestly.

## Result — IL2CPP player, 25-chunk flood (means of 4 runs each)

**Sustained flood — avg ms/frame:**

| Build          | Frame |   Tick | Tick % |  Mesh | Light | Light % |
|----------------|------:|-------:|-------:|------:|------:|--------:|
| Serial (P3)    |  9.95 |  0.188 |  ~1.9% | 1.359 | 6.730 |    ~67% |
| Parallel (P4a) |  9.56 |  0.184 |  ~1.9% | 1.331 | 6.312 |    ~66% |
| **Δ**          | −0.39 | −0.004 |      — | −0.03 | −0.42 |       — |

**Dam-break tick spike — max single-frame Tick ms:**

| Build          | Run 1 | Run 2 | Run 3 | Run 4 |             Mean | Range           |
|----------------|------:|------:|------:|------:|-----------------:|-----------------|
| Serial (P3)    | 145.4 | 143.1 | 141.4 | 142.8 |        **143.2** | [141.4, 145.4]  |
| Parallel (P4a) | 140.0 | 135.9 | 135.1 | 135.4 |        **136.6** | [135.1, 140.0]  |
| **Δ**          |       |       |       |       | **−6.6 (−4.6%)** | nearly disjoint |

Min FPS = 7 in both (the dam-break frame). Frame peak ≈ 150 ms (serial) vs ≈ 143 ms (parallel).

## Reading the numbers

- **The tick-spike reduction is real and repeatable**, not single-sample noise: the two spike distributions barely
  overlap (parallel's worst, 140.0, sits just under serial's best, 141.4). P4a consistently saves ~6.6 ms on the
  cascade-start frame.
- **The sustained-frame `Frame` drop (−0.39 ms) is NOT P4a.** It tracks the `Light` drop (−0.42 ms) almost exactly,
  and P4a does not touch lighting (identical lighting on identical applied world state). The actual P4a effect on
  the sustained frame is the sustained-tick delta: **~0.004 ms** — zero. The Light/Frame difference is
  session/lighting variance between runs.
- **Why the win is small:** only the **margin-4 interior (~25 % of columns)** parallelizes, and it was *already*
  Burst (Phase 3) — a thin, fast slice. The dam-break spike is **~95 % managed border (Tier-2) + serial overhead**
  (snapshot-prep, blocking `Complete`, the managed-border drain), none of which P4a moves. A 143 ms → 137 ms spike
  is still a ~7-FPS single-frame stall — **imperceptible improvement**.
- **The worst flood *frames* (≈ 142–150 ms) have Tick ≈ 0.001 ms** — they are render/generation/GC hitches on a
  *different* frame than the tick spike. So even eliminating the tick spike entirely leaves coincident ~145 ms
  hitches during the dam-break.
- **GC/frame trended slightly higher** under parallel (≈17 vs ≈14 KB avg) — plausibly the cost of scheduling ~25
  jobs/tick — a small tax partly offsetting the compute saving.

## Conclusion / decisions

- **Phase 4a is correct (C4 determinism gate + this A/B) and shipped default-on**, behind a worker-count guard
  (`JobsUtility.JobWorkerCount ≥ 2`) that falls back to the serial path on core-starved hosts. The win is real but
  marginal and does not change perceived performance.
- **Phase 4b (close the Tier-2 border via the neighbor view) is deferred.** The spike *is* border-dominated, so P4b
  would target the right cost — but the coincident render/gen worst-frame hitches and the lighting-bound sustained
  frame mean the tick is not the frame bottleneck. Low ROI for the 🔴 neighbor-view work.
- **The ocean-frame lever is Lighting (~66 % sustained)** and the render/gen worst-frame hitches — not the tick.
  That is where a follow-up §5-style attribution should point.

> **Hardware note.** Captured on an i9-9900K (16 threads). On a slower-but-many-core target (e.g. an 8-core
> Snapdragon 690 phone), the *absolute* interior-compute saving could be larger (slower cores → bigger absolute
> slice), but the *relative* ceiling is the same (border-dominated). The worker-count guard protects genuinely
> core-starved hosts (≤2 cores) where scheduling overhead would exceed the win.
