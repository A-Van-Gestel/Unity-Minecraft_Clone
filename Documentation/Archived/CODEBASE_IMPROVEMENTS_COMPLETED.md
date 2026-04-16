# Codebase Improvements — Completed

> Archived: April 2026
> These items were completed as part of the ongoing codebase modernization tracked in the original `CODEBASE_IMPROVEMENTS.md` analysis. Kept here as a historical record of what was changed and why.

---

## 1.1 Legacy Input Manager → Unity Input System  `[DONE]`

**What:** Migrated from `UnityEngine.Input` (`Input.GetAxis`, `Input.GetKeyDown`, `Input.GetMouseButtonDown`) to the Unity Input System package with event-driven `InputAction` handling via a dedicated `InputManager.cs` wrapper.

**Files changed:** `Player.cs`, `PlayerInteraction.cs`, `Toolbar.cs`, `DragAndDropHandler.cs`, benchmark scripts.

**Why it mattered:** Event-driven model eliminated per-frame polling, added rebinding support, and future-proofed against Unity deprecating the legacy Input Manager.

---

## 2.1 `.material` / `.mesh` Implicit Cloning  `[DONE]`

**What:** `SectionRenderer.cs` was migrated to the Advanced Mesh API (`SetVertexBufferParams`, `SetVertexBufferData`, `SetIndexBufferParams`, `SetSubMeshes`) — no implicit cloning. `ChunkLoadAnimation.cs` no longer references `.mesh` or `.sharedMesh` at all (only manipulates `transform.position`).

**Files changed:** `SectionRenderer.cs`, `ChunkLoadAnimation.cs`.

**Why it mattered:** Eliminated hidden memory allocations on every pool activation and cloud tile creation, reducing GC pressure.

---

## 2.4 Runtime `AddComponent` in Pooling  `[MITIGATED]`

**What:** `Chunk.cs` constructor calls `AddComponent<ChunkLoadAnimation>()` once per pool slot creation and caches the result in `_loadAnimation`. The component is not re-added on every pool activation — it's enabled/disabled instead.

**Files changed:** `Chunk.cs`.

**Why it mattered:** Reduced from one `AddComponent` per activation to one per pool slot lifetime. Residual improvement would be pre-attaching via prefab, but the current pattern is acceptable.

---

## 3.3 LINQ in Startup Hot Loop  `[DONE]`

**What:** Removed `.Any()` calls from the startup lighting coroutine loop condition. The only remaining `.Count(predicate)` usage is inside a `Debug.LogError` on a safety-break error path (fires once on failure, not in the hot loop).

**Files changed:** `World.cs`.

**Why it mattered:** Eliminated per-iteration enumerator allocations during world startup, reducing GC pauses.

---

## Architectural Strengths (No Action Required)

These areas were already well-implemented at the time of analysis. Documented for completeness.

- **`ChunkCoord` — Optimal Struct Design:** `readonly struct` with `IEquatable<ChunkCoord>` and `HashCode.Combine(X, Z)`. Eliminates boxing in `Dictionary`/`HashSet` operations.
- **`HashSet<ChunkCoord>` for Spatial Lookups:** `_activeChunks`, `_chunksToBuildMeshSet`, `_currentViewChunks` all use `HashSet` for O(1) membership. The parallel `_chunksToBuildMeshSet` guard prevents duplicate insertions without scanning the list.
- **`UnityEngine.Pool` Usage:** `ListPool<T>.Get()` / `.Release()` and `HashSetPool<T>` used throughout tick-processing and lighting code for zero-allocation temporary collections.
- **Native Memory Lifecycle Management:** Job data structs properly manage `NativeArray`/`NativeList` lifetimes with explicit `Dispose()` and appropriate allocator choices.
