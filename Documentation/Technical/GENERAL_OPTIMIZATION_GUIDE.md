# General Optimization & Architecture Guide

This document outlines optimization strategies, common pitfalls, and architectural best practices for the project. It focuses on high-level C# logic, data structures, and Unity lifecycle management (outside of the Burst/Job system).

## Table of Contents

1. [Case Study: Fixing Exponential Startup Time](#1-case-study-fixing-exponential-startup-time)
2. [Voxel-Specific Optimizations](#2-voxel-specific-optimizations)
3. [General Algorithmic Guidelines](#3-general-algorithmic-guidelines)
4. [Data Structures & Collections](#4-data-structures--collections)
5. [Unity & C# Specifics](#5-unity--c-specifics)
6. [Profiling Strategy](#6-profiling-strategy)

---

## 1. Case Study: Fixing Exponential Startup Time

In early versions, increasing the `View Distance` caused the initial world generation time to grow **exponentially** rather than linearly. Analyzing and fixing this provided two key lessons in engine architecture.

### Pitfall A: Lifecycle Interference (The "Race Condition")

**The Symptom:**
Setting a high `View Distance` (e.g., 25) caused the startup coroutine to hang for ~55 seconds, processing over 3000 chunks, even though the `MaxInitialLoadRadius` was clamped to 10 (approx. 441 chunks).

**The Cause:**
1.  `Start()` initiated a coroutine to generate the initial 441 chunks. **Crucially, `Start()` finishes immediately after starting the coroutine.**
2.  The coroutine yielded (`yield return null`) to process jobs.
3.  The next frame, Unity called `Update()`.
4.  `Update()` detected the player's position "changed" (initialized) and ran `CheckViewDistance()`.
5.  `CheckViewDistance()` ignored the startup clamp and immediately queued generation for the full radius (3000+ chunks).
6.  The startup coroutine, designed to "finish all pending jobs," was forced to process this massive influx of chunks synchronously.

**The Fix:**
We introduced a state flag `_isWorldLoaded`. The `Update()` loop is strictly blocked from running generation logic until the `Start()` coroutine explicitly sets this flag to true.

**Key Lesson:**
**Initialization logic running in Coroutines or Async tasks runs concurrently with the game loop.** Standard frame logic (`Update`, `FixedUpdate`) *will* interleave.
*   **Good:** Use boolean flags to block logic.
*   **Better:** Use a State Machine (e.g., `enum GameState { Initializing, Playing }`) to explicitly manage what logic runs during which phase.

### Pitfall B: Nested Iteration (The O(N*M) Problem)

**The Symptom:**
The "Lighting Scheduling" phase took **41,000ms** (41 seconds) during the bad benchmark. Even after fixing the chunk count, it remained a bottleneck.

**The Cause:**
The system used a flat `HashSet<Vector2Int>` to store columns needing sunlight recalculation (approx. 100,000 items).
When scheduling lighting for a chunk, the code did this:

```csharp
// Bad Implementation
foreach (Vector2Int column in globalColumnList) // Loop 100,000 times
{
    if (GetChunkFor(column) == currentChunk) // Math operation
    {
        // ...
    }
}
```

If scheduling 500 chunks, this resulted in **50,000,000 operations**.

**The Fix:**
We changed the data structure to `Dictionary<Vector2Int, HashSet<Vector2Int>>` (Map: Chunk Coordinate -> Set of Columns).
The lookup became:

```csharp
// Optimized Implementation (O(1))
if (globalDictionary.TryGetValue(currentChunkPos, out HashSet<Vector2Int> columns))
{
   // Process only relevant columns
}
```

**Key Lesson:**
**Choose data structures based on access patterns.** If you need to look up data *by chunk*, store it *by chunk*. Avoid iterating global lists inside per-chunk loops.

---

## 2. Voxel-Specific Optimizations

Voxel engines process millions of data points. Standard optimizations often aren't enough; memory layout is critical.

### Flattened 1D Arrays vs. Multi-Dimensional
The CPU retrieves data from RAM in "cache lines." It loves reading data sequentially.
*   **Bad:** `Block[16, 256, 16]` (3D Array). This scatters memory.
*   **Good:** `Block[16 * 256 * 16]` (1D Array). This ensures contiguous memory.
*   **The Formula:** `int index = x + (z * Width) + (y * Width * Depth);` (Example Y-Up Layout).

### Loop Order (Cache Locality)
To prevent "Cache Misses," your loops must strictly follow the memory layout.
**Rule:** The **innermost** loop must match the variable with the *smallest multiplier* in your index formula.

*   **If Index = `x + (z * Width) + ...`**:
    *   **Inner Loop:** `x` (Increments index by 1) -> FAST
    *   **Middle Loop:** `z` (Increments index by Width)
    *   **Outer Loop:** `y` (Increments index by Width*Depth)
*   **Pitfall:** If you swap this order, the CPU has to fetch a new RAM page for every single block, destroying performance.

### Object Pooling (Lifecycle)
Chunks and Meshes are heavy objects. Creating and destroying them triggers Garbage Collection (GC) and CPU spikes.
*   **Bad:** Calling `Destroy(chunkObject)` when a chunk goes out of view, and `Instantiate()` when a new one appears.
*   **Good:** **Object Pooling**.
    *   Disable the GameObject: `chunkObject.SetActive(false)`.
    *   Put it in a `Queue<GameObject>`.
    *   When a new chunk is needed, `Dequeue` one, reset its data/mesh, move it, and `SetActive(true)`.

---

## 3. General Algorithmic Guidelines

### Math Optimizations
*   **Square Roots:** `Vector3.Distance(a, b)` calculates a square root, which is slow.
    *   **Optimization:** Use `(a - b).sqrMagnitude`. Compare it against `distance * distance`.
*   **Divisions:** Division is slower than multiplication.
    *   *Bad:* `val / 2.0f`
    *   *Good:* `val * 0.5f`

### Avoid Nested Loops (Quadratic Complexity)
Nested loops over large datasets are performance killers.
*   **Bad:** Loop over all chunks -> Inside that, loop over all entities to see if they are in the chunk.
*   **Good:** Spatial Hashing. Store entities in a `Dictionary<ChunkCoord, List<Entity>>`. Loop over chunks, then look up the specific list for that chunk.

### Caching & Pre-calculation
If a value is expensive to calculate (e.g., `Mathf.PerlinNoise` or World Coordinate conversions), calculate it once and store it.
*   **Example:** The `Chunk` class stores its `WorldPosition` vector. It does not recalculate `x * width` every frame.

---

## 4. Data Structures & Collections

### `List<T>`
*   **Use when:** You need an ordered sequence, you iterate sequentially.
*   **Pitfall:** `Contains()` is **O(N)**.
*   **Pitfall:** `RemoveAt(0)` is **O(N)**. Use `Queue<T>` instead.

### `HashSet<T>`
*   **Use when:** You need unique items and fast lookup (`Contains`).
*   **Performance:** `Add`, `Remove`, and `Contains` are **O(1)**.

### `Dictionary<TKey, TValue>`
*   **Use when:** You need to map sparse data (e.g., Infinite Worlds where chunks exist at random coordinates).
*   **Performance:** Lookups are **O(1)**, but hashing keys (`GetHashCode`) has a CPU cost.
*   **Optimization:** `TryGetValue` is faster than `ContainsKey` + `[]`.
*   **Alternative:** For **Fixed Size Worlds** (like our 100x100 chunk limit), a 2D array `Chunk[100,100]` is significantly faster than a Dictionary because it avoids hashing.

### `Queue<T>`
*   **Use when:** First-In-First-Out (FIFO) operations (e.g., Processing VoxelMods, Lighting Queues).

### Structs vs. Classes
*   **Structs (Value Types):** Stored on the stack or inline in arrays.
    *   **Warning (Boxing):** Never cast a struct to `object` or an `Interface` (e.g., `List<IMyInterface>`). This forces the struct onto the Heap ("Boxing"), causing memory allocations that are often worse than just using a Class.
*   **Classes (Reference Types):** Stored on the heap. Creating them generates Garbage.

---

## 5. Unity & C# Specifics

### Modern Async: `Awaitable` vs. `Coroutine` (Unity 6)
*   **Coroutines (`IEnumerator`):** The classic Unity approach.
    *   *Downside:* Runs on Main Thread only. Allocates GC for every `yield return`.
*   **Awaitables (Unity 6):**
    *   *Benefit:* Low GC overhead. Crucially, allows easy thread switching.
    *   *Pattern:*
        ```csharp
        async Awaitable GenerateChunkAsync() {
            // 1. Move to background thread for heavy math
            await Awaitable.BackgroundThreadAsync(); 
            var data = CalculateVoxelData(); 
            
            // 2. Return to Main Thread to touch Unity Objects (Mesh)
            await Awaitable.MainThreadAsync();
            ApplyMesh(data);
        }
        ```

### Reducing GC Pressure (Garbage Collection)
1.  **Avoid `new` in `Update()`:** Do not create temporary classes or arrays inside frequently called methods.
2.  **Reuse Collections:** `Clear()` lists instead of creating new ones.
3.  **String Concatenation:**
    *   **Bad:** `text.text = "Coords: " + x + ", " + y;` (Allocates new strings every frame).
    *   **Good:** Use `StringBuilder` for complex strings.
    *   **Note:** C# String Interpolation (`$"{x}"`) in .NET Framework (Unity's Mono backend) typically allocates. Avoid in hot paths.

---

## 6. Profiling Strategy

1.  **The "Stopwatch" Method:**
    For specific logic blocks on the main thread.
    ```csharp
    Stopwatch sw = Stopwatch.StartNew();
    // ... code ...
    sw.Stop();
    Debug.Log($"Time: {sw.ElapsedMilliseconds} ms");
    ```

2.  **ProfilerRecorder:**
    Used in `DebugScreen.cs`. Gives real-time stats in builds with zero overhead.

3.  **Unity Profiler:**
    *   **Warning:** "Deep Profile" mode adds massive overhead to every method call. It distorts timing data, making small, frequent functions look like bottlenecks. Use **Standard Profiling** first. Only use Deep Profile if you are completely lost.
