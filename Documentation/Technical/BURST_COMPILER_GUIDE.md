# Burst Compiler Best Practices & Optimization Guide

This document outlines the rules and best practices for writing code compatible with the Unity Burst compiler (Unity 6 environment). Following these guidelines is essential for achieving maximum performance in our Job System.

## Table of Contents

1. [The Golden Rules: Making Code Burst-Compatible](#1-the-golden-rules-making-code-burst-compatible)
2. [The Next Level: Optimizing Burst-Compiled Code](#2-the-next-level-optimizing-burst-compiled-code)
3. [Quick Reference Checklists](#3-quick-reference-checklists)

---

## 1. The Golden Rules: Making Code Burst-Compatible

These are the absolute requirements. Code that violates these rules will fail to compile.

### Rule 1: No Managed Objects (Classes/Arrays) inside Jobs

Burst code runs outside the C# garbage-collected environment. You cannot allocate managed memory.

**The Error:**
`BC1028: Creating a managed array is not supported`

**The Fix:**

* **Bad:** Creating a new C# array inside the job.
  ```csharp
  public void Execute()
  {
      // BAD: Allocates managed memory every frame.
      int[] offsets = new int[] { 0, 1, -1 }; 
  }
  ```

* **Good:** Using `static readonly` for constants, or `NativeArray` for dynamic data.
  ```csharp
  // GOOD: Burst can read static readonly arrays directly.
  private static readonly int[] Offsets = { 0, 1, -1 };

  public void Execute()
  {
      int val = Offsets[0];
  }
  ```

### Rule 2: All Struct Fields Must Be "Blittable"

A "blittable" type has an identical memory representation in both C# and C++.

**The Error:**
`BC1063: Field 'isSolid' of type 'bool' is not blittable`

**The Cause:**
A standard C# `bool` is not guaranteed to be 1 byte (it is often 4 bytes). Burst requires explicit memory layout.

**The Fix:**

* **Bad:** Using a raw `bool`.
  ```csharp
  public struct VoxelState
  {
      public bool IsSolid; // BAD: Non-blittable
  }
  ```

* **Good:** Explicitly marshaling as a 1-byte unsigned integer.
  ```csharp
  using System.Runtime.InteropServices;

  public struct VoxelState
  {
      [MarshalAs(UnmanagedType.U1)]
      public bool IsSolid; // GOOD: Compatible
  }
  ```

### Rule 3: Restricted API Access

You cannot access most standard Unity APIs inside a job (e.g., `GameObject.Find`, `GetComponent`, accessing `Transform`).

**Exceptions (Unity 6):**

* You **can** use `Debug.Log`, `LogWarning`, and `LogError` if you use **String Literals** or `FixedString` types.
* *Note:* While allowed, logging is slow. Use it for debugging only and strip it from production code.

**The Fix:**

* **Bad:** Accessing the Scene Graph.
  ```csharp
  public void Execute()
  {
      var player = GameObject.Find("Player"); // BAD
  }
  ```

* **Good:** Passing necessary data into the job via a struct.
  ```csharp
  public struct MyJob : IJob
  {
      public float3 PlayerPosition; // GOOD: Passed in from Main Thread
      public void Execute() { /* Use PlayerPosition */ }
  }
  ```

---

## 2. The Next Level: Optimizing Burst-Compiled Code

Once your code is compiling correctly, apply these principles to make it even faster.

### Optimization 1: `[ReadOnly]` and `[NoAlias]`

**The Concept:**
The compiler assumes all arrays *might* point to the same memory address ("Aliasing"). If it thinks `InputBlocks` and `OutputMesh` overlap, it disables vectorization (SIMD) safety features, slowing down the code.

* **`[ReadOnly]`**: Tells the Job Scheduler that multiple jobs can read this data simultaneously (Parallelism).
* **`[NoAlias]`**: Tells the Burst Compiler that a specific NativeArray does not overlap with any other array in memory (Vectorization).

**The Fix:**

```csharp
public struct MeshJob : IJob
{
    // We promise not to write here.
    [ReadOnly] public NativeArray<Block> Input; 
    
    // We promise this memory is unique and doesn't overlap 'Input'.
    [NoAlias] public NativeList<float3> Output; 
}
```

### Optimization 2: Parameter Passing (`in` vs Value)

Contrary to older documentation, you should **not** always pass by reference.

* **Pass by Value (Small Structs):** Types like `int`, `float3`, `int4`, or small structs (< 16-32 bytes).
* *Why:* It keeps data in CPU registers. Dereferencing a pointer is slower than just reading the register.
* **Pass by Reference (Large Structs):** Large data structures (e.g., a `VoxelChunk` struct containing fixed buffers or matrices).
* *Why:* Copies are expensive. Using `in` avoids copying 64+ bytes.

**Example:**

```csharp
// Fast: float3 fits in registers.
void ProcessPos(float3 pos) { ... } 

// Fast: Avoids copying a large struct.
void ProcessChunk(in ChunkData bigStruct) { ... } 
```

### Optimization 3: Branchless Logic (`math.select`)

**The Concept:**
Voxel engines perform millions of checks (e.g., `if (block.ID == 0)`). Inside a tight loop, `if/else` statements cause **Branch Misprediction**, stalling the CPU pipeline while it guesses which path to take.

**The Fix:**
Use `math.select` to compute both outcomes and pick one based on a condition, keeping the CPU pipeline full.

* **Bad:** Branching Logic.
  ```csharp
  // CPU might stall here anticipating the jump
  float density;
  if (isSolid) 
      density = 1.0f;
  else 
      density = 0.0f;
  ```

* **Good:** Branchless Selection.
  ```csharp
  // math.select(ValueIfFalse, ValueIfTrue, Condition);
  float density = math.select(0.0f, 1.0f, isSolid);
  ```

### Optimization 4: Keep Loops Simple and Data Linear

CPUs are fastest when they can predict what's coming next.

* **Data Access:** Accessing a `NativeArray` sequentially (e.g., `for (int i = 0; i < array.Length; i++)`) is significantly faster than jumping around randomly. This optimizes CPU Cache usage.
* **Complexity:** Complex `switch` statements inside a tight loop can prevent Burst from vectorizing the code.
* **Recommendation:** If you have a heavy logic branch, try to separate it into a different pass/job rather than putting it inside the tightest loop of the mesh generation.

### Optimization 5: Prefer `Unity.Mathematics` over `UnityEngine`

**The Concept:**
Burst is specifically designed to work with the `Unity.Mathematics` library. Types like `float3`, `int3`, and `quaternion` map directly to CPU registers. `UnityEngine` types (like `Vector3`) are managed wrappers.

Using `Unity.Mathematics` allows Burst to **"vectorize"** operations, performing a single instruction on multiple pieces of data at once (SIMD).

**What to Swap:**
Inside your jobs and any `[BurstCompile]` methods, use the following replacements:

| UnityEngine (Avoid inside loop) | Unity.Mathematics (Use this) | Why? |
| :--- | :--- | :--- |
| `Vector3` | `float3` | Maps directly to SIMD registers. |
| `Vector3Int` | `int3` | Maps directly to SIMD registers. |
| `Mathf.Sqrt()` | `math.sqrt()` | Optimized intrinsic instruction. |
| `Mathf.Sin()` | `math.sin()` | Optimized intrinsic instruction. |
| `Quaternion.Euler()` | `quaternion.Euler()` | Faster calculation. |
| `quat * vec3` | `math.mul(quat, vec3)` | Explicit multiplication logic. |

**A Note on Conversion Overhead (Vector3 -> float3):**
You might worry about the cost of converting your existing `transform.position` (`Vector3`) to `float3` when passing data into a job.

* **The Reality:** There is **zero to negligible overhead**.
* `Vector3` and `float3` have the exact same memory layout (3 floats: x, y, z).
* Burst and Unity can interpret the memory of a `Vector3` as a `float3` without doing any complex processing or allocation.
* **Best Practice:** Pass data into the Job as `float3`. If you have a `Vector3` in your MonoBehaviour, simply assign it to the `float3` field. The implicit conversion is efficient.

**Example:**

```csharp
// In your MonoBehaviour
myJob.Position = transform.position; // Implicit conversion Vector3 -> float3 (Free)

// Inside the Job
public struct MyJob : IJob
{
    public float3 Position; // Use float3 for all internal math
    
    public void Execute()
    {
        // FAST: SIMD optimized math
        float3 result = math.mul(Position, 2.0f); 
    }
}
```

### Optimization 6: Tune `[BurstCompile]` Options

You can provide hints to the compiler to make further trade-offs between precision and speed.

* **`FloatMode.Fast`**: Allows the compiler to rearrange math operations (e.g., assuming `a + b + c == c + a + b`). This breaks strict IEEE 754 floating-point compliance but is acceptable for games (mesh generation, noise) and yields significant speedups.
* **`FloatPrecision.Standard`**: Usually sufficient. Lowering to Low/Medium is rarely worth the accuracy loss on modern hardware.

```csharp
[BurstCompile(FloatPrecision = FloatPrecision.Standard, FloatMode = FloatMode.Fast)]
public struct GenerationJob : IJob { ... }
```

---

## 3. Quick Reference Checklists

### Compatibility Checklist (To Fix Errors)

-   [ ] Is all my data in `structs`, not `classes`?
-   [ ] Am I using `NativeArray<T>` instead of managed arrays (`T[]`)?
-   [ ] If I need a constant array, is it `static readonly`?
-   [ ] Does my struct contain any `bool` fields? If so, have I added `[MarshalAs(UnmanagedType.U1)]` to them?
-   [ ] Am I using `Debug.Log` sparingly and only with String Literals?

### Optimization Checklist (For Speed)

-   [ ] **Memory:** Have I added `[ReadOnly]` to input arrays?
-   [ ] **Memory:** Have I added `[NoAlias]` to output arrays in critical jobs?
-   [ ] **Math:** Am I using `Unity.Mathematics` (`float3`, `int3`, `math.xyz`) instead of `UnityEngine`?
-   [ ] **Math:** Am I using `math.select` instead of `if/else` for simple assignments?
-   [ ] **Params:** Am I passing large structs by `in` (ref) and small structs by value?
-   [ ] **Loops:** Is my most performance-critical loop iterating sequentially (0 to Length)?
-   [ ] **Compile:** Can I use `FloatMode = FloatMode.Fast` on the `[BurstCompile]` attribute? (Check if slight precision loss—e.g., `(a+b)+c` vs `a+(b+c)`—is acceptable for this logic).
