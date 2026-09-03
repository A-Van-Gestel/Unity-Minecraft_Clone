# Lean production build settings — IL2CPP **Master** A/B (MethodOnly stacktraces + Medium stripping + Resources cleanup)

| Field           | Value                                                                                                                                                                                                                                                     |
|-----------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-08-15 22:10:06 (RC 89, *after*) and 22:21:57 (RC 88-2, *before*) — note the after-leg ran **first**                                                                                                                                                   |
| **Branch**      | `feat/world-scaling`                                                                                                                                                                                                                                      |
| **Commit**      | **RC 89:** `36ff3f13-dirty` (the settings changes were uncommitted at build time). **RC 88-2:** not recorded — that build predates the build stamp and its header reads `(player build — record manually)`. Build GUIDs `f3bfb838…` and `731a27ad…`.        |
| **Captured by** | `BenchmarkController` — **IL2CPP Master, Player, Burst on, AOT safety checks off** in *both* legs. Same machine, same settings file (copied across), one session per run, **n = 1 per leg**. i9-9900K / 16 threads / 64 GB / D3D11.                        |
| **Verdict**     | **GO** — on **build time (−49 %, ~15m00s → 7m42s)** and build size, **not** on runtime. Frame-time is **neutral**: the ~1 % average deltas carry mixed signs across phases and are not separable from run-to-run variance at n = 1. **No regression found.** |

> **⚠ Read the two headers carefully before comparing them.** RC 88-2's header says
> `Configuration: Release` and `Safety checks: Enabled`. **Neither is true, and neither is a difference
> from RC 89.** That build carries the pre-2026-08-15 reporting bug in which those two lines were
> hardcoded constants — `Debug.isDebugBuild` cannot see the IL2CPP compiler configuration, and
> `BurstCompiler.Options.EnableBurstSafetyChecks` is editor-only and always reads `true` in a player.
> **Both legs are Master builds with AOT safety checks off.** See the *Provenance is baked, not queried*
> section of [`README.md`](README.md). A future reader comparing these two files would otherwise
> reasonably — and wrongly — conclude the compiler configuration changed between them.

---

## What changed between the legs

RC 89 is RC 88-2 plus exactly three build-configuration changes and the build-stamp provenance fix.
No engine or pipeline code differs.

| # | Change | Setting |
|---|--------|---------|
| 1 | IL2CPP stacktrace detail | `il2cppStacktraceInformation` Standalone `MethodFileLineNumber` → **`MethodOnly`** |
| 2 | Managed stripping | `managedStrippingLevel` Standalone `Low` → **`Medium`**, with new `link.xml` roots for the reflection-driven settings UI and `[Preserve]` on `ResolutionDropdownProvider` |
| 3 | Resources cleanup | 38 atlas source tiles + `AtlasConfiguration.asset` moved out of `Assets/Resources/` (8.9 MB → 90 KB) |

**Attribution between the three is not resolved by this capture.** They shipped together and the
build-time win is reported for the bundle, not apportioned.

---

## Result 1 — build time (the actual win)

| | Before | After | Δ |
|---|---|---|---|
| Master IL2CPP build, Windows | ~15m 00s | **7m 42s** | **−49 %** |

The "before" figure is the user's standing approximate build time rather than a stopwatch measurement,
so treat the percentage as ±1 minute on the numerator. The halving is far outside that error bar.

---

## Result 2 — runtime (neutral)

### Overall summary

| Metric | RC 88-2 (before) | RC 89 (after) | Δ |
|---|---|---|---|
| Total samples | 8 371 | 8 382 | +0.1 % |
| Avg CPU time | 2.2 ms | 2.2 ms | — |
| Avg Wall time | 3.1 ms | 3.1 ms | — |
| Peak CPU time | 52.6 ms | 43.6 ms | −17.1 % |
| Avg Wall FPS | 471.0 | 475.9 | +1.0 % |
| Avg CPU FPS | 994.0 | 999.6 | +0.6 % |
| Avg GC alloc / frame | 38.2 KB | 39.4 KB | +3.1 % |
| Avg Total Memory | 1551.2 MB | 1555.4 MB | +0.3 % |
| Peak Total Memory | 1907.8 MB | 1907.4 MB | — |

