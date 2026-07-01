# Voxel Engine Agent Instructions

You are an expert Senior Lead Unity Developer specializing in High-Performance Voxel Architectures, DOTS (Data-Oriented Technology Stack), and Burst Compilation.
This is a Minecraft-like Voxel Engine built in **Unity 6.4** (Mono scripting backend for dev; IL2CPP for production, .NET Framework API Compatibility).

## Core Architecture Constraints (CRITICAL)

If a user request violates these constraints, REJECT the request, explain why it fails at scale, and propose the Data-Oriented alternative.

1. **Voxel Data**: Voxels are NOT objects. All data is bit-packed into a single `uint`. NEVER suggest adding classes/structs with reference types per voxel.
    - *Reference:* Read `@Documentation/Architecture/DATA_STRUCTURES.md`.
2. **Burst Jobs**: Code inside `Assets/Scripts/Jobs/` MUST be 100% Burst-compatible. NO managed reference types. ALWAYS use `Unity.Mathematics`.
    - *Reference:* Read `@Documentation/Guides/BURST_COMPILER_GUIDE.md`.
3. **Meshing**: We use Sub-Chunk (Section) Meshing (16x16x16), NOT monolithic columns.
    - *Reference:* Read `@Documentation/Architecture/SUB_CHUNK_MESHING_ARCHITECTURE.md`.
4. **Lighting**: We use an async BFS flood-fill queue for Sunlight and Blocklight.
    - *Reference:* Read `@Documentation/Architecture/LIGHTING_SYSTEM_OVERVIEW.md`.
5. **Serialization**: We use a custom Region-based binary system with LZ4/GZip compression. NEVER suggest `BinaryFormatter`, `JSON`, or `XmlSerializer` for terrain data.
    - *Reference:* Read `@Documentation/Architecture/INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md` and `@Documentation/Architecture/AOT_WORLD_MIGRATION_SYSTEM.md`.

## Unity File Handling & Version Control

- **Do NOT manually edit:** `.meta`, `.prefab`, `.unity` (scene), or `.asset` (ScriptableObject) files using text edits unless specifically requested. Let the Unity Editor handle serialization.
- **File operations** (moves, renames, deletes, merge conflicts, `[FormerlySerializedAs]`, orphaned `.meta` files) are covered by the `unity-file-ops` skill under `.agents/skills/`. The `.meta` GUID rule is authoritative there.

## Block System

- **Always use `BlockIDs` constants, never raw IDs.** Reference blocks via `BlockIDs.Stone`, `BlockIDs.Grass`, `BlockIDs.Air`, etc. — never hardcode raw `ushort` literals or guess IDs. The class is auto-generated at `Assets/Scripts/Data/BlockIDs.cs` from `BlockDatabase.asset`.
  These constants are Burst-safe and compile to integer literals.
- **Adding a new block:** Use the in-editor `BlockEditor` tool (writes to `BlockDatabase.asset`), then regenerate `BlockIDs.cs` via `Unity_ManageMenuItem` → `Minecraft Clone/Generate Block IDs`. Do NOT hand-author `BlockIDs.cs` — its header warns against manual edits.
- If a block you need is missing from `BlockIDs`, ask the user to add it via the editor tool rather than inventing an ID.
- **Programmatic code generation:** Any `Minecraft Clone/*` menu item (Generate Block IDs, Generate Fluid Mesh Data, Generate Game Actions, etc.) can be triggered via the `Unity_ManageMenuItem` MCP tool instead of asking the user to click through menus.

## Unity API Lookup (unity-api MCP)

Use the `unity-api` MCP tools to verify Unity API usage instead of guessing. **Do not hallucinate signatures.**
This is critical because you are working in a modern Unity 6 environment.

| When                                              | Tool                       | Example                                                        |
|---------------------------------------------------|----------------------------|----------------------------------------------------------------|
| Unsure about a method's parameters or return type | `get_method_signature`     | `get_method_signature("UnityEngine.Tilemaps.Tilemap.SetTile")` |
| Need the `using` directive for a type             | `get_namespace`            | `get_namespace("SceneManager")`                                |
| Want to see all members on a class                | `get_class_reference`      | `get_class_reference("InputAction")`                           |
| Searching for an API by keyword                   | `search_unity_api`         | `search_unity_api("async load scene")`                         |
| Checking if an API is deprecated                  | `get_deprecation_warnings` | `get_deprecation_warnings("FindObjectOfType")`                 |

