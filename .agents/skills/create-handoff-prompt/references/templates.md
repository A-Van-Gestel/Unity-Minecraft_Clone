# Handoff-prompt templates (accreting)

One template per **anchor type**. Match the situation to a template; fill the `{PLACEHOLDERS}`;
verify against the SKILL.md Step 3 checklist (templates cover the structure, not the thinking).

**Accretion rule:** add a new template only after a real handoff prompt of that shape has been
written and accepted/run. Genericize the actual prompt (keep placeholders structural — never bake
in current doc names or item IDs), and record provenance: date + one line on the originating case.
Update, don't fork, when a new case refines an existing shape.

---

## Template A — Continue a planned item from a roadmap / design doc

**Applies when:** the next session executes work already specced as an item/section in a
`Documentation/Design/` (or Architecture) doc — a roadmap entry, a filed hardening plan, a phased
design. The doc carries the detail; the prompt pins scope, traps, and gates.

```
Analyze section "{SECTION TITLE / ITEM IDS}" of @{ROADMAP-OR-DESIGN-DOC} together with
{SUPPORTING FINDINGS/ENTRIES — e.g. fidelity finding IDs, bug entries} in @{SUPPORTING-DOC},
and the code they target: {KEY FILES/SYMBOLS, with approximate locations}.
Context: {1–2 sentences: what happened, where the durable record lives — e.g. the archived bug
entry or session outcome note}.

After the analysis, create an implementation plan for {IN-SCOPE ITEMS, in order}
({OUT-OF-SCOPE ITEMS} is explicitly out of scope — {REASON / where it went}).
The plan must include:
- {ITEM 1}: {per-item obligations: prerequisite audits, hard constraints, its acceptance test /
  prove-red, any measurement}.
- {ITEM 2}: {…, including protocol invariants to honor and doc-sync obligations in-commit}.
- {ITEM 3}: {…, including ordering constraints relative to other items and its prove-red story}.
Wait for my approval of the plan before writing code, then work phase-by-phase with
{THE GATE — e.g. "the full <X> validation suite green (<N> baselines)"} as the gate after each
phase. On completion, {CLOSING BOOKKEEPING — e.g. "flip findings {IDS} per {DOC}'s closing note"}.
```

**Provenance:** genericized 2026-07-04 from the HF-1…HF-3 harness-hardening handoff (roadmap §10
+ fidelity A5/B7/C9), itself modeled on the AS-1 kickoff prompt that ran the Bug-13 session.
That prompt ran successfully 2026-07-05 (HF-1 + HF-2 executed as planned); the template was
reused the same day for the mid-plan HF-3-only re-handoff — completed items are referenced via
their doc **Outcome blocks** (not re-explained), and the acceptance test can point at a
*documented recipe* from a prior item's outcome (HF-3's prove-red re-applies HF-1's B60 sabotage).
Key lines that earned their place: the per-item **acceptance test** (HF-1's B60 sabotage re-run —
the proof the work achieved its purpose), the **audit-before-change prerequisite** (a
session-discovered trap), and the explicit **out-of-scope + destination** (HF-4 → AS-2), which
stopped a cold session from re-planning deferred work.

---

## Template B — Continue a documented bug fix (validation-driven-bugfix flow)

**Applies when:** a bug documented in `Documentation/Bugs/` has a deterministic repro (usually an
expected-red suite scenario) and the next session diagnoses/fixes it. The bug entry + scenario
carry the detail; the prompt names the repro and the lifecycle protocol.

```
Proceed with fixing {BUG ID: TITLE} (documented in @Documentation/Bugs/{FILE}) as per the
validation-driven-bugfix skill, using {REPRO SCENARIO IDS} in @{SCENARIO FILE} as the repro
(currently expected-red; red for the documented reason: {ONE-LINE SYMPTOM}).
{KNOWN DIAGNOSTIC STATE — e.g. "root cause is suspected/confirmed as {X}, see the entry's Root
Cause section" or "instrument first: attribution between {A} and {B} is still open"}.
{TRAPS — e.g. "the {N} approach was tried and rejected: {reason + where recorded}"}.
All {N} baselines of {SUITE MENU ITEM} must stay green throughout; stop after the fix is
suite-green for my in-game confirmation before promoting/archiving.
```

**Provenance:** genericized 2026-07-04 from the Bug-13/Bug-14 sessions ("proceed with the fixing
of Bug 13 as per the validation-driven-bugfix skill"). Key lines: naming the **repro scenario
IDs** (skips re-authoring), stating the **red-for-the-documented-reason** check (a repro red for a
different reason is a different bug), and pinning the **stop point** at in-game confirmation (the
protocol's step the session must not skip). Record **rejected approaches** here when they exist —
the Bug-13 emitter-side guard rejection saved the Bug-14 session from re-walking a dead end.
