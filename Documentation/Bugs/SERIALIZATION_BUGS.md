# Known Serialization & Storage related bugs

This document outlines **open** bugs related to saving, loading, Region files, and Mod Manager. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** August 2026
>
> **Numbering note:** `§02`, `§03` and `§06` are **retired, not free** — all three are archived in
> [`_FIXED_BUGS.md`](./_FIXED_BUGS.md), and `§03` is still cited by name from
> `CompressionFactory.cs`, `LIBRARY_BUGS.md` and
> `INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md`. New entries continue from `§10`.

---

## 01. Region File Thread Safety adds massive overhead

**Severity:** Performance / Concurrency  
**Files:** `RegionFile.cs`

The `_fileLock` works correctly to prevent save data corruption but adds massive overhead.

**Status:** Needs careful architectural changes to split read and write concurrency. See `Documentation/Design/REGION_FILE_CONCURRENCY.md` for a full breakdown of requirements before addressing this.

---

> Bug 02 (Mod Manager / Block Database initialization coupling) no longer describes live code —
> `RestoreChunkModifications` was dissolved by a later refactor. Archived to
> [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> Bug 03 (NativeCompressions LZ4Stream asymmetric format hang, June 2026) has been fixed and
> archived to [`_FIXED_BUGS.md`](./_FIXED_BUGS.md). The 0.6.0 version pin and library follow-ups
> remain documented in [`LIBRARY_BUGS.md`](./LIBRARY_BUGS.md).

---

## 04. Fixed 256 KB serialization buffer can overflow on dense chunks with large pending light queues

**Severity:** Bug (silent data loss)  
**Confidence:** High (mechanism verified by code inspection; likelihood in normal play is low–medium)  
**Files:** `SerializationBufferPool.cs` (BUFFER_SIZE), `ChunkSerializer.cs` — `Serialize`, `WriteChunkInternal`, `WriteLightQueue`, `ChunkStorageManager.cs` — `SaveChunk` / `SaveChunkAsync`

`ChunkSerializer.Serialize` writes into a **non-expandable** `MemoryStream(outputBuffer)` over a pooled fixed 256 KB buffer. The worst-case uncompressed chunk payload is ~197 KB (8 sections × flag 0x01 = voxels 16 KB + LightData 8 KB each, plus header/heightmap/bitmask), leaving only ~65 KB of headroom. The pending BFS light queues are serialized **without any count cap** at 16 bytes per node — roughly **4,000 queued nodes across both queues exhaust the buffer**. When that happens, `MemoryStream` throws `NotSupportedException`, the exception is caught
and logged in `SaveChunk`/`SaveChunkAsync`, and the chunk is **silently not saved** (reverts to its last saved state, or regenerates, on next load).

Most realistic trigger: chunks at the edge of the load area accumulate queue entries via `ModifyVoxel` (each edit enqueues ~7 nodes) while their lighting job can't run (`AreNeighborsDataReady` false), then an autosave fires. `CompressionAlgorithm.None` removes the compression safety margin entirely.

**Related asymmetry:** `WriteLightQueue` writes unbounded counts, but `ReadLightQueue` throws `InvalidDataException` for counts > 100,000 — a chunk saved with a queue between the buffer limit and 100k can never exist, but the bounds should match whatever cap is chosen.

**Proposed fix:** Cap (or drop-and-flag) the serialized light queues — e.g. clamp to a few thousand nodes and set `NeedsInitialLighting`/column-recalc flags instead — and/or grow the buffer / use an expandable stream with pooled segments. Not save-format-breaking if the cap is write-side only.

---

## 05. `WriteChunkInternal` "snapshot" of `SectionUniformSkyLevel` is a reference copy, not a value copy

**Severity:** Latent race condition (currently unreachable in normal flow)  
**Confidence:** Medium (race window verified, but current call patterns avoid it)  
**Files:** `ChunkSerializer.cs` — `WriteChunkInternal` (line ~135), `ChunkStorageManager.cs` — `SaveChunk`, `WorldJobManager.cs` — `TryCompactSectionLight`

`WriteChunkInternal` contains `byte[] skyLevels = data.SectionUniformSkyLevel;` with a comment claiming a *"value copy is safe for the background thread"* — but this copies the **array reference**, not the values. If the main thread mutates `SectionUniformSkyLevel` (e.g. `TryCompactSectionLight` after a lighting job, or `PromoteCompactSection` on a block edit) while a background thread serializes the **live** `ChunkData`, the bitmask phase and the section-write phase can observe different values. Worst case: a slot is included in the bitmask as a compact
light-only section (`safeSections[i] == null`, sky set), then the sky level flips to `UNIFORM_SKY_NONE` before the write loop → **neither branch writes anything** → all subsequent sections shift → corrupt chunk payload (caught on load → chunk regenerates).

**Why it doesn't currently fire:** `SaveChunkAsync` serializes an isolated snapshot created on the main thread (`CreateSerializationSnapshot`), and the synchronous `SaveChunk` path is only called from the main thread. The bug becomes live the moment anyone passes a **live** `ChunkData` to a background `Serialize` call.

**Proposed fix:** Actually copy the array (`skyLevels = (byte[])data.SectionUniformSkyLevel.Clone()` or copy into a pooled buffer), and read `skyLevels[i]` (the local copy) in the write loop — or document loudly that `WriteChunkInternal` must only ever receive snapshots/main-thread data. Note the queue `lock`s in `WriteChunkInternal` have the same asymmetry: the main-thread enqueue sites (`AddToSunLightQueue` etc.) do not lock, so the reader-side locks only protect snapshot objects that nobody else touches anyway.

---

> Bug 06 (deserialization failure leaks pooled objects) has been fixed — `ReadChunkInternal` now
> hoists the pooled shell above its `try` and returns it in the `catch`. Archived to
> [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

---

## 07. RegionFile robustness niggles (grouped)

**Severity:** Minor / Robustness  
**Confidence:** High (verified by inspection); each item is low impact  
**Files:** `RegionFile.cs`

1. **Partial reads treated as corruption:** `LoadChunkData` issues single `_fileStream.Read(...)` calls and returns null when fewer bytes than requested arrive. `FileStream` on local disks practically always fills the buffer, but a read-exact loop (like `ChunkSerializer.ReadBulkData`) would make this airtight.
2. **Unsynchronized `_offsets` in `GetAllChunkCoords`:** already flagged by an inline TODO — the iterator reads `_offsets` without the lock while writers update it under `_fileLock`. `int` reads are atomic so this can't tear, but it can observe mid-migration state. Fold into the Bug 01 concurrency rework.
3. **Crash window during relocation:** when a chunk grows/shrinks, its old sectors are freed and may be reused by a *different* chunk's write before this chunk's new offset-table entry is flushed. A crash in that window leaves the table pointing at sectors now owned by another chunk (detected on load by the length/version sanity checks → chunk regenerates, but the data is gone). Minecraft's region format has the same window; worth noting in the Bug 01 redesign.
4. **Trailing free sectors ignored:** `FindFreeSectors` appends at `_sectorUsage.Count` even when the file ends with a run of free sectors, growing files slightly faster than necessary.

---

## 08. `LightingStateManager.AddPending` logs invalid columns but stores them anyway

**Severity:** Minor  
**Confidence:** High  
**Files:** `LightingStateManager.cs` — `AddPending` (lines ~38–57)

The validation loop only `Debug.LogError`s out-of-range local columns; the subsequent add loop inserts **all** columns including invalid ones. On `Save()` they are byte-truncated (`(byte)col.x`), and on `Load()` the truncated values may pass validation and queue sunlight recalcs for the wrong columns. Fix: `continue`/skip invalid columns in the add loop (or validate-and-skip in one pass).

---

## 09. Loading a world with an unreadable `level.dat` has no failure contract — the player spawns unplaced

**Severity:** Minor (edge case) / Missing failure contract  
**Confidence:** High (found by static reading during the SP-1 refactor; not observed in-game)  
**Files:** `World.cs` — `StartWorld` load block; `Spawn/SpawnResolution.cs` — `Classify`

`StartWorld` guards the metadata read (`if (metadata != null)`) but the spawn classification does not: a world opened
from the menu (`!isNewGame`) with persistence enabled classifies as `LoadedSave` **regardless of whether
`SaveSystem.LoadWorldMetadata` actually returned anything**. When it returns null (missing or corrupt `level.dat`),
nothing supplies a saved position, so the player is placed at whatever position the Player prefab carries — with no
surface height probe, since `LoadedSave` deliberately probes only the spawn point. Inventory, rotation, time of day,
and the border radius are likewise silently skipped.

Reachability is low but not zero: the world-select menu lists saves by reading `level.dat`, so the file must become
unreadable *between* the menu listing and world load (external deletion, corruption, a permissions/IO failure).

**Preserved deliberately, not introduced, by SP-1.** This is pre-SP-1 behavior, kept byte-for-byte because that
refactor's contract was "change nothing"; SP-1 only made it visible in one place. It is documented in
`SpawnResolution.Classify`'s `<remarks>` and pinned by the *"Spawn Classify Pins All Flag Combinations"* baseline in
the **Validate Spawn** suite — so a fix is a deliberate, visible edit (that baseline must be updated to go green,
which is the intended signal).

**Status:** Open, unscheduled. The fix is one condition — `Classify` consults `hasExistingMetadata` on the
`LoadedSave` arm and falls through to `Fresh` — but it is a behavior change in the save-loading path and wants its
own decision: silently spawning fresh discards a possibly-recoverable world, so surfacing the failure to the player
(rather than treating a corrupt save as a new world) is likely the better contract.

---

## 10. A v1 world is repacked but never format-migrated — every chunk regenerates from seed

**Severity:** Bug (silent total data loss, scoped to v1 worlds)  
**Confidence:** Mechanism High **in current `HEAD`** (verified by code inspection; reproduced by the Migration
Chain suite's `K10`). **Whether it was ever shipped working is OPEN** — see "Unresolved: was this always
broken?" below, which must be settled before any fix is designed.  
**Files:** `MigrationManager.cs` — `RunAOTMigrationAsync` (the `needsLayoutMigration` branch), `Migration_v1_to_v2_RegionRepack.cs` — `PerformRegionLayoutMigration` / `ProcessOldRegionFile`

`RunAOTMigrationAsync` chooses **one** of two region strategies:

```csharp
bool needsLayoutMigration = migrationPath.Any(s => s.RequiresRegionLayoutMigration);
if (!Directory.Exists(regionPath))        { /* skip */ }
else if (needsLayoutMigration)            { /* PerformRegionLayoutMigration + directory swap */ }
else                                      { /* the per-chunk MigrateChunk format chain */ }
```

The two branches are mutually exclusive, and `MigrationV1ToV2RegionRepack` is the only step that sets
`RequiresRegionLayoutMigration`. So for a **v1** world — the only version whose migration path contains that step —
the `else` branch never runs and **the chunk-format chain is skipped entirely**. `ProcessOldRegionFile` only
decompresses, recompresses and rewrites at the corrected address; its own summary says so ("The chunk binary payload
is unchanged — only the addressing is corrected").

The result is a world stamped `v15` on disk whose chunk payloads are still **chunk-format v1/v2**. On load,
`ChunkSerializer.Deserialize` reads a version byte of 1 or 2, takes the wrong-version path (the one
`Validate Deserialization Robustness` **B4** pins as "→ null, no throw"), and returns null for every chunk — so the
engine regenerates the whole world from seed. Every player edit is gone, and the pre-migration backup has already
been rotated by the time it is noticeable.

**Not reachable from v2 or later.** A world at v2–v9 has no layout step in its path, so it takes the `else` branch
and its chunks *are* format-migrated correctly. The defect is specific to world version 1.

**Live blast radius is probably zero, but the code path is real.** No v1 save is known to survive on this machine
(the oldest backups present are v6), and any v1 world migrated before this was found would already have lost its
chunks. That is why this is filed rather than hot-fixed.

**Repro:** `Minecraft Clone/Dev/Validate Migration Chain` → `K10` (a complete v1 fixture world — v1 `level.dat`,
`pending_mods`, and region files at the historically broken V1 addresses — migrated to current, then every chunk
read back through the real `ChunkSerializer.Deserialize`). It asserts the chunks are *readable*, which is the
correct post-migration contract, and is therefore expected **red** until this is fixed.

### Unresolved: was this always broken, or is it a regression? *(open question, 2026-08-20)*

**The project owner recalls the v1→v2 migration working correctly**, but a long time ago — before the
Migration Chain suite existed and before several serialization changes landed. So the branch structure above
may be a **regression introduced after the repack step was written**, not an original defect. Nothing in this
entry settles that, and the answer changes the fix: a regression is reverted, an original defect is designed
around.

Established at `HEAD` (do not re-derive): the two region strategies are mutually exclusive, `v1→v2` is the
only step setting `RequiresRegionLayoutMigration`, `ProcessOldRegionFile` does not format-migrate, and `K10`
observes 0 of 2 chunks readable after migrating an authored v1 world.

**Not established — the actual open questions:**

1. **Was the branch ever non-exclusive?** `MigrationManager.cs` has ~28 commits (first `2834a572`
   2026-02-10 "Initial rework of the save system", most recent `e6181f2d` 2026-08-10). Two leads worth
   walking: `Migration_v1_to_v2_RegionRepack.cs` was authored `0865f6cb` (2026-02-28, "Broken region file
   usage leading to max 4 sections being used per region file"), and it was last touched by `07609bdd`
   (2026-07-22, the CP-3 commit — which also touched `MigrationManager.cs` and whose message mentions
   "migration fault isolation"). Read the region-branch structure as it stood at the repack's authoring
   commit and diff it forward.
2. **Did some other mechanism upgrade v1 chunk payloads at the time?** A historical load-time/lazy format
   upgrade in `ChunkSerializer` or `ChunkStorageManager` would make this a non-bug for the era it shipped in.
   Today `Deserialize` hard-rejects a wrong version byte (pinned by `Validate Deserialization Robustness`
   **B4**), but that contract is itself CP-3-era.
3. **Is `K10`'s fixture faithful to a real v1 world?** Its chunk payload is authored from the migration
   steps' own inline read definitions (the only surviving record of the layout) — see the limits documented
   in `MigrationChainValidationSuite.ChunkFixture.cs`. If a real v1 world differed in any way that changes
   which branch runs, `K10` could be reproducing a fixture artifact rather than the shipped defect.

**What would settle it:** a git-archaeology pass over `MigrationManager.cs`'s region-branch structure across
the range above, answering "at the commit where the repack shipped, did a v1 world's chunks get
format-migrated?" — plus, if a genuine v1-era save can be located, one real load. Until then treat the
severity as conditional.

**Proposed fix (undecided — do not apply without a decision):** run the format chain over the repacked chunks. Two
shapes, and the choice matters: either `PerformRegionLayoutMigration` receives the remaining `migrationPath` and
applies `MigrateChunk` as it rewrites each payload, or the manager stops treating the branches as exclusive and runs
the format loop over the swapped-in region folder afterwards. The second is less invasive to the step contract but
walks every chunk twice. Either way this touches shipped migration orchestration and needs an in-game load of a real
old save before it can be trusted — `K10` going green is necessary, not sufficient.
