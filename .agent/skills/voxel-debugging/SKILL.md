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

1. **DO NOT GUESS:** Do not offer a hypothetical fix immediately if the root cause is not 100% obvious. Searching in the dark breaks things in a multi-threaded engine.
2. **INSTRUMENT FIRST:** Generate a "Diagnostic Patch" instead of a fix.
    - Add targeted `Debug.Log` statements to trace data flow.
    - Suggest creating temporary `OnDrawGizmos` to visualize the data state.
3. **BURST JOB LOGGING:** If the bug is inside a Burst Job (`Assets/Scripts/Jobs/`), you must use `Unity.Collections.LowLevel.Unsafe.UnsafeUtility` or `Debug.Log` strictly with **FixedStrings/String Literals only**. Do not use string interpolation (`$""`) in Jobs.
4. **VERIFY ASSUMPTIONS:** State explicitly what you are trying to test (e.g., "We need to verify if the neighbor chunk is actually providing the correct data index before we change the meshing logic").
5. **WAIT:** Wait for the user to run the game and provide the diagnostic logs before writing the actual bug fix.
