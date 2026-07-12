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
  **ASSUMPTION** in so many words.
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
[references/lenses.md](references/lenses.md): the 7 **core lenses** always, plus the **domain
packs** matching the task shape (hot-path, chunk pipeline, on-disk format, editor tooling,
documented bug, warm start). Each lens produces one of four dispositions:

- **Mechanical fix** → fold into the plan silently (nested-progress-bar class of defect).
- **Taste decision** → trade-off table, goes to Step 4's decision menu.
- **Assumption** that can't be verified this session → assumptions list, with the step that
  will verify it.
- **Limitation** → stated consequence in the final presentation.

Calibration: if this pass finds nothing, it wasn't actually run — on a plan the size of VS-2
it found ten items, three of them implementation-breaking.

## Step 4 — Present: revised plan + decision menu

One message containing, in order:

1. The **revised plan** (post-review, mechanical fixes already folded in).
2. The **decision menu**: every surviving taste call, each with a compact pros/cons table and
   a recommended option listed first. Use `AskUserQuestion` when the options fit its shape;
   numbered list otherwise. Do not silently default these — in the VS-2 session the user
   reversed the recommended option twice.
3. **Assumptions** the plan rests on that could not be verified, and where they get tested.
4. **Limitations** — what the deliverable does NOT do, stated as consequences ("an entry point,
   not a running CI gate"), plus any doc-drift corrections from Step 0.

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
