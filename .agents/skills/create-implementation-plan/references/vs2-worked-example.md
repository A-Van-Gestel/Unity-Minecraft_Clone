# Worked Example — the VS-2 Planning Session (2026-07)

Snapshot of the real session this skill was distilled from. An Opus 4.8 session was asked to
analyze the VS-2 finding ("suites are human-in-the-loop only — no aggregate run, no CI entry
point") in `PERFORMANCE_IMPROVEMENTS_REPORT.md` and plan the implementation. The first-pass
plan was competent — but a single follow-up prompt, *"go over the full implementation plan
again with a critical eye"*, surfaced **10 findings**, three of them implementation-breaking.
This skill exists to make that second pass an internal phase of plan creation instead of a
user-prompted rescue.

This file is a stable snapshot: the described code (aggregate runner, registry, XML writer)
shipped afterwards and has since evolved — treat it as pedagogy, not as a map of the current
`Assets/Editor/Validation/Framework/`.

## What pass one got RIGHT (keep doing these)

- Verified environment facts before designing: no `.asmdef` under `Assets/Editor/` (so code
  compiles into `Assembly-CSharp-Editor`), which packages were installed, which suites already
  exposed `Execute()` vs. `void` wrappers.
- Explicit out-of-scope list (void-returning standalone tests + fuzz deep-runs) with the
  auto-join payoff stated.
- Bisectable 4-commit sequence; effort/risk matched against the report's estimate.
- Ended by offering scope adjustment instead of silently starting.

## The 10 findings, mapped to lenses

| # | Finding (condensed) | Lens | Disposition |
|---|---|---|---|
| 1 | Suites share global `World.Instance` via reflection stubs → aggregation creates order-dependency; one mid-suite throw contaminates the next suite; plan had no per-suite try/catch, no isolation guard, and its acceptance gate only compared counts in one order | L1 composition | Mechanical fix (guard + synthetic-failure result + forward/reverse/individual diff gate) |
| 2 | "Wrap the run in one progress bar" — inner runner's `finally` already calls `ClearProgressBar()`, so the outer bar can't survive the first suite | L2 read-before-claim | Taste decision (drop the bar / ambient suppress flag / thread params through 7 signatures) |
| 3 | Aggregate `RanNothing` = *all* suites empty; one suite silently registering zero scenarios still shows green | L3 false-green | Mechanical fix (`AnySuiteRanNothing` + discovered-count floor) |
| 4 | Attribute + TypeCache discovery vs. explicit static list — reflection's failure mode is a *silent drop*, the list's is a compile error; report "asked for" self-registration | L4 taste + L3 | Taste decision |
| 5 | `order: 10/20/30` magic ints scattered across 7 files | L5 conventions | Folded into #4 (list order is the order) |
| 6 | Hand-rolled NUnit3 XML is the fragile step — one wrong roll-up count and CI parsers show 0 tests (false-green at the reporting layer) | L6 fragility | Mechanical fix (writer behind interface + parse-test/self-test gate) |
| 7 | Known-bug repro → `Inconclusive` vs. `Skipped` rendering across CI dashboards | L4 taste | Taste decision (kept Inconclusive) |
| 8 | Deliverable is an entry point, **not** a running CI gate — no pipeline exists; stays latent until someone schedules it | L7 limitations | Limitation, stated in docs |
| 9 | Aggregate covers 7 of ~14 menu items — headline benefit is half-true until the void-returning tests adopt the result object | L7 limitations | Limitation (out-of-scope list existed, the *consequence* wasn't stated) |
| 10 | Batchmode viability asserted without proof ("should work") — menu-run testing doesn't exercise `Exit`, CLI args, or batch asset loading | L6 assumption | ASSUMPTION → real batchmode run added to the verify step |

Also caught in passing: the report's "2 nightly fuzz" was stale — the code had 3 (warm-start
doc-drift lens).

## The decision menu that emerged — and why it matters

Six taste calls went to the user. **The user reversed the recommended option on two** (#2:
chose threading params through all 7 signatures over the recommended "drop the bar"; #4: chose
the explicit list over the report-endorsed attribute discovery) and added a requirement on a
third (#6: unit-test the XML writer, which became the "Validation Framework" self-test suite).
That reversal rate is the empirical argument for the decision menu: silently defaulting taste
calls means shipping a design the user would have rejected, discovered only at review time.

## Shape of the final plan (post-decisions)

Opened with a design summary flagging **where each decision changed the design** ("Registry:
explicit static list — #4/#5; Isolation: snapshot-assert-force-restore — #1; …"), then renumbered
steps each ending in a gate, an isolation acceptance gate (individual vs. forward vs. reversed
must be byte-identical per scenario), an honest "residual risks I'm still watching" section,
and a bisectable commit sequence. It also stated the guarantee's *ceiling* explicitly (the
guard covers `World.Instance`; anything else only the reversed-order diff catches) — stating a
mitigation's boundary is part of the mitigation.
