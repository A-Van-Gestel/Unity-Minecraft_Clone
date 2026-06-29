# TG-4 Full-World Fluid Stress Pass — Real-Frame Tick / Mesh / Lighting Attribution

| Field               | Value                                                                                                                                                                                                                                                                                     |
|---------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**        | 2026-06-23 (local)                                                                                                                                                                                                                                                                        |
| **Branch**          | `feat/Modular-World-Generation-&-World-Types`                                                                                                                                                                                                                                             |
| **Feature commits** | `ea973b6` (WorldFrameProfiler) · `1ccc08c` (RuntimeMode.FluidStress) · `59b3b31` (stress pass) — captured atop base `5f0dfe8`                                                                                                                                                             |
| **Captured by**     | `Assets/Scripts/Benchmarks/FluidStressController.cs` — **IL2CPP player** (build GUID `54b8a668…`), 5×5 = 25-chunk region                                                                                                                                                                  |
| **Cross-ref**       | Mono editor run (same harness, same machine) for backend comparison                                                                                                                                                                                                                       |
| **Verdict**         | **Attribution gate resolved.** Mesh-rebuild does **not** dominate (refuted). The behavior **tick owns the catastrophic dam-break spike** (TG-4-justified, and GC-bound → Burst is the right tool). The **sustained** flood frame is **lighting-dominated** — which TG-4 does not address. |

> **This closes the open gate** from the isolated tick snapshot
> [`BEHAVIOR_TG4_FLUID_TICK_2026_06_23_BENCHMARK.md`](BEHAVIOR_TG4_FLUID_TICK_2026_06_23_BENCHMARK.md): that snapshot proved the
> *tick* is a real ~21 ms/tick cost in isolation but could not attribute the *real* ocean frame (tick vs the
> mesh-rebuild it triggers vs lighting). This pass measures the real, throttled, full-pipeline ocean flood and
> splits each frame across **Tick / Apply / Mesh / Light** via `WorldFrameProfiler` (Stopwatch — IL2CPP-valid,
> unlike `ProfilerRecorder`).

## What this measures

A deterministic **suspended basin** (a light-transparent `Facade` floor + a water cap, stamped throttled across a
5×5 = 25-chunk region in the sky — see `FluidBenchmarkScenarios`) is settled, **Baseline**-measured, then the water
cap is released and the cross-chunk cascade is recorded as the **Flood** phase. Every frame contributes a true
per-frame sample of the whole frame (`unscaledDeltaTime`, incl. render/GPU) and of the four main-thread
`World.Update` cost centers. The substrate stamp + settle is **before** the baseline, so the floor's one-time
relight is unmeasured. (Region scale = render-distance-5, the regime of the historical ocean stutter.)

## Result — IL2CPP player, 25-chunk flood

**Average ms/frame** (the sustained cost):

| Phase     |     Frame |  Tick | Apply |      Mesh |     Light | Min FPS | GC/frame |
|-----------|----------:|------:|------:|----------:|----------:|--------:|---------:|
| Baseline  |      1.83 | 0.018 | 0.005 |     0.045 |     0.062 |      44 |   2.7 KB |
| **Flood** | **10.44** | 0.244 | 0.089 | **1.509** | **6.925** |   **5** |  10.4 KB |

**Peak ms/frame** (the worst single frame):

| Phase     |      Frame |       Tick | Apply | Mesh | Light |
|-----------|-----------:|-----------:|------:|-----:|------:|
| Baseline  |      22.64 |      12.63 |  5.44 | 7.07 | 13.04 |
| **Flood** | **185.78** | **179.17** | 22.53 | 5.52 | 26.51 |

The avg flood frame (10.44 ms) is **~66 % Light** (6.93), ~14 % Mesh (1.51), **~2 % Tick** (0.24), ~1 % Apply, the
rest render/generation/pool. The worst flood frame (185.78 ms ≈ 5 FPS) is **~96 % Tick** (179.17) — the single
**dam-break tick** when the whole suspended cap flows at once.

## Backend comparison — Mono editor → IL2CPP player (Flood phase)

| Metric             |       Mono |     IL2CPP |  speedup  |
|--------------------|-----------:|-----------:|:---------:|
| Frame avg (ms)     |      29.04 |      10.44 |   2.78×   |
| Light avg (ms)     |      15.75 |       6.93 |   2.27×   |
| Mesh avg (ms)      |       3.69 |       1.51 |   2.44×   |
| Tick avg (ms)      |       0.87 |       0.24 |   3.62×   |
| **Tick PEAK (ms)** | **187.80** | **179.17** | **1.05×** |
| GC/frame (KB)      |       97.5 |       10.4 | 9.4× less |

## Reading the result

- **Mesh-rebuild is NOT the dominant cost — the original alternative hypothesis is refuted.** Mesh is modest:
  1.51 ms avg / 5.52 ms peak in the flood. A "GO" on TG-4 was feared to leave a mesh-rebuild bottleneck; the data
  says the mesh is not the bottleneck (the throttled section-mesh pipeline + MR-2/MR-6 work holds up).
- **The behavior TICK owns the catastrophic spike.** The dam-break tick is **179 ms — 96 % of the worst frame**
  (~5 FPS). This is the concrete "ocean stutter": one synchronous main-thread tick over the whole active fluid set.
