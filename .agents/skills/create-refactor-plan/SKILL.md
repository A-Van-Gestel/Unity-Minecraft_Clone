---
name: create-refactor-plan
description: Analyze a whole engine system/pipeline for cleanup + refactor opportunities and author an execution-ready, phased design doc (the LP-*/MP-*/CP-* trilogy pattern) — doc+code census at a pinned commit, verified findings table, steelmanned decision sections, and per-phase executor packets (scope, prove-red, suite gates, doc-sync, serialization tripwires) that cold future sessions execute one phase at a time. Use when the user asks for a "clean-up / refactor analysis", an "execution-ready design doc", a "phased implementation plan" for a system, or "do the same for <system X>". Analysis + report ONLY — no production code. Layers on create-design-doc (which owns the doc format); for executing a planned rename/move use refactor-safely; for updating existing docs use docs-sync.
---

# Create Refactor Plan

Turns "clean up / refactor system X" into a self-contained `Documentation/Design/*_REFACTOR.md`
that future cold sessions execute phase-by-phase without re-deriving anything. This skill owns
the **analysis workflow and the phase-packet contract**; `create-design-doc` owns the document
format (header, status taxonomy, footer — invoke it before writing); `refactor-safely` owns
*executing* renames/moves when a phase later runs; `validation-driven-bugfix` takes over when a
defect found here is actually fixed.

Proven precedent: the pipeline-refactor trilogy of 2026-07-06
(`LIGHTING_PIPELINE_STATE_REFACTOR.md`, `MESHING_PIPELINE_ORCHESTRATION_REFACTOR.md`,
`CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md` — find current siblings via
`Glob Documentation/Design/*_REFACTOR.md`).

## When to use / when to skip

- **Use** for a system-scale analysis whose deliverable is a plan: multiple files, an
  orchestration layer, a flag/state surface, a "make X cleaner and more testable" request.
- **Skip** for: a single targeted refactor the user wants done now (just do it, or
  `refactor-safely`); a bug hunt (`voxel-debugging`); a pure performance pass
  (`burst-optimization` / `perf-benchmark` — though findings here may *interlock* with perf
  backlogs, see Step 4); updating an existing plan doc (`docs-sync`).

## Step 1 — Pin the scope contract

- Restate the boundary: which stages/files are IN, and which are owned by sibling plans or
  existing backlogs (OUT). Check for sibling `*_REFACTOR.md` docs and the auto-memory index —
  overlapping scope must be split explicitly, with coordination points named in both docs.
- State the gate up front and honor it: **analysis + report only, no production code, no
  refactor execution.** If the survey uncovers a live defect, it becomes a finding + an
  evidence-gated phase — not a fix in this session.

## Step 2 — Read the ground truth before the code

In order, before any code file:

1. The system's `Documentation/Architecture/` doc(s) — the constraint spine.
2. Its testability surface: the matching `Testing Framework/*_FIDELITY.md` / harness design
   docs and the validation-suite state (what is guarded, what is blind, current baseline tip).
3. Every related `Documentation/Design/` backlog the user names or the architecture docs link
   (performance reports, future designs like scaling/culling/palette docs) — these are the
   item-ID space you must interlock with, and the future seams the plan must keep open.
4. The relevant `.agents/rules/*` and skills (`chunk-lifecycle` etc. — invoke, don't skim, when
   the target has one), plus auto-memory for already-done consolidations.

The point: know what was **already extracted/consolidated** (build ON it — never re-propose it)
and what future designs constrain the plan before forming any opinion.

## Step 3 — Code census at a pinned commit

- Record `git rev-parse --short HEAD` + branch for the doc's **Audited** line.
- Orient with `codegraph_explore`, then switch to targeted `Read`/`Grep` for the census —
  read *regions*, not whole files; grep set/clear/call sites exhaustively for anything you will
  tabulate. Every claim in the doc must be verified in code this session or explicitly labeled
  "executor verifies".
- Pin `File.cs:Lxxx` anchors throughout, and mark them "anchors for the executor, not
  contracts — re-verify before editing".
- Hunt findings with the heuristics checklist in
  [references/refactor-plan-patterns.md](references/refactor-plan-patterns.md) §1 — the trilogy's
  real defects (stuck fire-and-forget flags, lost-update drops, silently-dropped saves) all came
  from those patterns, not from reading linearly.

