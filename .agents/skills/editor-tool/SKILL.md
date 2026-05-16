---
name: editor-tool
description: Use when creating or modifying Unity Editor tools (EditorWindows, custom inspectors, editor utilities). Covers lifecycle management, shared library usage, UI/UX consistency, performance, and stability patterns.
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

Before implementing any UI widget, check if it already exists in the shared libraries:

### `Assets/Editor/Libraries/EditorGUIHelper.cs`

| Method                                                | Use for                                                          |
|-------------------------------------------------------|------------------------------------------------------------------|
| `IntFieldWithSteppers(value, min, max)`               | Int fields with ◀/▶ stepper buttons                              |
| `DrawSearchableSelectionList<T>(...)`                 | Filterable, scrollable selection lists with custom row rendering |
| `DrawCheckerboardBackground(rect)`                    | Transparency checkerboard behind preview textures                |
| `HandleDragRotation(position, rotation, sensitivity)` | Mouse drag rotation for 3D previews                              |
| `DrawSprite(position, sprite)`                        | Drawing atlas sprites in editor UI                               |

### `Assets/Editor/Libraries/EditorUILayoutHelper.cs`

| Method                        | Use for                                                           |
|-------------------------------|-------------------------------------------------------------------|
| `SectionHeader(text)`         | 13pt bold section titles (uses `fixedHeight` to prevent clipping) |
| `SubHeader(text)`             | 11pt bold sub-section titles                                      |
| `SectionNote(text)`           | Muted grey description text (supports `<b>rich text</b>`)         |
| `BeginGroup()` / `EndGroup()` | Visually grouped property boxes with padding                      |
| `DrawSeparator()`             | 1px horizontal divider lines                                      |

**Rule:** Every editor tool MUST use `EditorUILayoutHelper` for section headers, descriptions, and grouping. Do not create one-off GUIStyles for these purposes.

### `Assets/Editor/Libraries/EditorPreviewMaterialUtility.cs`

Centralized material caching for 3D mesh previews. Use instead of creating materials directly.

### `Assets/Editor/Libraries/MeshPreviewWidget.cs`

Encapsulates `PreviewRenderUtility` for 3D mesh rendering. Handles camera, lighting, rotation, and cleanup. Call `Initialize()` in `OnEnable`, `Dispose()` in `OnDisable`.

### `Assets/Editor/WorldTools/Libraries/CrossSectionPanelHelper.cs`

Panel drawing, crosshair, click/scroll interaction, chunk borders, sea level overlays, texture management. Use for any cross-section or multi-panel terrain preview.

---

## 2. Window Architecture

### Partial class per tab

Multi-tab windows use one partial class file per tab, plus a core file for shared state:

```
MyWindow.cs                    — Core: shared state, tab router, OnEnable/OnDisable, lifecycle
MyWindow.TabName.cs            — Tab: drawing + generation for one feature
MyWindow.OtherTab.cs           — Tab: another feature
```

Shared state (seed, selection, toggles) lives in the core file. Tab-specific state lives in the tab file.

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

## 6. GUIStyle Caching

Cache as static fields with lazy initialization. Suppress `UDR0001` since styles are recreated on null-check after domain reload:

```csharp
#pragma warning disable UDR0001
private static GUIStyle s_myStyle;
#pragma warning restore UDR0001

private static void EnsureStyles()
{
    if (s_myStyle != null) return;
    s_myStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13, fixedHeight = 20 };
}
```

**`fixedHeight` rule:** When overriding `fontSize` on a GUIStyle derived from `EditorStyles`, always set `fixedHeight` to prevent IMGUI from clipping descenders (g, y, p, q) based on the base style's smaller font metrics.

---

## 7. Responsive Texture Display

For preview textures that should fill available space:

```csharp
Rect rect = GUILayoutUtility.GetRect(0, 0, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
if (rect.width > 10 && rect.height > 10)
{
    Rect drawRect = CrossSectionPanelHelper.GetFittedRect(rect, texture);
    GUI.DrawTexture(drawRect, texture, ScaleMode.StretchToFill);
    
    // Draw overlays in SCREEN SPACE after the texture (not baked into pixels)
    EditorGUI.DrawRect(new Rect(lineX, drawRect.y, 1, drawRect.height), Color.cyan);
}
```

**Rule:** Never bake overlay lines (chunk borders, sea level, crosshairs) into the texture pixels. Draw them as screen-space `EditorGUI.DrawRect` calls after the texture — this keeps them 1px regardless of zoom and avoids regeneration on toggle.

---

## 8. Performance — Burst Jobs for Heavy Preview Generation

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

---

## 9. Collapsible Panels

For optional preview panels or advanced settings:

```csharp
string label = _showPanel ? "Section ▼" : "Section ▲";
if (GUILayout.Button(label, EditorStyles.toolbarButton))
    _showPanel = !_showPanel;

if (_showPanel)
{
    // Panel content
}
```

Pair with `GUILayout.MaxHeight(position.height * 0.55f)` on the content above to prevent the panel from being squeezed out.

---

## 10. Tooltips

All user-facing controls MUST have tooltips via `GUIContent`:

```csharp
_value = EditorGUILayout.IntSlider(
    new GUIContent("Field Name", "Description of what this field does and valid ranges."),
    _value, min, max);

_toggle = GUILayout.Toggle(_toggle,
    new GUIContent("Label", "What enabling/disabling this does."),
    EditorStyles.miniButton);
```

For `EditorGUI.EnumPopup` with absolute `Rect` positioning, tooltips don't work on the popup itself — use a separate `GUI.Label` with the tooltip next to it.
