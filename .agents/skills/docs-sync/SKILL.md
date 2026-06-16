---
name: docs-sync
description: Keep Documentation/Architecture, Design, Guides, and Performance docs in sync with code changes that alter documented behavior. Use whenever a change modifies a system that has a corresponding architecture/design/guide document, when a Design doc's described feature has just shipped (promote to Architecture or flip status to "Implemented"), or when the user asks to "update the docs" / "check what doc this affects".
---

# Documentation Sync Protocol

This project's `Documentation/` tree is treated as authoritative — `CLAUDE.md` and many skills `@`-reference it as the source of truth for chunk lifecycle, lighting, serialization, meshing, etc. When code drifts from those docs, the docs become a *trap*: future readers (humans and agents) follow them and produce broken changes. This skill exists to force a doc-impact check on changes that alter documented behavior, before the change is considered done.

## When to use this skill

Use it when **any** of the following is true:

- The change modifies a code area that has a matching `Documentation/Architecture/*.md` (see mapping below). Bug fixes that preserve documented behavior do **not** trigger this — only changes that alter the behavior, contract, or invariants the doc describes.
- The change implements a feature that exists as a `Documentation/Design/*.md` proposal. The Design doc must either be promoted/replaced by an Architecture doc, or have its status updated to "Implemented" (see commit `0818b51 Updated: Sub Voxel Collision System design document to implemented` for the canonical pattern).
- The change adds, removes, or renames a public API/file/concept that is named in any doc under `Documentation/`.
- The user asks "update the docs", "is there a doc for this?", "what docs does this affect?", or finishes work and asks for review-readiness.

Skip it for: refactors that don't change observable behavior, formatting/comment-only edits, test additions, internal-only renames already covered by `refactor-safely`, and dependency bumps.

## How to use it

### Step 1 — Identify which docs the change touches

The doc tree as of writing:

```
Documentation/
├── Architecture/   ← authoritative system docs; must stay accurate
├── Design/         ← in-progress / proposed work; status-tracked
├── Guides/         ← stable how-to / style references
├── Performance/    ← phase baselines and benchmark snapshots
└── Bugs/, Archived/  (handled by archive-fixed-bug, not this skill)
```

**Code area → primary doc** (non-exhaustive — confirm with the graph, do not trust this list blindly if it looks stale):

| Code area                                                                  | Primary doc                                                                      |
|----------------------------------------------------------------------------|----------------------------------------------------------------------------------|
| `World.cs`, `WorldJobManager.cs`, `ChunkPoolManager.cs`, chunk state flags | `Architecture/CHUNK_LIFECYCLE_PIPELINE.md`                                       |
| Voxel bit-packing, `ChunkData` layout, block ID encoding                   | `Architecture/DATA_STRUCTURES.md`                                                |
| Sub-chunk / section meshing, `SubChunkMesher`, greedy meshing              | `Architecture/SUB_CHUNK_MESHING_ARCHITECTURE.md`                                 |
| Lighting BFS jobs, sunlight/blocklight propagation                         | `Architecture/LIGHTING_SYSTEM_OVERVIEW.md`                                       |
| Region files, `ChunkStorageManager`, LZ4/GZip serialization                | `Architecture/INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md`          |
| Save format / on-disk schema changes                                       | `Architecture/AOT_WORLD_MIGRATION_SYSTEM.md` (+ `serialization-migration` skill) |
| Sub-voxel collision, collision bounds                                      | `Architecture/SUB_VOXEL_COLLISION_SYSTEM.md`                                     |
| Fluid rendering, shoreline blending                                        | `Architecture/FLUID_SHORELINE_RENDERING.md`                                      |
| Profiler markers, performance instrumentation                              | `Architecture/PERFORMANCE_PROFILER_OVERHAUL.md` + `Performance/`                 |
| Burst jobs, Burst-compatibility patterns                                   | `Guides/BURST_COMPILER_GUIDE.md`                                                 |
| Optimization patterns, GC avoidance, pooling                               | `Guides/GENERAL_OPTIMIZATION_GUIDE.md`                                           |
| Directory layout / new architectural folder                                | `Guides/PROJECT_STRUCTURE.md`                                                    |
| Naming, bracing, const conventions                                         | `Guides/CODING_STYLE_GUIDE.md`                                                   |

**Use the CodeGraph MCP first.** Per `CLAUDE.md`, prefer graph tools over Grep:

```
codegraph_explore(query="docs sync for <changed file or feature>")
codegraph_search(query="<feature name>")  # find related code
```

Then grep `Documentation/` for the names of any files, classes, or concepts your change renamed or removed:

```
Grep pattern="<OldClassName>|<old_concept>" path="Documentation/"
```

Any hit is a doc that references your change and may need updating.

### Step 2 — Classify the doc impact

For each doc identified, the change is exactly one of these:

1. **No-op** — doc still accurately describes the system. Note this and move on; do not edit a doc just to touch it.
2. **Targeted edit** — a specific section, diagram, file/class name, or invariant in the doc is now wrong. Apply the **smallest** diff that restores accuracy. Do not rewrite surrounding paragraphs that are still correct (matches the `CLAUDE.md` "Modification: do not rewrite entire files to make minor changes" rule).
3. **Status promotion** (Design docs only) — the design has shipped. Update the front-matter / status line to "Implemented" and add a one-line pointer to the new Architecture doc if one exists. Use the same pattern as commit `0818b51`.
4. **New Architecture doc needed** — a substantial new system was introduced and there is no doc for it. Stop and ask the user whether to author one in this commit or open a follow-up — do not unilaterally create a new architecture document, since they are load-bearing and need user sign-off on tone/scope.

### Step 3 — Verify cross-references

After editing a doc, check that other docs and `CLAUDE.md` still link to it correctly:

```
Grep pattern="<DocFileName>" path="."   # finds @-references and links
```

If you renamed or moved a doc, update every `@Documentation/...` reference in `CLAUDE.md`, `AGENTS.md`, sibling docs, and any `.agents/skills/*.md` that names it. Broken `@`-refs silently degrade agent context windows.

### Step 4 — Commit alongside the code change

Doc updates that match a code change should be in the **same commit** as the code, not a follow-up. The commit message should reflect both, e.g.:

```
Updated: Lighting BFS to skip neighbor chunks + LIGHTING_SYSTEM_OVERVIEW.md
```

Status-only flips on Design docs may stand alone (the `0818b51` precedent), but behavior changes must travel with their docs.

## Constraints

- **Do not invent documentation.** If a code area has no matching doc and the change is small, do not write one — surface the gap to the user and let them decide. Speculative architecture docs rot faster than no docs at all.
- **Do not mass-rewrite.** Apply targeted diffs. Preserve existing tone, headings, ASCII diagrams, and `#region`-style structure. Never delete a section just because a *different* section is now wrong.
- **Do not edit `Documentation/Bugs/` or `Documentation/Archived/` from this skill.** Those are handled by `archive-fixed-bug` and the `voxel-debugging` workflow respectively.
- **Do not duplicate content.** If the same fact lives in `CLAUDE.md` and an Architecture doc, link from `CLAUDE.md` to the doc — do not copy the doc's body into `CLAUDE.md`.
- **Performance docs are append-only snapshots.** `Documentation/Performance/PHASE_*` files capture a benchmark moment; never retroactively edit a phase report. Add a new phase file instead.
