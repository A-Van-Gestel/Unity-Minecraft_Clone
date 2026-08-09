# Performance Improvements Report

**Version:** 1.2  
**Date:** 2026-07-26  
**Status:** **Open backlog.** 31 items open, 29 complete. Completed items keep their ✅ row in the master
summary table; their detail sections live in
[`../Archived/PERFORMANCE_IMPROVEMENTS_COMPLETED.md`](../Archived/PERFORMANCE_IMPROVEMENTS_COMPLETED.md).  
**Target:** Unity 6.5 (Mono for dev; IL2CPP for production)

> The single master backlog for **all open runtime performance improvements** in the VoxelEngine.
> Every finding shows, at a glance: the affected system, implementation effort, regression risk,
> expected benefit, and whether it can affect world-generation determinism (seed) or the on-disk
> save format.
>
> Status: **Open backlog.** When an item is implemented and verified, its **detail section** is moved to
> [`../Archived/PERFORMANCE_IMPROVEMENTS_COMPLETED.md`](../Archived/PERFORMANCE_IMPROVEMENTS_COMPLETED.md)
> while its **row stays in the master summary table below**, marked ✅. The table is therefore the index of
> the *whole* ID space, open and closed — so IDs are never recycled, and the many `MR-*` / `LI-*` / `TG-*`
> references made from other docs and from code comments still land somewhere meaningful.

**Last audited:** 2026-06-12, at commit `39c92ef` (branch `feat/Modular-World-Generation-&-World-Types`). **Implementation status synced:** 2026-06-20, at commit `ea2aec0` — all Meshing & Rendering items except MR-8 (greedy meshing) are now closed and in-game confirmed (MR-1 through MR-7, MR-9). **Implementation status synced:** 2026-07-08 — `VS-1` (shared validation-suite runner) shipped:
`Framework/ValidationSuiteRunner` + `ValidationRunResult`, six suites + `ChunkRelativePositionTests`
migrated with unchanged verdicts; `VoxelMetadataUtilityTests`/`FastNoiseLiteTests` left as a tracked follow-up. VS-2/VS-3 now build on the runner's result object. **Implementation status synced:** 2026-07-12 — `WS-1` (chunk-math shift/mask centralization) shipped on `feat/world-scaling`: Burst-safe `ChunkMath` voxel↔chunk↔region helpers + all ~11 chunk-math call sites migrated, guarded by the "Chunk Math" suite; byte-identical over the reachable range (no save bump). Audit correction folded in: the V2 codec truncation was latent-but-unreachable, not a
live bug.  
**Implementation status synced:** 2026-08-03 — `VQ-3` (sub-voxel-aware interaction raycast) shipped on
`feat/world-scaling` and in-game confirmed: a shared `Helpers/BlockCollisionBoundsUtility` (physics + debug
visualization migrated onto it, guarded by a `CheckPhysicsCollision` golden master because physics has no
suite), a `Helpers/RayBoundsIntersection` narrow phase behind `VoxelRayDDA`, and highlight/place-preview
boxes shaped to the block's volume. `VQ-4` (compound bounds for stairs/L-shapes) stays open.  
**Implementation status synced:** 2026-08-03 — `VQ-2` (exact DDA placement ray traversal) shipped on
`feat/world-scaling`: reusable `Helpers/VoxelRayDDA`, `FaceNormal` heuristic and `checkIncrement` both retired,
guarded by four new oblique-ray scenarios in the Placement suite (authored red-first — the suite's pre-existing
scenarios are all axis-aligned and could not distinguish the two implementations). Audit correction folded in:
that entry named the suite's "13 baselines" as its gate — the count was right when written and had grown to 17,
but no scenario at either size could gate a ray-march change. Validate All 375/375; no save bump.
`VQ-1` (integer `TryGetVoxel` fast path — WS-1's runtime-API half) shipped 2026-07-12 on the same branch: one-chunk-coord integer query + one-entry last-chunk cache, `GetVoxelState(Vector3)` kept as a floor-then-delegate wrapper, physics/placement/mod-apply consumers migrated; guarded by a float↔int decomposition-parity sweep in the "Chunk Math" suite; Placement suite + Validate All green (no save bump). **Third-pass audit:** 2026-07-02, at commit `99c3e6e` — added `WG-1..3`, `LI-2`, `GS-6`, `WS-1`; re-scoped `P-1` (see the pipeline table note).  
**Fourth-pass audit:** 2026-07-02, at commit `99c3e6e` — added `SL-1..4` (serialization save/load),
`VQ-1..2` + `PH-1` (voxel query layer, interaction, physics), `SU-1..2` (startup/world load): the last previously-unaudited runtime systems. **Fifth-pass audit:** 2026-07-02, at commit `99c3e6e` — added `DT-1..4` (debug tooling: voxel visualizer modes, debug screen / perf HUD, terrain-gen overlay), lifting the fourth pass's debug-tooling exemption. **Sixth-pass audit:** 2026-07-02, at commit `99c3e6e` — added `ET-1..4` (editor tooling, deep pass on `Assets/Editor/WorldTools/` + quick pass on the remaining editor tools). **Seventh-pass audit:**
2026-07-02 — added `VS-1..3` (editor validation suites), completing the audit coverage: every system in the repository has now had at least one audit pass. **Review sync:** 2026-07-10 — branch code review of `feat/async-lighting-validation-suite` added
`LI-3` (eager double neighbor-gate evaluation in the lighting ready-set scan; plan-owned by
`LIGHTING_PIPELINE_STATE_REFACTOR.md` F7 → LP-6). Findings are from static code review unless stated otherwise — capture a baseline per
`Documentation/Performance/README.md` before implementing the larger items.

**Audit scope note (second pass, 2026-06-12):** the `GS-*` (GPU & Shaders) and `OM-*` (CPU-starved device / OOM hardening) sections were added after a second review pass targeting two gaps: shader/GPU cost was previously unexamined, and the engine's behavior on CPU-starved hardware (e.g. midrange Android) where work production outpaces consumption until the process is killed out-of-memory — observed during benchmark/stress runs with fast movement. The `OM-*` items are the *consumption-side and ceiling-side* complement to `P-4` (production-side
backpressure in the pipeline doc §3): P-4 stops over-scheduling, OM-* makes sure that even when the backlog wins, the result is degradation instead of a crash.

**Audit scope note (third pass, 2026-07-02):** the `WG-*` (World Generation) section, `LI-2`, `GS-6`, and `WS-1` were added after a third review pass targeting gaps the first two passes never examined:
the standard world-generation pipeline (schedule-side buffer churn, the main-thread populate/scan, managed structure expansion), the post-P-2-Phase-1 lighting gather (full-height copies regardless of content), draw-call submission architecture, and the world-scaling enablers analyzed in
`WORLD_SCALING_ANALYSIS.md` but never tracked here. `P-1` was re-scoped in place (see the pipeline table note).

**Audit scope note (fourth pass, 2026-07-02):** the `SL-*` (Serialization & Save/Load), `VQ-*`/`PH-*`
(Voxel Queries, Interaction & Physics), and `SU-*` (Startup & World Load) sections were added after a fourth review pass over the last runtime systems no prior pass had examined: the disk **read** path (OM-3 only covered save-burst scheduling), the `GetVoxelState` query layer and its per-frame consumers (the physics solver, the placement ray march), and the world-load boot sequence. Explicitly exempt from auditing: `Legacy/` (deprecated), `Serialization/Migration/` (one-shot upgrade code),
`DebugVisualizations/` + editor tooling + benchmarks (not shipped), and UI/Input (event-driven, cold — `MT-3` already covered the one hot piece).

**Audit scope note (fifth pass, 2026-07-02):** the `DT-*` (Debug Tooling) section lifts the fourth pass's `DebugVisualizations/` exemption. The rating rationale differs from every other section:
these items are ⚪ *because they only cost while a developer is debugging* — but that is exactly when measurement fidelity matters most. A visualizer that hitches on toggle or allocates per frame distorts the very captures it exists to read (the same rationale that justified `MT-3`), and the lighting/fluid modes will be pointed at the engine's most perf-sensitive systems during LI-2/GS-5 work. Covered: `VoxelVisualizer`/`VisualizerChunkData` + the `World.HandleVisualization` driver, the
`DebugScreen` + `PerformanceMonitor` + `GraphRenderer` HUD stack, `TerrainGenDebugOverlay`, and
`ChunkBorderVisualizer` (clean — see the section's baseline note).

**Audit scope note (sixth pass, 2026-07-02):** the `ET-*` (Editor Tooling) section covers the in-editor world tools at the user's request — deep on `Assets/Editor/WorldTools/` (the
`ChunkPreview3DWindow` + `WorldGenPreviewWindow` stacks and `EditorChunkPipelineRunner`, which drive the *production* generation/lighting/meshing jobs plus their own managed preview paths — and run under Mono with no IL2CPP boost for the managed halves), quick on the rest. The quick pass came back largely clean: `BlockIconGenerator`/`AtlasPacker`/`StructurePreviewWindow`/`CaveDensityAnalyzer`/
`BiomeConfigValidator` are on-demand tools using sane patterns (PreviewRenderUtility, real pipeline jobs, dirty-flag-gated validation); the only recurring-cost nit is
`WorldGenPreviewWindow.PollForAssetChanges` stat-ing a file timestamp every editor-update tick (throttle to ~0.5 s when convenient). **The validation suites are deliberately excluded — they are their own future audit pass.** Production-parity scoreboard for the 3D preview: MR-2 ✅ (shares
`SectionRenderer.Layout` with an anti-drift comment), P-2 Phase 1 ✅ (worker-thread halo gather), MR-6 pre-size ✅ (inherited via constructor) / pooling intentionally absent (TG-6 convention); MR-5 ❌ (`ET-4`), and the remaining gaps are the `ET-*` items themselves.

**Audit scope note (seventh pass, 2026-07-02):** the `VS-*` (Validation Suites) section covers the six editor validation suites (Lighting, Meshing, Behavior, Placement, MeshQueue, LightScheduler)
plus the standalone test files (`VoxelMetadataUtilityTests`, `FastNoiseLiteTests`,
`ChunkRelativePositionTests`) — 14 menu entry points, ~13k lines. **The verdict is strongly positive**: the suites' *testing architecture* is in excellent shape — oracle + differential + golden-master layering, prove-red discipline written into scenario docstrings, fuzz layers with a 50-seed baseline / 2000-seed nightly split, synthetic block palettes deliberately decoupled from
`BlockDatabase.asset`, shared `ValidationReflection`/`GoldenMaster` framework helpers extracted exactly where drift had started, and test worlds that exercise production code paths (e.g. B21 via the real `ChunkData.FillJobVoxelMap`). Coverage backlogs live in the three fidelity docs (`Architecture/Testing Framework/*_FIDELITY.md`) and are **not** duplicated here — the `VS-*` items are purely *operational*: runner duplication, automation, and the stale-assembly foot-gun. Minor notes not worth IDs: the three small suites
(Placement/MeshQueue/LightScheduler) have no fidelity doc (their scope fits their file headers — fine at current size), and `FastNoiseLiteTests` mixes a 30-run benchmark into its validation menu item (harmless, but worth splitting if it ever slows the gate). **Which currently-uncovered systems deserve suites of their own** — serialization round-trip, worldgen determinism, pipeline state machine, physics, coordinate math, pool reset — is ranked with scope sketches in
[`VALIDATION_SUITE_COVERAGE_ROADMAP.md`](VALIDATION_SUITE_COVERAGE_ROADMAP.md) (`NS-1..6`); several
`⚠️`-gated backlog items (`SL-4`, `WG-3`, `ET-2`, `WS-1`) name those suites as their acceptance gates.

**Relationship to other documents:**

- `CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md` — deep-dive analysis of the chunk generation → lighting → meshing *pipeline* (per-job copies, backpressure, edge-check cascade), including implementation and incident history. Its open items are **summarized in the master table below (IDs `P-*`)** but their full analysis stays in that document — read it before implementing any `P-*` item.
- `CODEBASE_IMPROVEMENTS.md` — non-performance modernization backlog (API cleanups). All performance items formerly tracked there have been **absorbed into this report** (IDs noted per entry).
- `Documentation/Archived/CODEBASE_IMPROVEMENTS_COMPLETED.md` — historical record of completed items.
- `Guides/GENERAL_OPTIMIZATION_GUIDE.md` — the *techniques* reference (pooling, stackalloc, inlining). This report tracks *specific instances* in the codebase where those techniques are not yet applied.
- `WORLD_SCALING_ANALYSIS.md` — architectural analysis for world height/depth increases, negative quadrants / infinite XZ, cubic chunks, and floating origin. Several items in this report (`P-2`,
  `P-4`, `LI-1`, `OM-1`/`OM-2`) are prerequisites for that work and should be designed with its requirements in mind (3D-keyed, halo-padded storage; height-parameterized budgets) — see its §6.
- `WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md` (`TF-*`) and
  `LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md` (`RF-*`) — the *feature/design* counterparts to this report (2026-07-02 audit): biome borders/climate/hybrid terrain, dimensions, world types, day/night cycle, sky rendering, lighting effects. They cross-link `WG-*`/`LI-*`/`GS-*`
  IDs here rather than duplicating them; **their Benefit column is redefined** (player-facing value, not frame-time) — do not compare ratings across reports. The combined feature roadmap lives at the end of the `TF-*` report.
- `VOLUMETRIC_AND_RAYTRACED_EFFECTS_REPORT.md` (`VX-*`) — experimental-tier volumetric/traced effects (2026-07-20 feasibility pass): its render passes are constrained by `GS-2` (opaque texture) and fold into the `GS-4` tier audit; `MR-8`'s per-chunk 3D-light-texture aside is the same data structure as its VX-1 substrate, and its VX-8 (per-fragment voxel lighting)
  is the concrete design for MR-8's smooth-lighting escape hatch (constraint (b)).

---

## Legend

| Field       | Values                                                                                                                                                        |
|-------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Effort**  | 🟢 Low (hours, localized) · 🟡 Medium (days, several files) · 🔴 High (architectural, cross-system)                                                           |
| **Risk**    | 🟢 Low (isolated, easy to verify) · 🟡 Medium (touches shared state or visual output) · 🔴 High (touches pipeline invariants, lighting semantics, or shaders) |
| **Benefit** | 🟢 High (measurable frame-time/GC win in normal play) · 🟡 Medium (situational or smaller win) · ⚪ Low (cleanliness/scalability, negligible today)           |
| **Seed**    | ✅ Safe — cannot change generated terrain for a given seed · ⚠️ — see entry (changes some runtime-deterministic behavior, but never terrain)                  |
| **Save**    | ✅ Safe — no on-disk format change · ⚠️ Format — requires a save-format version bump + AOT migration step (see `serialization-migration` skill)               |

