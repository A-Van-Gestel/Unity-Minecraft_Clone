# Lighting System Overview

This document provides a technical overview of the voxel lighting engine. The system is designed to be asynchronous, multi-threaded, and capable of handling two distinct types of light: **Sunlight** and **Blocklight**.

## 1. Core Concepts

### Data Representation

Light is not stored as a separate component. Instead, it's packed directly into each voxel's `uint` data.

- **Sunlight:** A 4-bit value (0-15), representing light from the sky.
- **Blocklight:** A 4-bit value (0-15), representing light emitted from sources like torches or lava.
- **Final Light Value:** For rendering, the GPU uses the *maximum* of the Sunlight and Blocklight values for a given voxel. A value of 15 is full brightness, and 0 is complete darkness.

### The Algorithm: Flood-Fill (BFS)

The core of the system is a Breadth-First Search (BFS) flood-fill algorithm. When a block's light value changes, it triggers an update that propagates through its neighbors. There are two main operations:

1. **Light Propagation (Addition):**
    - Occurs when a light source is placed or a block is removed, exposing an area to light.
    - A `LightQueue` is seeded with the source position.
    - The algorithm spreads outwards, adding neighbors to the queue. The light level decreases by `1 + opacity` for each step.
    - This continues until the light level drops to 0.

2. **Darkness Propagation (Removal):**
    - Occurs when a light source is removed or a block is placed, obstructing light.
    - This is a two-step process:
        1. **Removal Pass:** The algorithm spreads outwards from the source of darkness, setting the light level of affected voxels to 0 and adding their old light level and position to a `LightRemovalNode` queue. This carves out the old light.
        2. **Re-Propagation Pass:** The neighbors of the now-darkened area are added to the `LightQueue`. This allows remaining light sources on the edge of the darkened area to "fill back in," recalculating the new lighting correctly.

## 2. Sunlight vs. Blocklight

- **Blocklight** is simple. It originates from a block with a `lightEmission` value > 0 and propagates outwards in all directions.

- **Sunlight** is more complex and has special rules:
    - **Source:** The source of all sunlight is the sky (Y = 127), with a value of 15.
    - **Initial Calculation (Heightmap):** When a chunk is generated, a `heightMap` is created that stores the Y-level of the highest *light-obstructing* block for each (X, Z) column.
    - **Vertical Pass:** Sunlight is cast straight down from the sky.
        - All voxels above the heightmap value in a column are set to 15.
        - Light then attenuates as it travels down through transparent blocks below the heightmap level.
        - Sunlight does *not* diminish when traveling straight down through fully transparent blocks (like air).
    - **Horizontal Propagation:** After the vertical pass, sunlight spreads horizontally from the lit areas using the same flood-fill algorithm as blocklight.

## 3. The Asynchronous Update Loop

The entire process is managed by the `WorldJobManager` and is designed to run in the background without freezing the game. Here is the lifecycle of a single block modification:

1. **Modification (`ChunkData.ModifyVoxel`)**
    - A player places or breaks a block.
    - The old sunlight and blocklight values of that voxel are captured.
    - The voxel itself and its 6 immediate neighbors are added to the chunk's internal `_sunlightBfsQueue` and `_blocklightBfsQueue`.
    - The chunk is marked as dirty (`HasLightChangesToProcess = true`).

2. **Scheduling (`WorldJobManager.ScheduleLightingUpdate`)**
    - On the main thread, the `World` update loop scans for active chunks with `HasLightChangesToProcess`.
    - If a chunk is found, it checks if all its 8 neighbors have finished generating their initial data.
    - If neighbors are ready, it creates a `NeighborhoodLightingJob` and schedules it. All pending light updates for that chunk are passed to the job.

3. **The Job (`NeighborhoodLightingJob`)**
    - **Inputs:** The job receives the central chunk's map (writable), and read-only copies of the maps for all 8 neighbors.
    - **Execution:** The job runs the flood-fill algorithm. It can read voxel data from the full 3x3 grid of chunks.
    - **Cross-Chunk Logic:** If the algorithm needs to change the light value of a voxel in a *neighbor* chunk, it cannot write to that neighbor's map. Instead, it adds a `LightModification` struct to an output list (`CrossChunkLightMods`).
    - **Output:** The job outputs the modified central chunk map, the list of `CrossChunkLightMods`, and a boolean flag (`IsStable`) indicating if it completed all possible light calculations in one pass.

4. **Processing Results (`WorldJobManager.ProcessLightingJobs`)**
    - Back on the main thread, the `World` checks for completed lighting jobs.
    - It copies the job's modified map data back to the `ChunkData`.
    - It iterates through the `CrossChunkLightMods` list. For each modification, it:
        1. Applies the light change directly to the neighbor `ChunkData`.
        2. Queues a *new* light update in that neighbor chunk, continuing the cascade.
    - If the job's `IsStable` flag was `false`, it means more work is needed, so `HasLightChangesToProcess` is set to `true` again for another pass in a future frame.
    - If `IsStable` is `true`, the chunk is now considered fully lit, and a mesh rebuild is requested.

This "pull" system (main thread schedules, job executes, main thread processes results) allows for complex lighting cascades that can span many chunks to be calculated over several frames without ever stalling the main thread.