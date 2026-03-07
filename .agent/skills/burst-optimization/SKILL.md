---
name: burst-optimization
description: Use when optimizing code, writing new Burst jobs, refactoring performance bottlenecks, or when the user explicitly asks to make something faster.
---

# Safe Optimization Protocol

This is a high-performance engine. Efficiency is key, but stability is paramount.

## When to use this skill

- Refactoring `IJob`, `IJobFor`, or `IJobParallelFor` implementations.
- Moving logic from the Main Thread into the Job System.
- The user asks "How can we optimize this?"

## How to use it

### 1. Automatic "Low-Risk" Optimizations

You may automatically implement these without asking:

- Caching `Transform` lookups.
- Replacing LINQ (`.Any()`, `.Count()`) inside hot loops (like `Update`) with standard `for`/`foreach` loops.
- Using `ListPool<T>`, `HashSetPool<T>`, or `ArrayPool<T>` instead of `new List<T>()` in methods that run frequently.
- Using `[SkipLocalsInit]` and `stackalloc` for small, temporary buffers.

### 2. Consultative "High-Risk" Optimizations

If you identify an optimization that requires changing:

- The Lighting Algorithm (BFS Queues)
- Threading / Job Dependencies
- Memory Layout (`NativeArray`, `NativeHashMap`)
- Changing Burst Math (e.g., using bitwise operations instead of standard arithmetic)

**YOU MUST:**

1. **Mention it clearly** at the start of your response.
2. **Explain the trade-offs** (Performance gained vs. Code Complexity added).
3. **STOP and Wait** for user confirmation before writing the actual code.

### 3. Native Memory Management

Whenever you create or modify a `NativeArray`, `NativeList`, or `NativeQueue`:

- You MUST ensure it has a deterministic lifecycle and is properly disposed (`.Dispose()`).
- Use the correct Allocator (`Temp`, `TempJob`, `Persistent`).
