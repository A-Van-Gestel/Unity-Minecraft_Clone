---
name: docs-sync
description: Keep Documentation/Architecture, Design, Guides, and Performance docs in sync with code changes that alter documented behavior. Use whenever a change modifies a system that has a corresponding architecture/design/guide document, when a Design doc's described feature has just shipped (promote to Architecture or flip status to "Implemented"), when a design's last phase completes and its phased doc must be merged into a current-state Architecture doc, or when the user asks to "update the docs" / "promote this design doc" / "mark this phase complete" / "check what doc this affects" / "is this area documented at all".
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
├── Architecture/   ← authoritative system docs; must stay accurate (may contain sub-folders, e.g. `World Generation/`)
├── Design/         ← in-progress / proposed work; status-tracked
├── Guides/         ← stable how-to / style references
├── Performance/    ← phase baselines and benchmark snapshots (append-only; never edit a past report)
├── Release Notes/  ← dated release snapshots (historical; not synced by this skill)
└── Bugs/, Archived/  (handled by archive-fixed-bug, not this skill)
```

**Code area → primary doc** (non-exhaustive — confirm with the graph, do not trust this list blindly if it looks stale):

| Code area                                                                                                                                 | Primary doc                                                                                                               |
|-------------------------------------------------------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------|
| `World.cs`, `WorldJobManager.cs`, `ChunkPoolManager.cs`, chunk state flags                                                                | `Architecture/CHUNK_LIFECYCLE_PIPELINE.md`                                                                                |
| Voxel bit-packing, `ChunkData` layout, block ID encoding                                                                                  | `Architecture/DATA_STRUCTURES.md`                                                                                         |
| Packed voxel **metadata** bits, block orientation/facing, BlockDatabase metadata authoring                                                | `Architecture/PER_BLOCK_METADATA_SCHEMAS.md`                                                                              |
| World-type architecture, `Data/WorldTypes/`, `IChunkGenerator` implementations, world-gen pipeline selection                              | `Architecture/World Generation/MODULAR_WORLD_GENERATION_&_WORLD_TYPES.md`                                                 |
| 3D density terrain shape, domain warping, multi-noise (continentalness/erosion/peaks-valleys), strata/lodes, `StandardChunkGenerationJob` | `Architecture/World Generation/PROCEDURAL_TERRAIN_GENERATION.md`                                                          |
| Cave carving — worm carvers (trunk + local), zone attenuation, noise/mask seeking, Cheese/Noodle/Spaghetti modes, cave isolation filter   | `Architecture/World Generation/CAVE_GENERATION.md` (+ `cave-tuning` skill)                                                |
| Sub-chunk / section meshing, `SubChunkMesher`, greedy meshing                                                                             | `Architecture/SUB_CHUNK_MESHING_ARCHITECTURE.md`                                                                          |
| Lighting BFS jobs, sunlight/blocklight propagation                                                                                        | `Architecture/LIGHTING_SYSTEM_OVERVIEW.md`                                                                                |
| Block-behavior tick — `FluidTickJob`, `FluidBurstTicker`, `BlockBehavior.*`, `ChunkData`'s active-voxel buckets, `World.TickChunksParallel` | `Architecture/BLOCK_BEHAVIOR_TICK_ARCHITECTURE.md`                                                                        |
| Floating origin — `WorldOrigin`, origin shift/re-anchor, `ChunkRelativePosition`, Unity↔voxel space conversions                            | `Architecture/WORLD_SCALING_FLOATING_ORIGIN.md` (+ `Guides/COORDINATE_SPACES_GUIDE.md`)                                   |
| Command console — `Commands/`, `CommandEngine`, `ConsoleUI`, command registration                                                          | `Architecture/COMMAND_CONSOLE_SYSTEM.md`                                                                                  |
| Lighting **validation suite** / harness (`Assets/Editor/Validation/Lighting/`) changes                                                    | `Architecture/Testing Framework/LIGHTING_VALIDATION_HARNESS_FIDELITY.md` (living doc; + `validation-driven-bugfix` skill) |
| Region files, `ChunkStorageManager`, LZ4/GZip serialization                                                                               | `Architecture/INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md`                                                   |
| Save format / on-disk schema changes                                                                                                      | `Architecture/AOT_WORLD_MIGRATION_SYSTEM.md` (+ `serialization-migration` skill)                                          |
| Sub-voxel collision, collision bounds                                                                                                     | `Architecture/SUB_VOXEL_COLLISION_SYSTEM.md`                                                                              |
| Fluid rendering, shoreline blending                                                                                                       | `Architecture/FLUID_SHORELINE_RENDERING.md`                                                                               |
| Profiler markers, performance instrumentation                                                                                             | `Architecture/PERFORMANCE_PROFILER_OVERHAUL.md` + `Performance/`                                                          |
| Reflection-based settings menu, `SettingsUIGenerator`, `SettingFieldAttribute`, `Settings`/`DevSettings` fields                           | `Architecture/DATA_DRIVEN_SETTINGS_UI.md`                                                                                 |
| Burst jobs, Burst-compatibility patterns                                                                                                  | `Guides/BURST_COMPILER_GUIDE.md`                                                                                          |
| Optimization patterns, GC avoidance, pooling                                                                                              | `Guides/GENERAL_OPTIMIZATION_GUIDE.md`                                                                                    |
| Directory layout / new architectural folder                                                                                               | `Guides/PROJECT_STRUCTURE.md`                                                                                             |
| Naming, bracing, const conventions                                                                                                        | `Guides/CODING_STYLE_GUIDE.md`                                                                                            |

**Use CodeGraph to understand the changed CODE — not to find the docs.** `Documentation/` is markdown
and is not in the graph at all, so locating a doc is always a Glob/Grep job. Per `CLAUDE.md`:

```
codegraph_explore(query="<changed classes/symbols>")   # what the change actually touches + its blast radius
```

Then grep `Documentation/` for the names of any files, classes, or concepts your change renamed or removed:

```
Grep pattern="<OldClassName>|<old_concept>" path="Documentation/"
```

Any hit is a doc that references your change and may need updating.

**Where there is deliberately no owning doc.** The tree covers the **DOTS engine core** — chunk
lifecycle, world generation, lighting, meshing, serialization, block-behavior tick, sub-voxel
collision, floating origin, sky rendering, the command console, and the reflection settings UI.
It deliberately does **not** cover the gameplay/glue layers, so "no match" there is a real answer,
not a lookup failure:

- Player input & controls (`Assets/Scripts/Input/`), spawning (`Spawn/`), block placement &
  interaction (`Placement/`), and general UI/HUD beyond the settings generator.
- Utility and debug glue — `Helpers/`, `Config/`, `Attributes/`, `DebugVisualizations/`.
- Most of `Assets/Editor/` tooling (`BlockEditor/`, `AtlasPacker/`, `StructureEditor/`,
  `WorldTools/`, `DataGeneration/`, `PropertyDrawers/`, …). The exception is
  `Assets/Editor/Validation/` (lighting harness), which has a fidelity doc and the
  `validation-driven-bugfix` skill.
- `Legacy/` and third-party `Libraries/` — undocumented by design.

When a change lands only in these areas and alters no documented system, the verdict is
**no-op / surface-the-gap**, never `needs-new-doc` on reflex. This list is category-level, not
exhaustive — if unsure whether a system quietly grew a doc since this list was written, Glob
`Documentation/**/*.md` and Grep it for the system's name (CodeGraph cannot answer this — it does
not index markdown).

### Step 2 — Classify the doc impact

For each doc identified, the change is exactly one of these:

1. **No-op** — doc still accurately describes the system. Note this and move on; do not edit a doc just to touch it.
2. **Targeted edit** — a specific section, diagram, file/class name, or invariant in the doc is now wrong. Apply the **smallest** diff that restores accuracy. Do not rewrite surrounding paragraphs that are still correct (matches the `CLAUDE.md` "Modification: do not rewrite entire files to make minor changes" rule).
3. **Status flip** (Design docs only) — a phase, or the design as a whole, has shipped. Mark the phase `✅ <date>` in its plan table and update the doc's status line; do **not** rewrite the doc's body to describe the final implementation. That belongs in the Architecture doc, not the now-historical Design doc. Use the same pattern as commit `0818b51`.
4. **Promotion to Architecture** — the design's *last* phase is complete and in-game confirmed, so the Design doc must be merged into a current-state Architecture doc. This is a rewrite, not an edit: see **Design → Architecture promotion** below.
5. **New Architecture doc needed** — a substantial new system was introduced and there is no doc for it. Stop and ask the user whether to author one in this commit or open a follow-up — do not unilaterally create a new architecture document, since they are load-bearing and need user sign-off on tone/scope.

### Step 2b — Design docs vs Architecture docs: the two contracts

The two trees make **different promises**, and most doc drift is one being edited as if it were
the other:

- **`Design/` is a record of intent over time.** Phases stay, and each carries its own dated
  status (`create-design-doc` owns the format). A reader is expected to read a completed phase as
  history and use its date to judge how far it has drifted.
- **`Architecture/` is a description of the codebase right now.** No phase structure, no "we
  then changed it to" — overlapping or stacked phases are merged into single logical sections
  describing current behavior only.

Two rules follow, and they are the ones that keep `Design/` honest:

**The freeze rule — patch a completed phase only for what was wrong at completion time.** Broken
links, wrong file paths, a mis-stated constant, a factual error that was already an error the day
the phase closed: fix those. **Never edit a completed phase to track code drift.** If the code has
moved on, that belongs to the Architecture doc, which is the artifact that promises current state.
A phase marked `⛔ Superseded` is frozen outright — record what replaced it and stop. Repeatedly
patching a closed phase produces text describing neither what shipped then nor what is true now.

**The promotion trigger — when the last phase is marked complete and in-game confirmed,
promotion is due.** Not "eventually": it becomes the next action, the way archiving a fixed bug
does. Without a hard trigger, finished work accumulates in `Design/` and `Architecture/`'s
current-state guarantee is never exercised. Audit/backlog reports (`Open backlog.` status) are
**exempt** — partial completion is their normal steady state, and their master table already
tracks it.

### Step 2c — Design → Architecture promotion

A promotion is a merge, and merges are where confident wrong sentences get written: text that was
true in phase 1 and superseded in phase 3 reads perfectly plausibly. The full protocol —
verification discipline, what moves to `Archived/`, what the promoted doc must carry — is in
[references/promotion-protocol.md](references/promotion-protocol.md). Read it before starting one.

The three rules that decide whether the result can be trusted:

- **Merge against the code, never against the phase text.** The Design doc's own prose is the
  *least* reliable input — it is exactly the thing suspected of drift. Every claim in the promoted
  doc must be verified in current code, per claim, and the doc gets a fresh `Audited:` line naming
  the files actually read at a pinned commit.
- **Prefer a clean session for a multi-phase merge.** A session that just finished the last phase
  knows *that* phase well and knows phase 1 — written months earlier, and the most likely to have
  drifted — no better than a cold reader, so its confidence is highest exactly where it is least
  earned. Hand off via `create-handoff-prompt` rather than folding a large merge into the session
  that happened to finish the work. A single-phase doc with nothing to merge may be promoted in
  place.
- **Carry the IDs across.** The promoted doc keeps an ID index table (`create-design-doc` Step 4)
  so `RF-3`-style references in commit messages and code comments still resolve after the phase
  sections they named are gone.

### Step 3 — Verify cross-references

Two checks. The first runs **every time** this skill runs; the second only when a doc's path or
name changed.

**Always — `@Documentation/` reference integrity.** This repo wires several dozen
`@Documentation/...` references from `CLAUDE.md`, `AGENTS.md`, and `.agents/skills/` into the doc
tree, and a broken one silently degrades agent context — nothing errors. It is cheap to check, so
do it regardless of what you changed:

```bash
python Tools/Python/check_doc_refs.py
```

It prints the number of references it found and lists any that do not resolve, exiting non-zero
on failure. **A "0 unresolved" result only means something if the found-count is plausible** — a
run reporting zero references found is a broken scan, not a clean tree. Re-measure rather than
trusting a remembered total; the count grows as skills and docs are added.

Globs and `{PLACEHOLDER}` segments are deliberately ignored as non-references — today that is one
reference, the `@Documentation/Bugs/{FILE}` slot in a handoff template. (A bare
`Documentation/Bugs/*.md` in prose is not an `@`-reference and never enters the scan.) If you
want the raw list instead, `grep -rn
'@Documentation/' CLAUDE.md AGENTS.md .agents/ Documentation/` gives it — mind the two subfolders
whose names contain spaces (`Architecture/World Generation/`, `Architecture/Testing Framework/`).
A reference to a moved or renamed file is the failure to fix. This is the check that catches
breakage from moves made
**outside** a docs-sync run, which is where most stale `@`-refs come from — so it is not gated on
having renamed anything yourself.

**On rename / move / split only — sweep the doc's inbound references.** When *this* change
renamed, moved, or split a doc, find everything that points at it and fix it in the same commit.
Sweep on the **bare filename**, because references come in two shapes and a link-only grep misses
one:

```bash
grep -rn "OldDocName.md" CLAUDE.md AGENTS.md Documentation/ .agents/
```

- **`@Documentation/...` references and markdown links** are relative, so they break on a *move*
  even when the filename is unchanged — check the relative path, not just the name.
- **Prose mentions** — a backticked path in a table or sentence. Skill files and guides cite docs
  this way rather than as links, so a name-only grep is what catches them.

Fix every hit in the same commit as the rename. Broken `@`-refs silently degrade agent context
windows.

### Step 4 — Commit alongside the code change

Doc updates that match a code change should be in the **same commit** as the code, not a follow-up. The commit message should reflect both, e.g.:

```
Updated: Lighting BFS to skip neighbor chunks + LIGHTING_SYSTEM_OVERVIEW.md
```

Status-only flips on Design docs may stand alone (the `0818b51` precedent), but behavior changes must travel with their docs.

## Output shape

End with a short block, not prose — state the verdict explicitly, because a silent skip is
indistinguishable from an oversight:

```
Doc impact: targeted edit
  Architecture/LIGHTING_SYSTEM_OVERVIEW.md — BFS neighbor-skip note (1 paragraph)
Not documented: Assets/Scripts/Input/ has no owning doc (gameplay glue — surface-the-gap)
Reported, not fixed: CHUNK_LIFECYCLE_PIPELINE.md still cites the old flag name near line 120
```

`Reported, not fixed` is a required line whenever you noticed drift **outside** the change's own
blast radius. Finding a stale doc is expected; fixing an unrelated one in the same commit is scope
creep — report it and let the next targeted pass own it.

**The doc edits this skill produces are themselves reviewable.** `review-changes` gates 15–19
(`references/gates-docs.md`) check the diff for a rewrite that silently dropped content, a patched
frozen phase, a promotion whose `Audited:` line was not re-earned, a dropped ID, and a restamped
date header. That skill detects; this one owns the fix. Worth running after a promotion or any
whole-file doc rewrite.

## Constraints

- **Do not invent documentation.** If a code area has no matching doc and the change is small, do not write one — surface the gap to the user and let them decide. Speculative architecture docs rot faster than no docs at all.
- **Do not mass-rewrite.** Apply targeted diffs. Preserve existing tone, headings, ASCII diagrams, and `#region`-style structure. Never delete a section just because a *different* section is now wrong.
- **Never restate a claim about code you did not read this session.** A targeted edit must not regenerate prose about behavior you did not verify — that silently launders an unverified claim into an authoritative doc. If a neighbouring claim looks wrong but you cannot confirm it, report it (see Output shape); do not fix it and do not delete it.
- **Do not restamp a date header for a targeted edit.** Many Architecture and Design docs carry a `Last Updated:` / `Date:` / `Analysis Date:` line, which means *the whole doc was verified at that date*. Restamping after a one-line fix makes the rest of the doc look fresher than it is — only move the stamp when you actually re-verified the whole doc.
- **Do not edit `Documentation/Bugs/` or `Documentation/Archived/` from this skill** — those are handled by `archive-fixed-bug` and the `voxel-debugging` workflow respectively. The one exception is a **Design → Architecture promotion**, which archives superseded phase detail (see `references/promotion-protocol.md` Step 4).
- **Do not patch a completed phase to track code drift.** The freeze rule (Step 2b) allows only corrections that were already wrong at completion time. Drift belongs to the Architecture doc, not to a historical phase.
- **Do not promote by paraphrasing the Design doc.** A promotion's claims come from current code, verified per claim; the design's own prose is the least trustworthy input in the room.
- **Do not duplicate content.** If the same fact lives in `CLAUDE.md` and an Architecture doc, link from `CLAUDE.md` to the doc — do not copy the doc's body into `CLAUDE.md`.
- **Performance docs are append-only snapshots.** `Documentation/Performance/PHASE_*` files capture a benchmark moment; never retroactively edit a phase report. Add a new phase file instead.
