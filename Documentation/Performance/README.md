# Performance Captures

Versioned performance numbers, captured against a specific commit on a specific machine. **Each file is a snapshot in time — none is ever updated in place, moved, or archived after capture.**

## Two kinds of file live here

| Kind | Suffix | What it is | Count |
|------|--------|------------|-------|
| **Baseline** | `*_BASELINE.md` | A "before" number for a system, captured so a later refactor can be shown not to regress it. Has a regression budget. | 5 |
| **Benchmark / A-B capture** | `*_BENCHMARK.md` | A measurement taken to answer a question — usually "is this change worth shipping?" — ending in an explicit **GO / NO-GO** verdict, or in a **regime verdict** for instrumentation captures that ship no behavior change. | 24 |

Baselines came first and the folder was originally named for them; A/B captures are now the large majority. The protocol below covers both, and the `perf-benchmark` skill owns the workflow.

## Why this folder exists

Large refactors of performance-sensitive systems (meshing, lighting, chunk generation, fluid simulation) need a comparable "before" number to verify they don't regress. Without a stored baseline, regressions are easy to miss and impossible to quantify after the fact.

A capture records:

- **Build context** — Unity version, scripting backend (Mono vs IL2CPP), **Development vs non-Development**, the **IL2CPP compiler configuration** (Debug/Release/Master — an axis independent of the Development flag), Burst AOT settings, source commit/branch, target platform.
- **Hardware context** — CPU model and core count, RAM, OS. (Same machine should produce comparable numbers across captures.)
- **The numbers** — raw output of the relevant in-engine benchmark, per scenario, **in full**.
- **The regression budget** (baselines) or **the verdict** (A/B captures) — see below.

## Naming convention

- `PHASE_NN_BASELINE.md` — baseline tied to a phase in a design doc.
- `<SYSTEM>_<DATE>_BASELINE.md` — ad-hoc baseline.
- `<SYSTEM>_<ID>_<DATE>_BENCHMARK.md` — A/B capture or instrumentation capture, where `<ID>` is the work item (`FP10`, `LI2`, `P4_CEILING_SCALING`, `TG4_PHASE4B_YBAND_AB`).

Add `_IL2CPP` when the capture is on the shippable backend, and `_INGAME` when it measures a live world rather than a harness scenario — both distinctions decide whether a number can be quoted as a shipping result.

## Index

Newest first within each arc. **Superseded** means a later capture withdrew or corrected its numbers — the file stays because the successor's argument is built on it.

### Build configuration

| Capture | Date | Status |
|---------|------|--------|
| [`BUILD_LEAN_PROD_IL2CPP_2026-08-15`](BUILD_LEAN_PROD_IL2CPP_2026-08-15_BENCHMARK.md) | 2026-08-15 | **GO — on build time, not runtime.** MethodOnly stacktraces + Medium stripping + Resources cleanup: build **−49 %** (~15m → 7m42s), frame-time **neutral** (mixed-sign ~1 % deltas at n = 1), all ten FP regime verdicts identical. Reserved memory consistently down; managed heap up ~2–3 % logged as a watch item. **Its "before" leg carries the pre-stamp header bug — read the ⚠ note before comparing the two headers.** |

### Chunk pipeline — FP-\* flight-profile telemetry

| Capture | Date | Status |
|---------|------|--------|
| [`CHUNK_PIPELINE_FP10_..._2026-08-01`](CHUNK_PIPELINE_FP10_FLIGHT_PROFILE_IL2CPP_2026-08-01_BENCHMARK.md) | 2026-08-01 | **Current.** Six-point sweep (vd 5–32), first on FP-9b's derived route. Reproduces FP-8; supplies P-8's mechanism. **F2's inference is corrected by the P-8 capture** — admitted work was held down by a throughput ceiling, not by the gate's willingness to accept; and its high-vd rows are not a valid baseline for builds carrying FP-11a. Raw counts stand. |
| [`CHUNK_PIPELINE_FP8_..._2026-07-31`](CHUNK_PIPELINE_FP8_FLIGHT_PROFILE_IL2CPP_2026-07-31_BENCHMARK.md) | 2026-07-31 | **Superseded for the verdict by FP-10**, but still live as FP-10's comparison baseline. Ran the pre-FP-9b route, so its values are not continued. First Release-build capture; first under §7.1 v2. |
| [`CHUNK_PIPELINE_FP4_..._2026-07-28`](CHUNK_PIPELINE_FP4_FLIGHT_PROFILE_IL2CPP_2026-07-28_BENCHMARK.md) | 2026-07-28 | **Superseded by FP-8** — scored under §7.1 v1, which counted never-admitted requests as waste and inverted the ordering trend. Its raw counts are the input to FP-8's rescoring. |

