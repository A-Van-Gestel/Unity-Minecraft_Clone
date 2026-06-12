# Known Job System related bugs

This document outlines **open** bugs related to the Unity Job System integration for chunk generation, meshing, and lighting. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** June 2026 (full codebase audit)

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

## 02. `ChunkJobArrayPool` worst-case retention is ~96 MB (documented trade-off, monitor on mobile)

**Severity:** Improvement (memory) — by-design behavior worth tracking
**Confidence:** High (arithmetic from class constants)
**Files:** `Helpers/ChunkJobArrayPool.cs` (`MAX_RETAINED_PER_TYPE = 512`)

The pool retains up to 512 buffers per element type: 512 × 128 KB (uint voxel maps) + 512 × 64 KB (ushort light maps) ≈ **96 MB** of Persistent native memory in the worst case. The pool only ever retains the actual concurrent-rental peak (typically far lower), and the cap exists precisely to absorb the `maxLightJobsPerFrame` (32) × 18-buffer bursts — but on Android (initial support recently merged) a backlog spike during fast movement could pin a meaningful fraction of this until world teardown. If profiling shows it matters, consider a soft-trim (
dispose down to N when idle) or a platform-dependent cap.
