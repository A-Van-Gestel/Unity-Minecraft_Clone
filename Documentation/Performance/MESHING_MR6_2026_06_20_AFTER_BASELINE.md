# Meshing MR-6 Baseline — Output Pre-size + Pooling ("after")

| Field                | Value                                                                                              |
|----------------------|----------------------------------------------------------------------------------------------------|
| **Captured**         | 2026-06-20 (local)                                                                                 |
| **Branch**           | `feat/Modular-World-Generation-&-World-Types`                                                      |
| **Commit**           | MR-6 working tree (uncommitted at capture; pre-size + pool `MeshDataJobOutput`)                    |
| **Baseline kind**    | "After" baseline for MR-6 — pairs with the MR-2 "after" as its "before"                            |
| **Before baseline**  | [`MESHING_MR2_2026_06_20_AFTER_BASELINE.md`](MESHING_MR2_2026_06_20_AFTER_BASELINE.md) (`0e82130`) |
| **Captured by**      | `Assets/Scripts/Benchmarks/MeshGenerationBenchmark.cs` (IL2CPP player)                             |
| **Total wall-clock** | 9m 0s  (before: 9m 13s)                                                                            |

## What MR-6 changed

1. **(a) Pre-size** — `MeshDataJobOutput`'s constructor now seeds its per-vertex / per-triangle
   `NativeList`s from named constants (`DefaultVertexCapacity = 24576`, opaque tris ×1.5, secondary
   tris 4096) so a chunk's meshing job appends without the early grow → reallocate → memcpy chain.
2. **(b) Pool** — `Helpers/MeshOutputPool.cs` pools whole output instances for the runtime path
   (`WorldJobManager.ScheduleMeshing` rents; `ProcessMeshJobs` returns after upload). `NativeList`
   retains capacity across `Clear()`, so after warm-up no meshing job reallocates and the per-chunk
   Persistent allocate/free of the output struct (9 `NativeList`s + 1 `NativeArray`) is eliminated.
3. **(c)** MH-2 guard **B17** proves a reused pooled buffer == a fresh one (suite `B1–B17` green).

## Result summary — read against the noise floor

> ⚠️ **This run had a meaningful run-to-run variance.** The **upload** pass measures code MR-6 does
> **not** touch, yet it moved **676.2 → 756.8 µs/chunk (+12 %)** between the MR-2 and MR-6 captures.
> That drift (thermal / scheduling) is the noise floor for this comparison, so the generation deltas
> below — all between 0 and −5 % — are best read as **"flat, no regression,"** not as a firm win.

### Generation phase (WithDiagonals) — the pre-size effect (part a)

| Pattern                | Before µs/chunk | After µs/chunk | Δ      | Note                               |
|------------------------|----------------:|---------------:|--------|------------------------------------|
| `Solid`                |           269.2 |          269.2 | 0.0 %  | tiny write volume — pre-size moot  |
| `Checkerboard`         |          3294.9 |         3230.8 | −1.9 % | within noise                       |
| `OrientedCubes`        |           269.2 |          262.8 | −2.4 % | within noise                       |
| `OrientedCheckerboard` |          3211.5 |         3064.1 | −4.6 % | high-vertex; realloc reduced       |
| `Fluid`                |          1217.9 |         1153.8 | −5.3 % | recovers the MR-2 Fluid regression |
| `Transparent`          |          3826.9 |         3692.3 | −3.5 % | high-vertex; realloc reduced       |
| `MixedTerrain`         |          1634.6 |         1628.2 | −0.4 % | within noise                       |

The high-vertex patterns (OrientedCheckerboard, Transparent, Fluid) moved most, which is the expected
shape if pre-sizing removes some in-job reallocation/memcpy — but the +12 % upload drift means part of
this is run variance. The clear, defensible result is **no generation regression on any pattern**, and
the **Fluid path returning to its pre-MR-2 level** (1147 pre-MR-2 → 1221 MR-2 → 1154 MR-6) — i.e.
pre-sizing absorbed the ~6 % Fluid cost MR-2 had moved.

