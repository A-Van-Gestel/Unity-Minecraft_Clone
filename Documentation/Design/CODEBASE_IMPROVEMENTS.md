# Codebase Improvement Backlog

> Open improvement items for the VoxelEngine codebase. Each finding includes affected files, a recommendation, and a brief **Impact Analysis**.
> Completed items have been archived to `Documentation/Archived/CODEBASE_IMPROVEMENTS_COMPLETED.md`.

**Last audited:** April 2026

---

## 1. Unity API Modernization

### 1.2 `GameObject.Find` Runtime Lookups  `[Improvement]`

`GameObject.Find(string)` performs an **O(n)** scan of all active objects using string comparison every time it is called.

| Affected Files          | Example Call                                     |
|-------------------------|--------------------------------------------------|
| `UIItemSlot.cs`         | `GameObject.Find("World").GetComponent<World>()` |
| `CreativeInventory.cs`  | `GameObject.Find("World").GetComponent<World>()` |
| `DragAndDropHandler.cs` | `GameObject.Find("CreativeInventory")`           |
| `PlayerInteraction.cs`  | `GameObject.Find("HighlightBlocks")`             |

**Recommendation:** Replace all `GameObject.Find` calls with Inspector-serialized fields (`[SerializeField]`) or use the existing `World.Instance` singleton directly.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — straightforward field wiring or singleton substitution.
> - **Risk:** 🟢 Low — change is isolated per-class, easy to test.
> - **Benefit:** 🟢 High — eliminates a hidden O(n) scan on every `Awake/Start` call and makes dependencies explicit.

---

### 1.3 `Camera.main` Usage  `[Improvement]`

`Camera.main` is cached internally since Unity 2020.1 and is no longer a performance problem. However, relying on it introduces an implicit dependency on `MainCamera` tags.

| Affected Files                                                    |
|-------------------------------------------------------------------|
| `World.cs`, `Player.cs`, `PlayerInteraction.cs`, `DebugScreen.cs` |

**Recommendation:** Inject or serialize the camera reference for explicit control.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — add a `[SerializeField]` field and assign in Inspector.
> - **Risk:** 🟢 Low — purely architectural cleanup.
> - **Benefit:** 🟡 Medium — cleaner dependency graph; avoids accidental tag mismatches.

---

## 2. Unity API Performance

### 2.2 `Clouds.cs` — Legacy Mesh API with `.ToArray()`  `[Performance]`

Cloud mesh generation builds lists of vertices, triangles, and normals, then copies them into managed arrays via `.ToArray()` before assigning them to the mesh.

```csharp
mesh.vertices = vertices.ToArray();
mesh.triangles = triangles.ToArray();
mesh.normals  = normals.ToArray();
```

| Affected File                          |
|----------------------------------------|
| `Clouds.cs` (lines ~194-196, ~250-252) |

**Recommendation:** Use the **NativeArray Mesh API** (`Mesh.SetVertexBufferData`, `Mesh.SetIndexBufferData`) or at minimum `mesh.SetVertices(list)` / `mesh.SetTriangles(list, 0)` which accept `List<T>` directly — avoiding the `.ToArray()` heap allocation entirely.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — API is a direct substitution.
> - **Risk:** 🟢 Low — cloud meshes are visually simple.
> - **Benefit:** 🟡 Medium — eliminates 3 temporary array allocations per cloud tile creation, reducing GC spikes during chunk loading.

---

### 2.3 `UnityEngine.Random` → `Unity.Mathematics.Random`  `[Performance]`

`UnityEngine.Random` is globally locked and not Burst-compatible. The codebase uses it in voxel behavior logic (`BlockBehavior.cs`) which runs every tick.

| Affected Files                        | Context                                  |
|---------------------------------------|------------------------------------------|
| `BlockBehavior.cs`                    | Grass spreading (hot tick path)          |
| `ChunkLoadAnimation.cs`, `Toolbar.cs` | Initialization (cold path, low priority) |

**Recommendation:** Use `Unity.Mathematics.Random` in `BlockBehavior.cs`. It is deterministic, thread-safe, and Burst-compilable. Use `UnityEngine.Random` only in MonoBehaviour initialization code where convenience matters more.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — replace calls and seed per-chunk or per-tick.
> - **Risk:** 🟢 Low — behavioral results will differ slightly due to different RNG sequence, but this is cosmetic (grass spread patterns).
> - **Benefit:** 🟡 Medium — removes global lock contention in tick processing; enables future Burst compilation of block behavior logic.

---

## 3. C# Data Structure & Algorithm Improvements

### 3.1 `List.Insert(0)` and `List.RemoveAt(i)` — O(n) Mesh Queue  `[Performance]`

The meshing pipeline uses a `List<Chunk>` as a priority queue. Inserting at index 0 and removing from the middle are both **O(n)** operations because they require shifting all subsequent elements in memory.

```csharp
_chunksToBuildMesh.Insert(0, chunk); // O(n) shift
_chunksToBuildMesh.RemoveAt(i);      // O(n) shift
```

| Affected File                          |
|----------------------------------------|
| `World.cs` (lines ~1022, ~1033, ~1607) |

