# Known Job System related bugs

This document outlines **open** bugs related to the Unity Job System integration for chunk generation, meshing, and lighting. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** August 2026 (documentation audit — every code-checkable claim re-verified against current source)

---

## 01. Pooled-buffer release lists must stay manually in sync with job-data structs

**Severity:** Latent leak risk (technical debt — no current leak)  
**Confidence:** High (structural risk; verified all current fields are covered)  
**Files:** `WorldJobManager.cs` — `ReleaseLightingJobData`, `ReleaseMeshingJobInputs`; `Jobs/Data/LightingJobData.cs`; `Jobs/Data/MeshingJobData.cs`

The June 2026 buffer-pooling refactor replaced `LightingJobData.Dispose()` / the meshing `Dispose(JobHandle)` chain with hand-written release methods in `WorldJobManager` that enumerate **every field individually** (18 pooled buffers + per-job containers each). `LightingJobData.Dispose()` and `MeshingJobData.Dispose()` still exist for the non-pooled paths (startup TempJob, editor pipeline, benchmarks), so each struct now has **two parallel cleanup lists** that must be kept in sync by hand. Adding a NativeContainer field to either struct and updating only
`Dispose()` produces a per-job native memory leak in the steady-state gameplay path — the worst possible place — with no compiler diagnostic.

As of this audit, all fields are covered in both paths. **Rule:** when adding a field to `LightingJobData`/`LightingJobInputData`/`MeshingJobData`, update `Dispose()` AND the matching `Release*` method in the same commit.

**Minor doc nit (same refactor):** the XML docs on the `ScheduleLightingUpdate(Chunk, ...)` overload claim *"the full-volume maps are always pooled Persistent buffers"* — they are pooled only when `allocator == Allocator.Persistent`; the startup TempJob path allocates per-job. The `ScheduleLightingUpdate(ChunkData, ...)` doc says this correctly.

---

## 02. `ChunkJobArrayPool` cap-limited retention is ~246 MB across four buffer types (documented trade-off)

**Severity:** Improvement (memory) — by-design behavior worth tracking  
**Confidence:** High (arithmetic from class constants)  
**Files:** `Helpers/ChunkJobArrayPool.cs` (`DefaultMaxRetainedPerType = 512`, `Settings.chunkJobArrayPoolRetention`)

The pool retains up to `_maxRetainedPerType` buffers **per element type**, across four types:

| Stack | Element | Length | Per buffer | × 512 |
|---|---|---|---|---|
| `_voxelMaps` | `uint` | 32,768 (16×128×16) | 128 KB | 64 MB |
| `_lightMaps` | `ushort` | 32,768 | 64 KB | 32 MB |
| `_paddedVoxels` | `uint` | 51,200 (20×128×20) | 200 KB | 100 MB |
| `_paddedLight` | `ushort` | 51,200 | 100 KB | 50 MB |

giving a **cap-limited** ceiling of ≈ **246 MB** of Persistent native memory.

**Demand-limited retention is much lower, and the distinction is the point.** The pool never retains more than the actual concurrent-rental peak for each type, and that peak is *not* uniform across the four:

- The full-chunk maps are rented **9 per job** (centre + 8 neighbours), so at max job settings they peak at ≈468 each — `(32 lighting + 20 mesh) × 9` — which is what the 512 cap was sized just above, and what makes their combined ≈88 MB reachable in practice.
- The **padded** buffers are rented **one of each per lighting job**, so they are bounded by lighting jobs in flight (tens, not hundreds) — on the order of 10 MB, nowhere near their 150 MB share of the cap.

Steady-state retention is therefore ≈96 MB; only a pathological rental pattern approaches the 246 MB the cap permits.

**A platform-dependent cap already exists:** **OM-1** device calibration derives `Settings.chunkJobArrayPoolRetention` as `min(512, f(systemMemorySize))`, so low-RAM devices (including Android) get a proportionally smaller cap and a high-RAM desktop resolves to 512. The **soft-trim** (dispose down to N when idle) does not: retention is monotonic within a session, and the cap is captured once at `ChunkJobArrayPool` construction, so a changed retention only applies on the next world load.

**Worth revisiting if the padded stacks ever grow a high-concurrency consumer** — their per-buffer cost is the largest of the four, and they are currently protected only by low demand, not by a tighter cap.
