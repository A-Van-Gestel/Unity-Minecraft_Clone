---
name: create-release-notes
description: Create a new WIP release notes entry in Documentation/Release Notes/. Use when the user asks to write, draft, or generate release notes for a new tag or date range, or when preparing a release summary from a git log.
---

# Release Notes Authoring Protocol

This skill codifies the conventions and workflow for producing release notes entries in `Documentation/Release Notes/`. The notes serve as a curated, human-readable changelog — **not** a raw commit dump. Each entry should read like a product update that a developer-user can scan quickly while still capturing the technical depth that makes the notes a useful historical record.

## When to use this skill

- The user asks to "create release notes", "write a changelog", or "summarize the release".
- The user specifies a date/tag range (e.g., "from `2026-06-04` to `2026-06-25`").
- A new WIP release is being tagged and needs its notes file.

## Workflow

### Step 1 — Gather inputs

1. **Identify the tag range.** The user provides a "from" tag and a "to" tag (or date). If not given, ask.
2. **List existing release notes** in `Documentation/Release Notes/` to find the most recent entry — its structure takes precedence for formatting decisions.
3. **Read the two most recent release notes files** to internalize the current style and the "previous releases" carry-forward list.
4. **Get the full commit log** between the two tags:
   ```bash
   git log <from-tag>..<to-tag> --pretty=format:"%h %s" --no-merges --reverse
   ```
   Use `--reverse` so commits appear in chronological order (oldest first), making it easier to trace feature arcs. For large ranges, paginate with `--skip=N -n M` (git's own flags, so no shell dependency) to avoid truncation.
5. **Count total commits** for the summary header context:
   ```bash
   git rev-list --count <from-tag>..<to-tag> --no-merges
   ```
6. **Get the changed-file stats** for the range — the paths a change touched are a second,
   independent signal for classification (see Step 2):
   ```bash
   git diff --stat <from-tag>..<to-tag>
   ```
   When a single commit's intent is ambiguous from its subject, inspect that commit's own paths:
   ```bash
   git show --stat <hash>
   ```

### Step 2 — Classify and bundle commits

Classify each commit using **both** signals together — its subject/body **and** the files it
touched. This repo's commit messages are clean and carry the real intent (the `WS-4` / `CL-3` /
`Bug 19` IDs, phase labels, and `→` fix summaries the notes are built from), so the subject is the
**primary** signal here, not a fallback. The changed paths from the `git diff --stat` pass are the
**corroborating** signal: they confirm which subsystem a change actually lands in and catch the
occasional commit whose wording describes intent while the diff touches a different area. When the
two disagree, read the diff before classifying — the paths win on *where*, the message wins on
*why*.

Classify each into one of these categories:

| Category                                  | Bundling rule                                                                                                                                                                                            | Example                                                                                                                                  |
|-------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------|
| **Major Feature**                         | Group all commits that build one feature into a single top-level bullet with indented sub-bullets for phases/milestones.                                                                                 | "Full RGB Smooth Lighting Engine" (spans design doc, Phase B legacy removal, smooth lighting per mesh type, persistence, editor tooling) |
| **Optimization (by ID)**                  | Bundle all commits sharing an optimization ID (e.g., `LI-1`, `MR-2`, `TG-4`) into **one** entry. Include the ID in the heading. Multi-phase optimizations get indented sub-bullets per phase.            | "TG-4: Full Fluid Burst Port (Phases 0–4b)"                                                                                              |
| **Validation Suite / Testing**            | Bundle all commits for a validation suite into a single `TESTING:` entry. Include key stats: number of subsystems, total baselines, nightly coverage.                                                    | "TESTING: Full Lighting Validation Suite" with "3 subsystems, 55 baselines, nightly 2000-seed fuzz"                                      |
| **Bug Fix (systematic)**                  | If a set of bugs were fixed as a campaign (e.g., driven by a validation suite), group them under one heading with per-bug sub-bullets using the format `Bug NN: <one-line description> → <fix summary>`. | "Lighting Bug Fixes (Bugs 06–12)"                                                                                                        |
| **Bug Fix (standalone)**                  | Collect miscellaneous standalone fixes into a single "Bug Fixes" bullet list at the end. Only fixes for bugs that **existed in the previous release** qualify — apply the shipped-bug test (Step 2a).    | "Corrupt LZ4 chunk payloads hanging the loader forever → validate frame magic"                                                           |
| **Refactor**                              | Only mention refactors that are user-visible or architecturally significant (extracted shared helpers, codebase-wide renames). Collect into a single "Refactors" bullet.                                 | "Extracted CrossChunkLightModApplier, ... Renamed neighbour → neighbor codebase-wide."                                                   |
| **Chore / Docs / Agents / Version bumps** | **Omit entirely** from the release notes. These are internal hygiene. Exception: Unity version upgrades, which get their own bullet.                                                                     | Version bumps, agent config changes, doc-only commits                                                                                    |