**Recommendation:** Replace with a `LinkedList<T>` (O(1) insert/remove at ends) or a `PriorityQueue<T, TPriority>` (.NET 6+) for distance-based sorting with **O(log n)** operations.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — the iteration and indexing patterns around the list must be adapted.
> - **Risk:** 🟡 Medium — meshing order directly affects visual pop-in behavior, requiring careful testing.
> - **Benefit:** 🟢 High — with hundreds of chunks loaded, O(n) shifts on every insertion add up. A proper queue structure eliminates this entirely.

---

### 3.2 `List<VoxelMeshData>.Contains` / `.IndexOf` — O(n) Lookup  `[Performance]`

During startup serialization (`PrepareJobData`), unique custom meshes are collected into a `List`, then searched with `.Contains()` and `.IndexOf()` — both O(n).

```csharp
if (!uniqueCustomMeshes.Contains(blockType.meshData))
    uniqueCustomMeshes.Add(blockType.meshData);
// ...later:
customMeshIndex = uniqueCustomMeshes.IndexOf(blockType.meshData);
```

| Affected File                   |
|---------------------------------|
| `World.cs` (lines ~1094, ~1150) |

**Recommendation:** Use a `Dictionary<VoxelMeshData, int>` to map each mesh to its index. Both insertion and lookup become **O(1)**.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — change list to dictionary, minor API adjustment.
> - **Risk:** 🟢 Low — this code runs once at startup.
> - **Benefit:** 🟢 Low — negligible with small block counts, but scales cleanly if the block database grows significantly.

---

## 4. C# Allocation & GC Improvements

### 4.1 String Allocation in Chunk Pool Reset  `[Performance]`

Every time a chunk is pulled from the pool, it is renamed with string interpolation:

```csharp
ChunkGameObject.name = $"Chunk {Coord.X}, {Coord.Z}";
```

This allocates a C# string on the managed heap **and** a native C++ string inside Unity's engine layer — on every pool activation.

| Affected Files                                                                    |
|-----------------------------------------------------------------------------------|
| `Chunk.cs` (line ~67), `ChunkPoolManager.cs` (line ~236), `Clouds.cs` (line ~281) |

**Recommendation:** Guard renaming behind `#if UNITY_EDITOR` preprocessor directives so it only runs in the Editor (for debugging) and is completely stripped from production builds.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — wrap existing line in `#if`.
> - **Risk:** 🟢 Low — names are only used for hierarchy readability.
> - **Benefit:** 🟡 Medium — eliminates 2 allocations per chunk activation, which happens constantly during player movement.

---

### 4.2 `.ToArray()` Intermediate Allocations  `[Performance]`

Several places build data with `List<T>` and then call `.ToArray()` to pass it to a `NativeArray` constructor, creating a temporary managed array that is immediately discarded.

```csharp
var customMeshesJobData = new NativeArray<CustomMeshData>(customMeshesList.ToArray(), Allocator.Persistent);
```

| Affected File                 |
|-------------------------------|
| `World.cs` (lines ~1185-1188) |

**Recommendation:** Use `NativeArray.CopyFrom(list)` or build directly into a `NativeList<T>` and convert via `.AsArray()`.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — API substitution.
> - **Risk:** 🟢 Low — startup-only code path.
> - **Benefit:** 🟢 Low — saves 4 temporary array allocations at startup. Minor but clean.

---

## 5. Future Architectural Scalability

### 6.1 `BlockBehavior.cs` Data Separation (ECS / DOTS Pattern)  `[Architecture]`

Currently, `World.activeVoxels` handles all ticking blocks (fluids, grass spreading, etc.) in a single monolithic `NativeHashMap<Vector3Int, VoxelMod>`, evaluating logic per-block via a central `switch` statement in `BlockBehavior.cs`. As the number of behavior types grows, this forces a monolithic tick loop that iterates over unrelated voxel types.

**Recommendation:** Separate active voxels by type into dedicated collections (e.g., `NativeList<Vector3Int> _activeFluids`, `NativeList<Vector3Int> _activeGrass`). This allows each behavior to run in its own independent Burst-compilable IJob, dramatically improving cache locality, parallelization, and reducing monolithic class bloat. Follows the Data-Oriented Design (DoD) philosophy.

> **Impact Analysis:**
> - **Effort:** 🔴 High — requires splitting the unified `_activeVoxels` map into separate collections and re-architecting the `TickUpdate` pump.
> - **Risk:** 🔴 High — touches the core world ticking engine.
> - **Benefit:** 🟢 High — scales horizontally across CPU cores. Ideal for adding many complex behaviors later (e.g., fire spread, leaf decay) with zero main-thread overhead.

---

### 6.2 `BlockBehavior.cs` Function Pointers  `[Architecture]`

If Data Separation (6.1) is overkill, the `switch` statement in `BlockBehavior` could be replaced by an unmanaged function pointer registry (`Unity.Burst.FunctionPointer<T>`) indexing into an array of Burst-compiled function pointers.

**Recommendation:** Define a standard delegate pattern, compile each behavior method via `BurstCompiler.CompileFunctionPointer`, and execute via dynamic dispatch based on voxel ID.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — requires initializing unmanaged pointers during Burst startup.
> - **Risk:** 🟡 Medium — mismanaged function pointers in Burst cause hard crashes.
> - **Benefit:** 🟡 Medium — decouples logic, removing the large `switch` statement, but maintains a single serialized active voxel collection.
