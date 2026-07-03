# Editor Suite Map — Existing Tools & Pattern Exemplars

Companion reference for the `editor-tool` skill: what already exists under `Assets/Editor/`,
and which file to read as the exemplar when implementing a pattern. Check this before building
a new tool — extending an existing window (or copying its structure) usually beats starting
from scratch.

## Tool inventory

| Folder               | Tool                                                                                          | Notes                                                                                             |
|----------------------|------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------|
| `WorldTools/`        | `WorldGenPreviewWindow` (partials: `WorldType`, `CrossSection`, `BiomeEditor`, `NoiseChannels`, `WorldBlending`) | Flagship multi-tab window: world-gen previews + biome editing                                     |
| `WorldTools/`        | `ChunkPreview3DWindow` (partials: `Pipeline`, `Rendering`, `UI`)                              | Real-pipeline 3D chunk preview via `EditorChunkPipelineRunner`                                    |
| `BlockEditor/`       | `BlockEditorWindow` (partials: `BlockEditor`, `TagManager`; helpers: `BlockIconGenerator`, `EditorMeshGenerator`) | Block database editing; writes `BlockDatabase.asset`                                             |
| `AtlasPacker/`       | `AtlasPackerWindow` + `AtlasConfiguration`                                                     | Texture atlas packing                                                                             |
| `StructureEditor/`   | `StructurePreviewWindow`                                                                       | Structure template preview                                                                        |
| `CreditsEditor/`     | `CreditsEditorWindow`                                                                          | Credits database editing (`REFERENCES_AND_CREDITS` policy)                                        |
| `Dev/`               | `CaveDensityAnalyzer`                                                                          | Static-API analysis tool, invoked via `Unity_RunCommand` (see the `cave-tuning` skill)            |
| `DataGeneration/`    | `BlockIdGenerator`, `FluidDataGenerator`, `GameActionGenerator`, `PlacementTagMigration` + `Editor*DatabaseCache` | Code generators behind `Minecraft Clone/*` menu items + asset caches                              |
| `ProjectUtilities/`  | `AssetReserializer`, `GameVersionManager`                                                      | Project maintenance utilities                                                                     |
| `PropertyDrawers/`   | `BlockIDDrawer`                                                                                | Custom drawer for block-ID fields                                                                 |
| `Jobs/`              | `NoisePreviewJob`, `WorldBlendingPreviewJob`                                                   | Editor-only Burst preview jobs (the §"Burst Jobs for Heavy Preview Generation" pattern)          |
| `Validation/`        | Lighting / Meshing / Behavior / Placement / MeshQueue / LightScheduler suites                 | Owned by the `validation-driven-bugfix` skill — not general editor tooling                        |
| `Benchmarking/`      | `ActiveVoxelScanBenchmark`, `RecalculateCountsBenchmark`                                       | One-off perf measurement harnesses                                                                |

## Pattern → exemplar

| Pattern                                                | Read this exemplar                                                        |
|--------------------------------------------------------|-----------------------------------------------------------------------------|
| Partial-class-per-tab window, tab router in core file | `WorldTools/WorldGenPreviewWindow.cs` + its tab partials                  |
| Sub-tabs within a tab                                  | `WorldTools/WorldGenPreviewWindow.BiomeEditor.cs`                         |
| Debounced regeneration on slider drag                  | `WorldTools/ChunkPreview3DWindow.cs` (`EditorDebounceTimer` wiring)       |
| Cross-window settings sync                             | `WorldTools/ChunkPreview3DWindow.cs` (`WorldGenPreviewSettings` consumer) |
| Editor-time run of the REAL chunk pipeline             | `WorldTools/ChunkPreview3DWindow.Pipeline.cs`                             |
| Cross-section panels (crosshair, borders, sea level)   | `WorldTools/WorldGenPreviewWindow.CrossSection.cs`                        |
| Inline config validation with severity boxes           | `WorldTools/WorldGenPreviewWindow.BiomeEditor.cs` (`BiomeConfigValidator`) |
| Burst `IJobParallelFor` → `Texture2D` preview          | `Jobs/NoisePreviewJob.cs` and its window call sites                       |
| Manual field editing on an in-memory database copy     | `BlockEditor/BlockEditorWindow.BlockEditor.cs`                            |
| 3D mesh preview widget usage                           | `StructureEditor/StructurePreviewWindow.cs`, `BlockEditor/`               |
| Static-API tool driven by `Unity_RunCommand`           | `Dev/CaveDensityAnalyzer.cs`                                              |
| Menu-item code generator writing `.cs` output          | `DataGeneration/BlockIdGenerator.cs`                                      |
