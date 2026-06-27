# C — Decoupling `World.blockDatabase` from the `World` Instance

> **Status: Design (proposed). Follow-up to OM-1 — not a blocker for it.** The `BlockDatabase` is a
> world-agnostic, shared asset (every world currently uses the same one), yet it is owned by `World` as a
> serialized field and reached as `World.Instance.blockDatabase` throughout. This document specifies
> removing that ownership so the database is loaded once from a static source and the engine stops
> treating a global resource as `World` state.

**Relationship to other documents:**

- [`OM1_DEVICE_CALIBRATION.md`](./OM1_DEVICE_CALIBRATION.md) — OM-1 introduces the two enablers this
  cleanup builds on: **A** `ResourceLoader.LoadBlockDatabase()` (the static load path) and **B** the
  runtime `JobDataManagerFactory` (a `World`-free way to build job data). **C depends on A existing**;
  it should land *after* OM-1, when the load path and factory are already in place and the remaining work
  is mostly deleting the serialized field and repointing readers.
- `Architecture/DATA_STRUCTURES.md` — the `BlockDatabase` / `BlockType` data model this references.

---

## 1. Problem — a shared global asset modeled as `World` state

`World.blockDatabase` is a `public BlockDatabase` serialized field (`World.cs:78`) wired through the World
prefab's Inspector. But the database is not per-world data:

- Every world uses the **same** database; nothing varies it per save or per world type today.
- It is **world-agnostic** by nature — block definitions, materials, and custom meshes are global engine
  content, not terrain state.
- Other systems that need it already **bypass `World`** and load it independently
  (`FluidTickBenchmark` does `Resources.Load<BlockDatabase>("Data/BlockDatabase")`; the BlockEditor /
  StructureEditor windows hold their own `_blockDatabase`). The serialized field on `World` is therefore
  one of several uncoordinated access paths to a single shared asset.

The coupling forces anything needing block data to either route through the `World` singleton or
re-load the asset ad hoc — exactly the friction OM-1's calibrator hit when it had to build job data at
the Main Menu where no `World` exists.

---

## 2. Goals & non-goals

**Goals**

- Make the `BlockDatabase` reachable from a single static source of truth
  (`ResourceLoader.LoadBlockDatabase()`), independent of any `World` instance.
- Remove the `public BlockDatabase blockDatabase` serialized field from `World` (and its World-prefab
  wiring) once all readers are repointed.
- Preserve all current behavior — `World` still surfaces `OpaqueMaterial` / `TransparentMaterial` /
  `LiquidMaterial` / `BlockTypes`, just backed by the loaded instance rather than a serialized field.

**Non-goals**

- Not changing the `BlockDatabase` asset, its contents, or its on-disk format.
- Not introducing per-world database swapping. (If multiple databases are ever wanted, that is a separate
  feature; this cleanup assumes — and preserves — today's single-shared-database reality.)
- Not touching the editor windows' independent `_blockDatabase` loads except where they read it *through*
  `World` (only `WorldEditor` does).

---

## 3. Blast radius

Reference sites for `World.blockDatabase` (and the accessors backed by it):

**Runtime — concentrated in `World.cs`:**

- Declaration: `World.cs:78` (`public BlockDatabase blockDatabase`).
- Material accessors: `OpaqueMaterial`/`TransparentMaterial`/`LiquidMaterial` (`World.cs:90–92`),
  `BlockTypes` (`:80`).
- Job-data build: `PrepareGlobalJobData` (`:1750–1819`) — **subsumed by OM-1's `JobDataManagerFactory` (B)**.
- Gameplay logic: break/support/placement rules at `:2145, 2161, 2174–2175, 2197, 2205, 2211`, and
  `:3294, 3318`.

**Runtime — external:**

- `FluidTickBenchmark.cs:189` — *sets* `world.blockDatabase = db` (test scaffolding; would instead rely on
  the static loader).

**Editor:**

- `WorldEditor.cs:20, 38, 45` — reads `world.blockDatabase` (and marks it dirty for preset application).
- `BlockEditorWindow*`, `StructurePreviewWindow` — hold their **own** `_blockDatabase` (loaded
  independently); **not** `World`-coupled, out of scope.

**Serialization:**

- The **World prefab** carries the serialized object reference. Removing the field is a `.prefab`/`.meta`
  serialized-reference change — handle via the `unity-file-ops` skill, not a hand text-edit.

---

## 4. Proposed approach

1. **Land OM-1's A first** — `ResourceLoader.LoadBlockDatabase()` is the single load path C standardizes on.
2. **Back `World`'s reference with the loader.** Replace the serialized field with a `World`-owned
   instance resolved once at startup via `ResourceLoader.LoadBlockDatabase()`. The material/`BlockTypes`
   accessors and all gameplay-logic readers keep working unchanged — only their backing source moves from
   "serialized field" to "loaded instance".
3. **Repoint external readers.** `WorldEditor` reads the same loaded instance (or loads via the static
   loader directly); `FluidTickBenchmark` drops its manual `world.blockDatabase = db` assignment and lets
   the loader supply it.
4. **Remove the serialized field** and clean the World prefab's now-dangling reference (`unity-file-ops`).
5. **Optionally collapse the ad-hoc loads** (`FluidTickBenchmark`, editor windows) onto
   `ResourceLoader.LoadBlockDatabase()` for a single load path — nice-to-have, can be incremental.

---

## 5. Considerations & risks

| Concern                                     | Notes                                                                                                                                                                                           |
|---------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Prefab serialized reference**             | Removing a serialized field changes the World prefab; route through `unity-file-ops`, do not hand-edit `.prefab`/`.meta`. Verify no other serialized asset references the field.                |
| **Material accessors**                      | `OpaqueMaterial`/`TransparentMaterial`/`LiquidMaterial` are rendering-facing and must keep returning the same materials — they move with the loaded instance, they are not removed.             |
| **Load timing**                             | The loaded instance must be ready before first use in `World` startup (today the serialized field is available in `Awake`). Resolve in the same early init step that currently reads the field. |
| **Inspector workflow loss**                 | Designers can no longer swap the database via the World Inspector. Acceptable under the single-shared-database assumption (§2 non-goal); revisit only if per-world databases become a feature.  |
| **Editor preset tool**                      | `WorldEditor` marks the database dirty for preset application — ensure it operates on the same asset instance the loader returns so edits persist.                                              |
| **Blast radius is mostly intra-`World.cs`** | Most readers are inside `World` itself, lowering cross-file risk; the genuinely external coupling is just `FluidTickBenchmark` (set) and `WorldEditor` (read).                                  |

---

## 6. Verification

- `dotnet build "Assembly-CSharp.csproj"` + `Assembly-CSharp-Editor.csproj` green.
- `World` starts and renders with correct materials; block placement/break/support rules unchanged
  (the `:2145–3318` readers).
- BlockEditor / StructureEditor windows unaffected (they never used `World`'s reference).
- `FluidTickBenchmark` runs without manually assigning the database.
- No dangling serialized reference left on the World prefab.

---

## 7. Why this is separate from OM-1

OM-1 needs only **A** (a static load path) and **B** (a `World`-free factory) to run its calibrator at
the Main Menu — both of which it delivers. Fully removing `World`'s serialized field additionally drags
in the World prefab serialization and a dozen repoint sites that have nothing to do with calibration.
Bundling them would couple a performance feature to a structural cleanup and bloat the diff. With A in
place, C becomes a small, focused follow-up: load once, repoint readers, delete the field.
