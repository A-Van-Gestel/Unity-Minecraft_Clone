# Shared Editor Libraries — API Catalog

Companion reference for the `editor-tool` skill: the reusable-before-you-write-new-code
inventory, with public surfaces. Verify exact signatures in the source file before calling —
this catalog names what exists and what it's for, not every overload/default.

## General-purpose — `Assets/Editor/Libraries/`

### `EditorGUIHelper.cs`

| Member                                                | Use for                                                          |
|-------------------------------------------------------|------------------------------------------------------------------|
| `IntFieldWithSteppers(value, min, max)`               | Int fields with ◀/▶ stepper buttons                              |
| `DrawSearchableSelectionList<T>(...)`                 | Filterable, scrollable selection lists with custom row rendering |
| `DrawCheckerboardBackground(rect)`                    | Transparency checkerboard behind preview textures                |
| `HandleDragRotation(position, rotation, sensitivity)` | Mouse drag rotation for 3D previews                              |
| `DrawSprite(position, sprite)`                        | Drawing atlas sprites in editor UI                               |

### `EditorUILayoutHelper.cs`

| Member                         | Use for                                                           |
|--------------------------------|--------------------------------------------------------------------|
| `SectionHeader(text)`          | 13pt bold section titles (uses `fixedHeight` to prevent clipping) |
| `SubHeader(text)`              | 11pt bold sub-section titles                                      |
| `SectionNote(text)`            | Muted grey description text (supports `<b>rich text</b>`)         |
| `BeginGroup()` / `EndGroup()`  | Visually grouped property boxes with padding                      |
| `ValidationBox(message, type)` | Inline validation/warning boxes (`MessageType` severity)          |
| `DrawSeparator()`              | 1px horizontal divider lines                                      |

### `EditorDebounceTimer.cs`

Debounce for expensive reactions to GUI changes (e.g. terrain regeneration while a slider drags).
Members: `Request(Action)`, `Poll()`, `Cancel()`, `IsPending`.

Wiring pattern (used by `WorldGenPreviewWindow` and `ChunkPreview3DWindow`):

```csharp
private readonly EditorDebounceTimer _debounceTimer = new EditorDebounceTimer(DEBOUNCE_SECONDS);

// In OnGUI / update:            _debounceTimer.Poll();
// On a change worth reacting to: _debounceTimer.Request(RegeneratePreview);
```

Only the latest `Request` fires, after the delay has elapsed since the last call.

### `EditorPreviewMaterialUtility.cs`

Centralized material caching for 3D mesh previews — use instead of creating materials directly.
`GetConfiguredMaterial(...)` + `DisposeCachedMaterials(ref blockMat, ref fluidMat)` (call in `OnDisable`).

### `MeshPreviewWidget.cs`

Encapsulates `PreviewRenderUtility` for 3D mesh rendering: camera, lighting, rotation, cleanup.
`Initialize()` in `OnEnable`, `Dispose()` in `OnDisable`. Two drawing modes:

- **Single-mesh:** `UpdatePreview(mesh, material, isFluid)` + `Draw(rect)`.
- **Multi-mesh scene:** `BeginDraw(rect)` → `DrawMesh(...)` / `DrawMeshDirect(...)` / `DrawWireCube(...)` / `DrawTransparentPlane(...)` → `EndDraw(rect)`.

Also: `HandleScrollZoom(rect, ...)`, camera/light properties (`CameraPosition`, `CameraFieldOfView`, `LightIntensity`, `PivotOffset`, `WireframeColor`, `ForceOpaque`, `BackgroundColor`).

### `CrossSectionBlockColorMap.cs`

Static block-ID → preview-color palette for cross-section renderers:
`GetBlockColor(blockID)`, `GetBlockName(blockID)`, `GetSkyColor(y, maxY)`, `GetWaterColor(y, seaLevel)`.

## World-gen tooling — `Assets/Editor/WorldTools/Libraries/`

### `CrossSectionPanelHelper.cs`

Panel drawing and interaction for cross-section / multi-panel terrain previews:
`GetFittedRect`, `DrawPanelTexture`, `DrawCrosshairOnPanel`, `DrawSeaLevelLine`,
`DrawChunkBordersVertical`, `DrawChunkBordersTopDown`, `HandlePanelClick`, `HandlePanelScroll`,
`EnsureTexture(ref tex, w, h)` (handles destroy + recreate on resize).

### `EditorChunkPipelineRunner.cs`

Runs the REAL runtime chunk pipeline (generation → structures → lighting, all Burst jobs reused
from the runtime) at editor time, without a `World` instance or MonoBehaviour lifecycle.
`IDisposable` — `Initialize(seed, worldType, blockDatabase, isSingleBiomeMode, selectedBiome)`,
`ScheduleGeneration(coord)`, `ExpandStructure(marker)`, `ScheduleLighting(...)`, `Dispose()`.

**Use this instead of hand-rolling editor-side generation** — a hand-rolled copy of the pipeline
drifts from production behavior. Exemplar consumer: `ChunkPreview3DWindow.Pipeline.cs`.

### `EditorJobDataManagerFactory.cs`

`Create(...)` builds the `(JobDataManager, FluidVertexTemplatesNativeData)` pair from a
`BlockDatabase` asset without a `World`. Thin wrapper over the shared runtime
`JobDataManagerFactory`, which owns the single copy of the flatten logic — never reimplement it.

### `WorldGenPreviewSettings.cs`

Static settings broker for cross-window synchronization (`WorldGenPreviewWindow` ↔
`ChunkPreview3DWindow`): `Publish(seed, worldType, crosshairPos, isSingleBiomeMode,
selectedBiome, seaLevel)`, read-only properties for each value, `Revision` counter, and an
`OnSettingsChanged` event. Subscribe in `OnEnable` (unsubscribe-then-subscribe to prevent
doubles), unsubscribe in `OnDisable`; compare `Revision` to detect missed updates.

### `BiomeConfigValidator.cs`

Static validation suite for `StandardBiomeAttributes` configs — detects noise-parameter
combinations that produce visual artifacts (steep cliffs, domain-warp folds, cave edge cases).
`Validate(biome, seaLevel)` → `List<BiomeValidationResult>` (message + `ValidationSeverity` +
sub-tab index), `FilterBySubTab(results, subTabIndex)`, `ValidateTrunkWormConfig(config)`.
Display results with `EditorUILayoutHelper.ValidationBox`.

## Asset caches — `Assets/Editor/DataGeneration/`

### `EditorBlockDatabaseCache.cs`

Fast dictionary cache of `BlockDatabase` for editor tools — replaces `AssetDatabase` queries
inside `OnGUI` loops; auto-rebuilds on compilation/domain reload. `Database`, `Cache`
(`IReadOnlyDictionary<ushort, BlockType>`), `GetBlockType(id)`, `RefreshCache()`.

### `EditorCreditsDatabaseCache.cs`

Same pattern for the credits database. Copy this pattern for any new frequently-read database
asset instead of ad-hoc `AssetDatabase.LoadAssetAtPath` calls per repaint.