### Chunk pipeline — P-8 admission-gate scaling

| Capture | Date | Status |
|---------|------|--------|
| [`CHUNK_PIPELINE_P8_GATE_SCALING_IL2CPP_2026-08-01`](CHUNK_PIPELINE_P8_GATE_SCALING_IL2CPP_2026-08-01_BENCHMARK.md) | 2026-08-01 | **Current, and a NO-GO.** Ten runs on one build: seven residency-scaled view distances (5–32) plus **same-build unscaled controls** at vd 8/26/32. Refutes the fix FP-8/FP-10 ranked #1 — the backlog grows to meet whatever threshold it is given, so a 4.2× threshold moved gate closure 0.1 pt at vd 32 while completions fell 16 % and loading min FPS fell ~⅓. Identifies schedule `Quota` as the binding constraint. **Also establishes that FP-10 is no longer a valid high-vd baseline for the FP-11a build** — read its §F5 before comparing anything against FP-10 at vd ≥ 20. |

### Chunk pipeline — P-4 backpressure

| Capture | Date | Status |
|---------|------|--------|
| [`CHUNK_PIPELINE_P4_CEILING_SCALING_IL2CPP_2026-07-23`](CHUNK_PIPELINE_P4_CEILING_SCALING_IL2CPP_2026-07-23_BENCHMARK.md) | 2026-07-23 | **GO (final)** — FPS-cap-proportional ceiling refinement. |
| [`CHUNK_PIPELINE_P4_BACKPRESSURE_IL2CPP_2026-07-23`](CHUNK_PIPELINE_P4_BACKPRESSURE_IL2CPP_2026-07-23_BENCHMARK.md) | 2026-07-23 | **GO (final)** — confirms the screening capture on the shippable backend. |
| [`CHUNK_PIPELINE_P4_BACKPRESSURE_2026-07-23`](CHUNK_PIPELINE_P4_BACKPRESSURE_2026-07-23_BENCHMARK.md) | 2026-07-23 | **GO (screening)** — editor Mono; superseded as a shipping result by the IL2CPP capture above, kept as the screening leg. |

### Lighting — LI-\* banded gather, P-2 storage

| Capture | Date | Status |
|---------|------|--------|
| [`LIGHTING_LI2B_INGAME_IL2CPP_2026-07-11`](LIGHTING_LI2B_INGAME_IL2CPP_2026-07-11_BENCHMARK.md) | 2026-07-11 | **GO — ships default-on** (bottom band; frame-neutral in-game, priced in). |
| [`LIGHTING_LI2B_BOTTOM_BAND_2026-07-11`](LIGHTING_LI2B_BOTTOM_BAND_2026-07-11_BENCHMARK.md) | 2026-07-11 | GO pending IL2CPP — the screening leg for the above. |
| [`LIGHTING_LI2_INGAME_IL2CPP_2026-07-11`](LIGHTING_LI2_INGAME_IL2CPP_2026-07-11_BENCHMARK.md) | 2026-07-11 | **GO — ships default-on.** Sustained in-game frame win, not merely "not slower". |
| [`LIGHTING_LI2_2026-07-11`](LIGHTING_LI2_2026-07-11_BENCHMARK.md) | 2026-07-11 | GO pending IL2CPP — the screening leg for the above. |
| [`LIGHTING_P2_PHASE1_2026_06_22`](LIGHTING_P2_PHASE1_2026_06_22_BENCHMARK.md) | 2026-06-22 | Phase 1 acceptance gate **MET** — and this is what **flips LI-1 from NO-GO to GO**. |
| [`LIGHTING_LI1_2026_06_22`](LIGHTING_LI1_2026_06_22_BENCHMARK.md) | 2026-06-21/22 | **NO-GO standalone** (gather-bound), folded into P-2 rather than dropped. Read with the file above. |
| [`LIGHTING_RGB_PHASE2_BASELINE`](LIGHTING_RGB_PHASE2_BASELINE.md) | 2026-06-06 | Baseline. |

### Behavior / fluids — TG-4

