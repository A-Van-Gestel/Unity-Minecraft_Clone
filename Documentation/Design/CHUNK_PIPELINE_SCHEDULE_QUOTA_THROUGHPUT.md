# P-9 — Schedule-Quota Throughput Ceiling

**Version:** 1.3
**Date:** 2026-08-01
**Status:** Proposed design — not implemented.
**Target:** Unity 6.5 (Mono for dev; IL2CPP for production)

> The chunk pipeline delivers a near-constant 5 658–6 803 chunks per 30 s phase from view distance 10
> to 32, in **both** legs of the P-8 A/B, while requests grow 4.4×. This document identifies the
> cause and it is not a tuning accident: **the P-4 §3.4 rate quota is an absolute per-second
> throughput ceiling — `cap × 60` items/second — whose two terms contain neither view distance nor
> frame rate, so delivered chunks/second cannot vary with view distance by construction.** On the
> capture machine that ceiling is 1 440 lighting schedules/s and 660 mesh schedules/s, against
> ~190–227 delivered chunks/s.
>
> **The single most important decision here is that P-9 does not begin by raising the caps.** The
> ceiling has two factors — the rate, and the number of quota-consuming operations spent per
> delivered chunk (currently ~6–8 lighting schedules and ~3 mesh schedules). Raising the rate buys
> throughput at direct main-thread cost, which is the trade that made P-8 a NO-GO. The preferred
> lever is instead to stop spending that multiplier *ahead of first visibility*: **deliver a chunk on
> its first viable mesh and let later lighting passes correct it in place**, rather than buying full
> correctness before the player is allowed to see anything (§6, Option B2 — an explicit product
> decision, §3.4).
>
> **Nothing here needs a new build to test.** The two caps are live settings on the Performance tab
> and settings.json is fully in effect for benchmark captures (§7.1), so the rate identity in §3.1
> can be falsified on the *existing* FP-11a build, before a line of production code is written.

**Amended:** 2026-08-01 (v1.1) — two corrections after review. (1) §7.1: the belief that a benchmark
run ignores settings.json except for five overlaid fields is **wrong on the menu-launched path**;
verified in code, which makes a cap-sweep probe (P9-0a) free and same-build. (2) §3.4/§6: the
per-chunk multiplier is reframed from "redundant work to delete" to "correctness work serialized
ahead of first delivery", on an explicit product preference — a dark or intermediately-lit mesh now,
corrected seconds later, beats looking into the void. Acceptance test restructured accordingly (§2).

**Audited:** 2026-08-01, at commit `c7bea678` (branch `feat/world-scaling`).
Findings are from static review of `World.cs:2078–2345` (the lighting ready-set scan and the mesh
schedule/apply passes), `Helpers/PipelinePassBudget.cs` (`ComputeQuota`, `ScaleCeilingMs`,
`ClassifyStop`, `Window`), `Helpers/MeshDrainPolicy.cs`, `Helpers/LightWorkScheduler.cs`,
`SettingsManager.cs` (budget fields + `OverlayBenchmarkSettingsFromDisk`),
`Config/DeviceCalibration.cs`, `Benchmarks/WorldFrameProfiler.cs`, `Benchmarks/PipelineTelemetry.cs`
and `Benchmarks/BenchmarkController.cs`. Measured values are quoted from the P-8 capture, never
from field defaults (§7). No production code was changed for this document.

**Relationship to other documents:**

- [`CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md`](CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md) — parent
  analysis; §3.4 defines the rate-quota/ms-ceiling machinery this document attacks, and §6 item 7
  is P-9's backlog entry.
- [`../Performance/CHUNK_PIPELINE_P8_GATE_SCALING_IL2CPP_2026-08-01_BENCHMARK.md`](../Performance/CHUNK_PIPELINE_P8_GATE_SCALING_IL2CPP_2026-08-01_BENCHMARK.md)
  — the capture that promoted P-9 (§F3 identifies `Quota`; §F5 invalidates FP-10 as a high-vd
  baseline).
- [`FLIGHT_PROFILE_CAPTURE.md`](FLIGHT_PROFILE_CAPTURE.md) — the instrument. §7.3 row 1 is P-9's
  ranking rationale; the attribution work in §7 below is an extension of that instrument.
- [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) — master backlog;
  P-9's row points here.
- [`../Architecture/LIGHTING_SYSTEM_OVERVIEW.md`](../Architecture/LIGHTING_SYSTEM_OVERVIEW.md) and
  [`../Architecture/SUB_CHUNK_MESHING_ARCHITECTURE.md`](../Architecture/SUB_CHUNK_MESHING_ARCHITECTURE.md)
  — the two systems whose per-item cost sets what a quota unit buys.

---

## 1. Goals & non-goals

### Goals

1. **Explain the flat completion band** — mechanically, from the code, not by correlation (§3).
2. **Establish what the quota protects before proposing to change it** — P-8's lesson, discharged
   in §4 rather than assumed.
