# Serialization & serialized-field gates

Load when the diff touches `Assets/Scripts/Serialization/`, `ChunkData.cs`,
`ChunkStorageManager.cs`, or a `[SerializeField]` / public field on a
`MonoBehaviour` or `ScriptableObject`.

Both gates here are silent-data-loss gates: nothing fails at compile time, and
the damage lands on data that already exists on disk or in a scene — a player's
save, a wired-up prefab. Reference `INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md`
and `AOT_WORLD_MIGRATION_SYSTEM.md`; route to `serialization-migration` (gate 8)
and `refactor-safely` / `unity-file-ops` (gate 9).

**Source of truth: `.agents/rules/serialization-safety.md`.** It carries the
five-step AOT migration protocol and the frozen-DTO rule that gate 8 only
summarizes. Read it when gate 8 fires.

Each gate carries **what fails**, **how to check**, **severity**, and its
delta/absolute nature.

---

## Gate 8 — On-disk serialization layout changed with no AOT migration

**What fails.** The diff changes the **on-disk binary layout** of terrain data —
a field added, removed, reordered, or resized in a serialized chunk/region
struct, or a change to how `ChunkStorageManager` reads/writes it — and there is no
AOT migration step to carry old saves forward. An existing world silently fails to
load, or loads corrupt.

**How to check.** Read the changed struct/reader/writer against
`AOT_WORLD_MIGRATION_SYSTEM.md`. The **now half** is the layout break itself: the
moment a serialized terrain field's order/width/presence changes, that is a
Blocker, whether or not the migration is written. The **owed half** (intermediate
runs) is *authoring* the migration step and the in-editor round-trip that proves
an old save still loads — that needs an Editor session and can sit under `Still
owed before merge`. Do not let the owed half excuse the now half: a layout change
with no migration *plan* is still a finding, it just may not be fully written yet.

A change that only touches **in-memory** structure, or bumps a version number and
adds a migration branch, is not a violation — that is the sanctioned path.

Three failures that are *not* layout changes and would otherwise slip past this
gate, all from `serialization-safety.md`:

- a migration step that **imports a live engine type** (`ChunkData`,
  `ChunkSection`, `VoxelState`) instead of a frozen DTO — the step silently
  changes meaning the next time that type is rewritten
- a chunk-layout change that bumps `CURRENT_VERSION` but **not**
  `TargetChunkFormatVersion`, or that overrides `MigrateChunk` without writing the
  new version byte first (the manager fail-fasts with `InvalidDataException`)
- an edit to an **already-shipped** migration step that changes what it
  *produces*. Its byte transform must stay bit-identical for every input it
  previously handled; semantic changes go in a new step. Non-semantic hardening
  (error handling, fault isolation, logging, retries) is allowed.

**Absolute** (for the layout break). The migration-authoring half is the only
part that defers.

**Severity.** Blocker. Corrupting or dropping a player's world is the worst
outcome this review guards against.

---

## Gate 9 — Serialized field renamed or deleted without its asset data carried over

**What fails.** A `[SerializeField] private` field, or a `public` field
referenced by prefabs/scenes/ScriptableObjects, is **renamed or deleted** and the
assets that held a value under the old key lose it. Unity matches serialized data
by field name; the moment the scene/prefab/asset re-serializes, the old value is
gone — a wired Inspector reference becomes null, a tuned value resets to default.
The project deliberately does not use `[FormerlySerializedAs]`: the rename is
carried over by the `unity-file-ops` workflow (reserialize, diff, restore dropped
values from git), so **do not flag a missing attribute** — flag a missing carry-over.

**How to check.** Read the `-`/`+` pair on the field. A rename shows as a removed
field and an added one with a new name; a deletion shows as a removed field with
no replacement. Then grep HEAD's YAML for the old key
as a whole word (`git grep -nwF "_oldName" HEAD -- '*.unity' '*.prefab' '*.asset'`),
which also catches scene prefab-instance overrides (`propertyPath: _oldName`,
`parent._oldName`) — a reserialize never migrates those. For each hit, the change
must carry that asset with the old key gone and the new key holding the **same
value** (a reference keeps its `guid`; an override is re-keyed to the new name). An asset still carrying the old
key is Medium — name the asset and the step that clears it: the reserialize for an
`.asset`, the `LoadPrefabContents` round-trip for a `.prefab` (a reserialize can leave
one untouched), and the user-run Prune Stale Prefab Overrides In Scenes for a scene
override (a reserialize never removes those); a new key at
the C# default where the old one held something else is the loss (Blocker).
Never verify through the runtime accessor — an absent key falls back to the
initializer, which matches whenever the authored value was the default.

Renaming with Rider `rename_refactoring` does not touch serialized keys or
`.meta`/prefab GUID bindings — the `refactor-safely` and `unity-file-ops`
guardrails still apply on top of any Rider rename. This is why the gate exists
even when a "safe" rename tool was used.

**Delta-based.**

**Severity.** Blocker when an asset's value under the old key did not survive
(silent loss). Medium when the old key is still in an asset because the
reserialize has not run yet. No finding when HEAD's YAML never held the old key
(a brand-new field renamed within the same uncommitted session, e.g.) — say how
you know.