| Capture | Date | Status |
|---------|------|--------|
| [`BEHAVIOR_TG4_PHASE4B_YBAND_AB_2026-06-27`](BEHAVIOR_TG4_PHASE4B_YBAND_AB_2026-06-27_BENCHMARK.md) | 2026-06-27 | **GO** — free, byte-identical serial tick win; collapses the large-flood tail. |
| [`BEHAVIOR_TG4_PHASE4B_HALO_AB_2026-06-24`](BEHAVIOR_TG4_PHASE4B_HALO_AB_2026-06-24_BENCHMARK.md) | 2026-06-24 | **GO** — full-height halo is a net serial win, not a cost. |
| [`BEHAVIOR_TG4_FULLWORLD_FLUID_PARALLEL_2026-06-24`](BEHAVIOR_TG4_FULLWORLD_FLUID_PARALLEL_2026-06-24_BENCHMARK.md) | 2026-06-24 | P4a correct, win real but marginal (~6.6 ms off the dam-break spike). |
| [`BEHAVIOR_TG4_FULLWORLD_FLUID_2026_06_23`](BEHAVIOR_TG4_FULLWORLD_FLUID_2026_06_23_BENCHMARK.md) | 2026-06-23 | Attribution gate — mesh-rebuild dominance **refuted**; the behavior tick owns the spike. |
| [`BEHAVIOR_TG4_FLUID_TICK_2026_06_23`](BEHAVIOR_TG4_FLUID_TICK_2026_06_23_BENCHMARK.md) | 2026-06-23 | Profile gate — resolves toward TG-4's parallel direction for fluid. |
| [`SEAM_WAKE_FLUID19_2026-07-27`](SEAM_WAKE_FLUID19_2026-07-27_BENCHMARK.md) | 2026-07-27 | **GO (screening)** for the pair-walk gate; ocean seam cost recorded, **not** gated — needs an IL2CPP fill-load capture. |

### Meshing — MR-\*

| Capture | Date | Status |
|---------|------|--------|
| [`MESHING_MR6_2026_06_20_AFTER_BASELINE`](MESHING_MR6_2026_06_20_AFTER_BASELINE.md) | 2026-06-20 | After MR-6 pooling. |
| [`MESHING_MR2_2026_06_20_AFTER_BASELINE`](MESHING_MR2_2026_06_20_AFTER_BASELINE.md) | 2026-06-20 | After MR-2 vertex packing (60 B → 32 B). |
| [`MESHING_MR2_2026_06_19_BASELINE`](MESHING_MR2_2026_06_19_BASELINE.md) | 2026-06-19 | The "before" for the pair above. |

### Project-wide

| Capture | Date | Status |
|---------|------|--------|
| [`PHASE_02_BASELINE`](PHASE_02_BASELINE.md) | 2026-04-25 | Oldest capture in the folder; per-block-metadata-schemas Phase 2. |

## How to use a baseline

