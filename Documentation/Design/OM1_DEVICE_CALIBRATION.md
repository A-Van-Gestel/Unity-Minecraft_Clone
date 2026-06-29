# OM-1 — Device Calibration of Throughput & Memory Budgets

> **Status: Implemented (2026-06-27); player-build verified + reference re-anchored (2026-06-28).** A
> first-launch IL2CPP run calibrated and wrote `settings.json` correctly, and `REFERENCE_*_MS` were
> re-anchored from a 99-sample player-build capture (§3.2). Promotes the backlog
> finding [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](./PERFORMANCE_IMPROVEMENTS_REPORT.md) → **OM-1** into a
> full design and records the as-built implementation. OM-1 replaces the desktop-tuned absolute constants
> that gate engine throughput and native memory retention with values **calibrated once on first launch**
> and written to `settings.json`, where they remain fully user-editable.
>
> **As-built deltas from the original design** (see §3.2, §5): throughput uses a **reference-anchored**
> scaling model (not an absolute time-slice — the time-slice regressed the desktop); the `IsolatedJobProbe`
> extraction is **mesh-only** (the lighting leg is self-contained to avoid refactoring a lighting-suite
> guard). The calibrated knobs persist as `Settings` fields: `maxMeshRebuildsPerFrame`,
> `maxLightJobsPerFrame`, `maxInFlightMeshJobs`, `chunkJobArrayPoolRetention`, `calibrationVersion`.
> Desktop sanity verified: an i9-9900K / 64 GB box resolves in a **player build** to exactly the
> historical 10 / 32 / 20 / 512.
>
> This document also specifies two enabling refactors required by the calibrator and worth doing on
> their own merits:
> - **A.** `ResourceLoader.LoadBlockDatabase()` — a static block-database loader.
> - **B.** A shared **runtime** `JobDataManagerFactory` that collapses the duplicated job-data flatten
    > logic in `World.PrepareGlobalJobData` and `EditorJobDataManagerFactory`.
>
> A third, larger structural cleanup it unblocks — **C. Fully decoupling `World.blockDatabase` from the
> `World` instance** — is intentionally **out of scope** here and specified separately in
> [`BLOCK_DATABASE_DECOUPLING.md`](../Architecture/BLOCK_DATABASE_DECOUPLING.md).

**Relationship to other documents:**

- [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](./PERFORMANCE_IMPROVEMENTS_REPORT.md) — OM-1 is the master-table
  source finding. OM-1 is the *ceiling-side* complement to **P-4** (production-side backpressure) and a
  prerequisite for **OM-2** (memory-pressure response), which consumes OM-1's tier-derived budget.
- [`WORLD_SCALING_ANALYSIS.md`](./WORLD_SCALING_ANALYSIS.md) §6 — OM-1 is listed there as a prerequisite
  for height/depth scaling; budgets should be expressible as functions, not hardcoded constants, so a
  taller world can re-derive them.
- `Architecture/CHUNK_LIFECYCLE_PIPELINE.md` — owns the in-flight mesh cap and the per-frame budgets OM-1
  scales. The pipeline doc's mentions of those numbers must point at the calibration once this ships.

---

## 1. Problem — every budget is a desktop-tuned absolute constant

Every throughput and retention knob in the engine is a fixed number chosen on desktop hardware; none
consult the device. A 3–4 GB / 4-slow-core phone gets the same in-flight memory envelope as a 64 GB
desktop — and needs *lower* caps twice over: less RAM to hold the backlog **and** fewer cores to drain
it.

