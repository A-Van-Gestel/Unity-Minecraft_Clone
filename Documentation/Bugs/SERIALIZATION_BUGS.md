# Known Serialization & Storage related bugs

This document outlines **open** bugs related to saving, loading, Region files, and Mod Manager. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** August 2026
>
> **Numbering note:** `§02`, `§03`, `§06` and `§10` are **retired, not free** — all four are archived in
> [`_FIXED_BUGS.md`](./_FIXED_BUGS.md), and `§03` is still cited by name from
> `CompressionFactory.cs`, `LIBRARY_BUGS.md` and
> `INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md`. New entries continue from `§14`.

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

**Severity:** Bug (data loss)  
**Confidence:** High (mechanism **reproduced**, 2026-08-21 — no longer inspection-only; likelihood in normal play is low–medium)  
**Files:** `SerializationBufferPool.cs` (BUFFER_SIZE), `ChunkSerializer.cs` — `Serialize`, `WriteChunkInternal`, `WriteLightQueue`, `ChunkStorageManager.cs` — `SaveChunk` / `SaveChunkAsync`  
**Repro:** `K04` in `Minecraft Clone/Dev/Validate Serialization Round-Trip` (NS-1). A dense chunk (8 sections × flag 0x01) with 2,500 nodes in each BFS queue, saved under `CompressionAlgorithm.None`. The scenario carries a control leg — the same chunk with 100-node queues — which passes, so the failure is attributable to the queue size rather than to the fixture.

`ChunkSerializer.Serialize` writes into a **non-expandable** `MemoryStream(outputBuffer)` over a pooled fixed 256 KB buffer. The worst-case uncompressed chunk payload is ~197 KB (8 sections × flag 0x01 = voxels 16 KB + LightData 8 KB each, plus header/heightmap/bitmask), leaving only ~65 KB of headroom. The pending BFS light queues are serialized **without any count cap** at 16 bytes per node — roughly **4,000 queued nodes across both queues exhaust the buffer**. When that happens, `MemoryStream` throws `NotSupportedException` (*"Memory stream is not expandable."*), which `SaveChunkAsync` maps to `ChunkSaveResult.Failed`.

**Corrected 2026-08-21 by the `K04` repro:** the outcome is no longer the silent drop this entry originally described — CP-6's durability layer catches the throw and stages the chunk in the retry registry. But the fault is *deterministic* (the buffer is always 256 KB), so every retry fails identically, and the observed end state is the retry loop exhausting into `Dispose`'s final flush, which logs **"this session's edits to that chunk are lost"**. Same data loss, reached through retry exhaustion rather than a swallowed exception — and it is `Failed` (retryable) rather than `FailedPermanent`, so the doomed chunk occupies the retry loop for the whole session.

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

**Proposed fix:** Actually copy the array (`skyLevels = (byte[])data.SectionUniformSkyLevel.Clone()` or copy into a pooled buffer), and read `skyLevels[i]` (the local copy) in the write loop — or document loudly that `WriteChunkInternal` must only ever receive snapshots/main-thread data. Note the queue `lock`s in `WriteChunkInternal` have the same asymmetry: the main-thread enqueue sites (`AddToSkylightQueue` etc.) do not lock, so the reader-side locks only protect snapshot objects that nobody else touches anyway.

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
**Confidence:** High (mechanism **reproduced**, 2026-08-21)  
**Files:** `LightingStateManager.cs` — `AddPending` (lines ~94–118)  
**Repro:** `K08` in `Minecraft Clone/Dev/Validate Serialization Round-Trip` (NS-1 part 5).

The validation loop only `Debug.LogError`s out-of-range local columns; the subsequent add loop inserts **all** columns including invalid ones. On `Save()` they are byte-truncated (`(byte)col.x`), and on `Load()` the truncated values may pass validation and queue skylight recalcs for the wrong columns. Fix: `continue`/skip invalid columns in the add loop (or validate-and-skip in one pass).

