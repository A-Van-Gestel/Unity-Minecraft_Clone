# Known Chunk Management related bugs

This document outlines known bugs and major improvements related to chunk loading, unloading, pooling, and lifecycle management.


## 01. `ChunkCoord` integer division truncates negative coordinates incorrectly

**Severity:** Bug  
**Files:** `Chunk.cs` — `ChunkCoord(Vector2Int)` (lines 445–449), `ChunkCoord(Vector3Int)` (lines 457–461)

The `Vector2Int` and `Vector3Int` constructors perform raw integer division (`pos.x / VoxelData.ChunkWidth`) without `FloorToInt`. In C#, integer division truncates toward zero, so for negative coordinates (e.g., `pos.x = -1`), the result is `0` instead of the expected `-1`. This means voxels at negative positions map to the wrong chunk.

While the world is currently constrained to positive coordinates (`0` to `WorldSizeInChunks`), this is a latent bug that would surface if the world ever allows negative coordinates. The `Vector3` and `Vector2` constructors correctly use `Mathf.FloorToInt`.


## 02. `ChunkCoord.GetHashCode()` has high collision potential

**Severity:** Improvement  
**Files:** `Chunk.cs` — `ChunkCoord.GetHashCode` (lines 481–485)

The hash function `31 * X + 17 * Z` is a simple linear combination. For chunks in typical ranges (0–100), this produces values from 0 to roughly 4800, which is a very small hash space. More critically, swapping adjacent coordinates can produce hash overlaps (e.g., `(2, 3)` = 113, `(3, 2)` = 127 — these happen to not collide, but patterns like `(0, 1) = 17` and `(17, 0) = 527` vs `(1, 0) = 31` shows the distribution isn't great for HashSet/Dictionary bucket distribution).

A standard approach like `HashCode.Combine(X, Z)` or a better mixing function would provide significantly fewer collisions for dictionary lookups.


## 03. `Tick()` iterates `_activeChunks` which can be modified concurrently

**Severity:** Bug  
**Files:** `World.cs` — `Tick` (lines 861–876)

The `Tick` coroutine iterates over `_activeChunks` using a `foreach` loop. If `CheckViewDistance` (called from `Update`) modifies `_activeChunks` (via `Clear()` and `UnionWith()`) between the `foreach` iteration resuming after its `WaitForSeconds`, this could cause an `InvalidOperationException` ("Collection was modified during enumeration").

Since the tick coroutine runs on the main thread and `yield return new WaitForSeconds(...)` resumes between frames (during `Update`), there is a window where the collection can be modified before the `foreach` resumes.


## 04. `_chunksToBuildMesh` uses `List.Remove()` which is O(n)

**Severity:** Improvement  
**Files:** `World.cs` — `CheckViewDistance` (lines 1799–1802), `UnloadChunks` (line 1693)

When deactivating or unloading a chunk, the code calls `_chunksToBuildMesh.Remove(chunk)`, which is an O(n) linear scan on a `List<Chunk>`. While the `HashSet` (`_chunksToBuildMeshSet`) provides O(1) duplicate detection, the actual removal from the ordered list is still slow. With many chunks pending rebuild, this could become a performance bottleneck during rapid player movement.


## 05. Chunk load animation adds component on every activation

**Severity:** Improvement  
**Files:** `Chunk.cs` — `PlayChunkLoadAnimation` (lines 417–421)

The `PlayChunkLoadAnimation` method uses `GetComponent<ChunkLoadAnimation>() == null` before `AddComponent<ChunkLoadAnimation>()`. While the null check prevents duplicates, `GetComponent` itself is not free — it performs a component search every time. For chunks that are frequently activated/deactivated (pool cycling), this runs on every activation. A cached boolean flag would avoid the component lookup overhead.


## 06. `PrepareJobData` is called even when Singleton is being destroyed

**Severity:** Bug  
**Files:** `World.cs` — `Awake` (lines 155–177)

If a duplicate `World` component exists (which the Singleton pattern handles by destroying the duplicate), `PrepareJobData()` on line 176 is called *outside* the else branch, meaning it runs for both the surviving and the soon-to-be-destroyed instances. This wastes resources allocating `NativeArray` data that is never disposed, causing a native memory leak.

```csharp
// Current code layout (simplified):
if (Instance is not null && Instance != this)
{
    Destroy(gameObject);
}
else
{
    Instance = this;
    // ... initialization ...
}

// BUG: This line runs for BOTH branches
PrepareJobData();
```


## 07. Unreachable diagnostic check in `UnloadChunks`

**Severity:** Code Quality  
**Files:** `World.cs` — `UnloadChunks` (lines 1629–1634)

The diagnostic check at line 1631 (`if (data.IsAwaitingMainThreadProcess)`) is unreachable because the same condition was already checked on line 1620 and causes a `continue` if true. This dead code should either be removed or the logic restructured if it was meant to catch a specific edge case.
