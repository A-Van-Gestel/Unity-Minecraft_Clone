---
name: run-validation-suite
description: How to RUN the editor validation suites (one, a subset, or all via "Validate All"/headless CI) and how to READ their console + NUnit3 XML output. Use when the user asks to "run the validation suite(s)", "validate the engine/lighting/meshing/etc.", "run Validate All", "run the regression suites", check a change didn't regress, run suites in batch/headless/CI, or asks what a suite's PASS/FAIL/Inconclusive/"fix candidate"/"isolation violation" output means. For WRITING new suites/scenarios or fixing a documented bug through a suite, use validation-driven-bugfix instead; for live-editor MCP mechanics see unity-mcp.
---

# Running & reading the validation suites

This skill owns **executing** the editor validation suites and **interpreting** what they report.
The suites are Burst-era regression guards under `Assets/Editor/Validation/`, built on the shared
`Framework/ValidationSuiteRunner` (VS-1) with a `Validate All` aggregate + headless/agent entry
point (VS-2).

Neighboring concerns owned elsewhere — stated here so the seam is explicit:
- **Building a new suite, adding a scenario, or fixing a documented bug test-first** → the
  `validation-driven-bugfix` skill (red→green→promote→archive lifecycle).
- **Live-editor MCP tool mechanics** (RunCommand quirks, ReadConsole, menu execution) → the
  `unity-mcp` skill; this skill only names the recipes it needs.
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

1. Trigger a recompile: `AssetDatabase.Refresh()` +
   `UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation()` (via `Unity_RunCommand`
   — note the RunCommand wrapper shadows a bare `CompilationPipeline`, so **fully-qualify** it).
2. Wait until `Unity_ManageEditor → GetState` reports `IsCompiling == false`.
3. Only then run the suite. (There is no automatic stale-assembly guard yet — that is the open
   VS-3 item.) When in doubt, a fresh `Unity_RunCommand` wave is the reliable ground truth.

## Step 2 — Run

The authoritative list of suites the aggregate runs is `ValidationSuiteRegistry.Suites`; the
`Validate All` menu runs exactly those, and `ExpectedSuiteCount` is the floor the runner asserts
against. **Read the registry rather than trusting any list, here or elsewhere** — it is one line per
suite and it is the only place that cannot go stale.

Standard inventory — **24 suites**, in registry run/report order, which is also the display-name
spelling `RunSelected` expects:

**Lighting Engine · Meshing · Behavior · Placement · Physics Solver · Voxel Occlusion · Mesh Build
Queue · Light Work Scheduler · Chunk Math · Chunk Unload Decision · Pool Prune Decision · Pipeline
Backpressure · Save Durability · Deserialization Robustness · Serialization Round-Trip · Migration
Chain · Spawn · Command Console · World Clock · Sky & Celestial · Sky Render · UI Blur Render · Worm
Carver · Validation Framework**

Each has a `Minecraft Clone/Dev/Validate <name>` menu item, plus the aggregate **Validate All** —
**with two where the menu path is NOT the display name**: `Voxel Occlusion` → *Validate Occlusion*,
and `Sky & Celestial` → *Validate Sky*. Use the display name for `RunSelected`, the menu path for
`Unity_ManageMenuItem`; crossing them fails (an unknown subset name rejects the whole request).

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

**Agent recipe (Unity MCP).** Two ways:
- `Unity_ManageMenuItem` to fire a `Minecraft Clone/Dev/Validate …` item, then `Unity_ReadConsole`
  to read the summary.
- `Unity_RunCommand` calling `ValidationSuiteCI.RunSelected("…", true)` (or `…AggregateRunner.Run`)
  and inspecting the returned result object. **Caveat:** RunCommand reports `success:false` whenever
  the run emits *any* `Debug.LogWarning`/`LogError` — and healthy runs do (every known-bug repro is a
  warning; some suites log a `B7 INCONCLUSIVE` zero-alloc note). That is **not** a suite failure —
  read the real verdict from `executionLogs` / the returned counts, not from the tool's success flag.

**⚠️ Runtime budget — never drive `Validate All` through MCP.** A full pass is **~190–205 s**
(last verified run: 204 s), and **Lighting alone is the dominant share — ~182 s when measured**;
the other 23 suites together take ~6 s. Anything sent through
`Unity_RunCommand` that outlives the MCP response window is **re-issued**, and the retries stack on
the Editor's main thread, survive a client-side task stop, and are cleared only by restarting the
Editor — so a single `Validate All` over MCP becomes an endless re-run loop that blocks every later
call. Route it accordingly:

| Want | Do |
|---|---|
| The full aggregate, agent-driven | `Unity_ManageMenuItem` → `Minecraft Clone/Dev/Validate All` (recipe below) |
| The full aggregate, by hand | `Minecraft Clone/Dev/Validate All` from the Editor menu |
| A fast agent-side sweep | `Unity_RunCommand` over the registry **skipping `"Lighting Engine"`** (~6 s, safely inside the window) |

**Menu-item recipe** — the reliable way to drive the full aggregate from an agent. `Unity_ManageMenuItem`
Execute `Minecraft Clone/Dev/Validate All`. The call exceeds 120 s and the harness **moves it to a
background task cleanly** — no stacking, no re-execution — then delivers a completion notification. Do not
`TaskStop` it or re-issue it; just wait, then read the combined summary from the editor log.

**Resolve that log by newest write time** — it is usually `<project>/Logs/Editor.log` but some sessions
write only `%LOCALAPPDATA%\Unity\Editor\Editor.log` (when the project log cannot be opened, the editor
logs that reason at startup and falls back). A frozen log looks exactly like a job that never started. Use
`Get-Content -Tail N` (that file reaches GB scale).

**Do not try to schedule long work off a `Unity_RunCommand` via `EditorApplication.delayCall`.** The call
returns `success: true` and the queued delegate then **never runs** — no output, no marker, no exception,
an idle editor (the dynamic run-command assembly is torn down once `Execute` returns). It is
indistinguishable from a slow run until you give up on it. Route long work through a menu item; if some
task has no menu item, add one rather than scheduling it.

**Batch / headless / CI.** `ValidationSuiteCI.RunHeadless` is the `-executeMethod` target:

```
Unity -batchmode -projectPath <path> \
  -executeMethod Editor.Validation.Framework.ValidationSuiteCI.RunHeadless \
  [-validationSuites "Lighting Engine,Meshing"] [-nunitXml <path>]
```

It runs the selected suites (all by default), writes NUnit3 XML (default
`TestResults/validation-results.xml`, gitignored), and **exits 0 only when every baseline passed and
no suite ran nothing, else 1**. Do **not** pass `-quit` (it exits itself), and **never** call
`RunHeadless` from `Unity_RunCommand` in a live editor — `EditorApplication.Exit` would quit the
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
- RunCommand `success:false` ≠ suite failure — read the counts/logs (Step 2 caveat).
- Do not call `RunHeadless` in a live editor (it exits the editor); file writes are blocked inside
  `Unity_RunCommand`, so generate the XML via the menu-less batch path, not RunCommand.
- This skill runs and reads suites; it does not author them — route creation/bugfix work to
  `validation-driven-bugfix`.
