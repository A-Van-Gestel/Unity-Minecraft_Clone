# ES-0 — Time to Stable: Launch to Handoff, Drained Pipeline and Settled Frame Time

| Field           | Value                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            |
|-----------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-10-10                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       |
| **Branch**      | `main`                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           |
| **Commit**      | `c48cc578` + uncommitted ES-0 drain stamp (`StartupTimeline`, `PipelineDrainPredicate`, the `startup-new` / `startup-existing` launch actions); the stamp only observes, no engine behavior change                                                                                                                                                                                                                                                                                                                                               |
| **Captured by** | `StartupProbeController` (`-mc-run startup-new` / `startup-existing`, `-mc-set perfMonitorTier=Capture`) — **IL2CPP player, Master configuration, non-development**, Burst AOT, D3D11, Unity 6000.6.5f1, i9-9900K / RTX 4070 Ti, 240 Hz display; 3 new-world + 3 existing-world launches, alternating; one Editor (Mono) screening launch                                                                                                                                                                                                        |
| **Verdict**     | **BASELINE (instrumentation capture, no behavior change)** — at vd 10 the pipeline is **drained 4.9–5.6 s** after the scene load request on a new world and **1.7 s** on an existing one, inside the roadmap's ≤ 8 s goal. Frame time settles **3–10 s later**, held back only by the fluid tick's prepare (34–43 fluid chunks, 3.6–6.0 ms per tick) while the spawn's fluids settle. The new-world startup coroutine is four frozen frames: one ~1.05 s generation-processing frame with 4–5 collections and three 0.34–0.41 s lighting sweeps. |

> Delivers ES-0's last item, the once-per-launch drain stamp, in
> [`ENGINE_SCALING_PERFORMANCE_ROADMAP.md`](../Design/ENGINE_SCALING_PERFORMANCE_ROADMAP.md) §4 Tier 0: "ms to
> `_isWorldLoaded`, to drained, to frame time within 1.25× median for 2 s — splits the 30 s into before/after handoff".
> The traversal side of the same re-measurement is
> [`ENGINE_SCALING_ES0_REMEASURE_IL2CPP_2026-10-10_BENCHMARK.md`](ENGINE_SCALING_ES0_REMEASURE_IL2CPP_2026-10-10_BENCHMARK.md).

## What this measures (and what it does NOT)

**Measures**, per launch, milliseconds from the World scene load request (`WorldLaunchState.MarkWorldSceneRequested`,
stamped where the main menu and the automated launcher load the scene) to:

| Mark          | Stamped at                                                                                                            |
|---------------|-----------------------------------------------------------------------------------------------------------------------|
| **Awake**     | `World.Awake`                                                                                                         |
| **LoadsDone** | the startup coroutine's initial loads finishing (ES-1's counter barrier)                                              |
| **Handoff**   | `_isWorldLoaded = true` — `World.Update` takes over                                                                   |
| **Drained**   | the first of 30 consecutive frames passing P-4's tail-inclusive drain test (`PipelineDrainPredicate`)                 |
| **Stable**    | the end of the first trailing 2 s window after the drain whose worst frame ≤ max(1.25 × window median, median + 2 ms) |

The drain test is P-4's: generation queue, generation jobs, lighting ready-set, lighting jobs and mesh jobs all empty,
and every chunk of the load square populated. The mesh build queue and the lighting waiting set are excluded (their
steady state is the perimeter ring). The player is held at spawn. Leaving the start chunk ends a measurement as
*interrupted*, and no settled frame time within 300 s of the handoff ends it as *timed out*. Both rules are pinned by
`Validate Performance Monitor` B38/B39.

**Does NOT measure:**

- **Process start to the scene load request** (the boot and the main menu). The anchor is the request.
- **Time to stable while moving.** Streaming new terrain is not startup.
- **View distances other than 10.**
- **What the four frozen startup frames spend their time on.** No slot covers the startup coroutine, and counters
  read 0 until the handoff. The coroutine's own phase report (logged by `ForceCompleteDataJobsCoroutine`) gives the
  split below.

## Methodology

`Windows - Production` build (`2026-10-10 - ES-0 remeasure Master [IL2CPP]`). The build folder's `settings.json` was
created on the first launch with the device-calibrated values (32 light / 10 mesh jobs per frame, initial load radius
10, vSync 1). Six unattended launches, alternating new and existing: `startup-new` deletes the probe world
(`StartupProbe_Saves/StartupProbe`, seed 0) and generates it; `startup-existing` loads what the previous run saved on
quit. Each run writes a `StartupRun_*.log` and quits with exit code 0 only when every mark is reached. All six exited 0.
Capture tier, so every frame is in a session file. The run raises the tier to Systems itself, for the disk counters
that count chunks loaded versus generated.

