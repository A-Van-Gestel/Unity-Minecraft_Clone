---
name: run-validation-suite
description: How to RUN the editor validation suites (one, a subset, or all via "Validate All"/headless CI) and how to READ their console + NUnit3 XML output. Use when the user asks to "run the validation suite(s)", "validate the engine/lighting/meshing/etc.", "run Validate All", "run the regression suites", check a change didn't regress, run suites in batch/headless/CI, or asks what a suite's PASS/FAIL/Inconclusive/"fix candidate"/"isolation violation" output means. For WRITING new suites/scenarios or fixing a documented bug through a suite, use validation-driven-bugfix instead; for live-editor CLI mechanics see unity-editor.
---

# Running & reading the validation suites

This skill owns **executing** the editor validation suites and **interpreting** what they report.
The suites are Burst-era regression guards under `Assets/Editor/Validation/`, built on the shared
`Framework/ValidationSuiteRunner` (VS-1) with a `Validate All` aggregate + headless/agent entry
point (VS-2).

Neighboring concerns owned elsewhere — stated here so the seam is explicit:
- **Building a new suite, adding a scenario, or fixing a documented bug test-first** → the
  `validation-driven-bugfix` skill (red→green→promote→archive lifecycle).
- **Live-editor CLI mechanics** (`unity command eval`, console reads, menu execution, detached
  jobs) → the `unity-editor` skill; this skill only names the recipes it needs.
- **Coverage gaps / known blind spots of a specific suite** → that suite's
  `*_VALIDATION_HARNESS_FIDELITY.md` under `Documentation/Architecture/Testing Framework/`.

## When to use / when to skip