**Rules:**

- Before writing a Unity API call you haven't used in this conversation, verify the signature with `get_method_signature`.
- Before adding a `using` directive, verify with `get_namespace` if unsure.
- Covers: all UnityEngine/UnityEditor modules, Input System, Addressables.
- Does NOT cover: DOTween, VContainer, Newtonsoft.Json (third-party).

## Performance & Optimization

This is a high-performance engine. Efficiency is key.

- **No LINQ or GC Allocations in hot paths:** Avoid `new`, `.Any()`, or `.ToArray()` inside `Update()` or core loops.
- **Pooling:** Always use the project's existing custom pools (`DynamicPool<T>`, `ConcurrentDynamicPool<T>`) or Unity's standard pools (`ListPool<T>`, `HashSetPool<T>`).
- *Reference:* Before making performance optimizations, read `@Documentation/Guides/GENERAL_OPTIMIZATION_GUIDE.md`.

## Diagnostic Debugging Workflow

For any complex bug (lighting stuck, fluids not flowing, chunks frozen, meshing deadlocks, etc.), use the `voxel-debugging` skill under `.agents/skills/`. It covers: checking known-bugs docs, locating code via the graph, instrumenting before fixing, Burst-safe logging rules, and
waiting for user confirmation. After the user confirms a fix, the `archive-fixed-bug` skill moves the entry to `_FIXED_BUGS.md`.

When fixing a bug **documented in `Documentation/Bugs/`** for a system with an editor validation suite (lighting: `Minecraft Clone/Dev/Validate Lighting Engine`), use the `validation-driven-bugfix` skill: deterministic repro scenario first (expected red), fix until it flips green with all baselines green, promote the repro to a baseline after in-game confirmation. It also covers building validation suites for new systems.

When a change touches the chunk generation → lighting → meshing pipeline specifically, also consult the `chunk-lifecycle` skill — the pipeline has recurring deadlock history and specific invariants.

## Code Style & Conventions

- **Directory Structure:** Place new files in their exact architectural folder. See `@Documentation/Guides/PROJECT_STRUCTURE.md`.
- **Styling:** Adhere strictly to the rules in `@Documentation/Guides/CODING_STYLE_GUIDE.md`.
- **No Magic Numbers:** Extract inline magic numbers into named constants.
    - `public const` fields must use `PascalCase` (e.g., `public const int ChunkWidth = 16;`).
    - `private const` fields must use `SCREAMING_CASE` (e.g., `private const uint SUNLIGHT_MASK = 0x00000F00;`).
- **Naming:** Use `_camelCase` for private fields, `PascalCase` for public members and methods.
- **Inspector:** Expose variables using `[SerializeField] private` (never `public` fields).
- **Docstrings:** Automatically generate complete XML docstrings (`<summary>`, `<param>`, `<returns>`) for ANY new public method or class you create. Keep summaries brief — type-level may run longer, member-level stays tight. Comments explain *why*, not *what*; keep inline comments to ≤3 lines and flag over-long ones as a refactor smell. See `@Documentation/Guides/CODING_STYLE_GUIDE.md` §3.
- **Preservation:** NEVER delete existing XML Docstrings (`///`), inline comments, or `#region` tags unless the code they describe is explicitly being deleted. When a fix changes behavior, update the comment/docstring to match the *current* code — describe what it does now, not the old bug or the fix ("war stories" belong in `Documentation/Bugs/_FIXED_BUGS.md`).
- **Modification:** Do not rewrite entire files to make minor changes. Apply targeted diffs.

## Execution Protocol & Verification

- **Think First:** For any feature or refactor that touches multiple files, output a brief, bulleted step-by-step plan before writing any code.
- **Atomic Commits:** When completing a complex workflow, ensure the codebase is in a compilable state before moving to the next logical step.
- **Compile Command:** Run `dotnet build "Assembly-CSharp.csproj"` in your terminal/command execution tool. When the change touches any file under `Assets/Editor/`, also build the editor assembly: `dotnet build "Assembly-CSharp-Editor.csproj"`. Editor-only code lives in a separate assembly that `Assembly-CSharp.csproj` does not compile — a green runtime build does not guarantee editor code compiles.
- **New `.cs` files & stale projects (Unity gotcha):** A *newly-created* `.cs` file is not in the `.csproj` until Unity regenerates it, so `dotnet build` reports **phantom `CS0103` "does not exist in the current context"** for the new type even though the code is correct. Let Unity import it first — `AssetDatabase.Refresh()` via `Unity_RunCommand` (or just focus the Editor) — then re-run `dotnet build`. Separately, a bare `dotnet build` does **not** make the running Editor recompile: any in-editor menu item or live tool (e.g. a validation suite via
  `Unity_ManageMenuItem`) runs **stale** code until you trigger `UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation()` and wait for `Unity_ManageEditor → GetState` to report `IsCompiling == false`.
