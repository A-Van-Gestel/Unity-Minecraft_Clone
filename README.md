# Voxel Engine Project (Minecraft Clone)

![Unity Version](https://img.shields.io/badge/Unity-6000.4-black?logo=unity)

A high-performance voxel sandbox engine built in Unity, inspired by Minecraft. This project leverages modern Unity technologies, including the **Job System** and **Burst Compiler**, to create a flexible and efficient procedural world.

## Key Features

### Core Engine Features

* **Procedural World Generation:** A chunk-based world generated using [FastNoiseLite](https://github.com/Auburn/FastNoiseLite) (Burst-compiled port) with support for multiple noise types (Cellular, Fractal Ridged, Domain Warp) and a modular biome system.
* **Multi-threaded Architecture:** Heavy lifting tasks like chunk generation, mesh building, and lighting calculations are offloaded to background threads using the C# Job System.
* **Burst-Optimized Section Meshing:** The world is rendered as independent 16x16x16 section meshes (sub-chunk meshing) using the Advanced Mesh API, fully optimized with the Burst Compiler.
* **Dynamic Lighting Engine:** A custom async BFS flood-fill lighting system that supports both sunlight (sky light) and block light (torches, lava), calculated asynchronously across chunk boundaries.
* **Fluid Simulation:** A performant system for "water-like" and "lava-like" fluids, including flowing, source regeneration, optimal flow pathfinding, and smoothed visuals via a custom shader.
* **Region-Based Save System:** World data is persisted using a custom region-based binary format with LZ4 compression, supporting async I/O and an AOT (Ahead-Of-Time) migration pipeline for safe save-format upgrades.
* **Custom Block & Mesh Support:** Easily extend the game with new block types, including blocks with custom 3D models, managed through a `BlockDatabase` (ScriptableObject) and an in-editor `BlockEditor` tool.

### Editor Tooling

* **Block Editor:** A custom editor window for creating, modifying, and managing all block definitions (solid, transparent, fluid). Includes live 3D mesh previews and pixel-perfect isometric icon generation — no Play Mode required.
* **Auto-Generated Block IDs:** The `BlockIdGenerator` parses the `BlockDatabase` asset and generates a compile-time `BlockIDs` static class (`BlockIDs.Stone`, `BlockIDs.Grass`, etc.) that is Burst-safe and used throughout the codebase. Triggered via the `Minecraft Clone > Generate Block IDs` menu.
* **Atlas Packer:** Automatically reads block textures and packs them into a strict `Texture2DArray` asset for GPU-efficient rendering.
* **Noise Preview:** A real-time noise visualization window for previewing and tuning FastNoiseLite configurations used by the world generation system.
* **Fluid Data Generator:** Generates the vertex-height gradient templates used by the mesh generation job for water/lava surface slopes.
* **World Editor:** Debug utilities for inspecting and modifying active world/chunk serialization state from the Inspector.

### Gameplay Features

* **Scalable Terrain:** Explore a world with multiple biomes, caves, and ore generation. The underlying architecture supports large-scale worlds (tested at 100,000 x 100,000 chunks), though rendering at extreme distances requires a world origin shift system (not yet implemented). Currently hard-limited to 100x100 chunks.
* **Block Interaction:** Place and break blocks to shape the world.
* **Player Controller:** A physics-based character controller supporting walking, sprinting, jumping, and flying.
* **Creative Mode Inventory:** A full UI for accessing any block in the game.
* **Save & Load System:** World data, including all player modifications, is saved to disk using region files and can be reloaded. Save formats are forward-compatible via the AOT migration system.

## Technical Overview

This project is built around a data-oriented and multi-threaded philosophy to handle the scale of a voxel world.

* **World Data:** Voxel data is stored efficiently in `uint` arrays using bit-packing to store block ID (16 bits), sunlight level (4 bits), block light level (4 bits), and metadata (8 bits) in a single integer per block.
* **Chunk Management:** The world is divided into 16x128x16 chunks, further subdivided into 16x16x16 sections. Chunks are loaded/unloaded around the player based on view distance.
* **Job Pipeline:**
    1. **Generation Job:** Creates the raw voxel data for a chunk (terrain, biomes, ores) using Burst-compiled FastNoiseLite.
    2. **Lighting Job:** Calculates sunlight and block light propagation via BFS flood-fill after generation or modification, with cross-chunk boundary handling.
    3. **Mesh Generation Job:** A Burst-compiled job that builds the visual mesh for each 16x16x16 section, handling face culling, custom meshes, fluid surfaces, and shoreline foam effects.
* **Serialization:** Region files store chunks in a Minecraft-like format with LZ4/GZip compression. An AOT migration pipeline runs before scene load to upgrade save files between versions safely.
* **Shaders:** Custom shaders are used for standard blocks, transparent blocks, and a more advanced "Uber" shader for animated fluid effects with directional flow and waterfall rendering.
* **Editor Tools:** A custom `BlockEditor` window allows for easy management of all block types, textures, and properties directly within the Unity Editor. Block IDs are auto-generated from the database.

## Getting Started

### Prerequisites

* **Unity Hub**
* **Unity 6.4** (6000.4.2f1 at time of writing)
* A code editor like **Visual Studio** or **JetBrains Rider**.

### Setup

1. **Clone the repository:**
   ```bash
   git clone https://github.com/A-Van-Gestel/Unity-Minecraft_Clone.git
   ```
2. **Open the project:**
    * Open Unity Hub.
    * Click "Add" and select the cloned project folder.
    * Open the project with the correct Unity version.
3. **Run the game:**
    * Open the `MainMenu` or `World` scenes located in `Assets/Scenes/`.
    * Press the Play button.

## Project Structure

* `Assets/Scripts/`: Contains all the core C# source code.
    * `Data/`: Structs, ScriptableObjects, and data containers (e.g., `ChunkData`, `BlockType`, `BlockIDs`).
    * `Jobs/`: All `IJob` structs for the Job System (e.g., `MeshGenerationJob`, `StandardChunkGenerationJob`).
    * `Helpers/`: Static helper classes, including Burst-compiled `VoxelMeshHelper` and pooling utilities.
    * `Serialization/`: Region file I/O, save/load logic, and the AOT migration pipeline.
    * `Legacy/`: Sealed legacy modules (original generation system) kept for regression comparison.
    * `UI/`: Scripts related to the user interface.
* `Assets/Editor/`: Custom editor tools (no runtime code).
    * `BlockEditor/`: Block Editor window, icon generator, and live mesh preview helpers.
    * `DataGeneration/`: Offline generators for `BlockIDs.cs`, fluid vertex templates, and game action enums.
    * `AtlasPacker/`: Texture atlas packing pipeline for `Texture2DArray` assets.
    * `WorldTools/`: Noise preview window and world serialization inspector.
* `Assets/Shaders/`: All custom HLSL shaders for rendering blocks and fluids.
* `Assets/Resources/`: Assets that need to be loaded by script, such as `FluidMeshData`.
* `Documentation/`: Project documentation organized by purpose:
    * `Architecture/` — How implemented systems work (authoritative references).
    * `Guides/` — Developer references (coding style, optimization, Burst rules).
    * `Design/` — Proposals and specs for features not yet implemented.
    * `Bugs/` — Active bug tracker and fixed-bugs archive.

## Future Development

This project is an active sandbox. Current and planned work includes:

*   [ ] **Modular World Generation:** Expanding the biome system with configurable world types, biome blending, and new terrain features.
*   [ ] **Infinite World Support:** Implementing a world origin shift system to eliminate floating-point precision loss at extreme distances, removing the current 100x100 chunk hard limit.
*   [ ] **More Block Types:** Add interactive blocks (doors, chests) and logic blocks.
*   [ ] **Survival Mode:** Health, hunger, crafting, and inventories.
*   [ ] **Multiplayer:** Refactor the architecture to support a client-server model.