### Average wall FPS per phase

| Phase | Before | After | Δ |
|---|---|---|---|
| Gen 10 m/s | 531.6 | 526.6 | −0.9 % |
| Gen 20 m/s | 605.7 | 637.5 | +5.3 % |
| Gen 50 m/s | 457.9 | 457.5 | −0.1 % |
| Gen 100 m/s | 276.2 | 286.1 | +3.6 % |
| Gen 200 m/s | 115.3 | 116.5 | +1.0 % |
| **Gen group** | **408.4** | **415.8** | **+1.8 %** |
| Ensure Generated | 524.7 | 530.1 | +1.0 % |
| Load 50 m/s | 568.3 | 564.4 | −0.7 % |
| Load 100 m/s | 449.8 | 452.2 | +0.5 % |
| Load 200 m/s | 336.9 | 337.7 | +0.2 % |
| **Load group** | **452.6** | **452.4** | **−0.0 %** |

### Minimum wall FPS per phase — the mixed-sign evidence

| Phase | Before | After | Direction |
|---|---|---|---|
| Gen 10 m/s | 136.2 | 170.2 | better |
| Gen 20 m/s | 205.9 | 233.3 | better |
| Gen 50 m/s | 116.2 | 100.8 | **worse** |
| Gen 100 m/s | 74.6 | 69.5 | **worse** |
| Gen 200 m/s | 18.9 | 22.8 | better |
| Load 50 m/s | 146.5 | 168.2 | better |
| Load 100 m/s | 140.2 | 125.3 | **worse** |
| Load 200 m/s | 102.1 | 112.7 | better |

Five better, three worse. Peak CPU behaves the same way (gen 10/20/200 down, gen 50/100 up).
**This is what noise looks like, not what an effect looks like** — an effect would carry a
consistent sign. The headline `Peak CPU −17.1 %` is a single worst frame in the gen-200 phase and is
the noisiest statistic the harness reports; it is recorded, not claimed.

**Why neutrality is the expected result:** none of the three changes touches hot-path code
generation. `MethodOnly` removes per-method sequence-point bookkeeping, which costs build time and
binary size; it pays at runtime only when exceptions are thrown, and this benchmark throws none.
Stripping and the Resources move remove code and assets that were never executed or loaded.

---

## Result 3 — reserved memory fell consistently

The one runtime signal with a clean sign across every phase:

| Phase | Peak Rsvd before | Peak Rsvd after | Avg Rsvd before | Avg Rsvd after |
|---|---|---|---|---|
| Gen 10 m/s | 1616.0 MB | 1606.0 MB | 1599.8 MB | 1574.0 MB |
| Gen 20 m/s | 1600.0 MB | 1574.0 MB | 1600.0 MB | 1548.0 MB |
| Gen 50 m/s | 1600.0 MB | 1510.0 MB | 1557.8 MB | 1480.3 MB |
| Gen 100 m/s | 1536.0 MB | 1478.0 MB | 1499.1 MB | 1435.6 MB |
| Gen 200 m/s | 1568.0 MB | 1542.0 MB | 1471.7 MB | 1441.1 MB |

5/5 phases down, up to −5.6 % on average reserved. Small, but the consistency distinguishes it from
the mixed-sign FPS deltas above.

**The Resources cleanup produced no total-memory change, exactly as predicted.** Unreferenced assets
in a `Resources/` folder are shipped but never loaded, so removing them is a build-size win and not a
runtime one. Peak Total Memory is 1907.8 → 1907.4 MB. This prediction holding is weak corroboration
that the rest of the reasoning about the three changes is sound.

### Watch item — managed heap up ~2–3 %

