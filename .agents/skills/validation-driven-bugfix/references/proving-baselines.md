# Proving a Baseline: Evidence Rules

A green suite proves the baselines pass, not that they test anything. Every rule below is a way a
suite, a prove-red or a doc claim has passed while the thing it named was broken. Read it before
writing a baseline, running a prove-red, or writing "closed" into a doc.

## 1. Running a prove-red

- **Use the tool:** `python -u Tools/Python/prove_red.py --file <path> --old <text> --new <text> --suite "<menu path>" [--expect <regex>]`.
  It backs up with a checksum, replaces exactly one match, recompiles (a non-compiling mutation
  aborts), runs the suite detached, reads this run's `[FAIL]` lines, and always restores + recompiles.
  Exit 0 = red, 1 = survived, 2 = could not run.
- **By hand, never restore with `git checkout -- <file>`.** The mutated file is usually the one you are
  editing, and the checkout discards the uncommitted fix along with the mutation. Copy every file the
  sweep touches to the scratchpad first, restore from the copy, then confirm `grep -c MUTATION` and
  `git status --short` show nothing left.
- **Confirm the red is the assertion, not a throw.** The runner catches a scenario's exception and
  reports it as failed. Read `ScenarioResult.Exception` (null for a real assertion) and check the
  `[FAIL]` line quotes the measured value. A red with an exception attached proves nothing.
- **Write the `Prove-red:` docstring after the run**, transcribing the mutation that actually went red.
  One written while authoring is a prediction; when the evidence also lives in a fidelity-doc table,
  reconcile both in one step.

## 2. A mutation that stays green — rule these out before blaming the baseline

| Cause                       | Check                                                                                                                                                                                     |
|-----------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| The mutation is a no-op     | A literal nudged below float32 precision (relative ~1.2e-7) rounds to the same value: `struct.unpack('f', struct.pack('f', x))` both sides. Prefer mutating a scale, offset or threshold. |
| A sibling guard swallows it | In `a && b && c`, the fixture must make every other conjunct true, so the mutated term alone decides. Ask which clause is actually rejecting the fixture.                                 |
| No reachable input hits it  | Check what bounds the producer at plan time. A defensive floor no reachable input exercises can stay, but does not earn a baseline.                                                       |
| The fixture samples a range | When the value comes from a hash, seed or range lerp, pin min = max = the boundary under test. Log the value the fixture produced.                                                        |
| The gate cannot observe it  | A check that samples before the write it guards, or runs in a different cwd/env than the code, is blind. Reproduce the code's own cwd/env/argv; run a self-mutating operation twice.       |
| The code is not loaded      | See §6 — a stale or un-reloaded domain reads as a false green.                                                                                                                            |

## 3. Choosing the mutation

- **It must separate the old and new implementation.** Fixing a vacuous assertion needs a *green old /
  red new* pair; a mutation that would also red the old assertion only shows the suite reacts to
  something.
- **Ask what the broken state would report.** If the answer is "also a pass", the probe is invalid.
- **Run every mutation; never record a predicted one.** A clamp that looks enforcing may be redundant
  because the input is bounded upstream.

## 4. Assertions that cannot fail

- **A freshly extracted helper is never asserted against what it wraps** (the original expression, the
  delegate it forwards to, or a sibling sharing its implementation). Both move together. Write the
  expected values by hand from the fixture, then mutate the helper and confirm *that* baseline reds.
- **Re-derived relationships turn identities into algebra.** After inverting or re-deriving a formula,
  re-read every assertion that mentions both sides and ask whether it can still fail; replace one that
  cannot (e.g. inject a rogue input and assert the clamp holds).
- **The `Check()` message is a contract.** A message naming a number, a set or "identical" must be
  tested by its predicate: count the set, hash the bytes, load the artifact. `sections.Length >= 1`
  under "carries 8 sections", or two compile-time constants compared, is decoration.
