---
name: burst-optimization
description: Safe optimization protocol for this Burst/DOTS engine — auto-apply low-risk wins (pooling, LINQ removal, caching), stop and consult before high-risk changes (threading, memory layout, lighting algorithm), and drive decisions with Unity MCP profiler data. Use when optimizing code, writing new Burst jobs, refactoring performance bottlenecks, or when the user explicitly asks to make something faster.
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

### 3. Script Validation (unity-mcp)

Before and after optimization work, use the Unity MCP validation tools:

- `Unity_ValidateScript` (level: `standard`) — run on modified scripts to catch GC allocation patterns the compiler misses (e.g. string concatenation in Update, boxing in hot paths). Use this as a pre-optimization scan to find candidates.
- `Unity_PackageManager_GetData` — verify installed Burst/Collections/Mathematics package versions before suggesting API usage that may require a specific version.

### 4. Data-Driven Profiling (unity-mcp — requires profiling data)

When the user has profiling data available (play mode with profiler recording), use the Profiler MCP tools for evidence-based optimization instead of guessing:

- `Unity_Profiler_GetOverallGcAllocations` — find which samples allocate the most GC across all recorded frames.
- `Unity_Profiler_GetFrameTopTimeSamples` — identify the slowest calls in a specific frame.
- `Unity_Profiler_GetFrameRangeTopTimeSummary` — aggregate timing across a frame range to find sustained bottlenecks (not just spike frames).
- `Unity_Profiler_GetFrameSelfTimeSamples` — self-time analysis excludes child calls, revealing the actual hotspot vs. the call tree parent.
- `Unity_Profiler_GetBottomUpSampleTime` — bottom-up view shows which leaf functions consume the most time.
- `Unity_Profiler_GetRelatedSamples` — cross-thread correlation to find if a main-thread stall is caused by a job thread.
- `Unity_Profiler_GetFrameGcAllocations` / `GetSampleGcAllocations` — drill into GC sources for a specific frame or sample.

**Rule:** Always check `Unity_ManageEditor` → `GetState` first to confirm profiling data is available. These tools return empty results without an active profiling session.

### 5. Native Memory Management

Whenever you create or modify a `NativeArray`, `NativeList`, or `NativeQueue`:

- You MUST ensure it has a deterministic lifecycle and is properly disposed (`.Dispose()`).
- Use the correct Allocator (`Temp`, `TempJob`, `Persistent`).
