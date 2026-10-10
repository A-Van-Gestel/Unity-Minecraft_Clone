---
name: perf-benchmark
description: Measure-and-verdict protocol for performance work — capture drift-corrected baselines, run the benchmark harnesses (runtime Assets/Scripts/Benchmarks for IL2CPP captures, editor Assets/Editor/Benchmarking micro A/Bs), write append-only Documentation/Performance reports with an explicit GO/NO-GO verdict, and gate shipping on frame-level wins. Use when the user asks to benchmark a change, capture a baseline, run a profile gate, re-measure after an optimization, write a benchmark report, run the benchmark unattended in a built IL2CPP / Master player, capture a Profiler or GC.Alloc profile from a player build, or asks "did it actually get faster?". The burst-optimization skill owns making code fast; this skill owns proving the change paid off.
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
generate benchmark noise for changes nobody will gate on. A correct system beats a marginally
faster broken one: on a bug fix, record "deliberately not measured, none owed" (with the reasoning
if it plausibly costs something), never a "never perf-measured" debt line.

## The harness inventory (two homes, different jobs)

| Home                          | What lives there                                                                                                            | Runs where                                                                      |
|-------------------------------|------------------------------------------------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------|
| `Assets/Scripts/Benchmarks/`  | The runtime benchmark framework (`BenchmarkController`/`BenchmarkEnvironment`/metrics + report generators) and the per-system benchmarks (`MeshGenerationBenchmark`, `LightingJobBenchmark`, `ChunkGenerationBenchmark`, `FluidTickBenchmark`, stress controllers, `WorldFrameProfiler`) | Editor play mode AND **IL2CPP Development Build players** — the only home that can produce real captures |
| `Assets/Editor/Benchmarking/` | Editor-only micro A/Bs for isolated managed-vs-Burst comparisons                                                              | Editor only — never compiled into a build                                        |

**Rule:** a benchmark that must run under IL2CPP belongs in the runtime assembly. Editor-only
harnesses can *screen* candidates but cannot produce shippable numbers.

**Arming the in-world harnesses:** `MeshGenerationBenchmark`, `LightingJobBenchmark`, and
`ChunkGenerationBenchmark` sit on `BenchmarkRunner` in `World.unity` but are **default-off** —
`MicroBenchmarkGate.IsArmed()` gates them on the Benchmark tab's "Enable Micro-Benchmarks" setting,
read once in `Start()`. Turn it on and **reload the world**, then trigger with `C` / `M` / `L`.
They are forced off under `WorldLaunchState.IsAutomatedMode`, so a route or fluid-stress capture
never sees them. An inert trigger key means the setting is off or the world predates the change —
not a broken harness.

**World seam:** runtime benchmarks own their world (inert `World`, synthetic chunks, dedicated
scene) and refuse to run against a live game world — follow `FluidTickBenchmark`'s pattern
(`CreateInertWorld` + `RegisterSyntheticChunk`) when writing a new one, so the harness controls
tick cadence with zero interference.

## Unattended runs (command line)

The full player workflow — build, launch, attach the Profiler for a GC.Alloc capture, read the result,
clean up — is [references/player-capture.md](references/player-capture.md). The options themselves:

Any player build — Master included — starts a harness from the command line and exits when it is done:

```
"Minecraft Clone.exe" -force-d3d11 -mc-run benchmark -mc-set benchmarkGenerationSpeeds=200 -mc-mute -mc-quit
```

- `-mc-run benchmark | fluidstress | startup-new | startup-existing` — the actions in
  `Scripts/Launch/LaunchActionInstaller.cs`. The two `startup-*` actions report ES-0's time-to-stable stamp
  (handoff / drained / stable) for a fresh or a saved probe world in a `StartupRun_*.log`; run `startup-new` first.
- `-mc-set field=value` (repeatable) — overrides any `Settings` field for this session only; `dev.<field>`
  targets `DevSettings` and is rejected outside development builds. The run otherwise inherits the build's
  `settings.json`, exactly like a menu launch. Overrides are never written back to the file.
- `-mc-mute` — `masterVolume=0` for the session. **An agent running a harness always passes it.** In an Editor
  Play-mode run, the same mute is `Launch.LaunchSession.MuteForSession()` through `unity command eval` once Play
  mode is on (the domain reload on entering Play mode would drop it if applied earlier).
