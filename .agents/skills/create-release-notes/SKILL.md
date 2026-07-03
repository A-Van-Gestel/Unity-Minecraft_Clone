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
   ```
   git log <from-tag>..<to-tag> --pretty=format:"%h %s" --no-merges --reverse
   ```
   Use `--reverse` so commits appear in chronological order (oldest first), making it easier to trace feature arcs. For large ranges, paginate with `Select-Object -Skip N -First M` (PowerShell) or `head`/`tail` (Unix) to avoid truncation.
5. **Count total commits** for the summary header context:
   ```
   git log <from-tag>..<to-tag> --oneline --no-merges | Measure-Object -Line
   ```

### Step 2 — Classify and bundle commits

Read through every commit subject line and classify each into one of these categories:

| Category                                  | Bundling rule                                                                                                                                                                                            | Example                                                                                                                                  |
|-------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------|
| **Major Feature**                         | Group all commits that build one feature into a single top-level bullet with indented sub-bullets for phases/milestones.                                                                                 | "Full RGB Smooth Lighting Engine" (spans design doc, Phase B legacy removal, smooth lighting per mesh type, persistence, editor tooling) |
| **Optimization (by ID)**                  | Bundle all commits sharing an optimization ID (e.g., `LI-1`, `MR-2`, `TG-4`) into **one** entry. Include the ID in the heading. Multi-phase optimizations get indented sub-bullets per phase.            | "TG-4: Full Fluid Burst Port (Phases 0–4b)"                                                                                              |
| **Validation Suite / Testing**            | Bundle all commits for a validation suite into a single `TESTING:` entry. Include key stats: number of subsystems, total baselines, nightly coverage.                                                    | "TESTING: Full Lighting Validation Suite" with "3 subsystems, 55 baselines, nightly 2000-seed fuzz"                                      |
| **Bug Fix (systematic)**                  | If a set of bugs were fixed as a campaign (e.g., driven by a validation suite), group them under one heading with per-bug sub-bullets using the format `Bug NN: <one-line description> → <fix summary>`. | "Lighting Bug Fixes (Bugs 06–12)"                                                                                                        |
| **Bug Fix (standalone)**                  | Collect miscellaneous standalone fixes into a single "Bug Fixes" bullet list at the end.                                                                                                                 | "Corrupt LZ4 chunk payloads hanging the loader forever → validate frame magic"                                                           |
| **Refactor**                              | Only mention refactors that are user-visible or architecturally significant (extracted shared helpers, codebase-wide renames). Collect into a single "Refactors" bullet.                                 | "Extracted CrossChunkLightModApplier, ... Renamed neighbour → neighbor codebase-wide."                                                   |
| **Chore / Docs / Agents / Version bumps** | **Omit entirely** from the release notes. These are internal hygiene. Exception: Unity version upgrades, which get their own bullet.                                                                     | Version bumps, agent config changes, doc-only commits                                                                                    |

**Key principle: one logical change = one entry, regardless of how many commits it took.** Never list individual commits.

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
- **Preserve the existing carry-forward chain.** The "previous releases" section is an append-only accumulator — never drop items from earlier releases.
- **Do not fabricate measured numbers.** Only include performance figures (−47%, 3× speedup) if they appear in the commit messages or linked benchmark reports. If no number is available, say "benchmark-confirmed" or omit the metric.
- **One file per release.** Never modify a previous release notes file when creating a new one.
- **Ask before assuming.** If the tag range is ambiguous, or if a commit's intent is unclear from the subject line alone, ask the user rather than guessing.
