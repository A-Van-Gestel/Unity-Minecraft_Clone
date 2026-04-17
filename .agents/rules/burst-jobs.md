---
name: burst-jobs
description: Burst compiler compatibility rules for Unity Job System code. Enforced when editing files under Assets/Scripts/Jobs/.
trigger: glob
glob: Assets/Scripts/Jobs/**/*.cs
paths:
  - "Assets/Scripts/Jobs/**/*.cs"
---

# Burst Job Compatibility Rules

All code under `Assets/Scripts/Jobs/` MUST compile under Unity's Burst compiler. Violations cause hard build failures or silent runtime crashes.

## Hard requirements (code will not compile if violated)

- **No managed types.** No `class` fields, no `string`, no managed arrays (`new int[]`). Use `NativeArray<T>`, `NativeList<T>`, `NativeHashMap<K,V>`, or `static readonly` arrays for constant data.
- **All struct fields must be blittable.** A raw `bool` is not blittable — use `[MarshalAs(UnmanagedType.U1)] public bool` or replace with `byte`/`int` flags.
- **No Unity API access.** No `GameObject`, `Transform`, `GetComponent`, `MonoBehaviour`, or scene-graph calls. Pass all required data into the job struct from the main thread.
- **No `try`/`catch`/`finally`.** No exception handling of any kind inside Burst code.
- **No virtual calls.** No `interface` dispatch, no `virtual` methods, no delegates (except `FunctionPointer<T>`).
- **No LINQ.** No `.Any()`, `.Where()`, `.Select()`, `.Count()`, `.ToArray()`, `.ToList()` — these allocate managed enumerators.

## Math library

- **Always use `Unity.Mathematics`** — `math.sin()`, `math.sqrt()`, `math.clamp()`, `math.select()`, `float3`, `int3`, `bool4`, etc.
- **Never use** `UnityEngine.Mathf`, `System.Math`, `Vector3`, `Vector2`, `Vector3Int` inside jobs.

## Logging

- `Debug.Log` is allowed in Burst **only with string literals or `FixedString` types**.
- **Never use string interpolation** (`$"value: {x}"`) — it allocates managed strings. Use `FixedString128Bytes` and `.Append()` if you need dynamic values.
- Strip all debug logging before committing to production code paths.

## Performance best practices

- Mark read-only inputs with `[ReadOnly]` — enables parallel job scheduling.
- Mark non-overlapping arrays with `[NoAlias]` — enables SIMD vectorization.
- Pass small structs (< 32 bytes) by value, large structs by `in` reference.
- Prefer `math.select()` over `if/else` in tight loops — avoids branch misprediction.
- Use `[SkipLocalsInit]` and `stackalloc` for small temporary buffers.

## Reference

Full guide with examples: `@Documentation/Guides/BURST_COMPILER_GUIDE.md`