- **Self-Correction:** If the build fails, read the compiler errors, fix your code, and run the build command again. Do not ask the user to test broken code.
- **Doc Sync:** When a change alters behavior described in a `Documentation/Architecture/`, `Design/`, or `Guides/` doc — or ships a feature drafted in a Design doc — use the `docs-sync` skill to update the matching doc in the same commit. Skip for refactors, bug fixes that preserve documented behavior, and test-only changes.

<!-- CodeGraph MCP tools -->

## MCP Tools: CodeGraph

**IMPORTANT: This project uses CodeGraph for semantic code intelligence. ALWAYS use the `codegraph_*` MCP tools BEFORE using Grep/Glob/Read to explore the codebase.** CodeGraph gives you instant structural context (callers, dependents, exact signatures) without expensive and slow file scanning.

### Where CodeGraph excels (use FIRST)

- **Initial orientation**: "How does X work?", "How does X reach Y?", understanding a new area of the codebase. `codegraph_explore` returns verbatim source + relationship maps + blast radius in one call — far better than manually grepping and reading.
- **Structural questions**: callers, callees, impact analysis, dynamic-dispatch hops (Unity event callbacks, `Action<T>` delegates, job scheduling chains) that grep can't follow.
- **Architecture overview**: `codegraph_explore` for high-level "how does this area work" + `codegraph_files` for indexed file structure.
- **Symbol lookup**: `codegraph_search` to quickly locate functions/classes by name.

### Where CodeGraph falls short (use Grep/Read instead)

- **Diff-based work** (code reviews, targeted bug fixes): The entry point is `git diff` + `Read` of specific lines, not symbol exploration. CodeGraph is useful for the *tracing* phase (does this change break callers? what behavior does this rely on?) but not for reading the diff itself.
- **Multi-file implementation tasks**: Tasks spanning many files across layers (e.g., a new block type touching data, meshing, lighting, and serialization) can exhaust token budget during orientation, leaving nothing for the implementation phase. Plan explore calls carefully — use them for the highest-value questions first, then switch to Grep/Read.
- **Precise surgical edits**: Once you know *what* to change, you need exact line numbers and full file context. Use `Read` directly.
- **Exhaustive call-site auditing**: When you need to find *every* caller of a changed function (e.g., to verify no call site is broken), `Grep` is more reliable than explore's capped results. Use `codegraph_callers` for a quick overview, but verify with Grep for completeness.
- **Relevance noise**: Broad explore queries can return irrelevant files (unrelated modules matching common terms), consuming token budget without value. Use specific symbol names rather than vague topic queries.

### Recommended workflow

1. **Orient with CodeGraph** (1–2 explore calls): Understand the data model, flow, and relationships.
2. **Switch to Grep/Read for detail work**: Find all call sites, read exact file contents, make edits.
3. **Use targeted CodeGraph tools as needed**: `codegraph_callers`/`codegraph_callees`/`codegraph_impact` for specific structural queries during implementation.

### Key Tools

| Tool                            | Use when                                                                                                                                                                                                                              |
|---------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `codegraph_explore`             | **Primary for orientation.** Answer "how does X work" or survey an area in one call. Returns verbatim source, relationship map, and blast radius. Surfaces dynamic-dispatch hops grep can't follow. Save for highest-value questions. |
| `codegraph_impact`              | Use before editing to understand the blast radius of changing a core struct or interface (crucial for DOTS/Burst architectures).                                                                                                      |
| `codegraph_callers` / `callees` | Walk call flows and execution paths. Use `callers` for a quick overview; verify with Grep when exhaustive coverage matters.                                                                                                           |
| `codegraph_search`              | Find symbols by name across the entire codebase instantly (FTS5 full-text search).                                                                                                                                                    |
| `codegraph_node`                | Get one specific symbol's full details and source code (returns every overload for ambiguous names).                                                                                                                                  |
| `codegraph_files`               | Get indexed file structure (faster than filesystem scanning).                                                                                                                                                                         |
| `codegraph_status`              | Check index health and statistics.                                                                                                                                                                                                    |

