# P-9 — Schedule-Quota Throughput Ceiling

**Version:** 1.7a
**Date:** 2026-08-02
**Status:** **Measurement complete — P9-0a, P9-0 and P9-1 are all done.** The instrument ships,
guarded by baselines B20–B22, and has been used for a five-run capture that re-ranks §6 and corrects
§3.3's inferences. **No fix has been written**; every remaining lever is proposed.
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
> **Nothing here needed a new build to test.** The two caps are live settings on the Performance tab
> and settings.json is fully in effect for benchmark captures (§7.1), so the rate identity in §3.1 was
> falsifiable on the *existing* FP-11a build before a line of production code was written — which is
> what **P9-0a** did on 2026-08-02.
>
> **⚠ P9-0a confirmed the identity and re-ranked the levers; see the v1.4 amendment below.** Raising
> the cap works and is unaffordable (×4.79 CPU, ×0.61 min FPS), and at ×2 the binding limit becomes the
> **8 ms ceiling**, not a higher quota. The lead lever is now **Option C — cut per-item main-thread
> cost**, specifically the unbudgeted lighting merge (**P-3**); B2 above keeps its product rationale
> (§3.4) but is no longer the throughput answer.
>
> **⚠⚠ P9-1 measured all of the above and re-ranked them again — read the v1.7 amendment before acting
> on §6.** The rate identity is confirmed within 4 % across vd 10→32, but two of this document's own
> inferences are wrong: **pre-delivery mesh amplification is exactly 1.00**, not ~3.5, so Option B2 has
> no pre-delivery mesh work to skip; and **the latency is 82 % admission wait**, so B2 can reach 16 % of
> a 42 % gap. B2 is **refuted as a throughput lever** and re-filed as a product item. **P-3 is demoted
> from gating to enabler** — the schedule pass is `Quota`-bound on 98 % of frames, so cheaper items buy
> frame time and not one extra chunk. The lead is now **Option B1**, the only lever that raises delivery
> at zero frame-time cost.

**Amended:** 2026-08-01 (v1.1) — two corrections after review. (1) §7.1: the belief that a benchmark
run ignores settings.json except for five overlaid fields is **wrong on the menu-launched path**;
verified in code, which makes a cap-sweep probe (P9-0a) free and same-build. (2) §3.4/§6: the
per-chunk multiplier is reframed from "redundant work to delete" to "correctness work serialized
ahead of first delivery", on an explicit product preference — a dark or intermediately-lit mesh now,
corrected seconds later, beats looking into the void. Acceptance test restructured accordingly (§2).

**Amended:** 2026-08-02 (v1.4) — **P9-0a ran, and it confirms §3.1 while closing §6's Option A′.**
Two settings-only legs at vd 32 on the P-8 build
([capture](../Performance/CHUNK_PIPELINE_P9_0A_CAP_SWEEP_IL2CPP_2026-08-02_BENCHMARK.md)):
doubling `maxLightJobsPerFrame` reopened the gate (95.1 % → 62.6 % closed), collapsed
`enqueue→populated` (2 999 → 2 134 ms) and raised completions 21 % — the exact mechanism §2's
prediction named — at a cost of **×4.79 CPU and ×0.61 minimum FPS**, failing Q2 decisively. Three
corrections follow, folded into the sections below: **(1)** §4.2's headroom is quantified as *less
than ×2* — at ×2 the binding limit becomes the **8 ms ceiling** (`Ceiling` on 95.8 % of frames), after
which the count cap is inert; **(2)** the frame cost is **not** in the schedule pass (which explains
only 6.8 ms of +23.1 ms), and a fitted model points at the unbudgeted `ProcessLightingJobs` merge —
**P-3** — making **Option C the gating lever** rather than a parallel one; **(3)** §3.4's Option B2 is
weakened as a *throughput* lever: the recoverable hops total ~537 ms of a 3 703 ms latency, so it
cannot meet the visibility budget alone. Its product rationale is untouched. Phases P9-0a/L5/L3 are
resolved or withdrawn in §8.

**Amended:** 2026-08-02 (v1.7) — **P9-1 ran, and it corrects this document as much as it confirms it**
([capture](../Performance/CHUNK_PIPELINE_P9_1_ATTRIBUTION_IL2CPP_2026-08-02_BENCHMARK.md)). Five
same-build IL2CPP runs — vd 10/20/26/32 at the OM-1 caps plus a vd-32 cap-48 A/B leg. **Confirmed:**
§3.1's identity holds within 4 % across a 3.2× view-distance range (1 435–1 496 lighting schedules/s
against a predicted 1 440) and the flat completion band reproduces on a fresh build. **Corrected:**
§F4's fitted 0.37 ms/merge parameter was the *sum of both lighting passes* — measured, a lighting job
costs **0.15 ms to schedule + 0.18 ms to merge**, and the merge is 39 % of the ×2-cap frame growth, not
the ~70 % the model implied. **Refuted:** §3.3's ~3.5 mesh schedules per delivered chunk — pre-delivery
mesh amplification is **exactly 1.00** at every view distance, and §3.3's ~7.6 lighting figure is the
*total* (6.3–7.4), of which only **3.9** is pre-delivery. **Answered:** §10 q4 — parking is 43–48 % of
the idle-pass `populated→lit` hop. §2's kill condition is **not** triggered (the two scheduling passes
are 32 % of the frame). Consequences: §6 re-ranked to **B1 → C → A′**, B2 refuted as a throughput lever
and re-filed as a product item, P-3 demoted from gating to enabler.