## Result — IL2CPP Master, vd 10, ms after the scene load request

| Run | World    | Awake | LoadsDone | Handoff | Drained | Stable | Settled window (median / worst) | Chunks from disk / generated |
|-----|----------|------:|----------:|--------:|--------:|-------:|---------------------------------|------------------------------|
| S1  | new      | 59    | 192       | 3 736   | 5 580   | 15 434 | 4.16 / 4.24 ms                  | 0 / 729                      |
| S2  | existing | 46    | 221       | 641     | 1 749   | 10 270 | 4.17 / 4.32 ms                  | 729 / 0                      |
| S3  | new      | 46    | 153       | 3 638   | 4 946   | 11 168 | 4.17 / 4.30 ms                  | 0 / 729                      |
| S4  | existing | 48    | 226       | 647     | 1 671   | 4 351  | 4.17 / 4.29 ms                  | 729 / 0                      |
| S5  | new      | 46    | 166       | 3 517   | 4 867   | 11 038 | 4.17 / 4.28 ms                  | 0 / 729                      |
| S6  | existing | 45    | 218       | 649     | 1 704   | 6 292  | 4.17 / 4.28 ms                  | 729 / 0                      |

**The startup coroutine's own split** (`ForceCompleteDataJobsCoroutine`, same runs):

| Run | Coroutine total | Generation processing | Lighting scheduling | Lighting completion | "Initial data load" log line |
|-----|----------------:|----------------------:|--------------------:|--------------------:|-----------------------------:|
| S1  | 3 380 ms        | 1 020 ms              | 1 078 ms            | 1 225 ms            | 3 502 ms                     |
| S3  | 3 315 ms        | 977 ms                | 1 035 ms            | 1 239 ms            | 3 365 ms                     |
| S5  | 3 182 ms        | 859 ms                | 1 022 ms            | 1 209 ms            | 3 253 ms                     |
| S2  | 270 ms          | 0 ms                  | 130 ms              | 133 ms              | 391 ms                       |
| S4  | 271 ms          | 0 ms                  | 133 ms              | 132 ms              | 396 ms                       |
| S6  | 274 ms          | 0 ms                  | 128 ms              | 135 ms              | 391 ms                       |

**The longest frames of each launch** (session files; frame index, wall time, collections in that frame):

| Run | Longest             | 2nd        | 3rd               | 4th        |
|-----|---------------------|------------|-------------------|------------|
| S1  | f89 1 038 ms (4 GC) | f92 397 ms | f91 382 ms        | f90 364 ms |
| S3  | f90 1 053 ms (4 GC) | f92 396 ms | f93 378 ms        | f91 356 ms |
| S5  | f89 1 044 ms (5 GC) | f91 392 ms | f92 379 ms        | f90 336 ms |
| S2  | f3 181 ms           | f67 164 ms | f8 151 ms (21 GC) | f4 106 ms  |
| S4  | f3 186 ms           | f69 161 ms | f8 149 ms (20 GC) | f4 107 ms  |
| S6  | f3 180 ms           | f69 164 ms | f8 157 ms (21 GC) | f4 107 ms  |

The three vd-10 benchmark runs of the traversal report open with the same shape (frame 89–91: 1 043–1 065 ms with 4–5
collections, then 337–408 ms frames).

**What holds frame time between Drained and Stable** (session files; a frame is over the bound when its wall time
exceeds median + 2 ms ≈ 6.17 ms; each over-bound frame is attributed to its costliest slot):

