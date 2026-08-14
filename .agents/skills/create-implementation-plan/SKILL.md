---
name: create-implementation-plan
description: Analyze a session-scale task (bug fix, feature, editor tool, perf change) and produce an implementation plan that has already survived an adversarial self-review — verified-facts research, a draft with per-step verification gates, a mandatory critical pass over named lenses (shared-state/composition, read-before-claim, false-green audit, taste-vs-mechanical decisions, conventions, fragility, limitations), then present the revised plan with a decision menu of genuine judgment calls plus explicit assumptions. Use when the user asks to "create an implementation plan", "analyze X and create a plan", "plan this fix/feature", or hands over a single Design/backlog item to implement. Session plans only — for a system-wide phased refactor design doc use create-refactor-plan; for authoring persistent docs use create-design-doc; for pausing planned work across sessions use create-handoff-prompt.
---

# Create Implementation Plan

Turns "analyze X and plan the implementation" into a session-scale plan that has **already
survived an adversarial self-review before the user sees it** — the first plan presented is the
second draft. This skill owns the analyze → draft → self-review → decision-menu workflow for
single-session work. Seams: `create-refactor-plan` owns system-scale analyses whose deliverable
is a phased `Documentation/Design/` doc; `create-design-doc` owns persistent documents;
`create-handoff-prompt` takes over when planned work pauses across sessions. Plan mode (the
harness feature) is the *mechanism* for plan approval; this skill is the *methodology* that
fills it.

Provenance: distilled from a real VS-2 planning session in which a solid first-pass plan still
yielded **10 findings** from a one-line "review it critically" re-prompt — every one traceable
to a mechanical lens that pass one skipped, and the user reversed the model's default on 2 of
the 6 judgment calls once they were surfaced. Before/after snapshot:
[references/vs2-worked-example.md](references/vs2-worked-example.md).

The six phases, of which only Step 4 is visible to the user (Step 5 executes the approved plan):

```
0 Entry  →  1 Research  →  2 Draft  →  3 Adversarial  →  4 Present  →  5 Bake in & execute
 (warm or   (verified      (internal,    (7 lenses +       (decision menu +   (restate, then
  cold?)     facts only)    never shown)  matching packs)   labeled assumptions) run the gates)
```

## When to use / when to skip

- **Use** for session-scale planning: implementing one backlog/design item (warm start), a
  feature/fix/tool sized for roughly one session (cold start), or any "research this, then give
  me an implementation plan" request.
- **Skip / route away:**
  - System-wide cleanup or refactor analysis, multi-session phased plan → `create-refactor-plan`.
  - The deliverable is a persistent document, not a plan for *this* session → `create-design-doc`.
  - Undiagnosed bug → root cause first (`voxel-debugging`); plan the fix once the cause is known.
  - Trivial change (one file, one obvious approach) → just do it; a plan would be ceremony.

## Step 0 — Entry: warm or cold start

**Warm start** (an existing Design/backlog entry, doc section, or report finding is named):

- Read the entry and everything it links before touching code.
- Run the **doc-vs-code drift check**: docs describe the code as of their writing — re-verify
  every count, name, menu item, and API claim against the current code before the plan repeats
  it. (VS-2 example: the report said "2 nightly fuzz" menu items; the code had 3.) Corrections
  found here surface in Step 4's limitations/drift notes and feed `docs-sync` later.
- **Never re-plan what the doc already scoped.** If its plan is still sound, your deliverable is
  "execute step N of that doc", not a new plan — say so and stop.

**Cold start** (verbal request only):

1. Restate goal, hard constraints, and definition-of-done in a few lines. Ask only if genuinely
   ambiguous — otherwise state the interpretation and proceed.
2. Check `Documentation/Design/` and auto-memory for an entry that already covers the task —
   if one exists, this is a warm start; never cold-plan what a doc already scoped.
3. Scale check: if research reveals the task is actually system-scale or wants a persistent
   doc, route away (list above) before investing further.