**Audited:** 2026-08-01, at commit `c7bea678` (branch `feat/world-scaling`).
Findings are from static review of `World.cs:2079–2400` (the lighting ready-set scan and the mesh
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
- [`../Performance/CHUNK_PIPELINE_P9_0A_CAP_SWEEP_IL2CPP_2026-08-02_BENCHMARK.md`](../Performance/CHUNK_PIPELINE_P9_0A_CAP_SWEEP_IL2CPP_2026-08-02_BENCHMARK.md)
  — **this document's phase P9-0a**. Confirms §3.1's rate identity by the mechanism §2 predicted,
  and prices §6's Option A′ out on frame time.
- [`../Performance/CHUNK_PIPELINE_P9_1_ATTRIBUTION_IL2CPP_2026-08-02_BENCHMARK.md`](../Performance/CHUNK_PIPELINE_P9_1_ATTRIBUTION_IL2CPP_2026-08-02_BENCHMARK.md)
  — **this document's phase P9-1**, and the only capture taken with the P9-0 instrument. It supersedes
  P9-0a's §F4 model and refutes two of §3.3's inferences. **Read it before acting on §6.**
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

> **✅ MEASURED by P9-1 (2026-08-02), and the table above is half wrong.** Loading @ 200 m/s, per
> delivered chunk, across vd 10/20/26/32:
>
> | Quantity                          | inferred above | **measured**                | verdict |
> |-----------------------------------|----------------|-----------------------------|---------|
> | Lighting schedules, **total**     | ~6.3–7.6       | **6.26 / 6.47 / 6.28 / 7.41** | ✅ close |
> | Lighting schedules, **pre-delivery** | (assumed all) | **3.83 / 3.94 / 3.92 / 3.95** | ⚠️ ~half |
> | Mesh schedules, **pre-delivery**  | ~2.9–3.5       | **1.00 / 1.00 / 1.00 / 1.00** | ❌ **refuted** |
>
> Three things follow. **(1)** The ~7.6 figure was right about the *total* and wrong about what it
> counted — only ~62 % of lighting work happens before first delivery; the rest is post-delivery
> correction (~1.9/chunk) and work on chunks later discarded (~0.4/chunk). **(2) There is no
> pre-delivery mesh redundancy at all.** Every chunk is meshed exactly once before it is delivered, to
> the unit, at every view distance — so the mesh half of the multiplier this section describes does not
> exist, and Option B2's mesh-side premise is void (§3.4). **(3)** Pre-delivery lighting amplification is
> **flat in view distance but varies by regime**: 3.8–4.0 on the loading pass @ 200 m/s versus **6.6–6.8**
> in the generation pass @ 10 m/s. So it is not a single constant, and a lever that targets it must say
> which regime it targets.

If those ratios are real they are the more interesting factor — but **not because the work is
necessarily wasted**. The v1.0 draft framed amplification as redundancy to delete; §3.4 corrects
that. Known contributors visible in the code:

- **Re-scheduling.** A chunk leaves the lighting ready set on a successful schedule
  (`World.cs:2281`) but re-enters via its completion's flag callback, `PromoteNeighborhood`, and the
  ~1 s `PromoteAll` fail-safe (`World.cs:2135`). Edge checks re-flag neighbours, so one delivered
  chunk legitimately drives several lighting jobs — but nothing today distinguishes *necessary*
  re-lighting from redundant re-lighting.
- **Re-meshing.** `MeshDrainPolicy.Drain` spends a quota unit per `TrySchedule` that succeeds; a
  chunk whose neighbours light up after it meshed is re-queued and spends another. ~3 mesh builds
  per delivered chunk is a plausible ordering artefact (P-7's territory) *or* a real dependency
  requirement — and again, unmeasured.
- **Declined candidates cost no quota** (the quota increments only on success, `World.cs:2276`,
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

> **❌ REFUTED as a throughput/visibility lever by P9-1 (2026-08-02). The product argument below
> stands; the mechanism does not.** Two independent measurements kill it:
>
> - **Its mesh premise is void.** This section reasons that "~3.5 mesh builds are spent ahead of the
>   player seeing anything". Measured pre-delivery mesh amplification is **exactly 1.00** at every view
>   distance (§3.3). There is no pre-delivery mesh work to skip, and no double-mesh cost to confine —
>   which also makes the `SectionRenderer` stream-3 refinement below a solution to a problem that was
>   not there.
> - **Its reachable latency is 16 % of a 42 % gap.** At vd 32 / 200 m/s the end-to-end p50 of 3 644 ms
>   decomposes as `enqueue→populated` **2 976 ms (82 %)**, `populated→lit` 570 ms (16 %),
>   `lit→meshApplied` **7.1 ms (0.2 %)**. B2 acts only on the last two. Removing them entirely leaves
>   3 067 ms against a 2 560 ms budget. The pipeline is not waiting on lighting before it can show a
>   mesh — it is waiting on the panic gate to admit the chunk at all (closed on 91 % of frames).
>
> **Disposition:** B2 is removed from P-9's phases and re-filed as a standalone product item — "a dark
> chunk beats void" remains a legitimate thing to want, and preference order 1–3 above is still the right
> way to want it. It is simply not what closes the visibility budget, and P-9 should stop claiming it is.

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
docstring at `World.cs:2169`). Every unit of mesh quota buys a `TrySchedule` with its own neighbour
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
  limit hit — `World.cs:2202–2189`, `MeshDrainPolicy.cs:87–107`), so the reasons partition the
  frames.
- `Quota` holds 99.3 % of `LightSchedule` frames at vd 32. Therefore `CeilingExpired` accounts for
  **≤ 0.7 %** of them.

Two consequences, and they cut in opposite directions:

1. **There is headroom under the ceiling — but P9-0a measured it as less than ×2.** Doubling the cap
   put `LightSchedule` on `Ceiling` for 95.8 % of frames, after which the count cap is inert and
   delivery is `ceiling_ms ÷ per-item cost`. The estimate below was directionally right and
   quantitatively generous; treat it as superseded by the capture. The pass finishes its quota inside 8 ms. The P-8
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
| Light schedule quota                | `World.cs:2156` — `ComputeQuota(maxLightJobsPerFrame, unscaledDeltaTime)`; break at `World.cs:2202` leaves the remainder READY (§9.1 semantics) |
| Mesh schedule quota                 | `World.cs:2387` → `MeshDrainPolicy.Drain`; break at `MeshDrainPolicy.cs:87` leaves chunks queued in place                                     |
| Caps                                | `maxMeshRebuildsPerFrame` 10 / `maxLightJobsPerFrame` 32 in `SettingsManager.cs:399,409`, **overwritten at runtime by OM-1** (`DeviceCalibration`) |
| ms ceilings                         | `lightScheduleBudgetMs` 8 / `meshScheduleBudgetMs` 6 (`SettingsManager.cs:514,527`), FPS-cap-scaled only (`ScaleCeilingMs`)                   |
| In-flight bounds                    | `maxInFlightLightingJobs` 64, `maxInFlightMeshJobs` 20 — memory bounds, not throughput; `InFlightCap` dominates no phase in any P-8 run       |
| Per-pass main-thread ms             | ✅ **Instrumented by P9-0** (not yet captured). `WorldFrameProfiler`'s phases are now one slot per budgeted pass **plus the three unbudgeted lighting regions — `LightMerge`, `LightStagingDrain` and `LightFailSafeScan`**. Each of the three runs outside the schedule pass's budget window, so keeping them separate is what leaves `LightSchedule`'s milliseconds directly comparable to `lightScheduleBudgetMs`. `BenchmarkController` enables the profiler; `LastFrameLightMs`/`LastFrameMeshMs` remain derived sums, so the fluid-stress collector is unchanged |
| Quota utilisation / amplification   | ✅ **Instrumented by P9-0** (not yet captured). `PipelineTelemetry.RecordPassWork` records served vs granted per pass, **over frames where that pass had work available** — idle frames are excluded from *both* scheduling passes so the two utilisations are computed over the same kind of population and can be compared; a mesh frame refused by the in-flight cap counts at 0 served, since work existed and bought nothing. `StampLightScheduled`/`StampMeshScheduled` count quota units per chunk, split pre-delivery / no-live-trace / wasted |
| Parked time per chunk               | ✅ **Instrumented by P9-0** (not yet captured, §10 q4). `LightWorkScheduler`'s park/promote transitions accumulate per-chunk waiting-set time onto the trace — the class the stop-reason instrument is blind to by construction |
| Benchmark A/B surface               | `OverlayBenchmarkSettingsFromDisk` (`SettingsManager.cs:1116`) copies four benchmark fields + `scalePanicGateThresholdsWithResidency` only    |

Those three rows were the whole reason P-9 opened with attribution: the instrument reported *that* the
pass stopped on quota, and nothing about what the quota bought or what it cost. **P9-0 closed the
instrument gap and P9-1 used it, both on 2026-08-02.** The headline numbers, loading @ 200 m/s:

| Measured (vd 10 → 32)                     | Value                                          |
|-------------------------------------------|------------------------------------------------|
| Whole instrumented pipeline               | **68.5–70.9 % of wall clock, view-distance-invariant** |
| `LightMerge` (unbudgeted)                 | **261–288 ms/s — the largest single slot**     |
| `LightSchedule` (budgeted, 8 ms ceiling)  | 215–221 ms/s                                   |
| Cost per lighting job                     | **0.15 ms schedule + 0.18 ms merge**           |
| `LightStagingDrain` + `LightFailSafeScan` | **< 0.6 ms/s combined — negligible**           |
| Quota utilisation at the shipping cap     | 96–98 %, `Quota`-bound on ~98 % of frames      |

Two consequences worth stating here rather than in §6. **The pipeline's main-thread cost per second is
fixed by the rate**, so it does not grow with view distance — what grows is everything beside it, which
is why the frame rate falls while the pipeline's share does not. And **the two unbudgeted lighting
regions this instrument added on suspicion turned out to be negligible**; only the merge mattered.

Note what the instrument still does *not* separate: `LightMerge` is timed as a whole pass, so P9-1
confirms *that* the merge is the largest cost centre without sizing its internals. **P-3 needs a finer
breakdown before it can be scoped.**

---

## 6. Decision: which lever leads

The ceiling is `rate ÷ amplification`. Three levers move it; they are not exclusive, and the
question is only which one leads.

> **⚠⚠ FINAL RANKING — re-ranked again by P9-1 (2026-08-02), which measured what P9-0a inferred. The
> order is `B1 → C → A′`; B2 leaves P-9 entirely.** This supersedes the P9-0a block below, which is kept
> because its reasoning is still how the probe was designed.
>
> - **B1 leads.** Delivered chunks/s = rate ÷ schedules-per-delivered-chunk = 1 435 ÷ 6.28 = 228.5/s,
>   which matches the measured 228.6/s exactly. It is **the only lever that raises the numerator of that
>   fraction at zero frame-time cost**, and it compounds with every other lever and every device.
>   ⚠️ Conditional, as it always was: P9-1 *sizes* the multiplier, it does not show any of it is
>   redundant — and §3.3 shows it varies by regime, which is what genuinely-required convergence work
>   would also look like.
> - **C is demoted from gating to enabler.** The merge is real and is the largest slot (§5), but at the
>   shipping cap the schedule pass is **`Quota`-bound on ~98 % of frames, not `Ceiling`-bound**. Making
>   each item cheaper therefore frees frame time and delivers **not one extra chunk**. P-3 buys Q2
>   headroom and is the precondition for A′; it is not a substitute for it.
> - **A′ stays closed**, re-failing Q2 on a second independent build (×2.71 CPU, ×0.66 min FPS).
> - **B2 is refuted and leaves P-9** (§3.4): its mesh premise is void and it reaches 16 % of a 42 % gap.
>
> Derivation: [P9-1 capture](../Performance/CHUNK_PIPELINE_P9_1_ATTRIBUTION_IL2CPP_2026-08-02_BENCHMARK.md)
> §F1–F7.

> **⚠ Superseded — re-ranked by P9-0a (2026-08-02). Read this before the options below, which are left
> intact as the reasoning that produced the probe.** The order P9-0a measured was **C → B2 → A′**:
>
> - **A′ is closed** (not merely deferred): ×4.79 CPU for +21 % delivery, and beyond ×2 the ceiling
>   makes the cap inert.
> - **C is promoted from parallel to gating.** Once the ceiling binds, delivery is
>   `ceiling_ms ÷ per-item cost`, so cutting that cost is the *only* way to raise throughput at
>   constant frame time. The capture's fitted model puts the cost in the **unbudgeted
>   `ProcessLightingJobs` merge (P-3)** rather than the schedule scan — pending P9-0's attribution.
> - **B2 is weakened as a throughput lever** (its product rationale in §3.4 is untouched): the hops it
>   can recover total ~537 ms of a 3 703 ms latency, so it cannot meet the visibility budget alone.
>
> Derivation: [P9-0a capture](../Performance/CHUNK_PIPELINE_P9_0A_CAP_SWEEP_IL2CPP_2026-08-02_BENCHMARK.md)
> §F3–F4.

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

### Option B1 — Delete redundant amplification ✅ **CHOSEN (LEADS, per P9-1)**

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

### Option B2 — Deliver on first viable mesh, refine in place ❌ **REFUTED by P9-1 — left P-9, re-filed as a product item (§3.4)**

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

### Option C — Cut per-item main-thread cost ✅ **CHOSEN (enabler for A′, not a throughput lever on its own — P9-1)**

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
- ⚠️ **Run captures on an otherwise idle machine, and say so in the report.** Q2's minimum-FPS arm is
  a single-worst-frame statistic, so one background spike in one leg can manufacture a Q2 failure —
  and Q2 is the hard gate. Throughput criteria are averaged over 30 s and far more robust. P9-0a's
  L1 was captured while the operator was multitasking; it happened to reproduce P-8's control within
  2 %, which bounds the damage there but is not a licence to repeat it.
- ⚠️ **Restore `maxLightJobsPerFrame` / `maxMeshRebuildsPerFrame` to their OM-1 values (24 / 11 on the
  capture machine) after any cap sweep.** Because a menu-launched run inherits the whole settings
  file (§7.1), a left-over experimental cap silently contaminates every later capture and appears in
  no diff. Always re-read the values from the run's own settings block rather than trusting the file.

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

### 7.2 Zero-code options that remain available

Recorded because they cost a capture session rather than an implementation, and P9-0a showed how far
settings alone can go:

- **Ceiling discriminator** — hold `maxLightJobsPerFrame` at 48 and *lower* `lightScheduleBudgetMs`
  from 8 ms. If frame cost stays far above the ×1 leg, the schedule pass is exonerated and §F4's
  merge attribution gains support. Partially confounded (a lower ceiling also lowers throughput, and
  therefore merges), so it weakens rather than settles the question — P9-0's attribution is the clean
  answer. Worth one run only if P9-0 is not going to be built soon.
- **Mesh-side ceiling** — untested. `MeshSchedule` was still `Quota`-bound on ~70 % of frames in
  P9-0a's ×2 leg, so a second ceiling may sit behind it; but `maxInFlightMeshJobs` (20) already
  reported `InFlightCap` on 29 % of those frames, so a mesh-quota leg needs that cap raised in the
  same run or it measures the in-flight bound instead.

## 8. Phased implementation plan

| Phase                                     | Scope                                                                                                                                                                                                                    | Effort | Depends on |
|-------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:------:|------------|
| ~~**P9-0a — Cap-sweep probe (zero code)**~~ ✅ **DONE 2026-08-02** | Ran as two settings-only legs at vd 32 (24 → 48 light jobs) on the P-8 build. **Identity confirmed, Option A′ closed**: gate 95.1 % → 62.6 % closed, `enqueue→populated` −28.9 %, completions +20.9 %, at ×4.79 CPU / ×0.61 min FPS (Q2 fail). Binding limit moved to the **8 ms ceiling**, not a higher quota. [Capture](../Performance/CHUNK_PIPELINE_P9_0A_CAP_SWEEP_IL2CPP_2026-08-02_BENCHMARK.md). Planned vd 26 legs were made redundant by the size of the vd 32 failure |   🟢   | —          |
| ~~**P9-0 — Attribution instrument**~~ ✅ **DONE 2026-08-02** | Shipped, no production behaviour change. `WorldFrameProfiler` now carries one slot per budgeted pass **plus `LightMerge` and `LightFailSafeScan`** — the two unbudgeted regions, and the reason §F4 had to be modelled; `LastFrameLightMs`/`LastFrameMeshMs` survive as derived sums so the fluid-stress collector and its past captures are unaffected. `PipelineTelemetry` gains `RecordPassWork` (served vs granted), per-chunk schedule counts split **pre-delivery / no-live-trace / wasted**, and per-chunk **parked time** (§10 q4) hooked at `LightWorkScheduler`'s park/promote transitions. `BenchmarkController` enables the profiler and clears it in `OnDestroy`. Report prints **NOT MEASURED** rather than 0.0 ms when the profiler did not run. Guarded by **B20–B22**, each prove-red-verified to redden exactly itself (370 baselines, `Validate All` green with telemetry enabled *and* disabled). The §7.1 `SettingsManager` remark rider was already discharged at `7eabda7b`. **Amended the same day after code review** — seven measurement defects found and six fixed *before* any capture ran, since a capture on the flawed instrument would have had to be re-run: the staging drain got its own slot (it sits outside the budget window, so charging it to `LightSchedule` broke that slot's ceiling-comparability); the two passes' utilisation denominators were made the same population; the park interval now survives a flush-and-restart and `LightWorkScheduler.Clear()`; and B21's wall-clock assertions now compare against *measured* spin durations, so an editor hitch can no longer redden a baseline. The seventh — the fail-safe promote-to-rescan gap — is **deliberately left unfixed** and documented in §10 q4 instead |   🟡   | P9-0a      |
| ~~**P9-1 — Capture and decide**~~ ✅ **DONE 2026-08-02** | Five same-build IL2CPP runs (vd 10/20/26/32 at the OM-1 caps + a vd-32 cap-48 A/B leg). [Capture](../Performance/CHUNK_PIPELINE_P9_1_ATTRIBUTION_IL2CPP_2026-08-02_BENCHMARK.md). **Identity confirmed within 4 %**; §F4's model half-confirmed and corrected (0.15 ms schedule + 0.18 ms merge; the merge is 39 % of the ×2-cap growth); **§3.3's mesh multiplier refuted** (pre-delivery = 1.00); §10 q4 answered (parking = 43–48 % of the idle-pass hop). **Kill condition NOT triggered.** Lever order → **B1 → C → A′**; B2 leaves P-9 |   🟢   | P9-0       |
| **P9-2 — Delete redundant amplification** (Option B1) ⬅ **NEXT** | **The lead fix.** Target: 6.28 lighting schedules per delivered chunk (3.9 pre-delivery, ~1.9 post-delivery corrections, ~0.4 on chunks later discarded). Find and remove schedules that recompute an unchanged result — raising delivery at zero frame-time cost. **Starts with an investigation, not a patch:** P9-1 sizes the multiplier but does not show any of it is redundant, and §3.3 shows it varies by regime. **May legitimately come back empty**, in which case the lead passes to C → A′. ⚠️ Touches the lighting convergence model — `chunk-lifecycle` skill mandatory |   🟡   | P9-1       |
| ~~**P9-2b — Deliver-then-refine** (Option B2)~~ ⛔ **WITHDRAWN from P-9 2026-08-02** | Refuted as a throughput/visibility lever by P9-1 (§3.4): pre-delivery mesh amplification is exactly 1.00 so there is no pre-delivery mesh work to skip, and the hops it can reach total 16 % of a 42 % gap. **Re-filed as a standalone product item** — "a dark chunk beats void" is still worth wanting, it just does not close the visibility budget |   —    | —          |
| **P9-3 — Re-tune the rate** (Option A′)   | ⛔ **BLOCKED, and P9-1 re-confirmed it on a second independent build** (×2.71 CPU, ×0.66 min FPS). At ×2 the ceiling binds *completely* — **one `Quota` stop in 930 frames**, utilisation collapsing to 58.5 % — so the cap is inert past that point and the same work is merely repacked into 3.2× fewer, 3.2× longer frames. Re-openable **only after per-item cost falls (P-3)**, at which point the same 8 ms buys more items. Do not attempt before then.                                                                                                                                          |   🟡   | **P-3**, P9-1 |

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
   delivered chunk is inferred from two ratios. The split, not the total, is what decides whether
   Option B2 has anything to reorder. If the multiplier lands near 1, both B options are dead and the
   lead passes to A′ or C. **P9-0 now measures both (2026-08-02); the question stays open until P9-1
   runs a capture.**

   > **✅ ANSWERED by P9-1 (2026-08-02).** Amplification is real, and the split is roughly **62 %
   > pre-delivery / 30 % post-delivery correction / 8 % spent on chunks later discarded** — 3.92 of
   > 6.28 lighting schedules per delivered chunk, loading @ 200 m/s at vd 32. It does **not** land near
   > 1, so the B-options were not dead on this axis. But B2 died on a different one (§3.4), so the split
   > now serves **B1**: it says how much work exists to examine, not that any of it is removable.
   > The mesh half of the question is settled outright — pre-delivery mesh amplification is **1.00**.

   Two properties of the instrument to carry into reading it. The post-delivery half is recorded as
   "schedules with no live trace", which is an **upper bound** — it also absorbs schedules for chunks
   that were never traced (saturation) or already closed by an unload, so it must be read beside the
   phase's saturation flag. And the four buckets — pre-delivery, no-live-trace, wasted, and
   **unresolved** (superseded by a re-request, or still in flight when the phase ended) —
   **partition every schedule stamped**. The report checks their sum against the quota table's
   independently-counted total and flags any gap, which is the guard against the failure this kind of
   split makes easy: a bucket quietly missing a population still prints a perfectly plausible ratio.