1. Identify the latest applicable baseline file for the system you're touching.
2. Reproduce the same hardware context as closely as possible.
3. Run the in-engine benchmark against the **pre-change** code on your machine — this gives you a "drift-corrected" baseline number. Hardware drift between machines is real; the absolute numbers in the file are less reliable than your local relative measurements.
4. Apply your change.
5. Re-run the benchmark and compare against your local baseline (not the file's). The regression budget applies to the relative delta.

## How to capture

1. Run the relevant in-engine benchmark on a clean build with no other processes contending for CPU.
2. Copy the full report output (don't summarize — include all rows so the next person can compare like-for-like).
3. Save as a new file under this folder using the naming convention above.
4. Cross-link the capture from the design doc that motivated it, **and back**, in the same commit.
5. Add a row to the index above.

## Conventions

- **Never edit a captured file in place.** If a capture turns out to be wrong, write a new one and link the two.
- **Never move, rename, or archive a capture.** This folder is a time series, not a working set — see below.
- **Always include the commit hash** the capture was taken against, so readers can `git checkout` and reproduce. Player builds now bake this in (see below), but a `-dirty` suffix means the tree carried uncommitted changes and the hash alone will **not** reproduce the binary.
- **Say which backend and configuration** produced the numbers. Editor Mono is screening-only; the shipping result is an IL2CPP build at the project's production compiler configuration (**Master**). A Development build inflates frame-time-proportional budgets and can measure a regime no player experiences.

#### Provenance is baked, not queried (from 2026-08-15)

The git commit, IL2CPP compiler configuration, and Burst AOT flags in a capture header are written
into a `BuildStamp` asset by `BuildStampBaker` at build time. None of the three is knowable from a
running player: git state is absent from a build, and no runtime managed API exposes the other two.

**Captures taken before this date carry two false header lines in player builds, and must be read
with that in mind:**

| Header line | What it printed | Why |
|---|---|---|
| `Safety checks:` | `Enabled`, always | Read `BurstCompiler.Options.EnableBurstSafetyChecks`, which Burst documents as editor-only ("Does not have an impact on player mode") and whose constructor hardcodes `true`. Player AOT code was in fact compiled with the project's setting — safety checks **off**. |
| `Configuration:` | `Release`, always | Derived from `Debug.isDebugBuild`, which only distinguishes Development from non-Development and cannot see the IL2CPP compiler configuration. Master and Release both printed `Release`. |

Those captures' **measurements remain valid** — they always ran the real production configuration;
only the header misdescribed it. Per the append-only rule the affected files are left untouched. But
a capture whose *reasoning* leaned on either line (e.g. discounting a result as pessimistic because
"safety checks were on") needs its conclusion revisited even though its numbers stand.
#### Managed Code Variant governs instrumentation (from 2026-09-01, Unity 6.6)

Unity 6.6 deprecates `DEVELOPMENT_BUILD` and moves diagnostic gating to a **Managed Code Variant**
Player Setting (`Debug` / `Checked` / `Instrumented` / `Release`), settable **per build profile**.
**It defaults to `Release`, and it is independent of the Development Build checkbox.**

This is a capture-comparability axis, not just a project setting. Measured on 6000.6.0f1:

| variant | dev build | `UNITY_ENABLE_CHECKS` | `UNITY_INCLUDE_INSTRUMENTATION` | `ENABLE_PROFILER` |
|---|---|---|---|---|
| Checked | Y / N | ✓ / ✓ | ✓ / ✓ | ✓ / ✓ |
| Instrumented | Y / N | – / – | ✓ / ✓ | ✓ / ✓ |
| **Release** (default) | **Y** / N | – / – | **–** / – | ✓ / – |

> **⚠ Unresolved conflict — do not treat the `Release`+dev row as settled.** The table above is
> measured from `CompilationPipeline.GetAssemblies(AssembliesType.Player)`. Unity's 6.6 manual
> ([`managed-code-variants.html`](https://docs.unity3d.com/6000.6/Documentation/Manual/managed-code-variants.html))
> states that "for the time being the Development Build option still defines `UNITY_ASSERTIONS`
> **and `UNITY_INCLUDE_INSTRUMENTATION`**". The measurement agrees on `UNITY_ASSERTIONS` but shows
> `UNITY_INCLUDE_INSTRUMENTATION` **absent**. The API does model a dev overlay (it adds
> `ENABLE_PROFILER` and `DEBUG`), so this is not simply the query ignoring the flag. **Only an
> actual player build settles it** — no build was made when this was written. Either way the manual
> calls the overlay "subject to change" and says to set the variant explicitly, so the guidance
> below is unaffected: state the variant, and set `Checked` for captures.

The manual also confirms the historical equivalence directly: **before 6.6, Development Build
produced "the equivalent of the Checked managed code variant"** — which is why `Checked` is the
setting that reproduces pre-6.6 capture conditions.

Unity's own packages moved onto these symbols, so **a Development Build at the default `Release`
variant may silently lose URP diagnostics that pre-6.6 Development Builds had for free** — most
importantly for this folder, Render Graph profiling samplers and URP's per-pass
`ScriptableRenderPass.profilingSampler` (both gated by `UNITY_INCLUDE_INSTRUMENTATION`). The
project's own `ProfilerMarker`s survive, because `ENABLE_PROFILER` is still defined for
`Release` + Development.

**Consequence:** a capture taken on 6.6+ at the `Release` variant is **not** comparable to any
pre-6.6 capture in this folder at the render-pass level — the earlier ones carry per-pass GPU/CPU
breakdowns the newer one cannot produce. Engine-side markers still compare.

**Therefore, from this date every capture header must state the Managed Code Variant**, alongside
backend and IL2CPP configuration. To reproduce pre-6.6 Development Build behavior, set the variant
to **`Checked`** — preferably as a per-profile override on a dedicated capture build profile rather
than by flipping the global setting, since production Master builds must stay on `Release`.

This project's own diagnostics were migrated off `DEVELOPMENT_BUILD` on the same date: assertions
to `UNITY_ENABLE_CHECKS`, telemetry/counters to `UNITY_INCLUDE_INSTRUMENTATION`. So the engine
counters a capture reads are themselves variant-gated now.

- Captures from before a system's API stabilized are often not directly comparable to later ones. Note this explicitly in the file.

### Why superseded captures stay here

A superseded capture is not stale documentation — it is the **evidence its successor argues from**, and removing it silently downgrades the successor from verifiable to assertable. The FP-\* chain is the clearest case: FP-8's headline is *"here are FP-4's raw counts rescored under a corrected rule"* and prints both columns; FP-10's is *"FP-8's curve reproduces on a rebuilt route"* and cites FP-8's five values directly. Each depends on its predecessor remaining readable.

Supersession is therefore marked **in-band** — in the successor's header table, in its `Relationship to FP-N` section, and in this index — never by moving a file. `Documentation/Archived/` is for *design and backlog documents* whose work is finished (each titled `` `[ARCHIVED]` ``), which is a different thing from a measurement taken on a date.
