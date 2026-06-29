# Decoupling `World.blockDatabase` from the `World` Instance

> **Status: Implemented.** Follow-up to OM-1[^c], landed once OM-1's enablers A/B were in place. The
> `BlockDatabase` was a world-agnostic, shared asset (every world uses the same one) yet was owned by
> `World` as a serialized field and reached as `World.Instance.blockDatabase` throughout. The serialized
> field is gone: `World` now resolves the database once in `Awake` via `ResourceLoader.LoadBlockDatabase()`
> and exposes it through a read-only `World.BlockDatabase` property; the material / `BlockTypes` accessors
> and all gameplay readers are backed by that loaded instance.
>
> **Two implementation notes beyond §3's original blast radius** (both grew after this doc was first
> written): the edit-mode validation fixtures `BehaviorTestWorld` and `SectionRendererTestFixture` inject a
> *stub* database. Because they bypass `Awake`, they now set the private backing field directly via
> `ValidationReflection.SetInstanceField(world, "_blockDatabase", stub)` (consistent with how they already
> inject `Instance`/`ChunkPool`); the `Awake` loader only runs when the field is still null, so the stub is
> never overwritten. `FluidTickBenchmark` dropped its manual assignment and relies on the loader (it uses
> the real asset anyway), keeping only its pre-flight existence check.

**Relationship to other documents:**

- [`OM1_DEVICE_CALIBRATION.md`](../Design/OM1_DEVICE_CALIBRATION.md) — OM-1 introduces the two enablers this
  cleanup builds on: **A** `ResourceLoader.LoadBlockDatabase()` (the static load path) and **B** the
  runtime `JobDataManagerFactory` (a `World`-free way to build job data). **C depends on A existing**;
  it should land *after* OM-1, when the load path and factory are already in place and the remaining work
  is mostly deleting the serialized field and repointing readers.
- [`DATA_STRUCTURES.md`](./DATA_STRUCTURES.md) — the `BlockDatabase` / `BlockType` data model this references.

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

- `WorldEditor.cs` — *was* a `[CustomEditor(typeof(World))]` that read `world.blockDatabase` for its
  preset tool. It turned out to be entirely commented out (dead since before this work), so it had no live
  edit-time read to repoint — and a `World.BlockDatabase` that resolves only at runtime (in `Awake`) would
  have read null in the edit-time inspector anyway. The dead file was deleted as part of this cleanup.
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
5. **Collapse the remaining ad-hoc loads** onto a single path — *done as a follow-up.* On inspection this
   was far smaller than §4.5 first implied, and splits by assembly:
    - **Runtime → `ResourceLoader.LoadBlockDatabase()`:** `FluidTickBenchmark` was the only straggler (a
      duplicated `Resources.Load<BlockDatabase>("Data/BlockDatabase")`); it now calls the loader. Every
      runtime BlockDatabase load (`World`, `DeviceCalibration`, the benchmark) goes through one path.
    - **Editor → `EditorBlockDatabaseCache.Database`:** editor tools must **not** use `ResourceLoader`
      (which is `Resources.Load`-based). `EditorBlockDatabaseCache` intentionally uses `AssetDatabase`
      (finds the asset anywhere, not just under `Resources/`; builds an OnGUI lookup dict; auto-refreshes on
      domain reload; yields an editable, path-anchored reference for `SetDirty`/`SaveAssets`). The editor
      windows (`BlockEditorWindow`, `StructurePreviewWindow`) **already** route through it; the lone
      straggler was `ActiveVoxelScanBenchmark` (hardcoded asset path), now repointed at the cache.
      `BlockIdGenerator` keeps its own `FindAssets` — code-gen genuinely needs the asset *path* to emit
      `BlockIDs.cs` beside it. This supersedes §4.5's "editor windows … onto `ResourceLoader`" wording,
      which conflicted with §2's non-goal; §2 was correct.

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

---

[^c]: OM-1's design doc ([`OM1_DEVICE_CALIBRATION.md`](../Design/OM1_DEVICE_CALIBRATION.md)) labels this
cleanup **C** — the third follow-up after enablers **A** (`ResourceLoader.LoadBlockDatabase()`) and
**B** (the shared runtime `JobDataManagerFactory`). That **A/B/C** lettering is retained there for
cross-reference; this document no longer carries the "C — " prefix in its title.