> **Seed-breaking note:** With one flagged exception, the items in this report do not modify
> world-generation noise, biome selection, structure placement, or any generation-job logic — they
> cannot change the terrain produced by a given seed. The ⚠️ markers under *Seed* flag changes to
> *runtime* RNG or lighting determinism, with details in the entry. The exceptions are `WG-3`
> (structure-expansion refactor) and `ET-2` (shared column-evaluator extraction): both touch
> worldgen *plumbing*, so they are gated on a byte-identical-output acceptance criterion (same
> discipline as LI-1's lighting bit-identity) — done correctly they change nothing, but they are
> the items whose implementation *could* break seeds if that gate is skipped.

---

## Master summary table

> Rows marked **✅** are complete. Their Effort/Risk/Benefit ratings are kept because the *measured*
> outcomes calibrate the legend for future items (see MR-1's 🟢→🟡 downgrade and MR-7's confirmed −18 %
> below). Their full detail sections — problem, recommendation, as-built notes and before/after numbers —
> are in [`../Archived/PERFORMANCE_IMPROVEMENTS_COMPLETED.md`](../Archived/PERFORMANCE_IMPROVEMENTS_COMPLETED.md).

### Meshing & Rendering

| ID      | Finding                                                           | Effort | Risk | Benefit | Seed | Save |
|---------|-------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| MR-1 ✅ | Per-vertex `Quaternion.Euler` in standard cube face generation    |   🟢   |  🟢  |   🟡¹   |  ✅  |  ✅  |
| MR-2 ✅ | 60-byte vertex format with a near-constant 16-byte color stream   |   🟡   |  🟡  |   🟢    |  ✅  |  ✅  |
| MR-3 ✅ | `new Material[3]` + `sharedMaterials` set per section mesh update |   🟢   |  🟢  |   🟡    |  ✅  |  ✅  |
| MR-4 ✅ | `RecalculateBounds()` per section update despite known bounds     |   🟢   |  🟢  |   🟡    |  ✅  |  ✅  |
| MR-5 ✅ | `MeshPostProcessJob` blocks the main thread per chunk apply       |   🟢   |  🟢  |   🟡    |  ✅  |  ✅  |
| MR-6 ✅ | Mesh output `NativeList`s start at default capacity               |   🟢   |  🟢  |   🟡    |  ✅  |  ✅  |
| MR-7 ✅ | Per-fluid-voxel `Allocator.Temp` arrays in the meshing job        |   🟢   |  🟢  |   🟢²   |  ✅  |  ✅  |
| MR-8    | Greedy meshing (coplanar quad merging)                            |   🔴   |  🔴  |   🟢    |  ✅  |  ✅  |
| MR-9 ✅ | `Clouds.cs` legacy mesh API with `.ToArray()`                     |   🟢   |  🟢  |   🟡    |  ✅  |  ✅  |

> ¹ MR-1 benefit downgraded 🟢→🟡 after measurement: implemented and suite-guarded, but the
> throughput delta is within the benchmark's noise floor — a correctness/cleanliness win, not a
> measurable speedup. See the MR-1 detail section for the before/after table.
>
> ² MR-7 benefit confirmed 🟢 by measurement: **−18% on the fluid pattern** (1365 → 1115 μs/chunk),
> controls flat — a real fluid-path win. See the MR-7 detail section.

### Lighting

| ID   | Finding                                                                                                                                                                                                                                                                                                                                                                                                               | Effort | Risk | Benefit | Seed | Save |
|------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| LI-1 ✅ | Branchy 9-map dispatch + hashmap cache → halo-padded volume; layout validated, **shipped net-positive via P-2 Phase 1** (worker-thread gather)                                                                                                                                                                                                                                                                     |   🟡   |  🟡  |   🟢    |  ⚠️  |  ✅  |
| LI-2 ✅ | Halo gather/extract/scans copied the full 128-voxel column regardless of content → **derived Y-band, shipped default-on** (`EnableLightingBandGather`); bit-identical (B75–B78), IL2CPP in-game **−26 % settled-streaming frame / −27 % Light** (flood sustained Light −9 %); **LI-2b bottom band also shipped 2026-07-11** (per-section emissive metadata; another −49…−59 % marginal on engaged shapes, B79–B85) |   🟡   |  🔴  |   🟢    |  ⚠️  |  ✅  |
| LI-3 | Ready-set scan eagerly evaluates BOTH neighbor gates for every ready chunk each visit (plan-owned by `LIGHTING_PIPELINE_STATE_REFACTOR.md` LP-6)                                                                                                                                                                                                                                                                      |   🟢   |  🟢  |   🟡    |  ✅  |  ✅  |

### World Generation

| ID   | Finding                                                                               | Effort | Risk | Benefit | Seed | Save |
|------|---------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| WG-1 | ~230 KB of Persistent generation buffers allocated + freed per generated chunk        |   🟡   |  🟡  |   ⚪⁴   |  ✅  |  ✅  |
| WG-2 | Main-thread section copy + per-section empty scan in `ChunkData.Populate`             |   🟡   |  🟡  |   🟡    |  ✅  |  ✅  |
| WG-3 | Structure expansion is a managed main-thread iterator over ScriptableObject templates |   🟡   |  🟡  |   🟡    |  ⚠️  |  ✅  |

> ⁴ WG-1 benefit is TG-6-class today (native churn, mostly off the frame) but the byte volume
> multiplies ~5× under `WORLD_SCALING_ANALYSIS.md` Tier A heights — pool sizing should be
> height-parameterized from the start (same rule as OM-1 budgets).

### Tick & Gameplay

| ID      | Finding                                                                                                                | Effort | Risk | Benefit | Seed | Save |
|---------|------------------------------------------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| TG-1 ⏭️  | Double voxel lookup + float-path cross-chunk queries per tick (obviated by TG-4 for fluids; grass residual negligible) |   🟡   |  🟡  |   🟢    |  ✅  |  ✅  |
| TG-2 ✅ | `OnDataPopulated` full-chunk scan through managed `BlockType`s                                                         |   🟢   |  🟢  |   🟡    |  ✅  |  ✅  |
| TG-3 ✅ | `UnityEngine.Random` → `Unity.Mathematics.Random` in behaviors                                                         |   🟢   |  🟢  |   🟡    |  ⚠️  |  ✅  |
| TG-4 ✅ | `BlockBehavior` data separation (ECS/DOTS pattern)                                                                     |   🔴   |  🔴  |   🟢    |  ✅  |  ✅  |
| TG-5 ⏭️  | `BlockBehavior` Burst function pointers (lighter alt. to TG-4 — superseded, not needed)                                |   🟡   |  🟡  |   🟡    |  ✅  |  ✅  |
| TG-6 ✅ | Per-chunk `ActiveVoxels` `NativeList<int>` alloc/free churn — pool it (TG-2 follow-up)                                 |   🟡   |  🟡  |   ⚪³   |  ✅  |  ✅  |

> ³ TG-6 benefit downgraded 🟡→⚪ after the change shipped: the pooled buffer is a `Persistent`
> (native, not GC) container, and its alloc/free is a sub-µs main-thread op over a handful of chunks
> per streaming frame — below every frame benchmark's noise floor. Two IL2CPP harnesses (the full-world
> fluid stress pass and the isolated tick bench) came back **frame-neutral / no-regression**, exactly as
> expected: the win is real but small and mostly off the main thread (worker-thread realloc-growth
> avoidance on water-heavy chunks). Shipped as a cleanliness/scalability fix per the CLAUDE.md "pool
> repeatedly alloc/freed containers" mandate and the MR-6 `MeshOutputPool` precedent, not for a
> measurable *frame* speedup. (The dedicated `ChunkGenerationBenchmark` fresh-vs-pooled leg *does* resolve
> it in isolation — ~0.95 µs/ch of main-thread time — via narrowed micro-timing; see the TG-6 detail section.)

### Main Thread & Miscellaneous

| ID      | Finding                                                    | Effort | Risk | Benefit | Seed | Save |
|---------|------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| MT-1 ✅ | `List.Insert(0)` / `RemoveAt(i)` O(n) mesh priority queue  |   🟡   |  🟡  |   🟢    |  ✅  |  ✅  |
| MT-2 ✅ | Light scheduler snapshots the full dirty set every frame   |   🟢   |  🟡  |   🟡    |  ✅  |  ✅  |
| MT-3 ✅ | `DebugScreen` intermediate string allocations per refresh  |   🟢   |  🟢  |   ⚪    |  ✅  |  ✅  |
| MT-4 ✅ | Startup `List.Contains`/`.IndexOf` O(n) custom-mesh lookup |   🟢   |  🟢  |   ⚪    |  ✅  |  ✅  |
| MT-5 ✅ | Startup `.ToArray()` intermediates feeding `NativeArray`   |   🟢   |  🟢  |   ⚪    |  ✅  |  ✅  |
| MT-6 ✅ | `CompressionFactory` "GZip" actually writes raw Deflate    |   🟢   |  🟢  |   ⚪    |  ✅  |  ⚠️  |

### GPU & Shaders

| ID   | Finding                                                                           | Effort | Risk | Benefit | Seed | Save |
|------|-----------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| GS-1 | Liquid shader: per-pixel procedural 3D simplex FBM (up to ~30 snoise calls/px)    |   🟡   |  🟡  |   🟢    |  ✅  |  ✅  |
| GS-2 | URP Opaque Texture required globally; `SampleSceneColor` even with refraction off |   🟢   |  🟡  |   🟢    |  ✅  |  ✅  |
| GS-3 | Voxel lighting math (4× `pow`) runs per-fragment on per-vertex data               |   🟢   |  🟢  |   🟡    |  ✅  |  ✅  |
| GS-4 | Render pipeline tier audit: shadow variants, TwoSided casting, MSAA, render scale |   🟢   |  🟢  |   🟡    |  ✅  |  ✅  |
| GS-5 | Section occlusion culling (underground sections render despite being sealed)      |   🔴   |  🟡  |   🟢    |  ✅  |  ✅  |
| GS-6 | Per-section GameObject + MeshRenderer submission (BatchRendererGroup conversion)  |   🔴   |  🔴  |   🟡    |  ✅  |  ✅  |

### CPU-Starved Device / OOM Hardening

| ID   | Finding                                                                               | Effort | Risk | Benefit | Seed | Save |
|------|---------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| OM-1 | All budgets/caps are desktop-tuned absolute constants — no device-tier scaling        |   🟢   |  🟢  |   🟢    |  ✅  |  ✅  |
| OM-2 | No memory-pressure response: `Application.lowMemory` unused, no resident-chunk budget |   🟡   |  🟡  |   🟢    |  ✅  |  ✅  |
| OM-3 | Unbounded concurrent chunk saves on mass unload (one `Task` per chunk)                |   🟡   |  🟡  |   🟢    |  ✅  |  ✅  |

### Serialization & Save/Load

| ID   | Finding                                                                                           | Effort | Risk | Benefit | Seed | Save |
|------|---------------------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| SL-1 | Per-chunk managed allocations on the load/save path (payload `byte[]`, wrappers, padding)         |   🟡   |  🟡  |   🟡    |  ✅  |  ✅  |
| SL-2 | Disk-load apply path runs unbudgeted on the main thread (no per-frame cap)                        |   🟡   |  🟡  |   🟡    |  ✅  |  ✅  |
| SL-3 | `SaveChunkAsync` snapshots up to ~190 KB per chunk on the main thread at unload                   |   🟡   |  🟡  |   🟡    |  ✅  |  ✅  |
| SL-4 | Whole-file region lock serializes chunk loads behind saves (design: `REGION_FILE_CONCURRENCY.md`) |   🟡   |  🔴  |   🟡    |  ✅  |  ✅  |

### Voxel Queries, Interaction & Physics

