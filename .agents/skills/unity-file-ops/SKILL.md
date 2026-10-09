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
- **Deleting a `.cs` file MUST delete its `.meta` file** in the same commit. An orphan `.meta` without its asset produces a duplicate-GUID warning on the next Editor import. Before deleting, `mcp__rider__safe_delete` with `preview: true` confirms no *code* references remain (it covers the code side only — still grep for the GUID to catch prefab/scene references).
- **Adding a `.cs` file:** let Unity generate the `.meta` on import. Commit both in the same commit so teammates do not get a GUID-mismatch when they pull.

### Verifying assets via the Unity CLI

Live Editor commands (mechanics: the `unity-editor` skill) complement file-level operations:

- `unity command find_assets --name <name>` (or `--type <Type>`) — an asset's path and GUID without parsing `.meta` files.
- `get_scene_hierarchy` → `get_serialized_fields --target <instanceId> --component <Type>` — verify component references are wired correctly after a move or rename, private `[SerializeField]` values included.
- `get_tags_layers` / `set_tags_layers` — verify tags and layers exist before referencing them in code, or create them instead of asking the user.
- `unity command console --level warn --tail 50 --result-only` — after a file operation, check for "missing script", "missing reference", or "duplicate GUID" warnings that indicate a break the compiler won't catch.

### Deleting serialized assets

Before deleting a prefab, ScriptableObject, or scene:

1. Search for references by GUID, not by filename. Use `unity command find_assets` to get the GUID, or open the `.meta` file and copy the `guid:` value. Then `Grep` the project for that 32-character string.
2. Check `.unity` scenes and `.prefab` files for hits — those are the call sites Unity will break.
3. If references exist, either update them to a replacement asset or confirm with the user that breakage is intended.

### Renaming a serialized field without losing data

`[SerializeField] private int _fooBar;` renamed to `_fooBaz` is a **data break**, not a compile break. Unity silently resets the field to its default on next scene/prefab load — any data previously set in the Inspector is gone.

**This project does not use `[FormerlySerializedAs]`.** Every asset is in version control and the pre-commit reserialize surfaces a dropped value immediately, so the attribute would be permanent clutter guarding a loss that is already caught and cheaply reversed. Instead:

1. **Audit the exposure before renaming.** Serialized keys are a surface separate from the binary chunk format: sweep HEAD's YAML for the old key as a whole word (`git grep -nwF "_fooBar" HEAD -- '*.unity' '*.prefab' '*.asset'`), which catches both the field line `_fooBar: …` and prefab-instance overrides in scenes (`propertyPath: _fooBar`, `parent._fooBar`), and report what is at risk.
2. **Rename, then reserialize** (`Tools/Voxel Engine/Force Reserialize All Assets`, CLAUDE.md pre-commit step 0).
3. **Diff the touched assets; restore any dropped value** by copying the old YAML value back from `git show HEAD:<file>`, through the Editor. A reserialize does **not** migrate a scene's prefab-instance override (`m_Modifications` keeps the stale `propertyPath`), so every override hit from step 1 must be re-applied on the instance under the new name, and the stale
   `propertyPath: _fooBar` entry then removed with `Tools/Voxel Engine/Prune Stale Prefab Overrides In Scenes`
   (user-run: dry run first, a human reviews each candidate). A `.prefab` that still carries the old key
   needs the `PrefabUtility.LoadPrefabContents` → `SaveAsPrefabAsset` round-trip — a reserialize can leave it
   untouched (recipe: the `unity-editor` skill's `references/editor-writes.md`).
4. **Verify in the YAML, not through the runtime accessor.** Grep each asset for both the old and the new key as whole words (`grep -nw`): the old must be gone — from field lines and `propertyPath:` overrides alike — and the new present. An accessor read-back can report the expected value while the asset still carries the old key — an absent key leaves the C# initializer in place, which matches whenever the authored value was the default.

### Scene and prefab merge conflicts

`.unity` and `.prefab` files are YAML but order-sensitive and GUID-heavy. Do not hand-edit them in a text editor to resolve conflicts unless you understand Unity's YAML serialization format.

- Preferred: use Unity's built-in **Smart Merge** (`UnityYAMLMerge`) — configure once in `.gitconfig` via `git config --global merge.unityyamlmerge.cmd ...`.
- If no smart-merge is configured: ask the user to open both branches in two Unity instances and reconcile manually, then commit the result.
- Never use `git checkout --theirs` or `--ours` on a scene/prefab blindly — you will silently drop work from one side.

### Recovering from `.meta` / asset mismatches

- **`.cs` without `.meta`:** open the Editor, let Unity generate the `.meta` on import, commit it.
- **`.meta` without `.cs`:** either restore the `.cs` from git history or delete the orphan `.meta`. Do not leave orphan `.meta` files — they cause duplicate-GUID warnings.
- **Duplicate GUID warning:** two `.meta` files contain the same `guid:` value. Usually caused by copy-pasting a folder instead of duplicating through the Editor. Change one of the GUIDs (Unity will regenerate on next import) or delete the duplicate.

### Hand-editing a ScriptableObject `.asset` (only when the user asks)

For plain-field assets only — no `{fileID:}` / GUID references:

- Edit in Python, not `Edit`: Unity writes an empty scalar as `field: ` **with a trailing space**, which `Edit` cannot express. Assert each anchor matches exactly once and write with `newline="\n"`.
- Validate with a real parser: strip the `%YAML`, `%TAG` and `--- !u!` lines, then `yaml.safe_load` the body (`python -m pip install pyyaml`).
- Quote a scalar only when required — a value containing `": "` must be single-quoted. Non-ASCII may be written literally; Unity re-escapes and re-folds long scalars on its next save (a re-fold is a spurious hunk to revert when splitting commits).
- Enum fields are stored as bare ints, so an enum backing an asset is append-only: pin explicit values and land the enum and its data in the same commit.
- The proof it is well-formed is a reserialize whose `git diff --numstat` shows **0 deletions**.

Writing assets through the Editor instead (scenes, prefabs, import settings) has its own traps: the `unity-editor` skill's `references/editor-writes.md`.

## Never

- Never manually text-edit `.meta`, `.prefab`, `.unity`, or ScriptableObject `.asset` files unless the user explicitly asks. Let the Editor handle serialization.
- Never add `[FormerlySerializedAs]` — use the rename workflow above.
- Never delete a `.meta` file without also deleting its asset (or confirming the asset is already gone).
- Never do `git rm {file}.cs` without also removing `{file}.cs.meta`.
