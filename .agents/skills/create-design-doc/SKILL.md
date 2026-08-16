---
name: create-design-doc
description: Author a new document under Documentation/Design/ (system designs, audit/backlog reports) or Documentation/Architecture/ following the project's mandatory header, status taxonomy, body patterns, and Document History footer. Use when the user asks to write, draft, or create a design doc, architecture doc, proposal, feature audit, backlog report, or to "document this design/idea/roadmap". For updating or promoting EXISTING docs use the docs-sync skill instead.
---

# Create Design Doc

Authoring protocol for new documents in `Documentation/Design/` and `Documentation/Architecture/`.
These docs are load-bearing: `CLAUDE.md` and skills `@`-reference them as ground truth, so a new
doc must land with the house structure from its first commit. This skill owns *creating* docs;
`docs-sync` owns keeping existing docs accurate and promoting Design → Architecture. For the
full "analyze a system → execution-ready phased refactor plan" workflow (which produces a
system-design doc *through* this skill), use `create-refactor-plan` — it owns the analysis
protocol and per-phase executor-packet contract; this skill owns only the document format.

## Step 1 — Pick the document species

| Species                    | Lives in                                               | Describes                                                 | Template                                                                                         |
|----------------------------|--------------------------------------------------------|-----------------------------------------------------------|--------------------------------------------------------------------------------------------------|
| **System design**          | `Design/` (→ `Architecture/` once shipped)             | One system/feature: decisions, architecture, phasing      | [references/system-design-template.md](references/system-design-template.md)                     |
| **Audit / backlog report** | `Design/` (living backlog, items removed when shipped) | A ranked findings list over an area (`TF-*`/`RF-*` style) | [references/audit-report-template.md](references/audit-report-template.md)                       |
| **Architecture doc**       | `Architecture/`                                        | An already-implemented system, authoritatively            | Same skeleton as system design with `Status: Implemented`; body describes what *is*, not options |

Read the matching template before writing — it contains the full skeleton with the mandatory
header and footer. Do not link or copy from existing docs as structural exemplars: they are
living documents that get promoted, amended, and archived, so only the templates are stable.

**Before authoring an Architecture doc**, remember the `docs-sync` constraint: do not
unilaterally invent architecture docs for small changes — they need user sign-off on scope.
Design docs are cheaper; when in doubt, start in `Design/`.

## Step 2 — Name the file and its IDs

- Filename: `SCREAMING_SNAKE_CASE.md`, descriptive, no date. Suffix conventions:
  `*_REPORT.md` for audit reports, `*_ROADMAP.md` for plan-only docs.
- If the doc introduces trackable work items, give them a short ID prefix unique across the
  repo (existing: `TF-`, `RF-`, `LI-`, `GS-`, `MR-`, `MH-`, `AS-`, `S`/`SA-` phases, …).
  Grep `Documentation/` for a candidate prefix before claiming it.
- Implementation phases are numbered `<PREFIX>0..N` with phase 0 = data/foundation work.

## Step 3 — Write the mandatory header

Every doc starts with this block (full example in the templates):

```markdown
# <Title>

**Version:** 1.0  
**Date:** <YYYY-MM-DD>  
**Status:** <see taxonomy below>  
**Target:** Unity 6.4 (Mono for dev; IL2CPP for production)   <!-- optional, engine-touching docs -->

> One-blockquote summary: what the doc covers and its single most important
> decision/finding, so a reader can stop here.

**Audited:** <YYYY-MM-DD>, at commit `<short-hash>` (branch `<branch>`).
<What was reviewed to write this — name the code actually read. State findings were
verified in code, not assumed.>

**Relationship to other documents:**

- [`<RELATIVE_LINK>.md`](<path>) — one line on how it relates.
```

Rules:

