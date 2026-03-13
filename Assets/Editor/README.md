# Voxel Engine Editor Tools

Welcome to the `Assets/Editor` directory. This folder strictly contains custom Unity Editor GUI windows, pipeline utilities, and offline data-generation tools utilized during the development of the high-performance DOTS Voxel Engine.

*No runtime scripts belong in this folder.*

---

## Architecture & Directory Structure

To keep the Editor tools scalable, they are organized using a Feature-Based approach:

### 📈 Benchmarking (`/Benchmarking`)

Tools intended to stress-test or analyze C# or Burst compilation performance.

- **`RecalculateCountsBenchmark.cs`**: Performance benchmark analysis tool tracking light mapping and block-state recalculation logic specifically optimized for the chunk handling engine.

### 🧱 Block Editor (`/BlockEditor`)

The central feature for adding custom data-oriented Blocks to the engine.

- **`BlockEditorWindow.cs`**: The main GUI window/entry-point for creating, modifying, and managing solid, transparent, and fluid voxel definitions.
- **`/Helpers`**:
    - **`BlockIconGenerator.cs`**: Specialized PreviewRenderUtility script to render mathematically accurate, pixel-perfect isometric 2D icons of complex meshes with custom face lighting.
    - **`EditorMeshGenerator.cs`**: Leverages the `VoxelMeshHelper` to build in-editor Mesh approximations of voxel models for GUI live-previews without entering Play Mode.

### 🧠 Data Generation (`/DataGeneration`)

Offline generation pipelines used to produce highly-optimized data states or metadata structures for the Burst Compiler.

- **`BlockIdGenerator.cs`**: Automates parsing of Block Editor states and dynamically generates the global lookup IDs required by Voxel data sets.
- **`FluidDataGenerator.cs`**: Responsible for creating/managing the specific vertex-height gradient templates used heavily by the Voxel Mesher when calculating custom water and lava slopes.

### ⚙️ Project Utilities (`/ProjectUtilities`)

General project scope pipelines and workflow tools.

- **`AtlasPacker.cs`**: Dedicated pipeline step for automatically reading custom pixel-art block textures and safely packing/stitching them into the heavily strict Unity `Texture2DArray` asset format.
- **`GameVersionManager.cs`**: Project-management utility tool to seamlessly control build stamps, revisions, and internal version data serialization.

### 🌍 World Tools (`/WorldTools`)

Tools dedicated to manipulating or debugging the serialized endless-world saves.

- **`WorldEditor.cs`**: Contains Unity Editor utilities to debug/modify active world/chunk serialization streams and inspect generated terrain from the Inspector view.
