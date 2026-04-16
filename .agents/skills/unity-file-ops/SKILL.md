---
name: unity-file-ops
description: Authoritative rules for Unity file operations that are not pure refactors — deleting assets, resolving scene/prefab merge conflicts, renaming serialized fields without losing data, fixing missing-GUID errors, and recovering from orphaned .meta files. Use when the user deletes a script, hits a "The associated script cannot be loaded" error, resolves a merge conflict in a .unity or .prefab file, or asks about [FormerlySerializedAs].
---

# Unity File Operations Protocol

Unity tracks assets by GUID, stored in the sibling `.meta` file. Text-level file operations that ignore this indirection silently break prefab and scene references. This skill is the authoritative source for the GUID rule and related data-preservation concerns.

## When to use this skill

- Deleting a `.cs` file, `.prefab`, `.unity`, or ScriptableObject `.asset`.
- Hitting "The associated script cannot be loaded" in the Editor.
- Resolving a merge conflict inside a `.unity` or `.prefab` file.
- Renaming a `[SerializeField] private` field or public field that prefabs/scenes reference.
- "I committed a `.cs` without its `.meta`" / "I committed a `.meta` without its `.cs`".
- Duplicate GUID warnings in the Editor console.

## How to use it

### The `.meta` GUID rule (authoritative)

Every asset that Unity tracks has a `{asset}.meta` sibling containing a GUID. Scenes, prefabs, and ScriptableObjects reference other assets by that GUID — never by file path. Therefore:

- **Moving or renaming a `.cs` file MUST move its `.meta` file along with it.** Use `git mv` for both, or move both in the file system and commit together. A missing `.meta` migration leaves the file compiling fine but every prefab that referenced the script shows "missing script" in the Editor.
- **Deleting a `.cs` file MUST delete its `.meta` file** in the same commit. An orphan `.meta` without its asset produces a duplicate-GUID warning on the next Editor import.
- **Adding a `.cs` file:** let Unity generate the `.meta` on import. Commit both in the same commit so teammates do not get a GUID-mismatch when they pull.

### Deleting serialized assets

Before deleting a prefab, ScriptableObject, or scene:

1. Search for references by GUID, not by filename. Open the `.meta` file, copy the `guid:` value, then `Grep` the project for that 32-character string.
2. Check `.unity` scenes and `.prefab` files for hits — those are the call sites Unity will break.
3. If references exist, either update them to a replacement asset or confirm with the user that breakage is intended.

### Renaming a serialized field without losing data

`[SerializeField] private int _fooBar;` renamed to `_fooBaz` is a **data break**, not a compile break. Unity silently resets the field to its default on next scene/prefab load — any data previously set in the Inspector is gone.

Two safe options:

- **`[FormerlySerializedAs]`** — preserves the data:

  ```csharp
  [FormerlySerializedAs("_fooBar")]
  [SerializeField] private int _fooBaz;
  ```

  Requires `using UnityEngine.Serialization;`. Keep the attribute for at least one release cycle or until you are certain every scene/prefab has been re-saved.

- **Manual migration** — grep `.unity`/`.prefab`/`.asset` files for the old field name and replace with the new name. Riskier; prefer `[FormerlySerializedAs]`.

### Scene and prefab merge conflicts

`.unity` and `.prefab` files are YAML but order-sensitive and GUID-heavy. Do not hand-edit them in a text editor to resolve conflicts unless you understand Unity's YAML serialization format.

- Preferred: use Unity's built-in **Smart Merge** (`UnityYAMLMerge`) — configure once in `.gitconfig` via `git config --global merge.unityyamlmerge.cmd ...`.
- If no smart-merge is configured: ask the user to open both branches in two Unity instances and reconcile manually, then commit the result.
- Never use `git checkout --theirs` or `--ours` on a scene/prefab blindly — you will silently drop work from one side.

### Recovering from `.meta` / asset mismatches

- **`.cs` without `.meta`:** open the Editor, let Unity generate the `.meta` on import, commit it.
- **`.meta` without `.cs`:** either restore the `.cs` from git history or delete the orphan `.meta`. Do not leave orphan `.meta` files — they cause duplicate-GUID warnings.
- **Duplicate GUID warning:** two `.meta` files contain the same `guid:` value. Usually caused by copy-pasting a folder instead of duplicating through the Editor. Change one of the GUIDs (Unity will regenerate on next import) or delete the duplicate.

## Never

- Never manually text-edit `.meta`, `.prefab`, `.unity`, or ScriptableObject `.asset` files unless the user explicitly asks. Let the Editor handle serialization.
- Never delete a `.meta` file without also deleting its asset (or confirming the asset is already gone).
- Never do `git rm {file}.cs` without also removing `{file}.cs.meta`.
