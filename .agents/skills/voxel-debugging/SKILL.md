---
name: voxel-debugging
description: Use when the user asks you to fix a complex bug, undefined behavior, lighting issues, fluid flow problems, or chunk generation glitches.
---

# Diagnostic Debugging Protocol

When debugging complex systems in this voxel engine, you must act as a Senior Systems Engineer. Do not blindly rewrite core logic.

## When to use this skill

- "Water isn't flowing correctly."
- "Lighting is broken at chunk borders."
- "Chunks are getting stuck in the generation queue."
- Any request to "fix a bug" in a core system.

## How to use it

0. **CHECK KNOWN BUGS FIRST:** Before anything else, scan the relevant category file in `@Documentation/Bugs/` (e.g. `LIGHTING_BUGS.md`, `FLUID_BUGS.md`, `CHUNK_MANAGEMENT_BUGS.md`, `JOB_SYSTEM_BUGS.md`) and `_FIXED_BUGS.md` to see if the symptom matches an open issue, a known limitation, or a previously-fixed bug that may have regressed.
1. **LOCATE THE CODE:** Use the code-review-graph MCP to find the suspected surface area before instrumenting.
    - `semantic_search_nodes` to find code related to the symptom (e.g. "light propagation", "fluid flow", "chunk meshing").
    - `query_graph` with `callers_of` / `callees_of` to trace call chains into and out of the suspected function.
    - `get_impact_radius` on suspected files to see what else depends on them.
    - `detect_changes` first — recent changes are the most common source of new bugs; check them before assuming a deep-rooted issue.
    - `get_flow` to visualize full execution paths through the suspected area.
2. **INSPECT LIVE STATE (unity-mcp):** Before guessing, use the Unity MCP to observe what's actually happening:
    - `Unity_ReadConsole` — check for errors/warnings/exceptions that correlate with the symptom. Filter by type (`Error`, `Warning`) and text.
    - `Unity_ManageGameObject` — find the affected chunk/object and inspect its component state, including `[SerializeField]` values not visible from code reads (e.g. `find` by name, then `get_components` with `include_non_public_serialized: true`).
    - `Unity_ManageScene` — verify which scene is active, check the hierarchy for expected objects (e.g. `GetHierarchy` with depth limit to find World, ChunkPoolManager, etc.).
    - `Unity_RunCommand` — execute arbitrary C# queries in the editor to inspect runtime state (e.g. query `World.Instance.LoadedChunks.Count`, check flag values on a specific chunk, read static counters).
    - `Unity_Camera_Capture` — capture visual evidence of the bug (lighting artifacts, mesh holes, rendering glitches).
    - `Unity_ManageEditor` → `GetState` — check if the editor is in play mode, has compilation errors, or is paused.
3. **DO NOT GUESS:** Do not offer a hypothetical fix immediately if the root cause is not 100% obvious. Searching in the dark breaks things in a multi-threaded engine.
4. **INSTRUMENT FIRST:** Generate a "Diagnostic Patch" instead of a fix.
    - Add targeted `Debug.Log` statements to trace data flow.
    - Suggest creating temporary `OnDrawGizmos` to visualize the data state.
    - Check `@Documentation/Guides/DEBUG_METHODS_EXAMPLES.md` for existing debug tools (e.g. `DebugRaycastChunkState`) before writing new instrumentation.
5. **BURST JOB LOGGING:** If the bug is inside a Burst Job (`Assets/Scripts/Jobs/`), you must use `Unity.Collections.LowLevel.Unsafe.UnsafeUtility` or `Debug.Log` strictly with **FixedStrings/String Literals only**. Do not use string interpolation (`$""`) in Jobs.
6. **VERIFY ASSUMPTIONS:** State explicitly what you are trying to test (e.g., "We need to verify if the neighbor chunk is actually providing the correct data index before we change the meshing logic").
7. **WAIT:** Wait for the user to run the game and provide the diagnostic logs before writing the actual bug fix.
8. **ARCHIVE ON CONFIRMATION:** After the user confirms the fix works, use the `archive-fixed-bug` skill to move the bug entry to `_FIXED_BUGS.md`. Do not archive pre-emptively.