| Knob                             | Location                                            | Kind                          | Today                     |
|----------------------------------|-----------------------------------------------------|-------------------------------|---------------------------|
| `maxLightJobsPerFrame`           | `SettingsManager.cs:386`                            | user setting `[Range(1,128)]` | `32`                      |
| `maxMeshRebuildsPerFrame`        | `SettingsManager.cs:376`                            | user setting `[Range(1,50)]`  | `10`                      |
| In-flight mesh cap               | `World.cs:1669` (`JobManager.MeshJobs.Count < 20`)  | hardcoded literal             | `20`                      |
| `ChunkJobArrayPool` retention    | `ChunkJobArrayPool.cs:31` (`MAX_RETAINED_PER_TYPE`) | `private const`               | `512` (≈96 MB worst case) |
| Pool prune buffer / multipliers  | `ChunkPoolManager.cs:32,107,113,116`                | `private const`               | `1.25`, ×2, ×8            |
| `viewDistance` (default + range) | `SettingsManager.cs:167`                            | user setting `[Range(1,32)]`  | default `5`               |
| `maxInitialLoadRadius`           | `SettingsManager.cs:367`                            | user setting                  | `10` (secondary)          |
| `maxStructureModsPerFrame`       | `SettingsManager.cs:397`                            | user setting                  | `5000` (secondary)        |

Note the in-flight cap (`20`) and the pool retention (`512`) are already **coupled by hand** — the
`ChunkJobArrayPool` doc comment sizes `512` as `(32 lighting + 20 mesh) × 9 buffers`. Centralizing both
in one calibrated source removes that fragile manual coupling.

---

## 2. Goals & non-goals

**Goals**

- Resolve the throughput and memory budgets **once on first launch** from the actual device, write them
  into `settings.json`, and let the user tweak them afterward like any other setting.
- Reproduce **today's exact desktop values** on a high-end desktop, so desktop behavior is unchanged
  (zero-regression default).
- Keep the engine shippable on CPU-starved / low-RAM Android as the *Android-survivability wave*
  prerequisite it is listed as in the report.

**Non-goals**

- **Never clamp the maximum of any user-facing field.** Calibration sets the *initial value* a fresh
  `settings.json` is written with; every `[Range]` ceiling stays. A user who wants render distance 32 on
  a potato gets render distance 32 on a potato. We do not gate experimentation.
- Not a runtime adaptive controller. OM-1 resolves once; dynamic per-second enforcement is **P-4**, and
  reactive memory-pressure response is **OM-2**. The three compose (§7).
- No on-disk terrain format change. The only schema addition is `calibrationVersion` plus the
  now-persisted budget fields, which `JsonUtility` round-trips. Seed/Save: ✅ / ✅.

---

## 3. Model — two signals, never one

OM-1 bundles two physically different things that must not share a signal:

| Budget group                                                                                   | Protects against               | Correct signal                                                   |
|------------------------------------------------------------------------------------------------|--------------------------------|------------------------------------------------------------------|
| **Memory caps** — `ChunkJobArrayPool` retention, in-flight job caps, pool prune                | OOM kill                       | `SystemInfo.systemMemorySize` — **directly**                     |
| **Throughput budgets** — `maxLightJobsPerFrame`, `maxMeshRebuildsPerFrame`, in-flight mesh cap | frame hitches / CPU starvation | how fast the device **actually** meshes/lights — **benchmarked** |

