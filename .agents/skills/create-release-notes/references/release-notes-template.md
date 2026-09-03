# Release Notes — Document Template & Formatting Conventions

Companion reference for the `create-release-notes` skill: the exact document skeleton and the
per-section formatting rules. The most recent files in `Documentation/Release Notes/` take
precedence over this template if they differ — **except** on structure: every file up to and
including `release_notes_2026-08-13.md` predates the section layout and is a flat list. Those are
frozen; read them for tone and entry phrasing, not for document shape.

## Document structure

The skeleton below is the **sectioned** form. Whether a given release uses it is a judgment call —
see skill Step 2b for the 3–4-entries-per-section floor and the sectioned-vs-flat decision. In the
flat form, every `##` content heading collapses away and the entries run as one list under
"This release includes the following major new features and improvements:", with `Previous
Releases` and `What's Changed` unchanged.

```markdown
**Build**: `<build name from the builds archive>` · **Range**: `<from-tag>` → `<to-tag>` · **Commits**: <N> · **Unity**: <version> · **level.dat**: v<N>

<Opening paragraph — the headline features in bold>

## Highlights

- **<Headline 1>**: <One line — what a reader gets, not how it was built>
- **<Headline 2>**: ...

## Gameplay & Visuals

- **<Feature>**: <Summary sentence>
    - <Sub-detail 1>
    - <Sub-detail 2>

## Engine & Performance

- **<OPT-ID>: <Optimization Name>**: <What changed + measured result if available>

## Tooling & Editor

- **<Tool>**: <Summary>

## Testing & Validation

- **<Suite Name>** (<N> baselines): <What the baselines cover>
- **Validate All now runs <N> suites / <N> baselines green.**

## Bug Fixes

- **<Subsystem>**:
    - <What was broken> → <what the fix does>

## Refactors & Internals

- <One-line summary of significant refactors>

## Compatibility

- **level.dat**: v<N> → v<N> (<what each bump carries>). Auto-migrated on load.
- **Chunk/region format**: <changed + migration, or "unchanged">
- **Unity**: <version> (from <version>)
- **Settings**: <new or renamed settings.json keys>

## Previous Releases

This release also contains the changes & improvements of the previous three releases:

- **<Previous release highlight 1>**
- **<Previous release highlight 2>**

## What's Changed

* <PR title> by @<author> in <PR URL>

**Full Changelog**: https://github.com/<owner>/<repo>/compare/<from-tag>...<to-tag>
```

## What belongs in each section

| Section                   | Contents                                                                                                                                  |
|---------------------------|-------------------------------------------------------------------------------------------------------------------------------------------|
| **Highlights**            | The 5–7 genuinely headline entries, one line each, cross-cutting the sections below. Only when sectioned — the flat form's opener covers it. |
| **Gameplay & Visuals**    | Anything a player sees or does: world generation, rendering, lighting, audio, input, UI, interaction.                                       |
| **Engine & Performance**  | Optimizations by ID, pipeline/scheduling work, storage internals, physics — engine changes with no direct on-screen feature.                 |
| **Tooling & Editor**      | Editor windows, in-game consoles/instruments, benchmark and profiling tools, code generation.                                                |
| **Testing & Validation**  | Validation suites, baseline growth, harness fidelity work, CI entry points.                                                                 |
| **Bug Fixes**             | Fixes for bugs that shipped in a previous release (skill Step 2a). Systematic campaigns stay attached to their feature entry.                |
| **Refactors & Internals** | Architecturally significant refactors, renames, extractions, dead-code removal, repo hygiene.                                               |
| **Compatibility**         | On-disk format versions, engine/Unity versions, new settings keys — everything an upgrading reader needs.                                    |

## Formatting conventions

### Metadata line

- A single line, `·`-separated, directly above the opening paragraph. No H1 above it — the GitHub
  release is published under its build name (see skill Step 1.7).
- Omit any field that does not apply or cannot be verified; never guess a version.
- `**Range**` names the **planned** "to" tag, which does not exist yet — it is cut after these notes
  are committed. The git commands behind these numbers use `<from-tag>..HEAD`.
- `**level.dat**` shows the release's *final* version; the full chain goes in `Compatibility`.

### Opening paragraph

- Bold (`**...**`) every headline feature name. End with a period.
- **Sectioned form**: a short lead-in naming the 3–4 biggest items, with the rest carried by
  `## Highlights`. Do not try to name everything.
- **Flat form**: the classic single sentence, no line break.
- A twelve-clause sentence is a symptom that the release wanted sections.

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

- Under `## Testing & Validation`, entries are named plainly — `**Sky & Celestial** (15 baselines):`.
  The old `TESTING:` bullet prefix is retired; the heading carries that meaning.
- In the flat form, keep the `**TESTING: <Suite Name>**:` prefix.
- Always include the baseline count and what the baselines cover (grouped by ranges with short
  descriptions). For a **newly added** suite also give its menu path in backtick-parens
  (`Minecraft Clone/Dev/Validate Lighting Engine`) — a reader who wants to run it needs it, and an
  existing suite's path is already in the previous notes. Do not list per-suite subsystem counts;
  no shipped note has ever carried them.
- For a suite that grew, state the movement — `**Chunk Math** grew 47 → 56 baselines`.
- Mention nightly/stress sweep counts if applicable, and list closed harness fidelity findings.
- End the section with the aggregate: **Validate All now runs N suites / N baselines green.**
  Both numbers **and** the word "green" come from the Step 1.8 `Validate All` verdict line. If that
  run did not happen or came back red, do not write this sentence as-is — say what was actually
  observed.

### Bug fix entries

- Only bugs that existed in the previous release appear here — run the shipped-bug test (skill
  Step 2a) first. A fix for a feature introduced in this same range is folded into that feature's
  bullet or omitted.
- Systematic campaigns: group under one heading with per-bug sub-bullets, kept next to the feature
  or suite that drove them.
- Standalone fixes: a flat list is fine up to ~6 entries. Beyond that, group by subsystem
  (`**Rendering**`, `**World & Storage**`, `**UI**`, `**Pipeline**`, …) with the fixes as
  sub-bullets, using the same subsystem names as the sections above.
- Format: `<What was broken> → <what the fix does>`.
- Include the fix mechanism (class/method names) — these notes double as a technical record.

### Compatibility section

- Lead with the on-disk formats: `level.dat`, chunk/region codec, `pending_mods`, `settings.json`.
- Show the full version chain across the range (`v12 → v15`), with a parenthetical naming what each
  bump carries, then state the migration behavior ("Auto-migrated on load").
- Explicitly say **unchanged** for a format that did not move — silence reads as an oversight.
- The Unity upgrade lives here, including the intermediate versions stepped through.

### Previous Releases section

- Carry forward **exactly the last 3 releases**, newest first: add the previous release's own
  headline features at the top, then drop everything older than the three most recent releases.
  The older files remain in `Documentation/Release Notes/` — this list is a rolling window, not an
  archive.
- Attribute every bullet to the release whose **own** feature list introduced it (skill Step 1.3),
  not to whichever previous note happened to mention it.
- Use `**Bold**` for feature names, `&` to join related pairs **from the same release** — never
  across releases.

### What's Changed / Full Changelog

- Copy the PR reference and changelog URL pattern from the previous release notes, updating the tag names.
- One blank line after the `## What's Changed` heading, and exactly one blank line before
  `**Full Changelog**`.