2. ~~**How much of amplification is P-7's ordering problem wearing a different hat?**~~ **Resolved as
   a scoping decision, 2026-08-01.** It may well be partly ordering, and P9-0's split will say so —
   but it no longer changes what P-9 does. Under §3.4 the fix is to stop gating visibility on
   settled lighting, which helps whether the late neighbour arrived late for ordering reasons or
   throughput reasons. P-9 and P-7 stay independent items with a **shared acceptance target** (the
   visibility criterion, Q1); if both ship, they compound rather than overlap.
3. **What is the right steady-state ms budget for the two scheduling passes?** The current 8 + 6 ms
   are hitch guards (§4.2). Nobody has stated what fraction of a frame the pipeline *should* own,
   and Option A′ cannot be tuned without an answer.
4. **Why is `populated→lit` ~3.4 s at low speed when the lighting pass is idle?** ⬅ **new, from
   P9-0a — a candidate third cause this document does not otherwise name.** In the generation pass
   @ 10 m/s, in **both** legs, the panic gate is effectively never closed (0.4 % / 0.0 % of frames)
   and `LightSchedule` reports **`OutOfWork` on ~92 % of frames** — an *idle* lighting pass — yet:

   | Leg | light cap | `populated→lit` p50 | `lit→meshApplied` p50 |
   |-----|-----------|---------------------|-----------------------|
   | L1  | 24        | 3 417 ms            | 1 437 ms              |
   | L2  | 48        | 3 408 ms            | 1 439 ms              |

   **Doubling the quota moved it by 0.3 %.** That is not a throughput ceiling, an admission stall or
   a budget: it is a chunk sitting parked, waiting to become *eligible*. The likely mechanism is
   readiness/promotion latency — a chunk blocked on neighbour readiness is parked by MT-2, which
   removes it from the ready set, so it cannot be counted as an `AllDeclined` candidate. **The
   stop-reason instrument is blind to this class by construction**, which is why no capture has
   named it before.

   Consequences: it bounds what *any* throughput or admission work can achieve in the uncongested
   regime; and if the same waiting contributes at high view distance, part of §3.3's amplification
   is neither redundancy nor pre-delivery correctness work but pure latency. P9-0's attribution
   should count **parked-time per chunk**, not only pass costs. May belong to the lighting async
   roadmap (`AS-*`) rather than to P-9 — decide once it is measured rather than inferred.

   > **✅ ANSWERED by P9-1 (2026-08-02): parking is 43–48 % of the hop, and it is view-distance-invariant.**
   > In the generation pass @ 10 m/s, where `LightSchedule` reports `OutOfWork` on 89–97 % of frames and
   > the gate never closes:
   >
   > | vd | `populated→lit` p50 | **parked p50** | parked ÷ hop |
   > |----|---------------------|----------------|--------------|
   > | 10 | 3 282 ms            | **1 568 ms**   | **47.8 %**   |
   > | 20 | 3 343 ms            | **1 542 ms**   | **46.1 %**   |
   > | 26 | 3 392 ms            | **1 516 ms**   | **44.7 %**   |
   > | 32 | 3 434 ms            | **1 474 ms**   | **42.9 %**   |
   >
   > So about half of this stall is a chunk sitting **ineligible**, which no quota, gate or budget change
   > can reach — the bound this question predicted. The remaining ~1.9 s is neither parked nor un-served
   > (the pass is idle). The most likely home is the **serialized edge-check cascade**: `populated→lit`
   > measures to the *last* lighting completion and pre-delivery amplification in this regime is ~6.7
   > passes per chunk. ⚠️ **That attribution is NOT measured** — the instrument counts parked time and
   > schedules, not cascade depth per round. It is the obvious next question, and it belongs to the
   > `AS-*` lighting roadmap rather than to P-9.

   **✅ Instrumented 2026-08-02 (P9-0).** Per-chunk parked time is now accumulated
   across `LightWorkScheduler`'s park/promote transitions and reported as percentiles beside the hop
   latencies, so P9-1 can compare it directly against `populated→lit`.

   ⚠ **The measure is a LOWER BOUND on ineligibility, and every bias runs the same way — against the
   chunks that waited longest**, which are the ones this question is about. Three scope limits, all
   stated before the numbers arrive so none of them can be discovered as a convenient explanation
   afterwards:

   - **Delivered chunks only.** The sample is emitted when a trace closes as `MeshApplied`, so a chunk
     that parked and was then unloaded contributes to no percentile.
   - *(Not a limit — recorded because it was one.)* A wait spanning a **re-request or a phase boundary**
     *is* counted, and in full. The open interval is keyed by chunk in a side table rather than held on
     the trace, precisely because a chunk stays parked across both events while its trace does not
     survive either; holding it on the trace reported zero for exactly the population this question is
     about. The wait is credited to the phase it ends in.
   - **The lighting waiting set only.** A chunk stalled in the mesh queue on readiness surfaces as
     `AllDeclined` or queue depth, never as parked time.
   - **The promote-to-rescan gap is not counted.** The ~1 Hz fail-safe promotes the whole parked set at
     once, but a chunk only re-parks when the scan actually *reaches* it — and the scan breaks on quota
     or ceiling. A promoted-but-unreached chunk therefore sits in the ready set accruing nothing.
     **Deliberately not fixed:** that time is already visible as `ReadyCount` plus a `Quota`/`Ceiling`
     stop, so counting it as parked would double-count against a signal the same report carries. Note
     the gap is near zero in the regime this question actually asks about — at 10 m/s `LightSchedule`
     reports `OutOfWork` on ~92 % of frames, so the scan is idle and reaches promoted chunks within a
     frame — and widest in the congested high-view-distance regime.

