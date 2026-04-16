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
2. **DO NOT GUESS:** Do not offer a hypothetical fix immediately if the root cause is not 100% obvious. Searching in the dark breaks things in a multi-threaded engine.
3. **INSTRUMENT FIRST:** Generate a "Diagnostic Patch" instead of a fix.
    - Add targeted `Debug.Log` statements to trace data flow.
    - Suggest creating temporary `OnDrawGizmos` to visualize the data state.
    - Check `@Documentation/Guides/DEBUG_METHODS_EXAMPLES.md` for existing debug tools (e.g. `DebugRaycastChunkState`) before writing new instrumentation.
4. **BURST JOB LOGGING:** If the bug is inside a Burst Job (`Assets/Scripts/Jobs/`), you must use `Unity.Collections.LowLevel.Unsafe.UnsafeUtility` or `Debug.Log` strictly with **FixedStrings/String Literals only**. Do not use string interpolation (`$""`) in Jobs.
5. **VERIFY ASSUMPTIONS:** State explicitly what you are trying to test (e.g., "We need to verify if the neighbor chunk is actually providing the correct data index before we change the meshing logic").
6. **WAIT:** Wait for the user to run the game and provide the diagnostic logs before writing the actual bug fix.
7. **ARCHIVE ON CONFIRMATION:** After the user confirms the fix works, use the `archive-fixed-bug` skill to move the bug entry to `_FIXED_BUGS.md`. Do not archive pre-emptively.
