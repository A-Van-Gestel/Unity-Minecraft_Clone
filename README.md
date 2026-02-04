# Voxel Engine Project (Minecraft Clone)

![Unity Version](https://img.shields.io/badge/Unity-6000.3-black?logo=unity)

A high-performance voxel sandbox engine built in Unity, inspired by Minecraft. This project leverages modern Unity technologies, including the **Job System** and **Burst Compiler**, to create a flexible and efficient procedural world.

## ✨ Key Features

### Core Engine Features
*   **Procedural World Generation:** A (not yet) infinite, chunk-based world generated using Perlin noise.
*   **Multi-threaded Architecture:** Heavy lifting tasks like chunk generation, mesh building, and lighting calculations are offloaded to background threads using the C# Job System.
*   **Burst-Optimized Meshing:** The entire mesh generation pipeline is heavily optimized with the Burst Compiler for maximum performance.
*   **Dynamic Lighting Engine:** A custom flood-fill lighting system that supports both sunlight (sky light) and block light (torches, lava), calculated asynchronously.
*   **Fluid Simulation:** A performant system for "water-like" and "lava-like" fluids, including flowing and smoothed visuals via a custom shader.
*   **Custom Block & Mesh Support:** Easily extend the game with new block types, including blocks with custom 3D models.
*   **Data-Driven Design:** Block properties are managed through a `BlockDatabase` (ScriptableObject), allowing for easy editing and expansion.

### Gameplay Features
*   **Infinite Terrain:** Explore a vast world with multiple biomes, caves, and ore generation. (Infinite worlds are WIP, but technically supported)
*   **Block Interaction:** Place and break blocks to shape the world.
*   **Player Controller:** A physics-based character controller supporting walking, sprinting, jumping, and flying.
*   **Creative Mode Inventory:** A full UI for accessing any block in the game.
*   **Save & Load System:** World data, including all modifications, can be saved to disk and reloaded.

## ⚙️ Technical Overview

This project is built around a data-oriented and multi-threaded philosophy to handle the scale of a voxel world.

*   **World Data:** Voxel data is stored efficiently in `uint` arrays using bit-packing to store ID, light levels, orientation, and fluid levels in a single integer per block.
*   **Chunk Management:** The world is divided into 16x128x16 chunks. Chunks are loaded/unloaded around the player based on view distance.
*   **Job Pipeline:**
    1.  **Generation Job:** Creates the raw voxel data for a chunk (terrain, biomes, ores).
    2.  **Lighting Job:** Calculates sunlight and block light propagation after generation or modification.
    3.  **Mesh Generation Job:** A Burst-compiled job that builds the visual mesh for a chunk, handling face culling, custom meshes, and fluid surfaces.
*   **Shaders:** Custom shaders are used for standard blocks, transparent blocks, and a more advanced "Uber" shader for animated fluid effects.
*   **Editor Tools:** A custom "Block Editor" window allows for easy management of all block types, textures, and properties directly within the Unity Editor.

## 🚀 Getting Started

### Prerequisites
*   **Unity Hub**
*   **Unity 6.3** (6000.3.6f1 at time of writing)
*   A code editor like **Visual Studio** or **JetBrains Rider**.

### Setup
1.  **Clone the repository:**
    ```bash
    git clone https://github.com/A-Van-Gestel/Unity-Minecraft_Clone.git
    ```
2.  **Open the project:**
    *   Open Unity Hub.
    *   Click "Add" and select the cloned project folder.
    *   Open the project with the correct Unity version.
3.  **Run the game:**
    *   Open the `MainMenu` or `World` scenes located in `Assets/Scenes/`.
    *   Press the Play button.

## 📁 Project Structure

*   `Assets/Scripts/`: Contains all the core C# source code.
    *   `Data/`: Structs, ScriptableObjects, and data containers (e.g., `ChunkData`, `BlockType`).
    *   `Jobs/`: All `IJob` structs for the Job System (e.g., `MeshGenerationJob`).
    *   `Helpers/`: Static helper classes, including Burst-compiled `VoxelMeshHelper`.
    *   `UI/`: Scripts related to the user interface.
*   `Assets/Editor/`: Custom editor scripts and windows, like the `BlockEditorWindow`.
*   `Assets/Shaders/`: All custom HLSL shaders for rendering blocks and fluids.
*   `Assets/Resources/`: Assets that need to be loaded by script, such as `FluidMeshData`.
*   `/Documentation/`: High-level documentation about the project's architecture, style guides, and technical deep-dives. **New developers should start here!**

## 🗺️ Future Development

This project is a functional sandbox. Future work could include:

*   [ ] **Survival Mode:** Health, hunger, crafting, and inventories.
*   [ ] **More Block Types:** Add interactive blocks (doors, chests) and logic blocks.
*   [ ] **Performance Optimization:** Further refine job dependencies and memory management.
*   [ ] **Multiplayer:** Refactor the architecture to support a client-server model.
