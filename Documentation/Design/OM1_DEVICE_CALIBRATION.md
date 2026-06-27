# OM-1 — Device Calibration of Throughput & Memory Budgets

> **Status: Implemented (2026-06-27) — pending in-game/player-build verification.** Promotes the backlog
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
> Desktop sanity verified: a 16-core / 64 GB box resolves to exactly the historical 10 / 32 / 20 / 512.
>
> This document also specifies two enabling refactors required by the calibrator and worth doing on
> their own merits:
> - **A.** `ResourceLoader.LoadBlockDatabase()` — a static block-database loader.
> - **B.** A shared **runtime** `JobDataManagerFactory` that collapses the duplicated job-data flatten
    > logic in `World.PrepareGlobalJobData` and `EditorJobDataManagerFactory`.
>
> A third, larger structural cleanup it unblocks — **C. Fully decoupling `World.blockDatabase` from the
> `World` instance** — is intentionally **out of scope** here and specified separately in
> [`BLOCK_DATABASE_DECOUPLING.md`](./BLOCK_DATABASE_DECOUPLING.md).

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
- **Anchor values are editor-measured** on a 16-core / 64 GB desktop (Burst on): `REFERENCE_MESH_MS ≈
  1.233`, `REFERENCE_LIGHT_MS ≈ 1.110`. Re-anchor from a player-build capture when tuning (a follow-up;
  editor Burst ≈ player Burst, so editor-anchoring is slightly generous in player — acceptable, it only
  means a fast box gets ≥ the default, never less).

---

## 4. Calibration lifecycle

- Runs when `settings.json` is **missing** OR its `calibrationVersion` < the current constant. Otherwise
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
([`BLOCK_DATABASE_DECOUPLING.md`](./BLOCK_DATABASE_DECOUPLING.md)). Until C lands, two access paths
coexist (World's serialized reference and the static loader resolve to the same shared asset).

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
- **Desktop sanity:** calibration on a desktop produces throughput budgets ≥ today's defaults (a fast box
  must not regress) and memory caps == `512` / `20` exactly.
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

| Risk                                           | Severity | Mitigation                                                      |
|------------------------------------------------|----------|-----------------------------------------------------------------|
| Probe variance yields unstable budgets         | 🟡       | Warmup + median-K + fixed pattern; ±1 acceptance gate           |
| First-run Burst compile stalls the menu        | 🟢       | One-time; "Calibrating…" status; warmup absorbs it              |
| Bootstrap ordering at Main Menu                | 🟢       | Data is Resources-loadable & World-free (§6); Option B fallback |
| `JobDataManagerFactory` merge changes behavior | 🟢       | Only delta is `IsActiveById`/warning — preserved for both sites |
| Conservative caps under-use a fast device      | 🟢       | Tunable, user-editable, "Recalibrate"; report rates risk 🟢     |

---

## 11. Open tuning decisions (not blockers)

- `f(systemMemorySize)` / `g(retention)` breakpoints for the memory caps.
- `TARGET_MESH_MS_PER_FRAME` / `TARGET_LIGHT_MS_PER_FRAME` slice budgets and `FLOOR`.
- Probe `K` (iterations) and warmup count.
- Calibrate-at-Main-Menu (Option A, recommended) vs defer-to-first-frame (Option B).
