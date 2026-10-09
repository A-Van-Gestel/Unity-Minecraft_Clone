# Editor Writes: Making a Change Actually Land

An editor-side write that reports success is not evidence the value is on disk. Every trap below
returned success while the file stayed wrong. **Rule: read back the effect from the saved file
(`grep` / `git diff` the `.unity`, `.prefab` or `.asset`) before reporting a write as done — never the
apply call's return value, and never an in-memory re-query alone.**

## SerializedObject and scene writes

- **Re-read through a NEW `SerializedObject`** over the same target after
  `ApplyModifiedPropertiesWithoutUndo()`, and bail before saving if it disagrees; then grep the saved
  scene (`_field: {fileID: 0}` means the reference never landed).
- **Load assets AFTER `EditorSceneManager.OpenScene(path, OpenSceneMode.Single)`, never before.** The
  switch unloads assets the new scene does not reference; a handle taken earlier is a destroyed object,
  and assigning it to `objectReferenceValue` writes null with no error. Restore the previously open scene
  afterwards, and refuse to switch when it is dirty.
- **A property set on a prefab-instance component needs
  `PrefabUtility.RecordPrefabInstancePropertyModifications(component)`** (plus `EditorUtility.SetDirty`).
  Without it the live object changes and the save drops the value; `Undo.RecordObject` is not enough.
  Adding a component to an instance is tracked on its own, so the same command can persist its additions
  and lose every property write.
- **A scene save is never guaranteed to be a no-op.** Saving to clear a dirty flag can materialize stale
  prefab-override values (`m_Modifications` `value:` lines). Never save a scene you did not mean to
  modify, and `git diff` after every save; revert with `git restore --source=HEAD -- <file>` after
  switching Unity off that scene.
- **A serialized default is frozen once the scene is saved.** A new `[SerializeField] private float _x = 0.7f;`
  takes its initializer on first load, then the scene stores `0.7` and wins. Retuning means changing the C#
  initializer **and** the scene/prefab value (edit through `SerializedObject` + `SaveScene`; open a
  non-current scene additively, then `CloseScene`), then `grep -n "_x" Assets/Scenes/<scene>.unity`.
- **Which scenes carry a script is a GUID grep:** `m_Script: {fileID: 11500000, guid: <guid from the .meta>}`.
  A class-name grep also hits strings and labels and lists false members.

## Assets and import settings

- **Play-mode property writes on a project asset (URP asset, ScriptableObject) do not dirty it.**
  `IsDirty` is false, `SaveAssets` writes nothing, `git diff` is clean — yet the loaded instance keeps the
  value after Play exits and the next session snapshots it as "authored". `ImportAsset(ForceUpdate)` does
  not clear it. Runtime mutation needs a capture-once snapshot + restore hooked to `Application.quitting`
  (worked example: `UI/GraphicsSettingsController`); the gate is reading the property after Play exits.
- **`QualitySettings` is the opposite case:** a play-mode write lands in
  `ProjectSettings/QualitySettings.asset`. Snapshot the **authored** value from the file (or
  `git show HEAD:<path>`), never the running value (the game applies the player's saved preferences at
  startup), and run `git status ProjectSettings/` after any play session that touched it.
- **An `AssetPostprocessor` `userData` stamp also blocks repair.** Assets imported before an edited
  postprocessor compiled keep the old settings, and the stamp makes it skip them forever. Clear the stamp
  (`importer.userData = string.Empty; importer.SaveAndReimport();`) and verify the outcome (`clip.channels`,
  `importer.forceToMono`, load type). Order: edit → let Unity compile → then import.
- **`AssetDatabase.ForceReserializeAssets(paths, …)` rewrites `.asset` files but can no-op on a `.prefab`**
  (orphaned keys survive, mtime unchanged). Round-trip a prefab instead:
  `var root = PrefabUtility.LoadPrefabContents(path); try { EditorUtility.SetDirty(root); PrefabUtility.SaveAsPrefabAsset(root, path, out _); } finally { PrefabUtility.UnloadPrefabContents(root); }`.
  Gate every reserialize on a guid census (`python Tools/Python/audit_reserialize_guids.py`).
- **Stale prefab-instance overrides survive a reserialize** in scenes (`m_Modifications` keyed on a removed
  field). Use `Tools/Voxel Engine/Prune Stale Prefab Overrides In Scenes` — dry run first, a human reviews
  every candidate (a field compiled out by a platform `#if` looks removed). These menus are user-run: they
  open scenes in `Single` mode and can prompt.
- **A stub `.meta`** (only `fileFormatVersion` + `guid`, no `MonoImporter:` block) is fixed without changing
  the GUID by `AssetDatabase.ForceReserializeAssets(paths, ForceReserializeAssetsOptions.ReserializeMetadata)`.
  Never delete-and-reimport (new GUID) or hand-author the block. A stub meta alone is harmless; the
  asset-pipeline wedge is the *combination* of a stub meta, the file missing from
  `Library/Bee/artifacts/*.dag/Assembly-CSharp.rsp`, and its type absent from the DLL (`CS0246` on just
  that type, every refresh a no-op) — only an Editor restart clears that.

## AudioMixer from script

`UnityEditor.Audio.*` types are internal, but their serialized fields are not: drive a mixer through
`SerializedObject` (tool: `Assets/Editor/Dev/AudioMixerSetup.cs`, menu `Minecraft Clone/Dev/Audio/Fix Audio Mixer`).

- Exposing a parameter appends `{guid, name}` to `m_ExposedParameters`, where `guid` is the group's own
  `m_Volume` id (not `m_GroupID`). Both are `[GUID]` properties with `data[0..3]` words.
- **Read and write GUID words with `uintValue`.** `intValue` clamps any word with the high bit set to 0,
  corrupting most ids while looking successful.
- `AudioMixer.SetFloat` always returns false outside Play mode; `GetFloat` is the edit-mode check that a
  name is bound.
- Deleting a group: unlink it from the parent's `m_Children`, drop its id from
  `m_AudioMixerGroupViews[].guids`, then `RemoveObjectFromAsset` + `DestroyImmediate` it and its effects.
