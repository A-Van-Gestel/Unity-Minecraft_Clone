# Meshing MR-2 Baseline — Vertex Format (pre-pack)

| Field                 | Value                                                                           |
|-----------------------|---------------------------------------------------------------------------------|
| **Captured**          | 2026-06-19 (local)                                                              |
| **Branch**            | `feat/Modular-World-Generation-&-World-Types`                                   |
| **Commit**            | `0e453e0`                                                                       |
| **Baseline kind**     | "Before" baseline for the MR-2 vertex-format packing (~60 B → 32 B/vertex)      |
| **Regression budget** | Generation patterns ≤ 5% (must not regress); upload phase expected to drop ~45% |
| **Captured by**       | `Assets/Scripts/Benchmarks/MeshGenerationBenchmark.cs` (IL2CPP player)          |
| **Total wall-clock**  | 12m 16s                                                                         |

## Why this baseline exists

Captured immediately before the MR-2 vertex-format optimization (the headline win of the
MR-* meshing set, per [`Documentation/Design/PERFORMANCE_IMPROVEMENTS_REPORT.md`](../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md)).
MR-2 repacks the per-vertex GPU format from ~60 B to ~32 B:

- TexCoord0 `Float32×4` (16 B) → `half4` (8 B)
- Color `Float32×4` (16 B) → `Color32`/UNorm8×4 (4 B)
- Normal `Float32×3` (12 B) → SNorm8×4 (4 B)
- Position kept `Float32×3` (12 B) — fluids carry sub-block surface heights; half precision risked visible cracks
- TexCoord1 (smooth light) `UNorm8×4` (4 B) — UNCHANGED (B11 pins this encoding byte-for-byte)

The **generation** patterns (`Solid`…`MixedTerrain`) measure mesh *generation* throughput and
must **not** regress (MR-2 changes the upload format, not the generation algorithm — but the
writer element types change, so generation is re-measured to confirm neutrality). The new
**Upload benchmark** block is the one MR-2 is designed to improve: `60 B/vertex → 32 B/vertex`
should cut `Vertex upload rate` MB moved per chunk by ~45% and the per-chunk upload time
proportionally (subject to driver/PCIe behaviour).

## Capture environment

This is the **first IL2CPP / WindowsPlayer** meshing baseline (prior `PHASE_02_BASELINE.md` was
Mono/Editor). IL2CPP + Player numbers are the production-parity reference; do **not** compare these
absolute numbers against the Mono/Editor Phase 2 baseline — only compare MR-2 before/after on this
same IL2CPP build.

```
=== System ===
CPU:            Intel(R) Core(TM) i9-9900K CPU @ 3.60GHz
CPU threads:    16
CPU base MHz:   3600
RAM:            65 381 MB
OS:             Windows 10  (10.0.19045) 64bit
Graphics API:   Direct3D11

=== Build ===
Unity:          6000.5.0f1
Platform:       WindowsPlayer
Mode:           Player
Backend:        IL2CPP
Build GUID:     d89e9b9e07aa48c5a772625f39507e6a
Git commit:     0e453e0

=== Burst ===
Compilation:    Enabled
Safety checks:  Enabled
Synchronous:    Disabled
```

## Capture configuration

- **Chunks per run:** 156
- **Runs per scenario:** 100
- **Modes:** `WithDiagonals`, `CardinalsOnly`
- **Upload phase:** `Checkerboard` pattern, 8 non-empty sections, 200 repeats

## Verbatim captured report

