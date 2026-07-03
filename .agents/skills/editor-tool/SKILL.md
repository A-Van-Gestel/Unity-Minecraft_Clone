---
name: editor-tool
description: Standards and patterns for building custom Unity Editor tools in this project — shared UI libraries, window/tab architecture, lifecycle cleanup, SerializedObject editing, and Burst-powered preview generation. Use when creating or modifying Unity Editor tools (EditorWindows, custom inspectors, editor utilities), or when debugging editor memory leaks (textures, meshes, materials not cleaned up).
---

# Editor Tool Development Guide

Standards and patterns for building custom Unity Editor tools in this project. Prioritizes maintainability (shared libraries), UI/UX consistency (unified styling), and stability/performance (proper cleanup, Burst jobs for heavy work).

## When to use this skill

- Creating a new `EditorWindow` or custom inspector.
- Adding tabs, panels, or preview features to an existing editor tool.
- Refactoring editor UI code for consistency or performance.
- Debugging editor memory leaks (textures, meshes, materials not cleaned up).

---

## 1. Shared Libraries — Use Before Writing New Code

Before implementing any UI widget or editor-side generation, check if it already exists. The full API catalog (member tables, wiring patterns) is in [references/shared-libraries.md](references/shared-libraries.md); the tool inventory and pattern-exemplar map is in [references/editor-suite-map.md](references/editor-suite-map.md). Index:

**General-purpose — `Assets/Editor/Libraries/`:**

| Library                         | Owns                                                                          |
|---------------------------------|--------------------------------------------------------------------------------|
| `EditorGUIHelper`               | Widgets: stepper int fields, searchable selection lists, checkerboard, drag rotation, sprite drawing |
| `EditorUILayoutHelper`          | Section headers/notes, groups, separators, validation boxes                   |
| `EditorDebounceTimer`           | Debounced reactions to GUI changes (`Request`/`Poll`/`Cancel`)                |
| `EditorPreviewMaterialUtility`  | Cached preview materials — never create preview materials directly            |
| `MeshPreviewWidget`             | `PreviewRenderUtility` wrapper: 3D mesh previews, camera/lighting/zoom, cleanup |
| `CrossSectionBlockColorMap`     | Block-ID → preview color palette for cross-section renderers                  |

**World-gen tooling — `Assets/Editor/WorldTools/Libraries/`:**

| Library                       | Owns                                                                                 |
|-------------------------------|----------------------------------------------------------------------------------------|
| `CrossSectionPanelHelper`     | Cross-section panels: fitted rects, crosshair, chunk borders, sea level, click/scroll, texture management |
| `EditorChunkPipelineRunner`   | Editor-time run of the REAL runtime chunk pipeline (gen → structures → lighting) without a `World` |
| `EditorJobDataManagerFactory` | `JobDataManager` + fluid templates from a `BlockDatabase`, no `World` needed          |
| `WorldGenPreviewSettings`     | Cross-window settings sync broker (`Publish` / `OnSettingsChanged` / `Revision`)      |
| `BiomeConfigValidator`        | Biome config artifact detection → severity-tagged warnings for the Biome Editor       |

**Asset caches — `Assets/Editor/DataGeneration/`:** `EditorBlockDatabaseCache`, `EditorCreditsDatabaseCache` — dictionary caches replacing `AssetDatabase` queries in `OnGUI` loops; copy this pattern for any new frequently-read database asset.

**Rules:**

- Every editor tool MUST use `EditorUILayoutHelper` for section headers, descriptions, and grouping. Do not create one-off GUIStyles for these purposes.
- Editor-side chunk/terrain generation MUST go through `EditorChunkPipelineRunner` / `EditorJobDataManagerFactory` — a hand-rolled copy of the pipeline drifts from production behavior.

---

## 2. Window Architecture

### Partial class per tab

Multi-tab windows use one partial class file per tab, plus a core file for shared state:

```
MyWindow.cs                    — Core: shared state, tab router, OnEnable/OnDisable, lifecycle
MyWindow.TabName.cs            — Tab: drawing + generation for one feature
MyWindow.OtherTab.cs           — Tab: another feature
```

Shared state (seed, selection, toggles) lives in the core file. Tab-specific state lives in the tab file. Exemplars: `WorldGenPreviewWindow` (5 tab partials), `ChunkPreview3DWindow` (Pipeline/Rendering/UI partials), `BlockEditorWindow` — see [references/editor-suite-map.md](references/editor-suite-map.md) for the full pattern → exemplar table.

### Tab implementation pattern

```csharp
// In the core file's OnGUI:
_selectedTabIndex = GUILayout.Toolbar(_selectedTabIndex, s_tabLabels, GUILayout.Height(25));
switch (_selectedTabIndex)
{
    case 0: DrawFirstTab(); break;
    case 1: DrawSecondTab(); break;
}
```

### Sub-tabs within a tab

