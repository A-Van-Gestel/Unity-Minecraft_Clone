---
name: refactor-safely
description: Plans and executes safe renames, file moves, and dead-code removal in this Unity/Burst voxel engine using the CodeGraph MCP. Use when the user asks to rename a class/method/field/file, move code between folders, split a large file, extract a type, or clean up suspected dead code.
---

# Safe Refactor Protocol

This voxel engine has Unity-specific and Burst-specific refactor landmines that generic refactoring tools do not catch. Use the CodeGraph MCP for structural analysis, then layer the project-specific guardrails on top before applying anything.

## When to use this skill

- "Rename `X` to `Y`" (class, method, field, or file)
- "Move this code to `Assets/Scripts/Foo/`"
- "Find dead / unreferenced code in module X"
- "Split this large file"
- "Extract this into its own class/struct"

## How to use it

### 1. Plan with the graph (preview only)

- `codegraph_search` — Find the exact symbol and its ID.
- `codegraph_callers` — See everywhere the symbol is used to ensure you catch all references.
- `codegraph_impact` — Understand the blast radius before proceeding (e.g., changing a struct might impact downstream Burst jobs).
- `codegraph_explore` — Survey the surrounding architecture if you are splitting a large file or extracting a type.

### 2. Project-specific guardrails (verify BEFORE applying)

Generic rename/move tools miss these. Check each before applying edits:

- **`.meta` file rule:** Moving or renaming a `.cs` file MUST also move/rename the sibling `.meta` file, or use `git mv`. A missing `.meta` migration silently breaks prefab/scene GUID references.
- **Burst job compile re-verification:** Any rename of a field, struct, or type touched by code under `Assets/Scripts/Jobs/` requires confirming the job still Burst-compiles. Run `dotnet build "Assembly-CSharp.csproj"` and ask the user to confirm the Burst Inspector is clean.
- **Architectural constraints:** Reject any refactor that introduces reference types per voxel, replaces sub-chunk meshing with monolithic columns, or routes terrain data through JSON/XmlSerializer. See `AGENTS.md`.
- **Public API → scene/prefab data break:** Renaming a `[SerializeField] private` field or a public field referenced by a ScriptableObject/prefab is a *data* break. Either (a) use `[FormerlySerializedAs("oldName")]` or (b) grep `.unity` / `.prefab` / `.asset` files for the old name.
- **Docstring & region preservation:** Existing `///` XML docstrings, inline comments, and `#region` tags must survive the refactor. Use targeted diffs.

### 3. Apply and verify

- Make the code edits using standard file write tools.
- Wait ~2 seconds for CodeGraph to automatically sync the changes in the background.
- `codegraph_status` — Check that there are no pending syncs.
- `codegraph_callers` — Re-run on the newly named symbol to ensure references survived and re-linked properly.
- Run `dotnet build "Assembly-CSharp.csproj"`. If touching Editor code, run `dotnet build "Assembly-CSharp-Editor.csproj"`.
- **Unity MCP verification (unity-mcp):** After the refactor compiles:
    - `Unity_ManageAsset` → `GetInfo` on moved/renamed assets to confirm the GUID is preserved.
    - `Unity_ManageGameObject` → `get_components` on affected prefab instances to verify component references didn't break.
    - `Unity_ReadConsole` — check for "missing script" or "missing reference" warnings that indicate a GUID break the compiler can't catch.