**Key principle: one logical change = one entry, regardless of how many commits it took.** Never list individual commits.

### Step 2a — The shipped-bug test (bug fixes only)

A "Bug Fixes" entry describes something a reader could have hit. A fix for a feature introduced in
*this same range* was never in anyone's hands, so listing it below that feature's own bullet reads
as shipping a bug and patching it in one breath. This is the same "one logical change = one entry"
principle: an intra-release fix is part of the feature's arc, not a separate change.

Before writing any bug-fix entry, establish that the bug predates `<from-tag>`:

1. **Feature ID** — `git log <from-tag>..<to-tag> --grep=<ID>`. If every commit introducing that ID
   is inside the range, the bug is intra-release.
2. **Path existence** — `git cat-file -e <from-tag>:<path>` on the files the fix touched. A file
   that did not exist at `<from-tag>` cannot have shipped a bug.
3. **Line age** — for pre-existing files, blame the changed lines at `<from-tag>` to see whether the
   buggy lines predate it.

Intra-release fixes are not dropped on the floor — they are **folded**:

- The fix changes the feature's final behavior → make it a sub-bullet of that feature's own entry,
  phrased as what the feature *does*, not as a bug ("emissive alpha defaults to 255", not "RF-3
  emissive alpha was seeded to 0").
- The fix was development churn with no effect on the shipped contract → omit entirely, like a
  chore commit.

Bugs tracked in `Documentation/Bugs/` almost always pass this test; verify rather than assume.

### Step 3 — Write the release notes

Follow the exact document skeleton and per-section formatting conventions in
[references/release-notes-template.md](references/release-notes-template.md) — read it before
writing. It covers the full structure (opening paragraph → feature bullets → TESTING entries →
optimization entries → bug fixes → previous-releases carry-forward → What's Changed) plus the
formatting rules for each section type. The two most recent release notes files take precedence
over the template if they differ.

### Step 4 — File naming and location

- **Path:** `Documentation/Release Notes/release_notes_<YYYY-MM-DD>.md`
- **Date** in the filename matches the "to" tag date.

## Constraints

- **Never list raw commits.** Every entry must be a curated, human-written summary of a logical change.
- **Never include**: version bump commits, agent/skill config changes, doc-only commits (unless they represent a major new architecture doc), or chore commits.
- **Bug-fix entries describe shipped bugs.** Never list a fix for a feature introduced in the same
  range as a standalone bug fix — fold it into that feature's entry or omit it (Step 2a).
- **Preserve the existing carry-forward chain.** The "previous releases" section is an append-only accumulator — never drop items from earlier releases.
- **Do not fabricate measured numbers.** Only include performance figures (−47%, 3× speedup) if they appear in the commit messages or linked benchmark reports. If no number is available, say "benchmark-confirmed" or omit the metric.
- **One file per release; a shipped note is frozen.** While the current release's note is still
  WIP (uncommitted), edit it freely — that is expected. Once a release note is committed, treat it
  as historical: never edit it in place to fix or update something, because a reader may already
  have acted on the shipped text. Publish the correction as its own dated note/addendum (or an
  explicit correction entry in the next release's notes) stating what changed and why. Never
  silently rewrite a shipped note, and never modify an earlier release's file when creating a new
  one.
- **Ask before assuming.** If the tag range is ambiguous, or if a commit's intent is unclear from the subject line alone, ask the user rather than guessing.