- **An expected value that encodes a contract with an unexecuted consumer** (a shader, the GPU, a file
  another tool reads) cannot be validated by the suite, even with a passing prove-red — the assertion
  and the mutation come from the same reading of the code. Say so in the docstring and get eyes on the
  real output.
- **Evaluate a new metric against the broken input and read the number.** A "still moves per frame"
  check rose as precision degraded, because quantization makes motion lumpy, not smaller.
- **An oracle can be the less precise side.** Never leave two `float`s adjacent in a double-precision
  oracle; carry precision in `double` locals, not casts (a warnings cleanup removes "redundant" casts).
- **Exactness claims use `ExactValue`, measurements use a tolerance.** `Editor/Validation/Framework/ExactValue`
  (`Equal`/`IsZero`) is for "copied unmodified", "a 0/1 gate", "a reserved channel is exactly 0";
  `Mathf.Approximately` or an epsilon is for anything computed or read back. A tolerance on an
  exactness claim accepts 0.999 and is weaker than the raw `==`. Needs `using Editor.Validation.Framework;`.
- **An intermediate flag cannot catch an ordering bug.** Physics `B41` asserts a jump request is not
  latched and stays green when the jump is armed a tick late; `B43` asserts the outcome (peak == final,
  jump counter unchanged) and reds. Assert the observable result, not a step on the way to it.
- **A fixture at a balance point satisfies a directional assertion by cancellation.** A falling column
  open on four sides yields outward vectors whose mean is exactly zero, so "no horizontal push" passed
  without the fix (the wall-backed fixture discriminates); a fluid at buoyancy 1 cancels gravity, so a
  "body loses the vertical contest" scenario passes for free (`Id.SinkingFluid` exists for this).
- **Derive expected values from the suite's own constants, never from the constant under test.** The
  first Performance Monitor `B32` computed its expected age from `PerfStore.RowFinalAge`, and an
  off-by-one in that constant survived prove-red; it now uses the suite's `BACKFILL_MAX_AGE + 1`.

## 5. Fixtures and harnesses

- **A harness that paraphrases a Unity lifecycle method must be diffed against it step by step.** The
  physics `Tick()` ran "resolve, then move" and silently omitted `FixedUpdate`'s jump application, so
  for a year no jump could fire in the suite. Call the real method (here, the extracted
  `VoxelRigidbody.ApplyPendingJump`) instead of re-describing it.
- **Pooled-collection leaks cannot be asserted.** `UnityEngine.Pool.CollectionPool<,>` (the base of
  `HashSetPool<T>` / `DictionaryPool<,>`) exposes no active/inactive counters; only `ChunkPool`'s
  `ActiveData`/`ActiveSections` support a balance check. A collection-pool leak is a code-review item.
- **Never park throwaway counters on a shared stats class a scenario resets.** Physics `B25` calls
  `PhysicsQueryStats.Reset()` mid-suite, which once turned a shadow run into "0 mismatches over 0
  comparisons". Give instrumentation its own class.

- **Extract the state machine, not just the predicate.** `_wasX` / `_lastX` latches and accumulators
  are where the defects live; a struct-in / struct-out `Advance()` (pattern: `FootfallTracker`) makes
  every transition a test case. "The wiring stays an in-game check" is fine for sampling and playback,
  never for edge detection.
- **Write out the sequence an instrument generates** (a fixture, dev command or fuzz seed) and confirm
  the target state appears in it — ask what ordering its parameters force, not what they permit.
- **A gate proves only the function it exercises.** For tools that extract candidates and then decide,
  assert the candidate COUNT (zero is a hard error) and write one fixture per extractor edge.
- **`PhysicsTestWorld` is one 16×128×16 chunk.** `SetBlock` outside x/z ∈ [0,16), y ∈ [0,128) throws —
  which fakes a prove-red (§1).