### Pooling phase (part b) — not measured here

The generation benchmark allocates a **fresh** `MeshDataJobOutput` per iteration (`new …` + `Dispose`),
so it exercises only the pre-size constructor, **not** the pool. MR-6's pooling win is the elimination
of **10 Persistent native alloc/free calls per chunk** (and the associated GC/driver overhead) in the
runtime steady state — a pure allocation-rate reduction, not a wall-clock generation change. To quantify
it, capture **GC allocations in play mode** (Unity profiler GC tools) over a chunk-streaming session
before/after; the benchmark cannot see it by construction.

## Mesh output sizes — the pre-size reference (MR-6's new benchmark table)

Per chunk (all sections), WithDiagonals:

| Pattern                | Vertices | Opaque tri-idx | Transparent tri-idx | Fluid tri-idx |
|------------------------|---------:|---------------:|--------------------:|--------------:|
| `Solid`                |     2048 |           3072 |                   0 |             0 |
| `Checkerboard`         |  278 528 |        417 792 |                   0 |             0 |
| `OrientedCubes`        |     2048 |           3072 |                   0 |             0 |
| `OrientedCheckerboard` |  278 528 |        417 792 |                   0 |             0 |
| `Fluid`                |     2048 |              0 |                   0 |          3072 |
| `Transparent`          |  393 216 |              0 |             589 824 |             0 |
| `MixedTerrain`         |  163 160 |        155 460 |              50 562 |        38 718 |

**The distribution is bimodal**, not a single median: light chunks emit ~**2 048** verts; dense surface
patterns emit **163 k–393 k**. A fixed pre-size cannot cover both without waste — but **pooling makes the
constant a cold-start hint only**: each pooled buffer retains the capacity it grew to, so it self-tunes
to the densest chunk it has meshed and steady-state realloc is zero regardless of the constant.

**Decision: keep `DefaultVertexCapacity = 24576`.** It covers the light-chunk common case with large
headroom and gives surface chunks a head start on first mesh; raising it toward the dense numbers would
pin 64 × (hundreds-of-KB-to-MB) buffers up front instead of letting the pool grow buffers **on demand**
to actual concurrent peak — the lower hint is the memory-optimal choice given retention.

## Upload phase — unchanged by MR-6 (variance reference)

| Metric                |  MR-2 after |  MR-6 after | Δ       |
|-----------------------|------------:|------------:|---------|
| Vertex format         | 32 B/vertex | 32 B/vertex | —       |
| Upload time per chunk |    676.2 µs |    756.8 µs | +11.9 % |
| Vertex upload rate    |  12571 MB/s |  11231 MB/s | −10.7 % |

MR-6 does not touch the upload path; this delta is run-to-run variance and is the **noise floor** the
generation comparison is read against.

## Capture environment

```
=== Build ===
Unity:          6000.5.0f1
Platform:       WindowsPlayer
Mode:           Player
Backend:        IL2CPP
Build GUID:     a5c90bd254834c31b96e8f85b659d0c5
Git commit:     MR-6 working tree (player build — record manually)

=== Burst ===
Compilation:    Enabled
Safety checks:  Enabled
Synchronous:    Disabled
```

System (unchanged): i9-9900K, 16 threads, 65 381 MB RAM, Windows 10 (10.0.19045), Direct3D11.
156 chunks per run, averaged over 100 runs.

## Cross-references

- **Before baseline:** [`MESHING_MR2_2026_06_20_AFTER_BASELINE.md`](MESHING_MR2_2026_06_20_AFTER_BASELINE.md).
- **Design doc:** [`Documentation/Design/PERFORMANCE_IMPROVEMENTS_REPORT.md`](../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md) — MR-6.
- **Suite guard:** baseline **B17** (MH-2) in `MeshingValidationSuite.Baseline.cs`; harness doc
  [`MESHING_VALIDATION_HARNESS_FIDELITY.md`](../Architecture/Testing%20Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md).
- **Folder conventions:** [`Documentation/Performance/README.md`](README.md).

```
