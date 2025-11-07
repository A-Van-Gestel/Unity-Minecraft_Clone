# Burst Compiler Best Practices & Optimization Guide

This document outlines the rules and best practices for writing code compatible with the Unity Burst compiler. Following these guidelines is essential for achieving maximum performance in our Job System.

## Table of Contents

1. [The Golden Rules: Making Code Burst-Compatible](#1-the-golden-rules-making-code-burst-compatible)
2. [The Next Level: Optimizing Burst-Compiled Code](#2-the-next-level-optimizing-burst-compiled-code)
3. [Quick Reference Checklists](#3-quick-reference-checklists)

---

## 1. The Golden Rules: Making Code Burst-Compatible

These are the absolute requirements. Code that violates these rules will fail to compile with Burst.

### Rule 1: No Managed Objects or Managed Code

Burst code runs outside the C# garbage-collected environment. This is the source of its speed but also its biggest restriction.

- **NO `class` instances:** You cannot create or use objects of a class. Use `struct` instead.
- **NO managed arrays:** You cannot create `new int[]` or `new Vector3Int[]` inside a job. For constant data, use `static readonly` arrays. For dynamic data, use native containers like `NativeArray<T>`.
    - **Error:** `BC1028: Creating a managed array is not supported`
    - **Fix:**
      ```csharp
      // Bad: Creates a new managed array every time the job runs.
      private void MyJobMethod() {
          var offsets = new Vector3Int[] { new Vector3Int(0, 1, 0), ... };
          // ...
      }

      // Good: The array is created once and its data is read by Burst.
      private static readonly Vector3Int[] s_offsets = { new Vector3Int(0, 1, 0), ... };
      private void MyJobMethod() {
          // Use the static readonly array here...
      }
      ```
- **NO calls to most of the UnityEngine API:** You cannot call methods like `Debug.Log()`, `GameObject.Find()`, `GetComponent()`, or `Mathf.PerlinNoise()`.

### Rule 2: Pass Structs by Reference, Not Value

Passing a struct "by value" creates a copy. Burst disallows this for performance reasons.

- **DO** pass structs using the `in`, `ref`, or `out` keywords.
- **DO** use the `in` keyword for structs that are only read from. This is the most common and safest option.
    - **Error:** `BC1064: Unsupported parameter 'UnityEngine.Vector3Int position'`
    - **Fix:**
      ```csharp
      // Bad: 'position' is passed by value (copied).
      public static void MyBurstMethod(Vector3Int position) { /* ... */ }

      // Good: 'position' is passed by reference (no copy).
      public static void MyBurstMethod(in Vector3Int position) { /* ... */ }
      ```

### Rule 3: All Structs Must Be "Blittable"

A "blittable" type is one that has an identical memory representation in both managed (C#) and unmanaged (Burst/C++) code.

- **The `bool` problem:** A C# `bool` is not blittable. It's often 4 bytes in memory, not the 1 byte you'd expect.
- **Error:** `BC1063: Field 'MyStruct.MyField' of type 'bool' is not blittable.`
- **Fix:** Explicitly tell the compiler to treat the `bool` as a single byte using an attribute. You will need to add `using System.Runtime.InteropServices;`.
  ```csharp
  using System.Runtime.InteropServices;

  public struct MyJobData
  {
      // Bad: This bool is not blittable.
      public bool isSolid;

      // Good: This bool is now treated as a 1-byte value.
      [MarshalAs(UnmanagedType.U1)]
      public bool isSolidBlittable;
  }
  ```

---

## 2. The Next Level: Optimizing Burst-Compiled Code

Once your code is compiling correctly, apply these principles to make it even faster.

### Optimization 1: Use the `[ReadOnly]` Attribute Aggressively

This is the most important optimization for parallelism.

- **What it does:** It tells the Job Scheduler that your job will *only read from* a native container (`NativeArray`, `NativeList`, etc.).
- **Why it's fast:** When the scheduler knows data is read-only, it can safely schedule multiple jobs that use that same data to run in parallel without worrying about race conditions. Without `[ReadOnly]`, it assumes a write could happen and may serialize jobs unnecessarily.
- **How to use:**
  ```csharp
  [BurstCompile]
  public struct MyJob : IJob
  {
      [ReadOnly] // This array will only be read.
      public NativeArray<BlockTypeJobData> BlockTypes;

      // This array will be written to, so it does NOT get the attribute.
      public NativeArray<uint> OutputMap;
  }
  ```

### Optimization 2: Prefer `Unity.Mathematics` over `UnityEngine`

Burst is specifically designed to work with the `Unity.Mathematics` library.

- **Why it's faster:** Types like `float3`, `int3`, `quaternion`, and the `math` library are simple value types that map directly to CPU instructions. `UnityEngine` types like `Vector3` are more complex wrappers. Burst can "vectorize" operations on `Unity.Mathematics` types,
  performing a single operation on multiple pieces of data at once (SIMD).
- **What to do:** Inside your jobs and any `[BurstCompile]` methods, use:
    - `float3` instead of `Vector3`
    - `int3` instead of `Vector3Int`
    - `math.sqrt()` instead of `Mathf.Sqrt()`
    - `quaternion.Euler()` instead of `Quaternion.Euler()`
    - `math.mul(quaternion, float3)` instead of `quaternion * vector3`

### Optimization 3: Tune `[BurstCompile]` Options

You can provide hints to the compiler to make further trade-offs between precision and speed.

- **`FloatMode.Fast`**: Allows the compiler to reorder floating-point operations. This is not strictly IEEE 754 compliant but is often much faster and is perfectly safe for calculations where tiny precision differences don't matter (e.g., vertex positions, noise).
- **`FloatPrecision.Medium` or `Low`**: Use floating-point formats with less precision, which can be faster on some hardware. `Standard` is the default and is fine for most cases.

- **Example:**
  ```csharp
  [BurstCompile(FloatPrecision = FloatPrecision.Standard, FloatMode = FloatMode.Fast)]
  public struct MeshGenerationJob : IJob
  {
      // ...
  }
  ```

### Optimization 4: Keep Loops Simple and Data Linear

CPUs are fastest when they can predict what's coming next.

- **Data Access:** Accessing a `NativeArray` sequentially (e.g., `for (int i = 0; i < array.Length; i++)`) is much faster than jumping around randomly. This is because of how CPUs cache memory.
- **Branching:** Complex `if/else` chains or `switch` statements inside a tight loop can sometimes prevent Burst from vectorizing the code. If you have a performance-critical loop, try to minimize complex logic inside it.

---

## 3. Quick Reference Checklists

### Compatibility Checklist (To Fix Errors)

-   [ ] Is all my data in `structs`, not `classes`?
-   [ ] Am I using `NativeArray<T>` instead of managed arrays (`T[]`)?
-   [ ] If I need a constant array, is it `static readonly`?
-   [ ] Are all struct parameters passed with `in`, `ref`, or `out`?
-   [ ] Does my struct contain any `bool` fields? If so, have I added `[MarshalAs(UnmanagedType.U1)]` to them?
-   [ ] Am I avoiding calls to `Debug.Log`, `GetComponent`, and other managed Unity APIs?

### Optimization Checklist (For Speed)

-   [ ] Have I added `[ReadOnly]` to every native container that the job does not write to?
-   [ ] Am I using `Unity.Mathematics` types (`float3`, `int3`, `math`) inside my job instead of `UnityEngine` types?
-   [ ] Can I add `FloatMode = FloatMode.Fast` to my `[BurstCompile]` attribute for math-heavy jobs?
-   [ ] Is my most performance-critical loop simple and accessing memory sequentially?