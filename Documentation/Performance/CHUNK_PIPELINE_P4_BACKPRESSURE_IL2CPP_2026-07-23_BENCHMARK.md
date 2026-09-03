# P-4 §3.4/§3.5 Pipeline Backpressure — IL2CPP player A/B (time budgets + panic gate vs legacy)

| Field           | Value                                                                                                                                                                                                                                                                                                                                                |
|-----------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-07-23 18:28 (local)                                                                                                                                                                                                                                                                                                                             |
| **Branch**      | `feat/world-scaling`                                                                                                                                                                                                                                                                                                                                 |
| **Commit**      | `fbef09c4`                                                                                                                                                                                              |
| **Captured by** | `P4BackpressureBenchmark` TEMP harness (F10 in-build, single 6-leg run, report auto-written by `BenchmarkEnvironment`) — **IL2CPP Development Build player, Burst on** (safety checks on), world `Test P4 IL2CPP`, LoadDistance 23 (47×47 = 2209-chunk square)                                                                                       |
| **Verdict**     | **GO (final)** — frame health confirmed on the shippable backend (peak fill frame 481.8 → 78.0 ms uncapped), plus a stronger finding the editor predicate could not see: **legacy never reaches a drained pipeline after relocation (3/3 OFF legs timed out at 300 s); budgets-ON drains fully in 15.5–132.5 s.** Rollback-flag retirement unblocked. |

> IL2CPP confirmation capture for `CHUNK_PIPELINE_P4_BACKPRESSURE_2026-07-23_BENCHMARK.md`
> (editor Mono screening, GO (screening)). Serves `Design/CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md`
> §3 recommendations 4 + 5. Same A/B lever: `enablePipelineTimeBudgets` +
> `enableGenerationPanicGate` toggled together per leg (OFF = exact legacy count caps by
> construction). Correctness guard at capture time: Validate All 335/335 across 16 suites.

## What this measures (and what it does NOT)

Same shape as the editor screening — one leg = `/teleport` to fresh, never-generated terrain, measure wall-clock **fill** — with two deliberate differences:

1. **Tail-inclusive drain predicate** (harness review round 3): fill ends only when the load square is fully populated AND generation queue + in-flight generation jobs + lighting ready-set + in-flight lighting jobs + in-flight mesh jobs + draw queue are ALL empty for 30 consecutive frames. The editor screening's predicate omitted the lighting/mesh/draw tail, which biased it toward legacy (legacy pushes exactly that work past the old predicate). **Fill values are therefore NOT comparable to the editor report's** — only within this capture.
2. **Metrics arm at arrival** (departure-detected), so the teleport-hold frames are excluded.