**Sharpened by the repro:** the "may pass validation" is not a maybe — it is decided by the truncated value, and both arms are observable. Queueing columns `(259, 4)` and `(272, 5)` on one chunk yields, after a save → load cycle: `(272, 5)` → `(16, 5)`, correctly rejected by `LoadPendingColumns`' bounds check; but `(259, 4)` → **`(3, 4)`, which is in range and is silently queued** — a skylight recalculation for a column the caller never named, indistinguishable on load from a legitimate request. So the failure is not "an invalid column is dropped late", it is "an invalid column becomes a *different valid* column". `AddPendingBlocklight` already gets this right (it `return`s on invalid input); only `AddPending` falls through.

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

## 11. The earliest v1 chunk layout predates `needsLight` and the light queues, so `v2->v3` misreads it

**Severity:** Bug (total data loss, scoped to the oldest v1 worlds)  
**Confidence:** High (observed, not inferred: the shipped step throws on a real payload, and a full migration of a
real world faulted every one of its 6,163 chunks)  
**Files:** `Migration_v2_to_v3_RestoreLighting.cs` - the V1/V2 READ DEFINITION

`MigrationV2ToV3RestoreLighting` branches on the version byte to handle v1's 256-byte heightmap vs v2's 512-byte
one, which is correct for the *common* v1 layout. But it unconditionally reads a `needsLight` boolean after the
coordinates, and two 13-byte-entry light queues after the sections - and **the earliest v1 saves have neither**.

Both fields were added during the world-v1 era without a version bump, so world version 1 covers at least two
incompatible on-disk chunk layouts that are indistinguishable by their version byte:

| Layout | Header | Trailer | Example payload | Compression |
|---|---|---|---|---|
| early v1 (pre-`a951b788`) | no `needsLight` | no light queues | 131,365 B | Deflate |
| common v1 (post-`d0a015d8`) | `needsLight` | two queues | 131,374 B | LZ4 |

Reading a 9-byte header where the payload has 8 shifts everything after it by one byte, so the section bitmask is
read out of the middle of section 0 and the parse runs off the end of the stream.

**Observed:** feeding one real early-v1 chunk to the shipped step throws `EndOfStreamException`; the equivalent
common-v1 chunk migrates cleanly to format 3. Migrating a whole early-v1 world (`New World - orig`, 1,559 region
files) repacks the addresses correctly and then reports **6,163 of 6,163 chunks corrupted**.

**Not the same bug as the archived §10** (`_FIXED_BUGS.md` Serialization 07), and not fixed by it. §10 was the
manager skipping the format chain entirely; that is fixed, and it is what makes this one reachable. The failure is
now loud (per-chunk warnings and the corruption prompt) instead of silent, which is an improvement but not a fix -
the chunks are still lost.

**Blast radius:** unknown but small. Only one such world has been identified so far, and only the first decodable
chunk of eight sampled worlds was parsed, so the survey is not exhaustive. A third variant (queues but no
`needsLight`, from the window between `a951b788` and `d0a015d8`) is possible and unsampled.

**Proposed fix (undecided).** The shipped step cannot be edited to sniff the layout - `AOT_WORLD_MIGRATION_SYSTEM.md`
§6 forbids changing what an already-shipped step produces, and length-sniffing would change its output for inputs it
already handles. The plausible shapes are a new pre-step that normalizes an early-v1 payload into the common v1
shape before the chain runs, or accepting the loss and documenting it. Either way this wants its own decision;
the affected saves are the oldest on disk and are already backed up by the migration itself.

---

## 12. Chunks dropped by a region-layout step never reach the corruption prompt

**Severity:** Minor (under-reporting, no data loss on its own)  
**Confidence:** High (verified by inspection)  
**Files:** `Migration_v1_to_v2_RegionRepack.cs` - `ProcessOldRegionFile`; `MigrationManager.cs` -
`RunAOTMigrationAsync` (layout pass)

