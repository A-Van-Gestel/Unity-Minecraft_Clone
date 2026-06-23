# TG-4 Behavior-Tick Profile Gate — Fluid + Grass Tick Cost

| Field           | Value                                                                                                                                                                                                           |
|-----------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-06-23 (local)                                                                                                                                                                                              |
| **Branch**      | `feat/Modular-World-Generation-&-World-Types`                                                                                                                                                                   |
| **POST commit** | `db7a8f0` (Cave-Fill-Cascade + Ocean-25ch scenarios)                                                                                                                                                            |
| **Captured by** | `Assets/Scripts/Benchmarks/FluidTickBenchmark.cs` — **IL2CPP player**, 150 runs/scenario, 5 warm-up ticks                                                                                                       |
| **Cross-ref**   | Mono editor run (same harness, commit `59dcebf`, 250 runs) for backend comparison — 5 shared scenarios only                                                                                                     |
| **Verdict**     | **Profile gate resolves toward TG-4's parallel direction for fluid.** Ocean-scale tick = ~21 ms/tick, single-threaded, perfectly linear across chunks → parallelism would largely eliminate it; TG-5 would not. |

> **This is the TG-4 §5 profile-gate capture** — the data that decides full TG-4 (parallelize the behavior tick
> into per-family Burst jobs, Phases 2–4) vs the lighter TG-5 finisher, per
> [`Design/TG4_BLOCK_BEHAVIOR_DATA_SEPARATION.md`](../Design/TG4_BLOCK_BEHAVIOR_DATA_SEPARATION.md) §5. It measures
> the **isolated** behavior-tick cost — `Chunk.TickUpdate` (per-family buckets) + `World.ApplyModifications` — with
> **no rendering, meshing, or lighting**, over hand-seeded interior (Tier-1) chunks.

## What this measures (and what it does NOT)

The benchmark drives the **production** tick path over synthetic chunks: `Chunk.TickUpdate` →
`BlockBehavior.Behave`/`Active` over the real per-family `NativeHashSet` buckets, then `World.ApplyModifications`
(mod drain + six-neighbor re-activation). It is the cost TG-4 re-architects, isolated from frame noise.

**Out of scope (deliberately):** mesh rebuilds (every fluid edit dirties the section mesh), lighting updates,
cross-chunk fluid spread (scenarios are interior-only Tier-1), and render contention. The historical ocean stutter
was tick **+** mesh-rebuild **+** lighting **+** cross-chunk; this snapshot quantifies only the **tick slice** of
that frame. See *Scope boundary* below.

## Methodology

IL2CPP player build, Burst enabled + safety checks on. Each scenario runs `_benchmarkRuns` times (150 here); each
run rebuilds the chunks fresh, runs `ticks` behavior steps, and contributes **one avg-ms/tick sample**. The
ms/tick columns aggregate those per-run samples: `min` = clean uninterrupted floor (best CPU-cost proxy), `mean`
includes GC/scheduler cost, `stddev` = spread (per-voxel `List<VoxelMod>` GC), `peak` = single worst tick.
`µs/voxel = min ms/tick × 1000 ÷ peak active voxels` (the GC-spike-free per-voxel cost). `PeakActive` is the max
active-voxel count sampled across the run (fluid flow grows the set as the front spreads).

## Result — IL2CPP, ms/tick over 150 per-run samples

| Scenario          | Chunks | PeakActive |       mean |        min |     median | stddev |   peak |  µs/voxel |
|-------------------|-------:|-----------:|-----------:|-----------:|-----------:|-------:|-------:|----------:|
| Fluid-Small       |      1 |        836 |      0.850 |      0.756 |      0.839 |  0.076 |  3.131 |     0.904 |
| Fluid-Medium      |      1 |       3456 |      2.093 |      1.892 |      2.090 |  0.072 |  7.530 |     0.548 |
| Fluid-Large-4ch   |      4 |      13824 |      8.204 |      7.580 |      8.193 |  0.288 | 38.944 |     0.548 |
| Cave-Fill-Cascade |      1 |        712 |      0.908 |      0.780 |      0.891 |  0.093 |  3.401 |     1.096 |
| **Ocean-25ch**    | **25** |  **17800** | **22.502** | **21.431** | **22.516** |  0.379 | 46.136 | **1.204** |
| Grass-Field       |      1 |        144 |      0.007 |      0.006 |      0.006 |  0.000 |  0.110 |     0.044 |
| Mixed-Lake+Grass  |      1 |        944 |      0.976 |      0.857 |      0.967 |  0.066 |  2.984 |     0.908 |