5. **Is the regime this document optimises even a shipping regime?** ⬅ **new, from P9-1 — and it
   governs whether P-9 restarts at all.** `Settings.viewDistance` defaults to **5**
   (`SettingsManager.cs:168`), and P9-1 measured **vd 10 meeting the visibility budget** (813 ms
   against 800 ms). Every failure this document is built on lives at **vd ≥ 20**: the 1.4–1.5×
   shortfalls, the 91 % closed gate, the ×2.71 CPU cost of raising the cap. P-9 was promoted to top
   pipeline item on P-8's **vd 32** numbers — a configuration a default-settings player never enters.

   This is *not* an argument that the work was wasted: the rate identity, the per-pass attribution and
   the instrument are permanent, and they apply at every view distance. It is an argument that the
   **priority** was set by a stress point rather than by a shipping configuration, and nobody has
   decided which vd 32 is. The same question FP-10 left open about vd 32's 5 GB peak.

   **Decide it before P-9 restarts after P9-2.** If vd 32 is a supported configuration, the remaining
   levers are worth their cost; if it is a stress point, P-9's honest disposition after P9-2 is
   *parked*, and the pipeline's real backlog is P-7 (which is scoped to low view distance and therefore
   to the default) plus the correctness items. Whoever answers this should also state where the
   supported ceiling is, since `viewDistance` is `[Range]`-editable up to a value no capture defends.

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
* **v1.4** - **P9-0a captured** ([report](../Performance/CHUNK_PIPELINE_P9_0A_CAP_SWEEP_IL2CPP_2026-08-02_BENCHMARK.md)).
  §3.1's rate identity is **confirmed** by the mechanism §2 pre-committed to — doubling the light cap
  reopened the gate (95.1 % → 62.6 % closed) and collapsed `enqueue→populated` (−28.9 %) rather than
  the lighting hop — and §6's Option A′ is **closed on frame time** (×4.79 CPU, ×0.61 min FPS). Three
  body corrections: §4.2's headroom quantified as **less than ×2** (the binding limit becomes the 8 ms
  ceiling, after which the cap is inert); §6 re-ranked to **C → B2 → A′**, promoting per-item cost
  (**P-3**, the unbudgeted lighting merge) from parallel to gating; §3.4's B2 weakened as a throughput
  lever while its product rationale stands. §8: P9-0a done, P9-3 blocked behind P-3.

