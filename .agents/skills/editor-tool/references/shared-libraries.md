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

## `EditorAudioPreview` — auditioning clips

| Member | Purpose |
|---|---|
| `IsAvailable` | Whether this Unity version exposes the internal preview API at all. Gate play buttons on it. |
| `Play(AudioClip)` / `StopAll()` | Start or stop the single preview voice. |
| `IsPlaying()` / `IsPlayingClip(clip)` | Drive a ▶/⏹ button's state. |
| `RepaintWhilePlaying(window)` | Returns a handler to register; nothing repaints an editor window when audio ends on its own. |
| `StopRepainting(handler)` | Unregister it in `OnDisable`, and call `StopAll()` there too — a preview outlives the window otherwise. |

## `AudioLoudnessAnalyzer` — measuring loudness

| Member | Purpose |
|---|---|
| `IsAvailable` / `ResetAvailability()` | Cached probe for ffmpeg on PATH; reset it after the user installs it. |
| `Measure(filePath)` | Integrated LUFS, true peak dBFS and LRA for one file, read from disk. Check `HasLoudnessRange` before trusting the LRA — 0 LU is a legitimate reading for a steady loop, so the flag is what separates it from an absent one. |
| `ParseMeterOutput(text)` | The parse alone, so it can be pinned against captured output with no ffmpeg present. |

**Integrated loudness is undefined for short clips.** EBU R128 gates on 400 ms blocks, so a clip
shorter than one block has no qualifying block and ffmpeg returns its **-70.0 LUFS floor** — which
means "unmeasurable", not "silent". Measured in this project: 0.15 s and 0.36 s clips both report
-70.0; 0.55 s and 0.78 s clips measure normally. Roughly 45 of the 199 shipped clips are affected,
all of them block one-shots. Any statistic taken over a mixed set (a median, a mean, a "quietest")
is poisoned by them, and so is every trim derived from it. Filter by duration, or measure short
clips with a different metric (`astats` RMS / `volumedetect` mean_volume) before comparing them
against loops.

**If you spawn a process from editor code, drain every stream you redirect.** Redirecting stdout
and reading only stderr deadlocks once the unread pipe fills (~4 KB): the child blocks on write and
the stream you *are* reading never reaches EOF. `ffmpeg -version` writes ~2.3 KB to stdout and
nothing to stderr, so the bug hides until a build with a longer banner. Read both asynchronously
(`BeginOutputReadLine`/`BeginErrorReadLine`) and let `WaitForExit(timeout)` be what you block in —
otherwise the timeout sits after a blocking read and guards nothing. Pass `-nostdin` too, or the
child inherits the editor's stdin.

**Do not reach for `AudioClip.GetData` instead.** It returns samples only for clips imported as
`DecompressOnLoad`. This project's ambience beds import as `Streaming` and its fluid emitters as
`CompressedInMemory`; for both, `GetData` returns false with the clip stuck in
`AudioDataLoadState.Loading` — verified to persist through `LoadAudioData`, through a temporary
importer flip with a synchronous reimport, and through `SaveAndReimport` after unloading the stale
instance. Reading the file is the only route that covers every profile.