Use a secondary toolbar for sub-sections (e.g., Biome Editor's Terrain/Surface/Blending sub-tabs):

```csharp
_subTabIndex = GUILayout.Toolbar(_subTabIndex, s_subTabLabels, GUILayout.Height(22));
```

---

## 3. Lifecycle & Cleanup

### OnEnable / OnDisable

```csharp
private void OnEnable()
{
    LoadAssets();
    _widget?.Initialize();
    
    // Prevent double subscription
    EditorApplication.update -= PollForChanges;
    EditorApplication.update += PollForChanges;
}

private void OnDisable()
{
    EditorApplication.update -= PollForChanges;
    _widget?.Dispose();
    CleanupTextures();
}
```

### Texture cleanup — CRITICAL

Every `Texture2D` created in editor code MUST be destroyed in `OnDisable`. Use `DestroyImmediate()` (not `Destroy()`):

```csharp
if (_texture != null) { DestroyImmediate(_texture); _texture = null; }
```

For textures that resize, use `CrossSectionPanelHelper.EnsureTexture(ref tex, w, h)` which handles destroy + recreate.

### Mesh and material cleanup

- `MeshPreviewWidget.Dispose()` handles its own cleanup.
- `EditorPreviewMaterialUtility.DisposeCachedMaterials()` for shared preview materials.
- Any meshes created with `new Mesh()` must be destroyed in `OnDisable`.

---

## 4. Data Editing Patterns

### SerializedObject (recommended default)

Provides automatic Undo/Redo support, dirty marking, and works with all property drawers:

```csharp
_serializedObject = new SerializedObject(targetAsset);
_serializedObject.Update();

EditorGUILayout.PropertyField(_serializedObject.FindProperty("fieldName"), true);

if (_serializedObject.ApplyModifiedProperties())
{
    // Properties changed — trigger live preview update
    if (_liveUpdate) RegeneratePreview();
}
```

**Use for:** Biome editing, any ScriptableObject property editing where Undo matters.

### Manual field editing (performance-critical only)

Direct field assignment without SerializedObject. Requires manual `Undo.RecordObject()` and `EditorUtility.SetDirty()`:

**Use for:** BlockEditor's in-memory database copy where SerializedObject overhead is measurable.

### [HideInInspector] fields

`EditorGUILayout.PropertyField` and `NextVisible` skip fields marked `[HideInInspector]`. To draw them anyway, iterate child properties by explicit path:

```csharp
SerializedProperty child = serializedObject.FindProperty("parentField.childField");
if (child != null) EditorGUILayout.PropertyField(child);
```

---

## 5. Asset Loading & Refreshing

### Cached database helpers (preferred for repeated access)

Use dedicated cache classes (e.g., `EditorBlockDatabaseCache`) for assets loaded frequently.

### AssetDatabase scanning (for discovery)

```csharp
string[] guids = AssetDatabase.FindAssets("t:MyScriptableObject", new[] { "Assets/Data" });
foreach (string guid in guids)
{
    string path = AssetDatabase.GUIDToAssetPath(guid);
    var asset = AssetDatabase.LoadAssetAtPath<MyScriptableObject>(path);
}
```

### External change polling

For detecting when a user edits an asset in the Inspector while the editor window is open:

```csharp
private void PollForAssetChanges()
{
    if (_asset == null || !_autoGenerate) return;
    string fullPath = Path.GetFullPath(AssetDatabase.GetAssetPath(_asset));
    DateTime writeTime = File.GetLastWriteTimeUtc(fullPath);
    if (writeTime != _lastWriteTime)
    {
        _lastWriteTime = writeTime;
        RegeneratePreview();
    }
}
```

---

## 6. IMGUI Widget Patterns — see references/imgui-patterns.md

Recurring IMGUI implementation patterns live in [references/imgui-patterns.md](references/imgui-patterns.md); load it when actually writing widget code. It covers:

- **GUIStyle caching** — static lazy-init pattern, `UDR0001` suppression, the `fixedHeight` rule for overridden font sizes.
- **Responsive texture display** — fill-available-space rects, and the rule that overlays (chunk borders, crosshairs) are drawn screen-space, never baked into texture pixels.
- **Collapsible panels** — toolbar-button toggle pattern + `MaxHeight` pairing.
- **Tooltips** — mandatory `GUIContent` tooltips on all user-facing controls, and the `EnumPopup` limitation.

---

## 7. Performance — Burst Jobs for Heavy Preview Generation

When a preview evaluates noise or terrain for thousands of pixels, use a Burst `IJobParallelFor`:

```csharp
[BurstCompile(FloatPrecision.Standard, FloatMode.Default)]
public struct MyPreviewJob : IJobParallelFor
{
    [ReadOnly] public int TextureSize;
    [WriteOnly][NativeDisableParallelForRestriction] public NativeArray<byte> OutputPixels;
    
    public void Execute(int index) { /* evaluate one pixel */ }
}
```

**Key rules:**

- Allocate output with `Allocator.TempJob` (not `Allocator.Temp` — that's frame-scoped and can't be used with job scheduling).
- Use `NativeArray<byte>` in RGBA32 format + `texture.LoadRawTextureData(array)` for zero-copy transfer.
- Batch size of 64 is a good default for `Schedule(count, 64)`.
- Call `.Complete()` synchronously in editor code (no background scheduling complexity needed).
- Dispose the output array after copying to texture.

**When NOT to use jobs:** If the evaluation has sequential Y-dependencies (e.g., `previousDensity` tracking in column evaluation), it can't be parallelized per-pixel. Parallelize per-column instead, or keep it sequential if performance is acceptable.

**Full chunk pipeline previews:** when the preview needs actual chunk data (not just per-pixel noise), do not write a new job — run the real pipeline via `EditorChunkPipelineRunner` (see section 1). It reuses the runtime Burst jobs and stays correct as the pipeline evolves.