3. **Attribute the per-frame main-thread cost of the two budgeted scheduling passes**, which no
   instrument reports today (§5's two "not measured" rows; delivered by phase P9-0 in §8).
4. **Measure the work-amplification factor** — quota units spent per delivered chunk — and decide
   from it which lever leads (§3.3, §6).
5. **Get terrain in front of the player inside the visibility budget at high view distance, with no
   loading-pass frame-time regression** — the pre-committed test in §2. Note this is a *visibility*
   goal, not a completions-per-second goal; §3.4 explains why the distinction decides the lever.

### Non-goals (v1)

- **Ordering (P-7).** Still #2, scoped to low view distance. Waste is *expected* to rise here
  whenever more work is delivered and is explicitly not scored (§2, criterion Q5) — P-7 owns it.
  A candidate mechanism for it — **predictive ordering by lead time**, of which today's nearest-first
  is the zero-speed limit — was recorded on P-7's backlog row on 2026-08-01. It is noted here only
  because §2's lead-distance identity bounds what it can deliver, and because the same
  predicted-position score could drive this document's provisional-delivery trigger (open question 0).
  Designing the two together is a reasonable future call; folding ordering into P-9's phases is not.
- **Re-testing P-8.** Deliberately blocked on this item; `scalePanicGateThresholdsWithResidency`
  stays default-OFF and untouched. It becomes worth re-testing only *after* P-9 moves the ceiling,
  at which point admission is binding again. It is **not** a P-4 flag-retirement candidate.
- **FP-11b (latency-sample cap) and the per-chunk CSV export** — §7.3 rows 11 and 13, unrelated
  instrument work.
- **Removing the quota.** A v2 wish at most: the quota is the pipeline's only steady-state
  main-thread bound (§4.2). Replacing it with something better is §8's roadmap, not v1.
- **The OM-1 calibration interaction.** The capture machine calibrates *up*; a P-9 change meeting
  scaled-*down* caps on a weak device is untested here, as it was in P-8 (its Limitation 6). Called
  out as a known limitation of any P-9 verdict, not addressed.

---

## 2. Acceptance test (pre-committed, falsifiable)

Stated before any design so the design cannot be written to fit a result. Scored per
`perf-benchmark` §7.1 v2 on a **fresh same-build A/B** (§7), IL2CPP Release, loading pass @ 200 m/s.

**The primary criterion is visibility, not completion count.** The product goal (§3.4) is that the
player stops looking into the void, so P-9 adopts **FP-4's visibility criterion** —
`latency ≤ viewDistance × 16 ÷ speed` — which §7.3 row 3 already nominates as the pipeline's
acceptance target and which P-7 also scores against. Loading @ 200 m/s, that budget and the P-8
measurement are:

| vd | Budget (`vd × 16 ÷ 200`) | P-8 measured p50 e2e (ON / OFF) | Status  |
|----|--------------------------|----------------------------------|---------|
| 10 | 800 ms                   | 797 ms / —                       | ✅ meets |
| 20 | 1 600 ms                 | 2 538 ms / —                     | ❌ 1.6×  |
| 26 | 2 080 ms                 | 3 302 ms / 3 030 ms              | ❌ 1.5×  |
| 32 | 2 560 ms                 | 3 995 ms / 3 688 ms              | ❌ 1.4×  |

**Why this criterion also bounds what P-7 can ever deliver** (and so justifies P-9's ranking above it).
Multiply both sides by speed and the criterion restates itself geometrically:

```
latency ≤ vd × 16 ÷ speed    ⟺    latency × speed  ≤  vd × 16
                                  └ lead distance ┘    └ view distance ┘
```

*The distance the player travels while waiting must be less than the distance they can see.* That is
precisely the condition under which **any ordering policy can work at all**: if the chunk the player
needs lies outside the loaded region at the moment service would have to start, no priority function
can reach it. At vd 32 / 200 m/s the lead distance is 3 995 ms × 200 m/s = **800 m** against a load
distance of 35 chunks = **560 m** — the work was never requested in time, so that regime is
throughput-bound and ordering cannot rescue it. At 50 m/s the same latency gives 200 m of lead inside
the same 560 m, which is where ordering does have headroom. This is an independent confirmation of the
§7.3 ranking (P-9 before P-7) and of P-7's scoping to the lower-speed / lower-view-distance regime.

| #       | Criterion                      | Applies to | Threshold                                                                                                            | Why this number                                                                                                       |
|---------|--------------------------------|------------|-------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------|
| **Q1**  | **Visibility budget met** ⭐     | all levers | p50 `enqueue→MeshApplied` **≤ `vd × 16 ÷ speed`** at vd 20, 26 and 32; partial credit if the shortfall ratio improves ≥ ×1.3 | The table above: currently missed by 1.4–1.6× at every high vd. Matches independent visual observation (FP-4) and P-7's target |
| **Q2**  | **Frame time holds** ⚠️         | all levers | Loading-pass **min FPS ≥ ×0.95** and **avg CPU frame time ≤ ×1.05** vs the same-build OFF leg, at vd 20, 26 **and** 32     | The arm P-8 failed (−37 % / −32 % min FPS). **A Q2 failure is a NO-GO regardless of every other criterion**               |
| **Q3a** | **Rate lever moved the ceiling** | P9-0a, P9-3 | `LightSchedule` `Quota` share falls below **90 %** of frames at vd 32 (from 99.3 % / 99.5 %), **and** completions ≥ ×1.35   | For a rate change, throughput and quota share must move together; if `Quota` stays at 99 % while the cap rose, §3.1 is wrong |
| **Q3b** | **Refine lever moved latency**   | P9-2       | Q1 improves **while** `Quota` share and completions/s may legitimately stay flat                                          | Deliver-then-refine reorders work rather than adding rate. **Scoring it on Q3a would fail a success** — see §3.4          |
| **Q4**  | **Memory holds**               | all levers | Peak total memory ≤ **×1.10**                                                                                             | P-8's G3, unchanged — more in-flight work rents more pooled buffers                                                       |
| **Q5**  | **Waste is not scored**        | all levers | Recorded, not charged                                                                                                     | Delivering more work through an unfixed ordering stage raises discard; P-7 owns it (P-8 G5 precedent)                     |
| **Q6**  | **Coverage**                   | all levers | Tour coverage ≥ 99 %, else the loading pass is partly measuring generation                                                | P-8's G4, which went marginal at vd 26/32 — carried forward as a validity gate                                            |
| **Q7**  | **Corrections still converge** | P9-2       | Every provisionally-delivered chunk reaches correct lighting; no permanently-wrong mesh, and the correction is not merely deferred | The explicit condition on the §3.4 decision: showing early must not become never fixing. Guarded by a validation baseline, not only by capture |
| **Q8**  | **Quality is not traded away** ⭐ | P9-2       | Report **% of chunks fully lit at first visibility**. Must stay ≈ 100 % at vd 10, and the provisional fraction at vd 26/32 must be *below* the fraction currently missing the budget | §3.4's preference 1. Without this the design optimises void-avoidance and quietly degrades the common case; the vd 10 arm proves the trigger is conditional, not blanket |

**Kill condition.** If phase P9-0's attribution shows the two scheduling passes already consume a
majority of the main-thread frame at vd ≥ 26, then the rate ceiling *is* the frame-time bound, every
lever in §6 that raises it is closed, and P-9 reduces to the per-item cost work (§6, Option C) or is
parked. **P9-0a/P9-0 can therefore refute this document.** That is the intended outcome of measuring
first.

---

## 3. The mechanism — why completions are flat

### 3.1 The quota is a rate, and the rate contains no view distance

`PipelinePassBudget.ComputeQuota` (`PipelinePassBudget.cs:109`) is:

```csharp
int quota = Mathf.CeilToInt(capPerFrameAt60 * unscaledDeltaTime * ReferenceFps - QUOTA_EPSILON);
return Mathf.Clamp(quota, 1, capPerFrameAt60 * MAX_QUOTA_SCALE);
```

Items served per second = `quota / dt` = `cap × 60`, **independent of frame rate** — that
invariance is the feature P-4 §3.4 shipped, and it is what stops throughput collapsing with FPS.
The consequence nobody stated is the other half of the same identity: `cap × 60` is also
**independent of view distance, residency, backlog depth, and the panic gate**. It is an absolute
items/second ceiling.

On the capture machine (values read from the P-8 report's settings block, per §7):

| Pass                    | Cap (this machine) | Rate ceiling      | Per 30 s phase |
|-------------------------|--------------------|-------------------|----------------|
| `LightSchedule`         | 24 (default 32)    | **1 440 jobs/s**  | 43 200         |
| `MeshSchedule`          | 11 (default 10)    | **660 chunks/s**  | 19 800         |

A pass reporting `Quota` is a pass that hit exactly this rate. `LightSchedule` reports `Quota` on
99.3 % (ON) / 99.5 % (OFF) of frames at vd 32, so the pipeline spends essentially every frame at
1 440 lighting schedules/s and 660 mesh schedules/s — at vd 10 and at vd 32 alike. **Delivered
chunks/second therefore cannot grow with view distance**, and the observed 5 658–6 803 band
(≈ 189–227 chunks/s) is what a fixed rate divided by a roughly fixed per-chunk cost looks like.

That is the answer to the question P-9 was opened with, and it also explains why the answer was
identical in both P-8 legs: admission changes which chunks queue up, never how fast the queue drains.

### 3.2 Two caveats on the identity

- **Clamps.** `ComputeQuota` clamps to `[1, 8 × cap]`. The upper clamp binds only when
  `dt > 8/60 s` (133 ms), where the rate degrades below `cap × 60`; the lower binds above 60 × cap
  FPS. Neither is in play in the measured band, but a hitch-heavy phase loses a little rate.
- **`CeilToInt` rounds up**, so the realised rate is marginally *above* `cap × 60` at frame rates
  that do not divide evenly. Immaterial at these magnitudes.

### 3.3 The second factor: work amplification (unmeasured)

Delivered chunks/s = rate ÷ (quota units spent per delivered chunk). Dividing the §3.1 rates by the
measured delivery rate at vd 32 gives, for the ON / OFF legs respectively:

| Quantity                                | ON (vd 32) | OFF (vd 32) |
|-----------------------------------------|------------|-------------|
| Delivered chunks/s (`MeshApplied` ÷ 30) | 190        | 227         |
| **Lighting schedules per delivered chunk** | **~7.6** | **~6.3**    |
| **Mesh schedules per delivered chunk**     | **~3.5** | **~2.9**    |

⚠️ **These are inferences, not measurements** — they assume every `Quota`-stopped frame spent its
full quota (justified at a 99.3 % `Quota` share) and that the phase ran the full 30 s. They are the
*motivation* for phase P9-0, not evidence from it. P9-0 measures both ratios directly.

If those ratios are real they are the more interesting factor — but **not because the work is
necessarily wasted**. The v1.0 draft framed amplification as redundancy to delete; §3.4 corrects
that. Known contributors visible in the code:

- **Re-scheduling.** A chunk leaves the lighting ready set on a successful schedule
  (`World.cs:2243`) but re-enters via its completion's flag callback, `PromoteNeighborhood`, and the
  ~1 s `PromoteAll` fail-safe (`World.cs:2121`). Edge checks re-flag neighbours, so one delivered
  chunk legitimately drives several lighting jobs — but nothing today distinguishes *necessary*
  re-lighting from redundant re-lighting.
- **Re-meshing.** `MeshDrainPolicy.Drain` spends a quota unit per `TrySchedule` that succeeds; a
  chunk whose neighbours light up after it meshed is re-queued and spends another. ~3 mesh builds
  per delivered chunk is a plausible ordering artefact (P-7's territory) *or* a real dependency
  requirement — and again, unmeasured.
- **Declined candidates cost no quota** (the quota increments only on success, `World.cs:2242`,
  `MeshDrainPolicy.cs:124`), so amplification is genuinely repeated *work*, not repeated *looking*.
  This is a good property and the design must preserve it.

### 3.4 The multiplier is spent *before* first visibility — and that is a product decision

The pipeline currently treats a chunk as deliverable only once its lighting has settled: the mesh
schedule declines a chunk whose neighbours are not `ReadyAndLit`, so the ~7.6 lighting schedules and
~3.5 mesh builds are spent **ahead of the player seeing anything at all**. The player's experience of
that is not "a slightly wrong chunk" — it is **void**, for the whole 4 s the budget table in §2 says
they wait at vd 32.

**Decided (product), as a strict preference order:**

1. **Best — a fully-lit chunk, delivered inside the visibility budget.** This stays the goal, and the
   pipeline should take the normal full-correctness path whenever it can meet the budget.
2. **Acceptable — a dark or intermediately-lit chunk**, corrected within seconds. Bounded,
   self-correcting.
3. **Worst — void.** A hole in the world is worse than either.

**The ordering, not merely the fallback, is the decision**, and it makes provisional delivery
**conditional rather than blanket**: a chunk is delivered provisionally only when it would otherwise
be void — i.e. when it is inside (or imminently inside) the player's view and has missed its
visibility budget. Three consequences the blanket form would have got wrong:

- **It cannot regress the low-view-distance regime.** vd 10 already meets the budget (§2), so the
  fallback never fires there, and the default view distance is untouched by construction. A blanket
  "mesh immediately with whatever light exists" would have degraded quality at *every* view distance
  to fix a problem that only exists at high ones.
- **It bounds the double-work cost.** A provisional mesh plus a corrected one is two mesh builds
  where there was one — spent against `maxMeshRebuildsPerFrame`, i.e. against one of the two
  ceilings P-9 exists to relieve. Blanket delivery would pay that on nearly every chunk and could
  *lower* the fully-correct throughput; the conditional trigger confines it to chunks that were
  going to be void anyway.
- **It keeps preference 1 measurable rather than silently traded away** — hence criterion Q8 (§2).

**The correction may not need a remesh at all.** `SectionRenderer.Layout` (`SectionRenderer.cs:46–52`)
is multi-stream, and the smooth light lives in **stream 3** (`Normal` SNorm8×4 + `TexCoord1` UNorm8×4,
8 B/vertex), written by its own `SetVertexBufferData(..., stream: 3, ...)` at `SectionRenderer.cs:165`.
Geometry depends on voxel solidity, not on light levels, so a light-only correction is structurally a
**stream-3 rewrite** — no face culling, no index buffer, no geometry pass, and plausibly no mesh-quota
unit. That would remove the double-work cost above almost entirely.

⚠️ **Verified as layout, not as capability.** What is confirmed is that the buffer separates light
from geometry and can be written independently. What is *not* confirmed: that a light-only job exists
(it does not — it would be new work), and that vertex count and ordering are stable between the two
passes. Stability holds only while no neighbouring **voxel** data arrives in between; a late neighbour
*chunk* changes boundary face culling and needs a genuine remesh. So there are two correction cases —
light settled (cheap, stream-3) and geometry changed (full remesh, unavoidable) — and P9-1 must size
them separately before P9-2 commits to the cheap path.

Two consequences that shape the whole design:

1. **Amplification stops being "redundant work to delete" and becomes "correctness work serialized
   ahead of delivery".** Most of it is probably legitimate — edge checks and neighbour re-lights are
   how the lighting model converges. The lever is therefore to **reorder** it after first delivery,
   not to remove it. That is a materially different and lower-risk change than deleting lighting
   passes, and it does not touch the lighting model's correctness.
2. **It is explicitly *not* a latency-hiding trick.** Corrections keep running at the same rate and
   must still converge (criterion Q7). Delaying or dropping the correction would hide the throughput
   problem rather than fix it, and is rejected: the point is to stop *gating visibility* on
   correctness, not to stop being correct.

⚠️ **The honest cost of this decision.** Deliver-then-refine does **not** raise the §3.1 rate ceiling.
Total quota spent per fully-correct chunk is unchanged, so completions/second may not move at all —
what moves is *when the chunk becomes visible*. That is why §2 makes the visibility criterion primary
and gives this lever its own mechanism check (Q3b): scoring it on completion counts would report a
success as a failure. If the goal were "more fully-lit chunks per second" rather than "no void",
this lever would be the wrong one and Options A/C would lead instead.

---

## 4. What the quota is protecting (discharging P-8's trap)

P-8 loosened a limit without establishing what it bounded. The answer for the quota, from the code:

### 4.1 It bounds per-item main-thread cost, and the items are expensive

Every unit of light quota buys a `JobManager.ScheduleLightingUpdate`, which performs the
neighbourhood gather this analysis's §1 identifies as the pipeline's largest main-thread cost
centre (per-job full-volume copies; ~11 pooled buffers rented per job, per the `maxInFlightLightingJobs`
docstring at `World.cs:2144`). Every unit of mesh quota buys a `TrySchedule` with its own neighbour
gather. The readiness predicates (`AreNeighborsDataReady` / `AreNeighborsReadyAndLit`) run per
*candidate*, not per scheduled item, so they are outside the quota's control entirely.

So the quota is not an arbitrary throttle — it is the knob that converts "how much lighting/meshing
work exists" into "how many milliseconds of main thread this frame spends setting it up". That is a
real protective function and Q2 exists to defend it.

### 4.2 In steady state the quota, not the ms ceiling, is the operative bound

Each budgeted pass has a second limit — a Stopwatch ms ceiling (`lightScheduleBudgetMs` 8 ms,
`meshScheduleBudgetMs` 6 ms; `ScaleCeilingMs` returns them unchanged on an uncapped machine). The
measurement says the ceiling is **not** what stops these passes:

- `ClassifyStop` receives at most one true break flag per frame (the loops `break` on the first
  limit hit — `World.cs:2173–2189`, `MeshDrainPolicy.cs:87–107`), so the reasons partition the
  frames.
- `Quota` holds 99.3 % of `LightSchedule` frames at vd 32. Therefore `CeilingExpired` accounts for
  **≤ 0.7 %** of them.

Two consequences, and they cut in opposite directions:

1. **There is headroom under the ceiling.** The pass finishes its quota well inside 8 ms. The P-8
   report's whole-frame average CPU at vd 32 loading is 8.5 ms for the entire pass group — an upper
   bound on what *both* schedule passes plus everything else consume on an average frame — so the
   8 ms and 6 ms ceilings are sized as hitch guards, an order of magnitude looser than steady-state
   usage. (Treat 8.5 ms as a group-level bound only; the 200 m/s phase alone is lighter, which is
   what P9-0 will pin down.)
2. **Which means the ceiling is not a safety net for raising the cap.** If the cap doubles, the
   quota still binds first and main-thread cost rises with it, all the way up to a 14 ms combined
   ceiling that no one has validated as a steady-state budget. Anyone raising the cap is raising
   the *only* steady-state bound the pipeline has. **This is the precise shape of the P-8 mistake,
   and it is why §6 puts cap-raising last.**

---

## 5. Current state

| Area                                | State                                                                                                                                     |
|-------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------|
| Light schedule quota                | `World.cs:2135` — `ComputeQuota(maxLightJobsPerFrame, unscaledDeltaTime)`; break at `World.cs:2173` leaves the remainder READY (§9.1 semantics) |
| Mesh schedule quota                 | `World.cs:2331` → `MeshDrainPolicy.Drain`; break at `MeshDrainPolicy.cs:87` leaves chunks queued in place                                     |
| Caps                                | `maxMeshRebuildsPerFrame` 10 / `maxLightJobsPerFrame` 32 in `SettingsManager.cs:399,409`, **overwritten at runtime by OM-1** (`DeviceCalibration`) |
| ms ceilings                         | `lightScheduleBudgetMs` 8 / `meshScheduleBudgetMs` 6 (`SettingsManager.cs:514,527`), FPS-cap-scaled only (`ScaleCeilingMs`)                   |
| In-flight bounds                    | `maxInFlightLightingJobs` 64, `maxInFlightMeshJobs` 20 — memory bounds, not throughput; `InFlightCap` dominates no phase in any P-8 run       |
| Per-pass main-thread ms             | **Not measured.** `WorldFrameProfiler` splits `World.Update` into Tick/Apply/Mesh/Light, but only `FluidStressController` enables it — the flight capture never does |
| Quota utilisation / amplification   | **Not measured.** `PipelineTelemetry` records a stop *reason* per pass per frame, never items served or work-per-delivered-chunk              |
| Benchmark A/B surface               | `OverlayBenchmarkSettingsFromDisk` (`SettingsManager.cs:1116`) copies four benchmark fields + `scalePanicGateThresholdsWithResidency` only    |

The two "not measured" rows are the whole reason P-9 opens with attribution: the instrument reports
*that* the pass stopped on quota, and nothing about what the quota bought or what it cost.

---

## 6. Decision: which lever leads

The ceiling is `rate ÷ amplification`. Three levers move it; they are not exclusive, and the
question is only which one leads.

### Option A — Raise the caps (rejected as the opening move)

- ✅ Trivially implementable, and directly moves the term §3.1 identifies as binding.
- ✅ Would be the right answer if P9-0 shows the passes are cheap and amplification is irreducible.
- ❌ **It buys throughput with main-thread milliseconds, one-for-one, and the ms ceiling does not
  catch it (§4.2).** This is P-8's trade re-run on a different knob: P-8 bought +0.2 % admitted work
  for −32 % min FPS. Doing it *before* attribution repeats the exact error this item exists because
  of.
- ❌ Interacts badly with OM-1: a cap raised on a calibrated-up desktop scales onto weak devices
  through the same calibration curve, where the frame-time cost is worst and untested.

**Deferred, not rejected outright** — it becomes Option A-prime in phase P9-3, gated on P9-0's
numbers and scored by Q2. If it ever ships it needs the ms ceilings re-tuned as *steady-state*
budgets in the same change, since they are currently sized as hitch guards.

### Option B1 — Delete redundant amplification ✅ **CHOSEN (secondary)**

Remove quota units that buy nothing: lighting schedules and re-meshes that recompute an unchanged
result. Precedent: MT-2 (`LightWorkScheduler`'s ready/waiting split) removed *declined* candidates
from the per-frame cost, MT-1 dedupes mesh re-requests at the queue head — both removed repeated
work rather than budgeting more of it, and both shipped without a frame-time cost.

- ✅ Raises delivered chunks/second **without spending a millisecond more main thread**, so Q1 and Q2
  can both pass — which is not true of Option A.
- ✅ Compounds with everything else: −20 % amplification is +20 % ceiling at *any* cap, on every
  device, including the OM-1-scaled-down ones this document otherwise cannot reason about.
- ❌ **Probably a small fraction of the multiplier.** Most re-lighting is how the lighting model
  converges, not waste. Secondary rather than leading for that reason — and entirely conditional on
  P9-0 finding genuinely redundant work.

### Option B2 — Deliver on first viable mesh, refine in place ✅ **CHOSEN (leads)**

Stop gating visibility on settled lighting **for chunks that would otherwise be void** (§3.4): the
full-correctness path stays the default, and a chunk that misses its visibility budget is delivered
with initial/partial lighting and corrected in place — ideally by a stream-3 light rewrite rather
than a second full mesh.

Leads because it attacks the criterion that actually matters. The visibility budget is currently
missed by 1.4–1.6× at vd 20/26/32 (§2), and this is the only lever that can close that gap *without*
raising the rate — the multiplier stops standing between the player and the first frame of terrain.
It costs no extra main-thread milliseconds by construction (the same work runs, in a different
order), so Q2 is structurally safe in a way Option A is not.

It is also the lever that survives the §2 kill condition: if P9-0 shows the passes are already
frame-bound, B2 is still available when A and B1 are not.

⚠️ **Two honest limits.** It does not raise the fully-correct-chunks/second ceiling (§3.4), and it is
the phase with real risk: it touches the mesh schedule's readiness contract, which is
chunk-lifecycle-invariant territory with recurring deadlock history. The `chunk-lifecycle` skill is
mandatory for P9-2, and Q7 (corrections converge; no permanently-wrong mesh) is a hard gate with a
validation baseline behind it, not a capture-only check.

### Option C — Cut per-item main-thread cost ✅ **CHOSEN (parallel, existing backlog)**

Make each quota unit cheaper, so the same rate costs less frame time — which then *permits* Option A
within Q2. This is already filed as analysis §1 (per-job full-volume copies) and §2/P-3 (the
jobified lighting merge), both deprioritised by FP-4 on the grounds that throughput was not binding.
**That deprioritisation is exactly what the P-8 capture reverses for the high-view-distance regime.**

No new design is needed here; P-9's contribution is to re-rank these, and P9-0's attribution is the
evidence that decides whether they are worth their effort. Kept out of P-9's own phases to keep the
capture readable — the same reason P-7 is out of scope.

### Option D — Move scheduling off the main thread (rejected)

- ✅ Would remove the frame-time constraint entirely and make the quota moot.
- ❌ **The gathers touch live `ChunkData` that the main thread mutates**; making them concurrent is a
  chunk-lifecycle-invariant change of a size that dwarfs P-9, against a pipeline with recurring
  deadlock history. Out of proportion to a throughput item, and P-2's persistent-storage design is
  the correct home for that discussion if it is ever had.

---

## 7. Measurement protocol

Any P-9 verdict is scored per `perf-benchmark`; the constraints below are the ones this specific
item has already paid for.

- **Fresh baseline, same build.** FP-10 is **not** a valid baseline at vd ≥ 20 for builds carrying
  FP-11a (P-8 §F5: the vd-32 unscaled control ran ×1.50 generation CPU against FP-10, while vd 8
  reproduced within 6 %). Every P-9 comparison is a **same-build ON/OFF pair**, never cross-build.
- **View distances: 10, 20, 26, 32.** vd 26 is mandatory — it was the worst point on every
  frame-cost axis in the P-8 sweep and sits between the two view distances FP-10 sampled, so a
  20 → 32 sweep understates the cost. vd 10 anchors the low end of the flat band.
- **Read the caps from the run's own settings block**, never from field defaults: OM-1 calibration
  puts this machine at `maxLightJobsPerFrame` 24 and `maxMeshRebuildsPerFrame` 11, not 32 and 10.
- **Trace-buffer saturation** hit the vd 26/32 ensure phases in P-8; latency percentiles there cover
  a subset. Since Q1 is now a **p50 latency** criterion, check the saturation flag on every scored
  phase before reading it — this is the one criterion that saturation can distort. Disposition counts
  and stop tallies stay exact.

### 7.1 How a capture actually gets its settings (verified, and it corrects a standing belief)

The received rule — "benchmark mode builds a fresh `Settings` and honours only the five fields in
`OverlayBenchmarkSettingsFromDisk`, so an unlisted knob cannot be A/B'd without a rebuild" — is
**wrong for the menu-launched path**, which is how every capture to date was run. Read
`SettingsManager.cs:877–885`:

```csharp
if (WorldLaunchState.CurrentMode == RuntimeMode.Benchmark)
{
    if (s_cachedSettings != null) return s_cachedSettings;   // ← the live path
    s_cachedSettings = new Settings();
    OverlayBenchmarkSettingsFromDisk(s_cachedSettings);
    return s_cachedSettings;
}
```

The fresh-defaults branch runs **only on a cold cache**. `UIScaleController` is a component in
`MainMenu.unity` and calls `LoadSettings()` on scene load, while `CurrentMode` is still `Default`
(`MainMenuController.cs:139` sets `Benchmark` on the button click, strictly later). The cache is
therefore warm and populated **from settings.json**, and the benchmark run inherits the entire file.
The only cache reset is `ResetStatics` at `SubsystemRegistration` — process start, not scene load.

Two independent confirmations: `BenchmarkController` never assigns `viewDistance`, yet the P-8 sweep
varied it across ten runs on **one build**; and the report's settings block prints
`maxLightJobsPerFrame` 24 / `maxMeshRebuildsPerFrame` 11, which are neither code defaults (32 / 10)
nor overlaid fields — they are OM-1 values calibrated once, persisted, and read back from disk.

**What this buys P-9:**

- **Both caps are A/B-able today, same build, no code change.** They are `[SettingField(SettingsTab.Performance)]`
  (`SettingsManager.cs:395–409`) with `[Range(1,50)]` / `[Range(1,128)]`, editable from the in-game
  Performance tab or by hand in settings.json. Nothing clamps or re-derives them at load.
- **`ApplyCalibration` will not clobber the edit.** It re-runs only when
  `calibrationVersion < DeviceCalibration.CalibrationVersion` (`SettingsManager.cs:908`), and the
  file is already stamped current. ⚠️ Two things *do* clobber it: an explicit `RecalibrateDevice()`
  from a menu, and any future bump of `CalibrationVersion`. Re-read the run's settings block rather
  than trusting the file.
- **P9-0a can therefore run on the existing FP-11a build**, which sidesteps §7's baseline problem
  entirely — it is not merely same-build A/B, it is the *same build as the FP-11a captures*.

**Where the received rule still holds:** a genuinely cold-cache benchmark launch (booting straight
into the benchmark scene, or a future headless/CLI entry point that never touches the main menu)
takes the fresh-defaults branch, where only the five overlaid fields survive. Any new **rollback
flag** should still be listed in `OverlayBenchmarkSettingsFromDisk` so it is robust on both paths —
the P-8 convention is good practice, just not the load-bearing constraint it was believed to be.
`SettingsManager.cs:1108–1115`'s remark asserts the strong form and should be corrected to the
conditional one; that is a code-comment fix, filed as a P9-0 rider rather than done here.

---

## 8. Phased implementation plan

| Phase                                     | Scope                                                                                                                                                                                                                    | Effort | Depends on |
|-------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:------:|------------|
| **P9-0a — Cap-sweep probe (zero code)** ⬅ **first** | On the **existing FP-11a build** (§7.1): sweep `maxLightJobsPerFrame` / `maxMeshRebuildsPerFrame` from the in-game Performance tab at vd 26 and 32, ≥ 3 points each (e.g. ×1 / ×2 / ×4), recording Q1–Q4. **A falsification test, not a proposed fix** — if completions and `Quota` share do not respond to the cap, §3.1's rate identity is wrong and this document is void. If they do respond, the frame-time curve prices Option A honestly and bounds every later phase. |   🟢   | —          |
| **P9-0 — Attribution instrument**         | Report, per phase: main-thread ms for each budgeted pass (light schedule, mesh schedule, distinct from the process/apply passes); items served vs quota granted; and **work amplification** — lighting schedules and mesh schedules per `MeshApplied` chunk, **split by *first* delivery vs subsequent corrections** (the split §3.4 turns on). Extends `WorldFrameProfiler` (already Stopwatch-based and IL2CPP-valid) and `PipelineTelemetry`; wire it into `BenchmarkController`, which today never enables the profiler. **No production behaviour change.** Rider: correct the overstated remark at `SettingsManager.cs:1108–1115` (§7.1). |   🟡   | P9-0a      |
| **P9-1 — Capture and decide**             | Fresh same-build baseline at vd 10/20/26/32 per §7, reported per `perf-benchmark`. Answers: how many ms do the passes cost, and what is amplification really? **Selects the lever** and may trigger §2's kill condition.                                                                                                              |   🟢   | P9-0       |
| **P9-2 — Deliver-then-refine** (Option B2) | **The lead fix.** Decouples mesh delivery from settled lighting; corrections converge in place (Q7). Behind a default-OFF rollback flag, listed in the benchmark overlay for cold-cache robustness. ⚠️ Touches the mesh readiness contract — **`chunk-lifecycle` skill mandatory**, and Q7 needs a validation baseline with a prove-red, not a capture alone. |   🔴   | P9-1       |
| **P9-2b — Delete redundancy** (Option B1) | Only what P9-1 proves is recomputing an unchanged result. Scoped after the fact; **may legitimately be empty**.                                                                                                                                                                                                                       |   🟡   | P9-1       |
| **P9-3 — Re-tune the rate** (Option A′)   | Only if P9-0a/P9-1 show genuine ms headroom under Q2. Raises the caps *and* re-tunes the ms ceilings as steady-state budgets in one change (§4.2). Scored against §2 with Q2 as a hard gate.                                                                                                                                          |   🟡   | P9-0a, P9-1 |

**P9-0a alone delivers standalone value and costs a settings edit** — it can refute this entire
document for the price of one capture session, on a build that already exists (§7.1). **P9-0 + P9-1**
are the minimal set that leaves something behind regardless of the verdict: the pipeline gains the
per-pass cost attribution it has never had, and P-9's premise is confirmed or killed on evidence.

**Validation is built alongside, not after.** P9-0's derived quantities (amplification ratios,
quota-utilisation) are pure arithmetic over recorded counters and get baselines in
`Validate Pipeline Backpressure` alongside B1–B19, with a prove-red that reddens exactly the new
guard. Any P9-2/P9-3 flag gets its flag-off byte-identity pinned the way B19 pins P-8's. `Validate
All` must be fully green — **367 baselines across 16 suites** as of `c7bea678` — with telemetry
enabled *and* disabled after each phase.

### Extension roadmap (post-P9-3, in intended order)

| Version | Extension                                                                                                                     |
|---------|---------------------------------------------------------------------------------------------------------------------------------|
| **v2**  | Replace the fixed rate with a **closed-loop budget** that targets a frame-time fraction directly, instead of a count anchored at 60 FPS. Removes the "what cap?" question entirely — but needs P9-0's attribution to exist first, and a stability analysis the §3 death-spiral history demands. |
| **v2**  | Re-test P-8 (`scalePanicGateThresholdsWithResidency` → ON) once the ceiling has moved and admission is binding again — the one thing the P-8 capture said would become worth doing. |
| **v3+** | Per-pass adaptive caps that respond to OM-1 headroom rather than a calibration constant — gets its own design doc if it becomes concrete. |

---

## 9. Constraint compliance checklist

| Project constraint                              | How this design complies                                                                                                       |
|-------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------|
| Voxels are packed `uint`s, no per-voxel objects | Untouched — P-9 operates on scheduling counters and pass budgets, never on voxel storage                                          |
| Burst jobs 100 % Burst-compatible               | No job code changes in P9-0/P9-1. P9-2's scope is main-thread scheduling bookkeeping; any job-side change re-enters via the `chunk-lifecycle` skill and Burst rules |
| No GC / LINQ in hot paths                       | P9-0 follows `WorldFrameProfiler`'s pattern: static counters, no allocation on any path, and a single bool read when disabled       |
| Pooling conventions                             | The ready-set snapshot keeps using `ListPool<Vector2Int>`; nothing in P-9 rents buffers. Q4 guards the pooled-buffer consequence of more in-flight work |
| No BinaryFormatter/JSON for terrain             | **No on-disk format change in any phase.** Settings persistence is the existing `Settings` JSON, which is config, not terrain       |
| BlockIDs constants, no raw IDs                  | No block identity involved                                                                                                        |
| Domain-reload statics (UDR0004/5)               | New static counters fold into the existing `DomainReset` of the class that owns them — no second `[RuntimeInitializeOnLoadMethod]` |

---

## 10. Open questions

0. **What triggers provisional delivery?** §3.4 settles that it is conditional, not what the
   condition is: elapsed time against the visibility budget, distance to the player, or
   predicted time-to-visible. Each has a different failure mode at high speed. P9-1's latency
   distribution picks it; naming it now would be guessing. Note P-7's recorded predicted-position
   score (`p + v × t_lead`) would answer this question and its own in one mechanism — worth
   revisiting if the two items are ever scheduled together.
1. **Is amplification real, and how much of it is pre-delivery?** §3.3's ~7.6 lighting schedules per
   delivered chunk is inferred from two ratios. P9-0 measures it *and* splits it at first delivery —
   the split, not the total, is what decides whether Option B2 has anything to reorder. If the
   multiplier lands near 1, both B options are dead and the lead passes to A′ or C.
2. ~~**How much of amplification is P-7's ordering problem wearing a different hat?**~~ **Resolved as
   a scoping decision, 2026-08-01.** It may well be partly ordering, and P9-0's split will say so —
   but it no longer changes what P-9 does. Under §3.4 the fix is to stop gating visibility on
   settled lighting, which helps whether the late neighbour arrived late for ordering reasons or
   throughput reasons. P-9 and P-7 stay independent items with a **shared acceptance target** (the
   visibility criterion, Q1); if both ship, they compound rather than overlap.
3. **What is the right steady-state ms budget for the two scheduling passes?** The current 8 + 6 ms
   are hitch guards (§4.2). Nobody has stated what fraction of a frame the pipeline *should* own,
   and Option A′ cannot be tuned without an answer.

---

## Document History

* **v1.0** - Initial design. Identifies the rate quota's `cap × 60` items/second identity as a
  view-distance-independent throughput ceiling (§3), establishes that the quota — not the ms
  ceiling — is the pipeline's operative steady-state main-thread bound (§4.2), and commits to
  attribution before any cap moves, with a pre-committed acceptance test whose frame-time arm is a
  hard gate (§2).
* **v1.1** - Two review corrections. (1) **§7.1 corrects a standing project belief**: a menu-launched
  benchmark run inherits the *whole* of settings.json via the warm settings cache, so both caps are
  A/B-able on the existing FP-11a build with no code change — the fresh-defaults + 5-field-overlay
  rule holds only on a cold cache. This adds **P9-0a**, a zero-code falsification probe, ahead of
  every other phase. (2) **§3.4 records the product decision** that a dark or intermediately-lit mesh
  now beats void, reframing the multiplier as correctness work *serialized ahead of first delivery*
  rather than redundancy; the lead lever becomes deliver-then-refine (B2), the acceptance test
  becomes visibility-primary with a per-lever mechanism check (Q3a/Q3b) and a convergence gate (Q7).
* **v1.2** - §3.4's product decision sharpened into a **strict preference order** (fully lit inside
  budget > intermediate > void), which makes provisional delivery **conditional rather than blanket**:
  it fires only for chunks that would otherwise be void, so the low-view-distance regime is untouched
  by construction and the double-mesh cost is confined to affected chunks. Adds criterion **Q8**
  (% fully lit at first visibility) so preference 1 cannot be silently traded away, and records that
  `SectionRenderer.Layout` puts smooth light in its own **stream 3** — so a light-only correction is
  structurally a stream rewrite rather than a second mesh build (verified as layout, not capability;
  sized by P9-1). New open question 0: what triggers the fallback.
* **v1.3** - §2 gains the **lead-distance identity**: the visibility criterion rearranges to
  `latency × speed ≤ vd × 16`, i.e. *the distance travelled while waiting must be less than the
  distance visible* — which is exactly the condition under which any ordering policy can work, and
  which shows vd 32 / 200 m/s (800 m lead vs 560 m load distance) is unreachable by ordering and
  therefore throughput-bound. Independent confirmation of the §7.3 ranking of P-9 above P-7. The
  predictive-ordering mechanism this was derived for is recorded on **P-7's** backlog row, not here.

---

**Last Updated:** 2026-08-01
**Next Review:** when P9-0a starts, or immediately if a capture contradicts §3.1's rate identity
