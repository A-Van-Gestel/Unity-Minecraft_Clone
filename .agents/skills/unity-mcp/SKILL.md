---
name: unity-mcp
description: Reference card and recipes for the Unity MCP tools — live editor interaction, runtime inspection, profiling, visual capture, and programmatic code generation. Use when you need to interact with the running Unity Editor rather than just reading/writing files.
---

<objective>
The Unity Editor exposes tools via the `unity-mcp` MCP server that provide capabilities file reads cannot: inspecting runtime state, reading `[SerializeField]` values from binary scene files, querying the profiler, executing arbitrary C# in the editor, and triggering code generation.

This skill is a **reference card**. Domain-specific workflows (when and why to use these tools) live in the domain skills (`voxel-debugging`, `burst-optimization`, `review-changes`, `unity-file-ops`, `chunk-lifecycle`, `refactor-safely`). This skill is the authoritative source for **how** to call each tool correctly.
</objective>

<essential_principles>

**RunCommand is the most powerful tool — and the most dangerous.**

- Class MUST be named `CommandScript` (any other name causes NullReferenceException).
- MUST use `internal`, not `public` (causes "Inconsistent Accessibility" error).
- Do NOT run blocking or long-running code — it freezes the editor.
- Use `result.Log()` / `result.LogWarning()` / `result.LogError()` for output.
- Use `result.RegisterObjectCreation(obj)` after creating objects, `result.RegisterObjectModification(obj)` BEFORE modifying objects, `result.DestroyObject(obj)` instead of `Object.DestroyImmediate`.

**Profiler tools require an active profiling session.**
Always check `Unity_ManageEditor` -> `GetState` before calling any profiler tool. They return empty/error without profiling data (play mode with profiler recording).

**Play/Pause/Stop affects the user's editor state.**
ALWAYS confirm with the user before calling `Unity_ManageEditor` with `Play`, `Pause`, or `Stop`.

**Asset paths use forward slashes** relative to the project root (e.g. `"Assets/Scripts/World/Chunk.cs"`).

**Camera_Capture is expensive** — it renders a full frame. Only use when visual verification is genuinely needed.

**ValidateScript at "standard" level may produce false positives.** Treat output as hints, not hard failures.

</essential_principles>

<quick_start>

**Quick Reference**

| Tool                           | One-liner                                         |
|--------------------------------|---------------------------------------------------|
| `Unity_RunCommand`             | Execute arbitrary C# in the editor process        |
| `Unity_ReadConsole`            | Read/clear console logs with filtering            |
| `Unity_ManageScene`            | Scene hierarchy, active scene, build settings     |
| `Unity_ManageGameObject`       | Find objects, read components + serialized fields |
| `Unity_ManageMenuItem`         | Execute editor menu items programmatically        |
| `Unity_ManageAsset`            | Search assets, get GUIDs, asset metadata          |
| `Unity_ManageEditor`           | Play/Pause/Stop, editor state, tags/layers        |
| `Unity_Camera_Capture`         | Render from a camera for visual verification      |
| `Unity_ValidateScript`         | Unity-aware script lint (GC in hot paths, etc.)   |
| `Unity_PackageManager_GetData` | Check installed package versions                  |
| `Unity_FindInFile`             | Regex search with SHA256 verification             |
| **Profiler tools (10)**        | GC allocations, frame timing, bottom-up analysis  |

</quick_start>

<routing>

Based on what you need, read the appropriate reference:

| Need                                                    | Reference                                        |
|---------------------------------------------------------|--------------------------------------------------|
| Tool parameters, examples, recipes for a specific tool  | [references/tools.md](references/tools.md)       |
| Profiler tools, GC/timing analysis, drill-down workflow | [references/profiler.md](references/profiler.md) |
| End-to-end patterns, common tasks, constraints/gotchas  | [references/recipes.md](references/recipes.md)   |

For domain-specific workflows (when and why), consult the domain skill instead:

| Domain                                 | Skill                |
|----------------------------------------|----------------------|
| Debugging chunks, lighting, fluids     | `voxel-debugging`    |
| Performance optimization, Burst jobs   | `burst-optimization` |
| Code review, pre-merge checks          | `review-changes`     |
| File moves, renames, GUID verification | `unity-file-ops`     |
| Chunk pipeline state transitions       | `chunk-lifecycle`    |
| Safe renames, dead code removal        | `refactor-safely`    |

</routing>

<reference_index>
All reference material in `references/`:

**Tool details:** tools.md — parameters, rules, and example calls for all 12 named tools
**Profiler:** profiler.md — all 10 profiler tools organized by category with drill-down workflow
**Patterns:** recipes.md — 6 end-to-end recipes for common tasks + 8 documented constraints/gotchas
</reference_index>

<success_criteria>
This skill is used correctly when:

- You checked **essential_principles** before calling any unity-mcp tool
- You loaded the specific reference file you need, not the entire skill
- You used the correct parameters and followed the tool-specific rules
- You confirmed with the user before Play/Pause/Stop actions
- You verified profiler data exists before calling profiler tools
</success_criteria>
