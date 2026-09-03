# Documentation-edit gates

Load when the diff touches `Documentation/`, `.agents/skills/`, `CLAUDE.md`, or
`AGENTS.md` — that is, when the change **edits docs**. Gate 3 (core) covers the
opposite case, a code change with no doc edit; it stays in `core` precisely
because it must fire on diffs that contain no doc files at all. Keep the split:

- **Gate 3 (core)** — did the doc edit *happen*?
- **These gates** — is the doc edit *correct*?

These are silent-loss and silent-authority gates. Nothing fails at compile time,
because none of these files compile: drop a method from a `.cs` during a rewrite
and the build catches it, but drop a section from a markdown index, a `.json`, or
an `.asset` and nothing complains. That asymmetry is why the gates live here and
not in `core`.

Route every fix to `docs-sync` (it owns the promotion protocol, the freeze rule,
and per-claim merge verification) — report from here, do not restate its rules.

**Adoption caveat.** Gates 16–18 depend on conventions that land on newly authored
or newly promoted docs: dated per-phase status, ID index tables, re-earned
`Audited:` lines. A legacy doc that predates them is **not** a finding — these
gates fire on docs that carry the format, not on docs that lack it. Do not flag a
doc for non-conformance; that is a migration task, not a review finding.

Each gate carries **what fails**, **how to check**, **severity**, and its
delta/absolute nature.

---

## Gate 15 — A doc rewrite silently dropped content

**What fails.** A file was rewritten wholesale (Write rather than targeted Edit)
and content that was not meant to be removed went with it — an index entry, a
section, a table row, a link. Nothing errors; the loss is invisible until someone
looks for the missing thing. This is the failure mode the `CLAUDE.md`
"do not rewrite entire files to make minor changes" rule and the `docs-sync`
"do not mass-rewrite" constraint both exist to prevent, and it is now *scheduled*
rather than incidental, because a Design → Architecture promotion is a whole-doc
rewrite by design.

**How to check.** Two mechanical signals, both cheap:

```bash
# rewrites: files where deletions dwarf additions
git diff --numstat $RANGE -- '*.md' '*.json'

# headings present before and absent after (--no-color is REQUIRED: color.ui=always here, so ANSI
# escapes would break the ^- anchor and the gate would silently find nothing)
git diff --no-color $RANGE -- '*.md' | grep -E '^-#{1,4} '
```

A heading in that second list, with no matching `+` heading and no stated intent
to remove it, is the finding. For an index-shaped file (`MEMORY.md`, a master ID
table, a doc that enumerates other files) also check that **every entry still
resolves**: a rewrite that keeps 87 of 88 rows looks perfectly healthy in a
`--stat` summary.

Not a violation: a deletion the change explicitly set out to make (content moved
to `Archived/` during a promotion, a section deleted because its code was
deleted). The test is whether the removal was *intended and stated*, not whether
it was large.

**Delta-based.**

**Severity.** High. Lower than the serialization gates because the content is
still in git history and this review runs before merge — but a drop that ships
is effectively permanent, since nobody greps history for a section they do not
know is missing.

---

## Gate 16 — A completed phase was patched to track code drift

**What fails.** An edit lands inside a phase section already marked complete
(`✅ <date>`) or `⛔ Superseded`, and it is not a correction of something that was
*already wrong when the phase closed*. Completed phases are frozen records of
intent; editing one to reflect how the code looks now produces text describing
neither what shipped then nor what is true today.

**How to check.** Locate the edited section's phase status in the doc's plan
table. If the status is `✅`/`⛔`, ask what class the edit is:

- **Allowed** — broken link, wrong file path, mis-stated constant, a factual
  error that was an error on the completion date.
- **Finding** — updating behavior, renaming a type to match a later refactor,
  "we later changed this to…", any edit whose justification is that the code
  moved on.

Drift belongs in the Architecture doc, which is the artifact that promises
current state. If no such doc exists yet, that is gate 17's territory (promotion
is due), not a licence to patch the phase.

**Delta-based.**

**Severity.** Medium. No data is lost, but the doc quietly stops being a reliable
record, and the freeze rule is the only thing keeping `Design/` honest.

---

## Gate 17 — A promotion or Architecture edit reuses evidence it did not re-earn

**What fails.** An Architecture doc is created or substantially rewritten (most
often by promotion from `Design/`) and its `Audited:` line was inherited from the
source doc or left at an old date/commit. That line asserts *the whole document
was verified at that commit*. Carrying it forward launders unverified claims into
the tree that `CLAUDE.md` and the skills treat as ground truth.

**How to check.** In the diff, compare the `Audited:` line against the change:

- Substantial rewrite with an **unchanged** `Audited:` line → finding.
- New `Audited:` line whose commit is **not** current
  (`git rev-parse --short HEAD`) → finding.
- Promoted doc whose claims trace to the Design doc's prose rather than to code →
  finding, and the serious one: the design's own text is the *least* reliable
  input, since it is the thing suspected of drift.

Also check the inverse, which `docs-sync` already names as a constraint: a
**targeted** one-section edit must **not** restamp `Audited:` — see gate 19.

**Delta-based.**

**Severity.** High. An Architecture doc is load-bearing; a wrong claim there
propagates into every future change that trusts it.

---

## Gate 18 — An ID was dropped from an index table

**What fails.** A row for an issued ID (`RF-*`, `VO-*`, `P-*`, `MR-*`, …) was
deleted from a master or index table rather than marked `✅` / `⏸️` /
`⛔ Superseded`. Commit messages and code comments cite these IDs; the table is
what keeps those backlinks resolvable. IDs are never recycled and never dropped,
including for abandoned work.

**How to check.**

```bash
git diff --no-color $RANGE -- '*.md' | grep -E '^-.*\|\s*\*{0,2}[A-Z]{1,3}-[0-9]'
```

Any removed ID row is the finding unless the same ID appears on a `+` line
elsewhere in the diff (moved, not dropped). Archiving an ID's **detail section**
while its **row stays** in the master table is the sanctioned pattern — see
`Documentation/Design/PERFORMANCE_IMPROVEMENTS_REPORT.md`, which does exactly
this — and is not a violation.

**Delta-based.**

**Severity.** Medium. Nothing breaks immediately; a future reader following
`RF-3` from a commit message lands nowhere and re-derives what was already known.

---

## Gate 19 — `Last Updated:` / `Audited:` restamped for a targeted edit

**What fails.** A one-section fix moves the doc's date header. Those stamps mean
*the whole doc was verified at this date*; restamping after a targeted edit makes
every unverified section look freshly checked, which is worse than a visibly old
date — a stale date invites suspicion, a false-fresh one suppresses it.

**How to check.** Compare the size of the change against the stamp. A diff whose
only content edit is a paragraph or a link, plus a moved `Last Updated:` /
`Audited:` / `Date:` line, is the finding. Moving the stamp is correct only when
the whole doc was actually re-verified this session — which for anything
non-trivial should show up as a broad diff or an explicit statement that it was
re-read.

**Delta-based.**

**Severity.** Low. Misleading rather than wrong, and trivially fixed — but it is
the cheapest way to make a drifting doc look maintained.
