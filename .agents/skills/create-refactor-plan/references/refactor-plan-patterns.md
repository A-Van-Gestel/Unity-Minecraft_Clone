# Refactor-Plan Patterns — heuristics, census shapes, phase-packet template

Companion to `create-refactor-plan/SKILL.md`. Everything here is distilled from the 2026-07-06
pipeline trilogy (LP-*/MP-*/CP-*); the shapes are stable, the examples are illustrative.

---

## 1. Finding-hunting heuristics (what to actively look for)

Neither silent-by-construction defects (1–13) nor structural smells (14–17) surface from
linear reading — probe for these patterns:

1. **Fire-and-forget async with no failure path.** `_ = SomeAsync(...)` / `Task.ContinueWith`
   log-only: what state is left stuck if it faults? Who clears the in-progress flag on the
   exception path? (CP F1: `IsLoading` stuck forever → neighbor stall.)
2. **Bookkeeping cleared at fire time, not success time.** Removing from a dirty/modified set
   before the async operation confirms. (CP F5: `ModifiedChunks.Remove` before save success →
   silent edit loss.)
3. **Return values that lie to the caller.** A guard arm returning "success" for a state it
   didn't handle, where the caller's dequeue/cleanup then destroys the retry. (MP F1:
   in-flight → `return true` → request dequeued and dropped against a stale snapshot.)
4. **Zero-observable-window state.** A flag set and cleared within one main-thread pass while
   all readers run in other passes — likely dead, but *prove with instrumentation before
   deleting*. (LP F1: `IsAwaitingMainThreadProcess`.)