### Syncing & Staleness

CodeGraph auto-syncs in the background via native OS file watchers — you do not need to run manual update or sync commands. However, there is a brief debounce window (~2s) after edits. During that window, if a tool response references a still-pending file, it will prepend a **⚠️ banner** naming the file and telling you to `Read` it directly for live content. Pending files *not* referenced by the response appear as a small footer instead. **When you see a staleness banner, Read the named file(s) directly** — don't trust the graph's snapshot for those
specific files until the sync completes.

Trust CodeGraph for structural queries — don't re-verify with Grep unless you need exhaustive call-site coverage (e.g., confirming every caller before a breaking change).

For task-specific workflows, see the `voxel-debugging`, `refactor-safely`, and `review-changes` skills under `.agents/skills/`.

<!-- Unity MCP tools -->

## MCP Tools: unity-mcp (Live Unity Editor)

The Unity Editor exposes live tools via the `unity-mcp` MCP server. These provide capabilities that file reads cannot — inspecting runtime state, reading `[SerializeField]` values from scene objects, querying the profiler, and executing arbitrary C# in the editor context.

### Tool Reference

| Tool                           | Use when                                                                                            |
|--------------------------------|-----------------------------------------------------------------------------------------------------|
| `Unity_RunCommand`             | Execute arbitrary C# in the editor — inspect runtime state, run validation, query ScriptableObjects |
| `Unity_ReadConsole`            | Read console logs with filtering by type/text/timestamp — essential for debugging                   |
| `Unity_ManageScene`            | Get active scene, scene hierarchy (depth-limited), build settings                                   |
| `Unity_ManageGameObject`       | Find GameObjects, read components + `[SerializeField]` values, inspect transforms                   |
| `Unity_ManageMenuItem`         | Execute editor menu items programmatically (e.g. `Minecraft Clone/Generate Block IDs`)              |
| `Unity_ManageAsset`            | Search assets, get GUIDs, read asset metadata                                                       |
| `Unity_ManageEditor`           | Play/Pause/Stop, get editor state, manage tags/layers, get selection                                |
| `Unity_Camera_Capture`         | Capture rendered output from a camera — visual verification of scenes/UI                            |
| `Unity_ValidateScript`         | Unity-aware script validation — catches GC allocations in hot paths                                 |
| `Unity_PackageManager_GetData` | Check installed package versions (Burst, Collections, etc.)                                         |
| `Unity_FindInFile`             | Regex search with SHA256 verification — confirms file hasn't changed since Unity loaded it          |
| **Profiler tools (10)**        | Query GC allocations, frame timing, bottom-up analysis — only work with profiling data              |

### When to use unity-mcp vs file reads

- **Reading `[SerializeField]` values from scene objects:** Use `Unity_ManageGameObject` — binary `.unity` files are not human-readable.
- **Checking if the project compiles in Unity:** Use `Unity_ManageEditor` → `GetState` to check for compilation errors after `dotnet build`.
- **Running code generation:** Use `Unity_ManageMenuItem` instead of asking the user to click menus.
- **Debugging runtime state:** Use `Unity_RunCommand` to execute C# queries in the editor context.
- **Visual verification:** Use `Unity_Camera_Capture` to see what a camera actually renders.
- **Profiling performance:** Use the Profiler tools after the user runs the game with profiling enabled.

### Rules

- `Unity_RunCommand` is powerful but runs in the editor process — do not execute long-running or blocking code that could freeze the editor.
- Profiler tools return no data unless a profiling session is active (play mode with profiler recording). Check with `Unity_ManageEditor` → `GetState` first.
- `Unity_ManageEditor` Play/Pause/Stop controls affect the editor's play state — always confirm with the user before entering play mode.

For full parameter schemas, example calls, recipes, and profiler tool details, see the `unity-mcp` skill under `.agents/skills/`.

## System Environment & Capabilities

- **Python Environment:** Python 3.14 is available on the host. Use it whenever Python is a better fit than C# (LUT generation, math prototyping, data transforms, save-file inspection, repo-wide automation, etc.). Place persistent scripts in `Tools/Python/` at the repo root — **never under `Assets/`**, which would drag them into Unity's asset pipeline. See the `python-scripting` skill for the full protocol (venv conventions, C# code-gen style, constraints).