## Step 4 — Claim an ID prefix; interlock with existing backlogs

- New trackable phases need a fresh 2-letter prefix: `grep -E "\b<XX>-\d" Documentation/` must
  come back empty before claiming it.
- **The interlock rule (hard):** items already tracked elsewhere (perf reports, roadmaps,
  other designs) keep their IDs. A phase may *execute* an existing item — then say so in both
  docs ("execution packet" pointer in the owning doc) — but must never re-propose it under a
  new ID, and perf/backlog items outside the primary goal stay owned by their report
  ("interlock only", named per phase).

## Step 5 — Findings census and decision sections

- Tabulate the current state first (the report's ground-truth sections): stage map with
  per-stage suite coverage, state/flag inventory (storage, set sites, clear sites, serialized?,
  callbacks), request/transition censuses as fits the system — shapes in
  [references/refactor-plan-patterns.md](references/refactor-plan-patterns.md) §2.
- Then a numbered findings table (`F1…Fn`): one-paragraph defect-or-smell statements with
  anchors, each mapped to a phase (or an explicit deferral row). Distinguish actual defects
  from structure/naming smells; defects get evidence-gated fix phases.
- Write decision sections for every pivotal choice, per the `create-design-doc` option pattern:
  steelman the losers, mark `✅ **CHOSEN**` explicitly. The recurring meta-decisions to expect:
  representation collapse (enum vs flags vs transition API), harness-vs-extraction for
  testability, behavior-fix policy for each defect, and execute-now-vs-defer for interlocked
  items.

## Step 6 — Phase packets (the core deliverable)

Rank phases value-vs-risk with the user's stated PRIMARY goal (usually clarity/testability;
perf SECONDARY and measure-first). Every phase must be a complete executor packet — the full
checklist + template is
[references/refactor-plan-patterns.md](references/refactor-plan-patterns.md) §3. Structural
conventions that held across the trilogy:

- **Probe phase first** (🟢, `[Conditional]` editor/dev-only counters + assertions): turns
  convention-only invariants and suspected-dead state into evidence; later phases gate on its
  recorded results ("Amended" lines in the doc).
- **Behavior changes are isolated phases** with their own prove-red baseline, in-game
  confirmation, and — for anything user-visible (visuals, pacing) — an explicit user sign-off
  note in the packet.
- **A universal regression gate stated once** (named suites + both `dotnet build` targets +
  the stale-editor-code gotchas), plus per-phase extras.
- **Serialization tripwire in every phase** ("zero on-disk change; if a phase wants a format
  change, stop — `serialization-migration` + scope change").
- Extension roadmap (v2/v3+) for deferred wishes; Open Questions only for genuinely-open items.

## Step 7 — Author the doc

Invoke `create-design-doc` (system-design species, `Status: Proposed design — not
implemented.`), filename `<SYSTEM>_..._REFACTOR.md`. The opening blockquote leads with the
single most important decision **and** any headline defect found — a reader must get the
verdict without scrolling.

## Step 8 — Close out (and stop)

1. **Cross-link in the same commit, bidirectionally**: relationship list in the new doc, plus
   pointers *from* every doc whose item a phase executes or whose backlog this interlocks
   (owning doc gains an "execution packet" line; coverage/roadmap docs gain groundwork notes).
2. **Memory**: write a `project` memory file (doc path, headline findings/decisions with
   "don't re-litigate", phase list, "0/N executed") + a `MEMORY.md` index line, linking sibling
   plan memories with `[[name]]`.
3. Offer a single-line `Docs: Add …` commit message (never auto-commit).
4. **Stop.** Execution belongs to future sessions running phases from the doc.

## Constraints

- **No production code, ever, in the analysis session** — including "trivial" fixes for defects
  found; they become phases.
- **No re-proposing tracked items** under new IDs (Step 4's interlock rule).
- **No unverified claims** — code anchor or "executor verifies" label on everything.
- **Findings that contradict a documented deliberate choice** (check memory + docs for
  "don't re-suggest" items) are dropped, not re-litigated.
- Phases must preserve documented invariants (gate ordering, flag pairing, pool reset,
  promotion contracts) — consult the system's guard skill (`chunk-lifecycle` etc.) and say so
  in the packets.
