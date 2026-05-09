<objective>
Detailed parameter reference and example calls for all 12 named Unity MCP tools. Load this when you need to check a tool's parameters or want a ready-made recipe to adapt.
</objective>

<tool name="Unity_RunCommand">

Execute arbitrary C# inside the Unity Editor process. The most powerful tool — can do anything the Unity Editor API exposes.

**Parameters:**

- `Code` (required) — C# source implementing `IRunCommand`
- `Title` (optional) — display name for the execution

**Template:**

```csharp
using UnityEngine;
using UnityEditor;

internal class CommandScript : IRunCommand
{
    public void Execute(ExecutionResult result)
    {
        // Your logic here
        result.Log("Done");
    }
}
```

**Rules:**

- Class MUST be named `CommandScript` (any other name causes NullReferenceException).
- MUST use `internal`, not `public` (causes "Inconsistent Accessibility" error).
- Use `result.RegisterObjectCreation(obj)` after creating objects.
- Use `result.RegisterObjectModification(obj)` BEFORE modifying objects.
- Use `result.DestroyObject(obj)` instead of `Object.DestroyImmediate`.
- Use `result.Log()` / `result.LogWarning()` / `result.LogError()` for output.
- Do NOT run blocking or long-running code — it freezes the editor.

**Recipes:**

Query a singleton's state:

```csharp
using UnityEngine;
internal class CommandScript : IRunCommand
{
    public void Execute(ExecutionResult result)
    {
        var world = Object.FindFirstObjectByType<World>();
        if (world == null) { result.LogError("World not found"); return; }
        result.Log("Loaded chunks: {0}", world.LoadedChunks.Count);
    }
}
```

Read a ScriptableObject's values:

```csharp
using UnityEngine;
using UnityEditor;
internal class CommandScript : IRunCommand
{
    public void Execute(ExecutionResult result)
    {
        var guids = AssetDatabase.FindAssets("t:BlockDatabase");
        if (guids.Length == 0) { result.LogError("BlockDatabase not found"); return; }
        var path = AssetDatabase.GUIDToAssetPath(guids[0]);
        var db = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
        result.Log("BlockDatabase at {0}, type: {1}", path, db.GetType().FullName);
    }
}
```

Check compilation state programmatically:

```csharp
using UnityEditor;
using UnityEditor.Compilation;
internal class CommandScript : IRunCommand
{
    public void Execute(ExecutionResult result)
    {
        bool compiling = EditorApplication.isCompiling;
        result.Log("IsCompiling: {0}", compiling);
    }
}
```

</tool>

<tool name="Unity_ReadConsole">

Read or clear the Unity console with filtering.

**Parameters:**

- `Action` — `"Get"` or `"Clear"` (default: `"Get"`)
- `Types` — array of `"Log"`, `"Warning"`, `"Error"`, `"All"` (default: all types)
- `Count` — max messages (default: 100)
- `FilterText` — substring filter
- `SinceTimestamp` — ISO 8601 timestamp cutoff
- `Format` — `"Plain"`, `"Detailed"`, `"Json"` (default: `"Detailed"`)
- `IncludeStacktrace` — bool (default: true)

**Recipes:**

Check for errors only:

```json
{
  "Action": "Get",
  "Types": [
    "Error"
  ],
  "Count": 20,
  "Format": "Detailed"
}
```

Check for warnings since a timestamp:

```json
{
  "Action": "Get",
  "Types": [
    "Warning"
  ],
  "SinceTimestamp": "2026-05-09T12:00:00Z",
  "Format": "Plain"
}
```

Search for specific text:

```json
{
  "Action": "Get",
  "FilterText": "NullReference",
  "Count": 10
}
```

Clear the console before a test:

```json
{
  "Action": "Clear"
}
```

</tool>

<tool name="Unity_ManageScene">

Query and manage scenes.

**Parameters:**

- `Action` (required) — `"GetActive"`, `"GetHierarchy"`, `"GetBuildSettings"`, `"Load"`, `"Save"`, `"Create"`
- `Name` — scene name (no `.unity` extension) for Create/Load/Save
- `Path` — asset path (e.g. `"Assets/Scenes/"`)
- `Depth` — hierarchy depth limit (`-1` = full, `0` = root only, `1+` = limited)
- `BuildIndex` — for load by build index

**Recipes:**

Get active scene:

```json
{
  "Action": "GetActive"
}
```