- **Hard line breaks between stacked fields (easy to get wrong — the spaces are invisible).**
  Markdown joins consecutive lines into one paragraph, so `**Version:**` / `**Date:**` /
  `**Status:**` / `**Target:**` render as a single run-on line unless each is terminated with
  **two trailing spaces**. The rule generalizes: *end a line with two spaces whenever the next
  line starts a new `**Label:**` field.* Corollaries:
    - The **last** field in a block needs none (a blank line follows it).
    - When a field's value wraps over several lines, the two spaces go on its **last** line
      only — the wrapped lines are meant to join.
    - Same rule in the footer (`**Last Updated:**` → `**Next Review:**`) and in any other
      field stack (bug entries' `**Reported:**` / `**Fixed:**` / `**Status:**`).
    - A field line that directly follows a **blockquote** cannot be fixed this way: without a
      blank line between them, lazy continuation swallows the field *into* the quote. Put a
      blank line there instead.
  Do not extend this to ordinary prose — these docs wrap at ~100 chars precisely so the
  renderer rejoins them. Two repo settings keep the spaces alive once written: `.editorconfig`
  (`[*.md] trim_trailing_whitespace = false`) and `.gitattributes`
  (`*.md whitespace=-blank-at-eol`, which stops `git apply --whitespace=fix` stripping them).
  Verify a doc with `python Tools/Python/check_markdown_breaks.py` (add `--fix` to repair).
- **Status taxonomy** (exact strings, bold in place):
    - `Draft — <horizon>` — direction captured, not scheduled; must name what to re-verify
      before implementation starts.
    - `Proposed design — not implemented.` — ready to build against.
    - `Open backlog.` — audit reports only; add "Items are removed (archived) when implemented
      and verified."
    - `Implemented (<Stable|…>)` — Architecture docs (or a Design doc awaiting promotion via
      `docs-sync`).
- **Audited line is not optional.** Pin the commit (`git rev-parse --short HEAD`) and be honest
  about what was inspected. If runtime state was checked live (Unity MCP), say so.
- **Relationship list**: link every doc this one builds on, constrains, or is constrained by —
  relative links (`../Architecture/...`). Include the parent design when writing an
  extension/child doc, and cross-link back from the parent in the same commit (see Step 6).

## Step 4 — Write the body using the house patterns

Use the patterns that fit; the templates show each in place. The recurring ones:

- **Goals & non-goals** — non-goals are versioned: things deferred to v2/v3 say so and point
  at the roadmap section rather than reading as permanent rejections.
- **Current state table** — one row per relevant area, findings from actual code reading with
  `file.cs:line`-style anchors where useful.
- **Decision sections** — when a choice was made, show the losing options too:
  `### Option A — <name> (rejected)` with ✅/❌ bullets, and mark the winner
  `✅ **CHOSEN**` (or `✅ **preferred direction**` in drafts). Verdicts are explicit —
  never leave a decision implied.
- **Constraint-compliance checklist** — table mapping each core architecture constraint
  (packed-`uint` voxels, Burst rules, no hot-path GC, pooling, serialization rules) to how the
  design satisfies it.
- **Phased implementation plan** — table with phase ID, scope, effort (🟢/🟡/🔴),
  dependencies, and a **Status** column. Core systems state that **validation baselines are
  built alongside each phase**, with per-phase suite scope named.
    - Status values: `—` (not started), `In progress`, `✅ <YYYY-MM-DD>` (complete, dated at
      in-game confirmation), `⏸️ <YYYY-MM-DD>` (analyzed, deliberately not implemented),
      `⛔ Superseded <YYYY-MM-DD> — <by what>` (the phase's approach was abandoned).
    - **The date is the point.** A doc-level `Last Updated:` cannot tell a reader *when this
      particular phase* was last true, so a completed phase with no date is indistinguishable
      from present tense — which is how a shipped phase silently becomes a trap. The date lets
      a reader calibrate: a phase closed last week is probably still accurate; one closed six
      months ago is a lead, not a fact.
    - Phase status is **independent of the doc's own status line and `Last Updated:` stamp** —
      marking a phase complete is not re-verifying the doc (see `docs-sync`'s no-restamp rule).
- **ID index** — every doc that owns an ID space (`RF-*`, `VO-*`, `P-*`, …) needs one place
  listing the whole space, open and closed. IDs are never recycled and never dropped when their
  detail is archived or merged away: commit messages and code comments cite them, so the index is
  what keeps those backlinks resolvable. It is not a new section in most docs — find which of the
  three homes applies:
    - **Audit report** → the **Master summary table**, which already is this (see
      `references/audit-report-template.md`). Completed rows stay, marked ✅.
    - **System design with phases** → the **phased plan table** already indexes the space; its
      dated Status column carries the open/closed state. Nothing extra to author.
    - **Promoted Architecture doc** → the one case needing a purpose-built table, near the **top**,
      pointing at the section that now covers each ID. `docs-sync`'s promotion protocol owns it.
- **Rejected alternatives** — a section at the **bottom** of the doc recording options considered
  and turned down, each with the reason and a date (`## 9` in
  `references/system-design-template.md`). Distinct from the inline
  `### Option A — <name> (rejected)` decision sections above, which capture a *single* choice in
  context; this is the doc's standing "do not re-litigate this" list, and it is what survives
  promotion to Architecture. Include options refuted by *measurement*, not only design-time
  choices — a benchmarked NO-GO is the most expensive knowledge to rediscover. Bottom placement is
  deliberate: reference material for a rare reader, not part of the current-state description.
- **Extension roadmap** — future versions (v2/v3+) in a table so deferred wishes have a home.
- **Effort/Risk/Benefit/Seed/Save legend** — audit reports only; copy the legend from the
  template verbatim so symbols stay comparable across reports.

Style rules (match the rest of `Documentation/`):

- ~100-char line width for prose; tables may exceed it. Wrapped prose lines are *meant* to be
  rejoined by the renderer — so never add trailing spaces to them. The one place a hard break
  is required is before a new `**Label:**` field (Step 3), including the `**Amended:**` lines
  below and the Step 5 footer.
- Markdown tables **pre-aligned** (padded pipes) — the repo linter aligns them anyway;
  authoring aligned avoids diff churn.
- Write the doc as a continuous design. Do **not** ship "Open questions → resolved same day"
  scaffolding: fold same-session answers into the sections where they belong. Only questions
  that genuinely remain open when the doc is committed may stay, and real later amendments get
  dated `**Amended:** <date> — <what changed>` lines under the Audited block plus a Document
  History entry.
- Cross-references use `§N` section numbers — re-check them after any restructuring.

## Step 5 — Close with the Document History footer

```markdown
---

## Document History

* **v1.0** - Initial <draft|design|report>

---

**Last Updated:** <YYYY-MM-DD>  
**Next Review:** <trigger — an event, not a date, e.g. "when S0 starts" / "on promotion to Architecture">
```

Every later substantive edit bumps **Version** in the header, adds a one-line entry here, and
updates **Last Updated**. Wording/typo fixes don't bump.

## Step 6 — Integrate and hand off

1. **Cross-link from related docs in the same commit** — if this doc is a child/extension of an
   existing design, edit the parent's roadmap/relationship section to point at it; if an
   umbrella report ranks work items, add/annotate the row there.
2. **Do not add the doc to `CLAUDE.md`** unless the user asks — `CLAUDE.md` references
   Architecture ground truth, not proposals.
3. **Commit message**: single-line `Docs: Add <DOC_NAME> (<key decisions/contents, ' + '-separated>)`
   per the project's commit style. Offer the message; never auto-commit.
4. When the design later ships, **promotion is `docs-sync`'s job** (per-phase status flip, or the
   full merge into `Architecture/`, plus a Document History entry) — mention this to the user if
   they ask about the doc's lifecycle. Two of its rules shape what you author here: a completed
   phase is **frozen** (patched only for what was already wrong when it closed), and promotion
   becomes **due** the moment the last phase is complete and in-game confirmed. That is why the
   phase table carries dated statuses and the doc carries an ID index — both are what a future
   promotion reads.