- **That spike is GC/managed-bound, not arithmetic-bound.** IL2CPP barely moves the tick *peak* (187.8 → 179.2 ms,
  **1.05×**) while speeding everything else 2.3–3.6×, and Mono burns 97.5 KB GC/frame in the flood. The spike is
  dominated by per-voxel managed work (the `List<VoxelMod>` churn + set traversal over a huge active set), exactly
  what TG-4 Phase 2/3's Burst port (per-family `NativeList`, no per-voxel GC) eliminates. **Burst is the right tool
  for this spike** — codegen alone (IL2CPP) does not fix it.
- **But the SUSTAINED flood cost is lighting-dominated, and TG-4 does not touch lighting.** Light is **6.93 ms/frame
  (66 % of the avg frame)** — water displacing skylight across 25 flooding chunks keeps the main-thread lighting
  process/schedule busy every frame. Parallelizing the *tick* removes the dam-break spike but leaves this sustained
  ~7 ms lighting floor. Lighting — not mesh, not the steady tick — is the larger lever for *average* ocean-frame
  smoothness (ties to the LI-1 / P-2 lighting line).
- **Min FPS 5 confirms the stutter is real** at render-distance-5 ocean scale, in a release IL2CPP build, on an
  i9-9900K.

## Verdict & TG-4 §5 decision

| Question                                                              | Answer                                                                                                                                                                                                               |
|-----------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Does the **mesh-rebuild** dominate the real ocean frame?              | **No** — modest (1.5 ms avg / 5.5 ms peak). The feared mesh bottleneck is refuted.                                                                                                                                   |
| Does the **tick** dominate?                                           | **Only the worst-case spike** — the 179 ms dam-break tick is 96 % of the peak frame. On *average* the tick is ~2 % of the frame.                                                                                     |
| What dominates the **sustained** flood frame?                         | **Lighting** — 6.9 ms (66 % of the avg frame). TG-4 does not address this.                                                                                                                                           |
| Is the tick spike the right TG-4 target, and is Burst the right tool? | **Yes** — the spike is GC/managed-bound (IL2CPP 1.05× on it), so TG-4 Phase 2/3's Burst/`NativeList` port is precisely what removes it.                                                                              |
| Commit the Phase-3 fluid→Burst engineering?                           | **Yes, to kill the dam-break stutter spike** — justified and well-targeted. But **do not expect it to make ocean flooding smooth on its own**: the sustained cost is lighting, which needs its own (separate) lever. |

**Bottom line:** the gate resolves in favor of TG-4's parallel-for-fluid finisher **for the right reason** — it
removes the genuine, GC-bound, ~180 ms dam-break tick spike that IL2CPP cannot. The pass also corrects the framing:
**mesh-rebuild was never the ocean bottleneck, and the *sustained* ocean-frame cost is lighting**, so TG-4 should be
planned as "remove the fluid-tick stutter spike," not "make ocean flooding smooth" — the latter additionally needs
the lighting line (LI-1 / P-2).

## Caveats

- **Manufactured basin, not a natural ocean.** A real ocean spawn has nowhere to flow and seeds drift across
  world-gen versions, so the flood is a deterministic suspended `Facade` basin (light-transparent floor → no
  skylight-shadow oscillation; see Bug 13 in `Documentation/Bugs/LIGHTING_BUGS.md`). The water volume + cross-chunk
  spread are representative of ocean-scale flooding, but exact magnitudes are scenario-specific.
- **The harness's printed auto-verdict ("TICK dominates") compares only tick-vs-mesh *peak*** and ignores lighting
  and averages — it flags the spike but understates the lighting-dominated sustained cost. This document is the
  authoritative reading. *(Follow-up: the `FluidStressReportGenerator` verdict could weigh Light + avg-vs-peak.)*
- **Mono row is editor-mode** (includes editor overhead); the IL2CPP row is the headline. Tick path is managed
  under both — Burst settings affect the surrounding engine, not the tick under measurement.

## Capture environment

```
CPU:      Intel(R) Core(TM) i9-9900K @ 3.60GHz (16 threads)
RAM:      65 381 MB
OS:       Windows 10 (10.0.19045) 64bit
Unity:    6000.5.0f1
Headline: WindowsPlayer / IL2CPP (build GUID 54b8a668a36a4c2299dd028930dd70e2)
Cross-ref: WindowsEditor / Mono (git 5f0dfe8)
Burst:    Enabled, safety checks on, async
```

5×5 = 25 chunks (render-distance-5). Baseline 4 s, Flood ~16 s. Per-frame avg + peak are true per-frame (whole frame
from `unscaledDeltaTime`; sub-phases from `WorldFrameProfiler`).

## Cross-references

- **Isolated tick (gate opener):** [`BEHAVIOR_TG4_FLUID_TICK_2026_06_23_BENCHMARK.md`](BEHAVIOR_TG4_FLUID_TICK_2026_06_23_BENCHMARK.md).
- **Design / fork:** [`Design/TG4_BLOCK_BEHAVIOR_DATA_SEPARATION.md`](../Design/TG4_BLOCK_BEHAVIOR_DATA_SEPARATION.md) §5.
- **Backlog:** [`Design/PERFORMANCE_IMPROVEMENTS_REPORT.md`](../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md) — TG-4 (tick spike) + the lighting line (LI-1 / P-2).
- **Harness:** `Assets/Scripts/Benchmarks/FluidStressController.cs`, `WorldFrameProfiler.cs`, `FluidBenchmarkScenarios.cs`.
- **Related limitation:** `Documentation/Bugs/LIGHTING_BUGS.md` Bug 13 (opaque-slab skylight oscillation — why the floor is `Facade`).
- **Folder conventions:** [`README.md`](README.md).
