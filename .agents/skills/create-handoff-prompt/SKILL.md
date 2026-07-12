---
name: create-handoff-prompt
description: Author a self-contained continuation prompt that a future, cold session can execute — anchored on durable @-referenced artifacts (never conversation state), scope pinned in AND out, acceptance tests / prove-red obligations restated, session-discovered traps encoded, and plan-approval + verification gates set. Use when the user asks to "write a prompt for the next/future session", "prepare a handoff", "hand this off", asks for a "continuation prompt", or when a multi-session work arc pauses with its next step already planned. The prompt is a pointer + contract, not a payload — bulky content must be persisted to Documentation/ first (via docs-sync or create-design-doc), and writing the prompt doubles as the audit that everything WAS persisted.
---

# Create Handoff Prompt

Turn "we know what the next session should do" into a prompt that a session with **zero
conversation memory** can execute without re-deriving anything. This skill owns the prompt's
structure and the persistence audit that precedes it; the *content* the prompt points at is owned
by the documentation skills (`create-design-doc` for new docs, `docs-sync` for updating existing
ones) and by the domain protocols the prompt routes to (e.g. `validation-driven-bugfix`). A
handoff prompt never overrides those protocols — it names them and pins the session-specific
parameters they need.

## When to use / when to skip

**Use** at the end of a work arc whose follow-up is already planned (a roadmap item, a filed fix
plan, a promoted-but-unfinished investigation), or whenever the user asks for one.

**Skip** when:

- The next step is *not yet defined* — the missing deliverable is then a design doc or backlog
  entry (route to `create-design-doc`), not a prompt.
- The follow-up is trivial and single-step — a TODO line in the relevant doc beats a prompt.
- Work continues in the same session — just continue.

## Step 1 — Persist before you point (the keystone rule)

Inventory everything the future session will need: the analysis, the plan, discovered traps,
measured numbers, decision rationale. **Anything not already in a durable artifact goes into one
first** — a Design/Architecture doc (via `docs-sync` / `create-design-doc`), a bug entry, a
fidelity-findings doc, or auto-memory for user-preference-shaped facts.

The test: if the prompt would need more than ~5 lines of *original content* (facts stated nowhere
else), that content is missing from the docs — stop and file it. This makes prompt-writing double
as a persistence audit, and it is why good handoff prompts stay short.

## Step 2 — Pick a template, or fall back to the checklist

Match the situation's **anchor type** against [references/templates.md](references/templates.md)
(planned-item continuation, bug-fix continuation, …). If no template fits, compose directly from
the Step 3 checklist — and afterwards genericize what you wrote into a new template (see the
accretion rule in the templates file). Templates are seeded only from prompts that actually ran.

## Step 3 — The invariant checklist

Every handoff prompt, regardless of template, must satisfy all seven:

1. **Anchor on durable artifacts.** `@`-reference the docs/files/entries that carry the content
   (with section numbers or finding IDs). Assume the reader has the repo and nothing else.
2. **Pin scope both ways.** What is in scope, and what is explicitly out — *with the reason*
   ("X folds into item Y"), so the cold session neither re-litigates nor scope-creeps.
3. **Restate the acceptance tests.** The verification obligations that give the work its meaning
   (the prove-red, the re-measurement, the in-game check). This is the element a cold session is
   most likely to silently drop: it does the work, sees green, and never proves the point.
4. **Encode session-discovered traps.** Ordering constraints, rejected approaches, gotchas found
   the hard way ("audit callers BEFORE adding assertions") — the knowledge that dies with the
   session if it lives nowhere else. (If a trap is load-bearing, it should *also* be in a doc —
   see Step 1 — the prompt line is the pointer that makes sure it gets read.)
5. **Set the gates.** Where the session must stop for approval (plan-before-code), what must stay
   green between phases (the relevant validation suite / build), and what ends the session
   (user confirmation points).
6. **Route to governing skills/protocols by name** so their full procedure loads in the new
   session instead of being half-remembered from the prompt.
7. **Pointer, not payload.** Recap in one or two sentences at most; everything else is a
   reference. If you feel the urge to explain, the explanation belongs in a doc (Step 1).

## Step 4 — Cold-read verification and delivery

Re-read the draft *as the future session*: every noun must resolve from the repo alone — no
"as discussed", "the earlier fix", or "the usual suite" without a path or ID. Check that the
`@`-referenced files exist (they may have been renamed/promoted since the plan was filed — e.g.
a scenario file moving into a `Baselines/` folder). Deliver the prompt in a fenced code block so
the user can copy it verbatim, followed by at most a few sentences on why its key lines matter.

## Constraints

- **Never** rely on conversation memory, session-specific scratch files, or auto-memory recall as
  the anchor — memory is a hint system, not a contract; docs are the contract.
- **Do not duplicate doc content into the prompt** — drift makes the prompt actively misleading.
- **Templates accrete only from real cases.** Genericize a prompt that ran (or was accepted by
  the user), note its provenance in the template, and keep placeholders structural.
- **Prompts parameterize protocols; they never replace them.** If the prompt contradicts a
  governing skill, fix the prompt (or the skill via its own change process).
