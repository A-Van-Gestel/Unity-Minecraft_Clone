# Project Structure Guide

This document provides an overview of the folder structure for the Voxel Engine project. A consistent structure is crucial for keeping the project organized, scalable, and easy to navigate.

## Root Directory

The project root contains the standard Unity folders along with our own top-level directory for documentation.

-   `/Assets/` - Contains all game assets, scripts, and resources that will be included in the final build. **All game-related work happens here.**
-   `/Documentation/` - Contains all project documentation, including technical guides, design documents, and logs. These files are *not* part of the game build.
-   `/ProjectSettings/`, `/Packages/`, etc. - Standard Unity-generated folders.

---

## `/Assets/` Directory Breakdown

This is the primary folder for all game development.

### `Assets/Editor/`

-   **Purpose:** Contains all scripts that run exclusively within the Unity Editor. This includes custom editor windows, inspectors, and asset generation tools.
-   **Key Rule:** Code in this folder is wrapped in `#if UNITY_EDITOR` or is inside a class that inherits from an `Editor` type. It will not be included in the final game build.
-   **Examples:**
    -   `BlockEditorWindow.cs`: The custom UI for editing all `BlockType` assets.
    -   `AtlasPacker.cs`: The tool for combining individual block textures into a single texture atlas.
    -   `FluidDataGenerator.cs`: A script to pre-calculate and save fluid mesh data as `ScriptableObject` assets.

### `Assets/Scripts/`

This is the heart of the project, containing all C# source code. It is organized by functionality.

#### `Scripts/Core/` (Implicit - Root of `Scripts/`)

-   **Purpose:** Contains the main `MonoBehaviour` singletons and core architectural classes that manage the game loop and tie all systems together.
-   **Examples:**
    -   `World.cs`: The central orchestrator for the entire game. Manages chunks, jobs, and the player.
    -   `Player.cs`: The player controller, handling movement and input.
    -   `Chunk.cs`: The in-game representation of a chunk, holding the `GameObject` and managing its lifecycle.
    -   `BlockBehavior.cs`: Static class for defining special block logic like grass spreading and fluid flow.

#### `Scripts/Data/`

-   **Purpose:** Defines the "nouns" of the project. These are data containers, `ScriptableObjects`, and serializable classes that hold state but contain minimal logic.
-   **Guiding Principle:** This folder separates data from behavior. The logic that acts upon this data is found elsewhere (e.g., in `Jobs/` or `Core/`).
-   **Examples:**
    -   `BlockType.cs`: A serializable class defining all properties of a single block.
    -   `ChunkData.cs`: The serializable data for a chunk, including its `uint[]` voxel map.
    -   `BlockDatabase.cs`: A `ScriptableObject` that holds the master list of all block types and materials.
    -   `JobData.cs`: A collection of job-safe `structs` used to pass data to and from the Job System.

#### `Scripts/Helpers/`

-   **Purpose:** Contains `static` utility classes that provide reusable functions. These classes typically do not hold any state. They are the "verbs" or tools of the project.
-   **Examples:**
    -   `VoxelMeshHelper.cs`: A Burst-compiled static class with methods for generating mesh data for different voxel types (cubes, custom, fluids).
    -   `ResourceLoader.cs`: Handles loading assets from the `Resources` folder.
    -   `VoxelHelper.cs`: Provides utility functions for voxel math, like calculating face indices based on orientation.

#### `Scripts/Jobs/`

-   **Purpose:** The home for all multi-threaded logic. This folder contains all structs that implement the `IJob` interface.
-   **Subfolders:**
    -   `Jobs/BurstData/`: Contains low-level, Burst-specific helper structs and static classes (e.g., `BurstVoxelDataBitMapping.cs`).
    -   `Jobs/Data/`: Contains structs that define the input/output data containers for a specific job (e.g., `GenerationJobData.cs`).
-   **Examples:**
    -   `MeshGenerationJob.cs`: The highly optimized Burst job that builds a chunk's visual mesh.
    -   `ChunkGenerationJob.cs`: The job responsible for generating the initial voxel data for a new chunk.
    -   `NeighborhoodLightingJob.cs`: The job that calculates light propagation within a 3x3 chunk area.

#### `Scripts/UI/`

-   **Purpose:** Contains scripts that are directly related to managing UI elements and windows.
-   **Examples:**
    -   `TitleMenu.cs`: Logic for the main menu screen.
    -   `UIItemSlot.cs`: Manages the visual representation of an item slot in the inventory or toolbar.

### `Assets/Shaders/`

-   **Purpose:** Contains all custom shader code (`.shader` files) used for rendering.
-   **Examples:**
    -   `StandardBlockShader.shader`: The primary shader for opaque voxels.
    -   `TransparentBlockShader.shader`: A cutout shader for blocks like leaves and glass.
    -   `UberLiquidShader.shader`: The advanced shader for rendering animated and interactive water and lava.