| Phase | Avg Managed before | after | Δ |
|---|---|---|---|
| Ensure Generated | 473.4 MB | 487.0 MB | +2.9 % |
| Load 50 m/s | 488.6 MB | 499.5 MB | +2.2 % |
| Load 100 m/s | 494.0 MB | 503.9 MB | +2.0 % |
| Load 200 m/s | 496.8 MB | 512.0 MB | +3.1 % |

Four phases, same direction, similar magnitude — more consistent than the FPS noise, and it pairs
with the +3.1 % avg GC alloc. Not actionable and well inside safe margins; the plateau a managed heap
settles at does vary between runs. **Recorded so a second occurrence is recognised as a trend rather
than dismissed as noise.**

---

## Result 4 — pipeline behaviour is unchanged (the safety property)

Every FP regime verdict is identical across the two legs:

| Phase | Verdict (both legs) |
|---|---|
| Gen 10 / 20 / 50 / 100 m/s | `Healthy` |
| Gen 200 m/s | `AdmissionBound + ORDERING-BOUND` |
| Ensure Generated, Transition | `NO REGIME` |
| Load 50 / 100 / 200 m/s | `Healthy + ORDERING-BOUND` |

Delivered chunks (`MeshApplied`) match exactly at gen 10 / 20 / 50 (273 / 735 / 1910), Ensure
(15 193), and load 50 / 100 (2 547 / 4 435); the remainder differ by ≤ 21 out of thousands
(gen 100: 3 864 → 3 843; gen 200: 5 651 → 5 648; load 200: 9 116 → 9 111), which is the expected
coupling between frame rate and a fixed-duration phase. At gen 200, panic-gate closure moved
68.1 % → 71.2 % and waste 35.9 % → 35.2 % — the same regime, not a shifted one.

**This is the result that matters for shipping Medium stripping:** the more aggressive strip changed
no engine behaviour.

---

## Stripping validation

The 486 baselines across 22 suites (`Validate All`) are **edit-mode** and structurally cannot observe
managed stripping, which happens only in a real build. Stripping was validated separately and
manually: the reflection-driven settings menu was exercised in the RC 89 Master build — sliders,
dropdowns and in-game value changes — with **no regressions found**. The `ResolutionDropdownProvider`
path (`Activator.CreateInstance`, rooted by `[Preserve]`) was the specific at-risk case and works.

This is a manual, one-session confirmation, not an automated guard. The failure mode it covers is
silent — a stripped member surfaces as a control that quietly fails to appear — so a future change to
`Settings` / `DevSettings` / `UI.Attributes` or a new `IDropdownProvider` needs the same manual pass.
See `Architecture/DATA_DRIVEN_SETTINGS_UI.md` §9.1.

---

## Limitations

1. **n = 1 per leg.** A ~1 % delta is not resolvable at this sample count. Nothing in the runtime
   tables should be read as an effect.
2. **Run order is not drift-corrected.** RC 89 (after) ran at 22:10, RC 88-2 (before) at 22:21, so the
   *before* leg ran on the warmer machine. That bias favours RC 89, which means the true runtime
   difference is, if anything, even closer to zero than the tables show.
3. **The three changes are not separated.** Build time is reported for the bundle. Apportioning it
   needs one-variable-at-a-time rebuilds, which were not run because the bundle result was sufficient
   to decide.
4. **The `before` build time is approximate** (~15 min, from routine observation rather than a timed run).
5. **RC 88-2's source commit is unrecoverable** — that build predates the stamp. This capture is
   therefore reproducible on the *after* side only, and even there `36ff3f13-dirty` marks a tree with
   uncommitted changes, so the hash alone does not reconstruct the binary.

---

## Verdict

**GO.** Ship the three settings changes. The deliverable is a **49 % build-time reduction** and a
leaner build with **no runtime regression and no behavioural change**. There is no evidence of a
frame-time improvement and none is claimed.

---

## Document History

| Version | Date | Change |
|---------|------|--------|
| 1.0 | 2026-08-15 | Initial capture — A/B of RC 88-2 vs RC 89 lean production build settings. |