Get scene hierarchy (depth-limited to avoid token overload):

```json
{
  "Action": "GetHierarchy",
  "Depth": 2
}
```

Get build settings:

```json
{
  "Action": "GetBuildSettings"
}
```

Load a scene by name:

```json
{
  "Action": "Load",
  "Name": "World",
  "Path": "Assets/Scenes/"
}
```

</tool>

<tool name="Unity_ManageGameObject">

Find GameObjects, inspect components, read serialized fields, modify objects.

**Parameters:**

- `action` (required) — `"find"`, `"get_components"`, `"get_component"`, `"create"`, `"modify"`, `"delete"`, `"add_component"`, `"remove_component"`, `"set_component_property"`
- `target` — GameObject name, path, or instance ID (for modify/delete/component ops)
- `search_method` — `"by_name"`, `"by_id"`, `"by_path"`
- `search_term` — search string (for `find`)
- `find_all` — bool, return all matches
- `search_inactive` — bool, include inactive objects
- `include_non_public_serialized` — bool, include `[SerializeField] private` fields
- `component_name` — for single-component operations
- Plus: `name`, `tag`, `layer`, `parent`, `position`, `rotation`, `scale`, `component_properties`, `components_to_add`

**Recipes:**

Find a GameObject by name:

```json
{
  "action": "find",
  "search_term": "World",
  "search_method": "by_name"
}
```

Find all chunks in the scene:

```json
{
  "action": "find",
  "search_term": "Chunk",
  "search_method": "by_name",
  "find_all": true
}
```

Read all components including private serialized fields:

```json
{
  "action": "get_components",
  "target": "Main Camera",
  "include_non_public_serialized": true
}
```

Get a specific component's data:

```json
{
  "action": "get_component",
  "target": "EventSystem",
  "component_name": "EventSystem"
}
```

</tool>

<tool name="Unity_ManageMenuItem">

Execute, list, or check existence of Unity Editor menu items.

**Parameters:**

- `Action` (required) — `"Execute"`, `"List"`, `"Exists"`, `"Refresh"`
- `MenuPath` — full menu path (for Execute/Exists), e.g. `"Minecraft Clone/Generate Block IDs"`
- `Search` — filter string for List (case-insensitive)
- `Refresh` (required) — bool, force-refresh menu cache

**Recipes:**

Trigger Block ID code generation:

```json
{
  "Action": "Execute",
  "MenuPath": "Minecraft Clone/Generate Block IDs",
  "Refresh": false
}
```

List all project-specific menu items:

```json
{
  "Action": "List",
  "Search": "Minecraft Clone",
  "Refresh": false
}
```

Check if a menu item exists:

```json
{
  "Action": "Exists",
  "MenuPath": "Minecraft Clone/Generate Fluid Mesh Data",
  "Refresh": false
}
```

**Available project menu items:**

- `Minecraft Clone/Generate Block IDs`
- `Minecraft Clone/Generate Fluid Mesh Data`
- `Minecraft Clone/Generate Game Actions`
- `Minecraft Clone/Atlas Packer`

**Rule:** Set `Refresh` to `false` normally. Only use `true` if you just added a new menu item via code and it's not showing up.

</tool>

<tool name="Unity_ManageAsset">

Search assets, get metadata, GUIDs, and perform asset operations.

**Parameters:**

- `Action` (required) — `"GetInfo"`, `"Search"`, `"CreateFolder"`, `"Create"`, `"Modify"`, `"Delete"`, `"Duplicate"`, `"Move"`, `"Rename"`, `"Import"`, `"GetComponents"`
- `Path` (required) — asset path relative to project root
- `GeneratePreview` (required) — bool
- `SearchPattern` — glob pattern for Search (e.g. `"*.prefab"`)
- `FilterType` — filter by asset type
- `AssetType` — for Create (e.g. `"Material"`, `"Folder"`)
- `Destination` — target path for Move/Duplicate
- `Properties` — dict of properties for Create/Modify

**Recipes:**

Get asset info and GUID:

```json
{
  "Action": "GetInfo",
  "Path": "Assets/Scripts/World/Chunk.cs",
  "GeneratePreview": false
}
```

Search for all ScriptableObjects in a folder:

```json
{
  "Action": "Search",
  "Path": "Assets/Data/WorldGen/",
  "SearchPattern": "*.asset",
  "GeneratePreview": false
}
```

Search for prefabs:

```json
{
  "Action": "Search",
  "Path": "Assets/Prefabs/",
  "SearchPattern": "*.prefab",
  "GeneratePreview": false
}
```

</tool>

<tool name="Unity_ManageEditor">

Control play mode, query editor state, manage tags and layers.

**Parameters:**

- `Action` (required) — `"GetState"`, `"Play"`, `"Pause"`, `"Stop"`, `"GetProjectRoot"`, `"GetWindows"`, `"GetActiveTool"`, `"GetSelection"`, `"GetPrefabStage"`, `"SetActiveTool"`, `"AddTag"`, `"RemoveTag"`, `"GetTags"`, `"AddLayer"`, `"RemoveLayer"`, `"GetLayers"`
- `WaitForCompletion` — bool (optional)
- `ToolName` — for SetActiveTool
- `TagName` — for AddTag/RemoveTag
- `LayerName` — for AddLayer/RemoveLayer

**Recipes:**

Check editor state (compilation status, play mode, etc.):

```json
{
  "Action": "GetState"
}
```

Get project root path:

```json
{
  "Action": "GetProjectRoot"
}
```

Get current tags:

```json
{
  "Action": "GetTags"
}
```

Get current layers:

```json
{
  "Action": "GetLayers"
}
```

**Rules:**

- **ALWAYS confirm with the user before calling Play/Pause/Stop** — these affect the editor's play state and can disrupt the user's work.
- `GetState` returns compilation errors — use after `dotnet build` to verify Unity also sees a clean compile.

</tool>

<tool name="Unity_Camera_Capture">

Capture rendered output from a camera in the scene.

**Parameters:**

- `cameraInstanceID` (optional) — instance ID of a GameObject with a Camera component. Omit to capture the current scene view.

**Recipes:**

Capture the scene view (no camera ID needed):

```json
{}
```

Capture from a specific camera (get the instance ID from `ManageGameObject` `find` first):

```json
{
  "cameraInstanceID": 12345
}
```

**Notes:**

- Computationally expensive — use only when visual verification is genuinely needed.
- To get a camera's instance ID: use `Unity_ManageGameObject` with `find` by name (e.g. `"Main Camera"`), the instance ID is in the response.

</tool>

<tool name="Unity_ValidateScript">

Run Unity-aware validation on a C# script. Catches GC allocations in hot paths, common pitfalls, and syntax issues.

**Parameters:**

- `Uri` (required) — path to the script (e.g. `"Assets/Scripts/World/Chunk.cs"`)
- `IncludeDiagnostics` (required) — bool; `true` for full diagnostic details, `false` for counts only
- `Level` — `"basic"` (syntax only) or `"standard"` (deeper perf/pitfall checks, default: `"basic"`)

**Recipes:**

Full validation with diagnostics:

```json
{
  "Uri": "Assets/Scripts/World/Chunk.cs",
  "Level": "standard",
  "IncludeDiagnostics": true
}
```

Quick count-only check:

```json
{
  "Uri": "Assets/Scripts/World/Chunk.cs",
  "Level": "basic",
  "IncludeDiagnostics": false
}
```

</tool>

<tool name="Unity_PackageManager_GetData">

Check installed Unity package versions.

**Parameters:**

- `packageID` (required) — package name (e.g. `"com.unity.burst"`)
- `installedOnly` (required) — bool

**Recipes:**

Check Burst version:

```json
{
  "packageID": "com.unity.burst",
  "installedOnly": true
}
```

Check Collections version:

```json
{
  "packageID": "com.unity.collections",
  "installedOnly": true
}
```

Check Mathematics version:

```json
{
  "packageID": "com.unity.mathematics",
  "installedOnly": true
}
```

</tool>

<tool name="Unity_FindInFile">

Regex search inside a single file with SHA256 verification.

**Parameters:**

- `Uri` (required) — asset path (e.g. `"Assets/Scripts/World/Chunk.cs"`)
- `Pattern` (required) — regex pattern
- `IgnoreCase` — bool (default: true)
- `MaxResults` — int (default: 200)

**Recipes:**

Find all method definitions in a file:

```json
{
  "Uri": "Assets/Scripts/World/Chunk.cs",
  "Pattern": "public\\s+\\w+\\s+\\w+\\("
}
```

**When to prefer over Grep:** Only when you need the SHA256 hash to verify the file hasn't changed since Unity loaded it. For all other searches, use the Grep tool (multi-file, context lines, faster).

</tool>
