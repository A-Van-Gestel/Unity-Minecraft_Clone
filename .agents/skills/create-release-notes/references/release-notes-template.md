# Release Notes — Document Template & Formatting Conventions

Companion reference for the `create-release-notes` skill: the exact document skeleton and the
per-section formatting rules. The two most recent files in `Documentation/Release Notes/` take
precedence over this template if they differ — read them first (skill Step 1).

## Document structure

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

## Formatting conventions

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