## Backend comparison — Mono editor → IL2CPP player (min ms/tick, 5 shared scenarios)

| Scenario         | Mono (`59dcebf`) | IL2CPP (`db7a8f0`) | speedup |
|------------------|-----------------:|-------------------:|:-------:|
| Fluid-Small      |            1.096 |              0.756 |  1.45×  |
| Fluid-Medium     |            2.635 |              1.892 |  1.39×  |
| Fluid-Large-4ch  |           10.528 |              7.580 |  1.39×  |
| Grass-Field      |            0.012 |              0.006 |  2.0×   |
| Mixed-Lake+Grass |            1.268 |              0.857 |  1.48×  |

*(Cave-Fill-Cascade + Ocean-25ch were added after the Mono run, so they have no Mono PRE column.)*

## Reading the result

- **IL2CPP is only ~1.4× faster than Mono on fluid (2× on grass).** Modest, because the tick is dominated by
  `NativeHashSet` traversal + per-voxel `ChunkData`/neighbor lookups, not arithmetic — so IL2CPP's codegen gains
  are limited. *(This revises the earlier 2–4× hand-wave: the tick path does not get a big IL2CPP win.)*
- **Per-voxel cost converges to 0.548 µs/voxel** on the sustained source-boxes (Fluid-Medium = Fluid-Large-4ch
  exactly), and the workload is **perfectly linear across chunks**: Large-4ch min `7.580` = 4 × Medium `1.892`;
  Ocean PeakActive `17800` = 25 × Cave-Fill `712`. The tick is **embarrassingly parallel across chunks** — the
  exact premise TG-4 Phase 2's per-chunk parallel schedule relies on.
- **Grass is negligible (0.044 µs/voxel, ~12× cheaper than fluid; 0.006 ms/tick for a full field).** Grass is
  never worth jobifying — it stays managed/main-thread regardless of the fork.
