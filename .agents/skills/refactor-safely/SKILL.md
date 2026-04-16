---
name: refactor-safely
description: Plans and executes safe renames, file moves, and dead-code removal in this Unity/Burst voxel engine using the code-review-graph MCP. Use when the user asks to rename a class/method/field/file, move code between folders, split a large file, extract a type, or clean up suspected dead code.
---

# Safe Refactor Protocol

This voxel engine has Unity-specific and Burst-specific refactor landmines that generic refactoring tools do not catch. Use the code-review-graph MCP for structural analysis, then layer the project-specific guardrails on top before applying anything.

## When to use this skill

- "Rename `X` to `Y`" (class, method, field, or file)
- "Move this code to `Assets/Scripts/Foo/`"
- "Find dead / unreferenced code in module X"
- "Split this large file"
- "Extract this into its own class/struct"

## How to use it

### 1. Plan with the graph (preview only)

- `refactor_tool` mode="rename" — preview every affected location before touching anything.
- `refactor_tool` mode="dead_code" — find unreferenced code.
- `refactor_tool` mode="suggest" — community-driven decomposition suggestions.
- `get_impact_radius` — understand blast radius before proceeding.
- `get_affected_flows` — ensure no critical execution path breaks.
- `find_large_functions` — identify decomposition targets.

### 2. Project-specific guardrails (verify BEFORE applying)

Generic rename/move tools miss these. Check each before `apply_refactor_tool`:

- **`.meta` file rule:** Moving or renaming a `.cs` file MUST also move/rename the sibling `.meta` file, or use `git mv`. A missing `.meta` migration silently breaks prefab/scene GUID references — the code compiles, but prefabs show "missing script" in the Editor. This is one of the most common and hardest-to-diagnose Unity refactor failures.
- **Burst job compile re-verification:** Any rename of a field, struct, or type touched by code under `Assets/Scripts/Jobs/` requires confirming the job still Burst-compiles. `detect_changes` reports the C# rename but will not catch a newly-introduced Burst incompatibility (e.g. a renamed field now shadows a managed type, or a helper changed its signature to return a non-blittable). Run `dotnet build "Assembly-CSharp.csproj"` and, if possible, ask the user to confirm the Burst Inspector is still clean after the refactor.
- **Architectural constraints:** Reject any refactor that introduces reference types per voxel, replaces sub-chunk meshing with monolithic columns, swaps the BFS flood-fill lighting for something else, or routes terrain data through `BinaryFormatter`/JSON/XmlSerializer. See the "Core Architecture Constraints" section of `AGENTS.md` for the full list.
- **Public API → scene/prefab data break:** Renaming a `[SerializeField] private` field or a public field referenced by a ScriptableObject/prefab is a *data* break, not a compile break. Unity silently resets the field to its default on next load. Before renaming such fields, either (a) `[FormerlySerializedAs("oldName")]` preserves existing data, or (b) grep `.unity` / `.prefab` / `.asset` files for the old name so you know what you are about to break.
- **Docstring & region preservation:** Per `AGENTS.md`, existing `///` XML docstrings, inline comments, and `#region` tags must survive the refactor. Use targeted diffs, not full file rewrites.

### 3. Apply and verify

- `apply_refactor_tool` with the refactor_id from step 1.
- `detect_changes` after — confirm the refactor did what you expected and nothing else.
- Run `dotnet build "Assembly-CSharp.csproj"`. If the build fails, fix the errors before reporting the refactor done.
- If `.cs` files moved, confirm `.meta` files moved with them — `git status` should show renames, not delete + add.
- If public serialized fields were renamed, confirm either `[FormerlySerializedAs]` was added or the user has accepted the data-break.
