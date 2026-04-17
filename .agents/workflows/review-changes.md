---
name: review-changes
description: Run a complete pre-merge review of the current working tree against this voxel engine's project-specific gates (Burst compliance, hot-path GC, pool usage, architectural constraints, serialization compatibility). Produces a structured Blockers/High/Medium/Low report with a merge/hold recommendation.
---

# Review Changes Workflow

Execute a full, project-aware code review of the current working tree. Invoke with `/review-changes`.

## Steps

### 1. Gather scope

- Run `git status --short` to list all changed files (staged, unstaged, untracked).
- Run `git diff HEAD` to see the full diff against the last commit.
- Note which top-level directories are affected (`Assets/Scripts/Jobs/`, `Assets/Scripts/Serialization/`, `Documentation/`, etc.). This determines which gates apply.
- If there are zero changes, report that and exit — do not fabricate findings.

### 2. Load project-specific criteria

Load the `review-changes` skill from `.agents/skills/review-changes/SKILL.md`. It contains the authoritative gate definitions for this codebase. Apply every applicable gate below, using the skill for specific flag patterns and fix suggestions.

### 3. Apply each gate

For every changed file, run the following checks. A finding in any gate downgrades the merge recommendation.

- **Architectural constraint violations** (hard rejection): reference types per voxel, `BinaryFormatter`/JSON for terrain, monolithic-column meshing, bypassing the BFS flood-fill lighting queue. See `AGENTS.md` "Core Architecture Constraints".
- **Burst compliance** (hard rejection for job code): any diff under `Assets/Scripts/Jobs/` must be 100% Burst-compatible. Flag managed refs, non-blittable types, `string` interpolation in `Debug.Log`, non-`Unity.Mathematics` math, `try/catch`, LINQ, virtual calls.
- **Serialization compatibility**: changes to `Assets/Scripts/Serialization/**`, `Assets/Scripts/Data/ChunkData.cs`, or `ChunkStorageManager.cs` must include an AOT migration step. Reference `@Documentation/Architecture/AOT_WORLD_MIGRATION_SYSTEM.md`.
- **Hot-path GC allocation**: flag `new`, `.ToArray()`, `.ToList()`, LINQ (`.Any()`, `.Where()`, `.Select()`), lambda captures inside `Update()`, `LateUpdate()`, `FixedUpdate()`, meshing paths, or job dispatch wrappers. Suggest pooled alternative (`DynamicPool<T>`, `ListPool<T>`, `HashSetPool<T>`, `ArrayPool<T>`, `stackalloc`).
- **Pool usage**: `new List<T>()`, `new HashSet<T>()`, `new Dictionary<K,V>()` in frequently-called methods should route through a pool.
- **Unity serialized-field safety**: renaming or deleting `[SerializeField] private` fields or public fields referenced by prefabs/scenes is a silent data break — flag unless `[FormerlySerializedAs]` is present.
- **Known-bugs cross-check**: if the diff touches lighting, fluids, meshing, or chunk management, scan the relevant `Documentation/Bugs/*.md` file and `_FIXED_BUGS.md` to confirm the change doesn't reintroduce a known-fixed issue.
- **Chunk pipeline invariants**: if `World.cs`, `WorldJobManager.cs`, `ChunkPoolManager.cs`, or `ChunkData.cs` changed — verify flag pairing, gate ordering (`AreNeighborsReadyAndLit`), pool recycle safety. Reference the `chunk-pipeline` rule and `chunk-lifecycle` skill.
- **Coding standards**: magic numbers without named constants, `public` fields instead of `[SerializeField] private`, missing XML docstrings on new public API, incorrect constant casing (`public const` = PascalCase, `private const` = SCREAMING_CASE).
- **Documentation sync**: if pipeline code changed, did `Documentation/Architecture/CHUNK_LIFECYCLE_PIPELINE.md` get updated in the same commit? Flag if not.

### 4. Use the knowledge graph (if available)

If the `code-review-graph` MCP is connected, use these tools to scope the review:

- `detect_changes` for risk-scored change analysis.
- `get_affected_flows` for impacted execution paths.
- `get_impact_radius` for blast radius of each changed node.
- `query_graph` pattern=`tests_for` for test coverage of each changed function.

If the MCP is unavailable, fall back to Grep/Read for the same information.

### 5. Produce the report

Output findings grouped by severity. Each finding includes: what changed, why it matters (1 sentence), specific fix or alternative.

```
## Review: <branch> @ <commit>

### Blockers
- [file:line] <finding> — <fix>

### High
- [file:line] <finding> — <fix>

### Medium
- [file:line] <finding> — <fix>

### Low
- [file:line] <finding> — <fix>

### Test coverage
<per-file coverage summary from query_graph tests_for, or "not checked" if MCP unavailable>

### Recommendation
<one of: MERGE / HOLD — <brief reason>>
```

### 6. Do not commit

The workflow produces findings only. Never stage, commit, or push as part of the review — leave that decision to the user.