- **GC is a ~10 % effect in IL2CPP, not the dominant cost.** mean is only ~11 % over min (Medium `2.093` vs
  `1.892`), stddev is tiny, and `peak` spikes are small (`7.5` ms vs Mono's `34` ms). The per-voxel
  `List<VoxelMod>` allocation is real but the IL2CPP allocator absorbs it well — so GC-elimination is a
  *secondary* benefit of Burst, not the headline. **Parallelism is the prize.**
- **The realistic cascade costs ~2× per active voxel vs the sustained box** (Cave-Fill `1.096` / Ocean `1.204`
  µs/voxel vs box `0.548`): a flooding front emits more flow mods + neighbor re-activations per active cell than a
  settled source re-confirming itself. So the synthetic boxes *understate* realistic per-voxel cost — but the
  cascade has fewer simultaneously-active cells. The ocean's danger is **chunk count × moderate per-chunk active**,
  not density.
- **Slight superlinearity at 25 chunks:** per-chunk min `0.857` (Ocean ÷ 25) vs single-chunk `0.780`
  (Cave-Fill) — ~10 %. Likely the shared `World.ApplyModifications` drain (one queue across all chunks, with
  per-mod `GetChunkCoordFromVector3` dictionary work) not scaling perfectly. Minor.

## The historical ocean stutter — quantified

**Ocean-25ch (25 chunks = render distance 5, actively flooding) costs ~21.4 ms/tick on the main thread, IL2CPP,
on an i9-9900K.** That is **>1 full frame at 60 fps (16.7 ms)** — for the behavior tick *alone*, before any mesh
rebuild, lighting, or cross-chunk work. This directly reproduces and explains the historical "underwater caves
filled by the ocean above" stutter at render distance 5: the tick is single-threaded and the active fluid set at
ocean scale simply exceeds the frame budget. On a weaker CPU it is proportionally worse, and the real frame (with
mesh + lighting) was worse still.

## Verdict & TG-4 §5 decision

| Question                                                            | Answer                                                                                                                                                                                                                                    |
|---------------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Is the behavior tick a real main-thread cost at scale?              | **Yes** — ~21 ms/tick at render-distance-5 ocean, >1 frame @ 60 fps, tick-only.                                                                                                                                                           |
| Is it parallelizable?                                               | **Yes** — perfectly linear across independent chunks (the TG-4 Phase 2 premise). Projected ~21 ms serial ÷ realistic 4–6× job-system scaling ≈ **3.5–5 ms** — sub-frame.                                                                  |
| Does the fork favor full TG-4 (parallel) or TG-5 (lighter)?         | **TG-4's parallel direction, for fluid.** TG-5 leaves the ~21 ms ocean stall intact; parallelizing fluid across chunks largely removes it. **Grass stays managed** (negligible) under either.                                             |
| Is the GC-elimination half of TG-4 a strong motivation?             | **Secondary** — ~10 % in IL2CPP (was inflated to ~15–40 % under Mono). Worth having via Burst, but not the deciding factor.                                                                                                               |
| Is this enough to commit the Phase 3 (fluid-Burst) engineering now? | **Not yet** — gate it on a **full-world stress pass** confirming the *tick* (not mesh-rebuild/lighting) dominates the real ocean frame. This snapshot proves the tick win exists; it does not attribute the rest of the historical frame. |

**Bottom line:** the profile gate resolves in favor of TG-4's parallel finisher for the **fluid** family — the
ocean-scale tick is a genuine, linearly-parallel ~21 ms main-thread stall that parallelization would move to
sub-frame and TG-5 would not. Grass is out of scope (negligible). The remaining open question before committing
the Burst-port engineering is *attribution*: confirm via the full-world stress pass that the tick, not the
mesh-rebuild it triggers, is the dominant ocean-frame cost.

## Scope boundary (what a "GO" on TG-4 does and does not fix)

TG-4 re-architects **only the tick**. Even a perfect parallel TG-4 leaves untouched: the per-fluid-edit **mesh
rebuild**, the **lighting** update water displaces, and **cross-chunk** spread (Tier-1 here). If the real ocean
frame is, say, half mesh-rebuild, parallelizing the tick halves the tick and leaves that. The
`FluidBenchmarkScenarios` were built to be reused unchanged by a **full-world fluid stress pass** (the deferred
"layered" path) that includes those costs and the cross-chunk seam — that pass is what closes the attribution gap.

## Capture environment

```
=== System ===
CPU:            Intel(R) Core(TM) i9-9900K CPU @ 3.60GHz  (16 threads)
RAM:            65 381 MB
OS:             Windows 10 (10.0.19045) 64bit
Graphics API:   Direct3D11

=== Build ===
Unity:          6000.5.0f1
Platform:       WindowsPlayer
Mode:           Player
Backend:        IL2CPP
Build GUID:     ae2b57b2de2d4ebda90c5f5af6b39f64

=== Burst ===
Compilation:    Enabled
Safety checks:  Enabled
Synchronous:    Disabled
```

150 runs per scenario, 5 warm-up ticks discarded. Tick path is **managed** (not Burst) — Burst settings affect the
surrounding engine, not the tick under measurement. Per-voxel cost is min-based (GC-spike-free).

## Cross-references

- **Design / fork:** [`Design/TG4_BLOCK_BEHAVIOR_DATA_SEPARATION.md`](../Design/TG4_BLOCK_BEHAVIOR_DATA_SEPARATION.md) §5 (TG-4-vs-TG-5 profile-gated decision).
- **Backlog:** [`Design/PERFORMANCE_IMPROVEMENTS_REPORT.md`](../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md) — TG-4.
- **Harness:** `Assets/Scripts/Benchmarks/FluidTickBenchmark.cs`, `Assets/Scripts/Benchmarks/FluidBenchmarkScenarios.cs`.
- **Folder conventions:** [`README.md`](README.md).

```