## Step 1 — Research: verified facts only

- Orient with `codegraph_explore` (1–2 calls), then switch to targeted `Read`/`Grep` for the
  code paths the plan will build on (per the CodeGraph workflow in `CLAUDE.md`).
- Build an **environment-facts list**. Every fact carries its verification ("no .asmdef under
  Assets/Editor/ — checked", "package X installed — checked manifest") or is tagged
  **ASSUMPTION** in so many words. A list that comes back with no assumptions on a non-trivial
  plan means you did not look hard enough, not that everything was verified — these assumptions
  become the labeled list in Step 4.
- **Read-before-claim rule:** no behavioral claim about existing code enters the draft unless
  that code path was read this session. "It should compose fine" without reading the callee is
  how plans acquire load-bearing fiction.
- If the touched system has a guard skill (`chunk-lifecycle`, `serialization-migration`, …),
  invoke it now — its invariants are plan inputs, not review afterthoughts.

## Step 2 — Draft the plan (do NOT present it)

Required structure — a draft missing one of these is not done:

1. **Numbered steps**, each naming its files and ending in a **verification gate** — which
   build target(s), which suite(s), what specific check proves the step landed. Gates reference
   their owning skills (`run-validation-suite`, `perf-benchmark`, `validation-driven-bugfix`
   prove-red) instead of restating their content.
2. **Explicit out-of-scope list** — what is deliberately not being done, with the reason.
3. **Bisectable commit sequence** — each commit compiles and preserves verdicts on its own.
4. **Effort/risk statement** matched against the source entry's estimate when warm.

This draft is internal. Presenting it now is the failure mode this skill exists to prevent.

## Step 3 — Adversarial self-review (mandatory, internal)

Re-read the draft as a hostile reviewer, worst-first, against
[references/lenses.md](references/lenses.md) — **read that file now, every time, not only when
the change looks risky.** Run the 7 **core lenses** always, plus the **domain packs** matching
the task shape (hot-path, chunk pipeline, on-disk format, editor tooling, documented bug, warm
start). Run them adversarially: a pass that produces no changes to the draft is a failed pass,
not a clean one.

| # | Lens | Asks |
|---|---|---|
| L1 | Composition & shared state | who else is standing on what I am changing |
| L2 | Read-before-claim | is every behavioral claim here something I actually read |
| L3 | False-green audit | could my verification pass while the change is broken |
| L4 | Taste vs mechanical | am I silently defaulting a call that is the user's |
| L5 | Conventions | does this violate a rule already written down in this repo |
| L6 | Fragility ranking & matched gate | what is most likely to break, and does my gate exercise *that* |
| L7 | Limitations & drift | what does this not do, and what did I find broken on the way |

### Give every hit exactly one disposition

A finding is not resolved by noticing it. Each one is exactly one of these four, and the
disposition decides where it surfaces in Step 4:

| Disposition | When | Where it goes |
|---|---|---|
| **Mechanical fix** | one defensible answer given the constraints | folded into the plan silently — no need to narrate it (the nested-progress-bar class of defect) |
| **Taste decision** | a reasonable person could choose differently | the decision menu, with a recommendation |
| **Assumption** | the plan depends on it and you cannot verify it this session | the assumptions list, **naming the step that will verify it** |
| **Limitation** | true, unfixable in scope, and the user should know | the plan's limitations, stated as a consequence |

Two failure modes this prevents. A finding with *no* disposition gets noticed and then quietly
dropped — the review ran and changed nothing. A finding with the *wrong* disposition is worse: a
taste call filed as a mechanical fix is exactly the silent defaulting the decision menu exists to
stop, and an assumption filed as mechanical becomes a design claim the plan asserts without
evidence.

When torn between mechanical and taste, choose taste. Over-surfacing costs the user one line;
under-surfacing costs them the decision.

Calibration: if this pass finds nothing, it wasn't actually run — on a plan the size of VS-2
it found ten items, three of them implementation-breaking.

## Step 4 — Present: revised plan + decision menu

One message, in this shape. Keep it tight — a plan nobody finishes reading is skimmed, not
approved.

```markdown
## Goal
One or two sentences. What is true after this lands that is not true now.

## Verified
- <path> — what it actually does, read this session

## Plan
1. <step> — <file(s)>, ending in its verification gate
2. …

## Decisions I need from you
1. **<the call>** — Option A … / Option B … · **Recommend A**, because …

## Assumptions
- <assumption> — unverified because …; wrong ⇒ <consequence for the plan>

## Verification
<the exact command(s) — which dotnet build target(s), which suite>, and what result proves it.

## Not doing
- <adjacent thing> — <why it is out of scope>

## Doc drift found
- <doc> — says <X>, code says <Y>. Reporting, not fixing here.
```

Drop `Doc drift found` when Step 0 turned up nothing. Never drop the others.

Rules for the visible plan:

- **Decision menu = genuine judgment calls only.** Every surviving taste call gets a compact
  pros/cons table with the recommended option first. Use `AskUserQuestion` when the options fit
  its shape, a numbered list otherwise. Do not silently default these — in the VS-2 session the
  user reversed the recommended option twice.
- **Never present options without a recommendation.** "A or B?" with no lean is the reviewer
  doing your analysis.
- **Assumptions are labeled, not buried.** Each names its consequence if wrong, and the step
  that will test it.
- **The verification line is a command, not an intention.** "Run the suite" is not a gate; the
  exact invocation plus the result that would falsify the change is.
- **"Not doing" is mandatory.** Every plan has an edge; stating it is how the user catches a
  scope mismatch before the work, not after. Fold in any doc-drift corrections from Step 0.

Implementation does not start while decision-menu items are open (an empty menu may proceed
directly).

## Step 5 — Bake in decisions, then execute

- Restate the final plan briefly, flagging **where each decision changed the design** (so the
  user can audit that their answers landed).
- Execute under the normal protocol: `CLAUDE.md` compile gates (both csproj targets when editor
  code is touched, stale-domain recompile gotcha), the plan's per-step verification gates, and
  the suites via `run-validation-suite`.
- If the session ends with the plan approved but unexecuted → `create-handoff-prompt`.

## Constraints

- **Never present the first draft.** The self-review is an internal phase of plan creation,
  not a user-prompted follow-up.
- **Never silently default a taste decision** — surfacing options the user might reasonably
  reverse is the point of the decision menu.
- **An unverified assumption stated as design fact is a violation** — label it ASSUMPTION and
  give it a verification step.
- **A session plan is not a document.** If the plan wants to persist beyond the session, route
  to `create-design-doc` / `create-refactor-plan` rather than growing sections here.
- **Findings that contradict a documented deliberate choice** (memory/docs "don't re-suggest"
  items) are dropped, not re-litigated.

## Anti-patterns

- **Planning what a doc already planned.** Step 0 exists to catch this. The cost is not just
  wasted effort — a second plan that diverges from the filed one leaves two conflicting records.
- **Repeating a doc's numbers without re-checking them.** A warm start that copies counts,
  versions, or file names forward is how stale docs propagate into new work.
- **Presenting the first draft.** The tell is a plan with no assumptions section and no "not
  doing" section — nothing was stress-tested.
- **Research theater.** Listing files you opened without saying what they do. "Verified" means a
  claim, not a path.
- **A gate that tests the easy half.** See L6: the gate must exercise the thing most likely to
  break, not the thing easiest to assert.
- **Plans that are really commentary.** If the steps do not name files, it is not a plan yet.
- **Padding the decision menu** with questions you can answer yourself, to look collaborative.
- **Skipping Step 3 because the change is small.** A change under `Assets/Scripts/Jobs/` or in the
  chunk gen → lighting → meshing pipeline has the widest blast radius in this engine — that is
  exactly when L1 earns its keep.
