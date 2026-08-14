# Serialization & serialized-field gates

Load when the diff touches `Assets/Scripts/Serialization/`, `ChunkData.cs`,
`ChunkStorageManager.cs`, or a `[SerializeField]` / public field on a
`MonoBehaviour` or `ScriptableObject`.

Both gates here are silent-data-loss gates: nothing fails at compile time, and
the damage lands on data that already exists on disk or in a scene — a player's
save, a wired-up prefab. Reference `INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md`
and `AOT_WORLD_MIGRATION_SYSTEM.md`; route to `serialization-migration` (gate 8)
and `refactor-safely` / `unity-file-ops` (gate 9).

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

**Absolute** (for the layout break). The migration-authoring half is the only
part that defers.

**Severity.** Blocker. Corrupting or dropping a player's world is the worst
outcome this review guards against.

---

## Gate 9 — Serialized field renamed or deleted without `[FormerlySerializedAs]`

**What fails.** A `[SerializeField] private` field, or a `public` field
referenced by prefabs/scenes/ScriptableObjects, is **renamed or deleted** and no
`[FormerlySerializedAs("oldName")]` preserves the binding. Unity matches
serialized data by field name; the moment the scene/prefab/asset re-serializes,
the old value is gone — a wired Inspector reference becomes null, a tuned value
resets to default.

**How to check.** Read the `-`/`+` pair on the field. A rename shows as a removed
field and an added one with a new name; a deletion shows as a removed field with
no replacement. For either, the question is: is there a `[FormerlySerializedAs]`
(rename) or was the field genuinely unused by any asset (deletion)? Rider
`safe_delete` with `preview: true` doubles as the "is this field referenced
anywhere" check; if the IDE is closed, treat an Inspector-facing field as
referenced and flag it (uncertain), tool on `Not verified`.

Renaming with Rider `rename_refactoring` does **not** add `[FormerlySerializedAs]`
or fix `.meta`/prefab GUID bindings — the `refactor-safely` and `unity-file-ops`
guardrails still apply on top of any Rider rename. This is why the gate exists
even when a "safe" rename tool was used.

**Delta-based.**

**Severity.** Blocker when the field is Inspector-wired or holds tuned data
(silent loss). Downgrade to Medium only if you can show the field was never
serialized into any asset (a brand-new field renamed within the same uncommitted
session, e.g.) — and say how you know.