```text
--- MESH GENERATION BENCHMARK REPORT ---
Test configuration: 156 chunks per run, averaged over 100 runs.
All numbers are: ms per run (156 chunks) | μs per chunk (derived).
Total wall-clock runtime: 12m 16s

=== System ===
CPU:            Intel(R) Core(TM) i9-9900K CPU @ 3.60GHz
CPU threads:    16
CPU base MHz:   3600
RAM:            65 381 MB
OS:             Windows 10  (10.0.19045) 64bit
Graphics API:   Direct3D11

=== Build ===
Unity:          6000.5.0f1
Platform:       WindowsPlayer
Mode:           Player
Backend:        IL2CPP
Build GUID:     d89e9b9e07aa48c5a772625f39507e6a
Git commit:     (player build — record manually)

=== Burst ===
Compilation:    Enabled
Safety checks:  Enabled
Synchronous:    Disabled

=== Benchmark results ===
--- Solid ---
  - With Diagonals  :    43 ms  (  275,6 μs/chunk)
  - Cardinals Only  :    42 ms  (  269,2 μs/chunk)
  - Winner: CardinalsOnly by 1 ms (2,3% faster)

--- Checkerboard ---
  - With Diagonals  :   681 ms  ( 4365,4 μs/chunk)
  - Cardinals Only  :   681 ms  ( 4365,4 μs/chunk)
  - Result: Tied Performance.

--- OrientedCubes ---
  - With Diagonals  :    43 ms  (  275,6 μs/chunk)
  - Cardinals Only  :    44 ms  (  282,1 μs/chunk)
  - Winner: WithDiagonals by 1 ms (2,3% faster)

--- OrientedCheckerboard ---
  - With Diagonals  :   683 ms  ( 4378,2 μs/chunk)
  - Cardinals Only  :   683 ms  ( 4378,2 μs/chunk)
  - Result: Tied Performance.

--- Fluid ---
  - With Diagonals  :   179 ms  ( 1147,4 μs/chunk)
  - Cardinals Only  :   179 ms  ( 1147,4 μs/chunk)
  - Result: Tied Performance.

--- Transparent ---
  - With Diagonals  :   784 ms  ( 5025,6 μs/chunk)
  - Cardinals Only  :   797 ms  ( 5109,0 μs/chunk)
  - Winner: WithDiagonals by 13 ms (1,6% faster)

--- MixedTerrain ---
  - With Diagonals  :   364 ms  ( 2333,3 μs/chunk)
  - Cardinals Only  :   363 ms  ( 2326,9 μs/chunk)
  - Winner: CardinalsOnly by 1 ms (0,3% faster)

=== Upload benchmark (MR-2 vertex format) ===
  - Pattern                : Checkerboard (8 non-empty sections, 200 repeats)
  - Vertex format          : 60 B/vertex
  - Vertices per upload    :  278 528  (15,94 MB vertex data/chunk)
  - Upload time per chunk  :   1576,0 μs  (1,576 ms)
  - Vertex upload rate     :    10113 MB/s
```

## Mode-averaged generation reference (the "must not regress" set)

| Pattern                | Mode-averaged μs/chunk |
|------------------------|-----------------------:|
| `Solid`                |                  272.4 |
| `Checkerboard`         |                 4365.4 |
| `OrientedCubes`        |                  278.9 |
| `OrientedCheckerboard` |                 4378.2 |
| `Fluid`                |                 1147.4 |
| `Transparent`          |                 5067.3 |
| `MixedTerrain`         |                 2330.1 |

## Upload reference (the "should improve ~45%" target)

| Metric                | Before (60 B/vertex) |
|-----------------------|---------------------:|
| Vertex format         |          60 B/vertex |
| Vertex data per chunk |             15.94 MB |
| Upload time per chunk |            1576.0 μs |
| Vertex upload rate    |           10113 MB/s |

After MR-2 the format becomes **32 B/vertex** (≈8.5 MB/chunk). At the same MB/s the per-chunk
upload time should fall to roughly `1576 × 32/60 ≈ 840 μs`; capture an "after" file to record the
real number on this same IL2CPP build.

## Cross-references

- **Design doc:** [`Documentation/Design/PERFORMANCE_IMPROVEMENTS_REPORT.md`](../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md) — MR-2.
- **Architecture doc:** [`Documentation/Architecture/SUB_CHUNK_MESHING_ARCHITECTURE.md`](../Architecture/SUB_CHUNK_MESHING_ARCHITECTURE.md).
- **Folder conventions:** [`Documentation/Performance/README.md`](README.md).
- **Benchmark source:** [`Assets/Scripts/Benchmarks/MeshGenerationBenchmark.cs`](../../Assets/Scripts/Benchmarks/MeshGenerationBenchmark.cs).