* **v1.6** - **P9-0 implemented** (2026-08-02), no production behaviour change and no capture yet.
  `WorldFrameProfiler`'s phase set is split one-per-budgeted-pass and, critically, gives the two
  **unbudgeted** regions their own slots — `LightMerge` (the §F4 suspect) and `LightFailSafeScan` (the
  ~1 Hz full-world walk, which would otherwise have reproduced the same unattributed-cost gap in a new
  place). `LastFrameLightMs`/`LastFrameMeshMs` become derived sums, so the fluid-stress collector and
  captures taken before the split are unaffected. `PipelineTelemetry` gains served-vs-granted per pass,
  per-chunk schedule counts split pre-delivery / no-live-trace / wasted (§10 q1), and per-chunk parked
  time (§10 q4). The report prints **NOT MEASURED** rather than a table of zeros when the profiler did
  not run — a zero would read as "scheduling is free", the opposite of the truth. §5's two "not
  measured" rows are retired and a third (parked time) added; §8's P9-0 row closes and **P9-1 gains a
  vd-32 cap-48 leg**, because §F4's model was fitted across two caps and a single-cap sweep cannot test
  its slope. Guarded by B20–B22, each prove-red-verified.
* **v1.6a** - Code review of the v1.6 instrument, same day, **before any capture**. Six of seven findings
  fixed, all of them measurement-correctness rather than production behaviour: the **staging drain** gets
  its own unbudgeted slot (it runs before the budget window, so charging it to `LightSchedule` silently
  broke that slot's comparability with `lightScheduleBudgetMs` — the very property the split was for);
  the two scheduling passes' **utilisation denominators** were computed over different frame populations,
  which would have made lighting read as starved against a mesh figure counted only over frames it was
  allowed to serve; **parked time** now survives a flush-and-restart (a re-requested chunk previously
  recorded zero however long it had waited) and `LightWorkScheduler.Clear()`; two stale comments; and
  B21's wall-clock bounds now compare against **measured** spin durations rather than literals, so an
  editor hitch cannot redden a baseline while the ~10 ms discrimination each assertion needs survives.
  The seventh — the fail-safe promote-to-rescan gap — is **deliberately not fixed**, because that time is
  already carried by `ReadyCount` plus the `Quota`/`Ceiling` stop reasons and counting it as parked would
  double-count; §10 q4 now states it alongside the other two biases, all of which run the same direction.
* **v1.6b** - Second review round, still before any capture. Four findings, all fixed. **(1)** The
  amplification buckets did not partition the schedules: `Rerequested` and `InFlightAtPhaseEnd` traces
  hit neither the delivered nor the wasted arm, so their quota units left the accounting entirely and the
  columns silently stopped summing to what the quota table reported. Adds an **unresolved** bucket plus a
  **reconciliation check** — two independently-counted paths (per item, per frame) that must agree, with a
  banner on any gap, because a bucket missing a population still prints a plausible ratio. **(2)** Park
  state moves from the trace to a **coord-keyed side table** that phase boundaries do not clear. A chunk
  stays parked across both a re-request and a speed-tier boundary while its trace survives neither, so
  the per-trace timestamp reported **zero** for waits that really happened — on exactly the population
  §10 q4 asks about. This also retires v1.6a's carry-forward heuristic, which could only ever patch one
  of the two boundaries, and changes the semantics to credit the **whole** wait: the chunk was ineligible
  throughout, and a re-request is bookkeeping in this layer rather than a physical reset. **(3)** A
  comment claiming unloaded chunks' waits are "kept counted" when `CloseTrace` discards them. **(4)** A
  baseline whose label asserted more than it tested, and falsely — the disabled profiler *freezes* its
  published values rather than zeroing them, which is now pinned rather than mis-described.

* **v1.7** - **P9-1 captured** ([report](../Performance/CHUNK_PIPELINE_P9_1_ATTRIBUTION_IL2CPP_2026-08-02_BENCHMARK.md)),
  and it corrects this document as much as it confirms it. **Confirmed:** §3.1's rate identity within
  4 % across vd 10→32 (1 435–1 496/s against a predicted 1 440), the flat completion band on a fresh
  build, and the exact closure `delivered/s = rate ÷ schedules-per-chunk`. **Corrected:** §F4's fitted
  0.37 ms/merge was the *sum of both lighting passes* — measured 0.15 ms schedule + 0.18 ms merge, with
  the merge 39 % of the ×2-cap growth rather than ~70 %. **Refuted:** §3.3's ~3.5 mesh schedules per
  delivered chunk — pre-delivery mesh amplification is **exactly 1.00** everywhere, so §3.4's Option B2
  has no pre-delivery mesh work to skip; combined with an 82 %-admission-wait latency split, **B2 is
  refuted as a throughput lever and leaves P-9** for a standalone product item. **Answered:** §10 q1
  (62 % pre-delivery) and §10 q4 (parking = 43–48 % of the idle-pass hop). §2's kill condition is **not**
  triggered — the two scheduling passes are 32 % of the frame, though the whole pipeline is ~69 % and
  lighting alone ~49 %. §6 re-ranked to **B1 → C → A′**, with **P-3 demoted from gating to enabler**
  because the schedule pass is `Quota`-bound on ~98 % of frames, so cheaper items buy frame time and no
  extra chunks. §8: P9-1 closes, P9-2 becomes B1, P9-2b withdrawn.
* **v1.7a** - Handoff-audit pass. **New §10 open question 5**, which had existed only in conversation:
  `viewDistance` defaults to **5** and P9-1 measured **vd 10 meeting the budget**, so every failure this
  document is built on lives at vd ≥ 20 — a configuration a default player never enters. It asks whether
  vd 32 is a supported config or a stress point, and makes that the gate on P-9 restarting after P9-2.
  Also corrects **eight stale `World.cs` line references** in the audit header, §3.3, §4.1, §5 and §9.1:
  the P9-0 commit (`b5808a56`) added ~55 lines to the instrumented region, so every citation drifted by
  10–56 lines and would have sent a cold session to the wrong code.

---

**Last Updated:** 2026-08-02
**Next Review:** when P9-2 (delete redundant amplification) starts — its first job is to establish
whether *any* of the measured 6.28 lighting schedules per delivered chunk recomputes an unchanged
result. If none does, P-9's remaining path is C → A′ and this document should say so
