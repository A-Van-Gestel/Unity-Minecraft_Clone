# Design → Architecture promotion protocol

Loaded from `docs-sync` Step 2c. Use when a system-design doc's last phase is complete and
in-game confirmed, and the doc must become a current-state Architecture doc.

This is the most error-prone operation in the doc tree. The input is a document organized as a
sequence of intents over time; the output must read as a single description of how the code
behaves today. Every phase boundary is a place where a later phase quietly replaced an earlier
one, and the earlier text still reads as true.

## Step 1 — Decide whether to promote now, or hand off

Promote in place only when **all** of these hold:

- The doc has a single phase, or phases that never revised each other (purely additive).
- You read the relevant code **this session**, from the code — not from the design doc, not from
  a summary you wrote earlier in the session.

Otherwise hand off to a clean session with `create-handoff-prompt`. A multi-phase merge is close
to the ideal handoff: bounded scope, durable file inputs, and an acceptance test (the `Audited:`
line) that a cold session cannot fake by recall.

The reason is asymmetric confidence: the session that just finished the last phase knows that
phase intimately and knows phase 1 — written months earlier, and the phase most likely to have
been superseded — no better than a stranger. Same-session promotion feels safest exactly where
it is least warranted.

## Step 2 — Build the claim inventory before writing anything

List every substantive claim the Design doc makes: data layouts, call ordering, thresholds,
invariants, file and type names, "X calls Y", "Z is deferred to phase N".

For each claim, record the verdict from **current code**:

| Verdict | Meaning | Goes into the Architecture doc as |
|---|---|---|
| `TRUE` | Still holds, verified at `file.cs:line` | Current-state prose |
| `SUPERSEDED` | A later phase or later work replaced it | Omitted; the replacement is described instead |
| `FALSE` | Was never true, or drifted with nothing replacing it | Omitted — and reported, since it may indicate a bug |
| `UNVERIFIABLE` | Could not confirm from code this session | **Not written.** Report it; do not launder it |

`UNVERIFIABLE` is the one that matters. The failure mode of a promotion is not inventing new
claims — it is copying an old claim forward because it sounded right. If you cannot point at the
code, the claim does not enter the promoted doc.

Shaders (`Assets/Shaders/`) are not in CodeGraph — verify those claims by Read/Grep.

## Step 3 — Write the promoted doc

Structure, top to bottom:

1. **Header** per `create-design-doc` Step 3, with a **fresh `Audited:` line**: today's date, a
   newly pinned commit (`git rev-parse --short HEAD`), and the files actually read. Never inherit
   the Design doc's old `Audited:` line — the whole point of promotion is that the claims were
   re-earned.
2. **ID index table** — every ID the design ever issued, open and closed, one line each, pointing
   at the section that now covers it (or at the archived detail). IDs are never recycled and never
   dropped; commit messages and code comments cite them.
3. **Body** — merged into logical current-state sections. No phase numbering, no "originally we
   …", no "phase 2 changed this to". Where two phases touched one mechanism, describe the
   mechanism once, as it is.
4. **Rejected alternatives** — at the bottom, distilled from the design's decision sections plus
   anything measured and refuted along the way, each with its reason and date. This is the
   standing "do not re-litigate" list and it is load-bearing: this repo has repeatedly re-derived
   decisions that were already settled.
5. **Document History footer** per `create-design-doc` Step 5, with a `v1.0 — Promoted from
   Design/<NAME>.md` entry.

## Step 4 — Retire the source document

- **Superseded phase detail** moves to `Documentation/Archived/`, following the pattern
  `PERFORMANCE_IMPROVEMENTS_REPORT.md` already uses: the detail section is archived while its row
  stays in the master table. This is the one sanctioned reason for `docs-sync` to write to
  `Archived/` (the skill's constraint otherwise reserves that folder for `archive-fixed-bug`).
- The Design doc is **not deleted**. It keeps its phases and their dated statuses as the record of
  intent, gains a status line pointing at the Architecture doc that superseded it, and stops
  receiving edits — the freeze rule now applies to the whole document.
- **Sweep inbound references** per `docs-sync` Step 3: `@Documentation/` refs, markdown links, and
  bare prose mentions of the old filename across `CLAUDE.md`, `AGENTS.md`, `Documentation/`, and
  `.agents/`. Run `python Tools/Python/check_doc_refs.py` and confirm the found-count is plausible,
  not just that unresolved is zero.

## Step 5 — Report

State explicitly, in the `docs-sync` output shape:

- Which claims came back `SUPERSEDED` / `FALSE` / `UNVERIFIABLE`, with counts.
- Anything `UNVERIFIABLE` that a reader might expect to find in the promoted doc and won't.
- Whether a `FALSE` verdict looks like a latent code bug rather than doc drift — that is a finding,
  not a documentation task, and it belongs in `Documentation/Bugs/` via the normal route.

## Constraints

- **Never write a claim you did not verify in code this session.** Promotion launders text into an
  authoritative tree; an unverified sentence gains authority it never had.
- **Do not restamp and merge in one motion.** The fresh `Audited:` line asserts the *whole* doc was
  verified. If you only verified part, promote only that part and say so.
- **Do not drop an ID**, even for a phase that was abandoned. `⛔ Superseded` is a valid row.
