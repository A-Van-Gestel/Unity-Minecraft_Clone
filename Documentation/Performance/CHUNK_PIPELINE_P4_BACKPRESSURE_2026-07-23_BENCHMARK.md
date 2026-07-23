# P-4 §3.4/§3.5 Pipeline Backpressure — time budgets + panic gate vs legacy count caps

| Field           | Value                                                                                                                                                                                                                            |
|-----------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-07-23 (local)                                                                                                                                                                                                               |
| **Branch**      | `feat/world-scaling`                                                                                                                                                                                                             |
| **Commit**      | `10d993b6` + uncommitted P-4 §3.4/§3.5 working tree (both legs live in this build — flag-switched)                                                                                                                               |
| **Captured by** | In-game scripted fill probe (`Unity_RunCommand` coroutine over the live `World`) — **Editor Mono (screening only)**, 1 run/leg, 6 legs, world `Test S3.2` (v13, seed 116244), fresh never-generated destination per leg          |
| **Verdict**     | **GO (screening)** — streaming frame health transforms (fill-load FPS 13.3 → 29.1, hitch-frame rate 67% → 11%, peak frame 232 → 141 ms) at a known, tunable fill-latency cost (×1.7 uncapped); confirm with an IL2CPP capture |

> Serves `Design/CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md` §3 recommendations 4 (time-based budgets)
> and 5 (panic gate) — the last two open items of the P-4 backpressure family (§3.1/§3.2/rec-3
> shipped 2026-07-21). Legs A/B the new `Settings.enablePipelineTimeBudgets` +
> `enableGenerationPanicGate` flags (OFF = the exact legacy fixed per-frame count caps) across three
> externally-imposed FPS conditions. Correctness guard: `Minecraft Clone/Dev/Validate Pipeline
> Backpressure` (B1–B6) + Validate All 335/335 across 16 suites, both green on this working tree.

## What this measures (and what it does NOT)

One leg = teleport the player (via the live console engine, `/teleport`) to a fresh, never-generated
destination and measure wall-clock **fill time**: teleport issue → the full load square (27×27 = 729
chunks at LoadDistance 13) populated AND the generation queue, in-flight generation jobs, and the
lighting ready-set all drained, sustained for 30 consecutive frames. The measured path is the whole
production streaming pipeline: `CheckViewDistance` → `DrainGenerationRequests` (panic gate seam) →
`ProcessGenerationJobs` → lighting ready-set scan → mesh scheduling → `ProcessMeshJobs` →
`ChunksToDraw` drain — i.e. exactly the passes §3.4 budgets (plus their §5.3 rider).

Per-frame companions: `Time.unscaledDeltaTime` max (peak frame), count of frames > 50 ms
(hitch-frame count), average FPS over the fill, and the panic-gate close count.

**NOT measured here:** per-pass µs attribution (WorldFrameProfiler was not enabled), allocation
behavior (unreliable on editor Mono), and any player-backend number — **Editor Mono is
screening-only; the shippable capture is an IL2CPP Development Build player** (not yet captured).
Terrain variance across the six destinations is uncontrolled noise (~±10% plausible on fill time);
the legacy legs' internally consistent frames-to-fill (85–89) suggest it is small.

## Methodology

- **Same build, same session, flag-switched legs** — `enablePipelineTimeBudgets` +
  `enableGenerationPanicGate` toggled together (ON = the shipping default config, OFF = exact
  legacy behavior by construction: quota == cap and the budget window never expires when the flag
  is off).
- FPS conditions imposed with `QualitySettings.vSyncCount = 0` + `Application.targetFrameRate`
  (−1 / 15 / 5). "Uncapped" still lands at the machine's natural loaded rate.
- Budget defaults under test: light schedule 8 ms, mesh schedule 6 ms, gen process 6 ms, mesh apply
  4 ms, draw 2 ms; quota = per-frame cap × unscaledDeltaTime × 60 (clamped [1, 8×cap]); panic gate
  close ≥ 256 ready / reopen ≤ 128 ready.
- One run per leg (fill is a ~7–52 s macro-scenario; single-run noise bounded by the frames-to-fill
  consistency noted below). No warm-up runs — each leg's destination is cold by design.
- `hitches50` counts frames > 50 ms. **At the 15/5 caps every frame exceeds 50 ms by
  construction** — the column is only meaningful for the uncapped legs (marked cap-implied
  otherwise).

## Result — Editor Mono, 729-chunk fill per leg

