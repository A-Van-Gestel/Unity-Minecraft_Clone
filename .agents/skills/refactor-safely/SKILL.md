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
- **Public API → scene/prefab data break:** Renaming a `[SerializeField] private` field or a public field referenced by a ScriptableObject/prefab is a *data* break. The project does not use `[FormerlySerializedAs]`: audit HEAD's `.unity` / `.prefab` / `.asset` YAML for the old key, then follow the `unity-file-ops` rename workflow (reserialize, diff, restore dropped values, grep both keys).
- **Docstring & region preservation:** Existing `///` XML docstrings, inline comments, and `#region` tags must survive the refactor. Use targeted diffs.
- **Wide renames go through `Tools/Python/rename_tokens.py`** (an `old<TAB>new` map; dry-run by default, word-boundary only, historical records excluded). Classify tokens before counting them — one word can name two families — and never run a bare-word pass over prose, which rewrites unrelated sentences (check with `git diff --word-diff-regex='[A-Za-z_][A-Za-z0-9_]*'` and read every change). Gate the surfaces the compiler cannot see: HLSL varyings (`ShaderUtil.GetShaderMessages`), string-bound shader globals (`Shader.PropertyToID("X")` ↔ every consuming shader's `X`), and Unity-YAML serialized keys (above).
- **Deleting a flag, policy or mechanism needs a prose sweep too.** An identifier grep proves no code reads it, while comments still describe it in words: grep the phrases (`flag-off`, `rollback leg`, the policy's own wording) over `Assets/Scripts/` and `Assets/Editor/`, triage the hits, and treat a design doc's "delete X, Y, Z" scope line as a starting set, not the closed set. Before deleting an off-side baseline, ask whether a setting can still produce that state — it may need a rename, not removal. A scenario's NUnit name comes from its `new Scenario("…")` registration, so renaming that is not CI-neutral.

### 3. Apply and verify

- **Symbol renames:** prefer `mcp__rider__rename_refactoring` (preview first, then apply) over hand-editing every call site — it updates `nameof(...)`, XML-doc `<see cref>`, and other language-aware references in one atomic pass. Always pass `rootFolder` = repo root.
- **Dead-code deletion:** gate with `mcp__rider__safe_delete` `preview: true` — `{"conflicts":[]}` proves no *C#* reference remains (it resolves symbols, and counts an XML-doc `<see cref>` as a usage). It cannot see callers Unity binds by name: magic methods (`Awake`, `OnValidate`, …), UnityEvent handlers wired in scenes and prefabs (`m_MethodName:` under `m_PersistentCalls`), `SendMessage`/`Invoke` strings, `[MenuItem]`/`[RuntimeInitializeOnLoadMethod]` entry points and reflection — grep the `.unity`/`.prefab` YAML and the string call sites for the name before deleting. Then delete by hand, docstring included: with `preview: false` it can report `applied: true` while the file on disk is unchanged, and it removes only the member, not its `///` block. Confirm with `git diff --stat`, and tell the user to reload the file if Rider later prompts about an external change (saving Rider's stale buffer resurrects the deletion).
- **Extractions / signature changes / namespace moves:** `mcp__rider__extract_method` / `extract_interface` / `extract_base_class` / `change_api_signature` / `move_type_to_namespace` run on the same engine.
- Rider tools require Rider running with the solution open. Missing from the tool roster almost always means Rider is closed: ask the user to open it and run `/mcp`, which reconnects the tools mid-session. Only if that is not possible, fall back to standard file write tools + exhaustive Grep.
- **Rider MCP blind spots:** Unity-plugin inspections (e.g. a local variable hiding a serialized field) never surface through `lint_files` / `get_file_problems`, and `lint_files` stops at warning severity — when the user reports a warning the tools do not show, ask for its text. Solution-wide `'BurstCompile' is not an attribute` is a stale project model after a `.csproj` regeneration: reload the solution. `reformat_file` rejects `.md` files. To find a whole class of smell, sweep with `lint_files` first and grep only to confirm coverage — a literal-based regex misses the non-literal forms.
- `Minecraft Clone.sln.DotSettings` (ReSharper abbreviations such as `ID`, `UI`, `HUD`) is excluded locally and never committed, so silencing an acronym naming warning there fixes it on this machine only; follow the codebase convention (acronyms upper-case) instead.
- **Rider does NOT cover the step-2 guardrails** (`.meta` siblings, serialized-key renames, prefab/scene GUID references, Burst re-verification) — check them regardless of which tool applied the edit. File moves/renames still go through `git mv` with the `.meta` sibling, never through Rider.
- Remaining code edits (call-site adjustments, comment updates) use standard file write tools.
- CodeGraph syncs the changes automatically in well under a second (a bulk refactor's burst of writes coalesces under the ~2s debounce).
- `codegraph status` (CLI) — Confirm `pendingChanges` is zero before trusting the next query.
- `codegraph callers <newName>` (CLI) — Re-run on the newly named symbol to ensure references survived and re-linked properly.
- Run `unity recompile --format json` (covers runtime + editor assemblies and new files); without a running Editor, fall back to the `dotnet build` pair in `CLAUDE.md`.
- **Live Editor verification (Unity CLI, mechanics in the `unity-editor` skill):** After the refactor compiles:
    - `unity command find_assets --name <name>` on moved/renamed assets to confirm the GUID is preserved.
    - `get_serialized_fields` on affected scene or prefab objects to verify component references didn't break.
    - `unity command console --level warn --tail 50 --result-only` — check for "missing script" or "missing reference" warnings that indicate a GUID break the compiler can't catch.