`PerformRegionLayoutMigration` returns only a count of chunks it *succeeded* in repacking. Its per-chunk fault
isolation catches a failing chunk, emits a `Debug.LogWarning` and moves on, and nothing propagates that failure
back to the manager. So the layout pass cannot contribute to `corruptedChunksTotal`, which means:

- `onCorruptionDetected` never fires for chunks lost during a layout migration, however many there are;
- the count the player is shown ("N chunk(s) could not be migrated") silently excludes them;
- a world can lose chunks in the layout pass and still report a clean migration.

The per-chunk pass has the opposite behaviour - it counts every failure and drives the prompt - so the two
region passes disagree about what a "corrupted chunk" is worth reporting.

**Not currently reachable in the loud case.** The only shipped layout step is the v1->v2 repack, which merely
decompresses, re-addresses and recompresses; it fails a chunk only on a genuine I/O or compression fault, not on
a format mismatch. The early-v1 layout of §11 fails in the *per-chunk* pass, which does report correctly.

**Proposed fix:** give `PerformRegionLayoutMigration` a way to report failures alongside successes (a tuple or a
small result struct), and fold that count into `corruptedChunksTotal` before the Phase 2 prompt. This changes a
shipped step's *signature*, not its byte output, so `AOT_WORLD_MIGRATION_SYSTEM.md` §6's rule against altering
what a shipped step produces is not in the way - but it does touch every implementer of the abstract method.

**Found by:** a code review of the §10 fix (August 2026).

---

## 13. A wholly-unmigratable region is replaced by an empty region file

**Severity:** Bug (data loss on an explicit user "Continue", recoverable only from the backup)  
**Confidence:** High (mechanism verified; the staged empty shells were observed on disk after a real run)  
**Files:** `MigrationManager.cs` - `MigrateSingleRegion`, `RunAOTMigrationAsync` Phase 3

`MigrateSingleRegion` opens `new RegionFile(tempFile)` unconditionally, and `RegionFile`'s constructor
`SetLength`s a new file to two sectors (8,192 bytes). Phase 3 then swaps in any temp file that exists:

```csharp
if (File.Exists(tempFile)) { File.Delete(oldFile); File.Move(tempFile, oldFile); }
```

So a region file whose chunks **all** fail to migrate is replaced by an empty 8 KB shell, and the payloads -
which were still intact on disk - are gone. Observed concretely while migrating an early-v1 world (§11): 12 temp
files of exactly 8,192 bytes staged against 50.71 MB of live region data. The swap did not run only because the
migration was stopped at the corruption prompt.

**Mitigations already in place, which is why this is filed rather than hot-fixed:**

- The swap only happens after the player explicitly answers **Continue** at the corruption prompt. Answering
  Rollback throws `MigrationAbortedException`, and `WorldSelectMenu`'s `finally` calls `RollbackMigration`,
  which restores the pre-migration world.
- Backups are never removed by the migration system. `_currentBackupPath` is stamped with the source version
  and a UTC timestamp, so every run leaves its own folder; only `RollbackMigration` consumes one. The data is
  therefore recoverable - if the player knows to look.

**Why the obvious fix is wrong.** "Discard the temp file and keep the original when a region yields no chunks"
was tried and **reverts baselines B8, B9 and B10**. Those pin the opposite contract: a misbehaving step must not
leave payloads that a world stamped current will silently accept as migrated. Preserving the original trades
recoverable data loss for *silent semantic corruption* in every case where the un-migrated payload still parses
as the current format - which is the §10 failure mode in reverse. Do not "fix" this by editing B8-B10.

**The real fork (undecided):**

1. **Leave as-is.** The loss needs explicit consent and the backup survives. Cost: the prompt says corrupted
   chunks will be "regenerated", which is a poor description of "your entire world is discarded".