| Leg | Budgets+gate | FPS condition | Fill (s) | Frames to fill | Avg FPS | Peak frame (ms) | Hitch frames >50 ms | Fill rate (chunks/s) | Gate closes |
|-----|--------------|---------------|----------|----------------|---------|-----------------|----------------------|----------------------|-------------|
| L1  | **ON**       | uncapped      | 11.30    | 329            | **29.1**| **140.8**       | **36 (11%)**         | 64.5                 | 4           |
| L2  | **ON**       | 15 cap        | 17.30    | 259            | 15.0    | 97.2            | (cap-implied)        | 42.1                 | 3           |
| L5  | **ON**       | 5 cap         | 51.71    | 259            | 5.0     | 271.4           | (cap-implied)        | 14.1                 | 3           |
| L3  | OFF          | uncapped      | **6.70** | 89             | 13.3    | 232.3           | 60 (67%)             | **108.8**            | 0 (off)     |
| L4  | OFF          | 15 cap        | 7.61     | 86             | 11.3    | 210.6           | 83 (97%)             | 95.8                 | 0 (off)     |
| L6  | OFF          | 5 cap         | 16.78    | 85             | 5.1     | 241.4           | 85 (100%)            | 43.4                 | 0 (off)     |

## Analysis

1. **Frame health during streaming (the frame-level story).** Legacy's fill *is* a sustained
   hitch-storm: uncapped it drives its own FPS down to 13.3 (its passes are the frame), 67% of fill
   frames exceed 50 ms, peaking at 232 ms; under external caps its hitch-frame rate reaches
   97–100%. Budgets hold 29.1 FPS with an 11% hitch rate and a 141 ms peak through the identical
   fill. Frames-to-fill exposes the mechanism: legacy crams the whole backlog into ~85–89 giant
   frames; budgets spread it over 259–329 bounded ones.
2. **The cost: fill latency.** Uncapped fill is ×1.69 slower with budgets (11.30 vs 6.70 s) — the
   deliberate price of bounding the per-frame main-thread slice (~26 ms total ceilings vs legacy's
   unbounded ~75+ ms). Tunable: raising the ms ceilings trades smoothness back for fill speed.
3. **§3.4 throughput constancy holds in the mid band, ceilings bind deep.** With budgets, an
   externally imposed ÷1.94 FPS drop (29.1 → 15.0) slows fill only ×1.53 (11.30 → 17.30 s) — the
   rate quota compensating (legacy's same-shape drop is fully proportional: ×2.5 fill for ÷2.6 FPS,
   L3→L6). At the 5-cap the ms ceilings bind before the quota (identical 259 frames-to-fill in
   L2/L5 — per-frame work has gone ceiling-constant) and scaling reverts to proportional.
   **Known limitation:** the absolute-ms ceilings are conservative on very long frames — a 200 ms
   frame could afford more than 26 ms of pipeline. A frame-fraction ceiling is the natural future
   refinement if deep-low-FPS devices matter.
4. **Legacy out-fills budgets at deep caps** (16.78 vs 51.71 s at 5 FPS) for the same reason it
   hitches: unbounded per-frame work. At 5 FPS the frame is ruined either way; this corner is not
   the shipping regime.
5. **Panic gate.** Functional across every ON leg (3–4 close/reopen cycles per fill, hysteresis
   band 256→128 behaving exactly as the suite pins it) and zero-overhead when open. Separately
   witnessed in the acceptance session: 16 cycles over startup + 4 far teleports, backlog drained
   each time, and a 729/729 hole audit (0 missing / 0 unpopulated / 0 stuck / 0 without visuals) at
   a revisited area. Steady-state signal calibration: ready = 0, waiting = 104 ≈ the parked
   frontier ring (8×LoadDistance+4 = 108 predicted) — confirming ReadyCount (not ready+waiting) as
   the signal and leaving comfortable headroom under the 256 close threshold.

## Verdict details

**GO (screening), shipping default-ON with rollback flags** (`enablePipelineTimeBudgets`,
`enableGenerationPanicGate`) — the house pattern; the flags double as the A/B lever used here.
Grounds: the frame-level result is decisive for the shipped default's purpose (streaming that does
not destroy frame time — the §3 death-spiral fix), the fill-latency cost is known, bounded, and
knob-tunable, and the §3.4 constancy goal is met in the regime that matters (mid-band external FPS
drops) with the deep-cap ceiling limitation documented rather than hidden. Editor-Mono caveat
stands: these are screening numbers — an IL2CPP Development Build capture of the same A/B (the
flags ship in the build) should confirm the frame-health delta before the rollback flags are
retired in a future cleanup pass. Ceiling/threshold defaults (8/6/6/4/2 ms, 256/128) ship as
Settings fields, not constants, so per-device tuning needs no code change.
