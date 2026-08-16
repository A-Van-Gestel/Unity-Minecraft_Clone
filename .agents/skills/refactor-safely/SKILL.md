---
name: refactor-safely
description: Plans and executes safe renames, file moves, and dead-code removal in this Unity/Burst voxel engine using the CodeGraph MCP for analysis and the Rider MCP refactoring engine for application. Use when the user asks to rename a class/method/field/file, move code between folders, split a large file, extract a type, or clean up suspected dead code.
---

# Safe Refactor Protocol

This voxel engine has Unity-specific and Burst-specific refactor landmines that generic refactoring tools do not catch. Use the CodeGraph MCP for structural analysis and the Rider MCP refactoring engine (`mcp__rider__*`) for reference-complete application, then layer the project-specific guardrails on top before applying anything.

## When to use this skill

- "Rename `X` to `Y`" (class, method, field, or file)
- "Move this code to `Assets/Scripts/Foo/`"
- "Find dead / unreferenced code in module X"
- "Split this large file"
- "Extract this into its own class/struct"

## How to use it

### 1. Plan with the graph (preview only)

- `codegraph query <name>` (CLI, via Bash) — Find the exact symbol.
- `codegraph callers <sym>` (CLI) — See everywhere the symbol is used to ensure you catch all references. Use the CLI, not `codegraph_explore`: a refactor needs the *complete* call-site set and explore's results are capped.
- `codegraph impact <sym>` (CLI) — Understand the blast radius before proceeding (e.g., changing a struct might impact downstream Burst jobs).
- `codegraph_explore` (MCP) — Survey the surrounding architecture if you are splitting a large file or extracting a type.
- `mcp__rider__safe_delete` with `preview: true` — For dead-code candidates, confirm zero remaining usages with Rider's full conflict analysis before deleting anything.
- `mcp__rider__rename_refactoring` with `preview: true` — Audit the rename blast radius (`affects` counts include `nameof(...)` and XML-doc `<see cref>` references that grep misses).

### 2. Project-specific guardrails (verify BEFORE applying)

Generic rename/move tools miss these. Check each before applying edits:

- **`.meta` file rule:** Moving or renaming a `.cs` file MUST also move/rename the sibling `.meta` file, or use `git mv`. A missing `.meta` migration silently breaks prefab/scene GUID references.
- **Burst job compile re-verification:** Any rename of a field, struct, or type touched by code under `Assets/Scripts/Jobs/` requires confirming the job still Burst-compiles. Run `dotnet build "Assembly-CSharp.csproj"` and ask the user to confirm the Burst Inspector is clean.
- **Architectural constraints:** Reject any refactor that introduces reference types per voxel, replaces sub-chunk meshing with monolithic columns, or routes terrain data through JSON/XmlSerializer. See `AGENTS.md`.
- **Public API → scene/prefab data break:** Renaming a `[SerializeField] private` field or a public field referenced by a ScriptableObject/prefab is a *data* break. Either (a) use `[FormerlySerializedAs("oldName")]` or (b) grep `.unity` / `.prefab` / `.asset` files for the old name.
- **Docstring & region preservation:** Existing `///` XML docstrings, inline comments, and `#region` tags must survive the refactor. Use targeted diffs.

### 3. Apply and verify

- **Symbol renames:** prefer `mcp__rider__rename_refactoring` (preview first, then apply) over hand-editing every call site — it updates `nameof(...)`, XML-doc `<see cref>`, and other language-aware references in one atomic pass. Always pass `rootFolder` = repo root.
- **Dead-code deletion:** prefer `mcp__rider__safe_delete` — it refuses (with a conflict list) if usages remain, so nothing is half-deleted.
- **Extractions / signature changes / namespace moves:** `mcp__rider__extract_method` / `extract_interface` / `extract_base_class` / `change_api_signature` / `move_type_to_namespace` run on the same engine.
- Rider tools require Rider running with the solution open — if unavailable, fall back to standard file write tools + exhaustive Grep.
- **Rider does NOT cover the step-2 guardrails** (`.meta` siblings, `[FormerlySerializedAs]`, prefab/scene GUID references, Burst re-verification) — check them regardless of which tool applied the edit. File moves/renames still go through `git mv` with the `.meta` sibling, never through Rider.
- Remaining code edits (call-site adjustments, comment updates) use standard file write tools.
- CodeGraph syncs the changes automatically in well under a second (a bulk refactor's burst of writes coalesces under the ~2s debounce).
- `codegraph status` (CLI) — Confirm `pendingChanges` is zero before trusting the next query.
- `codegraph callers <newName>` (CLI) — Re-run on the newly named symbol to ensure references survived and re-linked properly.
- Run `dotnet build "Assembly-CSharp.csproj"`. If touching Editor code, run `dotnet build "Assembly-CSharp-Editor.csproj"`.
- **Unity MCP verification (unity-mcp):** After the refactor compiles:
    - `Unity_ManageAsset` → `GetInfo` on moved/renamed assets to confirm the GUID is preserved.
    - `Unity_ManageGameObject` → `get_components` on affected prefab instances to verify component references didn't break.
    - `Unity_ReadConsole` — check for "missing script" or "missing reference" warnings that indicate a GUID break the compiler can't catch.
