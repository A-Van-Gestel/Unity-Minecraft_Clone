---
name: validation-driven-bugfix
description: Test-driven workflow for fixing documented engine bugs using an editor validation suite — deterministic repro scenarios, oracle comparison, baseline regression guards, and the red→green→promote→archive lifecycle. Use when fixing any bug documented in Documentation/Bugs/ for a system that has (or warrants) a validation suite, or when the user asks to "add a repro", "validate the engine", or "promote a scenario".
---

# Validation-Driven Bugfix Protocol

Fix documented engine bugs test-first: encode the bug as a deterministic, expected-to-fail scenario in an editor validation suite, then fix until it flips green while every baseline stays green. This turns timing-dependent, in-game-only artifacts (flicker, races, cross-chunk corruption) into assertions that run headlessly via a menu item.

This protocol is **system-agnostic** within this project. The lighting suite (`Assets/Editor/Validation/Lighting/`) is the reference implementation — concrete file/API pointers live in `references/lighting-suite.md`. The meshing suite (`Assets/Editor/Validation/Meshing/`, `references/meshing-suite.md`) is a second worked example with a different emphasis: a **performance-refactor regression guard** (oracle = current pre-optimization ground truth) rather than bug reproduction — copy it when guarding an output-preserving optimization. When building a suite
for a new system (fluids, chunk pipeline, …), follow `references/building-a-new-suite.md`.

## Relationship to other skills

- `voxel-debugging` **precedes** this skill: it diagnoses and locates the root cause. Once the bug is understood (or already documented in `Documentation/Bugs/`), switch here for the fix.
- `archive-fixed-bug` **ends** this skill: after in-game confirmation, the bug entry moves to `_FIXED_BUGS.md`.
- `docs-sync` runs alongside: bug entries get repro-scenario pointers; fixes update the entry's Status.
- `chunk-lifecycle` / `serialization-migration` constraints still apply to the fix itself (e.g. a fix that changes a serialized struct needs a migration step — prefer format-neutral alternatives).

## The scenario taxonomy (core convention)

Every suite has exactly two scenario categories with **opposite semantics**:

| Category                                           | Asserts                           | Today          | A failure means                              |
|----------------------------------------------------|-----------------------------------|----------------|----------------------------------------------|
| **Baseline** (`B<n>`)                              | Behavior that works correctly     | GREEN          | Regression — suite is red, fix is wrong      |
| **Known-bug** (`K<nn><x>`, tagged with its bug ID) | The CORRECT behavior per the spec | RED (expected) | Nothing — it *reproduces* the documented bug |

Known-bug scenarios are **not** "tests that pass when the bug exists". They assert what *should* happen, so the moment a fix lands they flip green with zero test edits — and the runner flags them as fix candidates. Never write a scenario that asserts buggy behavior.

## The lifecycle

1. **Repro first.** Before touching engine code, write a `K`-scenario that reproduces the documented bug and fails *for the documented reason*. Verify the failure diff matches the bug entry's symptoms — a scenario that fails for a different reason has found a different bug (document it!) or has a setup error.
    - If the repro **won't reproduce** (engine converges correctly), do NOT ship it as a known-bug scenario — that would falsely advertise the bug as fixed later. Promote it to a baseline guarding that behavior, and note in the bug entry that a faithful repro is still TODO.
2. **Plant tripwires.** If the bug analysis predicts a naive fix would break something that currently works (e.g. behavior that only works *because* of the bug), encode that as a baseline BEFORE fixing. It must stay green through the fix.
3. **Fix.** Iterate: edit engine code → build → run the suite via its menu item → read the failure diffs. The target `K`-scenarios flip green; ALL baselines stay green. Both runtime and editor assemblies must build (`dotnet build "Assembly-CSharp.csproj"` and `"Assembly-CSharp-Editor.csproj"`).
4. **Update the bug entry** (`Documentation/Bugs/`): Status → "Fixed in code (Month Year) — awaiting in-game confirmation", with a short description of the fix parts and the scenario IDs. Commit the fix + suite changes + doc update together.
5. **WAIT for the user's in-game confirmation.** Suite-green is necessary, not sufficient — the harness mirrors production orchestration but is not the live game (frame timing, throttling, save/load round-trips differ).
6. **Promote.** After confirmation: move the `K`-scenario(s) into the baseline file (renumber `K<nn>` → `B<n>`, update the docstring to note the promotion and fix date). The repro becomes the permanent regression guard.
7. **Archive** via the `archive-fixed-bug` skill. The archived entry should name the guarding baseline scenario.

## Suite architecture rules (apply to any new suite)

- **The harness replays REAL production code.** Run the actual Burst job / system under test; apply cross-boundary effects through the same decision logic production uses. If the orchestration logic is welded to `World`/managers, do a behavior-neutral **extraction refactor first** (pure static helper, own commit) so harness and production share it. A harness that *reimplements* production logic tests a copy — regressions in the real code slip through.
- **The oracle encodes the SPEC, not the implementation.** A naive, obviously-correct solver over the whole problem (no chunking, no jobs, no snapshots) — the engine's converged result must equal it exactly. Validate the oracle against trivial cases the engine already handles before trusting it.
- **Fixtures are synthetic.** A test-local palette/dataset (like seed data in conventional test frameworks), independent of the real `BlockDatabase.asset` — deterministic under database edits and able to express cases the real data doesn't have (e.g. pure-R/G/B lamps). Test-local array indices do not violate the `BlockIDs` rule.
- **Sequential vs concurrent execution test different things.** Sequential convergence can never produce in-flight staleness; races need a Begin/Complete split (snapshot inputs → mutate live state → merge stale result) or wave-parallel rounds (Begin-all, then Complete-all). Convergence-budget exhaustion is the deterministic form of "flickers forever".
- **Failure output must be debuggable from the console**: bounded per-element diffs (position + per-channel expected/actual), in the established `[PASS]`/`[FAIL]` style.