5. **Hand-mirrored copies of since-extracted shared logic.** Startup coroutines, editor
   harnesses, and secondary paths that predate a shared-decision extraction. (LP F2: the
   startup coroutine's inlined scan arms.)
6. **Convention-only invariants.** "Every X site also does Y" enforced by nothing — enumerate
   the sites, verify each, propose a probe or structural funnel. (LP F6: work-queue entry ⇒
   chunk flagged; MP F8: every dropped request has a later re-request.)
7. **Comment/code mismatches on load-bearing math.** (CP F4: pool target comment says area,
   code computes width — 25× apart.)
8. **Duplicated loops/blocks with identical predicates** inside one function, and duplicated
   constants across files. (LP F3 gate loops; CP F8 `ChunkHeight` twins.)
9. **Naming asymmetries bridged by hand-written wiring tables.** (MP F6: Back/Front/Left/Right
   vs compass N/S/E/W, 16-line mapping.)
10. **Vestigial stages after a refactor moved their work elsewhere** — names that describe what
    the stage *used to* do, plus lifecycle holes (pooled/recycled references surviving in
    queues whose guards only check destruction). (MP F4: `ChunksToDraw`/`CreateMesh`.)
11. **Coverage-map gaps**: build the stage table (§2.1) and mark per-stage suite coverage —
    the ❌ rows between well-guarded stages are where plans earn their keep.
12. **Pool contract edges**: every rent has exactly one return on *every* path incl. faults;
    recycle resets every transient (cross-check `.agents/rules/pool-reset-safety.md`).
13. **Serialization boundary**: what actually crosses the disk (often less than assumed —
    LP: only one of five flags was persisted, making "zero on-disk change by construction"
    possible). Check `[NonSerialized]`, the serializer's write/read methods, and migrations.
14. **God-class residents ready for extraction.** A cohesive subsystem living inside an
    orchestrator god class (`World`, `WorldJobManager` are the standing examples): its own
    state cluster (fields only its methods touch), its own vocabulary, and callers that never
    need the host. Score candidates by *separation-of-concerns gain* (a dedicated
    manager/controller/helper with a nameable single responsibility) and *testability gain*
    (independently drivable by a suite or harness — a bonus, not a gate), with precedent as the
    guide (`LightWorkScheduler`, `MeshBuildQueue`, `ChunkPoolManager`, `PlacementController`
    were all such extractions). Two guardrails: (a) prefer extracting the pure *decision* first
    (the `LightingScanDecision` pattern) and the stateful manager second — the decision
    extraction is low-risk and often delivers most of the testability win; (b) an extraction
    phase is a wide-touch mechanical move, so it gets its own behavior-preserving phase
    (executed later via `refactor-safely`), never rides along inside another phase. The weak
    case to name honestly (rank low, don't drop) is line-count alone — no cohesive state, no
    clear seam, no clarity story beyond "the file is big"; say so in the finding and let the
    value-vs-risk ranking sort it.
15. **Second-sibling twins → consolidate instead of extracting again (the inverse of #14).**
    When a planned extraction would create the *second near-identical sibling* of something
    that already exists (a parallel pass skeleton, a twin decision class, a mirrored helper),
    prefer generalizing the existing home — neutral rename via `refactor-safely`, existing
    call sites migrate mechanically — over minting a parallel copy that then drifts.
    (Precedent: MP-4 generalizes the already-`TKey`-generic `LightingCompletionPass` into a
    shared `JobCompletionPass` rather than writing a mesh twin.) The same applies to helper
    sprawl in reverse: several tiny single-use helpers that always change together may merge
    into one combined home. Direction of travel is *cohesion*, not file count — and guardrail
    (b) from #14 applies: consolidation is its own behavior-preserving phase, with every
    affected system's suite in the regression gate.
16. **Duplicated orchestration logic wanting a shared seam (interface / base / struct-generic).**
    Two systems hand-rolling the same control-flow block (fault-isolation loops, gate
    evaluation, persist/replay stores) → extract one skeleton and drive it through an
    interface with **cached implementer objects, never delegate closures** (per-frame closures
    allocate — the no-GC hot-path rule; precedent: `ILightingCompletionDriver<TKey>` was
    deliberately interface-driven for exactly this). Two nuances from precedent: a class
    cannot implement the same generic interface twice with one type argument — a second pass
    on the same manager needs a small cached adapter object (MP-4's note); and a seam can
    *make a failure mode unrepresentable in tests* (`IPendingLightStore`'s in-memory mode
    makes disk I/O impossible by construction in the harness) — call that out as the
    testability gain when it applies. For Burst-adjacent code, prefer struct generics
    (`IBlockObstruction` pattern) over interfaces.
17. **The validation suites and editor tools are analysis targets too.** The harness is part
    of the system — audit it with the same eyes: (a) *overlapping baselines* asserting the
    same property on the same geometry → consolidate to representatives, keeping each distinct
    axis (precedent: the lighting Bug-09 fleet, 15 → 2 + kept axes, tabulated in the fidelity
    doc's redundancy section — retired baseline numbers stay unused so history stays valid);
    (b) *harness reimplementations of production logic* → re-route through shared code (the
    fidelity docs' A/B-finding pattern — every hand-mirrored block is a false-pass surface);
    (c) *assertions without positive controls / prove-red on record* → a vacuous-pass risk,
    file it; (d) suite-file organization (partial-class-per-concern, registration hygiene) and
    editor-tool lifecycle leaks (the `editor-tool` skill owns the rules — cross-check, don't
    restate). Suite-only findings become suite-only phases: harness-green is their whole
    verification, no in-game step needed.

## 2. Census table shapes (pick what fits the system)

- **2.1 Stage map**: `# | Stage | Code (entry points + anchors) | Suite coverage today` — one
  row per pipeline stage; the coverage column drives the testability story.
- **2.2 State/flag inventory**: `State | Storage | Serialized? | Callback | Set by (sites) |
  Cleared by` — exhaustive via grep, one row per flag/counter; include off-object state that
  co-encodes the machine (job dicts, scheduler sets, queues).
- **2.3 Transition census**: `# | Trigger (site) | Effect` — numbered rows a later
  transition-API phase maps 1:1; enumerate legal state *combinations* the code actually
  reaches (this is what kills naive exclusive-enum proposals).
- **2.4 Request/call-site census**: `Trigger | Site | Priority/flavor` — every producer of a
  queue/event, so a routing change is checkable against ground truth.
- **2.5 Gate table**: per gate, checks-per-neighbor/consumer + who calls it + notes.
- **Findings table**: `# | Finding (verified, anchored, one paragraph) | Addressed by (phase)`.

## 3. Phase-packet template (every phase carries ALL of this)

```
### <ID-n> — <Name> (<🟢|🟡|🔴>, <no behavior change | behavior change — the Fx fix | evidence-gated>)

- Precondition (if evidence-gated): which probe result unlocks it; what a contrary result does
  (STOP + record, demote to doc-note, re-plan).
- Scope: exact files + symbols + anchors; what the change is, mechanically; what it explicitly
  does NOT touch.
- Ordering: dependencies on other phases (and on sibling plans' phases — name the coordination
  rule, e.g. "either order, the suites arbitrate").
- Prove-red: the sabotage or scenario that must go red (and ONLY the expected baselines), then
  green on restore. New baselines: numbering claimed against the current suite tip; positive
  controls so nothing passes vacuously. Deletions have no prove-red — say so and lean on the
  regression gate.
- Acceptance / regression gate: the universal gate (named suites, both dotnet builds, the
  stale-editor-code recompile gotcha) + per-phase extras + in-game confirmation for anything
  behavior-changing (fault injection for failure-path fixes, soak/smoke for streaming paths).
  User sign-off note for user-visible changes (visuals, pacing).
- Testability gain: what becomes unit-testable / harness-drivable / observable that wasn't.
- Doc-sync (same commit): exact Architecture/Design sections, rules files, skill references,
  and fidelity-doc entries to update.
- Serialization: "none" or the exact mapping/migration statement + tripwire.
```

Plus, doc-level:

- **Probe phase** (first, 🟢): `[Conditional("UNITY_EDITOR")]`+`DEVELOPMENT_BUILD` counters and
  warn-once logs; results recorded as dated **Amended** lines in the doc; later phases cite them.
- **Universal regression gate stated once** in §"Phased implementation plan" preamble.
- **Effort marks** 🟢/🟡/🔴; a "minimal standalone-value set" line; extension roadmap for v2+.
- **Open questions**: only genuinely-open-at-commit items, each naming what resolves it and
  where the answer lands.

## 4. Close-out checklist

- [ ] New doc's relationship list links every constraining/constrained doc with a one-line "how".
- [ ] Owning docs of executed items gained an "execution packet: phase <ID-n>" pointer.
- [ ] Coverage/roadmap docs gained groundwork notes (which phases seed which suite families).
- [ ] Memory file (`project` type): doc path + commit, headline findings/decisions with
  "don't re-litigate", phase list, "0/N executed", `[[links]]` to sibling plan memories.
- [ ] `MEMORY.md` index line added.
- [ ] Commit message offered (`Docs: Add <DOC> (<key contents, ' + '-separated>)`), not committed.
- [ ] No production code in the working tree.
