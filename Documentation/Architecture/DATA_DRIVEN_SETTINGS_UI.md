# Data-Driven Settings UI Architecture

**Status:** Implemented (2026-05-14)

## Overview

The goal of this design is to transition the Voxel Engine's Settings Menu from a manually wired, static prefab into a **dynamic, reflection-based UI generator**. The UI will be constructed automatically at runtime based on the fields and attributes in the `Settings` and `DevSettings` classes.

This architecture eliminates manual prefab wiring issues, ensures the UI is always perfectly in sync with the codebase, and provides powerful, centralized control over setting visibility and mutability.

### Design Principles

* **Opt-In Model:** Only fields decorated with `[SettingField(...)]` are exposed in the UI. All other fields (constants, computed properties, internal fields) are silently skipped.
* **Immediate-Apply:** Settings are applied the moment the user changes them in the UI (modern UX, consistent with Minecraft Java Edition). There is no "Save" step — the "Done" button simply closes the menu and persists to disk.
* **Generate Once, Rebind on Open:** The UI hierarchy is built once during `Awake()`. On subsequent opens (`OnEnable()`), only the values are rebound from the cached `Settings` object — no re-instantiation or reflection overhead.
* **Separation of Concerns:** `SettingsMenuController` is the **shell** (scroll rect, tab switching, Done button). `SettingsUIGenerator` is the **builder** (reflection, instantiation, binding). The generator does not know about serialization details (e.g., the hidden-section DevSettings JSON injection).

---

## 1. Tab System

### `SettingsTab` Enum

All tab identifiers are defined in a single enum. This provides:

- **Compile-time safety** — typo-free tab references across `Settings`, `DevSettings`, and the generator.
- **Defined ordering** — the `SettingsUIGenerator` defines an explicit tab order array using this enum. A runtime assertion validates that every enum value has a defined position.
- **Extensibility** — adding a new tab is a single enum value addition, and the assertion will immediately flag if its order is missing.

```csharp
/// <summary>
/// Defines the available tabs in the Settings UI.
/// Used by [SettingField] to assign fields to tabs.
/// </summary>
public enum SettingsTab
{
    General,
    Controls,
    Graphics,
    World,
    Performance,
    Dev
}
```

### Tab Display Names

Each enum value maps to a human-readable tab button label. The generator reads `[InspectorName]` on enum values if present, otherwise falls back to `Enum.ToString()`. Since the current names are already clean (`"General"`, `"Controls"`, etc.), no `[InspectorName]` overrides are needed unless a tab needs a multi-word display name.

### Tab Order Definition

The canonical tab order is defined in `SettingsUIGenerator` as a private static array:

```csharp
/// <summary>
/// Defines the top-to-bottom order of tabs in the Settings UI.
/// Every SettingsTab enum value MUST appear in this array.
/// </summary>
private static readonly SettingsTab[] TAB_ORDER =
{
    SettingsTab.General,
    SettingsTab.Controls,
    SettingsTab.Graphics,
    SettingsTab.World,
    SettingsTab.Performance,
    SettingsTab.Dev
};
```

On `Awake()`, the generator validates completeness:

```csharp
// Validate that every SettingsTab value has a defined order
SettingsTab[] allTabs = (SettingsTab[])Enum.GetValues(typeof(SettingsTab));
foreach (SettingsTab tab in allTabs)
{
    if (Array.IndexOf(TAB_ORDER, tab) == -1)
    {
        Debug.LogError($"[SettingsUIGenerator] SettingsTab.{tab} is missing from TAB_ORDER! " +
                       "Add it to the TAB_ORDER array in SettingsUIGenerator.");
    }
}
```

This ensures that adding a new `SettingsTab` enum value without updating `TAB_ORDER` produces an immediate, actionable error message.

---

## 2. Attribute System Design

The system uses a combination of Unity's built-in attributes and a single consolidated custom attribute to drive the UI generation.

### Built-in Attributes (Reused)