Scale is also different: LoadDistance 23 → 2209 chunks/leg (editor: 13 → 729), per-frame cap anchors light 17 / mesh 10 (this machine's tuned settings), ms ceilings at the shipping defaults 8/6/6/4/2, gate 256/128, in-flight light cap 64.

**NOT measured:** per-pass µs attribution, allocations, editor Mono anything. **Caveat on the OFF legs:** the harness logs only the composite fill end, so a timed-out leg does not reveal *which*
term (s) stayed non-zero, nor whether the load square itself ever finished populating — the log forensics below constrain this but do not fully decompose it.

## Methodology

- Single build, single session, 6 legs run back-to-back by the TEMP harness (`Assets/Scripts/Benchmarks/P4BackpressureBenchmark.cs`, F10): ON/OFF × uncapped/15/5 FPS (`vSyncCount = 0` + `Application.targetFrameRate`), salted never-visited ring destinations
  ~30 000+ voxels apart, flags and frame-rate settings restored after the run.
- 300 s timeout per leg; a timed-out leg reports its state at timeout and is marked `TIMED OUT`.
- One run per leg (macro-scenario); `hitches50` counts frames > 50 ms — **cap-implied at the 15/5 conditions** (every frame exceeds 50 ms by construction), meaningful only uncapped.
- Machine: i9-9900K (16 threads), 64 GB, D3D11, Windows 10 19045, Unity 6000.5.4f1. Raw harness log: `%USERPROFILE%/AppData/LocalLow/johanaxel007/Minecraft Clone/Benchmarks/P4BackpressureAB_2026-07-23_18-28-15.log`.

## Result — IL2CPP player, 2209-chunk square per leg, 300 s timeout

| Leg | Budgets+gate | FPS condition | Fill (s)             | Frames | Avg FPS  | Peak frame (ms) | Hitch frames >50 ms | Gate closes |
|-----|--------------|---------------|----------------------|--------|----------|-----------------|---------------------|-------------|
| L1  | **ON**       | uncapped      | **15.49**            | 1246   | **80.4** | **78.0**        | **28 (2.2%)**       | 10          |
| L2  | **ON**       | 15 cap        | **38.29**            | 574    | 15.0     | 242.8           | (cap-implied)       | 8           |
| L5  | **ON**       | 5 cap         | **132.50**           | 663    | 5.0      | 209.0           | (cap-implied)       | 7           |
| L3  | OFF          | uncapped      | **>300 (TIMED OUT)** | 54 854 | 183.0    | 481.8           | 10 (see #2)         | 0 (off)     |
| L4  | OFF          | 15 cap        | **>300 (TIMED OUT)** | 4 488  | 15.0     | 253.0           | (cap-implied)       | 0 (off)     |
| L6  | OFF          | 5 cap         | **>300 (TIMED OUT)** | 1 499  | 5.0      | 200.8           | (cap-implied)       | 0 (off)     |

The harness log's auto-derived ratio rows (×0.05 fill cost etc.) are artifacts of the timeout floor — a leg that never finished has no fill time to ratio. Disregard them; the analysis below replaces them.

## Analysis

1. **The headline inverted from the screening.** The editor screening framed budgets as "×1.69 slower fill for transformed frame health". Under the honest tail-inclusive predicate on the shippable backend, there is no fill-latency cost to weigh: **every OFF leg failed to reach a drained pipeline within 300 s at any FPS condition, while every ON leg drained fully.** The screening's "legacy fills faster" was an artifact of its predicate ending at populated+gen-drained — legacy looked done while holding an unfinished lighting/mesh tail.
2. **Why legacy never drains (log forensics).** L3's shape gives it away: 54 854 frames at 183 FPS with only 10 hitches — the world was visibly settled and idling almost the whole 300 s, yet the pipeline never presented 30 consecutive drained frames. The Player.log shows `[LIGHTING RESCUE]`
   (orphaned-column persistence inside `UnloadChunks` teardown) trickling at ~0.35–0.55/s through the *entire* 300 s of every OFF leg — vs ~4–6/s bursts that finish within the fill window on ON legs. Mechanism: the legacy fill leaves a large unfinished lighting backlog across the previous leg's area; after relocation those chunks are tens of thousands of voxels out of range but still loaded, the ~1 s fail-safe scan keeps re-promoting them (the scan has no range check), lighting jobs keep scheduling and completing for a dead area, and their pending
   flags/running jobs make
   `UnloadChunks` defer teardown — a self-sustaining ~1 Hz churn that keeps the ready-set/lighting terms intermittently hot indefinitely. Budgets+gate prevent the backlog from forming during the fill, so the old area is quiet and tears down promptly. (The out-of-range work leak is the already-queued MP-arc discard item — this capture is its strongest evidence to date.)
3. **Frame health confirmed under IL2CPP.** ON/uncapped fills a 2209-chunk square in 15.5 s at 80.4 avg FPS with a 2.2% hitch rate and a 78 ms worst frame. Legacy's worst frame is 481.8 ms — 6.2× worse — despite doing *less* useful work per second. The screening's frame-level story carries over to the player backend with a wider margin.
4. **§3.4 constancy in the mid band, ceilings bind deep — reproduced.** ON: an externally imposed ÷5.4 FPS drop (80.4 → 15.0) costs only ×2.47 fill (15.49 → 38.29 s) — the rate quota compensating. Deep: 15 → 5 cap (÷3) costs ×3.46 (38.29 → 132.50 s) — the ms ceilings bind before the quota, scaling reverts to ~proportional, exactly the documented limitation (frame-fraction ceilings remain the future refinement).
5. **Panic gate at 3× the screening's scale.** 7–10 close/reopen cycles per ON leg on the 2209-chunk fill, zero anomalies, and the drained end-state of every ON leg doubles as the no-permanent-holes witness at LoadDistance 23.

## Verdict details

**GO (final).** The editor screening's verdict condition — "confirm the frame-health delta on IL2CPP before retiring the rollback flags" — is met, and exceeded: the shippable backend shows legacy not merely hitchier but *non-convergent* after relocation. Ship state stays default-ON (`enablePipelineTimeBudgets`, `enableGenerationPanicGate`); **flag retirement is now unblocked**
for a future cleanup pass (TG-4 precedent: let it soak, then retire). Follow-ups this capture motivates, in priority order: (a) the MP-arc out-of-range result-discard item (root cause of the legacy churn — still latent under ON for the *waiting* set's memory, just no longer throughput-relevant), (b) frame-fraction ceilings if deep-low-FPS devices become a target. The TEMP harness is deletable now that this capture is recorded.
