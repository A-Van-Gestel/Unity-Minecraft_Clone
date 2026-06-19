# Meshing MR-2 Baseline — Vertex Format (post-pack, "after")

| Field                | Value                                                                                               |
|----------------------|-----------------------------------------------------------------------------------------------------|
| **Captured**         | 2026-06-20 (local)                                                                                  |
| **Branch**           | `feat/Modular-World-Generation-&-World-Types`                                                       |
| **Commit**           | `0e82130` (MR-2: packed vertex format)                                                              |
| **Baseline kind**    | "After" baseline for MR-2 — pairs with the pre-pack "before"                                        |
| **Before baseline**  | [`MESHING_MR2_2026_06_19_BASELINE.md`](MESHING_MR2_2026_06_19_BASELINE.md) (`0e453e0`, 60 B/vertex) |
| **Captured by**      | `Assets/Scripts/Benchmarks/MeshGenerationBenchmark.cs` (IL2CPP player)                              |
| **Total wall-clock** | 9m 13s  (before: 12m 16s)                                                                           |

## Result summary

MR-2 repacked the per-vertex GPU format from **60 B → 32 B** (TexCoord0→Float16×4, Color→UNorm8×4,
Normal→SNorm8×4; Position + TexCoord1 unchanged). Measured on the **same machine / same IL2CPP build
config** as the before baseline (156 chunks × 100 runs; upload phase Checkerboard × 200 repeats).

### Upload phase — the MR-2 target

| Metric                    | Before (60 B) | After (32 B) | Δ           |
|---------------------------|--------------:|-------------:|-------------|
| Vertex format             |   60 B/vertex |  32 B/vertex | −47 %       |
| Vertex data per chunk     |      15.94 MB |      8.50 MB | −46.7 %     |
| **Upload time per chunk** | **1576.0 µs** | **676.2 µs** | **−57.1 %** |
| Vertex upload rate        |    10113 MB/s |   12571 MB/s | +24.3 %     |

The upload time fell **more** than the 32/60 byte ratio alone predicts (~840 µs). The smaller vertex
stride also raised effective throughput by ~24 % (better cache/DMA behaviour per `SetVertexBufferData`),
so the byte reduction and the throughput gain compound.

### Generation phase — bonus win + one regression

MR-2 was expected to be generation-neutral (it changes the upload format, not the algorithm), but the
writer buffer element types shrank too (`Uvs` 16 B→8 B, `Colors` 16 B→4 B), so the meshing job writes
**20 fewer bytes per vertex** (60 B→40 B of per-vertex stream writes). On vertex-throughput-bound
patterns that is a large, free win:

| Pattern                | Before (µs/chunk) | After (µs/chunk) | Δ          | Note                              |
|------------------------|------------------:|-----------------:|------------|-----------------------------------|
| `Solid`                |             272.4 |            272.4 | 0.0 %      | mostly culled — write volume tiny |
| `Checkerboard`         |            4365.4 |           3256.4 | −25.4 %    | max faces → write-bandwidth bound |
| `OrientedCubes`        |             278.9 |            266.0 | −4.6 %     |                                   |
| `OrientedCheckerboard` |            4378.2 |           3214.7 | −26.6 %    |                                   |
| `Fluid`                |            1147.4 |           1221.2 | **+6.4 %** | ⚠ see below                       |
| `Transparent`          |            5067.3 |           3737.2 | −26.2 %    |                                   |
| `MixedTerrain`         |            2330.1 |           1621.8 | −30.4 %    |                                   |

