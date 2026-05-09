<objective>
End-to-end recipes for common Unity MCP tasks, plus constraints and gotchas. Load this when you need a complete pattern for a specific editor interaction.
</objective>

<common_patterns>

<recipe name="check-project-compiles">

**"Does the project compile in Unity?"**

After running `dotnet build`, also check Unity's compilation state — Unity and `dotnet build` can disagree (missing assembly references, editor-only code, etc.):

1. `Unity_ManageEditor` -> `GetState` — check for `isCompiling` and compilation errors
2. `Unity_ReadConsole` -> `Types: ["Error"]` — check for compile-time error messages

</recipe>

<recipe name="read-serialized-field">

**"Read a [SerializeField] value from a scene object"**

Binary `.unity` files are not human-readable. Use the MCP:

1. `Unity_ManageGameObject` -> `find` by name to get the instance ID
2. `Unity_ManageGameObject` -> `get_components` with `include_non_public_serialized: true`

</recipe>

<recipe name="trigger-code-generation">

**"Trigger code generation"**

Instead of asking the user to click menus:

1. `Unity_ManageMenuItem` -> `Execute` with the menu path (e.g. `"Minecraft Clone/Generate Block IDs"`)
2. `Unity_ReadConsole` -> check for success/error output

</recipe>

<recipe name="verify-asset-guid">

**"Verify an asset exists and get its GUID"**

After a file move/rename:

1. `Unity_ManageAsset` -> `GetInfo` with the new path
2. Check the returned GUID matches what scenes/prefabs reference

</recipe>

<recipe name="capture-camera-output">

**"Capture what the camera sees"**

For visual verification of UI or rendering changes:

1. `Unity_ManageGameObject` -> `find` the camera by name to get instance ID
2. `Unity_Camera_Capture` with that instance ID (or omit for scene view)

</recipe>

<recipe name="find-objects-in-scene">

**"Check if a specific chunk/object exists in the scene"**

1. `Unity_ManageScene` -> `GetHierarchy` with `Depth: 1` to see root objects
2. `Unity_ManageGameObject` -> `find` with `search_term` and `find_all: true`

</recipe>

</common_patterns>

<constraints>

1. **RunCommand freezes the editor** if you run blocking or long-running code. Keep executions short and non-blocking.
2. **Profiler tools return empty/error** without an active profiling session. Always check `ManageEditor` -> `GetState` first.
3. **Play/Pause/Stop** affects the user's editor state. **Always confirm with the user** before changing play mode.
4. **ManageGameObject searches** find objects in the currently loaded scene only. If the wrong scene is active, switch first with `ManageScene` -> `Load`.
5. **ManageAsset paths** are relative to the project root and must use forward slashes (e.g. `"Assets/Scripts/World/Chunk.cs"`).
6. **Camera_Capture is expensive** — it renders a full frame. Only use when visual verification is genuinely needed.
7. **ValidateScript** at `"standard"` level may produce false positives. Treat its output as hints, not hard failures.
8. **ManageMenuItem Refresh** — set to `false` normally; only use `true` if you just added a new menu item via code, and it's not showing up.

</constraints>