| Run | Frames drained → stable | Over the bound | Worst   | Collections | Led by                                        | Tick-led frames: part, fluid chunks, Tick ms |
|-----|------------------------:|---------------:|--------:|------------:|-----------------------------------------------|----------------------------------------------|
| S1  | 2 354                   | 19             | 9.0 ms  | 1           | Tick 10, MeshSchedule 5, LightMerge 4         | Prepare ×10; 34–42; 3.7–4.6                  |
| S2  | 2 036                   | 22             | 10.0 ms | 0           | Tick 9, MeshSchedule 4, LightMerge 3, other 6 | Prepare ×9; 38–43; 4.0–5.8                   |
| S3  | 1 487                   | 14             | 9.9 ms  | 0           | Tick 7, MeshSchedule 4, LightMerge 2, other 1 | Prepare ×7; 34–42; 3.6–4.9                   |
| S4  | 642                     | 7              | 8.8 ms  | 0           | Tick 3, MeshSchedule 2, other 2               | Prepare ×3; 37–39; 4.0–4.3                   |
| S5  | 1 473                   | 13             | 9.1 ms  | 0           | Tick 7, MeshSchedule 4, LightMerge 2          | Prepare ×7; 34–42; 3.7–4.7                   |
| S6  | 1 095                   | 9              | 10.5 ms | 0           | Tick 5, LightMerge 3, LightSchedule 1         | Prepare ×5; 37–39; 3.9–6.0                   |

Every Tick-led frame had 187 active grass voxels: the grass tick is negligible here.

**Editor screening (Mono, not comparable).** One `startup-new` in the Editor: Awake 47, LoadsDone 976, Handoff
11 184, Drained 13 298 ms, then **timed out** — Editor frames (p50 3.33, p99 6.65, worst 17.7 ms over 2 048 frames)
exceed the settle bound in every 2 s window, so the Editor reports only the first four marks. It also ran on the
Editor's own `Assets/settings.json` (64 light / 15 mesh jobs, initial radius 15, 841 chunks), not on defaults; see the
limitation below.

## Analysis

- **The drained pipeline already meets the roadmap's ≤ 8 s goal at vd 10.** New world 4.9–5.6 s, existing world
  1.7 s. The 2026-10-02 picture of "30 s from Play to stable" belongs to the Editor and to pre-ES-1/ES-2 code: in
  Master, the handoff alone was 2 719 ms (loaded) / 4 623 ms (new) on 2026-09-22 and is 0.64 s / 3.5–3.7 s now.
- **The new-world handoff is the startup coroutine, and the coroutine is four frames.** Phase 1's generation
  processing runs as **one ~1.05 s frame containing 4–5 garbage collections** (no time window, roadmap §2.1 row 3). It
  is followed by three lighting-fixpoint sweeps of 0.34–0.41 s each. This also answers ES-1's open finding: the new-world
  coroutine is 3.2–3.4 s against 2.3 s on 2026-09-22, and collections do land inside its long frames. How much of the
  1.05 s the collections cost is not measurable: Boehm reports no pause time.
- **An existing world hands off in 0.64 s.** Its longest frames are 150–186 ms. Frame 8 carries ~20 collections in
  one frame, the deserialize burst of 729 disk loads.
- **After the drain, the only thing that keeps frame time unsettled is the fluid tick's prepare.** Of 7–22 frames over
  the bound per launch, every Tick-led one is the prepare part, with 34–43 fluid chunks copied (≈ 4 ms). The rest are
  isolated MeshSchedule / LightMerge frames of 6–10 ms. "Stable" therefore arrives when the spawn's fluids settle,
  which is why it varies 3–10 s between identical launches. The cost per fluid chunk matches PM-8's 0.11–0.14 ms.
- **The settle rule works in Master, not in the Editor.** At 240 Hz vSync the settled windows hold a 4.17 ms median
  with a worst frame of 4.24–4.32 ms. The Editor's jitter fails the same rule indefinitely.

## Verdict details

Baseline only; nothing ships behavior. Feeds the roadmap:

- **ES-3** (loading mode) owns the single 1.05 s generation-processing frame and the three sweep frames. This capture
  is its before-number, scored with `-mc-run startup-new`.
- **ES-29** (filed from this capture and the traversal report): the behavior tick's fluid prepare and managed grass tick.
  The prepare is the whole drained-to-stable tail here.
- **Limitation — benchmark-mode settings.** `SettingsManager.LoadSettings`' benchmark branch (now also used by the
  startup probe) returns the cached settings when any were loaded before the mode switch. The main menu always loads
  them first, so automated runs use the settings file rather than `new Settings()` defaults. A fresh player build's
  file holds the calibrated defaults, so these Master runs are unaffected. Editor runs use `Assets/settings.json`.
  Reported, not fixed.

**Reproduce:** `"Minecraft Clone.exe" -force-d3d11 -mc-run startup-new -mc-set perfMonitorTier=Capture -mc-mute -mc-quit`,
then `-mc-run startup-existing` with the same arguments.
