# Codebase Improvements — Completed

> Archived: April 2026
> These items were completed as part of the ongoing codebase modernization tracked in the original `CODEBASE_IMPROVEMENTS.md` analysis. Kept here as a historical record of what was changed and why.

---

## 1.1 Legacy Input Manager → Unity Input System  `[DONE]`

**What:** Migrated from `UnityEngine.Input` (`Input.GetAxis`, `Input.GetKeyDown`, `Input.GetMouseButtonDown`) to the Unity Input System package with event-driven `InputAction` handling via a dedicated `InputManager.cs` wrapper.

**Files changed:** `Player.cs`, `PlayerInteraction.cs`, `Toolbar.cs`, `DragAndDropHandler.cs`, benchmark scripts.

**Why it mattered:** Event-driven model eliminated per-frame polling, added rebinding support, and future-proofed against Unity deprecating the legacy Input Manager.

---

## 1.4 Shader `#pragma target` sweep to the 3.5 floor  `[DONE]`

**What:** Raised every project-owned shader to an explicit `#pragma target 3.5`. Three declared `2.0`
(`SkyboxShader`, `StandardBlockShader`, `TransparentBlockShader`) and seven declared nothing at all —
which is not neutral, since Unity defaults to `2.5` and its 8-interpolator budget. The two liquid shaders
were already at 3.5, moved there when RF-3's emissive read pushed `LiquidV2F` to 11 interpolators against
a declared `target 3.0`.

**Files changed:** `BorderWallShader`, `CloudShader`, `DebugVoxelShader`, `MaskedUIBlur`, `UIBlurBlit`,
`SkyboxShader`, `StandardBlockShader`, `TransparentBlockShader`, `Editor/BlockPreviewShader`,
`Editor/ChunkPreviewShader`.

**Why it mattered:** A uniformity change, not a correctness one — none of the ten needed more than 3.5
(`VoxelV2F` uses 4 interpolators). One number across the fleet removes per-shader capability guesswork and
keeps each declaration honest about what its shader uses. Note the overflow that prompted this was **not**
a reproducible failure: `target 3.0` with 11 interpolators compiled clean on desktop D3D11 *and* on the
Android target (Vulkan + OpenGLES3) with zero shader messages, so the original review finding's
"platform-conditional compile failure" was overstated. The cost is dropping DX11 feature level 9, which
this project does not target.
The rule itself lives in [`../Guides/SHADER_CONVENTIONS.md`](../Guides/SHADER_CONVENTIONS.md) §1 and
outlives this entry.

**Verified:** all ten reimported with `isSupported=true` and zero shader messages; `Validate All`
477/477 baselines across 21 suites, including Sky Render 7/7 and Meshing 57/57 (the rendered-pixel and
mesh-output guards over the shaders touched).

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

## 4.1 String Allocation in Chunk Pool Reset  `[DONE]`

**What:** GameObject renaming on pool activation (`$"Chunk {X}, {Z}"` / section names) is now wrapped in `#if UNITY_EDITOR` so it only runs in the Editor for hierarchy readability and is fully stripped from builds.

**Files changed:** `Chunk.cs`, `SectionRenderer.cs`, `ChunkPoolManager.cs`, `Clouds.cs`.

**Why it mattered:** Eliminated a managed string allocation plus a native engine-side string update on every chunk pool activation — a constant GC source during player movement. *(Verified implemented during the June 2026 performance audit; see `Design/PERFORMANCE_IMPROVEMENTS_REPORT.md`.)*

---

## Architectural Strengths (No Action Required)

These areas were already well-implemented at the time of analysis. Documented for completeness.

- **`ChunkCoord` — Optimal Struct Design:** `readonly struct` with `IEquatable<ChunkCoord>` and `HashCode.Combine(X, Z)`. Eliminates boxing in `Dictionary`/`HashSet` operations.
- **`HashSet<ChunkCoord>` for Spatial Lookups:** `_activeChunks`, `_chunksToBuildMeshSet`, `_currentViewChunks` all use `HashSet` for O(1) membership. The parallel `_chunksToBuildMeshSet` guard prevents duplicate insertions without scanning the list.
- **`UnityEngine.Pool` Usage:** `ListPool<T>.Get()` / `.Release()` and `HashSetPool<T>` used throughout tick-processing and lighting code for zero-allocation temporary collections.
- **Native Memory Lifecycle Management:** Job data structs properly manage `NativeArray`/`NativeList` lifetimes with explicit `Dispose()` and appropriate allocator choices.