- **Edit-mode suites pin the statics they read.** Domain reload happens on entering Play and on
  recompile, never on exiting Play, so a static set during a play session (e.g. `PerfStore.Tier`) is
  still set when a local suite runs. Set it explicitly and restore it in `finally`; headless CI starts
  a fresh process and hides this. `Validate All` can hide it too: Command Console `B37` went red only
  when run alone after a Play session moved `WorldOrigin`, because an earlier suite in the aggregate
  resets the origin first. Reproduce a suspected leak by setting the static (e.g.
  `Helpers.WorldOrigin.SetOrigin(...)` via `eval`) and running that one suite.
- **`job.Run()` needs every container constructed**, including `[ReadOnly]` inputs the tested path
  never indexes: pass zero-length arrays, never `default`.
- **Never name a suite namespace after a type the `Editor.Validation.*` tree references.**
  `Editor.Validation.ChunkMath` shadows `Helpers.ChunkMath` for every suite and breaks the editor
  assembly — which then keeps serving the last-good DLL. Pick a non-colliding leaf (`ChunkMathSuite`).

## 6. Is the code under test actually loaded?

- `StaleAssemblyGuard`'s `STALE-CODE WARNING` preamble is the honest signal; DLL timestamps are a
  heuristic (`AssetDatabase.ImportAsset(…, ForceUpdate)` re-stamps a source without recompiling).
- **Decisive check:** grep the DLL's UTF-16 string table for a literal unique to the edit —
  `python -c "d=open('Library/ScriptAssemblies/Assembly-CSharp.dll','rb').read(); print('LITERAL'.encode('utf-16-le') in d)"`.
  Added literals must be present, reverted ones absent.
- **A rebuilt DLL can still be unloaded:** the domain reload waits while the main thread is busy (a
  long menu-item run). For a pure decision function, call it from `unity command eval` with inputs where
  correct and mutated code differ observably — once to confirm the mutation is live, once after reverting.
- **`git stash` does not prove a commit compiles standalone.** Stashed untracked `.cs` files stay listed
  in the generated `.csproj` (`CS2001` per file), and their contents are not compiled at all.

## 7. Probes whose silence is the deliverable

- **Prove a silent probe live, per branch, by injection** in the same session; a dead probe is as
  silent as a satisfied invariant. Mutate in one call and read in the next.
- **Arm the injection against what the predicate reads** — a neighbor gate inspects the neighbors, so
  dirty a neighbor of the flagged chunk.
- **Instance-field counters on `World` reset when a world reloads.** Count `--- Startup complete ---`
  to learn how many instances a soak had.
- **The first-hit console warning spans instances, if nothing was evicted** — confirm the console
  buffer's oldest entry predates the first world load, and that any hits are your own injections.
- **Restore everything an injection touched** in the user's live world; read each flag before
  overwriting it.

## 8. Writing the claim into a doc

- **Scope "closed" to what a baseline executes.** Finish the sentence *"a regression in X would red
  baseline Y because Y executes Z"*; if no baseline executes Z, the word is "documented". Prefer
  "closed **for** <surface>" and name the residual in the same sentence.
- **An identical before/after suite result is a warning.** First hypothesis: no baseline exercises the
  change. Name the arrangement that separates old from new behavior and check a fixture builds it.
- **A live census is an instant, not an invariant.** "0 chunks in the bad state" in a quiesced world
  shows nothing was mid-transition at that moment.
- **Reconcile "cannot happen" with the design's reachable-state table** in the same edit.
- **A deferral ("latent", "rare") is a reachability claim.** Measure the trigger, not only the cause:
  name the input that reaches it and its real size.
- **Name the property a user would notice failing** before testing; a rigorous test can be aimed at the
  wrong invariant. When the user reports an artifact in output you produced, treat it as located but
  not yet measured, and ask for the one discriminating detail.
- **Re-read headings after editing bodies** — multi-pass edits update the substance and leave the
  headline behind.