## Pitfalls (all happened; all will recur)

- **The oracle is authoritative; hand-computed probe constants are not.** A spot-probe expectation computed in your head can be wrong while the field is correct (forgetting an opaque occluder changes a straight-line distance into a detour). Prefer oracle-field comparison as the real assertion; treat probes as readable documentation and derive their constants from the oracle.
- **The oracle can share the engine's bug.** When engine and oracle disagree, do not assume the engine is wrong — derive the correct value from first principles / the architecture docs, then fix whichever side is off (and say so in the commit).
- **Sentinel values collide with legitimate data.** Check the value domain before trusting a sentinel (e.g. `ushort.MaxValue` as out-of-bounds collides with a legitimately fully-lit `0xFFFF` voxel). Prefer bounds checks on a channel whose domain cannot collide.
- **Contaminated full-field compares: isolate the invariant.** If a *different* open bug corrupts the full-field result of your scenario, assert only the invariant your bug owns (e.g. a volume scan instead of an oracle compare) and leave a dated note to restore the full assertion once the other bug is fixed.
- **One repro can expose multiple independent defects.** When a diff shows artifacts the bug entry doesn't describe (wrong sign, wrong location), isolate them: write a minimal independent scenario; if it reproduces without the original setup, it's a new bug — document it in `Documentation/Bugs/` before deciding fix order.
- **Don't trust `IsStable`/convergence alone** — a stable-but-wrong field is the static form of the same defect that flickers in another configuration. Always pair convergence assertions with a field/invariant assertion.
- **A green baseline proves nothing until you've seen it red.** After a new or promoted baseline passes, temporarily revert *just the fix* (one line is enough — e.g. neuter the guard with `if (false && …)`), rebuild, and confirm that baseline — **and only it** — goes red, then restore. This proves the scenario actually exercises the mechanism you fixed, not something incidental that would stay green even if the fix regressed. (A K-scenario gets this for free — it was red before the fix — but a baseline added *alongside* a fix, or a new sub-assertion, has
  never been observed failing.)
- **When a faithful scenario is confounded by an orthogonal limitation, test the production decision directly.** If an end-to-end repro can't isolate the unit under test because a *different* open limitation swallows the effect (e.g. a cross-chunk removal that never propagates because of an orphaned-light-loop bug), don't ship a scenario that "passes" for the wrong reason. Instead expose the production decision function through a thin harness affordance and assert it directly with controlled inputs (the real function, synthetic inputs — still "replays
  REAL production code"). Leave a comment on the baseline saying *why* the scenario route was abandoned, so nobody later "simplifies" it back into a broken end-to-end test.

## Runner conventions

- One menu item per suite under `Minecraft Clone/Dev/` (run programmatically via `Unity_ManageMenuItem`; read results via `Unity_ReadConsole` — grep the dump for `[PASS]|[FAIL]|reproduces|PASSES`).
- **Recompile before running the menu item.** `dotnet build` does NOT make the running Editor pick up your edits — the suite will silently run the *previous* build (a brand-new scenario appears "missing", an edited assertion runs its old form). After editing, trigger `CompilationPipeline.RequestScriptCompilation()` via `Unity_RunCommand`, wait for `Unity_ManageEditor → GetState` `IsCompiling == false`, *then* execute the menu item. New `.cs` files also need an `AssetDatabase.Refresh()` first (see CLAUDE.md's new-file gotcha). The fast green/red signal is
  `Unity_ReadConsole` with `Types: ["Error"]` — the runner logs baseline failures (and the regression summary) as `Debug.LogError`, so **0 errors == all baselines green**; `Clear` the console before the run so the dump is only that run.
- **A `Unity_RunCommand` wave is the reliable ground truth — trust it over the menu-item run.** `IsCompiling == false` is necessary but **not sufficient**: the menu suite (`Unity_ManageMenuItem`) has been observed running *stale* engine code for one or more cycles after a runtime-assembly (`Assembly-CSharp`) edit, even with the editor reporting idle — so a fix looks like it "isn't working" / a repro looks unfixed when the code on disk is correct. A `Unity_RunCommand` script **compiles fresh against the currently-loaded assemblies on every invocation**,
  so it reflects the live code deterministically. When a menu result contradicts your analysis, reproduce the scenario inline with a self-contained `Unity_RunCommand` that builds a `LightingTestWorld`, runs the wave, and prints the decisive numbers (e.g. `LightingRunResult.ModsEmitted`/`ModsApplied`, seam voxel sky values, `MatchesOracle`). This both dodges menu staleness *and* lets you instrument internals the menu can't show (mod counts, per-voxel values, even a temporary `FixedString` `Debug.Log` inside the Burst job). A `RunCommand` wave never gives
  a false PASS for a stale build — stale code shows the *old* (failing) result, never a spurious green — so it is safe to trust even before you are certain the recompile landed. Only after the inline wave confirms the fix should you re-run the full menu suite for the baseline-regression sweep (clearing the console first). To force the editor to actually reload after stubborn staleness: `AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate)` + `RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache)`, then wait for
  `IsCompiling == false`.
- Scenarios are `Func<bool>` registered via partial methods (`AddBaselineScenarios` / `AddKnownBugScenarios`); each runs in try/catch so one exception cannot abort the suite.
- Summary semantics: baseline failures make the suite RED (regression); known-bug failures log a ⚠️ "reproduces Bug NN (expected)"; known-bug passes log a cyan ✅ "fix candidate — verify in-game, then archive".