Use it whenever you need to run a suite and act on the result — after a cross-cutting change
(`ChunkData`, pooling, a `Helpers/` refactor), before a PR, or when the user asks what some suite
output means. Skip it for pure doc/comment edits (nothing to validate) and for authoring new
scenarios (that's `validation-driven-bugfix`).

## Step 1 — Make sure you are running CURRENT code (do not skip)

A green suite on **stale** code launders a regression. `dotnet build` alone does **not** recompile
the running editor domain, and a newly-created `.cs` file is not in the suite until Unity imports
it. Before trusting any suite run after an edit:

1. `unity recompile --format json` — compiles the running Editor's assemblies, new files included,
   and must come back clean (exit `0`).
2. Wait for the domain reload that loads them: poll `unity command editor_status --result-only`
   until it reports `"status": "ready"`. `recompile` returns **before** that reload.
3. Only then run the suite. (There is no automatic stale-assembly guard yet — that is the open
   VS-3 item.)

## Step 2 — Run

The authoritative list of suites the aggregate runs is `ValidationSuiteRegistry.Suites`; the
`Validate All` menu runs exactly those, and `ExpectedSuiteCount` is the floor the runner asserts
against. **Read the registry rather than trusting any list, here or elsewhere** — it is one line per
suite and it is the only place that cannot go stale.

Standard inventory, in registry run/report order, which is also the display-name spelling
`RunSelected` expects (copied from the registry; the registry wins if they differ):

**Lighting Engine · Meshing · Behavior · Placement · Physics Solver · Voxel Occlusion · Mesh Build
Queue · Light Work Scheduler · Chunk Math · Chunk Unload Decision · Pool Prune Decision · Pipeline
Backpressure · Chunk Pipeline · Save Durability · Deserialization Robustness · Serialization
Round-Trip · Migration Chain · Spawn · Command Console · World Clock · Sky & Celestial · Sky Render ·
UI Band Layers · UI Blur Render · Underwater Render · Cloud Render · Worm Carver · Biome Selection ·
Sound Engine · Validation Framework**

Each has a `Minecraft Clone/Dev/Validate <name>` menu item, plus the aggregate **Validate All** —
**with two where the menu path is NOT the display name**: `Voxel Occlusion` → *Validate Occlusion*,
and `Sky & Celestial` → *Validate Sky*. Use the display name for `RunSelected`, the menu path for
`unity command menu --path`; crossing them fails (an unknown subset name rejects the whole request).

Not in the aggregate (run individually): the nightly fuzz deep-runs (`Validate Lighting Engine
(Border Height Fuzz)`, `(Bug 09 Geometry Fuzz)`, `(Bug 05 Canopy Fuzz)`,
`(Interrupted Reconciliation Fuzz)`), `Validate Fluid Parallel Determinism (Cross-Chunk Halo,
Y-band)`, and the standalone `Validate Voxel Metadata Utility` / `Validate FastNoiseLite`.

| Goal | Menu (human) | Programmatic, in-editor (agent, no exit) |
|------|--------------|------------------------------------------|
| **All standard suites** | `Minecraft Clone/Dev/Validate All` | `ValidationSuiteAggregateRunner.Run(true)` → `AggregateRunResult`, or `ValidationSuiteCI.RunSelected(null, true)` |
| **One suite** | its `Validate <Suite>` item | `ValidationSuiteCI.RunSelected("Meshing", true)`, or the suite's own `Execute(true, true)` → `ValidationRunResult` |
| **A subset** | (click each) | `ValidationSuiteCI.RunSelected("Lighting Engine,Meshing", true)` |

Subset names are the **display names** (case-insensitive), comma-separated; a single unknown name
**rejects the whole request** (returns null + logs the known names) so a typo can't silently run a
smaller set. Output order is registry order regardless of request order.

**Agent recipe (Unity CLI).** Two ways:
- `unity command menu --path "Minecraft Clone/Dev/Validate …"`, then read the summary with
  `unity command console --tail <n> --result-only`.
- `unity command eval` calling `Editor.Validation.Framework.ValidationSuiteCI.RunSelected("…", true)`
  and returning the counts from the `AggregateRunResult` (`SuiteCount`, `BaselinePassed`,
  `BaselineFailed`, `BugsReproduced`, …). `eval`'s `success` reflects compile + execution only;
  suite warnings (known-bug repros, `B7 INCONCLUSIVE`) do not flip it.

**Runtime budget.** A full pass takes about **3.5 minutes** (last measured 2026-09-27: 3 min 28 s to
3 min 37 s), and **Lighting alone is almost all of it** (3 min 24 s of that run); the other suites
together take seconds. That is far past a command's 30 s default timeout, so run it detached:

| Want | Do |
|---|---|
| The full aggregate, agent-driven | `unity command menu --path "Minecraft Clone/Dev/Validate All" --detach`, then bounded `unity job wait <jobId> --timeout 90` calls |
| The full aggregate, by hand | `Minecraft Clone/Dev/Validate All` from the Editor menu |
| A fast agent-side sweep | `eval` → `RunSelected(...)` over the registry **without `"Lighting Engine"`** (seconds) |

**Detached recipe:** `unity command clear_console`, fire the menu item with `--detach` (the reply
carries a `jobId`), then call `unity job wait <jobId> --timeout 90` as separate shell calls until one
returns the result (each bounded one that runs out exits `6` with *"The job keeps running"*; see the
`unity-editor` recipes), then read the combined summary with `unity command console --tail 400
--result-only`. A detached job neither blocks the shell nor re-runs.

**Do not schedule long work from `eval` via `EditorApplication.delayCall`.** The call returns
`success: true`, and the queued delegate then does not run on any predictable schedule. Observed
2026-08-25: no output for **70 minutes** on an idle editor, and it then fired unprompted when an
unrelated `RequestScriptCompilation` pumped the editor, **executing the pre-edit assembly** and
interleaving with a menu-item run issued in the meantime. You cannot tell "queued" from "finished",
and a forgotten delegate can wake up later and run **stale code** whose output looks current. Use a
menu item with `--detach`; if a task has no menu item, add one rather than scheduling it.

**Batch / headless / CI.** `ValidationSuiteCI.RunHeadless` is the `-executeMethod` target:

```
Unity -batchmode -projectPath <path> \
  -executeMethod Editor.Validation.Framework.ValidationSuiteCI.RunHeadless \
  [-validationSuites "Lighting Engine,Meshing"] [-nunitXml <path>]
```

It runs the selected suites (all by default), writes NUnit3 XML (default
`TestResults/validation-results.xml`, gitignored), and **exits 0 only when every baseline passed and
no suite ran nothing, else 1**. Do **not** pass `-quit` (it exits itself), and **never** call
`RunHeadless` from `unity command eval` in a live editor — `EditorApplication.Exit` would quit the
editor. (Batchmode also needs Unity license activation on the runner.)

## Step 3 — Read the console output

Every scenario is one of two categories, and they mean opposite things:

- **Baseline** (regression guard) — **must** pass. `[PASS] <name> (time)` green;
  `[FAIL] <name> (time)` red = a **regression**.
- **Known-bug** scenario — reproduces a *documented* bug/feature test-first, so it is **expected to
  fail** and does **not** fail the suite: `⚠️ <name>: reproduces <Bug id> (expected …)`. When one
  starts **passing** it prints cyan `✅ <name>: known-bug scenario PASSES — <Bug id> may be
  fixed/implemented …` — a **fix/implementation candidate**, not a pass to celebrate blindly.

Per-suite summary line: green `ALL N … BASELINE TESTS PASSED` or red
`M OF T … BASELINE TESTS FAILED — REGRESSION`, followed by a `Failed baselines (M):` recap and a
`Slowest 3 scenario(s)` list (a scenario drifting pathologically slow shows up here).

`Validate All` adds a combined block: `=== Validate All — combined summary ===` with one
`✅/❌/⚠️ <Suite>: P/T baselines …` line per suite, then a single verdict —
`VALIDATE ALL: all N baselines across S suites PASSED` (green) /
`VALIDATE ALL: REGRESSION — …` (red) / a yellow *ran-nothing* warning.

`ISOLATION VIOLATION: '<suite>' left World.Instance mutated …` means that suite leaked
process-global state; the runner force-restored it (protecting the next suite) and marked that
suite failed+untrusted. Treat it as a real bug in that suite's teardown, not a flake.

## Step 4 — Read the XML results file

Only produced by the headless/batch path (`RunHeadless`). It is an NUnit3 `test-run` document; the
run/suite `result` + `passed/failed/inconclusive` roll-ups are the fast signal, `<failure>` and
`<reason>` children carry the detail. Full anatomy, attribute table, sample document, and the
scenario→test-case mapping: [references/nunit-xml-output.md](references/nunit-xml-output.md).

## Step 5 — Interpret & act

| Signal (console / XML) | Meaning | Action |
|------------------------|---------|--------|
| `failed == 0`, no ran-nothing | Clean regression pass | Proceed. |
| `[FAIL]` baseline / `result="Failed"` / red REGRESSION | A regression | Read the `<failure>`/`[FAIL]` detail; fix the code or revert. A baseline is not allowed to fail. |
| `⚠️ reproduces <Bug>` / `result="Inconclusive"` | Known bug still open — **expected** | None; it is not a suite failure. Rising inconclusive count over time is fine. |
| cyan `known-bug … PASSES` / `label="FixCandidate"` | A documented bug may now be fixed/implemented | Confirm in-game, then hand off to `validation-driven-bugfix` (verify → promote to baseline → `archive-fixed-bug`). |
| yellow *ran-nothing* / `AnySuiteRanNothing` | A suite registered 0 scenarios (dropped registration, unbuilt partial) | Suspicious — investigate; CI treats it as failure. |
| `ISOLATION VIOLATION` | A suite leaked `World.Instance` | Fix that suite's teardown (it must restore any global it stubs); the run is untrusted until fixed. |

## Constraints

- **Confirm current code first** (Step 1) — a stale green run is worse than no run.
- A **baseline failure is a regression**; an **Inconclusive/known-bug repro is expected**. Never
  invert these when reporting a result.
- **Long runs go through `--detach` + bounded `unity job wait --timeout 90` calls**, never a blocking call.
- Do not call `RunHeadless` in a live editor (it exits the editor); generate the XML through the
  batch path.
- This skill runs and reads suites; it does not author them — route creation/bugfix work to
  `validation-driven-bugfix`.
