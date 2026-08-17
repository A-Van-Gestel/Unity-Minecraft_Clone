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
2. **List existing release notes** in `Documentation/Release Notes/` to find the most recent entry — its tone and entry phrasing take precedence for formatting decisions. Its *structure* does not: everything through `release_notes_2026-08-13.md` predates the section layout (Step 2b).
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
7. **Gather the metadata-line and Compatibility inputs** (see the template's header and
   `Compatibility` section):
    - **Build name** — the release is published under the build's own name, from the builds archive
      at `C:\Projects\Unity\Minecraft Voxel Engine\_builds\` (e.g. `RC 83  World Scaling (WS-1 +
      WS-2 + WS-3)`). Take the newest entry matching the "to" date; ask if it is ambiguous. If the
      archive is unreachable (another machine, path moved), omit the `**Build**` field and ask the
      user for the name — never infer a build name from commit subjects.
    - **Save-format versions** — every on-disk format bump in the range, with its final value:
      ```bash
      git log <from-tag>..<to-tag> --oneline --grep='v[0-9]\+ *→\|level.dat\|format version'
      ```
      Cross-check against the migration steps registered under `Assets/Scripts/Serialization/`.
    - **Unity version** — from the Unity upgrade commits in the range, or `ProjectSettings/ProjectVersion.txt`.

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
| **Validation Suite / Testing**            | Bundle all commits for a validation suite into **one** entry. Include key stats: menu path, number of subsystems, total baselines, nightly coverage.                                                     | "Full Lighting Validation Suite" with "3 subsystems, 55 baselines, nightly 2000-seed fuzz"                                              |
| **Bug Fix (systematic)**                  | If a set of bugs were fixed as a campaign (e.g., driven by a validation suite), group them under one heading with per-bug sub-bullets using the format `Bug NN: <one-line description> → <fix summary>`. | "Lighting Bug Fixes (Bugs 06–12)"                                                                                                        |
| **Bug Fix (standalone)**                  | Collect miscellaneous standalone fixes together; group them by subsystem past ~6. Only fixes for bugs that **existed in the previous release** qualify — apply the shipped-bug test (Step 2a).           | "Corrupt LZ4 chunk payloads hanging the loader forever → validate frame magic"                                                           |
| **Refactor**                              | Only mention refactors that are user-visible or architecturally significant (extracted shared helpers, codebase-wide renames). Collect them into one entry.                                              | "Extracted CrossChunkLightModApplier, ... Renamed neighbour → neighbor codebase-wide."                                                   |
| **Chore / Docs / Agents / Version bumps** | **Omit entirely** from the release notes. These are internal hygiene. Exception: Unity version upgrades, which are always reported.                                                                    | Version bumps, agent config changes, doc-only commits                                                                                    |

**This table decides how commits collapse into entries — not where those entries go.** Placement is
Step 2b's job: it owns the section set, what belongs in each, and whether the release is sectioned
at all. Classify here, place there.

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

### Step 2b — Choose the section layout

A release note is read by three different audiences — someone looking for what they can *play
with*, someone looking for what changed *under the engine*, and someone upgrading an existing
world. Sorting entries into `##` sections keeps those readers out of each other's way; a single
flat list of twenty-plus entries does not.

The section set, in reading order (see the template for what belongs in each):

`Highlights` → `Gameplay & Visuals` → `Engine & Performance` → `Tooling & Editor` →
`Testing & Validation` → `Bug Fixes` → `Refactors & Internals` → `Compatibility` →
`Previous Releases` → `What's Changed`

**A section must earn its heading: 3–4 entries minimum.** A section with one or two entries is not
a section — merge its items into the nearest broader one (a lone editor tool goes under
`Gameplay & Visuals` rather than opening a `Tooling & Editor` section for itself). If fewer than
two content sections clear the floor, drop the headings entirely and write the flat list.

This is a **content judgment, not a line count**. A release with six sprawling entries spanning
rendering, storage, and tooling may deserve sections; one with twelve entries that all belong to a
single feature arc may read better flat, as one bullet with sub-bullets. Weigh whether the split
helps a reader *find* something, not whether the file is long.

Some sections are exempt from the entry floor, because readers seek them out by name regardless of
size: `Highlights`, `Compatibility`, `Bug Fixes`, and the fixed `Previous Releases` /
`What's Changed` footer. Omit any section that would be empty — never leave a heading with nothing
under it.

**When the split is genuinely ambiguous, ask the user rather than picking.** Concretely: a major
arc that could sit under two sections (a profiling instrument is both tooling and engine work), or
a layout where most sections land right at the 3–4 floor so the whole sectioned-vs-flat call is a
coin flip.

### Step 3 — Write the release notes

Follow the exact document skeleton and per-section formatting conventions in
[references/release-notes-template.md](references/release-notes-template.md) — read it before
writing. It covers the full structure (metadata line → opening paragraph → sections in reading
order → previous-releases carry-forward → What's Changed), what belongs in each section, and the
formatting rules for each entry type. The two most recent release notes files take precedence over
the template if they differ — **except** where they predate the section layout (everything up to
and including `release_notes_2026-08-13.md` is a flat list; those are frozen and are not the model
for structure, only for tone and entry phrasing).

### Step 4 — File naming and location

- **Path:** `Documentation/Release Notes/release_notes_<YYYY-MM-DD>.md`
- **Date** in the filename matches the "to" tag date.

## Constraints

- **Never list raw commits.** Every entry must be a curated, human-written summary of a logical change.
- **Never include**: version bump commits, agent/skill config changes, doc-only commits (unless they represent a major new architecture doc), or chore commits.
- **Bug-fix entries describe shipped bugs.** Never list a fix for a feature introduced in the same
  range as a standalone bug fix — fold it into that feature's entry or omit it (Step 2a).
- **Carry forward exactly the last 3 releases.** The `Previous Releases` section is a rolling
  window, not an archive: add the previous release's headline features at the top, then **drop
  every item older than the three most recent releases**. Nothing is lost — each older release
  keeps its own file in `Documentation/Release Notes/` — and the list stays short enough to stay
  relevant instead of growing without bound.
- **The metadata line and Compatibility section state facts, not summaries.** Format versions,
  Unity versions, and the build name are verifiable values; take them from the repo and the builds
  archive rather than inferring them from commit prose. Omit a field you cannot verify.
- **No H1 title.** The GitHub release is published under its build name, so a title inside the file
  would duplicate it. The file opens with the metadata line; the filename date carries its
  identity in-repo.
- **Do not fabricate measured numbers.** Only include performance figures (−47%, 3× speedup) if they appear in the commit messages or linked benchmark reports. If no number is available, say "benchmark-confirmed" or omit the metric.
- **One file per release; a shipped note is frozen.** While the current release's note is still
  WIP (uncommitted), edit it freely — that is expected. Once a release note is committed, treat it
  as historical: never edit it in place to fix or update something, because a reader may already
  have acted on the shipped text. Publish the correction as its own dated note/addendum (or an
  explicit correction entry in the next release's notes) stating what changed and why. Never
  silently rewrite a shipped note, and never modify an earlier release's file when creating a new
  one.
- **Ask before assuming.** If the tag range is ambiguous, or if a commit's intent is unclear from the subject line alone, ask the user rather than guessing.