| ID   | Finding                                                                                                                                                                                                           | Effort | Risk | Benefit | Seed | Save |
|------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| VQ-1 ✅ | **SHIPPED 2026-07-12** — integer `TryGetVoxel` fast path (one chunk-coord, no float/nullable) + one-entry last-chunk cache; `GetVoxelState(Vector3)` kept as wrapper; physics/placement/mod consumers migrated. **Contract narrowed 2026-07-27** (Fluid §18): resolves `IsPopulated` chunks only — checked live *after* the cache, so a placeholder that generates later resolves on the next query |   🟡   |  🟡  |   🟡    |  ✅  |  ✅  |
| VQ-2 ✅ | **SHIPPED 2026-08-03** — placement ray march replaced by an exact Amanatides–Woo traversal (`Helpers/VoxelRayDDA`); no cell is skipped, the entered face is the stepped axis (the fractional-offset `FaceNormal` heuristic is deleted), and `checkIncrement` is retired as a setting. ~159 → ≤15 queries per probe at `reach = 8` |   🟡   |  🟡  |   ⚪    |  ✅  |  ✅  |
| VQ-3 ✅ | **SHIPPED 2026-08-03** — the interaction ray gained a sub-voxel narrow phase (`Helpers/RayBoundsIntersection` behind `VoxelRayDDA`'s broad phase, via the shared `BlockCollisionBoundsUtility`): a half-slab now stops the ray only where its volume is, the reported face is the block's rather than the cell's, and the highlight / place-preview boxes hug that volume |   🟡   |  🟢  |   ⚪⁶   |  ✅  |  ✅  |
| VQ-4 | Single AABB per block type cannot express stairs / L-shapes (`SUB_VOXEL_COLLISION_SYSTEM.md` §7 deferred)                                                                                                          |   🔴   |  🟡  |   ⚪⁶   |  ✅  |  ✅  |
| PH-1 ✅ | **SHIPPED 2026-08-04** — gather once per substep into a per-entity `PhysicsCellBuffer`; all nine sweeps read it, with a direct-scan fallback for sweeps that escape the envelope. Identical by construction (shadow pass: 0 mismatches / 142 sweeps). **2.08× fewer cell reads per FixedUpdate**, 0 fallbacks over 32,555 gathers |   🟡   |  🟡  |   ⚪⁵   |  ✅  |  ✅  |
| PH-2 ✅ | **SHIPPED 2026-08-04** — the substep loop advances a local `runningPos` and `ResolveMovement` takes the position to resolve from as an argument; `CalculateVelocity` no longer writes the transform at all, so the staged position and its trailing revert are gone. Behavior-neutral by measurement (shadow pass: 0 mismatches / 5,846 substepped ticks); `B26` pins the invariant. ≈5.95 staged transform accesses elided per tick at 2.477 substeps/tick |   🟢   |  🟡  |   ⚪⁵   |  ✅  |  ✅  |

> ⁶ VQ-3/VQ-4 are **correctness/capability** items, not frame-time ones — filed here because `VQ-*` is
> where the interaction and voxel-query layer is tracked, and there is no feature-report counterpart for
> interaction (`TF-*` is worldgen, `RF-*` is lighting/rendering). Read their ⚪ as "no measurable frame-time
> change expected", the same sense VQ-2's ⚪ carried.
>
> ⁵ VQ-2/PH-1/PH-2 benefits are ⚪ with a single player entity — but `VoxelRigidbody` is the collision
> solver any future entity (mobs, items) will reuse, and all three scale linearly with entity count.
> VQ-1 is 🟡 because every per-frame consumer funnels through it.

### Startup & World Load

| ID   | Finding                                                                                        | Effort | Risk | Benefit | Seed | Save |
|------|------------------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| SU-1 | Loading screen throttled by gameplay-tuned per-frame budgets                                   |   🟢   |  🟡  |   🟡    |  ✅  |  ✅  |
| SU-2 | Initial load schedules generation + disk loads for the whole radius at once (no in-flight cap) |   🟡   |  🟡  |   🟡    |  ✅  |  ✅  |

### Debug Tooling

| ID   | Finding                                                                                            | Effort | Risk | Benefit | Seed | Save |
|------|----------------------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| DT-1 | Debug visualization refresh has no per-frame budget (full-world burst on toggle, per-edit rescans) |   🟢   |  🟢  |   ⚪⁶   |  ✅  |  ✅  |
| DT-2 | `VisualizerChunkData` per-update Persistent container churn + `ToArray()`/bounds per apply         |   🟢   |  🟢  |   ⚪⁶   |  ✅  |  ✅  |
| DT-3 | Visualization update-set fed on every voxel edit even when the mode is `None`                      |   🟢   |  🟢  |   ⚪⁶   |  ✅  |  ✅  |
| DT-4 | Debug HUD/overlay allocation leftovers post-MT-3 (graph sample arrays, label `Format`, IMGUI)      |   🟢   |  🟢  |   ⚪⁶   |  ✅  |  ✅  |

> ⁶ ⚪ by definition (debug-only) — but these directly protect **measurement fidelity**: DT-1/DT-2
> make the lighting/fluid visualization modes usable *while* profiling the systems they visualize,
> and DT-3/DT-4 keep the disabled/idle debug stack at true zero so it never shows up in a capture.

### Editor Tooling (WorldTools)

| ID   | Finding                                                                                              | Effort | Risk | Benefit | Seed | Save |
|------|------------------------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| ET-1 | Cross-Section preview evaluates terrain columns in serial managed code on the main thread            |   🟡   |  🟢  |   ⚪⁷   |  ✅  |  ✅  |
| ET-2 | Preview replicates production logic (column shaping ~300 lines; replacement rules **diverge**)       |   🔴   |  🟡  |   🟡    |  ⚠️  |  ✅  |
| ET-3 | 3D-preview pipeline: full snapshot copies per job + full-grid ×5 lighting re-passes + dead copy-back |   🟡   |  🟢  |   ⚪⁷   |  ✅  |  ✅  |
| ET-4 | `MeshPostProcessJob` runs `Schedule().Complete()` per chunk in the preview (MR-5 not mirrored)       |   🟢   |  🟢  |   ⚪⁷   |  ✅  |  ✅  |

> ⁷ ⚪ = dev-time only, but these set iteration speed for worldgen authoring: at high preview
> resolutions/radii the managed paths freeze the editor for seconds per regenerate — under Mono,
> with no IL2CPP to hide it. ET-2 is 🟡 because it is also a **correctness** issue: the preview's
> hand-rolled replacement rules can show structures the game would not place (and vice versa).

### Validation Suites

| ID   | Finding                                                                                                                                                                                                                                                                                                                                                                                                               | Effort | Risk | Benefit | Seed | Save |
|------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| VS-1 ✅ | **SHIPPED 2026-07-08** — shared `Framework/ValidationSuiteRunner` + `ValidationRunResult` (per-scenario + total timing; `KnownBugChannel` ends the archive-vs-promote drift); six suites + `ChunkRelativePositionTests` migrated, verdicts unchanged; `VoxelMetadataUtilityTests`/`FastNoiseLiteTests` remain a tracked follow-up (assertion-model mismatch)                                                       |   ✅   |  ✅  |   ⚪    |  ✅  |  ✅  |
| VS-2 ✅ | **SHIPPED 2026-07-09** — `Validate All` aggregate + `ValidationSuiteCI` headless entry (`RunHeadless` exit-code + NUnit3 XML; `RunSelected`/`-validationSuites` subset) over an explicit registry; per-suite `World.Instance` isolation guard (snapshot→force-restore→mark-failed) proven leak-tight; `Validation Framework` self-test suite added (8 suites, 151 baselines, fwd==rev==individual)                 |   🟢   |  🟢  |   🟡    |  ✅  |  ✅  |
| VS-3 ✅ | **SHIPPED 2026-07-10** — `Framework/StaleAssemblyGuard` diagnostic preamble in the shared runner (warn-only, never fails a baseline, suppressed to warn once per aggregate); 3 signals (isCompiling/isUpdating, source-vs-DLL, domain-vs-disk `[InitializeOnLoadMethod]` capture) over the two project assemblies; 6 self-tests (Validation Framework → 16, aggregate → 159); live-proven stale warning fires once |   🟢   |  🟢  |   ⚪    |  ✅  |  ✅  |

### World Scaling Enablers

| ID   | Finding                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 | Effort | Risk | Benefit | Seed | Save |
|------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| WS-1 ✅ | **SHIPPED 2026-07-12** — `ChunkMath` shift/mask helpers (`VoxelToChunk`/`VoxelToLocal`/`ChunkToRegion`/`ChunkToRegionLocal`/`WorldToChunk`, Burst-safe) + all ~11 chunk-math call sites migrated (incl. `RegionAddressCodec.V2` and the `StandardWormCarverJob` Burst site); byte-identical over the reachable range (no save bump), negative-correct for Tier B; guarded by the "Chunk Math" suite (21 scenarios incl. a negative-coordinate teeth case). Audit finding: the V2 step-1 truncation was **latent-but-unreachable**, not "already live" — all encoder callers pass exact chunk origins |   🟡   |  🟡  |   ⚪    |  ✅  |  ✅  |

### Chunk Pipeline (deep-dive in `CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md`)

These remain fully documented in the pipeline analysis — the table is reproduced here so this report is the single at-a-glance view. **Read that document (and the `chunk-lifecycle` skill) before implementing any of these.**

| ID  | Finding (doc section)                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                               | Effort | Risk  | Benefit | Seed |   Save    |
|-----|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:------:|:-----:|:-------:|:----:|:---------:|
| P-1 | Border-slab copies instead of full-volume snapshots (§1.2)                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          |   🟡   |  🟡   |   🟢    |  ✅  |    ✅     |
| P-2 | ✅ Worker-thread gather (Layer 1) **SHIPPED 2026-06-22** (banks the LI-1 win, −34/−50 % vs LI-1 POST) + persistent zero-copy storage (Layer 2, §1.3) **SHELVED 2026-07-26** — profiler gate never triggered and no consumer remains (lighting took Layer 1; the fluid tick chose its own halo gather) — **[archived design](../Archived/PERSISTENT_CHUNK_STORAGE_P2.md)**                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                             | ✅→🔴  | ✅→🔴 |   🟢    |  ✅  |    ✅     |
| P-3 | Jobified lighting merge in `ApplyLightingJobResult` (§2)                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            |   🟡   |  🟡   |   🟢    |  ✅  |    ✅     |
| P-4 | ✅ Backpressure (§3) **COMPLETE**: §3.1 cap + §3.2 discard + §3.3 unload-via-persistence SHIPPED 2026-07-21; **§3.4 time budgets (rate quota + ms ceiling) + §3.5 panic gate (+ §5.3 draw-drain rider) SHIPPED 2026-07-23** — fill-load FPS 13.3→29.1, hitch frames 67%→11% (editor screening, [benchmark](../Performance/CHUNK_PIPELINE_P4_BACKPRESSURE_2026-07-23_BENCHMARK.md)); **IL2CPP GO (final)** ([player A/B](../Performance/CHUNK_PIPELINE_P4_BACKPRESSURE_IL2CPP_2026-07-23_BENCHMARK.md)): legacy never drains post-relocation (3/3 legs 300 s timeout) vs ON 15.5 s / 2.2% hitches / 78 ms worst frame — flag retirement unblocked (soak first). Refinement: FPS-cap-proportional ceilings (`scaleBudgetCeilingsWithFpsCap`, default-ON) so a voluntarily capped 30/15 FPS session is not over-throttled — B7 pinned, **IL2CPP GO** ([A/B](../Performance/CHUNK_PIPELINE_P4_CEILING_SCALING_IL2CPP_2026-07-23_BENCHMARK.md)): 30-cap fill ×1.82, 15-cap ×1.32, zero frame-health cost |   ✅   | 🟡→🔴 |   🟢    |  ✅  |    ✅     |
| P-5 | "Lighting stable" save bit to skip edge checks on load (§4.4)                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       |   🟡   |  🟡   |   🟢    |  ✅  | ⚠️ Format |
| P-6 | Smaller observations (§5): ~~O(n) removals~~ (5.1 ✅ MT-1), ~~draw-queue trickle~~ (5.3 ✅ 2026-07-23, P-4 rider); fail-safe scan counter (5.2) + LINQ style nit (5.4) remain                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       |   🟢   |  🟢   |   🟡    |  ✅  |    ✅     |
| **P-7** | **#2 open pipeline item** (was #1 by FP-4, then demoted below P-8 by FP-8; now behind **P-9** — see that row) (§6 item 5). **Chunk service ordering.** [FP-4](../Performance/CHUNK_PIPELINE_FP4_FLIGHT_PROFILE_IL2CPP_2026-07-28_BENCHMARK.md) finds waste above the 20 % ordering threshold in **all 9** loading phases across viewDistance 5/10/20 (22.9–61.2 %) — *including the default vd, where the panic gate never closes*, so it is intrinsic rather than a throttling artifact. At vd 20 / 200 m/s the pipeline starts 728 chunks/s and ships 219 (~3.3 units of work per chunk delivered). Three known order defects: stale nearest-first generation queue (refreshed only per boundary crossing), hash-ordered lighting ready set, FIFO mesh queue. **Acceptance target: `latency ≤ viewDistance × 16 ÷ speed`** (FP-4's visibility criterion, validated against independent visual observation at three view distances). **Needs its own design doc**; `chunk-lifecycle` skill mandatory. **⚠ SUPERSEDED 2026-07-31 by [FP-8](../Performance/CHUNK_PIPELINE_FP8_FLIGHT_PROFILE_IL2CPP_2026-07-31_BENCHMARK.md):** those percentages counted never-admitted requests as waste. Rescored, ordering-boundness **decays** with view distance (37.8/38.0/36.2/19.8/14.6 % at vd 5/8/10/15/20) — **confirmed intrinsic at the default vd 5 and absent by vd 20**, so P-7 is **demoted below P-8** and re-scoped to low view distance. **✔ CONFIRMED 2026-08-01 by [FP-10](../Performance/CHUNK_PIPELINE_FP10_FLIGHT_PROFILE_IL2CPP_2026-08-01_BENCHMARK.md):** the decay reproduced on FP-9b's rebuilt route (38.5/43.2/36.6/19.5/13.7/8.6 % at vd 5/8/10/15/20/32, within ~1 pt of FP-8 at four of five overlapping points), so it is a property of the pipeline and not of the benchmark route. **Worst case relocates to vd 8 / 200 m/s (50.8 %)** — where the gate has started closing but not yet suppressed admissions — so tune against vd 8, not vd 5. **Corroborated 2026-08-01 by the P-8 capture:** admitting more work raised waste exactly as the P-8 blockquote predicted (loading 200 m/s at vd 32: **17.9 % scaled vs 10.7 % unscaled**), confirming the gate had been suppressing ordering waste by refusing the work rather than the pipeline being well ordered. P-7 itself is unchanged and stays scoped to low view distance. **📌 Candidate mechanism recorded 2026-08-01 (design input, not yet a plan) — predictive ordering by lead time.** The proposal: score every chunk by its distance to a **predicted** player position `p + v × t_lead` rather than to `p`. This is deliberately **one** policy, not a low-speed and a high-speed one — as speed → 0 the term `v × t_lead` vanishes and the score degenerates *exactly* to today's nearest-first, so there is no mode switch, no crossover threshold to tune, and no hysteresis at the boundary (the standard failure of two-mode systems, and one that would trip constantly at 200 m/s). It also addresses the **staleness** defect named in the caveat above rather than the ordering defect alone: `CheckViewDistance`'s `SpiralLoop` order is already nearest-first but is refreshed only per boundary crossing, and an order computed with lead time stays valid longer than one computed at the current position. **`t_lead` has a natural self-calibrating value** — the pipeline's own service latency, already measured as p50 `enqueue→MeshApplied` — giving "prioritise the chunk the player will be standing in by the time it is ready". ⚠️ Two conditions on it: it is a **feedback loop** (ordering → latency → ordering) so it needs damping and a clamp rather than a raw per-frame sample, and the lead distance must be clamped to the loaded region or it prioritises chunks that were never requested. **⚠ Hard bound on what this can achieve**, derived in [`CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md`](CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md) §2: the visibility criterion rearranges to `latency × speed ≤ vd × 16` — *lead distance ≤ view distance* — which is exactly the condition under which **any** ordering policy can work. At vd 32 / 200 m/s the lead distance is 800 m against a 560 m load distance, so no priority function can reach the needed chunk and that regime is throughput-bound (P-9). Ordering has headroom at 50 m/s (200 m lead), which is where P-7 is scoped. **Mispredicted turns cost ordering quality but little real waste** — a mispredicted chunk inside view distance stays resident, merely serviced out of order. Belongs in P-7's own design doc; note the same predicted-position score could also drive P-9's provisional-delivery trigger (that doc's open question 0), which is an argument for designing them together once both are scheduled |   🔴   |  🔴   |   🟢    |  ✅  |    ✅     |
| **P-8** | **Scale the panic-gate thresholds with view distance** (§6 item 6). `panicGateCloseThreshold`/`ReopenThreshold` are absolute (256/128) while the resident square grows as view-distance² — a 256 backlog is 88.6 % of the resident set at vd 5 but 11.6 % at vd 20. Measured gate closure at loading 200 m/s: **0.0 % / 92.8 % / 96.4 %** at vd 5/10/20 ([FP-4](../Performance/CHUNK_PIPELINE_FP4_FLIGHT_PROFILE_IL2CPP_2026-07-28_BENCHMARK.md) F5). Same constant is an unreachable brake at the default and a permanent throttle at vd 20. Localized, but changes admission behavior (P-4 family) — pair with a vd-5 confirmation capture. **Interacts with P-7**: more admission without better ordering just buys more discarded work. **⬅ PROMOTED to TOP OPEN PIPELINE ITEM 2026-07-31 by [FP-8](../Performance/CHUNK_PIPELINE_FP8_FLIGHT_PROFILE_IL2CPP_2026-07-31_BENCHMARK.md)**, above P-7: a five-point sweep finds a **knee between vd 5 and 10** (closure 0/54.7/91.3/96.9/88.6 % at vd 5/8/10/15/20) and **12 087 requests dropped before admission** at vd 20/200 m/s — 55.8 % of everything requested. **✔ CONFIRMED at #1 2026-08-01 by [FP-10](../Performance/CHUNK_PIPELINE_FP10_FLIGHT_PROFILE_IL2CPP_2026-08-01_BENCHMARK.md), consequence quantified:** threshold-vs-residency runs 88.6/48.4/35.1/18.7/11.6/**5.1 %** at vd 5/8/10/15/20/32, so from vd 15 up the gate is essentially never open; across vd 5 → 32 **requests grow 4.47×/4.76× while admitted work grows only 1.51×/1.73×**, with completion-of-admitted showing no trend (53–68 %). **Constraint on the fix:** the gate is currently succeeding at protecting frame time (at vd ≥ 20 flying *faster* costs *less* CPU, because the faster phase trips it), so gate any change on frame time, not admission counts alone. **❌ BUILT AND REFUTED 2026-08-01 — PARKED, and the premise above is withdrawn** ([capture](../Performance/CHUNK_PIPELINE_P8_GATE_SCALING_IL2CPP_2026-08-01_BENCHMARK.md), NO-GO): ten IL2CPP Release runs on one build — seven scaled view distances plus same-build unscaled controls at vd 8/26/32. The backlog **grows to meet whatever threshold it is given**: at vd 32 a 4.2× threshold moved gate closure by **0.1 points** (94.6 % vs 94.5 %), admitted work by 0.2 %, and completions **down 16 %**. Admitted growth vd 5 → 32 was **1.58×** against a pre-committed ≥ 3.0× (unscaled: 1.51×), at a **−37 % / −32 % loading min-FPS** cost at vd 26/32. **The binding constraint is the lighting/mesh schedule `Quota`** — 99 %+ of frames in *both* legs — not admission, which corrects FP-10 F2: willingness to accept was downstream of a throughput ceiling. Code + **B19** retained behind `scalePanicGateThresholdsWithResidency`, **default-OFF** (behaviour byte-identical to pre-P-8). **Premature, not wrong** — re-test after the throughput ceiling moves |   🟢   |  🟡   |   ❌ refuted    |  ✅  |    ✅     |
| **P-9** | ⬅ **TOP OPEN PIPELINE ITEM — promoted 2026-08-01 by the P-8 NO-GO.** **Schedule-quota throughput ceiling at high view distance.** The pipeline finishes a near-constant amount of work no matter how much it admits: completions sit in a **5 658–6 803 band across vd 10 → 32** in *both* legs of the P-8 A/B, while requests grow 4.4×. The limit is measured directly — `LightSchedule` reports `Quota` on **99.3 %** of frames at vd 32 / loading 200 m/s with gate scaling ON and **99.5 %** with it OFF; `MeshSchedule` likewise; `InFlightCap` and `AllDeclined` dominate no phase in any of the ten runs. Knobs implicated: `maxLightJobsPerFrame` (32 default, **24** on the capture machine after OM-1 calibration) and `maxMeshRebuildsPerFrame` (10 default, **11**), which anchor the P-4 §3.4 rate quotas. **Not simply "raise the caps"** — the quota exists to bound main-thread cost, and P-8 demonstrated what happens when a limit is loosened without checking what it was protecting; any proposal must be gated on frame time exactly as P-8 was. **Design doc: [`CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md`](CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md)** (2026-08-01) — identifies the mechanism as the rate quota's `cap × 60` items/second identity, whose terms contain neither view distance nor frame rate, so delivered chunks/s is flat *by construction*; establishes that the **quota, not the ms ceiling, is the operative steady-state main-thread bound** (`CeilingExpired` ≤ 0.7 % of frames), so raising a cap has no second line of defence; and therefore leads with **deliver-then-refine** — a chunk becomes visible on its first viable mesh and later lighting passes correct it in place — over raising the caps, because the ~7.6 lighting and ~3.5 mesh schedules per delivered chunk (both *inferred*, unmeasured) are spent *ahead of first visibility*. Acceptance is **visibility-primary** (FP-4's `latency ≤ vd × 16 ÷ speed`, currently missed by 1.4–1.6× at vd 20/26/32), shared with P-7. Phase **P9-0a is a zero-code falsification probe on the existing FP-11a build** — §7.1 verifies that a menu-launched capture inherits all of settings.json through the warm settings cache, so both caps are A/B-able without a rebuild — and may kill the item. Evidence: [P-8 capture](../Performance/CHUNK_PIPELINE_P8_GATE_SCALING_IL2CPP_2026-08-01_BENCHMARK.md) §F3 + verdict details; ranking rationale in [FLIGHT_PROFILE_CAPTURE.md](FLIGHT_PROFILE_CAPTURE.md) §7.3 row 1. ⚠ **Baseline caveat:** FP-10 is **not** a valid comparison baseline at vd ≥ 20 for builds carrying FP-11a (P-8 §F5) — capture a fresh one on the current build. **✔ MECHANISM CONFIRMED 2026-08-02 by [P9-0a](../Performance/CHUNK_PIPELINE_P9_0A_CAP_SWEEP_IL2CPP_2026-08-02_BENCHMARK.md), and the obvious fix priced out.** Two settings-only legs at vd 32 on the P-8 build (no rebuild — the design doc's §7.1 establishes that a menu-launched capture inherits all of settings.json). Doubling `maxLightJobsPerFrame` 24 → 48 behaved *exactly* as the doc's pre-committed prediction required: the panic gate reopened on its own (**95.1 % → 62.6 %** closed — it keys on the lighting backlog, so draining it releases admission), `enqueue→populated` collapsed **2 999 → 2 134 ms** while `populated→lit` barely moved (533 → 494 ms), admitted rose 25 % and completions **21 %**, p50 e2e **−26 %**. So the rate identity is real and admission was downstream of it. **But Q2 fails by a factor** — loading avg CPU **×4.79** (6.1 → 29.2 ms), min FPS **×0.61** — and the binding limit did not move to a higher quota: it moved to the **8 ms schedule ceiling** (`Ceiling` on 95.8 % of frames), past which the count cap is inert. Decisively, the schedule pass explains only **+6.8 ms of +23.1 ms**; a model fitted to both legs puts the rest in the **unbudgeted `ProcessLightingJobs` merge** (jobs landing per frame rose ~8 → ~52 as the frame rate fell 5.4×). **Consequence: the lever order becomes C → B2 → A′.** Per-item cost — i.e. **P-3, the jobified lighting merge** — is promoted from parallel to **gating**, reversing FP-4's deprioritisation of it for this regime; raising the caps (P9-3) is **blocked behind P-3**; deliver-then-refine keeps its product rationale but cannot meet the visibility budget alone (the hops it recovers are ~537 ms of 3 703 ms). Next step is the P9-0 attribution instrument, which confirms or kills that model. **✅ P9-0 SHIPPED 2026-08-02 — instrument only, no capture yet and no production behaviour change.** `WorldFrameProfiler` is split from four phases to eight, giving the two **unbudgeted** regions their own slots: `LightMerge` (the §F4 suspect) and `LightFailSafeScan` (the ~1 Hz full-world walk, which would otherwise have recreated the same unattributed-cost gap somewhere new); `LastFrameLightMs`/`LastFrameMeshMs` remain derived sums so fluid-stress captures stay comparable across the split. `PipelineTelemetry` adds served-vs-quota-granted per pass, per-chunk schedule counts split **pre-delivery / no-live-trace / wasted**, and per-chunk **parked time** hooked at `LightWorkScheduler`'s park/promote transitions — the §10 q4 class that MT-2 makes structurally invisible to the stop-reason instrument. `BenchmarkController` now enables the profiler (it never did) and clears it in `OnDestroy`; the report prints **NOT MEASURED** rather than 0.0 ms when it did not run, so a silently-unprofiled capture cannot read as "scheduling is free". Guarded by **B20–B22**, each prove-red-verified to redden exactly itself; `Validate All` green at **370 baselines / 16 suites** with telemetry ambiently enabled *and* disabled. **Code-reviewed and corrected the same day, before any capture** — six measurement defects fixed while fixing them was still free (a capture on the flawed instrument would have needed re-running): the staging drain moved to its own unbudgeted slot, so `LightSchedule`'s ms stay comparable to `lightScheduleBudgetMs`; the two passes' utilisation denominators were unified onto "frames where work existed", without which lighting would have read as starved against a mesh figure counted only over frames it was allowed to serve; parked time now survives a flush-and-restart and `LightWorkScheduler.Clear()`; and B21's timing assertions now compare against measured spin durations so an editor hitch cannot redden a baseline. A seventh finding — the fail-safe promote-to-rescan gap — is deliberately **not** fixed, since that time is already carried by `ReadyCount` plus the `Quota`/`Ceiling` stops; it is documented in §10 q4 with the two other biases, all of which under-count the longest waiters. **A second review round (same day) found and fixed four more**, the two substantive ones being: the amplification buckets did not *partition* the schedules — `Rerequested` and `InFlightAtPhaseEnd` traces fell through every arm, so their quota units vanished from the accounting — now closed by an `unresolved` bucket plus a **reconciliation check** against the independently-counted quota total, with a banner on any gap; and park state moved from the trace to a **coord-keyed side table** that survives phase boundaries, because a chunk stays parked across both a re-request and a speed-tier boundary while its trace survives neither, which had been reporting **zero** parked time for exactly the §10 q4 population. Both defects shared a shape worth noting: each still printed a completely plausible number. **✅ P9-1 CAPTURED 2026-08-02** ([report](../Performance/CHUNK_PIPELINE_P9_1_ATTRIBUTION_IL2CPP_2026-08-02_BENCHMARK.md)) — five same-build IL2CPP runs, vd 10/20/26/32 at the OM-1 caps plus a vd-32 cap-48 A/B leg. **The rate identity is CONFIRMED within 4 % across a 3.2× view-distance range** (1 435–1 496 lighting schedules/s against a predicted `24 × 60 = 1 440`), the flat completion band reproduces on a fresh build (6 061–7 022 per phase), and the identity closes exactly: `1 435 ÷ 6.28 = 228.5` against a measured 228.6 chunks/s. The pipeline is **~69 % of the main thread and view-distance-invariant** — its per-second cost is fixed by the rate, so what grows with view distance is everything beside it. **§F4's model is half-confirmed and corrected**: a lighting job costs **0.15 ms to schedule + 0.18 ms to merge**, so P9-0a's single fitted 0.37 ms parameter was the *sum of both passes*, and the merge is 39 % of the ×2-cap frame growth rather than the ~70 % implied. The merge is nonetheless the largest single slot (261–288 ms/s) and the unbounded term in the spiral — at cap 48 it reached 9.4 ms/frame while the budgeted scan sat exactly on its 8 ms ceiling. **Two of the design doc's own inferences are refuted**: pre-delivery **mesh** amplification is **exactly 1.00** at every view distance (not ~3.5), and 82 % of end-to-end latency is admission wait while `lit→meshApplied` is 0.2 % — so **Option B2 (deliver-then-refine) is refuted as a throughput/visibility lever and leaves P-9** for a standalone product item. §10 q4 is answered: parking is **43–48 %** of the idle-pass `populated→lit` hop, view-distance-invariant. **§2's kill condition is NOT triggered** (scheduling passes = 32 % of the frame). **Lever order becomes B1 → C → A′**: B1 leads as the only lever that raises delivery at zero frame-time cost (target: 6.28 lighting schedules per delivered chunk, 3.9 pre-delivery), **P-3 is demoted from gating to enabler** because the schedule pass is `Quota`-bound on ~98 % of frames so cheaper items buy frame time and not one extra chunk, and A′ re-fails Q2 on a second independent build. **Next is P9-2 (Option B1)**, which starts as an investigation — P9-1 sizes the multiplier but does not show any of it is redundant, and it varies by regime (6.6–6.8 at 10 m/s generation vs 3.8–4.0 at 200 m/s loading), so it **may legitimately come back empty**. **✅ P9-2 INVESTIGATED 2026-08-02 — it did NOT come back empty; code + baselines are in, the capture is not.** Attribution (design doc §3.3b): the **edge-check cascade is essentially all of the 6.28** — 1 initial + 2 self rounds (`RemainingEdgeCheckRounds`) + 1–3 coalesced neighbour triggers (`TriggerNeighborEdgeChecks`) — and the ~1 s `PromoteAll` fail-safe is **closed as a source structurally**, since promotion sets no flag, so a promoted flag-less chunk takes `LightingScanDecision`'s `Remove` arm, is un-counted by FP-7c, and cannot spend a quota unit at all. The redundancy: `MergeCompletedLightingJob` re-arms the cascade on **`IsStable`**, which means only "no work left pending" — a condition a pass that wrote **nothing** also satisfies. Measured by a deterministic harness probe that diffs every chunk's light field around each round (5×5 grid, superflat + 12 seeded dense-canopy worlds, with an unsettled positive-control family proving the probe reports change when change exists): **production's round 2 was a no-op in 100 % of chunk-rounds in every fixture**, round 1 in **95.7 %** of the adversarial unsettled case, and the counterfactual "would have skipped a round that actually changed something" was **0 everywhere**. Fix ships **default-OFF** as `enableConvergentEdgeCheckCascade` (listed in `OverlayBenchmarkSettingsFromDisk` so a cold-cache benchmark can A/B it), expressed as the shared pure `EdgeCheckCascadeDecision` and fed by a change signal now returned from `ChunkData.ApplyJobLightMap` — compaction-aware, because a uniform-sky section reads as its level whether or not the section object survived compaction, so comparing `ChunkSection.LightData` would have produced false negatives (that trap is baselined). ⚠ **Note the probe's unit:** a "chunk-round" is a flagging wave run to quiescence, not a lighting schedule, so its no-op fraction is biased high against the per-schedule 6.28 and the two must not be quoted against each other. **Code review (same day) found six items, all addressed** — the substantive one being that *declining to spend* an edge-check round was wrong: it left converged chunks hoarding budget for their whole residency, invalidating the premise `ChunkData.ModifyVoxel`'s Bug-05 top-up rests on and arming cascades on ordinary post-generation edits legacy never armed. The decision now returns **`None` / `SpendOnly` / `SpendAndRearm`** — the round is spent exactly as legacy and only the *propagation* is conditional, which costs nothing since the flags (not the counter) buy the schedules. Guarded by **B97–B100**, each prove-red-verified to redden exactly itself; `Validate All` green at **374 baselines / 16 suites** with telemetry enabled *and* disabled. **§10 q5 is ANSWERED: large view distances ARE supported, vd 32 included, and 32+ is a future goal** — the `viewDistance = 5` default is legacy and the intended default is 12–15 — so P-9 does **not** park after P9-2, the vd 10–15 leg becomes shipping-relevant rather than a control, P-7's tuning vd (not its rank) needs revisiting, and FP-10's vd-32 5 GB peak becomes a shipping constraint. **✅ P9-2 CAPTURED, GO, SHIPPED DEFAULT-ON 2026-08-02** ([report](../Performance/CHUNK_PIPELINE_P9_2_CASCADE_IL2CPP_2026-08-02_BENCHMARK.md)) — ten same-build IL2CPP runs (a vd 10/20/26/32 × OFF/ON sweep, plus a corrected vd-32 pair at the shipping cap after the sweep was found to have inherited P9-0a's `maxLightJobsPerFrame` 48). At cap 24, vd 32, loading 200 m/s: lighting amplification **6.12 → 1.86** per delivered chunk and pre-delivery **3.82 → 1.09** — a chunk now reaches the player after essentially its initial lighting pass alone, and post-delivery correction collapses with it (11 973 → 389), so the work stopped rather than moved. Delivery **×2.12** (6 861 → 14 518), p50 e2e **3 603 → 822 ms**, and **Q1 — the visibility budget missed by 1.4–1.6× since P-8 — is MET at every view distance** (0.32× of budget at vd 32). ⭐ **The rate quota stops being the binding constraint**: `Quota`-bound frames **94.3 % → 8.3 %**, panic gate **85 % closed → fully open**, `AbandonedBeforeAdmission` 21 250 → 4 858. §3.1's `cap × 60` identity is confirmed and untouched — the pipeline simply no longer operates against it at vd 32, so **A′/P9-3 is arguably moot** and **P-3 (Option C) drops to a frame-time item rather than an enabler**. The pipeline spends **less** absolute main thread per second (731 → 630 ms/s) while delivering twice as much, i.e. **÷2.4 per delivered chunk**. **Q2 was reworded to Q2′** (frame time *per delivered chunk*) because the original was written for P-8's spend-to-admit shape and mis-scores a divisor lever — per-frame avg CPU (×1.96) and min FPS (×0.84) are now recorded, not gated, since they follow from delivering 2.1× more. **Q4 FAILS at ×1.15** (4 950 → 5 703 MB peak; native+reserved, from more resident meshes) and is **accepted as a recorded cost** — but the vd-32 memory ceiling on a memory-constrained device stays unmeasured and this raises it by 750 MB. **Q7 confirmed in-game**: chunk generation lights correctly and **RGB blocklight converges and mixes across chunk borders**, the engine's most defect-prone path. Also reproduced P9-1 §F4's frame-repacking effect on a third build, which is what invalidated the cap-48 session's frame-time axis and forced the corrected pair |   🟡   |  🟡   |   🟢    |  ✅  |  ✅  |

> **P-1 re-scope note (2026-07-02):** P-1 was written when the lighting neighborhood was gathered on
> the main thread at schedule time. P-2 Phase 1 moved that gather to worker threads, so P-1's win is
> now worker-side copy bandwidth, not main-thread schedule time. Re-evaluate it together with `LI-2`
> (section-ranged gather) — both attack the same copies on different axes; implement at most one of
> them first and re-measure before touching the other.

### Rollback flags awaiting retirement

The §8 one-toggle-revert discipline ships a proven change **default-ON behind a flag**, then a later
cleanup pass deletes the flag and its now-dead legacy leg once the change has soaked in-game. That
pass is a recurring task class, and the flags are easy to lose track of across arcs — TG-4 (4 flags,
retired 2026-07-23) and LI-2 (1 flag, retired 2026-07-24) are the worked precedents. This is the
census; retire in one atomic pass per family, not one flag at a time.

| Flag (`Settings`) | Family | Shipped | Evidence | Retire when |
|---|---|---|---|---|
| `enablePipelineTimeBudgets` | P-4 §3.4 | 2026-07-23 | [IL2CPP A/B](../Performance/CHUNK_PIPELINE_P4_BACKPRESSURE_IL2CPP_2026-07-23_BENCHMARK.md) — **GO (final)** | Soak complete. Retirement is *unblocked*, deliberately not yet done |
| `scaleBudgetCeilingsWithFpsCap` | P-4 §3.4 refinement | 2026-07-23 | [ceiling-scaling A/B](../Performance/CHUNK_PIPELINE_P4_CEILING_SCALING_IL2CPP_2026-07-23_BENCHMARK.md) — **GO (final)** | With the P-4 family |
| `enableGenerationPanicGate` | P-4 §3.5 | 2026-07-23 | same P-4 A/B | With the P-4 family |

**Not a retirement candidate — listed here so it is not mistaken for one:**
`scalePanicGateThresholdsWithResidency` (P-8, `4ea1a38e`) is **default-OFF** after its capture returned
**NO-GO** ([report](../Performance/CHUNK_PIPELINE_P8_GATE_SCALING_IL2CPP_2026-08-01_BENCHMARK.md)). Its default
*is* the legacy behaviour, so it is an opt-in experimental path rather than a rollback lever. **Do not delete it
in the P-4 retirement pass** — that would remove the retained derivation and its B19 guard, which exist to make
the re-test cheap once the `Quota` throughput ceiling moves.

**Two notes that cost a session each to learn.** (1) The P-4 harness
(`Benchmarks/P4BackpressureBenchmark.cs`) is **kept**, not deleted, when its flags go — collapsed to
a single configuration, following LI-2's FullHeight oracle and TG-4's pruned harness. (2) A rollback
flag must be listed in `SettingsManager.OverlayBenchmarkSettingsFromDisk` or it **cannot be A/B'd in
a player build at all**: benchmark mode builds a fresh `Settings` and copies only the fields that
method names, so an unlisted flag is pinned to its code default for every capture. P-8's flag is
listed; `enableGenerationPanicGate` is not, and has only ever been toggled programmatically by the
P-4 harness.

Not candidates (verified — do not re-flag these as rollback levers): `enableFarLands` and
`Clouds._useClassicPattern` are player-facing feature toggles; world-scaling `Precise64` is the
unconditional default with Classic as the opt-in; the worldgen/HUD/diagnostic bools are intentional
options.

---

## Detailed findings — Meshing & Rendering

### MR-8. Greedy meshing (coplanar quad merging)

**Observed:** The mesher emits one quad per visible voxel face. Merging coplanar, same-texture, same-lighting faces into larger quads ("greedy meshing") typically cuts opaque vertex counts by **60–90%** in natural terrain — the largest structural meshing win available, and previously absent from every design document.

**Constraints specific to this engine:**

- **Per-vertex smooth lighting** is the hard one: merged quads interpolate light across the merged area, which is wrong unless (a) merging is restricted to faces with identical corner light values (still merges large uniform areas — most of the win), or (b) lighting moves out of vertex data into a per-chunk 3D light texture sampled per-pixel (bigger refactor, also improves light quality). **Route (b) is owned by `VOLUMETRIC_AND_RAYTRACED_EFFECTS_REPORT.md` `VX-1` + `VX-8`** — do not re-derive it here.
- **⚠️ Route (b) unblocks *light*, not *ambient occlusion* (added 2026-08-09).** `VX-8` keeps AO vertex-baked, and for a hard reason recorded there: hardware trilinear filtering of a voxel-resolution volume is a product of three per-axis linear ramps, which is precisely the weighting that produces the engine's round-blob AO artifact (`SILHOUETTE_CONTACT_SHADOWS.md` finding **S2**). So AO cannot follow light into the volume, and **constraint (a) survives `VX-8` reworded as an *AO* predicate**: merge faces with identical corner AO. That still merges open floors and unobstructed walls — where the vertex count actually lives — so the bulk of the win survives.
- **Texture atlas UVs** can't tile across a merged quad. Requires `Texture2DArray` (UV.z = layer index, fragment-side `frac()` tiling) — a shader + atlas build change.
- The anisotropy quad-flip (`EmitQuadTriangles`) and AO/light diagonal logic must be re-derived for merged quads. Note it now runs **per sub-quad** on tessellated faces (`VO-9b`).
- **`SS-*` sub-cell shading is merge-neutral, and its gate partitions the face set with this item (added 2026-08-09).** `SILHOUETTE_CONTACT_SHADOWS.md` subdivides a face into N×N sub-quads whenever an occluder is within one cell of it — alarming next to a vertex-reduction item, but the two do not compete for the same faces: a face with an occluder in range is exactly a face whose corner AO is *non-uniform*, which constraint (a) cannot merge anyway. Tessellate the varying faces, merge the uniform ones. And `SS-*` does not shrink the mergeable set: its occlusion is zero beyond one cell from an occluder's silhouette, the same support today's AO has.
- Sub-chunk section stats (`MeshSectionStats`) and the visibility-culling connectivity work (`VISIBILITY_CULLING_ARCHITECTURE.md`) are unaffected — merging happens within a section.

**Recommendation:** Treat as a phased design doc of its own when picked up: Phase 1 opaque cubes with flat lighting + texture arrays; Phase 2 smooth-lighting-aware merge predicate. Capture a meshing baseline first (`Performance/README.md`).

> **Impact Analysis:**
> - **Effort:** 🔴 High — mesher core, shaders, atlas pipeline.
> - **Risk:** 🔴 High — visual regressions (lighting seams, texture tiling) are easy to introduce.
> - **Benefit:** 🟢 High — vertex/index counts drop by more than half; helps CPU meshing time, upload
>   bandwidth, GPU vertex load, and memory simultaneously.
> - **Seed/Save:** ✅ / ✅ — purely visual; voxel data unchanged.

---

## Detailed findings — Lighting

### LI-3. Ready-set scan eagerly evaluates BOTH neighbor gates for every ready chunk

*(Surfaced by the 2026-07-10 branch code review of `feat/async-lighting-validation-suite` — a cost introduced by the AS-2/HF-4 #1 `LightingScanDecision` extraction. Independently found by the LP census as `LIGHTING_PIPELINE_STATE_REFACTOR.md` **F7**, which owns the fix via **LP-6**; this entry exists so the master perf backlog lists it — details and the consolidation plan live there.)*

**Observed:** the `World.Update` lighting ready-set scan (`World.cs:1630–1631`) computes both
`AreNeighborsDataReady` *and* `AreNeighborsReadyAndLit` for **every** ready chunk on every visit to feed the pure `LightingScanDecision.EvaluateReadyChunk` call, where the pre-AS-2 code short-circuited: `AreNeighborsReadyAndLit` (the expensive gate — 8 neighbors × chunk-store lookup + in-flight probe + flag reads) ran only on the rare `NeedsEdgeCheck` arm, and neither gate ran when a job was already in flight (immediate park). During initial world load / heavy edit churn the ready set is large, so this is added cost in exactly the loop MT-2 was built to
slim down.

**Recommendation:** compute the gate booleans lazily at the call site — `neighborsReadyAndLit` only when `!jobInFlight && !needsInitialLighting && needsEdgeCheck`, `neighborsDataReady` only when an arm that reads it is reachable. `EvaluateReadyChunk` stays pure and its semantics are unchanged because each gate is only consulted on those paths (mirror the same lazy pattern in the frame simulator's `RunSchedulerPhase2` so the two call sites stay identical). LP-6 subsumes this if the gates are consolidated there first.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — call-site-only change in two mirrored callers; the shared decision is untouched.
> - **Risk:** 🟢 Low — no semantic change (gates are pure reads); guarded by the scheduler-mode
>   baselines B66–B70 + the legacy fleet.
> - **Benefit:** 🟡 Medium — O (ready-set) per frame; matters during world load and edit bursts,
>   negligible at steady state.
> - **Seed/Save:** ✅ / ✅ — scheduling-only; no lighting output or disk change.

---

## Detailed findings — World Generation

> **Context:** the generation pipeline never had a dedicated audit pass (the first two passes
> covered meshing, lighting, tick, GPU, and OOM hardening). These three items are the
> schedule-side, apply-side, and structure-side findings of the 2026-07-02 pass over
> `StandardChunkGenerator.ScheduleGeneration` → `WorldJobManager.ProcessGenerationJobs`.

### WG-1. Per-chunk Persistent generation buffers allocated and freed per chunk

**Observed:** `StandardChunkGenerator.ScheduleGeneration` (`StandardChunkGenerator.cs` ~line 351)
freshly allocates per scheduled chunk, all `Allocator.Persistent`: the 128 KB `outputMap`
(`NativeArray<uint>`, 32,768 voxels), `outputHeightMap` (512 B), `wormMask` (`NativeBitArray`, 4 KB), `caveMask` (32 KB) + `preCaveBlockIDs` (64 KB) when caves are enabled, two `NativeQueue`s (legacy mods + structure spawns), and the worm-telemetry list — ~230 KB of native alloc/free churn per generated chunk during streaming. TG-6 pooled exactly one of these (the 8 KB `ActiveVoxels`
list) and measured ~0.95 µs/chunk of main-thread schedule/release time for it; the remaining buffers are an order of magnitude more bytes through the same allocator, still unpooled — the repeated alloc/free pattern CLAUDE.md mandates pooling for.

**Recommendation:** Extend the TG-6 pattern to the fixed-size buffers: a `GenerationBufferPool`
mirroring `ChunkJobArrayPool` / `MeshOutputPool` / `ActiveVoxelListPool`, rented in
`ScheduleGeneration` and returned in `WorldJobManager.ReleaseGenerationJobData` — the terminal release helper the TG-6 double-dispose review established as the single correct release site. Reset discipline matters (the MR-6/B17 lesson): `wormMask`/`caveMask` are written sparsely and conditionally, so pooled instances must be cleared on rent or return, or stale bits carve phantom caves. Keep editor/benchmark callers on the fresh-alloc path via the same optional-pool parameter convention TG-6 added to `IChunkGenerator.ScheduleGeneration`.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — pool type + rent/return threading + reset discipline across the terminal
>   and shutdown release paths.
> - **Risk:** 🟡 Medium — native-container lifetime (the exact double-dispose class the TG-6 review
>   caught) and stale-data reuse; both have established mitigations (single terminal-release
>   helper, `ClearForReuse` + a B17-style pooled-reuse guard).
> - **Benefit:** ⚪ Low today (native, mostly off-frame, TG-6-class µs/chunk) — but the byte volume
>   multiplies ~5× under Tier A heights, so pool sizing should be height-parameterized from day one.
> - **Seed/Save:** ✅ (buffers fully rewritten per chunk once reset discipline holds) / ✅.

---

### WG-2. Main-thread section copy + per-section empty scan in `ChunkData.Populate`

**Observed:** `WorldJobManager.ProcessGenerationJobs` STAGE 1 calls `ChunkData.Populate` →
`PopulateFromFlattened` (`ChunkData.cs` ~line 335), which per generated chunk, on the main thread:
copies all 32,768 voxels from the job map into the 8 section arrays (128 KB of memcpy), then **linearly scans each section for a non-zero voxel** to decide pruning. The scan early-exits on the first non-zero, so occupied sections cost ~1 read — but every *empty* section pays the full 4,096 reads, which makes the worst case the common case (air-dominated sky sections). The comment at the copy site already flags it as optimizable. This is the generation-path sibling of P-3 (the lighting-merge main-thread scan).

**Recommendation:** The generation path already ends with a Burst pass over every voxel (`ActiveVoxelScanJob`) — extend it (or the terrain job) to emit a per-section occupancy summary (8-bit non-empty mask, or per-section nonAir counts). `PopulateFromFlattened` then skips both the copy and the scan for empty sections and drops the scan for occupied ones. Load-from-save and pool-recycle replay paths keep the current scan (the same fallback split TG-2 established). Longer term this folds into palettes (`Design/CHUNK_PALETTE_MAPPING.md`): uniform sections
should never materialize 4,096-entry arrays at all.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — job output field + populate fast path, load-path fallback kept intact.
> - **Risk:** 🟡 Medium — a wrong empty mask silently prunes real terrain; gate with a TG-2-style
>   differential (jobified summary vs full managed scan over the same finalized maps, zero diff).
> - **Benefit:** 🟡 Medium — removes up to ~32k managed-array reads plus some section copies per
>   chunk from the streaming apply path; scales with section count under Tier A.
> - **Seed/Save:** ✅ / ✅.

---

### WG-3. Structure expansion is managed, main-thread, per-mod work

**Observed:** `StandardChunkGenerator.ExpandStructure` (`StandardChunkGenerator.cs` ~line 847) is a C# `yield` iterator walking managed `CompositeStructureTemplate` / `StructureComponent`
ScriptableObjects. `ProcessGenerationJobs` STAGE 2 enumerates it per structure marker and feeds
`World.EnqueueVoxelModification` one `VoxelMod` at a time under the `maxStructureModsPerFrame`
budget. Costs, all on the main thread during streaming: an iterator state machine + enumerator per structure, cache-hostile managed template traversal, per-mod enqueue work — and when the budget exhausts, the whole generation job parks (`jobFullyProcessed = false`) and is re-visited next frame, trickling tree-dense chunks across many frames. Every other generator input was flattened into NativeArrays at `Initialize`; structure templates are the one managed survivor.

**Recommendation:** Profile first — confirm structure expansion registers on tree-dense streaming captures before paying the complexity. If it does: flatten templates at `Initialize` (component positions, block IDs, variant tables into NativeArrays — the established pattern), expand markers in a Burst job emitting a `NativeList<VoxelMod>` chained onto the generation job, and turn STAGE 2 into a bulk application. The rotation/stacking/variant selection logic and its RNG must be ported verbatim.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium → 🔴 High — template flattening + a faithful RNG port.
> - **Risk:** 🟡 Medium — expansion is deterministic worldgen; a regression changes structures.
> - **Benefit:** 🟡 Medium — removes managed expansion + the per-mod trickle from tree-dense chunk
>   streaming; situational elsewhere.
> - **Seed/Save:** ⚠️ **Seed-sensitive** — the Burst port must reproduce the exact
>   `Unity.Mathematics.Random` seed derivation and call order, or identical seeds place different
>   structures. Hard acceptance criterion: byte-identical mod stream for fixed seeds across
>   representative biomes (this is the exception in the report's seed-breaking note). / ✅.

---

## Detailed findings — Tick & Gameplay

### TG-1. Double voxel lookup + float-path cross-chunk queries in the tick loop

> **Status (2026-06-27): ⏭️ OBVIATED for the hot path by TG-4 — not worth pursuing standalone.** TG-1 named **fluid
> simulation** as its hot path ("active voxels cluster at chunk borders by nature"), and TG-4 eliminated **both** TG-1
> costs *there*: the Burst `FluidTickJob.Execute` evaluates Behave **and** Active in a **single pass** over one pre-tick
> snapshot (item 1 gone), and border voxels resolve cross-chunk reads from the **integer-indexed neighbor halo**
> (`GetStateLocal` over `PaddedVoxels`) instead of `ChunkData.GetState`'s `new Vector3` → `WorldData.GetVoxelState`
> float path (item 2 gone). Note TG-4 reached this via a *different* mechanism than TG-1 proposed (Burst job + halo,
> not "Behave returns a flag" + cached cardinal-neighbor refs).
>
> **Residual (deliberately left, negligible):** **grass** still ticks through the managed `Chunk.TickFamily`, which
> calls `BlockBehavior.Behave` then `BlockBehavior.Active` separately (item 1 — the TG-1 TODO still sits at
> `Chunk.cs:321`) and reaches cross-chunk neighbors via `ChunkData.GetState`'s float path (item 2). The same managed
> path is also the `EnableFluidBurstTick`-off fluid rollback. This is intentional: grass is **0.044 µs/voxel**
> (the reason Phase 2 was skipped), so applying TG-1's mechanism to grass alone is not worth the API churn + the
> stale-neighbor-reference pool-reset risk. If a future behavior family makes the managed path hot again, revisit
> TG-1 (or fold that family into the TG-4 job scaffolding). **Not marked ✅** — the managed two-pass + float path
> still exist; it is simply no longer worth doing as a standalone optimization.

**Observed:** Two compounding costs in the active-voxel tick path:

1. `Chunk.TickUpdate` (`Chunk.cs` ~lines 220–237) calls `BlockBehavior.Behave(...)` **and then**
   `BlockBehavior.Active(...)` for every active voxel — each re-fetches the voxel and re-probes the same neighbors. The TODO at `Chunk.cs:226` already acknowledges the duplication.
2. Every neighbor probe that crosses a chunk border goes `ChunkData.GetState` →
   `new Vector3` (float) → `WorldData.GetVoxelState` → `IsVoxelInWorld` float compares →
   `Mathf.FloorToInt` ×3 → dictionary lookup (`ChunkData.cs` ~line 840). For fluid simulation — where active voxels cluster at chunk borders by nature — this is the hot path, and it also boxes through `VoxelState?` nullables and managed `BlockType` property lookups.

**Recommendation:**

1. Make `Behave` return (or out-param) a "still active" flag so the separate `Active` pass disappears.
2. Add an integer-math cross-chunk path: `ChunkData` caches its 4 cardinal neighbor `ChunkData`
   references (invalidated on load/unload), and border probes resolve via
   `neighbor.GetVoxel(x & 15, y, z & 15)`-style integer wrapping without touching `Vector3`,
   `Mathf`, or the world dictionary.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — `BlockBehavior` API change plus a neighbor-reference lifecycle (must be
>   cleared in `ChunkData.Reset()` per pool-reset-safety rules).
> - **Risk:** 🟡 Medium — fluid behavior must be verified unchanged (fluid bugs have history here);
>   stale neighbor references after pool recycle would corrupt simulation.
> - **Benefit:** 🟢 High whenever fluids/grass are active at scale — per-tick cost drops by roughly
>   half from item 1 alone, more near borders from item 2.
> - **Seed/Save:** ✅ / ✅.

---

### TG-5. `BlockBehavior` Burst function pointers (lighter alternative to TG-4)

*(Absorbed from `CODEBASE_IMPROVEMENTS.md` §6.2.)*

> **Status (2026-06-27): ⏭️ SUPERSEDED — not needed.** TG-5 was the *lighter alternative* to be taken **if TG-4 was
> overkill**. TG-4 shipped in full (Phases 0–1 + 3 + 4a + 4b + Y-band, all default-on) with the tick now fully Burst +
> parallel and behavior byte-identical, so the function-pointer-dispatch fallback buys nothing TG-4 hasn't already
> delivered — and the tick is no longer the frame bottleneck (the lighting line is). Kept here for historical context.

**Observed/Recommendation:** If TG-4 is overkill, replace the central `switch` with a
`Unity.Burst.FunctionPointer<T>` registry indexed by voxel ID. Keeps a single active-voxel collection while decoupling behavior logic and enabling Burst-compiled dispatch.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — function-pointer initialization at Burst startup.
> - **Risk:** 🟡 Medium — mismanaged Burst function pointers hard-crash.
> - **Benefit:** 🟡 Medium — decoupling + Burst dispatch, without TG-4's parallelism win.
> - **Seed/Save:** ✅ / ✅.

**Parity guard (prerequisite):** same as TG-4 — guard the function-pointer dispatch swap with the behavior-tick harness ([BEHAVIOR_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/BEHAVIOR_VALIDATION_HARNESS_FIDELITY.md))
and the BH-D1 old-vs-new differential. Decoupling the `switch` into a registry must produce a byte-identical `VoxelMod`
stream tick-for-tick.

---

## Detailed findings — Main Thread & Miscellaneous

> **All items in this category are complete** (MT-1 … MT-6). Their detail sections are archived in
> [`../Archived/PERFORMANCE_IMPROVEMENTS_COMPLETED.md`](../Archived/PERFORMANCE_IMPROVEMENTS_COMPLETED.md);
> their rows remain in the master summary table above.

## Detailed findings — GPU & Shaders

### GS-1. Liquid shader: per-pixel procedural 3D simplex FBM

**Observed:** `LiquidCore.hlsl` evaluates Ashima-style 3D simplex noise (`snoise`, ~60+ ALU ops each) in **FBM loops per fragment**. At the High tier with dual-phase and refraction, one water pixel evaluates roughly: 2 phases × (wave FBM 4-oct + ripple FBM 4-oct + stream FBM 3-oct) plus 2 × 3-oct refraction-normal FBMs ≈ **25–30 `snoise` calls per pixel**. Lava is comparable (plus crust/spark FBMs). An ocean or lava lake covering half the screen is by far the most expensive thing the GPU does — on a midrange Android GPU this alone can blow the entire
frame budget.

The existing quality-tier keywords (`_FLUID_QUALITY_LOW/MED`, refraction opt-out) are the right mechanism and already help, but even the Low tier pays 2-oct procedural simplex per pixel, and the tier system reduces octaves rather than changing the *kind* of work.

**Recommendation (in increasing effort):**

1. **Pre-baked noise textures.** Replace procedural `snoise` FBM with 1–2 samples of a tiling, pre-baked FBM noise texture (scrolled/blended exactly like the current coordinates — the dual-phase flow-mapping logic is unchanged, only the noise *source* changes). Texture fetches are what mobile GPUs are good at; this typically cuts liquid fragment cost by 5–10×. A small 3D texture (or 2 blended 2D samples to fake the third dimension) preserves the "boiling"
   vertical animation. The bake can be generated offline via `Tools/Python/` or an editor tool.
2. **Derive refraction normals from existing results.** The two extra FBM evaluations per phase (`normal_dx`/`normal_dz` finite differences) can come from the noise texture's precomputed gradient channels (RGBA: value + xy-gradient) for free instead of 2 more FBM evaluations.
3. **Cheaper dual-phase.** With texture-based noise, consider whether the Low tier can drop to a single phase with a time-sliced texture swap, removing the 2× multiplier entirely.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — noise bake asset + shader change; tier macros stay.
> - **Risk:** 🟡 Medium — visual character of water/lava will shift slightly (tile period,
>   gradient quality); needs eyes-on comparison per tier.
> - **Benefit:** 🟢 High — largest single GPU win available; transforms the worst-case mobile frame.
> - **Seed/Save:** ✅ / ✅.

---

### GS-2. Opaque Texture required globally; scene color sampled even without refraction

**Observed:** Two compounding costs:

1. The URP asset (`Assets/settings/Rendering/VoxelEngine-URP-Asset.asset`) sets
   `m_RequireOpaqueTexture: 1` globally — URP performs a **full-screen color copy every frame**, whether or not any liquid is visible. On mobile tile-based GPUs this also forces a render-target resolve/store, one of the most expensive operations on those architectures.
2. `UberLiquidShader.shader` calls `SampleSceneColor(distortedUV)` and composites manually via
   `lerp(background, color, alpha)` **even when `_FLUID_REFRACTION_OFF` is set** — with refraction off, `distortedUV` is just the undistorted screen UV, so the manual composite is mathematically equivalent to standard hardware alpha blending and the opaque texture isn't needed at all.

**Recommendation:** When refraction is off (which should be the mobile default), switch the liquid pass to hardware alpha blending (`Blend SrcAlpha OneMinusSrcAlpha`, output alpha = the current lerp factor) inside the `_FLUID_REFRACTION_OFF` variant — no `SampleSceneColor`, no opaque-texture dependency. Then toggle `UniversalRenderPipelineAsset.supportsCameraOpaqueTexture` from
`GraphicsSettingsController` so the full-screen copy only exists when the refraction tier is active. (Note `m_OpaqueDownsampling` is already set — keep downsampled opaque texture for the refraction-on path; refracted water doesn't need full resolution.)

> **Impact Analysis:**
> - **Effort:** 🟢 Low — one shader variant + a settings hook.
> - **Risk:** 🟡 Medium — blending semantics for overlapping fluid faces must be checked (the
>   current manual composite reads pre-liquid opaque color; hardware blending composites over
>   whatever is in the framebuffer, including other transparent geometry — verify against the
>   transparent-blocks submesh ordering).
> - **Benefit:** 🟢 High on mobile — removes a per-frame full-screen copy + resolve; also a real
>   win on desktop at high resolutions.
> - **Seed/Save:** ✅ / ✅.

---

### GS-3. Voxel lighting math runs per-fragment on purely per-vertex data

**Observed:** `ApplyVoxelLightingRGB` (`VoxelLighting.hlsl`) computes 4 independent shade curves, each ending in `pow(x, 2.2)` — **4 `pow` calls per fragment** in the opaque, transparent, and liquid shaders. Every input (per-vertex light data + global uniforms) is available in the vertex shader; only the final `color * multiplier` needs the fragment stage.

**Recommendation:** Compute the sun multiplier (`sunShadow * skyColor`) and block multiplier (`half3` of the three channel shadows) in the vertex shader and interpolate them; the fragment does `col.rgb *= max(sunContrib, blockContrib)` (or interpolate the combined `max` directly — verify the visual difference across a face is acceptable; interpolating the two contributions separately and taking `max` per-pixel is the closer match). Pixels vastly outnumber vertices in voxel scenes, so this moves the `pow` chain to the cheap stage.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — shared include + V2F struct change.
> - **Risk:** 🟢 Low — minor interpolation differences across large faces; compare side-by-side
>   with the `DEBUG_LIGHTDATA` view.
> - **Benefit:** 🟡 Medium — meaningful fragment ALU reduction on mobile; small on desktop.
> - **Seed/Save:** ✅ / ✅.

---

### GS-4. Render pipeline tier audit (shadows, MSAA, render scale, shadow casting mode)

**Observed (current URP asset + code state):**

- `m_MainLightShadowsSupported: 1` with `m_ShadowDistance: 0` — shadows never *render* (distance 0), but the support flag still compiles shadow shader variants and keeps the shadow-map keyword plumbing active. If this is permanent (the voxel sky-light system replaces shadows), set supported = 0 to strip variants; if shadows are ever enabled, note that…
- `SectionRenderer` sets `ShadowCastingMode.TwoSided` on **every section** — with shadows actually on, the entire voxel world would render twice-sided into a 2048 shadow map; that needs its own tiered decision (e.g. shadows only from a small radius, or baked/none on mobile).
- `m_MSAA: 2` — MSAA on a voxel world of opaque cubes buys little; on mobile it costs bandwidth (though tilers handle it relatively well). Should be a quality-tier setting, not baked into the asset.
- `m_RenderScale: 1` — no resolution scaling hook for mobile; exposing render scale in
  `GraphicsSettingsController` is the single most effective GPU lever on phones.

**Recommendation:** Make these per-tier: a mobile URP asset (or runtime overrides via
`UniversalRenderPipelineAsset` properties) with shadows-unsupported, MSAA off/2×, render scale exposed as a setting, plus the GS-2 opaque-texture toggle. Desktop keeps the current values.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — settings/asset configuration, no engine code.
> - **Risk:** 🟢 Low.
> - **Benefit:** 🟡 Medium — variant stripping (build size + load time), bandwidth savings, and a
>   render-scale escape hatch on weak GPUs.
> - **Seed/Save:** ✅ / ✅.

---

### GS-5. Section occlusion culling — underground sections render despite being sealed

**Observed:** Fully empty sky sections are already skipped (no mesh, GameObject disabled), but **every meshed subsurface section renders** even when completely sealed from the camera by solid terrain — the "underground overdraw" problem. While walking on the surface above cave systems (or being inside one), the majority of rendered sections are invisible. A previous count-based attempt ("render only if connected to the section above, relative to the player") caused major rendering corruption and was removed — scalar air/opaque counts cannot represent
connectivity topology, so any count heuristic both over-culls (holes) and under-culls. The sound solution is graph-connectivity culling per `VISIBILITY_CULLING_ARCHITECTURE.md`, whose Phase 0 prerequisites (section renderers, `nonAirCount`/`opaqueCount`, empty-section skipping) are complete; Phases 1–3 are open.

**Recommendation:** Implement the design doc's connectivity-mask + BFS architecture **with the corrections in its new §7** (added alongside this entry): accumulated entry-face sets instead of single-entry visited marks, Checchi direction restriction, `forceRenderingOff` ownership split from `SetActive` (the likely cause of the old corruption — ✅ **shipped 2026-07-25 as MP-5**, so the culler now lands on an existing seam), mask publication synchronized with mesh apply, conservative defaults, and a position-only PVS without per-step frustum checks.
Expected win: the largest single rendering-side improvement available (draw calls, vertex work, Unity culling overhead scale with loaded sections), growing further with taller worlds (`WORLD_SCALING_ANALYSIS.md` Tier A) and carrying over unchanged to cubic chunks (Tier C).

> **Impact Analysis:**
> - **Effort:** 🔴 High — dedicated system (in-job flood fill + visibility manager + ownership
>   refactor), though cleanly phased in the design doc.
> - **Risk:** 🟡 Medium — over-culling bugs are visible holes; §7's rules + debug overlay make
>   them testable. Conservative failure direction (over-render) is designed in.
> - **Benefit:** 🟢 High — most subsurface sections stop rendering in normal play.
> - **Seed/Save:** ✅ / ✅ — masks are derived data, never persisted.

---

### GS-6. Per-section GameObject + MeshRenderer submission — BatchRendererGroup conversion

*(Surfaced by the 2026-07-02 third-pass audit — the structural complement to GS-5.)*

**Observed:** Every 16³ section is a pooled GameObject with its own `MeshFilter` + `MeshRenderer`
(`SectionRenderer`). At normal view distances that is thousands of live renderers, each paying Unity's per-renderer overhead every frame: main-thread culling bookkeeping, transform/hierarchy management, and per-object draw submission. GS-5 reduces *how many* sections render; this item changes *what each section costs* to exist and be submitted. The two compound — but they also interact (see below).

**Recommendation:** Long-horizon only; needs its own design doc when picked up. Convert section rendering to `BatchRendererGroup` (BRG): meshes registered with a batch group, per-section matrices and visibility handled in BRG's culling callback instead of per-GameObject renderers. **Ordering interaction with GS-5:** BRG has no `forceRenderingOff` — visibility is expressed in the culling callback's index output. Design the GS-5 `VisibilityManager` to *output a visible-section set* consumed by a thin, swappable presentation layer (today:
`forceRenderingOff` toggles; under BRG: the culling callback), so the culler survives a later BRG conversion unchanged. A matching note lives in `VISIBILITY_CULLING_ARCHITECTURE.md` §8.

> **Impact Analysis:**
> - **Effort:** 🔴 High — replaces the renderer layer (`SectionRenderer`, pooling, material paths).
> - **Risk:** 🔴 High — bespoke rendering path; per-platform validation, and every
>   renderer-adjacent behavior (mesh upload, bounds, layers, shadow-casting mode) must be
>   re-derived.
> - **Benefit:** 🟡 Medium on desktop today → 🟢 High at scale (thousands of sections, weak CPUs,
>   and any Tier A height increase that multiplies section counts).
> - **Seed/Save:** ✅ / ✅.

---

## Detailed findings — CPU-Starved Device / OOM Hardening

> **Context:** on a fast desktop (i9-9900K class), production and consumption rates stay roughly
> balanced and the documented §3 weaknesses rarely bite. On CPU-starved hardware (midrange Android),
> the same constants produce the observed failure: fast movement schedules work faster than it can
> drain, every queue grows, pinned chunks can't unload, and the OS kills the process out-of-memory.
> `P-4` (pipeline doc §3) addresses the *production* side. These items add the missing *scaling,
> ceiling, and emergency* layers. All three should be considered prerequisites for shipping on
> Android.

### OM-1. All budgets and caps are desktop-tuned absolute constants

> **IMPLEMENTED (2026-06-27, pending in-game/player verification) — full design + as-built:**
> [`OM1_DEVICE_CALIBRATION.md`](./OM1_DEVICE_CALIBRATION.md). First-launch calibration (specs → memory
> caps, micro-benchmark → throughput, reference-anchored) written to `settings.json`, plus enablers **A**
> (`ResourceLoader.LoadBlockDatabase()`) and **B** (shared runtime `JobDataManagerFactory`). Desktop
> reproduces the historical 10 / 32 / 20 / 512 exactly. The follow-up structural cleanup **C** (decoupling
> `World.blockDatabase`) is split out into [`BLOCK_DATABASE_DECOUPLING.md`](../Architecture/BLOCK_DATABASE_DECOUPLING.md).

**Observed:** Every throughput and retention knob is a fixed number chosen on desktop hardware:
`maxLightJobsPerFrame = 32`, `maxMeshRebuildsPerFrame = 10`, in-flight mesh cap `20` (hardcoded in
`World.Update`), `ChunkJobArrayPool` retention `512` buffers/type (**≈ 96 MB absolute worst case**
— sized for desktop concurrency per the pipeline doc §1.1 notes), pool prune targets, and default view/load distances. None of them consult the device. A phone with 3–4 GB of RAM and 4 slow cores gets the same in-flight memory envelope as a 64 GB desktop — and *lower* caps are actually needed there twice over: less RAM to hold the backlog **and** fewer cores to drain it.

**Recommendation:** Introduce a device-tier profile resolved once at startup from
`SystemInfo.systemMemorySize`, `SystemInfo.processorCount`, and platform: it scales the per-frame job budgets, the in-flight job caps, `ChunkJobArrayPool` retention (e.g. `min(512, f(memory))`), pool prune targets, and clamps the maximum selectable view distance. Per-frame budgets should also become time-based rather than count-based where P-4 lands (the two compose: tier sets the budget, P-4 enforces it per-second instead of per-frame).

> **Impact Analysis:**
> - **Effort:** 🟢 Low — a profile struct + plumbing into existing constants.
> - **Risk:** 🟢 Low — conservative tiers can only under-use fast devices until tuned.
> - **Benefit:** 🟢 High on mobile — shrinks every queue and pool ceiling to what the device can
>   actually drain and hold.
> - **Seed/Save:** ✅ / ✅.

---

### OM-2. No memory-pressure response: `Application.lowMemory` unused, no resident budget

**Observed:** Nothing in the codebase subscribes to `Application.lowMemory` (Unity's callback for the OS memory-pressure signal on Android/iOS), and no system tracks total resident chunk memory. The engine's only ceiling is "whatever the unloader manages to free" — and the unloader is exactly what the documented §3.3 pinning problem disables under load. When the backlog wins, there is no last line of defense between "degraded" and "killed by the OS".

**Recommendation:** Two layers:

1. **Resident-chunk budget (proactive).** Track loaded `ChunkData` count (a cheap proxy for memory; optionally refine with per-chunk section counts) against a tier-derived budget (OM-1). Crossing the budget triggers the §3.5 panic gate *keyed on memory, not queue length*: stop scheduling new generation, shrink the effective load radius, and let consumption catch up. This generalizes the pipeline doc's panic gate into the resource that actually kills the process.
2. **`Application.lowMemory` handler (reactive).** On the OS signal: halt generation scheduling, force the unload pass with a reduced radius (honoring pipeline invariants — prefer the §3.3 fix of persisting pending light columns so pinned chunks become unloadable), set all pool retention targets to zero and prune immediately, then `GC.Collect()` + `Resources.UnloadUnusedAssets()`. ⚠ Force-unload paths MUST go through the existing unload machinery — bypassing the
   `wouldStrandNeighbor` / pending-lighting checks trades an OOM crash for a lighting deadlock (see `chunk-lifecycle` skill).

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — the budget/gate is simple; the emergency unload must respect pipeline
>   invariants, which is where the care goes.
> - **Risk:** 🟡 Medium — interacts with the deadlock-prone unload gates; test with the benchmark
>   stress run on a memory-capped device/emulator.
> - **Benefit:** 🟢 High — converts the observed hard crash into a visible degradation (shorter
>   view distance, slower streaming).
> - **Seed/Save:** ✅ / ✅.

---

### OM-3. Unbounded concurrent chunk saves on mass unload

**Observed:** `World.UnloadChunks` fires `StorageManager.SaveChunkAsync(data, …)` for every unloaded chunk (`World.cs` ~line 1986; same pattern at ~3135), each of which snapshots the chunk and queues a `Task.Run` to the ThreadPool. During fast movement, a single unload pass can launch **hundreds of concurrent save tasks**: each holds a pooled snapshot until its turn (a memory spike proportional to the burst, on top of the already-stressed heap), and the ThreadPool spawns/queues threads that compete with Unity's job workers for the few cores a CPU-starved
device has — slowing down exactly the lighting/meshing drain that the backlog needs.

**Recommendation:** Replace fire-and-forget saves with a **bounded producer-consumer save queue**:
a fixed small number of writer workers (1–2; region files are lock-serialized anyway per
`REGION_FILE_CONCURRENCY.md`, so more writers mostly just contend) consuming from a channel with a bounded snapshot count. When the bound is hit, defer the unload of further chunks to the next frame (natural backpressure — the chunk simply stays loaded a little longer) rather than queueing unboundedly. Shutdown flushes the queue synchronously (the existing cancellation-token path already models this).

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — a save-queue service in `ChunkStorageManager` + unload-path change.
> - **Risk:** 🟡 Medium — must not lose saves on quit/crash (flush ordering), and deferred unload
>   must not fight the OM-2 emergency path (emergency mode should raise the writer count/priority,
>   not bypass the queue).
> - **Benefit:** 🟢 High on weak CPUs — caps the unload-burst memory spike and stops ThreadPool
>   oversubscription from starving the job system.
> - **Seed/Save:** ✅ / ✅ — same bytes written, only scheduling changes.

---

## Detailed findings — Serialization & Save/Load

> **Context:** the disk **read** path had never been audited (OM-3 covers only the save- *burst*
> scheduling side; MT-6 was a naming fix). These items are the 2026-07-02 fourth-pass findings over
> `RegionFile` → `ChunkSerializer` → `ChunkStorageManager` → `World.LoadOrGenerateChunk`. All edits
> here are byte-layout-neutral — but this is save-system code, so the `serialization-safety` rules
> apply to every change regardless.

### SL-1. Per-chunk managed allocations on the load/save path

**Observed:** Each streamed-in chunk allocates on the load path: the compressed payload `byte[]`
(`RegionFile.LoadChunkData`, `RegionFile.cs` ~line 147 — typically tens of KB), a 4-byte length header array, a 512 B `reader.ReadBytes(...)` heightmap array (`ChunkSerializer.cs` ~line 209 — inconsistent with the sections, which correctly stream into pooled arrays via `ReadBulkData`),
`Enum.IsDefined` reflection per load (`RegionFile.cs` ~line 139), plus per-load decompression-stream/`BinaryReader` wrapper objects and the `Task.Run` closure. Each saved chunk allocates: two `BitConverter.GetBytes` arrays, a zero `pad` array up to ~4 KB (`RegionFile.cs` ~line 231), a `new ChunkSection[8]` snapshot array (`WriteChunkInternal`), and
`MemoryStream`/`BinaryWriter`/compression-stream wrappers. The `SerializationBufferPool` exists but covers only the serialize-side output buffer. All of this runs on ThreadPool threads, but GC is process-wide — the allocation rate scales with streaming speed and contributes to the collections that pause the main thread.

**Recommendation:** Extend `SerializationBufferPool` with a length-aware rent for the read payload (`Deserialize` already takes `ReadOnlySpan<byte>`, so a pooled oversized buffer slices for free); read the heightmap via the existing `ReadBulkData` span path into a pooled/stack buffer; replace
`Enum.IsDefined` with a range check against the known enum values; keep a static zero-pad buffer; write the two 4-byte headers via stackalloc spans (`Stream.Write(ReadOnlySpan<byte>)`).

> **Corroborated 2026-08-05** by [`THIRD_PARTY_LIBRARY_IDEAS_REPORT.md`](THIRD_PARTY_LIBRARY_IDEAS_REPORT.md)
> §TP-1: an independent evaluation of Cysharp's `NativeMemoryArray` (`IBufferWriter<byte>` /
> pooled-buffer model) arrives at this same design, and confirms it needs no third-party package.
> That report also re-verified the load-side allocations at commit `1a5fc107`
> (`RegionFile.cs:137,167`) and the save side's existing pooling
> (`ChunkStorageManager.cs:230,292,687`). Adjacent: its `TP-4` covers the `async Task`
> state-machine/`Task` allocation this item touches on via "the `Task.Run` closure" — action both
> in one pass over `ChunkStorageManager` if scheduled.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — mechanical, but spread across three files and both directions.
> - **Risk:** 🟡 Medium — save-system code (bytes must stay identical — verify with a
>   round-trip diff of a saved world before/after); pooled-buffer lifetime across `Task.Run`.
> - **Benefit:** 🟡 Medium — removes the dominant steady-state GC source outside the main thread
>   during streaming; biggest on weak devices where GC pauses are longest.
> - **Seed/Save:** ✅ / ✅ — identical bytes, allocation strategy only.

---

### SL-2. Disk-load apply path runs unbudgeted on the main thread

**Observed:** After `await StorageManager.LoadChunkAsync(...)`, the continuation of
`World.LoadOrGenerateChunk` (`World.cs` ~lines 779–941) runs on the main thread and performs, per loaded chunk: `PopulateFromSave` (section ownership transfer + light-queue re-enqueue),
`OnDataPopulated` (the TG-2 bitmask scan — up to 32k reads on this path by design), pending-mod replay, pending-blocklight replay, a `new HashSet<Vector2Int>` for restored lighting columns (the generation twin in `ProcessGenerationJobs` uses `HashSetPool` — this path doesn't), and — when neighbors are ready — `RecalculateSunLightLight()`, a full 16×16-column sunlight seed walk. **There is no per-frame budget:** every load whose I/O completes gets its continuation the same frame. The generation path drains through `ProcessGenerationJobs` under
`maxStructureModsPerFrame`; the load path has no equivalent, so a fast flight over saved terrain produces uncapped multi-chunk apply bursts in single frames.

**Recommendation:** Instead of applying in the continuation, push loaded `ChunkData` into a completion queue drained by a budgeted per-frame pump (mirror `ProcessGenerationJobs`, which already handles the identical staging steps for generated chunks — potential to share the code). Pool the lighting-columns `HashSet` while there. ⚠ The apply steps fire pipeline events (`PromoteNeighborhood`, staging callbacks) — respect the flag-pairing invariants (`chunk-lifecycle` skill) when moving them.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — a queue + pump; the steps themselves move verbatim.
> - **Risk:** 🟡 Medium — pipeline-adjacent (deferred apply changes when neighbor-readiness flips);
>   the unload-during-await guard at `World.cs:781` must carry over to the queued form.
> - **Benefit:** 🟡 Medium — converts load-burst frame spikes into bounded per-frame work, exactly
>   like the generation side already does; most visible when re-visiting saved terrain fast.
> - **Seed/Save:** ✅ / ✅.

---

### SL-3. `SaveChunkAsync` snapshots up to ~190 KB per chunk on the main thread

**Observed:** `ChunkStorageManager.CreateSerializationSnapshot` (`ChunkStorageManager.cs` ~line 214)
runs on the calling (main) thread before each async save: per non-null section it rents a pooled section and copies 16 KB of voxels plus (for non-compact sections) 8 KB of LightData — up to
~190 KB of memcpy per chunk — plus both BFS queues under lock. During a mass-unload burst this multiplies by OM-3's unbounded save count: hundreds of snapshots in one frame, each also renting pooled sections that stay checked out until the ThreadPool worker finishes.

**Recommendation:** Solve together with OM-3's bounded save queue: enqueue the *chunk reference*
and take the snapshot at **dequeue** time inside the bounded writer's main-thread slot (a few per frame), so both the memcpy and the pooled-section retention are capped by the queue bound instead of the unload burst size. Independent extra: skip the LightData copy for compact sections is already implemented — the remaining copy is voxels, which a dirty-section mask (sections unchanged since load need no save at all) would shrink further; that needs per-section dirty tracking and should be its own follow-up if profiling justifies it.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — folds into the OM-3 implementation; snapshot-at-dequeue needs a
>   "chunk still loaded & unchanged" revalidation.
> - **Risk:** 🟡 Medium — a chunk can be modified between unload-request and snapshot; the dequeue
>   slot must snapshot the *current* state (which is also more correct than today's
>   frozen-at-burst state).
> - **Benefit:** 🟡 Medium — caps the unload-burst main-thread memcpy and pool pressure; pairs with
>   OM-3's memory-spike cap.
> - **Seed/Save:** ✅ / ✅ — same bytes, taken later.

---

### SL-4. Whole-file region lock serializes chunk loads behind saves

**Observed:** All `RegionFile` reads and writes share one `lock (_fileLock)`
(`RegionFile.cs` ~line 25 — the TODO there already names the problem): a chunk load stalls behind any in-flight save to the same region, and concurrent loads of neighboring chunks (which cluster in the same region file by construction) serialize each other. During streaming-while-saving the read path — which gameplay is waiting on — queues behind write I/O.

**Recommendation:** The full analysis and the recommended design (concurrent reads via
`System.IO.RandomAccess` stateless offset reads or a `FileStream` pool + single-writer discipline, with the metadata tables under an exclusive lock) already exists in **[`REGION_FILE_CONCURRENCY.md`](REGION_FILE_CONCURRENCY.md)** — this entry tracks it in the master backlog. Implement the hybrid (§3 of that doc) or `RandomAccess` (§4) variant; keep every
`_offsets`/`_sectorUsage` mutation exclusive.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — the read side is a contained change; the invariants are documented.
> - **Risk:** 🔴 High — concurrency bugs here corrupt saves; the doc's §"Critical Requirements"
>   (metadata sync, resize safety, atomic offset-table update) are hard gates, and a
>   corruption-focused stress test (parallel load/save hammering one region) must exist first.
> - **Benefit:** 🟡 Medium — removes load-behind-save stalls during streaming; compounds with SL-2
>   (budgeted apply) and OM-3 (bounded writers, which also shrink the write side of the contention).
> - **Seed/Save:** ✅ / ✅ — same bytes; only lock granularity changes.

---

## Detailed findings — Voxel Queries, Interaction & Physics

> **Context:** every per-frame gameplay consumer — the physics solver, the interaction ray, the
> placement probe, pending-mod application, and the managed grass tick (TG-1's residual) — funnels
> through one query API. TG-1/TG-4 fixed this *for the fluid tick* by bypassing it; the API itself
> and its remaining consumers were never audited until this fourth pass.

### VQ-4. Single AABB per block type cannot express stairs / L-shapes

**Observed:** `BlockCollisionBounds` is one AABB. `SUB_VOXEL_COLLISION_SYSTEM.md` §7 scopes this
deliberately ("Phase 6 explicitly targets rectangular sub-blocks only — half-slabs, quarter-slabs,
pillars") and names the consequences: **stairs** need 2 AABBs (bottom tread + top tread), **L-shapes** 2+,
and **wedges** are not representable at all (the diagonal needs OBB or triangle queries — the doc's
accepted fallback is an oversized AABB). Two further limits recorded there: collision shape is per *block
type*, so neighbor-dependent shapes (fence posts connecting) would need runtime computation, and
mesh-based collision is explicitly rejected as incompatible with the per-frame AABB-overlap pattern at
voxel density.

**Recommendation:** the §7 sketch is `CompoundCollisionBounds` holding a `NativeArray<BlockCollisionBounds>`.
Both consumers generalize cheaply once the data model exists — physics aggregates penetration across the
box list on the queried axis (§3.3's "largest absolute correction" rule already aggregates across *blocks*,
so extending to boxes-within-a-block is the same fold), and VQ-3's ray narrow phase takes the nearest
`tmin` among the boxes, with cell ordering still guaranteed. The cost sits in the data model and its
authoring: `BlockType` serialization, the Block Editor UI, the `MatchVisualMesh` derivation, and the debug
visualizer. **Not a prerequisite for VQ-3** — VQ-3 ships on the single-AABB model and picks up compound
shapes for free when this lands.

> **Impact Analysis:**
> - **Effort:** 🔴 High — data model + editor authoring + both query paths; multi-session.
> - **Risk:** 🟡 Medium — touches the physics hot path and block-asset serialization; needed the physics suite
>   (`NS-4`) in place first, which ✅ **shipped 2026-08-03** (`Minecraft Clone/Dev/Validate Physics Solver`). Its
>   sub-voxel baselines `B10`–`B13` are the single-AABB behavior compound bounds must not regress.
> - **Benefit:** ⚪ No frame-time change — unlocks stairs/L-shapes as buildable blocks.
> - **Seed/Save:** ✅ / ✅ — `BlockDatabase.asset` is a ScriptableObject; adding a box list is a Unity
>   serialization change, **not** a world-save format change, so no AOT migration is required.

---

## Detailed findings — Startup & World Load

> **Context:** MT-4/MT-5 fixed two specific startup allocations and OM-1 added device calibration,
> but the world-load coroutine (`World.cs` STEP 2/3 + `ForceCompleteDataJobsCoroutine`) was never
> audited end-to-end. The existing per-phase stopwatch instrumentation is good — keep it; these two
> items are about *throughput*, not measurement.

### SU-1. Loading screen throttled by gameplay-tuned per-frame budgets

**Observed:** The blocking startup phases run through the same per-frame budgets that protect gameplay frame time: `ForceCompleteDataJobsCoroutine` PHASE 1 yields a frame per sweep with
`ProcessGenerationJobs` bounded by `maxStructureModsPerFrame`, and after STEP 3 hands off to
`Update()`, the initial *meshing* wave drains at `maxMeshRebuildsPerFrame` (10) and the in-flight mesh cap (20) — budgets tuned to preserve 60 FPS for a player who, at this moment, is looking at a loading screen. Nothing during the load screen needs frame-rate protection; the budgets purely stretch time-to-playable.

**Recommendation:** Introduce a loading-mode budget multiplier (e.g. ×4–8 on the per-frame counts, or switch to a time-sliced ~100 ms/frame budget) active while `_isWorldLoaded == false`, reverting on handoff. OM-1's device tier supplies the safe ceiling (a phone's loading mode is smaller than a desktop's). Keep the safety-break iteration caps — scale them with the multiplier so the timeout semantics don't tighten.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — a multiplier read at the existing budget sites.
> - **Risk:** 🟡 Medium — bigger bursts stress the same queues P-4 wants to bound; the lighting
>   fail-safes and safety breaks must scale with the multiplier, not race it.
> - **Benefit:** 🟡 Medium — directly cuts time-to-playable, the most user-visible startup metric.
> - **Seed/Save:** ✅ / ✅.

---

### SU-2. Initial load schedules generation + disk loads for the whole radius at once

**Observed:** STEP 2 (`World.cs` ~lines 630–665) fires `LoadOrGenerateChunk` for every chunk in the `(initialLoadRadius + 1)` square simultaneously: each disk miss immediately calls
`JobManager.ScheduleGeneration` — there is no in-flight cap on this path — so a radius-10 start allocates ~440+ concurrent `GenerationJobData` buffer sets (~230 KB each per WG-1: ≈ **~100 MB of native buffers live at once**), and each disk hit spawns a ThreadPool load task in the same burst (the read-side mirror of OM-3's write burst). On memory-tight devices the startup burst is the first OOM opportunity, before streaming ever begins.

**Recommendation:** Schedule the initial wave ring-by-ring (inner rings first — they're also the ones `chunksToWaitFor` blocks on) with a bounded in-flight count. P-4's in-flight caps give this for free if implemented globally — implement SU-2 as "P-4's caps also apply during startup" rather than a separate mechanism, sized by the OM-1 tier and raised by SU-1's loading-mode multiplier. WG-1's pooling then bounds the buffer memory to the cap × per-chunk size.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — folds into P-4; standalone ring scheduling is also simple.
> - **Risk:** 🟡 Medium — ordering interacts with the lighting-neighbor gates (the +1 buffer ring
>   must still land before the wait ring finishes lighting); the startup coroutine's convergence
>   loop already tolerates arbitrary completion order.
> - **Benefit:** 🟡 Medium — caps startup native-memory and ThreadPool bursts; prerequisite-grade
>   on mobile (pairs with OM-1/OM-2).
> - **Seed/Save:** ✅ / ✅.

---

## Detailed findings — Debug Tooling

> **Baseline note (what is already right — keep these patterns):** `ChunkBorderVisualizer` builds
> **one static shared mesh** for all chunks (submesh-split topologies, uploaded + non-readable) — the
> model citizen of this section. `TerrainGenDebugOverlay` time-slices its minimap regeneration
> (512 px/frame) and early-outs when inactive. `VoxelVisualizer` meshes in a Burst job with pooled
> `VisualizerChunkData` GameObjects. `DebugScreen` post-MT-3 is zero-alloc with mode-gated
> components, throttled text/infrequent-data refresh, and is fully `SetActive(false)` when hidden.
> The findings below are the gaps left around those good bones. Note for GS-5: the culled-section
> wireframe overlay its §8 verification plan calls for should be built on this system — DT-1/DT-2
> are worth landing first so that overlay is usable at full view distance.

### DT-1. Debug visualization refresh has no per-frame budget

**Observed:** Switching `visualizationMode` queues **every active chunk** for visualization (`World.HandleVisualization`, `World.cs` ~line 2734), and the processing loop (~line 2767) drains **all ready chunks in a single frame**: per chunk, a full section scan (`Sunlight`/`Blocklight`/
`FluidLevel` visit every voxel of every non-empty section and insert every lit/non-air voxel into a
`Dictionary<Vector3Int, Color>` — thousands of entries per chunk), then the DT-2 conversion + job schedule; `VoxelVisualizer.LateUpdate` then completes and applies every finished mesh, also unbudgeted. At a few hundred active chunks the toggle is a multi-hundred-ms hitch. Worse, **while a mode is active** every voxel modification re-queues the chunk plus border neighbors (`World.cs` ~line 1853) for a *full rescan* — an ocean flood with the FluidLevel overlay on re-scans the entire flood front every tick batch, precisely when you're trying to watch it.

**Recommendation:** Drain the update set through a small per-frame budget (K chunks/frame, nearest-player first — the `MeshBuildQueue` pattern at debug scale), and rate-limit re-visualization of the same chunk (minimum interval, e.g. 250 ms) so tick-driven churn coalesces instead of rescanning per edit. Apply the same budget to the `LateUpdate` apply loop.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — a counter + interval check around existing loops.
> - **Risk:** 🟢 Low — debug-only; slightly stale overlays are acceptable by design (the readiness
>   gate already skips chunks mid-lighting).
> - **Benefit:** ⚪ — but converts the overlay from "unusable during heavy simulation" to a real
>   diagnostic tool for exactly those scenarios (fluid floods, lighting waves).
> - **Seed/Save:** ✅ / ✅.

---

### DT-2. `VisualizerChunkData` per-update native churn and apply-path allocations

**Observed:** Every chunk visualization update allocates **eight `Allocator.Persistent`
containers** (5 `NativeHashMap` + 3 `NativeList`, `VisualizerChunkData.PrepareJobData`) and disposes them after apply — the exact alloc/free-per-use pattern MR-6/TG-6/WG-1 eliminate elsewhere, at ~N-chunks-per-refresh frequency under DT-1's churn. The apply path adds:
`Triangles.AsArray().ToArray()` — a **managed index array per apply** (`VisualizerChunkData.cs`
~line 138; `SetIndices`/`SetIndexBufferData` accept the `NativeArray` directly) — and
`RecalculateBounds()` per apply despite the constant 16×128×16 chunk cell (the MR-4 twin). Finally,
`VoxelVisualizer.UpdateChunkVisualization` (~line 127) calls `JobHandle.Complete()` on re-entry — a synchronous stall whenever a chunk is re-visualized while its previous job is still running (DT-1's churn makes that common).

**Recommendation:** Retain the containers across updates on the pooled `VisualizerChunkData`
(allocate once, `Clear()` per use — capacity survives; dispose only in `Destroy()`, per the pool-reset-safety rules for native containers). Replace `ToArray()` with
`_mesh.SetIndices(Triangles.AsArray(), MeshTopology.Triangles, 0)`, and assign the constant chunk bounds instead of recalculating. On re-entry, skip-and-requeue instead of blocking on the in-flight job.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — established patterns, one class.
> - **Risk:** 🟢 Low — debug-only; retained containers must follow pool-reset-safety (clear on
>   reuse, dispose in `Destroy()`).
> - **Benefit:** ⚪ — removes native churn + GC from active-overlay sessions so captures taken with
>   an overlay up stay representative.
> - **Seed/Save:** ✅ / ✅.

---

### DT-3. Visualization update-set fed on every voxel edit even when disabled

**Observed:** The voxel-modification path calls `AddChunksToUpdateVisualization` unconditionally (`World.cs` ~lines 1853–1859) — including when `visualizationMode == None`, which is every frame of normal play. The `_chunksToUpdateVisualization` set only drains while a mode is active, so during normal play it just accumulates (a `HashSet` op per modified chunk per tick batch on the hot modification path, plus growth to every-chunk-ever-touched, including long-unloaded coords that the next mode activation then processes as dead lookups).

**Recommendation:** Gate the adds on `visualizationMode != None` (one branch — the mode-switch handler already queues all active chunks, so nothing is lost while disabled) and clear the set when switching to `None`.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — a guard + a `Clear()`.
> - **Risk:** 🟢 Low.
> - **Benefit:** ⚪ — makes the disabled debug stack genuinely zero-cost on the modification hot
>   path (fluid ticks), and keeps stale coords out of the first activation.
> - **Seed/Save:** ✅ / ✅.

---

### DT-4. Debug HUD/overlay allocation leftovers post-MT-3

**Observed:** MT-3 made the `DebugScreen` text refresh zero-alloc, but three neighbors missed the pass: (1) `DebugScreen.HandleNewMetrics` allocates two temp `float[]`s per metrics sample (`new[] { snapshot.CpuTimeMs, ... }`, ~20 Hz while the perf panel is visible — allocations that appear **in the GC graph being displayed**); (2) `GraphRenderer` label refreshes go through
`string.Format(yFormat, …)` / `string.Format(xFormat, …)` per label (`GraphRenderer.cs` lines 235/258/311/334); (3) `TerrainGenDebugOverlay.OnGUI` builds interpolated strings per IMGUI event (layout + repaint ≥2×/frame while active) for its ~10 labels. Related always-on note:
`PerformanceMonitor` samples its phase stopwatches every frame regardless of HUD visibility — **this is deliberate and must stay**: the history ring buffer is what makes a hitch that happened *while the HUD was closed* still visible when it is opened afterwards (`SyncGraphsWithHistory` →
`InjectHistory`). Cost is ~µs/frame, accepted by design — do not gate it on HUD visibility.

**Recommendation:** Give `GraphRenderer.AddSamples` a fixed-arity overload (or a reused sample buffer); route graph labels through the shared `StringBuilderFormat` helpers MT-3 created (and only on value change — grid labels rarely change); convert the overlay's static labels to cached strings

+ `StringBuilderFormat` for the dynamic ones (or migrate the panel off IMGUI onto the DebugScreen's TMP stack). `PerformanceMonitor`'s always-on sampling is out of scope (deliberate, see above).

> **Impact Analysis:**
> - **Effort:** 🟢 Low — MT-3's helpers already exist; this is finishing the sweep.
> - **Risk:** 🟢 Low.
> - **Benefit:** ⚪ — the perf HUD stops polluting its own GC metric; overlay sessions stop adding
>   IMGUI noise to captures.
> - **Seed/Save:** ✅ / ✅.

---

## Detailed findings — Editor Tooling (WorldTools)

> **Context:** these tools drive the *production* Burst jobs (generation, `NeighborhoodLightingJob`,
> `MeshGenerationJob`) plus managed preview paths of their own — and the managed halves run under
> editor Mono, with no IL2CPP to soften them. The audit's parity scoreboard is in the sixth-pass
> audit note at the top of this report. What is already right and worth protecting:
> `ChunkPreview3DWindow.Rendering` shares `SectionRenderer.Layout` (MR-2) with an explicit
> anti-drift comment; `EditorChunkPipelineRunner.ScheduleLighting` mirrors P-2 Phase 1's
> worker-thread halo gather (also commented); `WorldGenPreviewWindow` debounces regeneration
> (`EditorDebounceTimer`) and its Noise Channels / World Blending tabs render through parallel
> Burst jobs (`NoisePreviewJob`, `WorldBlendingPreviewJob`) into RGBA32 textures — the pattern
> ET-1 asks the Cross-Section tab to adopt.

### ET-1. Cross-Section preview evaluates terrain columns in serial managed code

**Observed:** `WorldGenPreviewWindow.CrossSection`'s `GenerateThreePanelPreview` evaluates every column of up to three panels via the managed `EvaluateColumn` (`WorldGenPreviewWindow.CrossSection.cs`
~line 1068) — serial, on the main thread, span up to 2048 columns × 128 voxels each, per panel, per regeneration (debounced to 0.1 s, so effectively per slider tick with live update on). Per-column managed allocations compound it (`new ushort[128]` per column, `new byte[128]`×2 with the cave filter, a `Color[span×128]` per panel — 16 B/pixel), and the result goes through the slow
`SetPixels(Color[])` path. The sibling tabs already solved this: `NoisePreviewJob` /
`WorldBlendingPreviewJob` are `IJobParallelFor` Burst jobs writing RGBA32. At X512+ the Cross-Section tab visibly freezes the editor per regenerate; higher resolutions are seconds.

**Recommendation:** Port the column evaluation to an `IJobParallelFor` over columns (the input structs — `CrossSectionNativeData`, `FastNoiseLite`, `BurstSpline`, `BiomeBlender` — are already Burst-compatible; the worm masks are already `NativeBitArray`), write `Color32` into a
`NativeArray` uploaded via `LoadRawTextureData`, and keep the flora/crosshair annotations as a managed post-pass. Best implemented **on top of ET-2's shared evaluator** so the port doesn't duplicate the logic a third time.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — the job pattern exists in-repo; the evaluator port is the work (see ET-2).
> - **Risk:** 🟢 Low — preview-only output; compare screenshots before/after.
> - **Benefit:** ⚪ (dev-time) — seconds → tens of ms per regenerate at high resolution; makes live
>   slider scrubbing actually live.
> - **Seed/Save:** ✅ / ✅.

---

### ET-2. Preview replicates production logic — column shaping and replacement rules diverge

**Observed:** Two replications, different severity:

1. **Terrain column shaping.** `EvaluateColumn` is a ~300-line managed re-implementation of
   `StandardChunkGenerationJob`'s per-column logic (its own docstring says "replicating StandardChunkGenerationJob logic"): biome selection, multi-noise height, density band, strata, caves, lodes, water. It shares the *primitives* (`BiomeBlender`, `BurstSpline`, `FastNoiseLite`)
   but not the *sequence* — every generator change must be hand-mirrored or the Cross-Section preview silently drifts from what the game generates. This is the same drift class the meshing suite exists to prevent, with no guard.
2. **Replacement rules (live divergence).** `ChunkPreview3DWindow.ApplyVoxelModToMap`
   (`ChunkPreview3DWindow.Pipeline.cs` ~line 205) hand-rolls the structure-mod replacement decision (`Default` ≈ "replace unless solid && !transparent-for-mesh"), while production routes
   `VoxelModSource.WorldGen` mods through the `worldGenCanReplaceTags` tag mask. **The 3D preview can therefore show structure placements the game would reject, and vice versa** — a correctness gap in the authoring tool, not just hygiene.

**Recommendation:** Extract shared single-source implementations callable from both sides, the
`BiomeBlender` pattern scaled up: (a) a static Burst-compatible **single-column evaluator** that
`StandardChunkGenerationJob` calls per column and the preview calls per pixel-column — gated on **byte-identical generation output** (fixed-seed differential over representative chunks, plus the
`ChunkGenerationBenchmark` as regression canary); (b) a shared **worldgen replacement-rule resolver** used by `ProcessGenerationJobs`' apply path and the preview's `ApplyVoxelModToMap`. Add a small editor validation ("preview column == job column for N random columns") so the drift class stays dead.

> **Impact Analysis:**
> - **Effort:** 🔴 High — restructures the generation job's inner loop into a shared evaluator;
>   the replacement-rule share (b) is 🟢-sized and can ship first.
> - **Risk:** 🟡 Medium — touching the generation job carries seed risk; the differential gate is
>   mandatory, not optional.
> - **Benefit:** 🟡 Medium — kills a permanent hand-sync tax and an active preview-vs-game
>   correctness gap; ET-1's Burst port then comes almost for free.
> - **Seed/Save:** ⚠️ **Seed-sensitive** — same contract as WG-3: the extraction must be
>   output-preserving, byte-identical for fixed seeds (this is the second exception in the
>   report's seed-breaking note). / ✅.

---

### ET-3. 3D-preview pipeline: snapshot copies, full-grid lighting re-passes, dead copy-back

**Observed:** Three compounding costs in `ChunkPreview3DWindow.Pipeline` + `EditorChunkPipelineRunner`, all `Allocator.Persistent` traffic on the editor main thread:

1. **Full snapshot copies per job.** `ScheduleLighting` copies the center + 8 neighbor voxel maps, heightmap, and 9 light maps into fresh Persistent arrays (~18 full-chunk copies ≈ ~2.5 MB per job); `ScheduleMeshing` does the same 19-buffer dance with a disposal-handle array. The sources are the window's own `_chunkMaps`/`_chunkLightMaps` dictionaries, which are **stable during each phase** — the copies exist only as lifetime insurance.
2. **Full-grid ×5 lighting fixpoint.** `ScheduleAllLighting` re-schedules **every** chunk each iteration (up to `MAX_LIGHTING_ITERATIONS = 5`) regardless of which chunks reported
   `IsStable` — production re-lights only dirty chunks. A radius-4 preview is ~100 chunks × up to 5 passes × the item-1 copies ≈ **~1.5 GB of transient native allocations per preview build**.
3. **Dead voxel-map copy-back.** `PollLighting` (~line 321) disposes and re-copies the *voxel* map from the completed job every pass — but the lighting job never writes voxels (light lives in the ushort light map since the RGB split). 128 KB × chunks × passes of pure waste. Similarly,
   `PollGeneration` copies `data.Map` into storage instead of taking ownership of the job's buffer it is about to dispose.

**Recommendation:** In order of value: drop the copy-back (3 — one-line class of fix); track per-chunk stability and re-light only unstable chunks + mod-touched neighbors (2); transfer ownership of generation outputs instead of copying, and let lighting/meshing jobs read the stored dictionaries directly with the phase acting as the lifetime fence (1) — falling back to a pooled copy only where aliasing is real. The runner also allocates the two padded halo volumes (~306 KB)
fresh per lighting job — reuse per-slot buffers across the passes.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — lifetime reasoning in (1) is the care point; (2)/ (3) are contained.
> - **Risk:** 🟢 Low — editor-only; wrong lifetimes fail loudly with the safety system on.
> - **Benefit:** ⚪ (dev-time) — preview builds drop from multi-GB churn + long waits to roughly
>   production-shaped costs; radius stops being capped by patience.
> - **Seed/Save:** ✅ / ✅.

---

### ET-4. `MeshPostProcessJob` runs synchronously per chunk in the preview (MR-5 not mirrored)

**Observed:** `ChunkPreview3DWindow.ConvertMeshOutput` (`ChunkPreview3DWindow.Rendering.cs` ~line 37)
runs `postProcessJob.Schedule().Complete()` on the main thread per meshed chunk — the exact pattern MR-5 removed from production, where the post-process is chained onto the mesh job at schedule time and is already done by the time the poll sees the handle complete. Minor sibling:
`mesh.RecalculateBounds()` per section (~line 122) despite the constant 16³ section cell (MR-4's constant-bounds fix applies; the clip-bounds feature only shrinks geometry, so the constant cell stays a valid conservative bound).

**Recommendation:** Chain the post-process inside `EditorChunkPipelineRunner.ScheduleMeshing`
(`postJob.Schedule(meshJobHandle)`), exactly as `WorldJobManager.ScheduleMeshing` does, and return the combined handle; assign constant section bounds in `ConvertMeshOutput`.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — mirror an existing production change.
> - **Risk:** 🟢 Low — same data-flow guarantees as production (B10 proved the chaining
>   byte-identical there).
> - **Benefit:** ⚪ (dev-time) — removes a per-chunk main-thread stall from the preview's meshing
>   phase.
> - **Seed/Save:** ✅ / ✅.

---

## Detailed findings — Validation Suites

> **All items in this category are complete** (VS-1 … VS-3). Their detail sections are archived in
> [`../Archived/PERFORMANCE_IMPROVEMENTS_COMPLETED.md`](../Archived/PERFORMANCE_IMPROVEMENTS_COMPLETED.md);
> their rows remain in the master summary table above. Remaining *coverage* gaps are tracked in
> [`VALIDATION_SUITE_COVERAGE_ROADMAP.md`](VALIDATION_SUITE_COVERAGE_ROADMAP.md), not here.

> **Context:** what these suites already do right is the seventh-pass audit note's list (top of this
> report) — the testing architecture itself needs no rework, and coverage gaps stay tracked in the
> fidelity docs. Suites that *don't exist yet* (serialization, worldgen determinism, pipeline state
> machine, physics, coordinate math, pool reset) are ranked in
> [`VALIDATION_SUITE_COVERAGE_ROADMAP.md`](VALIDATION_SUITE_COVERAGE_ROADMAP.md). The three items
> below are the operational layer around the existing tests: the runner, the way the suites are
> invoked, and one documented foot-gun. All three are behavior-preserving for
> the scenarios themselves — after VS-1, every suite must produce the same pass/fail verdicts it
> does today (run each before/after as its own gate).
>
> **Framework decision (2026-07-02):** migrating these suites to the Unity Test Framework was
> evaluated and rejected — see the status header in
> [`../Archived/UNITY_TEST_FRAMEWORK_MIGRATION.md`](../Archived/UNITY_TEST_FRAMEWORK_MIGRATION.md) for the full
> verdict. The operational gaps UTF would have closed (CI entry point, machine-readable results,
> coverage reports) land instead as the VS-1/VS-2 extensions below; the required packages are
> already installed via `com.unity.feature.development`.

## Detailed findings — World Scaling Enablers

> **All items in this category are complete** (WS-1). Its detail section is archived in
> [`../Archived/PERFORMANCE_IMPROVEMENTS_COMPLETED.md`](../Archived/PERFORMANCE_IMPROVEMENTS_COMPLETED.md);
> its row remains in the master summary table above. The wider world-scaling track is closed — see
> [`WORLD_SCALING_IMPLEMENTATION.md`](WORLD_SCALING_IMPLEMENTATION.md).

## Suggested implementation order

Grouped into waves by value-for-effort; within a wave, order is free. Capture the relevant benchmark baseline (`Performance/README.md`) before each wave that touches meshing or lighting.

1. **Quick wins, near-zero risk (one sitting each):**
   ~~MR-1 (Euler hoist) ✅ done — marginal~~, ~~MR-5 ✅ done — chain post-process~~, ~~MR-3 + MR-4 ✅ done — SectionRenderer~~, ~~MR-6 ✅ done — pre-size + pool~~, ~~MR-7 ✅ done — −18% fluid~~, ~~MR-9 ✅ done — clouds SetVertices/SetTriangles/SetNormals~~, ~~TG-2 ✅ done — jobified emission + bitmask fallback~~, ~~TG-3 ✅ done — seeded Unity.Mathematics.Random (grass + lava)~~, ~~MT-3 ✅ done — zero-alloc DebugScreen refresh~~, ~~MT-5 ✅ done — ToPersistentArray helper, no .ToArray () intermediates~~, ~~MT-4 ✅ done — Dictionary<VoxelMeshData,int> O (1) mesh-index
   lookup~~, ~~MT-6 ✅ done — enum rename GZip→Deflate, no save breakage~~. All MT-* items complete. GPU side: GS-3 (vertex-stage lighting) and GS-4 (pipeline tier audit) belong here too.
2. **Android-survivability wave (prerequisite for shipping on weak hardware):**
   OM-1 (device-tier scaling) → P-4 backpressure (pipeline doc §3 — production side; **SU-2** rides along: apply the same in-flight caps to the startup wave) → OM-2 (memory budget + `lowMemory` handler) → OM-3 (bounded save queue; **SL-3** rides along:
   snapshot at dequeue inside the bounded writer) → SL-2 (budgeted load-apply pump — the load-side twin of the generation pump) → SL-1 (pooled load/save buffers) → GS-2 (opaque-texture opt-out — the biggest mobile GPU lever after GS-1). SU-1 (loading-mode budget multiplier) slots anywhere after OM-1 supplies the tier ceiling.
3. **Pipeline stabilization (from the pipeline doc, already ordered there):**
   P-5 stable-save bit (⚠️ save migration) → P-3 jobified merge.
4. **Benchmark-gated structural work:**
   ~~MR-2 ✅ done — vertex format (60 B → 32 B/vertex, upload −57%)~~. ~~TG-6 ✅ done — pooled the per-chunk `ActiveVoxels` `NativeList` (`ActiveVoxelListPool`); benefit ⚪ (native, off-main-thread, frame-neutral), shipped as no-regression + CLAUDE.md/MR-6 pooling mandate~~ → GS-1 (baked-noise liquid shader) → LI-2 (section-ranged lighting gather — the next lighting-line item after P-2 Phase 1; hard gate:
   bit-identical light output, C3 darkening baselines B54/B55 stay green) → WG-1/WG-2 (generation-path buffer pooling + jobified section occupancy — gate with
   `ChunkGenerationBenchmark` + a TG-2-style differential) → WG-3 (structure expansion — profile a tree-dense streaming capture first; byte-identical mod stream is the acceptance gate) → ~~LI-1 ✅ done — padded lighting volume; layout validated (2.4–3× in-job BFS) but on-demand gather is the cost → NOT shipped standalone, folded into P-2~~ → ~~TG-1 (tick path) / TG-4 (full split) — ✅ TG-4 done (Phases 0–1+3+4a+4b+Y-band, all default-on); TG-1 ⏭️ obviated for the fluid hot path (grass residual negligible)~~. The GS-5 §7.3 ownership split
   (`forceRenderingOff` vs `SetActive`) is a small, independently harmless PR — now unblocked (MR-3/MR-4 done); do it early so GS-5 stays unblocked. *(✅ **Done 2026-07-25** as MP-5 —
   `SectionRenderer.SetOcclusionCulled(bool)` is the only code that *sets* `forceRenderingOff` (`Clear()` resets it on pool recycle — culling doc §7.3), guarded by meshing baselines B28–B30. GS-5's remaining work is Phases 1–3 of the culling doc.)*
5. **Long-horizon architecture:**
   **P-2 Layer 1 (worker-thread gather) ✅ SHIPPED 2026-06-22 — banks the LI-1 win net-positive ([benchmark](../Performance/LIGHTING_P2_PHASE1_2026_06_22_BENCHMARK.md)); P-2 Layer 2 (persistent zero-copy storage) is **SHELVED** — gate never triggered, no consumer left ([archived design](../Archived/PERSISTENT_CHUNK_STORAGE_P2.md))** → GS-5 (section occlusion culling — phased plan in `VISIBILITY_CULLING_ARCHITECTURE.md` §5+§7) → GS-6 (BatchRendererGroup conversion — own design doc; decide its ordering against GS-5 first, see the GS-6 entry) → MR-8 (greedy meshing — own design doc first).

WS-1 (chunk-math shift/mask centralization) ✅ **shipped 2026-07-12** — the first Tier B enabler is banked (`WORLD_SCALING_ANALYSIS.md` §6). **VQ-1** (integer voxel query fast path) is WS-1's runtime-API half and was deliberately deferred — it now builds on the shipped `ChunkMath` helpers, then PH-1 (gather-once collision sweeps) and VQ-2 (DDA ray march) build on it. SL-4 (region-file read concurrency, design in `REGION_FILE_CONCURRENCY.md`) is benchmark-gated and corruption-risk 🔴 — schedule it only with its stress test in place, after SL-1/SL-2 land the
cheap wins.

DT-1..4 (debug tooling) are also wave-independent: all 🟢/🟢, batchable into one small PR. Land DT-1/DT-2 *before* the next debugging session that points the lighting/fluid overlays at a perf-sensitive investigation (LI-2, GS-5's wireframe overlay) — that is when their ⚪ rating temporarily stops being ⚪.

ET-1..4 (editor tooling) are wave-independent dev-time items with one internal ordering: ET-4 and ET-3's items (2)/ (3) are cheap standalone wins; ET-2's replacement-rule share (its part b) is 🟢-sized and fixes the preview-vs-game correctness gap — do it early; ET-2's shared column evaluator (part a, 🔴, seed-gated) should be scheduled like any generator change (fixed-seed differential mandatory) and ideally alongside the next planned worldgen feature work, with ET-1's Burst port landing on top of it.

VS-1..3 (validation suites) form one small dependency chain: **VS-1's shared runner is ✅ done (2026-07-08** — `ValidationSuiteRunner` + result object; six suites + ChunkRelativePosition migrated, verdicts unchanged) and **VS-2 is ✅ done (2026-07-09** — `Validate All` aggregate + `ValidationSuiteCI`
headless/agent entry + NUnit3 XML, over an explicit registry with a leak-tight `World.Instance` isolation guard) and **VS-3 is ✅ done (2026-07-10** — `StaleAssemblyGuard` diagnostic preamble in the shared runner:
warn-only, suppressed to fire once per aggregate, three signals over the two project assemblies, 6 self-tests, live-proven). The whole VS-1..3 chain is now complete; the multi-suite regression campaigns ahead (LI-2, GS-5)
inherit a one-click `Validate All` that also flags stale-code runs automatically.

---

## Verification

- **Benchmarks:** `MeshGenerationBenchmark` for MR-*, `LightingJobBenchmark` for LI-1/P-3,
  `ChunkGenerationBenchmark` as a regression canary (no item here should move it).
- **Meshing correctness (regression guard for MR-*):** the **Meshing Validation Suite**
  (`Minecraft Clone/Dev/Validate Meshing`, `Assets/Editor/Validation/Meshing/`) asserts that an output-preserving meshing optimization does not change the generated geometry — it runs the real
  `MeshGenerationJob` against a standard-cube geometry oracle plus structural/determinism invariants. Capture-free: keep all baselines green through any MR-* change. Built test-first per the
  `validation-driven-bugfix` skill (the lighting suite's sibling). Fluid/custom-mesh/cross-mesh and UV/light *values* are not yet oracle-covered — extend the suite before optimizing those paths. **Which harness capability each open MR-* item needs first** (and the phased build order) is catalogued in
  [`Architecture/Testing Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md`](../Architecture/Testing%20Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md):
  e.g. MR-2 needs light/UV value oracles, MR-5 needs `MeshPostProcessJob` section-space coverage, MR-3 needs a `SectionRenderer` apply-path harness, MR-8 needs a merge-invariant oracle.
- **GC:** Profiler GC-allocation capture during sustained streaming (fly in a straight line at max speed) before/after waves 1 and 3 — MR-3/MR-9/TG-3/MT-* should drive steady-state allocations to
  ~zero outside debug UI.
- **Determinism:** For LI-1 and P-3: dump light maps for a fixed-seed test world before/after and diff — must be byte-identical. For TG-3: confirm worldgen output unchanged (it must be — the change is runtime-only); grass-spread pattern differences are expected and acceptable.
- **Visual:** MR-1/MR-2/MR-4 visual checks (rotated blocks, fluid rendering, section-culling bounds, smooth-lighting gradients) are **confirmed in-game**. MR-8 still needs eyes-on checks when implemented (merged-quad lighting seams, texture tiling). GS-1/GS-3 need side-by-side comparisons per quality tier (water/lava character, lighting gradients via `DEBUG_LIGHTDATA`).
- **GPU:** For GS-*: profile with the Frame Debugger + platform GPU profiler (Android GPU Inspector / Snapdragon Profiler on device) — record liquid-pass GPU time over a water-heavy view and total frame bandwidth before/after GS-1/GS-2. Desktop GPU timings will *understate* the opaque-texture and ALU wins; only on-device numbers count for mobile decisions.
- **OOM stress test:** For OM-*: run the benchmark fast-movement scenario on the weakest target device (or a memory-capped Android emulator). Pass criteria: resident memory plateaus instead of climbing, `GenerationJobs`/dirty-set counts stay bounded, no `lowMemory`-driven crash, and the failure mode under sustained overload is reduced view distance — not process death.

---

## Document History

*Entries below the newest are reconstructed from git history — this document predates the
project's Document History convention, so they record what the commits changed rather than
contemporaneous notes.*

* **v1.2** - `MR-8` annotated with two interlocks (2026-08-09), no scope change. (1) Its route-(b) escape hatch is owned by `VX-1`/`VX-8`, and that route unblocks **light only** — AO cannot follow it into a filtered volume, because trilinear filtering is the separable product that produces the engine's round-blob AO (`SILHOUETTE_CONTACT_SHADOWS.md` **S2**), so constraint (a) survives reworded as an *AO* predicate and keeps most of the win. (2) `SS-*`'s sub-cell tessellation is **merge-neutral** and partitions the face set with this item — a face with an occluder in range has non-uniform corner AO and was never mergeable
* **v1.1** - `SL-1` annotated with a corroboration note pointing at the new
  [`THIRD_PARTY_LIBRARY_IDEAS_REPORT.md`](THIRD_PARTY_LIBRARY_IDEAS_REPORT.md) (2026-08-05): an
  independent evaluation of Cysharp's `NativeMemoryArray` converges on `SL-1`'s pooled-buffer
  recommendation and needs no third-party package; that report also re-verified `SL-1`'s load-side
  allocations at commit `1a5fc107`. No finding, rating, or scope changed.
* **v1.0** - Mandatory header completed **and the completed/open split executed** (2026-07-26). The
  document had grown to 2,100 lines while its own header promised that implemented items are archived —
  roughly a third of it described finished work. **25 completed items' detail sections moved** to
  `../Archived/PERFORMANCE_IMPROVEMENTS_COMPLETED.md` (2,100 → 1,126 lines); their **rows stayed in the
  master summary table**, which remains the index of the whole ID space so IDs are never recycled and
  inbound `MR-*`/`LI-*`/`TG-*` references still land. Done markers normalized into the ID column for the
  seven rows that carried them in the Finding cell. `OM-1` and `GS-5` were deliberately **kept open**:
  OM-1's known-good-budget calibration pass is outstanding, and only GS-5's *prerequisite* shipped. Three
  emptied categories gained pointer notes. First versioned edition.
* *(2026-07-24 – 2026-07-25, `8d29a185` · `c2ccf257` · `f443903e`)* - TG-4 cleanup, LI-2 flag retirement
  and MP-5's GS-5 prerequisite synced into their entries.
* *(2026-07-23, `179e408b` · `3e744d30` · `9abd6a9c`)* - P-4 backpressure family closed with IL2CPP A/B
  GO (final).
* *(2026-07-08 – 2026-07-12, `8ca99ab7` · `5ce51201` · `3656cb9b` · `7dd4661a` · `1cb1e5b8`)* - `VS-1`,
  `VS-2`, `VS-3`, `WS-1` and `VQ-1` all marked shipped.
* *(2026-07-02, `7f75338a` · `50b3ff32`)* - **Audit passes 3–7**: 28 new items across
  WG/LI/GS/WS/SL/VQ/PH/SU/DT/ET plus `VS-1..3`, completing coverage — every system in the repository had
  then had at least one audit pass.
* *(2026-06-15 – 2026-06-27, many)* - The `MR-*`, `TG-*` and `MT-*` implementation wave: meshing items
  MR-1…MR-7/MR-9, the TG-2/TG-3/TG-6 tick work, MT-3…MT-6, LI-1 and OM-1 — each landing with a
  benchmark and a docs-sync commit.
* *(2026-06-12, `c4d58cb9`)* - Created as the single master performance backlog, absorbing every
  performance finding from `CODEBASE_IMPROVEMENTS.md` and adding the at-a-glance
  effort/risk/benefit/seed/save ratings.

---

**Last Updated:** 2026-08-09 (`MR-8` VX-8 / `SS-*` interlocks; 2026-08-05: `SL-1` corroboration note; 2026-07-26: header completed, completed
items archived, 2,100 → 1,126 lines)  
**Next Review:** on the next implementation wave — move each newly-finished item's detail section to the
archive and leave its ✅ row behind. A fresh audit pass is also due: the last one was 2026-07-02.
