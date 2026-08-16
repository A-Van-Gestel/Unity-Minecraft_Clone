---
name: create-handoff-prompt
description: Author a self-contained continuation prompt that a future, cold session can execute — anchored on durable @-referenced artifacts, never on conversation state. Use when the user asks to "write a prompt for the next/future session", "prepare a handoff", "hand this off", asks for a "continuation prompt", or when a multi-session work arc pauses with its next step already planned. The prompt is a pointer + contract, not a payload: bulky content is persisted to Documentation/ first (via docs-sync or create-design-doc).
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
   - **Do not point at `AGENTS.md` or `CLAUDE.md`** — they load automatically; listing them spends
     the reader's attention on what is already in front of them.
   - **Flag local-only files explicitly.** Anything untracked via `.git/info/exclude` (a local
     plan tracker, a scratch note) will not exist in a fresh clone — say so, and say what is lost
     without it. Check with `git ls-files --error-unmatch <path>`.
   - Prefer a path plus *what to look for* over a line number — line numbers rot.
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
   (user confirmation points). A gate needs a **consequence**, not just a preference — "stop after
   the plan and wait" is a gate; "check in when convenient" is not.
6. **Route to governing skills/protocols by name** so their full procedure loads in the new
   session instead of being half-remembered from the prompt.
7. **Pointer, not payload.** Recap in one or two sentences at most; everything else is a
   reference. If you feel the urge to explain, the explanation belongs in a doc (Step 1).
   Symptoms of payload creep: explaining how a subsystem works, restating a doc's contents,
   listing every file you touched, recapping the conversation. A good prompt is mostly proper
   nouns — paths, commands, commits, skill names — with short glue between them.

Two obligations are large enough to stand on their own, in the sections below: **non-git state**
(the top loss risk) and **corrections** (a disproved earlier claim must be retracted, not deleted).

## Non-git state is the top loss risk

Everything committed survives; everything else is one wiped clone away from gone. Enumerate it
explicitly, with a recovery step each:

| State | What the prompt must say |
|---|---|
| Uncommitted working changes | which files, and whether they are needed or discardable |
| `git stash` entries | the stash message, `git stash pop` to recover, and `git stash branch <name>` if it will sit for a while |
| Unpushed commits | the branch, and that it exists only locally |
| Local-only excluded files | the path, that it is untracked (`.git/info/exclude`), and what is lost without it |
| Manual / in-editor verification | what was verified in which scene or play-mode run — unreproducible from the repo alone |
| Anything skipped because it needed the user | the specific question, so it can be asked again |

Where the parked work is small, include **recreate-from-scratch steps** as insurance, not just a
recovery command — a recovery step assumes the stash or branch still exists; the rebuild steps
survive even if it does not.

## Corrections must travel

If something you asserted earlier in the arc was later disproved, **say so explicitly and name the
cause.** A fresh session inherits your confident wrong claim with no way to know it was retracted,
and will build on it.

Format that works:

```
❌ Earlier claim: "<the wrong statement>" — FALSE. <what is actually true>.
   Cause: <how the mistake was made>.
```

Keep the retraction; do not just delete the claim. The reasoning trail is what stops the next
session from re-deriving the same error.

## Step 4 — Cold-read verification and delivery

Re-read the draft *as the future session*: every noun must resolve from the repo alone. Hunt the
phrases that only work while the context is warm — "as discussed", "the earlier fix", "the usual
suite", "that bug", "the doc I mentioned" — and give each one a path, a commit hash, a branch, or
an ID. This is the single most common handoff defect, because the writer cannot feel it.

Then verify the anchors still resolve — files get renamed, promoted, and moved between filing and
execution (e.g. a scenario file moving into a `Baselines/` folder), and a pointer to a moved file
is worse than none because it reads as authoritative:

```bash
ls <every path the prompt names>
git log --oneline -1 <every commit hash the prompt cites>
git branch --list <every branch the prompt names>
```

Confirm each named skill still exists too (`ls .agents/skills/`) — routing by a remembered name
that was since renamed sends the next session nowhere.

**Deliver in a fenced code block** so the user can copy it verbatim, followed by at most a few
sentences on why its key lines matter. A handoff mixed into prose gets partially copied, and the
part left behind is usually the gates.

## Constraints

- **Never** rely on conversation memory, session-specific scratch files, or auto-memory recall as
  the anchor — memory is a hint system, not a contract; docs are the contract.
- **Do not duplicate doc content into the prompt** — drift makes the prompt actively misleading.
- **Templates accrete only from real cases.** Genericize a prompt that ran (or was accepted by
  the user), note its provenance in the template, and keep placeholders structural.
- **Prompts parameterize protocols; they never replace them.** If the prompt contradicts a
  governing skill, fix the prompt (or the skill via its own change process).

## Anti-patterns

- **The briefing.** Paragraphs of explanation the repo should already hold. The tell: the prompt
  is longer than the doc it points at.
- **Traps as generalities.** "Be careful with async context" teaches nothing; name the call, the
  mechanism, and the symptom.
- **Scope with no reasons.** Guarantees the exclusions get revisited.
- **A gate with no consequence.** "Let me know how it goes" is not a gate.
- **Silent retraction.** Deleting a wrong claim instead of marking it wrong — the next session
  re-derives it.
- **Pointing at chat.** "As we discussed" is unresolvable for a cold start.
- **Anchoring on a path you did not check.** A confident pointer to a moved file wastes the next
  session's first minutes and undermines the rest of the prompt.
- **Restating a skill.** If `validation-driven-bugfix` covers it, name it — do not paraphrase the
  procedure.