**⚠ Fluid +6.4 % — over the 5 % "must not regress" budget, accepted.** Unlike the standard-cube path
(whose UVs come from a cheap atlas lookup in `AddTexture`), the fluid mesher *computes* every UV
per-vertex (flow vectors, shore push, projected side-face flow) and now converts each to `half4` —
four `float→half` conversions per UV. The fluid path is also less vertex-throughput-bound (more
per-vertex compute: height smoothing, corner flow), so the smaller-write saving doesn't fully offset
the conversion cost. The regression is ~74 µs/chunk; it is dwarfed by the ~900 µs/chunk upload win and
the 25–30 % generation gains on the dense opaque/transparent patterns (net wall-clock −25 %). Per
`Documentation/Performance/README.md` §"Re-capturing post-refactor" point 7, the trade-off is recorded
here and the Fluid budget is treated as intentionally moved for MR-2. If it ever needs reclaiming, the
fix is a cheaper UV pack in the fluid path (e.g. pack once from a `float4` accumulator, or keep the
fluid submesh's TexCoord0 at Float32) — a micro-optimization, not pursued.

## Capture environment

```
=== Build ===
Unity:          6000.5.0f1
Platform:       WindowsPlayer
Mode:           Player
Backend:        IL2CPP
Build GUID:     773385a869eb4e269d892e2c90a33372
Git commit:     0e82130   (player report prints "record manually")

=== Burst ===
Compilation:    Enabled
Safety checks:  Enabled
Synchronous:    Disabled
```

System (unchanged from before baseline): i9-9900K, 16 threads, 65 381 MB RAM, Windows 10 (10.0.19045),
Direct3D11.

## Verbatim captured report

```text
--- MESH GENERATION BENCHMARK REPORT ---
Test configuration: 156 chunks per run, averaged over 100 runs.
All numbers are: ms per run (156 chunks) | μs per chunk (derived).
Total wall-clock runtime: 9m 13s

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
Build GUID:     773385a869eb4e269d892e2c90a33372
Git commit:     (player build — record manually)

=== Burst ===
Compilation:    Enabled
Safety checks:  Enabled
Synchronous:    Disabled

=== Benchmark results ===
--- Solid ---
  - With Diagonals  :    42 ms  (  269,2 μs/chunk)
  - Cardinals Only  :    43 ms  (  275,6 μs/chunk)
  - Winner: WithDiagonals by 1 ms (2,3% faster)

--- Checkerboard ---
  - With Diagonals  :   514 ms  ( 3294,9 μs/chunk)
  - Cardinals Only  :   502 ms  ( 3217,9 μs/chunk)
  - Winner: CardinalsOnly by 12 ms (2,3% faster)

--- OrientedCubes ---
  - With Diagonals  :    42 ms  (  269,2 μs/chunk)
  - Cardinals Only  :    41 ms  (  262,8 μs/chunk)
  - Winner: CardinalsOnly by 1 ms (2,4% faster)

--- OrientedCheckerboard ---
  - With Diagonals  :   501 ms  ( 3211,5 μs/chunk)
  - Cardinals Only  :   502 ms  ( 3217,9 μs/chunk)
  - Winner: WithDiagonals by 1 ms (0,2% faster)

--- Fluid ---
  - With Diagonals  :   190 ms  ( 1217,9 μs/chunk)
  - Cardinals Only  :   191 ms  ( 1224,4 μs/chunk)
  - Winner: WithDiagonals by 1 ms (0,5% faster)

--- Transparent ---
  - With Diagonals  :   597 ms  ( 3826,9 μs/chunk)
  - Cardinals Only  :   569 ms  ( 3647,4 μs/chunk)
  - Winner: CardinalsOnly by 28 ms (4,7% faster)

--- MixedTerrain ---
  - With Diagonals  :   255 ms  ( 1634,6 μs/chunk)
  - Cardinals Only  :   251 ms  ( 1609,0 μs/chunk)
  - Winner: CardinalsOnly by 4 ms (1,6% faster)

=== Upload benchmark (MR-2 vertex format) ===
  - Pattern                : Checkerboard (8 non-empty sections, 200 repeats)
  - Vertex format          : 32 B/vertex
  - Vertices per upload    :  278 528  (8,50 MB vertex data/chunk)
  - Upload time per chunk  :    676,2 μs  (0,676 ms)
  - Vertex upload rate     :    12571 MB/s
```

## Cross-references

- **Before baseline:** [`MESHING_MR2_2026_06_19_BASELINE.md`](MESHING_MR2_2026_06_19_BASELINE.md).
- **Design doc:** [`Documentation/Design/PERFORMANCE_IMPROVEMENTS_REPORT.md`](../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md) — MR-2.
- **Architecture doc:** [`Documentation/Architecture/SUB_CHUNK_MESHING_ARCHITECTURE.md`](../Architecture/SUB_CHUNK_MESHING_ARCHITECTURE.md).
- **Folder conventions:** [`Documentation/Performance/README.md`](README.md).