2. **Make a wholly-failed region non-continuable** - treat it as a hard failure that only offers Rollback.
   Satisfies both contracts (nothing is destroyed, nothing un-migrated is left behind), but changes the
   prompt's meaning and needs B8-B10 re-examined, since they answer Continue and expect completion.
3. **Preserve the original and refuse to stamp the world current.** Closest to "no data loss", but `level.dat`
   is stamped before region work begins, so this needs the stamp deferred to the end of a successful run.

Option 2 looks strongest; it wants its own decision and its own prove-red before anything ships.

**Found by:** a code review of the §10 fix (August 2026).

---

## 14. Region filename parsing is culture-sensitive, so a non-ASCII negative sign would silently hide negative-coordinate regions

**Severity:** Latent robustness (would present as silent world data loss — chunks read as never-generated)  
**Confidence:** Mechanism certain; **not reachable on the current runtime** — see the measurement below  
**Files:** `WorldInfoUtility.cs` - `GetWorldInfo`; `Migration_v1_to_v2_RegionRepack.cs` - `ProcessOldRegionFile`; `ChunkStorageManager.cs` - `GetRegion` (writer)

Region files are named `r.{x}.{z}.bin`. The writer interpolates `int` directly and both readers recover the
coordinates with the **culture-sensitive** `int.TryParse(string, out int)` overload:

```csharp
string path = Path.Combine(_saveFolderPath, $"r.{coord.x}.{coord.y}.bin");   // writer
string[] parts = Path.GetFileName(file).Split('.');                          // reader A
if (parts.Length >= 3 && int.TryParse(parts[1], out int rX) && ...)          // culture-sensitive
```

`NumberStyles.Integer` includes `AllowLeadingSign`, and the sign it accepts comes from
`CultureInfo.CurrentCulture.NumberFormat.NegativeSign`. `WS-3` made negative region coordinates reachable, so
`r.-1.-2.bin` is a filename the engine now produces.

**The failure shape, if a runtime ever supplies a non-ASCII negative sign:** a world written on an
ASCII-hyphen machine and opened where `NegativeSign` is U+2212 still *glob-matches* `r.*.*.bin`, but
`int.TryParse("-1")` returns false. `WorldInfoUtility` skips the file as an unrecognized filename and the
migration step logs "Skipping unrecognized filename" — so every chunk in the negative quadrant reads as
never-generated and is silently regenerated from seed, discarding the player's edits there. Round-tripping
within a single culture never exposes it, which is why no existing test sees it.

**Why this is filed rather than fixed (measured 2026-08-22).** All **342** cultures available on the Editor's
Mono runtime were enumerated: **every one** reports `NegativeSign == "-"` (U+002D) and parses `"-1"`
correctly — including `sv-SE`, `fi-FI`, `nb-NO` and `lt-LT`, the locales for which .NET 5+ CoreCLR/ICU is
documented to return U+2212. Unity's Mono BCL does not use that ICU data, so **the bug is not currently
reachable by changing the machine's locale**. A validation scenario for it was deliberately declined: it could
not fail under any reachable input.

**What would make it live — the reason this entry exists:**

- Unity moving the scripting runtime to CoreCLR (announced direction), which brings ICU culture data with it.
- A culture source not covered by `CultureInfo.GetCultures(CultureTypes.AllCultures)` on this runtime.
- **IL2CPP player builds — unverified.** The sweep ran in the Editor. IL2CPP uses the same Mono class
  libraries so the result is expected to carry over, but this has not been measured.

**Fix when it becomes live (or pre-emptively, it is nearly free):** pass `CultureInfo.InvariantCulture` at both
parse sites and format with it at the writer. The on-disk format does not change — today's filenames are
already invariant-identical — so this needs no save-version bump and no migration step.

**Found by:** a code review of the NS-5 `G3` region-filename pins (August 2026); the ICU premise was checked
against the runtime and did not reproduce, and the entry was kept on the user's call that the latent risk is
worth recording.
