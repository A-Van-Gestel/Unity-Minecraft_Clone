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

Follow this exact document structure:

```markdown
<Opening paragraph — one sentence summarizing the headline features in bold>

This release includes the following major new features and improvements:

- **<Feature 1>**: <Summary sentence>
    - <Sub-detail 1>
    - <Sub-detail 2>
- **<Feature 2>**: ...
- **TESTING: <Suite Name>**: <Summary with menu path in backtick-parens>:
    - <N> subsystems: <list>
    - <N> baselines covering: <grouped ranges with descriptions>
    - <Any nightly/stress coverage>
    - <Harness fidelity items closed>
- **<OPT-ID>: <Optimization Name>**: <What changed + measured result if available>
- **Bug Fixes**:
    - <Fix 1>
    - <Fix 2>
- **Refactors**: <One-line summary of significant refactors>
- **Unity Upgrade**: Updated to <version> (from <version>) <+ any notable stripping/pruning>

This release also contains the changes & improvements of the previous releases:

- **<Previous release highlight 1>**
- **<Previous release highlight 2>**
- ...

## What's Changed

* <PR title> by @<author> in <PR URL>

**Full Changelog**: https://github.com/<owner>/<repo>/compare/<from-tag>...<to-tag>
```

### Step 4 — File naming and location

- **Path:** `Documentation/Release Notes/release_notes_<YYYY-MM-DD>.md`
- **Date** in the filename matches the "to" tag date.

## Formatting Conventions

### Opening paragraph

- One sentence, no line break.
- Bold (`**...**`) every headline feature name.
- End with a period.

### Feature bullets

- Top-level bullets use `**Bold Name**:` followed by a summary sentence.
- Sub-details are indented 4 spaces (`    -`) and use sentence fragments (no trailing period unless multi-sentence).
- Use `backticks` for: class/struct names, file names, method names, settings, menu paths, and format versions.
- Use `→` (Unicode arrow) to separate "problem → fix" in bug descriptions.

### Optimization entries

- Start with the optimization ID in bold: `**MR-2: Packed Vertex Format**`.
- Include measured results when available: "−47%", "2.4–3× speedup", "benchmark-confirmed".
- Multi-phase optimizations list each phase as an indented sub-bullet.
- Note what guards/baselines protect the change if applicable.

### Validation suite entries

- Use the prefix `**TESTING: <Suite Name>**:`.
- Always include: number of subsystems, total baseline count, and what the baselines cover (grouped by ranges with short descriptions).
- Mention nightly/stress sweep counts if applicable.
- List closed harness fidelity findings.

### Bug fix entries

- Systematic campaigns: group under one heading with per-bug sub-bullets.
- Standalone fixes: collect under a single "Bug Fixes" bullet.
- Format: `<What was broken> → <what the fix does>`.
- Include the fix mechanism (class/method names) — these notes double as a technical record.

### Previous releases section

- Carry forward the previous release's "also contains" list.
- Add the previous release's own headline features at the top of the carried list.
- Use `**Bold**` for feature names, `&` to join related pairs.

### What's Changed / Full Changelog

- Copy the PR reference and changelog URL pattern from the previous release notes, updating the tag names.

## Constraints

- **Never list raw commits.** Every entry must be a curated, human-written summary of a logical change.
- **Never include**: version bump commits, agent/skill config changes, doc-only commits (unless they represent a major new architecture doc), or chore commits.
- **Preserve the existing carry-forward chain.** The "previous releases" section is an append-only accumulator — never drop items from earlier releases.
- **Do not fabricate measured numbers.** Only include performance figures (−47%, 3× speedup) if they appear in the commit messages or linked benchmark reports. If no number is available, say "benchmark-confirmed" or omit the metric.
- **One file per release.** Never modify a previous release notes file when creating a new one.
- **Ask before assuming.** If the tag range is ambiguous, or if a commit's intent is unclear from the subject line alone, ask the user rather than guessing.
