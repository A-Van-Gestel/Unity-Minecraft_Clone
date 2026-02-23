# General Optimization & Architecture Guide

This document outlines optimization strategies, common pitfalls, and architectural best practices for the project. It focuses on high-level C# logic, data structures, and Unity lifecycle management (outside of the Burst/Job system).

## Table of Contents

1. [Case Study: Fixing Exponential Startup Time](#1-case-study-fixing-exponential-startup-time)
2. [Voxel-Specific Optimizations](#2-voxel-specific-optimizations)
3. [General Algorithmic Guidelines](#3-general-algorithmic-guidelines)
4. [Data Structures & Collections](#4-data-structures--collections)
5. [Unity & C# Specifics](#5-unity--c-specifics)
6. [Profiling Strategy](#6-profiling-strategy)
7. [Advanced High-Performance C# (The "Danger Zone")](#7-advanced-high-performance-c-the-danger-zone)

---

## 1. Case Study: Fixing Exponential Startup Time

In early versions, increasing the `View Distance` caused the initial world generation time to grow **exponentially** rather than linearly. Analyzing and fixing this provided two key lessons in engine architecture.

### Pitfall A: Lifecycle Interference (The "Race Condition")

**The Symptom:**
Setting a high `View Distance` (e.g., 25) caused the startup coroutine to hang for ~55 seconds, processing over 3000 chunks, even though the `MaxInitialLoadRadius` was clamped to 10 (approx. 441 chunks).

**The Cause:**

1. `Start()` initiated a coroutine to generate the initial 441 chunks. **Crucially, `Start()` finishes immediately after starting the coroutine.**
2. The coroutine yielded (`yield return null`) to process jobs.
3. The next frame, Unity called `Update()`.
4. `Update()` detected the player's position "changed" (initialized) and ran `CheckViewDistance()`.
5. `CheckViewDistance()` ignored the startup clamp and immediately queued generation for the full radius (3000+ chunks).
6. The startup coroutine, designed to "finish all pending jobs," was forced to process this massive influx of chunks synchronously.

**The Fix:**
We introduced a state flag `_isWorldLoaded`. The `Update()` loop is strictly blocked from running generation logic until the `Start()` coroutine explicitly sets this flag to true.

**Key Lesson:**
**Initialization logic running in Coroutines or Async tasks runs concurrently with the game loop.** Standard frame logic (`Update`, `FixedUpdate`) *will* interleave.

* **Good:** Use boolean flags to block logic.
* **Better:** Use a State Machine (e.g., `enum GameState { Initializing, Playing }`) to explicitly manage what logic runs during which phase.

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

* **Bad:** `Block[16, 256, 16]` (3D Array). This scatters memory.
* **Good:** `Block[16 * 256 * 16]` (1D Array). This ensures contiguous memory.
* **The Formula:** `int index = x + (z * Width) + (y * Width * Depth);` (Example Y-Up Layout).

### Loop Order (Cache Locality)

To prevent "Cache Misses," your loops must strictly follow the memory layout.
**Rule:** The **innermost** loop must match the variable with the *smallest multiplier* in your index formula.

* **If Index = `x + (z * Width) + ...`**:
    * **Inner Loop:** `x` (Increments index by 1) -> FAST
    * **Middle Loop:** `z` (Increments index by Width)
    * **Outer Loop:** `y` (Increments index by Width*Depth)
* **Pitfall:** If you swap this order, the CPU has to fetch a new RAM page for every single block, destroying performance.

### Object Pooling (Lifecycle)

Chunks and Meshes are heavy objects. Creating and destroying them triggers Garbage Collection (GC) and CPU spikes.

* **Bad:** Calling `Destroy(chunkObject)` when a chunk goes out of view, and `Instantiate()` when a new one appears.
* **Good:** **Object Pooling**.
    * Disable the GameObject: `chunkObject.SetActive(false)`.
    * Return it to a managed pool.
    * When a new chunk is needed, retrieve one from the pool, reset its data/mesh, move it, and `SetActive(true)`.
* *(See Section 5 for details on our advanced pooling architectures).*

---

## 3. General Algorithmic Guidelines

### Math Optimizations

* **Square Roots:** `Vector3.Distance(a, b)` calculates a square root, which is slow.
    * **Optimization:** Use `(a - b).sqrMagnitude`. Compare it against `distance * distance`.
* **Divisions:** Division is slower than multiplication.
    * *Bad:* `val / 2.0f`
    * *Good:* `val * 0.5f`

### Avoid Nested Loops (Quadratic Complexity)

Nested loops over large datasets are performance killers.

* **Bad:** Loop over all chunks -> Inside that, loop over all entities to see if they are in the chunk.
* **Good:** Spatial Hashing. Store entities in a `Dictionary<ChunkCoord, List<Entity>>`. Loop over chunks, then look up the specific list for that chunk.

### Caching & Pre-calculation

If a value is expensive to calculate (e.g., `Mathf.PerlinNoise` or World Coordinate conversions), calculate it once and store it.

* **Example:** The `Chunk` class stores its `WorldPosition` vector. It does not recalculate `x * width` every frame.

---

## 4. Data Structures & Collections

### `List<T>`

* **Use when:** You need an ordered sequence, you iterate sequentially.
* **Pitfall:** `Contains()` is **O(N)**.
* **Pitfall:** `RemoveAt(0)` is **O(N)**. Use `Queue<T>` instead.

### `HashSet<T>`

* **Use when:** You need unique items and fast lookup (`Contains`).
* **Performance:** `Add`, `Remove`, and `Contains` are **O(1)**.

### `Dictionary<TKey, TValue>`

* **Use when:** You need to map sparse data (e.g., Infinite Worlds where chunks exist at random coordinates).
* **Performance:** Lookups are **O(1)**, but hashing keys (`GetHashCode`) has a CPU cost.
* **Optimization:** `TryGetValue` is faster than `ContainsKey` + `[]`.
* **Alternative:** For **Fixed Size Worlds** (like our 100x100 chunk limit), a 2D array `Chunk[100,100]` is significantly faster than a Dictionary because it avoids hashing.

### `Queue<T>`

* **Use when:** First-In-First-Out (FIFO) operations (e.g., Processing VoxelMods, Lighting Queues).

### Structs vs. Classes

* **Structs (Value Types):** Stored on the stack or inline in arrays.
    * **Warning (Boxing):** Never cast a struct to `object` or an `Interface` (e.g., `List<IMyInterface>`). This forces the struct onto the Heap ("Boxing"), causing memory allocations that are often worse than just using a Class.
* **Classes (Reference Types):** Stored on the heap. Creating them generates Garbage.

---

## 5. Unity & C# Specifics

### Modern Async: `Awaitable` vs. `Coroutine` (Unity 6)

* **Coroutines (`IEnumerator`):** The classic Unity approach.
    * *Downside:* Runs on Main Thread only. Allocates GC for every `yield return`.
* **Awaitables (Unity 6):**
    * *Benefit:* Low GC overhead. Crucially, allows easy thread switching.
    * *Pattern:*
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

1. **Avoid `new` in `Update()`:** Do not create temporary classes or arrays inside frequently called methods.
2. **Reuse Collections:** `Clear()` lists instead of creating new ones, or use `UnityEngine.Pool`.
3. **String Concatenation:**
    * **Bad:** `text.text = "Coords: " + x + ", " + y;` (Allocates new strings every frame).
    * **Good:** Use `StringBuilder` for complex strings.
    * **Note:** C# String Interpolation (`$"{x}"`) in .NET Framework (Unity's Mono backend) typically allocates. Avoid in hot paths.

### Advanced Pooling Architectures

To completely eliminate GC Allocations during gameplay, the engine heavily relies on object pooling. We use two distinct types of pools depending on the use case.

#### 1. Custom Domain Pools (`DynamicPool<T>` & `ConcurrentDynamicPool<T>`)

Used for heavy, domain-specific objects like `Chunk`, `ChunkData`, `ChunkSection`, and visualization `GameObjects`.

* **Amortized Cleanup (Pruning):** These pools implement a "drip-feed" pruning system (`UpdatePruning()`). Instead of holding onto memory forever, they slowly destroy excess items over several frames if the pool exceeds a maximum target capacity. This prevents massive GC spikes
  when the player leaves heavily populated areas or lowers their render distance.
* **Thread-Safety:** `ConcurrentDynamicPool<T>` utilizes locking to allow background threads (e.g., Chunk Serialization I/O) and the Main Thread to safely exchange memory and data objects without race conditions.

#### 2. Unity Collection Pools (`UnityEngine.Pool`)

Used for temporary, short-lived standard C# collections (`List<T>`, `HashSet<T>`, `Dictionary<TKey, TValue>`).

* **Usage:** Grab a collection for local method logic, use it, and release it as quickly as possible.
  ```csharp
  var list = ListPool<ChunkCoord>.Get();
  try 
  {
      // ... populate and process list ...
  } 
  finally 
  {
      // ALWAYS use finally to ensure memory is returned even if an error occurs
      ListPool<ChunkCoord>.Release(list);
  }
  ```
* **The "Trapdoor API" Anti-Pattern:** Never return a pooled collection from a method (e.g., `Dictionary<K,V> GetStats()`). The caller won't know they are responsible for releasing it, causing memory leaks.
    * *Better Design:* Pass the collection as a parameter: `void GetStats(Dictionary<K,V> buffer)`. The caller then controls the full lifecycle (Acquire -> Populate -> Consume -> Release), making ownership crystal clear.

#### 3. The Object Provenance Hazard (**⚠️ CRITICAL WARNING**)

`UnityEngine.Pool` does not track *provenance* (where an object came from).

* **The Hazard:** If you allocate a collection with `new HashSet<T>()` and later pass it into `HashSetPool<T>.Release()`, you have poisoned the pool. If another script or system kept a reference to that original object, both systems now think they own the exact same memory. When
  the pool auto-clears the collection, it will silently corrupt the other script's data.
* **The Rule:** **Never mix `new` and `Pool.Release`.** If an object is going to be released to a pool, it *must* have been acquired from that pool via `.Get()`.

### Method Inlining (`[MethodImpl(MethodImplOptions.AggressiveInlining)]`)

In the Mono backend (Unity's default scripting runtime), property accessors and small helper methods incur a CPU cost due to pushing/popping the stack. For "Hot Paths" (code that runs millions of times per frame), this overhead is significant.

**When to use it:**

* **Small Properties:** `get => _data;` or bitwise logic wrappers like `VoxelState.id`.
* **Math Helpers:** Simple math functions like `IsVoxelInChunk` bounds checks.
* **Struct Operators:** `operator ==` and `operator !=` on custom structs.

**When NOT to use it (The "Bloat" Warning):**

* **Large Methods:** Do not inline complex methods with loops or extensive logic.
* **Virtual Methods:** Inlining generally does not work with inheritance/polymorphism.
* **Recursive Methods:** Can cause infinite compile loops or stack overflows.

**The Danger of Over-Inlining (Code Bloat):**
Inlining literally copies the body of the function into the calling method.

* If you inline a large function that is called in 100 places, you increase the executable size significantly.
* **Instruction Cache (I-Cache) Misses:** If the generated code becomes too large, it won't fit in the CPU's high-speed Instruction Cache. The CPU will waste cycles fetching instructions from slower RAM, actually **reducing** performance.
* **Rule of Thumb:** Only inline methods that are smaller than the overhead of calling them (e.g., 1-5 lines of code).

### Uninitialized Memory (`[SkipLocalsInit]`)

**Relevance:** Extreme for Mesh Generation buffers.

By default, C# defensively "zeroes out" all local variables and stack-allocated arrays before allowing you to use them. In high-performance code like mesh generation, where we declare variables (`Vector3 v`, `Color c`) and immediately overwrite them, this zeroing is wasted CPU
time.

**When to use it:**

* Inside helper methods that declare many local structs before filling them.
* When using `stackalloc` for temporary buffers.

**Example:**

```csharp
[SkipLocalsInit]
public void GenerateFace() {
    // Compiler skips zeroing 'vert', 'norm', 'col'.
    // Memory contains garbage until assigned (Instant allocation).
    Vector3 vert; 
    Vector3 norm;
    Color col;
    
    // ... logic that immediately assigns values ...
}
```

### No Inlining (`[MethodImpl(MethodImplOptions.NoInlining)]`)

**Relevance:** Error Handling & Debugging.

Sometimes, you *want* to force a method call. This is useful for "Cold Paths" (code that rarely runs, like error logging) situated inside "Hot Paths". By forcing the error logic into a separate method and preventing inlining, you keep the Hot Path small, optimizing Instruction
Cache usage.

**When to use it:**

* Heavy exception throwing or string formatting logic inside a frequently called method.

**Example:**

```csharp
public void HotMethod() {
    if (error) {
       HandleError(); // Cold path
       return;
    }
    // ... hot logic ...
}

[MethodImpl(MethodImplOptions.NoInlining)]
void HandleError() { /* Expensive string formatting */ }
```

---

## 6. Profiling Strategy

1. **The "Stopwatch" Method:**
   For specific logic blocks on the main thread.
   ```csharp
   Stopwatch sw = Stopwatch.StartNew();
   // ... code ...
   sw.Stop();
   Debug.Log($"Time: {sw.ElapsedMilliseconds} ms");
   ```

2. **ProfilerRecorder:**
   Used in `DebugScreen.cs`. Gives real-time stats in builds with zero overhead.

3. **Unity Profiler:**
    * **Warning:** "Deep Profile" mode adds massive overhead to every method call. It distorts timing data, making small, frequent functions look like bottlenecks. Use **Standard Profiling** first. Only use Deep Profile if you are completely lost.

---

## 7. Advanced High-Performance C# (The "Danger Zone")

This section covers optimization techniques that bypass standard C# safety mechanisms (Bounds Checks, Garbage Collection) to achieve C++ levels of performance.
**Use these only in "Hot Paths" (code running thousands of times per frame, e.g., Voxel Meshing, Pathfinding).**

### 7.1. Stack Allocation (`stackalloc` & `Span<T>`)

Standard arrays (`new int[1024]`) are allocated on the **Heap**. This causes Garbage Collection (GC) pressure and CPU overhead to find free memory.
**Stack Memory** is pre-allocated and extremely fast (L1 Cache friendly).

**The Technique:**
Use `stackalloc` to create temporary arrays that exist *only* for the duration of the method.

```csharp
// BAD: Allocates GC memory every time the method is called
public void ProcessData() {
    int[] tempBuffer = new int[1024]; 
    // ... use buffer ...
}

// GOOD: Zero GC, instant allocation
public void ProcessData() {
    // Span<T> wraps the raw memory safely
    Span<int> tempBuffer = stackalloc int[1024];
    // ... use buffer ...
}
```

**⚠️ CRITICAL WARNINGS:**

1. **StackOverflowException:** The Stack is small (usually 1MB). If you `stackalloc` too much (e.g., `stackalloc byte[1000000]`), the game will crash instantly.
    * *Rule of Thumb:* Keep allocations under **4KB - 16KB**.
    * *Solution:* If you need more, use a shared `ArrayPool<T>` (Heap) instead.
2. **Scope Safety:** You **cannot** return a `Span` created via `stackalloc` from a method. The memory is destroyed as soon as the method returns.

### 7.2. Unsafe Pointers & Bounds Check Bypassing

C# arrays force a "Bounds Check" on every access to ensure you don't read outside the array.

* `arr[i]` -> checks `if (i < arr.Length)` -> returns value.

In a loop running 4096 times, that's 4096 useless checks if you already know the logic is correct.

**The Technique:**
Use `unsafe` and `fixed` pointers to iterate raw memory.

```csharp
public unsafe void FastIterate(int[] largeArray)
{
    // "fixed" pins the array in memory so the GC doesn't move it while we read it.
    fixed (int* pArray = largeArray)
    {
        int* ptr = pArray;
        int* end = pArray + largeArray.Length;

        // Pointer addition is faster than array indexing
        while (ptr < end)
        {
            int value = *ptr; // Read value
            // ... process ...
            ptr++; // Move to next integer
        }
    }
}
```

**⚠️ CRITICAL WARNINGS:**

1. **Memory Corruption:** If you read past `end`, you will read garbage data. If you *write* past `end`, you might overwrite variables belonging to other classes, causing random bugs that are nearly impossible to debug.
2. **Editor Crashes:** Accessing invalid memory usually crashes the entire Unity Editor instantly. Save your work before running unsafe code.

### 7.3. Bitmasks & Passability Maps

When processing voxels, storing data in `bool[]` is wasteful. A `bool` takes 1 byte (8 bits), but we only need 1 bit.
Compressing data into `uint` bitmasks fits more data into the CPU Cache (L1), drastically speeding up algorithms like Flood Fill.

**The Technique:**
Store 32 boolean states in a single `uint`.

```csharp
// Indexing into a bitmask
int index = ...; 
int intIndex = index >> 5;      // Divide by 32 (fast bitshift)
uint bitMask = 1u << (index & 31); // Modulo 32 (fast bitwise AND)

// Set True
passableMap[intIndex] |= bitMask;

// Check True
bool isTrue = (passableMap[intIndex] & bitMask) != 0;
```

### 7.4. Algorithmic Inversion (Solving the Reverse Problem)

Sometimes checking for the *presence* of something is slow (e.g., "Is this section opaque?").
It is often faster to check for the *absence* of the opposite (e.g., "Can Air NOT flow through this section?").

**Case Study: Visible Opacity**
Instead of checking every combination of opaque blocks (complex):

1. Assume the section is a solid block of air.
2. Mark actual blocks as "Obstacles".
3. Pour "Virtual Water" (Flood Fill) from the top.
4. If the water doesn't reach the bottom, the section is **Visibly Opaque**.

This transforms a complex 3D shape analysis problem into a standard connectivity algorithm (BFS/DFS), which can be highly optimized with the Bitmasks and Stack Allocation described above.
