# Plan Review Lenses

The checklist for the Step 3 adversarial self-review. Run every **core lens** on every plan;
add the **domain packs** matching the task shape. Work worst-first: a lens that finds an
implementation-breaking defect outranks ten style notes. Each hit gets a disposition
(mechanical fix / taste decision / assumption / limitation) per `SKILL.md` Step 3.

Lens examples below cite the VS-2 worked example
([vs2-worked-example.md](vs2-worked-example.md)) as "VS-2: …".

## Core lenses (every plan)

### L1 — Composition & shared state

Triggers whenever the plan runs existing components in a context they never ran in before:
in sequence, in parallel, aggregated, batched, or headless.

- Enumerate the shared **mutable static state** each component touches: singletons, static
  pools/caches, native allocations, editor globals. In this engine the documented coupling hub
  is `World.Instance` — test worlds stub it via reflection and restore on dispose.
- For each shared item, the plan needs: per-component fault isolation (one throw must not
  abort or contaminate the rest), a snapshot–assert–restore guard around the shared state, and
  an **order-permutation acceptance gate** (individual runs vs. combined forward vs. combined
  reversed must produce identical per-item verdicts).
- VS-2: aggregating 7 suites that had only ever run alone; `BehaviorTestWorld` stomps
  `World.Instance` while `LightingTestWorld` asserts it is null — a throw mid-suite would have
  leaked a stub into the next suite and silently changed its verdict.

### L2 — Read-before-claim

- List every existing API or code path the draft makes a *behavioral* claim about
  ("X composes with Y", "Z is re-entrant", "the runner handles that").
- Each claim must be backed by having read that code path this session; otherwise reclassify
  it as **ASSUMPTION** with a verification step.
- VS-2: "wrap the multi-suite run in one progress bar tier" — the inner runner's `finally`
  already calls `ClearProgressBar()`, so the outer bar dies at the first suite. One `Read`
  would have caught it.

### L3 — False-green audit

For every success signal the plan introduces or relies on (exit codes, `Success` flags, green
suites, "tests passed" summaries), name at least one failure mode that still shows green:

- **Vacuous pass** — the thing ran nothing (empty scenario list, dropped registration).
- **Silent drop** — discovery/registration lost an item (reflection typo, un-imported file,
  a count that used to be 7 is now 5 and nothing noticed).
- **Reporting-layer bug** — results mis-rolled into the output format (XML counts wrong →
  parser shows 0 tests → dashboard green).
- **Stale code** — the editor ran the pre-edit assembly (see the stale-domain gotcha in
  `CLAUDE.md`).

Each named false-green gets a gate (count floor, `RanNothing`-per-item check, parse-test of
emitted output, recompile confirmation) or an explicit accepted-risk note. VS-2: aggregate
`RanNothing` was defined as *all* suites empty — one silently-empty suite still showed green.

### L4 — Taste vs. mechanical decisions

Classify **every design decision in the draft**:

- **Mechanical** — one defensible answer given the constraints. Decide it, one-line rationale
  in the plan, done.
- **Taste** — a reasonable user could pick differently. Signals: API shape, discovery mechanism
  (reflective vs. explicit), output format or semantics, naming, where a parameter threads,
  anything the source doc "suggested" rather than required. These go to the decision menu with
  a pros/cons table — never silently defaulted.

Calibration from VS-2: six decisions survived review as taste calls; the user reversed the
recommended option on two of them (progress-bar threading, attribute-discovery vs. explicit
list). "The report asked for it" does not auto-win — surface the alternative anyway.

### L5 — Conventions pass

Diff the draft against `CLAUDE.md` and `Documentation/Guides/CODING_STYLE_GUIDE.md`:

- Magic numbers (VS-2: `order: 10/20/30` ints scattered across 7 files — the exact smell the
  style guide names); const naming; `_camelCase`/`PascalCase`.
- Pooling over `new` in hot paths; no LINQ in hot paths; `BlockIDs` constants, never raw IDs.
- Directory placement per `PROJECT_STRUCTURE.md`; XML docstrings on new public surface.
- Editor-only code → editor assembly → **both** csproj build targets in the gates.

### L6 — Fragility ranking + matched gates