You cannot benchmark "how much RAM is safe to pin" on first launch without allocating-until-it-hurts,
which is hostile and noisy. `systemMemorySize` is the direct, reliable signal. So **memory caps are a
continuous `f(systemMemorySize)`, not a tier label** — matching the report's literal `min(512,
f(memory))` wording.

**No platform tiers.** Classifying by `Application.platform` (`if Android → Low`) mis-handles high-end
mobile — a Snapdragon 8 Elite-class phone reports high `processorCount`/`systemMemorySize` and a
continuous `f(SystemInfo)` already classifies it correctly with no special-casing. Platform is at most a
tie-breaker, never the primary axis.

### 3.1 Memory caps (continuous, spec-derived)

```
JobArrayPoolRetention = min(512, f(systemMemorySize))   // 512 reproduces on high-RAM desktop
MaxInFlightMeshJobs   = g(JobArrayPoolRetention)         // keep retention ≈ (light+mesh)*9 coupling honest
```

`f`/`g` are documented monotonic functions with a floor (never starve the pipeline) and a ceiling at
today's constants. Exact breakpoints are tuning-only and live in one table in `DeviceCalibration`.

### 3.2 Throughput budgets (benchmarked, first launch — reference-anchored)

A first-launch micro-benchmark times the **real** mesh and lighting jobs (median ms/chunk) and maps
that to a per-frame budget by **anchoring against a reference device**:

```
maxMeshRebuildsPerFrame = clamp( round(DEFAULT_MESH_BUDGET  * REFERENCE_MESH_MS  / medianMeshMs),  FLOOR, fieldRangeMax )
maxLightJobsPerFrame    = clamp( round(DEFAULT_LIGHT_BUDGET * REFERENCE_LIGHT_MS / medianLightMs), FLOOR, fieldRangeMax )
```

- A device whose per-chunk time **equals the reference reproduces today's default budget exactly**
  (`DEFAULT_MESH_BUDGET = 10`, `DEFAULT_LIGHT_BUDGET = 32`); slower devices scale down, faster devices up.
- The `clamp` upper bound is the field's **own** `[Range]` max — not a new restriction (§2 non-goal).
- `REFERENCE_*_MS` are **where the irreducible hand-tuning lives** — centralized in `DeviceCalibration`.
  They were chosen over an absolute "ms-per-frame time slice" model because the historical defaults are
  *count*-based, not time-budgeted (10 mesh × ~1.2 ms ≈ 12 ms/frame is not a real per-frame slice), so a
  time-slice model regressed the desktop. Anchoring guarantees the "desktop unchanged" goal by construction.
- **Anchor values are player-build (IL2CPP) measured** on an i9-9900K (16 logical cores) / 64 GB / RTX
  4070 Ti, 99-sample median (`BASELINE_CALIBRATION`, std ≈ 0.03 ms): `REFERENCE_MESH_MS = 0.952`,
  `REFERENCE_LIGHT_MS = 0.604`. On that box this reproduces today's 10 / 32 exactly *in the player*. (The
  initial editor-measured anchor of 1.233 / 1.110 was ~1.5–1.8× generous — every device's budget was
  inflated by the editor-vs-player speed ratio; the player re-anchor removes that systematic bias.)
- This **single-anchor, inverse-proportional** model (`budget ∝ 1/medianMs`) is a v1 simplification. It
  assumes the right budget scales *linearly* with raw job throughput across the whole device spectrum —
  unverified. §3.3 plans a multi-baseline generalization that **validates or replaces** that assumption.

### 3.3 Multi-baseline interpolation (planned — v1 ships single-anchor)

The §3.2 model anchors on **one** reference device and scales the budget inversely-proportionally to
measured job time. That encodes a strong, untested assumption: **the right per-frame budget scales
linearly with raw job throughput across the entire device spectrum** — that a phone which meshes N× slower
than the desktop wants exactly `1/N` the budget. A single anchor **cannot** detect curvature, because one
point defines a *ray*, not a curve. The reference is also a ~2019 ultra-high-end desktop (i9-9900K / 64 GB)
— still high-end in 2026 — so *every* non-desktop device is a long **extrapolation from a single far-fast
endpoint**, the regime where a wrong slope hurts most.

**Plan: anchor on several real, hand-validated devices and interpolate between them**, so the budget curve
is *measured* at multiple points rather than assumed linear from one.

**Per-leg baseline table.** Each baseline is a real device on which a *known-good* budget was found by
playtesting (capture `medianMs` precisely via `BASELINE_CALIBRATION`, §5):

Captured player-build medians (2026-06-28, `BASELINE_CALIBRATION` 99-sample), sorted fast → slow:

| Device (player build)                | Class               | RAM     | Median mesh / light (ms) | Std mesh / light | Known-good budget                    |
|--------------------------------------|---------------------|---------|--------------------------|------------------|--------------------------------------|
| Sony Xperia 1 VIII (Adreno 840)      | 2026 flagship phone | 11.2 GB | 0.392 / 0.302            | 0.17 / 0.16      | — (playtest)                         |
| i9-9900K / RTX 4070 Ti (**anchor**)  | 2019 high-end PC    | 64 GB   | 0.952 / 0.604            | 0.03 / 0.03      | 10 mesh / 32 light (today's default) |
| Lenovo Legion 5 (i7-10750H/RTX 2060) | 2020 perf laptop    | 64 GB   | 0.962 / 0.710            | 0.22 / 0.11      | — (playtest)                         |
| Sony Xperia 10 III (Adreno 619)      | 2021 midrange phone | 5.5 GB  | 1.905 / 1.378            | 0.39 / 0.28      | — (playtest)                         |

**Observed (2026-06-28) — what the four captures already prove, and what is still pending:**

- **"No platform tiers" is empirically validated.** The 2026 flagship phone is the *fastest* device measured
  (2.4× mesh / 2.0× light vs the desktop anchor) and resolves *above* the laptop on throughput — purely from
  `f(SystemInfo)`. A `platform==Android → low` rule would have badly mis-classified it. (§3.)
- **The two-signal split (§3) behaves as designed on real hardware.** That same flagship is *memory*-capped,
  not throughput-capped (retention 350 / in-flight 14 from 11 GB, while its throughput budgets are high at
  24 / 64); the 5.5 GB midrange phone is capped on *both* (171 / 7 and 5 / 14). Memory and throughput scale
  independently, exactly as the two-signal model intends.
- **The anchor is no longer the fast end.** Extrapolation now runs *both* directions from the reference,
  which strengthens — not weakens — the case for multi-baseline interpolation over a single-anchor ray.
- **Mobile variance is large and is the linearity risk made concrete.** Std and especially *max* on mobile
  dwarf the desktop's near-flat distribution (Xperia 10 III: std 0.39, max **5.69 ms** ≈ 3× its median; vs
  desktop std 0.03). The median is robust to these spikes, but a short probe cannot see sustained thermal
  throttling, so the median-anchored *linear* budget is likely **optimistic for mobile under load**. This
  is the strongest argument yet that linear extrapolation onto mobile needs the §3.3 playtest validation.
- **Still pending — known-good budgets.** The implied-slice check (`S_i = knownGoodBudget_i × medianMs_i`)
  cannot run until each non-anchor device is *playtested* to find its smooth budget; the table's medians are
  ready, only the known-good column is open. Until then the shipped single-anchor model stands (now
  player-anchored), and these captures are the medians half of each future baseline row.

> The **Lenovo Legion 5** capture was taken with the *pre-re-anchor* constants (1.233 / 1.110), so its logged
> 13 / 50 is stale; medians are anchor-independent and under the re-anchored model it resolves to 10 / 27.

**Linearity self-check — the diagnostic the multi-baseline buys for free.** Each baseline implies an
effective per-frame "slice" `S_i = knownGoodBudget_i × medianMs_i`. Under the single-anchor model every
`S_i` is identical by construction. Compare the captured ones:

- **`S_i` agree (within tolerance)** → the linear model is *confirmed*; keep the cheap single-anchor
  formula, now backed by evidence instead of assumption.
- **`S_i` diverge** → the curve bends (e.g. a phone needs a *smaller* slice than linear predicts, hitting
  thermal / scheduler / memory-bandwidth ceilings the desktop never reaches). Switch to **piecewise-linear
  interpolation of `budget` vs `medianMs`** across the baselines sorted by `medianMs`:
    - between two baselines → linear interpolate;
    - faster than the fastest baseline → extrapolate along the first segment, then `clamp` to the field's
      `[Range]` max;
    - slower than the slowest baseline → extrapolate along the last segment, then `clamp` to the `FLOOR`.

This preserves every §2 non-goal: the interpolated value still only **seeds** `settings.json`, and the
upper `clamp` is the field's own range max — never a new restriction.

**Scope: throughput only.** Memory caps stay the continuous `f(systemMemorySize)` of §3.1 — already
multi-point by construction (a function, not an anchor), so they need no baselines.

---

## 4. Calibration lifecycle

- Runs when `settings.json` is **missing** OR its `calibrationVersion` < the current constant. Otherwise,
  the persisted (possibly user-tweaked) values are used unchanged.
- Adds a `calibrationVersion : int` field to `Settings` so the formula can be revised and re-run on
  upgrade without stomping user edits gratuitously.
- C# field initializers (`= 32`, `= 10`, …) are **not** removed — they demote to a *safe fallback if
  calibration fails* and remain the deterministic values benchmark mode already depends on.
- **Benchmark mode is naturally exempt.** `LoadSettings()` returns from its `RuntimeMode.Benchmark`
  branch (`SettingsManager.cs:598`) before the first-launch file path (`:636`), so calibration never
  perturbs benchmark runs.
- **Recalibration:** auto-run on `calibrationVersion` bump, plus an explicit **"Recalibrate
  Performance"** action (Dev menu and/or Settings tab) that clears the calibrated fields and re-runs —
  covers hardware swaps and lets a user reset after over-tweaking.
  - *Apply semantics (as built):* the per-frame budgets and the in-flight mesh cap are re-read from
    settings each frame and take effect immediately, but `chunkJobArrayPoolRetention` is captured once at
    `ChunkJobArrayPool` construction (`WorldJobManager` init), so a changed retention **applies on the
    next world load**. The Recalibrate action should therefore live where a reload follows (main menu),
    or a live pool resize must be wired before exposing it mid-session.

---

## 5. The first-launch micro-benchmark (probe)

`StartupCalibrationProbe` — headless (not a scene `MonoBehaviour`), short, run-once:

1. **Warmup iteration (discarded)** absorbs first-run Burst compilation of the two jobs.
2. **Median over K iterations** of one representative mesh job + one lighting job on a **fixed**
   (deterministic, not random) voxel pattern.
3. Returns `medianMeshMs`, `medianLightMs`.

**Reuse — `IsolatedJobProbe` (mesh shared; lighting self-contained — as built).** The mesh leg goes
through a shared `IsolatedJobProbe.ScheduleMesh` that owns the `MeshGenerationJob` field wiring and takes
its job data **by injection** (`JobDataManager` + `FluidVertexTemplatesNativeData`), **not** via the
static `World.Instance`. `MeshGenerationBenchmark.ScheduleBenchmarkMeshing` now delegates to it (passing
`World.Instance.*`); the calibrator passes a temporary one (§6) — so the two cannot drift.

The **lighting** leg is deliberately **self-contained**: `StartupCalibrationProbe` stands up its own
minimal flat-sunlit scenario and `NeighborhoodLightingJob` wiring rather than extracting from
`LightingJobBenchmark`. The lighting job is far more coupled to that benchmark's scenario machinery
(`NeighborMapSet`, padded volumes, three `NativeQueue`s, gather sources), so a full extraction would
invasively refactor a lighting-suite regression guard. The accepted trade is a small, intentional
duplication of job-field wiring on the lighting side, in exchange for leaving that guard untouched.

> **Editor job-safety note (as built):** `MeshGenerationJob` has 9 `[ReadOnly]` light maps that the
> benchmark leaves unassigned (it runs only in IL2CPP player builds where job-safety is off). The
> calibrator must also pass *editor* job-safety, so `MeshProbeInput` carries optional light maps: the
> benchmark leaves them `default` (unchanged behavior), the calibrator supplies zero-filled maps.

**Determinism mitigations & limits.** Warmup + median + fixed pattern stabilize the result (measured
variance < 0.01 ms across runs on the reference desktop — well within the ±1 budget acceptance gate, §8).
A short first-launch run cannot capture sustained thermal throttling — accepted, and the reason
"Recalibrate" exists (`SettingsManager.RecalibrateDevice()`).

### 5.1 Precision-capture mode & on-device output (`BASELINE_CALIBRATION`)

Re-anchoring `REFERENCE_*_MS` (§3.2) and harvesting §3.3 multi-baseline rows both need a *clean*
per-device measurement. `StartupCalibrationProbe.BASELINE_CALIBRATION` (a compile-time `const bool`, off
for shipping) is the opt-in precision mode:

- Raises the iteration counts (8 warmup + 99 measured vs 2 + 7) to drive down the median's noise floor;
  the counts fold at compile time (`const ? :`), so the shipping path keeps zero runtime branch.
- Logs each leg's full distribution (`min / median / max / mean / std`) so the noise floor is *visible* —
  a tight `std` means the median is a trustworthy anchor.
- **Persists a self-contained capture file** (device specs + per-leg distribution + reference constants +
  resolved budgets) via `DeviceCalibration.WriteBaselineReport` → the shared
  `BenchmarkEnvironment.WriteReportToDisk`, so the data can be harvested off devices whose logs are
  awkward to read.

**On-device output path — platform-split, because `persistentDataPath` is app-private on Android.** On
Android, `Application.persistentDataPath` is app-private external storage: invisible to file managers, and
even a `file://` open from inside the app trips a permission/URI error (the existing
`BenchmarkResultsScreen.OnOpenFolderClicked` `Application.OpenURL("file://…")` fails there for the same
reason). The destination split lives in **`BenchmarkEnvironment.WriteReportToDisk`** (shared by every
report writer, not just calibration):

