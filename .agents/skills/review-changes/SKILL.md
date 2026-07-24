---
name: review-changes
description: Reviews pending changes in this Unity/Burst voxel engine for Burst compliance, hot-path GC allocations, pool usage, architectural constraint violations, and serialization compatibility, using the CodeGraph MCP for risk scoring. Use when the user asks to review a diff, check pending changes, prepare a PR, or verify something is safe to merge.
---

# Change Review Protocol

Structured, risk-aware code review tuned for this voxel engine's performance and architectural constraints. Generic review misses the project-specific rejection rules — apply them as explicit gates.

## When to use this skill

- "Review my changes"
- "Look over this diff before I push"
- "Prepare a PR / check if this is ready to merge"
- "Is this change safe?"

## How to use it

### 1. Risk scoring via the graph

- Use standard file reads or `git diff` to see what changed.
- `codegraph_impact` — Run on modified symbols to understand their blast radius.
- `codegraph_callers` — Check what execution paths are affected by the changes.
- `codegraph_explore` — Contextualize how the changed code fits into the broader architecture without reading entire files.

### 2. Project-specific review gates

Apply each gate. A finding in any of these should block or downgrade the merge recommendation.

- **Hot-path GC allocation scan:** Flag any `new`, `.ToArray()`, `.ToList()`, LINQ (`.Any()`, `.Where()`, `.Select()`, `.Count()` with a predicate), `params` arrays, or lambda captures that appear inside `Update()` / `LateUpdate()` / `FixedUpdate()`, mesh-generation paths, chunk-loop bodies, or job-dispatch wrappers. Suggest the pooled alternative: `DynamicPool<T>`, `ConcurrentDynamicPool<T>`, `ListPool<T>`, `HashSetPool<T>`, `ArrayPool<T>`, or `stackalloc` for small fixed-size buffers.
- **Burst compliance scan:** Any diff under `Assets/Scripts/Jobs/` must be 100% Burst-compatible. Flag: managed reference fields, non-blittable types, `string` or `$""` interpolation (use `FixedString` / string literals), `Debug.Log($"...")` in jobs, non-`Unity.Mathematics` math (`Mathf`, `System.Math`), `try`/`catch` / exception types, LINQ, virtual calls, class fields.
- **Pool usage:** Any `new List<T>()`, `new HashSet<T>()`, `new Dictionary<K,V>()` in a frequently-called method should route through a pool. One-shot initialization code is fine.
- **Architectural constraints (hard rejections):** Any change that adds reference types per voxel, uses `BinaryFormatter` / JSON / XmlSerializer for terrain data, replaces sub-chunk meshing with monolithic columns, or bypasses the async BFS flood-fill for light propagation — reject and propose the data-oriented alternative. See `AGENTS.md` "Core Architecture Constraints".
- **Serialization compatibility:** Changes to `ChunkData.cs`, `ChunkStorageManager.cs`, or anything under `Assets/Scripts/Serialization/` that alter the on-disk layout need AOT migration consideration. Reference `@Documentation/Architecture/AOT_WORLD_MIGRATION_SYSTEM.md` and flag if a migration step is missing.
- **Known-bugs cross-check:** If the diff touches lighting, fluids, meshing, or chunk management, scan the relevant `@Documentation/Bugs/*.md` file to confirm the change does not reintroduce a known-fixed issue or collide with an open bug.
- **Unity serialized-field safety:** Flag renames or deletions of `[SerializeField] private` fields or public fields referenced by prefabs/scenes/ScriptableObjects — silent data loss risk unless `[FormerlySerializedAs]` is used.
- **Coding standards:** Magic numbers without named constants, `public` fields instead of `[SerializeField] private`, missing XML docstrings on new public API, incorrect constant casing (`public const` = PascalCase, `private const` = SCREAMING_CASE).
- **Unity-aware validation (unity-mcp):** Run `Unity_ValidateScript` (level: `standard`) on each modified `.cs` file. This catches Unity-specific issues the compiler and graph miss: GC allocations in Update/LateUpdate, string concatenation in hot paths, boxing patterns. Report findings as **Medium** severity.
- **Rider inspection sweep (rider MCP):** Run `mcp__rider__lint_files` on the modified `.cs` files (solution-relative paths, `rootFolder` = repo root). ReSharper-level findings — dead/unused members, impure-struct-copy warnings, domain-reload (UDR) analyzer hits — that neither the compiler nor `Unity_ValidateScript` reports. Report as **Medium**, upgrading to **High** if a finding reveals a real correctness issue. Skip silently if Rider isn't running.

### 3. Output format

Group findings by severity:

- **Blockers** — architectural constraint violations, Burst incompatibility in job code, missing AOT migration, silent data-loss risk.
- **High** — hot-path GC, untested high-risk changes, known-bug collision.
- **Medium** — missing pool usage, magic numbers, missing XML docstrings on new public members.
- **Low** — style / naming deviations from `@Documentation/Guides/CODING_STYLE_GUIDE.md`.

For each finding, include:

- What changed and why it matters (1 sentence).
- Impact radius (from CodeGraph)
- Specific fix or alternative.

End with a single merge/hold recommendation.