| Attribute                | UI Effect                                                                                                                                                                                                                                                                            |
|--------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `[Header("...")]`        | Generates a header text element above the field in the current tab.                                                                                                                                                                                                                  |
| `[Tooltip("...")]`       | Drives hover tooltip text on the generated UI element.                                                                                                                                                                                                                               |
| `[Range(min, max)]`      | Maps numeric fields to a Slider element. Without this, bare `int`/`float` fields map to an InputField.                                                                                                                                                                               |
| `[InspectorName("...")]` | *(On enum values)* Overrides the display name of individual enum values in dropdowns. This is a built-in Unity attribute (`UnityEngine.InspectorNameAttribute`) that is general-purpose — the same labels are used in the Unity Inspector and any other UI that reads enum metadata. |
| `[InitializationField]`  | *(From MyBox)* Marks the field as read-only when the settings menu is opened from in-game (Pause Menu). These fields can only be changed from the Main Menu before a world is loaded.                                                                                                |
| **Field Type**           | Dictates the UI element type (see [Type Mapping Table](#type-mapping-table)).                                                                                                                                                                                                        |

*Note: Integer-based settings that represent bounded modes (like `uiScale`) should be refactored into strongly-typed Enums to automatically map to Dropdown UI components.*

### `[SettingField]` — The Consolidated Custom Attribute

A single attribute that serves as both the **opt-in gate** and the **UI configuration** for a settings field. The `Tab` parameter is required (constructor argument), all others are optional named properties.

```csharp
/// <summary>
/// Marks a field for inclusion in the auto-generated Settings UI.
/// Fields without this attribute are invisible to the SettingsUIGenerator.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public class SettingFieldAttribute : Attribute
{
    /// <summary>Required. Which tab this setting belongs to.</summary>
    public SettingsTab Tab { get; }

    /// <summary>
    /// Optional. Display name override for the UI label.
    /// If null, the generator auto-converts the field name from camelCase to "Title Case".
    /// </summary>
    public string Label { get; set; }

    /// <summary>
    /// Optional. Numeric format string for the value display label next to sliders.
    /// Example: "f2" renders 1.20, "f0" renders 5.
    /// </summary>
    public string Format { get; set; }

    /// <summary>
    /// Optional. Sort order within the tab. Lower values appear first.
    /// Fields without an explicit order are placed after ordered fields, in declaration order.
    /// </summary>
    public int Order { get; set; } = int.MaxValue;

    /// <summary>
    /// Optional. If true, the field (and its UI element) are hidden in Release builds.
    /// Used for Dev/Debug tab contents. If all fields in a tab are debug-only,
    /// the entire tab is suppressed in release builds.
    /// </summary>
    public bool DebugOnly { get; set; }

    /// <summary>
    /// Creates a new SettingField attribute that opts this field into the Settings UI.
    /// </summary>
    /// <param name="tab">The tab this setting belongs to.</param>
    public SettingFieldAttribute(SettingsTab tab) => Tab = tab;
}
```

### Usage Examples

```csharp
// Simple — just tab assignment, label auto-generated from field name
[SettingField(SettingsTab.General)]
public bool enableChunkLoadAnimations = false;

// Full — all options specified
[SettingField(SettingsTab.Controls, Label = "Mouse Sensitivity", Format = "f2", Order = 0)]
[Range(0.1f, 10f)]
public float lookSensitivity = 1.2f;

// Debug-only field (hidden in release builds)
[SettingField(SettingsTab.Dev, Label = "Simulate Migration Corruption", DebugOnly = true)]
public bool simulateMigrationCorruption = false;

// Initialization-locked field (read-only during gameplay, uses MyBox attribute)
[SettingField(SettingsTab.World, Label = "Lighting", Order = 0)]
[InitializationField]
public bool enableLighting = true;
```

### Type Mapping Table

| Field Type | Attribute           | UI Element                                                        |
|------------|---------------------|-------------------------------------------------------------------|
| `bool`     | —                   | Toggle                                                            |
| `int`      | `[Range(min, max)]` | Slider (integer steps)                                            |
| `int`      | *(no Range)*        | InputField (numeric)                                              |
| `float`    | `[Range(min, max)]` | Slider (continuous)                                               |
| `float`    | *(no Range)*        | InputField (numeric)                                              |
| `enum`     | —                   | Dropdown (populated from enum values, respects `[InspectorName]`) |
| `string`   | —                   | InputField (text)                                                 |

---

## 3. UI Layout & Scene Hierarchy

The settings menu uses a **vertical tab layout**: tab buttons are stacked vertically on the left, with the active tab's content displayed in a scrollable area on the right. A "Done" button is centered at the bottom, outside the tab content area.

### Current Scene Hierarchy

```
SettingsMenu
├── SettingsTitleText (TMP)              ← "Settings" title
├── TabPanel
│   ├── TabBarScrollArea                ← Left side: scrollable tab button list
│   │   ├── Viewport
│   │   │   └── TabBarButtons           ← ★ Tab button container (VerticalLayoutGroup)
│   │   │       ├── GeneralTab (Button)
│   │   │       ├── GraphicsTab (Button)
│   │   │       └── DevTab (Button)
│   │   └── Scrollbar Vertical
│   └── TabContentScrollArea            ← Right side: scrollable tab content
│       ├── Viewport
│       │   ├── GeneralTabContent       ← ★ Tab content panels (one per tab)
│       │   │   ├── InterfaceHeading (TMP)
│       │   │   ├── UIScaleDropdown (Dropdown)
│       │   │   ├── InputHeading (TMP)
│       │   │   ├── lookSensitivitySlider (Slider)
│       │   │   ├── BonusHeading (TMP)
│       │   │   └── ChunkAnimationToggle (Toggle)
│       │   ├── GraphicsTabContent
│       │   └── DevTabContent
│       └── Scrollbar Vertical
└── Buttons
    └── Done (Button)
```

### Key Container Transforms

The generator needs references to two existing container transforms in the hierarchy:

| Container             | Path                                               | Purpose                                                                                           |
|-----------------------|----------------------------------------------------|---------------------------------------------------------------------------------------------------|
| `_tabButtonContainer` | `TabPanel/TabBarScrollArea/Viewport/TabBarButtons` | Parent for instantiated tab buttons. Uses a `VerticalLayoutGroup` to stack buttons top-to-bottom. |
| `_tabContentParent`   | `TabPanel/TabContentScrollArea/Viewport`           | Parent for instantiated tab content panels. Only one panel is active at a time.                   |

The `TabContentScrollArea`'s `ScrollRect` component swaps its `content` reference to point at the active tab's content panel `RectTransform` on each tab switch.

### Post-Generation Hierarchy

After the generator runs, the manually created tab buttons and content panels (GeneralTab, GraphicsTab, etc.) are replaced by dynamically instantiated equivalents. The structural containers (`TabBarButtons`, `Viewport`, `TabPanel`, `Buttons/Done`) remain as-is from the prefab.

---

## 4. Component Architecture

### `SettingsMenuController.cs` — The Shell

Remains a MonoBehaviour on the Settings Menu root. Responsibilities:

* **Tab switching** — `SwitchTab(int)` activates the correct content panel, swaps `ScrollRect.content`, and resets scroll position to top.
* **Done button** — calls `SettingsManager.SaveSettings()` and fires `onSettingsClosed` event.
* **ScrollRect management** — holds the `[SerializeField]` reference to `TabContentScrollArea`'s `ScrollRect`.
* **Context flag** — exposes a `bool IsInGame` property, set by the parent menu (Main Menu vs Pause Menu) before the settings panel is enabled. This determines `[InitializationField]` interactability.
* **Lifecycle** — on `OnEnable()`, tells the generator to rebind values. On first enable, triggers full generation.

```csharp
/// <summary>
/// Set by the parent menu before enabling the settings panel.
/// When true (opened from Pause Menu), [InitializationField] fields are non-interactable.
/// When false (opened from Main Menu), all fields are interactable.
/// </summary>
public bool IsInGame { get; set; }
```

### `SettingsUIGenerator.cs` — The Builder

A separate MonoBehaviour on the same GameObject (or a child). Responsibilities:

* **One-time generation** in `Awake()` or on first `Generate()` call.
* **Reflection** over `Settings` fields and `DevSettings` fields (hardcoded `Settings.Dev` access — see [Section 5, Step 3](#one-time-generation-first-awake)).
* **Instantiation** of UI prefabs from the `SettingsUIPrefabLibrary` into the container transforms.
* **Binding** `onValueChanged` callbacks to `FieldInfo.SetValue()` + `SettingsManager.OnSettingChanged`.
* **Rebind** — a lightweight `RebindValues()` method that reads the current `Settings` object and calls `SetValueWithoutNotify()` on all generated controls. Called every `OnEnable()`.

```
┌─ SettingsMenuController (Shell) ──────────────────────────┐
│  [SerializeField] ScrollRect _contentScrollRect           │
│  [SerializeField] SettingsUIGenerator _generator          │
│  bool IsInGame (set by parent menu)                       │
│                                                           │
│  OnEnable() → _generator.RebindValues(IsInGame)           │
│  SwitchTab(int) → swap ScrollRect.content, reset scroll   │
│  OnDoneClicked() → Save + Close                           │
│                                                           │
│  ┌─ SettingsUIGenerator (Builder) ──────────────────────┐ │
│  │  [SerializeField] SettingsUIPrefabLibrary _lib       │ │
│  │  [SerializeField] Transform _tabButtonContainer      │ │
│  │  [SerializeField] Transform _tabContentParent        │ │
│  │                                                      │ │
│  │  Generate() → reflect, instantiate, bind             │ │
│  │  RebindValues(isInGame) → rebind + interactable      │ │
│  └──────────────────────────────────────────────────────┘ │
└───────────────────────────────────────────────────────────┘
```

---

## 5. Dynamic UI Generation Workflow

### One-Time Generation (First `Awake()`)

1. Load the `Settings` singleton via `SettingsManager.LoadSettings()`.
2. Use C# Reflection to iterate over all **instance fields** on `Settings`.
3. Additionally iterate over all instance fields on `DevSettings` (accessed via the **hardcoded** `Settings.Dev` property). This is explicit — the generator does not generically scan for nested objects. If additional hidden sections are added in the future that should not appear in the UI, they are simply not iterated here.
4. **Filter:** Skip any field that does not have `[SettingField(...)]`.
5. **Visibility gate:** If the field has `DebugOnly = true` and `Debug.isDebugBuild` is false, skip.
6. **Tab creation:** Iterate `TAB_ORDER`. For each tab that has at least one visible field, instantiate a **Tab Button** into `_tabButtonContainer` and a **Tab Content Panel** into `_tabContentParent` from the prefab library. Tabs with zero visible fields are skipped entirely (e.g., the Dev tab in release builds).
7. Sort fields within each tab by `Order` value (ascending), then by declaration order for fields with `Order = int.MaxValue`.
8. For each field:
   a. If the field has a `[Header("...")]` attribute, instantiate a **Header Text** element first.
   b. Instantiate the correct UI Component Prefab based on the [Type Mapping Table](#type-mapping-table).
   c. Apply `LayoutElement` values from the prefab library entry (each prefab type defines its own `preferredHeight`, `flexibleWidth`, etc.).
   d. Set the display label from `Label` if specified, otherwise auto-convert the field name (`camelCase` → `"Title Case"`).
   e. Set the tooltip from `[Tooltip]`.
   f. For sliders: configure min/max from `[Range]`, configure the value label format from `Format`.
   g. For dropdowns: populate options from `Enum.GetValues()`, using `[InspectorName]` display names where present.
9. Wire the `onValueChanged` event of each UI element to:
   a. Call `FieldInfo.SetValue()` to immediately update the `Settings` object.
   b. Fire `SettingsManager.NotifySettingChanged(fieldName)` to broadcast the change.

### Rebind (Every `OnEnable()` After First Generation)

1. Load the `Settings` singleton.
2. For each generated UI element, call `SetValueWithoutNotify()` with the current field value (via `FieldInfo.GetValue()`).
3. Re-evaluate `[InitializationField]` interactability: if `isInGame` is true, set `interactable = false` on those elements. Otherwise, set `interactable = true`.

---

## 6. Callbacks & Events for Real-Time Updates

Settings are applied **immediately** when the user interacts with a UI element. To ensure subsystems (such as `UIScaleController`, cloud rendering, or chunk loading) react instantly, a centralized event pattern is used.

### Observer Pattern

The standard C# `Action<string>` event is used. It is:

* Extremely fast (no reflection on execution path)
* Fully compatible with IL2CPP / AOT compilation
* Completely decoupled from the Settings object

```csharp
// In SettingsManager.cs
public static event Action<string> OnSettingChanged;

/// <summary>
/// Invoked by the UI generator when a setting value changes.
/// Broadcasts the field name so subscribers can filter efficiently.
/// </summary>
public static void NotifySettingChanged(string fieldName)
{
    OnSettingChanged?.Invoke(fieldName);
}
```

### Subscriber Example

```csharp
private void OnEnable()
{
    SettingsManager.OnSettingChanged += HandleSettingChanged;
}

private void OnDisable()
{
    SettingsManager.OnSettingChanged -= HandleSettingChanged;
}

private void HandleSettingChanged(string settingName)
{
    if (settingName == nameof(Settings.uiScale))
    {
        ApplyScale(SettingsManager.LoadSettings().uiScale);
    }
}
```

### Save Behavior

Because settings are applied immediately, the **"Done" button** simply:

1. Calls `SettingsManager.SaveSettings()` to persist to disk.
2. Invokes `onSettingsClosed` so parent menus can transition.

There is no "Cancel" or "Revert" — all changes are live. This matches Minecraft Java Edition's settings UX.

---

## 7. Prefab Library: `SettingsUIPrefabLibrary.asset`

A ScriptableObject that serves as the **single source of truth** for all UI prefab references and their layout configuration. The developer maintains this asset in the Inspector.

### ScriptableObject Definition

```csharp
[CreateAssetMenu(fileName = "SettingsUIPrefabLibrary", menuName = "UI/Settings UI Prefab Library")]
public class SettingsUIPrefabLibrary : ScriptableObject
{
    [Header("Structural Prefabs")]
    public GameObject headerTextPrefab;     // InterfaceHeading (TMP)
    public GameObject tabButtonPrefab;      // Button
    public GameObject tabContentPrefab;     // SettingsTabContent

    [Header("Control Prefabs")]
    public ControlEntry togglePrefab;
    public ControlEntry sliderPrefab;
    public ControlEntry dropdownPrefab;
    public ControlEntry inputFieldPrefab;

    /// <summary>
    /// Pairs a UI prefab with its LayoutElement configuration.
    /// </summary>
    [System.Serializable]
    public class ControlEntry
    {
        public GameObject prefab;
        public float preferredHeight = 50f;
        public float flexibleWidth = 1f;
        public float flexibleHeight = 0f;
    }
}
```

### Default Prefab Mapping

| Entry              | Prefab Path                                                       |
|--------------------|-------------------------------------------------------------------|
| `headerTextPrefab` | `Assets/Prefabs/UI/Components/InterfaceHeading (TMP).prefab`      |
| `tabButtonPrefab`  | `Assets/Prefabs/UI/Components/Button.prefab`                      |
| `tabContentPrefab` | `Assets/Prefabs/UI/Components/Settings/SettingsTabContent.prefab` |
| `togglePrefab`     | `Assets/Prefabs/UI/Components/Toggle.prefab`                      |
| `sliderPrefab`     | `Assets/Prefabs/UI/Components/Slider - Handle.prefab`             |
| `dropdownPrefab`   | `Assets/Prefabs/UI/Components/Dropdown.prefab`                    |
| `inputFieldPrefab` | `Assets/Prefabs/UI/Components/InputField - Text.prefab`           |

---

## 8. Settings Class Refactoring

The following refactors are prerequisites for the generator:

### 8.1 Collapse Mouse Sensitivity

Replace the two separate X/Y fields with a single `lookSensitivity` field:

```csharp
// Before (current)
public float mouseSensitivityX = 1.2f;
public float mouseSensitivityY = 1.2f;

// After
[SettingField(SettingsTab.Controls, Label = "Mouse Sensitivity", Format = "f2", Order = 0)]
[Range(0.1f, 10f)]
public float lookSensitivity = 1.2f;
```

If separate X/Y control is needed in the future, it can be reintroduced as two separate settings with individual UI sliders.

> **Note:** This rename means existing `settings.json` files with `mouseSensitivityX`/`mouseSensitivityY` keys will have those keys silently ignored by `JsonUtility.FromJson`, and `lookSensitivity` will receive its default value (`1.2f`). This is acceptable since only dev builds currently exist with this setting.
>
> **TODO:** In the future, a settings migration system (analogous to the AOT World Migration system) may be needed if production builds ship and field renames become breaking changes.

### 8.2 Refactor `uiScale` to Enum

```csharp
public enum UIScale
{
    Small,
    Standard,
    Large
}

// In Settings:
[SettingField(SettingsTab.General, Label = "UI Scale", Order = 0)]
public UIScale uiScale = UIScale.Standard;
```

Since only dev builds currently exist with this setting, the JSON migration for `int` → `enum` is not a concern.

### 8.3 Move `keepChunksInMemory` to DevSettings

The `#if UNITY_EDITOR` guard makes this field invisible to reflection in builds. Move it into `DevSettings` where it belongs:

```csharp
// Before (in Settings, behind #if UNITY_EDITOR)
#if UNITY_EDITOR
public bool keepChunksInMemory = false;
#endif

// After (in DevSettings)
public class DevSettings
{
    [SettingField(SettingsTab.Dev, Label = "Keep Chunks In Memory", DebugOnly = true, Order = 0)]
    [Tooltip("Chunks are never unloaded and saving/loading is disabled.")]
    public bool keepChunksInMemory = false;

    [SettingField(SettingsTab.Dev, Label = "Simulate Migration Corruption", DebugOnly = true, Order = 1)]
    [Tooltip("Randomly fail ~1% of chunks during migration to test fault tolerance.")]
    public bool simulateMigrationCorruption = false;
}
```

The `Settings.EnablePersistence` property must be updated to reference `Dev.keepChunksInMemory` unconditionally (guarded by `Debug.isDebugBuild` at runtime instead of compile-time).

### 8.4 Add `[Range]` to Bounded Integers

```csharp
[SettingField(SettingsTab.Graphics, Label = "View Distance", Format = "f0", Order = 0)]
[Range(1, 32)]
public int viewDistance = 5;
```

### 8.5 Example: Fully Annotated Settings Class

```csharp
[Serializable]
public class Settings
{
    [NonSerialized]
    public DevSettings Dev = new DevSettings();

    // --- General ---
    [SettingField(SettingsTab.General, Label = "UI Scale", Order = 0)]
    public UIScale uiScale = UIScale.Standard;

    [SettingField(SettingsTab.General, Label = "Chunk Load Animations", Order = 10)]
    public bool enableChunkLoadAnimations = false;

    // --- Controls ---
    [SettingField(SettingsTab.Controls, Label = "Mouse Sensitivity", Format = "f2", Order = 0)]
    [Range(0.1f, 10f)]
    public float lookSensitivity = 1.2f;

    // --- Graphics ---
    [SettingField(SettingsTab.Graphics, Label = "View Distance", Format = "f0", Order = 0)]
    [Range(1, 32)]
    public int viewDistance = 5;

    [SettingField(SettingsTab.Graphics, Label = "Cloud Style", Order = 1)]
    public CloudStyle clouds = CloudStyle.Fancy;

    // --- World Generation (Read-only during play via [InitializationField]) ---
    [SettingField(SettingsTab.World, Label = "Lighting", Order = 0)]
    [InitializationField]
    public bool enableLighting = true;

    [SettingField(SettingsTab.World, Label = "Cave Generation", Order = 1)]
    [InitializationField]
    public bool enableCaves = true;

    [SettingField(SettingsTab.World, Label = "Lode Generation", Order = 2)]
    [InitializationField]
    public bool enableLodes = true;

    [SettingField(SettingsTab.World, Label = "Water Generation", Order = 3)]
    [InitializationField]
    public bool enableWater = true;

    [SettingField(SettingsTab.World, Label = "Major Flora Generation", Order = 4)]
    [InitializationField]
    public bool enableMajorFloraPass = true;

    [SettingField(SettingsTab.World, Label = "Minor Flora Generation", Order = 5)]
    [InitializationField]
    public bool enableMinorFloraPass = true;

    // --- Performance ---
    [SettingField(SettingsTab.Performance, Label = "Save Compression", Order = 0)]
    public CompressionAlgorithm saveCompression = CompressionAlgorithm.LZ4;

    [SettingField(SettingsTab.Performance, Label = "Max Initial Load Radius", Format = "f0", Order = 1)]
    [Range(2, 32)]
    public int maxInitialLoadRadius = 10;

    [SettingField(SettingsTab.Performance, Label = "Max Mesh Rebuilds Per Frame", Format = "f0", Order = 2)]
    [Range(1, 50)]
    public int maxMeshRebuildsPerFrame = 10;

    [SettingField(SettingsTab.Performance, Label = "Max Light Jobs Per Frame", Format = "f0", Order = 3)]
    [Range(1, 128)]
    public int maxLightJobsPerFrame = 32;

    [SettingField(SettingsTab.Performance, Label = "Max Structure Mods Per Frame", Format = "f0", Order = 4)]
    [Range(100, 50000)]
    public int maxStructureModsPerFrame = 5000;

    // --- Fields WITHOUT [SettingField] are NOT shown in UI ---
    public const int DATA_LOAD_BUFFER = 3;
    public int LoadDistance => viewDistance + DATA_LOAD_BUFFER;
    public bool EnablePersistence => Debug.isDebugBuild ? !Dev.keepChunksInMemory : true;
    public bool enableVolatileSaveData = true;
    public bool showChunkBorders = false;
    public bool enableDiagnosticLogs = false;
    public bool enableWaterDiagnosticLogs = false;
}
```

---

## 9. IL2CPP Compatibility

The reflection used in this system is limited to basic `System.Reflection` operations:

* `Type.GetFields()` — enumerate fields
* `FieldInfo.GetCustomAttribute<T>()` — read attributes
* `FieldInfo.GetValue(object)` / `FieldInfo.SetValue(object, value)` — read/write values
* `Enum.GetValues(Type)` — populate dropdowns

These operations are fully supported by IL2CPP in Unity 6. No `MakeGenericMethod`, `MakeGenericType`, or `Activator.CreateInstance<T>` is used in hot paths.

**Validation requirement:** The IL2CPP reflection path must be tested in an actual IL2CPP Development Build early in the implementation phase to catch any edge cases before they become blockers.

> **Future consideration:** If reflection proves problematic at scale, the system could be augmented with an editor-time "Bake" step that uses Roslyn source generators to emit concrete binding code. This would eliminate runtime reflection entirely but would require two test rounds (Editor reflection + Production baked). This optimization is deferred unless IL2CPP testing reveals issues.

---

## 10. Future Considerations

### Settings Migration System

> **TODO:** As the project matures toward production builds, field renames (e.g., `mouseSensitivityX` → `mouseSensitivity`) and type changes (e.g., `int uiScale` → `UIScale uiScale`) will silently reset affected settings to defaults when users upgrade. A **settings migration system** — analogous to the existing AOT World Migration system — should be designed to handle versioned `settings.json` transformations. This is not needed for the current design (dev-only builds), but should be planned before any public release.

---

## 11. Implementation Checklist

### Phase 1: Foundation

- [X] Create `SettingsTab` enum
- [X] Create `SettingFieldAttribute` class
- [X] Create `SettingsUIPrefabLibrary` ScriptableObject and populate it in Inspector
- [X] Refactor `mouseSensitivityX`/`Y` → `lookSensitivity`
- [X] Refactor `uiScale` from `int` to `UIScale` enum
- [X] Move `keepChunksInMemory` from `#if UNITY_EDITOR` to `DevSettings`
- [X] Update `Settings.EnablePersistence` to use `Debug.isDebugBuild` runtime check
- [X] Add `[Range(1, 32)]` to `viewDistance`
- [X] Annotate all UI-visible fields with `[SettingField(...)]`

### Phase 2: Generator

- [X] Implement `SettingsUIGenerator.cs` with `Generate()` and `RebindValues(bool isInGame)`
- [X] Implement `TAB_ORDER` array with startup completeness assertion
- [X] Implement reflection pipeline: field discovery → filtering → sorting → instantiation → binding
- [X] Hardcode `Settings.Dev` iteration for DevSettings fields
- [X] Implement `camelCase` → `"Title Case"` label conversion utility
- [X] Implement `[InspectorName]` reading for enum dropdown population
- [X] Wire `onValueChanged` → `FieldInfo.SetValue()` + `NotifySettingChanged()`
- [X] Handle `[InitializationField]` interactability based on `IsInGame` flag

### Phase 3: Integration

- [X] Add `OnSettingChanged` event and `NotifySettingChanged()` to `SettingsManager`
- [X] Add `bool IsInGame` property to `SettingsMenuController`
- [X] Update Main Menu and Pause Menu to set `IsInGame` before enabling settings
- [X] Refactor `SettingsMenuController` to shell role (remove all per-field `[SerializeField]` references)
- [X] Preserve ScrollRect content swap and scroll-to-top on tab switch
- [X] Migrate `UIScaleController` to use `OnSettingChanged` subscriber pattern
- [ ] Test immediate-apply behavior for all setting types

### Phase 4: Validation

- [ ] Verify all settings appear in correct tabs with correct labels and ordering
- [ ] Verify `[InitializationField]` fields are non-interactable when `IsInGame = true`
- [ ] Verify `DebugOnly` fields and tabs are hidden in non-debug builds
- [ ] Verify `TAB_ORDER` assertion fires when a new enum value is added without ordering
- [ ] Test IL2CPP Development Build to validate reflection path
- [ ] Verify `settings.json` round-trip (save → load → UI matches)