- Name the single step **most likely to be subtly wrong** (hand-rolled formats, schema
  emission, reflection plumbing, cross-process behavior). Every plan has one; naming it is the
  review's job, not the implementation's surprise.
- Its verification gate must **exercise the actual failure mode**, not adjacent behavior:
  parse-test emitted XML with a real parser or golden sample (not "read it and it looked
  fine"); actually run batchmode (a menu-item run doesn't exercise `EditorApplication.Exit`,
  CLI args, or batch asset loading); prove-red before green for bug fixes.
- Sweep the remaining "should work" phrases — each is an ASSUMPTION to label and test.
  VS-2: "batchmode viability of the suites is unverified — I asserted it without proof."

### L7 — Limitations & drift

- State what the deliverable does **NOT** do, as consequences in the user's terms — not just an
  out-of-scope list. VS-2: "this is an entry point, not a running CI gate — nothing is
  scheduled anywhere"; "the aggregate covers 7 of ~14 menu items, so the headline claim is
  half-true until the follow-up lands."
- Report doc-vs-code drift found during research (stale counts, renamed items) so `docs-sync`
  can fix the source doc in the same arc.

## Domain packs (add by task shape)

| Task shape | Extra lenses | Owning skill(s) |
|---|---|---|
| Hot-path / runtime perf | Burst compatibility, GC allocs in `Update()`/core loops, pooling, measure-first frame-level GO/NO-GO | `burst-optimization`, `perf-benchmark` |
| Chunk gen → lighting → meshing pipeline | readiness/neighbor gates, deadlock invariants, pool recycle path, state-flag pairing | `chunk-lifecycle` |
| Anything that ends up on disk | format version bump, migration path, "zero on-disk change unless the plan says otherwise" tripwire | `serialization-migration` |
| Editor tooling / windows / menu items | lifecycle cleanup (textures/meshes/materials), domain reload survival, batchmode viability, stale-compiled-code gotcha | `editor-tool`, `unity-mcp` (recipes) |
| Documented bug fix | deterministic prove-red repro before the fix, baseline promotion after in-game confirmation | `validation-driven-bugfix` |
| Warm start from a doc/report | doc-vs-code drift check on every asserted count/name/API (see `SKILL.md` Step 0) | `docs-sync` (for the corrections) |
| Failure-path / durability / retry-replay change | full pack below (no owning skill) | — |

Packs route to their owning skills for the actual rules — this table exists so the review
*selects* the right packs, not to duplicate their content.

### Pack: failure-path / durability / retry-replay changes

Triggers when the plan gives an operation a failure contract, adds retry/recovery, or retains
data for later replay (a registry, queue, or pending store). Provenance: CP-6 (2026-07-22) —
a solid plan still needed **three** post-implementation review rounds, and every finding traces
to one of these four checks skipped at plan time (worked record: the three Amended blocks in
`Documentation/Design/CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md` §7 CP-6).

1. **Enumerate the full outcome lattice** of the operation, not just "success/failure":
   success / **cancellation** / transient failure / **deterministic failure** — and give every
   cell an explicit disposition in the plan. CP-6: the unplanned `Canceled` cell was a silent
   quit-time data-loss hole; the unplanned deterministic cell was an infinite retry loop.
2. **Draw the path × lifecycle-exit matrix**: every code path that performs the operation
   (sync AND async arms, retry arm) × every lifecycle moment the mechanism must survive
   (normal play, quit, force-unload/world-switch, owner `Dispose`/swap, reload of the same
   key). Each cell is either covered or an explicit accepted limitation. CP-6's rounds 2–3
   were almost entirely uncovered cells (sync arm, Dispose discard, quit-flush ordering).
3. **State the freshness invariant** the moment stale copies are retained for replay:
   "a retained copy must never overwrite newer data" — then audit EVERY write ordering
   against it (replay vs. live save, flush vs. per-item saves, out-of-order completion of
   overlapping operations). Prefer a freshness stamp taken at data-capture time over any
   queue/arrival-order reasoning. CP-6: three separate bugs were this one unstated invariant.
4. **Audit existing guard clauses** of any method the plan adds responsibilities to — an
   early return written for the old contract can silently gate the new work. CP-6: a
   `Count == 0` early return skipped the new quit flush entirely, re-opening the fixed hole.