- `-mc-quit` — exit when the run has written its report: exit code `0` = report written, `1` = run aborted or no
  report, `2` = invalid arguments (nothing ran). `Player.log` records `[Launch] Run finished: report <path>, exit code <n>`.
- Unity's own flags pass through; an unknown `-mc-` option is an error, never ignored.
- **Always wait with a timeout and kill on expiry** (e.g. PowerShell `$p.WaitForExit(ms)` then `Stop-Process`). A
  player occasionally freezes after `Application.Quit` — window frozen, process alive, `Player.log` ending at
  `CodeReloadManager destroyed`. It predates the launch arguments, shows up more after many launches without a
  reboot, and is not a defect of the change under test.

## Step 1 — Baseline BEFORE the change

Follow `Documentation/Performance/README.md` (the authoritative protocol — read it, don't trust
this summary if they diverge). The essentials: find the latest applicable baseline file;
re-run the benchmark on **pre-change code on your machine** for a drift-corrected local
baseline (the file's absolute numbers are less reliable than your local relatives); the
regression budget applies to the local delta. If no baseline exists for the system, capture one
first as its own commit.

## Step 2 — Measurement discipline

- **Measure in the Editor first.** A Master player build costs ~9 minutes and ~1 GB; reserve it for
  what the Editor cannot answer (IL2CPP/Master-only behavior, a frame-level GO/NO-GO). When a plan
  has a Master A/B gate, ask whether an editor micro-bench under `Assets/Editor/Benchmarking/`
  answers the same question.
- **Read what a benchmark executes before naming it the instrument.** Grep the harness for the
  production method under test: `ChunkGenerationBenchmark` never runs
  `WorldJobManager.ProcessGenerationJobs` (its `AvgActive` only counts), and
  `ActiveVoxelScanBenchmark`'s `T_bitmask`/`T_register` legs are managed replicas — only `T_current`
  calls production. A count column or a replica named after the method is not coverage.
- **The route's own noise is ±1–5 % per phase** (same code, two runs). A change worth <1 % of a pass
  cannot be resolved by any benchmark here: measure the work removed with deterministic counters
  (calls, iterations) and price it with an editor micro A/B. Never read a single-run swing inside
  that band as a result.
- **Check the pass's stop reasons before choosing a metric.** A `Ceiling`-bound pass works until its
  budget expires either way, so a win shows as throughput (served/s, fewer `Ceiling` stops), never
  as fewer ms. The binding limit differs by backend: the per-frame quota is device-calibrated at
  launch, so a pass `Ceiling`-bound in the Editor can be `Quota`-bound in Master IL2CPP — read the
  player's stop-reason table before targeting it.
- **Prefer A/B legs over the same build** (flag-switched: e.g. `managed` / `halo-full` /
  `halo-band`) — one build, one session, per-leg rows. Separate builds add noise and doubt. In a
  player, put the flag on `Settings` and add it to `SettingsManager.OverlayBenchmarkSettingsFromDisk`'s
  whitelist, or pass it with `-mc-set`. Benchmark mode is meant to pin every other gameplay setting to `new Settings()`
  defaults, but the pin applies only when no settings were loaded before the mode switch — and the main menu always
  loads them — so a run actually uses the settings file. A fresh player build's file holds the calibrated defaults;
  an Editor run uses `Assets/settings.json`. Read the report's configuration block rather than assuming defaults.
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
- **State the Managed Code Variant (Unity 6.6+).** It is a Player Setting (`Debug`/`Checked`/
  `Instrumented`/`Release`), settable per build profile, **defaulting to `Release`**, and it is
  independent of the Development Build checkbox. At `Release` a Development Build carries **no**
  `UNITY_INCLUDE_INSTRUMENTATION`, so URP's per-pass `ScriptableRenderPass.profilingSampler` and
  Render Graph samplers are absent, and this project's own telemetry counters (migrated onto that
  symbol) compile out. Use **`Checked`** to reproduce pre-6.6 Development Build behavior — the
  **`Windows - Profiler` build profile already pins it**, so capture from that profile — and
  record the variant in the capture header — a capture at `Release` is not comparable to a pre-6.6
  one at the render-pass level. Full matrix: `Documentation/Performance/README.md` §"Managed Code
  Variant governs instrumentation".

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
