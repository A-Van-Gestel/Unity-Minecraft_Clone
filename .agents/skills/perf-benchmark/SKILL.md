---
name: perf-benchmark
description: Measure-and-verdict protocol for performance work — capture drift-corrected baselines, run the benchmark harnesses (runtime Assets/Scripts/Benchmarks for IL2CPP captures, editor Assets/Editor/Benchmarking micro A/Bs), write append-only Documentation/Performance reports with an explicit GO/NO-GO verdict, and gate shipping on frame-level wins. Use when the user asks to benchmark a change, capture a baseline, run a profile gate, re-measure after an optimization, write a benchmark report, or asks "did it actually get faster?". The burst-optimization skill owns making code fast; this skill owns proving the change paid off.
---

# Performance Benchmark & Profile-Gate Protocol

This skill owns the *measurement* side of performance work: baselines, benchmark runs, report
authoring, and the GO/NO-GO shipping verdict. `burst-optimization` owns writing the fast code;
`validation-driven-bugfix` owns proving the optimized code is *correct* (byte-identical / parity
suites) — a benchmark win means nothing until the parity guard is green.

## When to use / when to skip

Use for: any optimization with a claimed win (`LI-*`, `MR-*`, `TG-*`, `MT-*`, `OM-*`-style IDs),
any large refactor of a performance-sensitive system (meshing, lighting, generation, fluids),
or an explicit "capture a baseline" / "profile gate" request.

Skip for: micro-cleanups with no claimed perf effect, and correctness-only fixes — do not
generate benchmark noise for changes nobody will gate on.

## The harness inventory (two homes, different jobs)

| Home                          | What lives there                                                                                                            | Runs where                                                                      |
|-------------------------------|------------------------------------------------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------|
| `Assets/Scripts/Benchmarks/`  | The runtime benchmark framework (`BenchmarkController`/`BenchmarkEnvironment`/metrics + report generators) and the per-system benchmarks (`MeshGenerationBenchmark`, `LightingJobBenchmark`, `ChunkGenerationBenchmark`, `FluidTickBenchmark`, stress controllers, `WorldFrameProfiler`) | Editor play mode AND **IL2CPP Development Build players** — the only home that can produce real captures |
| `Assets/Editor/Benchmarking/` | Editor-only micro A/Bs for isolated managed-vs-Burst comparisons                                                              | Editor only — never compiled into a build                                        |

**Rule:** a benchmark that must run under IL2CPP belongs in the runtime assembly. Editor-only
harnesses can *screen* candidates but cannot produce shippable numbers.

**World seam:** runtime benchmarks own their world (inert `World`, synthetic chunks, dedicated
scene) and refuse to run against a live game world — follow `FluidTickBenchmark`'s pattern
(`CreateInertWorld` + `RegisterSyntheticChunk`) when writing a new one, so the harness controls
tick cadence with zero interference.

## Step 1 — Baseline BEFORE the change

Follow `Documentation/Performance/README.md` (the authoritative protocol — read it, don't trust
this summary if they diverge). The essentials: find the latest applicable baseline file;
re-run the benchmark on **pre-change code on your machine** for a drift-corrected local
baseline (the file's absolute numbers are less reliable than your local relatives); the
regression budget applies to the local delta. If no baseline exists for the system, capture one
first as its own commit.

## Step 2 — Measurement discipline

- **Prefer A/B legs over the same build** (flag-switched: e.g. `managed` / `halo-full` /
  `halo-band`) — one build, one session, per-leg rows. Separate builds add noise and doubt.
- **Fixed scenario set, fixed seed** — reuse the established scenarios for the system so numbers
  stay comparable across reports. Include warm-up iterations before timing.
- **Report the full distribution**: `mean`, `min` (clean floor — best CPU-cost proxy), `median`,
  `stddev` (spread), `peak` (worst single sample), plus a normalized unit (e.g. `µs/voxel`,
  `µs/chunk`) so scenarios of different sizes are comparable.
- **Editor Mono numbers are screening-only.** Allocation claims are unreliable on editor Mono
  (a zero-alloc assertion has been inconclusive there before); timing is noisier too. The
  shippable capture is an **IL2CPP Development Build, Burst on** — and the user runs player
  builds, so ask for the capture rather than simulating it.
- **Never compare across machines or backends.** Same machine, same backend, same session.

## Step 3 — Write the report

Reports live in `Documentation/Performance/`, one file per capture, **append-only**: never edit
a past report — a wrong baseline gets a new file that links the old one. Naming:
`<SYSTEM>_<ID/PHASE>_<YYYY-MM-DD>_BENCHMARK.md` for A/B captures, `PHASE_NN_BASELINE.md` /
`<SYSTEM>_<DATE>_BASELINE.md` for baselines.

Use the skeleton in [references/report-template.md](references/report-template.md). The
non-negotiable parts: the header table with **Captured / Branch / commit / Captured by
(harness + backend + run counts) / Verdict**; a "What this measures (and what it does NOT)"
section; the methodology (legs, runs, warm-ups, stat semantics); **full raw result tables**
(never summarize away rows — the next reader compares like-for-like); and cross-links to the
design doc that motivated the capture (and back from it, same commit).

## Step 4 — The verdict (profile gate)

Every report ends in an explicit bolded verdict in the header: **GO** / **NO-GO** (or GO with
scope, e.g. "GO for fluids, grass stays managed"). Rules learned the hard way:

- **Decide at the frame level, not the job level.** A large job-level win can be frame-neutral
  if the frame is bound elsewhere — name what the frame is *actually* bound by (in-game stress
  pass / `WorldFrameProfiler` attribution), and say so in the verdict ("serial tick −46% tail,
  frame-neutral in-game: flood frame is Light-bound").
- **A NO-GO is a result, not a failure** — write it up with the same rigor and record where the
  idea folds into instead (precedent: a layout win that was gather-bound standalone was NO-GO'd
  and folded into a later design). The report is what stops the idea being re-litigated later.
- **Ship default-ON with a rollback flag** when the gate passes; the flag is removed in a later
  cleanup pass once the change has soaked in-game.

## Step 5 — Integrate

- Parity/regression guards green **before** the verdict is trusted (`validation-driven-bugfix`).
- Cross-link report ↔ design doc in the same commit; status/phase updates in the design doc are
  `docs-sync`'s rules.
- Offer a single-line `Perf:`/`Docs:` commit message; never auto-commit.

## Constraints

- **Never fabricate or extrapolate numbers.** Only measured values go in reports and commit
  messages; if a number wasn't captured, say "not captured", don't estimate it.
- **Never edit a captured report/baseline in place** (append-only folder).
- **Never present editor-Mono numbers as the shipping result.**
- **No benchmark code in hot paths or builds**: editor micro-benchmarks stay under
  `Assets/Editor/`; runtime harnesses must be inert unless explicitly driven.
