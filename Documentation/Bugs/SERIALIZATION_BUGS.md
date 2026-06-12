# Known Serialization & Storage related bugs

This document outlines **open** bugs related to saving, loading, Region files, and Mod Manager. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** June 2026 (full codebase audit)

---

## 01. Region File Thread Safety adds massive overhead

**Severity:** Performance / Concurrency  
**Files:** `RegionFile.cs`

The `_fileLock` works correctly to prevent save data corruption but adds massive overhead.

**Status:** Needs careful architectural changes to split read and write concurrency. See `Documentation/Design/REGION_FILE_CONCURRENCY.md` for a full breakdown of requirements before addressing this.

---

## 02. Mod Manager depends on Block Database Initialization

**Severity:** Architecture  
**Files:** `ModificationManager.cs`

`RestoreChunkModifications` relies on `World.Instance.blockDatabase` being loaded before data is restored.  
**Impact:** Tight coupling and order-of-initialization dependency.

---

> Bug 03 (NativeCompressions LZ4Stream asymmetric format hang, June 2026) has been fixed and
> archived to [`_FIXED_BUGS.md`](./_FIXED_BUGS.md). The 0.6.0 version pin and library follow-ups
> remain documented in [`LIBRARY_BUGS.md`](./LIBRARY_BUGS.md).

---

## 04. Fixed 256 KB serialization buffer can overflow on dense chunks with large pending light queues

**Severity:** Bug (silent data loss)
**Confidence:** High (mechanism verified by code inspection; likelihood in normal play is low–medium)
**Files:** `SerializationBufferPool.cs` (BUFFER_SIZE), `ChunkSerializer.cs` — `Serialize`, `WriteChunkInternal`, `WriteLightQueue`, `ChunkStorageManager.cs` — `SaveChunk` / `SaveChunkAsync`

`ChunkSerializer.Serialize` writes into a **non-expandable** `MemoryStream(outputBuffer)` over a pooled fixed 256 KB buffer. The worst-case uncompressed chunk payload is ~197 KB (8 sections × flag 0x01 = voxels 16 KB + LightData 8 KB each, plus header/heightmap/bitmask), leaving only ~65 KB of headroom. The pending BFS light queues are serialized **without any count cap** at 16 bytes per node — roughly **4,000 queued nodes across both queues exhaust the buffer**. When that happens, `MemoryStream` throws `NotSupportedException`, the exception is caught and logged in `SaveChunk`/`SaveChunkAsync`, and the chunk is **silently not saved** (reverts to its last saved state, or regenerates, on next load).

Most realistic trigger: chunks at the edge of the load area accumulate queue entries via `ModifyVoxel` (each edit enqueues ~7 nodes) while their lighting job can't run (`AreNeighborsDataReady` false), then an autosave fires. `CompressionAlgorithm.None` removes the compression safety margin entirely.

**Related asymmetry:** `WriteLightQueue` writes unbounded counts, but `ReadLightQueue` throws `InvalidDataException` for counts > 100,000 — a chunk saved with a queue between the buffer limit and 100k can never exist, but the bounds should match whatever cap is chosen.

**Proposed fix:** Cap (or drop-and-flag) the serialized light queues — e.g. clamp to a few thousand nodes and set `NeedsInitialLighting`/column-recalc flags instead — and/or grow the buffer / use an expandable stream with pooled segments. Not save-format-breaking if the cap is write-side only.

---

## 05. `WriteChunkInternal` "snapshot" of `SectionUniformSkyLevel` is a reference copy, not a value copy

**Severity:** Latent race condition (currently unreachable in normal flow)
**Confidence:** Medium (race window verified, but current call patterns avoid it)
**Files:** `ChunkSerializer.cs` — `WriteChunkInternal` (line ~135), `ChunkStorageManager.cs` — `SaveChunk`, `WorldJobManager.cs` — `TryCompactSectionLight`

`WriteChunkInternal` contains `byte[] skyLevels = data.SectionUniformSkyLevel;` with a comment claiming a *"value copy is safe for the background thread"* — but this copies the **array reference**, not the values. If the main thread mutates `SectionUniformSkyLevel` (e.g. `TryCompactSectionLight` after a lighting job, or `PromoteCompactSection` on a block edit) while a background thread serializes the **live** `ChunkData`, the bitmask phase and the section-write phase can observe different values. Worst case: a slot is included in the bitmask as a compact light-only section (`safeSections[i] == null`, sky set), then the sky level flips to `UNIFORM_SKY_NONE` before the write loop → **neither branch writes anything** → all subsequent sections shift → corrupt chunk payload (caught on load → chunk regenerates).

**Why it doesn't currently fire:** `SaveChunkAsync` serializes an isolated snapshot created on the main thread (`CreateSerializationSnapshot`), and the synchronous `SaveChunk` path is only called from the main thread. The bug becomes live the moment anyone passes a **live** `ChunkData` to a background `Serialize` call.

**Proposed fix:** Actually copy the array (`skyLevels = (byte[])data.SectionUniformSkyLevel.Clone()` or copy into a pooled buffer), and read `skyLevels[i]` (the local copy) in the write loop — or document loudly that `WriteChunkInternal` must only ever receive snapshots/main-thread data. Note the queue `lock`s in `WriteChunkInternal` have the same asymmetry: the main-thread enqueue sites (`AddToSunLightQueue` etc.) do not lock, so the reader-side locks only protect snapshot objects that nobody else touches anyway.

---

## 06. Deserialization failure leaks pooled objects

**Severity:** Minor (pool churn, no crash)
**Confidence:** High
**Files:** `ChunkSerializer.cs` — `ReadChunkInternal`, `ReadSectionWithFlag`

When `ReadChunkInternal` throws mid-read (corrupt/truncated chunk), the `ChunkData` obtained from `World.Instance.ChunkPool.GetChunkData(...)` and all **sections already read into it** are abandoned — `Deserialize` catches the exception and returns null without returning them to the pool. The per-section `try/catch` in `ReadSectionWithFlag` only returns the section currently being read. The objects are GC-reclaimed, so this is pool-efficiency churn rather than a leak, but on a save with many corrupt chunks it defeats the pooling.

**Proposed fix:** Wrap the body in a `try/catch` that calls `chunk.Reset(...)` + `ReturnChunkData(chunk)` before rethrowing.

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
