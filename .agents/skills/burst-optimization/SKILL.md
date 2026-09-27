---
name: burst-optimization
description: Safe optimization protocol for this Burst/DOTS engine — auto-apply low-risk wins (pooling, LINQ removal, caching), stop and consult before high-risk changes (threading, memory layout, lighting algorithm), and drive decisions with profiler data read through the Unity CLI. Use when optimizing code, writing new Burst jobs, refactoring performance bottlenecks, or when the user explicitly asks to make something faster.
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

### 3. Script Validation

Before and after optimization work:

- Rider `lint_files` on modified scripts — real inspections (allocations, impure struct copies,
  domain-reload analyzer hits) with line numbers. It needs Rider running with the solution open.
- `unity recompile --format json` — confirms the change compiles in the Editor itself.
- `unity command package_list --result-only` — verify installed Burst/Collections/Mathematics
  versions before suggesting API usage that may require a specific version.

### 4. Data-Driven Profiling (requires profiling data)

When profiling data is available — recorded in play mode, or a saved capture under
`ProfilerCaptures/` — read it with `Tools/UnityCli/Profiler/ProfilerQueries.cs` through
`unity command run_script` instead of guessing. The `unity-editor` skill's profiler reference has
the entry points and the drill-down workflow:

- `FrameRangeSummary` — sustained cost vs. spike frames over a range, and the worst frames.
- `OverallGc` — which samples allocate the most, and in how many frames.
- `FrameTopTime` / `FrameSelfTime` — where a slow frame's time goes; self time separates the
  actual hotspot from the parent that merely contains it.
- Job threads: `Threads` for a frame, then `FrameSelfTime` on a worker's index — to see whether a
  main-thread stall waits on job work.

**Rule:** start with `Status` to confirm frames are loaded. Entering play mode to record needs
the user's confirmation.

### 5. Prove the win (perf-benchmark)

This skill owns *making* code fast; the `perf-benchmark` skill owns *proving* it paid off —
baseline captures, benchmark harness runs, append-only `Documentation/Performance/` reports, and
the frame-level GO/NO-GO shipping verdict. For any optimization with a claimed win (an `MR-*`/
`TG-*`-style ID, or a change someone will gate on), hand off there before declaring victory.

### 6. Native Memory Management

Whenever you create or modify a `NativeArray`, `NativeList`, or `NativeQueue`:

- You MUST ensure it has a deterministic lifecycle and is properly disposed (`.Dispose()`).
- Use the correct Allocator (`Temp`, `TempJob`, `Persistent`).