| Platform         | Capture target                                                                | How the user retrieves it            |
|------------------|-------------------------------------------------------------------------------|--------------------------------------|
| Desktop / editor | `persistentDataPath/Benchmarks/` (canonical, matches benchmarks)              | open the folder directly             |
| Android          | **public `Downloads/<product>/`** via **MediaStore** (API 29+, no permission) | Files / Downloads app, or `adb pull` |

The Android leg uses MediaStore (`ContentResolver.insert` into `MediaStore.Downloads`), which needs **no
storage permission** under scoped storage and works on the Xperia 10 III (API 30+). It falls back to
`persistentDataPath` if MediaStore fails (still `adb pull`-able), so it is never worse than today. The JNI
body (`BenchmarkEnvironment.TryWriteToAndroidDownloads`) is `#if UNITY_ANDROID`-guarded (the desktop build
can't reference `UnityEngine.AndroidJNIModule`), so the Android player build is its compile check.

> **Build dependency:** the JNI path requires the built-in **Android JNI** module
> (`com.unity.modules.androidjni`), which was added to `Packages/manifest.json` for this feature. It had
> been pruned from the lean module set; do not re-prune it while the MediaStore export exists.

> **Done (post-review #8):** the MediaStore-Downloads routing was generalized into
> `BenchmarkEnvironment.WriteReportToDisk`, so **every** benchmark report (not just calibration) now lands
> in public Downloads on Android. The **Benchmark Results Screen** "Open folder" action was likewise
> repointed to `BenchmarkEnvironment.OpenReportsLocation`, which opens the system Downloads view
> (`DownloadManager.ACTION_VIEW_DOWNLOADS`) on Android instead of the no-op `file://` URL.

---

## 6. Bootstrap — the probe runs at Main Menu, no live `World`

**Where `settings.json` is created today:** at the Main Menu. Several menu controllers call
`LoadSettings()` (`SettingsUIGenerator`, `GraphicsSettingsController`, `UIScaleController`,
`WorldSelectMenu`), so the first-launch create-defaults branch (`SettingsManager.cs:636`) already fires
there, before any world streaming.

**What the probe needs:** both jobs read `BlockTypes` (lighting *and* mesh — opacity/emission live in
`BlockTypeJobData`); mesh additionally needs `CustomMeshes/Faces/Verts/Tris` and
`Water/LavaVertexTemplates`. Today both scene benchmarks pull these from `World.Instance.JobDataManager`
/ `FluidVertexTemplates` — i.e. they require a started gameplay `World`.

**But that data is fully reachable without a `World`** — this is the decisive finding:

- `BlockDatabase` lives at `Assets/Resources/Data/BlockDatabase.asset`, Resources-loadable
  (`FluidTickBenchmark.cs:179` already does `Resources.Load<BlockDatabase>("Data/BlockDatabase")`).
- `ResourceLoader.LoadFluidTemplates()` is a static loader usable anywhere.
- `EditorJobDataManagerFactory` already proves a `JobDataManager` can be built outside a live `World`.

**Recommended sequencing (Option A — calibrate at Main Menu):**

1. First launch detected (no file / version bump).
2. `ResourceLoader.LoadBlockDatabase()` (**A**) + `ResourceLoader.LoadFluidTemplates()`.
3. `JobDataManagerFactory.Create(...)` (**B**) → temporary `JobDataManager` + `FluidVertexTemplatesNativeData`.
4. Run `StartupCalibrationProbe` (injected with the temporary data) → throughput budgets.
5. Memory caps from `SystemInfo.systemMemorySize`.
6. Write `settings.json` (+ `calibrationVersion`); **dispose the temporary native job data.**

One-time, first-launch only. **Cost to flag:** first-run Burst compilation adds latency at the menu
(the warmup eats it) — surface a one-line *"Calibrating performance…"* status so the menu never appears
hung.

**Fallback (Option B — defer to first frame).** If menu-time bootstrap proves awkward: write
spec-derived throughput defaults at Main Menu, run the probe on the first in-game frame (reusing the
already-live `World.Instance.JobDataManager`), and rewrite `settings.json` once. Memory caps — the
actual OOM guard — are spec-derived and available immediately either way, so a spec-derived throughput
value on the very first world load is an acceptable bridge. Not expected to be needed.

### 6.A `ResourceLoader.LoadBlockDatabase()`

Static loader consistent with the existing `ResourceLoader.LoadFluidTemplates()`. Canonical path
`"Data/BlockDatabase"` (already used at runtime by `FluidTickBenchmark`). Low risk, independently
useful. Note: OM-1 does **not** remove `World`'s serialized `blockDatabase` reference — that is **C**
([`BLOCK_DATABASE_DECOUPLING.md`](../Architecture/BLOCK_DATABASE_DECOUPLING.md)). C has since landed:
`World` now resolves the database via the static loader and the serialized reference is gone.

### 6.B Shared runtime `JobDataManagerFactory`

`EditorJobDataManagerFactory.Create(BlockDatabase)` is already a near-verbatim copy of
`World.PrepareGlobalJobData` (its doc says *"Mirrors the flattening logic in World.InitializeJobData()"*).
OM-1 needs a third caller (the calibrator), so unify into one **runtime** factory:

- **Assembly direction (load-bearing):** the factory must live in the runtime assembly
  (`Assembly-CSharp`) because the editor assembly may depend on runtime, never the reverse.
- `World.PrepareGlobalJobData` → delegates to the factory, then does its World-only steps.
- `EditorJobDataManagerFactory` → becomes a thin forwarder to the runtime factory (or is deleted and its
  callers repointed).
- **Reconciliation:** the only behavioral delta between the two existing copies is that World's version
  also co-builds `IsActiveById` (`bool[]`) in the same loop and logs a `>6 faces` warning, which the
  editor copy omits. The unified factory should return `IsActiveById` (editor callers ignore it) and
  keep the warning — so merging is behavior-preserving for both sites.

---

## 7. Interaction with P-4 and OM-2

- **P-4 (per-second enforcement).** OM-1's throughput fields are *budgets*; P-4 later changes the
  *enforcement unit* from per-frame to per-second. They compose: tier sets the budget, P-4 enforces it.
  Keep field names budget-oriented, not "per-frame", to ease that transition.
- **OM-2 (memory-pressure response).** OM-2's resident-chunk budget is *tier-derived from OM-1*. OM-1
  must therefore expose its memory caps as queryable values, not bury them inside the consuming systems.

---

## 8. Implementation steps

1. **A —** `ResourceLoader.LoadBlockDatabase()`.
2. **B —** runtime `JobDataManagerFactory` (single flatten copy incl. `IsActiveById` + warning);
   `World.PrepareGlobalJobData` and `EditorJobDataManagerFactory` delegate. Rebuild **both**
   `Assembly-CSharp` and `Assembly-CSharp-Editor`; confirm `World` still starts and editor tools build.
3. **`IsolatedJobProbe`** extraction from the two scene benchmarks (inject job data; scene benchmarks
   pass `World.Instance.*` and stay green).
4. **`DeviceCalibration`** (specs → memory caps) + **`StartupCalibrationProbe`** (probe → throughput) →
   `CalibrationResult`.
5. **Main-menu first-launch hook** + `calibrationVersion` + *"Calibrating…"* status.
6. **Wire consumers:** `World.cs:1669` in-flight cap → calibrated value; `ChunkJobArrayPool`
   `MAX_RETAINED_PER_TYPE` `const` → `readonly` injected at construction (`WorldJobManager.cs:99`),
   doc comment updated; settings throughput fields seeded from calibration on first launch.
7. **Verify + docs-sync** (§9). Log **C** as a follow-up in the OM-1 report entry.

`ChunkPoolManager` keys its prune targets off the (calibrated) view distance, so it scales for free; its
`1.25`/×2/×8 constants are left unless a low-RAM thin-buffer is later desired (deferred).

---

## 9. Verification

- `dotnet build "Assembly-CSharp.csproj"` after each step; editor-touching steps also build
  `Assembly-CSharp-Editor.csproj`. New `.cs` files → expect phantom `CS0103` until `AssetDatabase.Refresh()`.
- **Desktop sanity:** calibration in a **player build** on the reference desktop reproduces today's
  defaults exactly (10 / 32) and memory caps == `512` / `20`. *In-editor* the same box now resolves
  *lower* (editor Burst is ~1.5–1.8× slower than the player it is anchored to) — expected, not a regression,
  since the shipped path is the player build.
- **Forced low-spec:** debug override feeding the calibrator a high `medianMeshMs` / low
  `systemMemorySize` → confirm small budgets propagate into `World`, the pool, and `settings.json`.
  Doubles as the report's "test on a memory-capped device" stand-in without hardware.
- **Determinism:** repeat the probe N times; assert budget variance ≤ ±1. If noisy, raise K / warmup.
- **Validation suites:** `Validate Meshing` + `Validate Lighting Engine` stay green (they touch
  budgets/scheduling).
- **No save-format break** beyond the additive `calibrationVersion` + persisted budget fields.

**Docs-sync targets on ship:** promote/mark this OM-1 entry done in
`PERFORMANCE_IMPROVEMENTS_REPORT.md`; record the new files as the SoT for device-scaled budgets; update
the in-flight-cap / per-frame-budget mentions in `Architecture/CHUNK_LIFECYCLE_PIPELINE.md` to point at
the calibration.

---

## 10. Risks

| Risk                                                           | Severity | Mitigation                                                                                                           |
|----------------------------------------------------------------|----------|----------------------------------------------------------------------------------------------------------------------|
| Probe variance yields unstable budgets                         | 🟡       | Warmup + median-K + fixed pattern; ±1 acceptance gate                                                                |
| First-run Burst compile stalls the menu                        | 🟢       | One-time; "Calibrating…" status; warmup absorbs it                                                                   |
| Bootstrap ordering at Main Menu                                | 🟢       | Data is Resources-loadable & World-free (§6); Option B fallback                                                      |
| `JobDataManagerFactory` merge changes behavior                 | 🟢       | Only delta is `IsActiveById`/warning — preserved for both sites                                                      |
| Conservative caps under-use a fast device                      | 🟢       | Tunable, user-editable, "Recalibrate"; report rates risk 🟢                                                          |
| Single-anchor linear model mis-scales far-from-desktop devices | 🟡       | Multi-baseline interpolation (§3.3) validates/replaces the linearity assumption with measured laptop + phone anchors |

---

## 11. Open tuning decisions (not blockers)

- `f(systemMemorySize)` / `g(retention)` breakpoints for the memory caps.
- `TARGET_MESH_MS_PER_FRAME` / `TARGET_LIGHT_MS_PER_FRAME` slice budgets and `FLOOR`.
- Probe `K` (iterations) and warmup count.
- Calibrate-at-Main-Menu (Option A, recommended) vs defer-to-first-frame (Option B).
- **Multi-baseline throughput interpolation (§3.3):** capture laptop + Xperia 10 III baselines, compute
  each implied slice `S_i`, and decide single-anchor-linear vs piecewise-linear from whether they agree.
