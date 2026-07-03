---
name: voxel-debugging
description: Instrument-first diagnostic protocol for complex engine bugs — check known-bugs docs, locate code via CodeGraph, inspect live editor state, ship a diagnostic patch, and wait for logs before fixing. Use when the user asks you to fix a complex bug, undefined behavior, lighting issues, fluid flow problems, or chunk generation glitches. Once the root cause is understood and the system has a validation suite, hand off to the validation-driven-bugfix skill.
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
1. **LOCATE THE CODE:** Use the CodeGraph MCP to find the suspected surface area before instrumenting.
    - `codegraph_search` to find code related to the symptom (e.g., "propagateLight", "fluidLevel").
    - `codegraph_callers` / `codegraph_callees` to trace call chains into and out of the suspected function.
    - `codegraph_explore` to view the relevant implementations and interfaces grouped contextually.
    - `codegraph_impact` on suspected files/structs to see what else depends on them before altering them.
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
8. **SWITCH TO VALIDATION-DRIVEN FIXING WHERE A SUITE EXISTS:** Once the root cause is understood, check whether the affected system has an editor validation suite (e.g. lighting: `Minecraft Clone/Dev/Validate Lighting Engine`). If so, hand off to the `validation-driven-bugfix` skill — write the deterministic repro scenario first, then fix against it instead of iterating purely on in-game logs.
9. **ARCHIVE ON CONFIRMATION:** After the user confirms the fix works, use the `archive-fixed-bug` skill to move the bug entry to `_FIXED_BUGS.md`. Do not archive pre-emptively.
