# Performance Improvements Report

> The single master backlog for **all open runtime performance improvements** in the VoxelEngine.
> Every finding shows, at a glance: the affected system, implementation effort, regression risk,
> expected benefit, and whether it can affect world-generation determinism (seed) or the on-disk
> save format.
>
> Status: **Open backlog.** Items are removed (archived) when implemented and verified.

**Last audited:** 2026-06-12, at commit `39c92ef` (branch `feat/Modular-World-Generation-&-World-Types`).
**Implementation status synced:** 2026-06-20, at commit `ea2aec0` — all Meshing & Rendering items
except MR-8 (greedy meshing) are now closed and in-game confirmed (MR-1 through MR-7, MR-9).
**Third-pass audit:** 2026-07-02, at commit `99c3e6e` — added `WG-1..3`, `LI-2`, `GS-6`, `WS-1`;
re-scoped `P-1` (see the pipeline table note).
**Fourth-pass audit:** 2026-07-02, at commit `99c3e6e` — added `SL-1..4` (serialization save/load),
`VQ-1..2` + `PH-1` (voxel query layer, interaction, physics), `SU-1..2` (startup/world load): the
last previously-unaudited runtime systems.
**Fifth-pass audit:** 2026-07-02, at commit `99c3e6e` — added `DT-1..4` (debug tooling: voxel
visualizer modes, debug screen / perf HUD, terrain-gen overlay), lifting the fourth pass's
debug-tooling exemption.
**Sixth-pass audit:** 2026-07-02, at commit `99c3e6e` — added `ET-1..4` (editor tooling, deep pass
on `Assets/Editor/WorldTools/` + quick pass on the remaining editor tools).
**Seventh-pass audit:** 2026-07-02 — added `VS-1..3` (editor validation suites), completing the
audit coverage: every system in the repository has now had at least one audit pass.
Findings are from static code review unless stated otherwise — capture a baseline per
`Documentation/Performance/README.md` before implementing the larger items.

**Audit scope note (second pass, 2026-06-12):** the `GS-*` (GPU & Shaders) and `OM-*` (CPU-starved
device / OOM hardening) sections were added after a second review pass targeting two gaps: shader/GPU
cost was previously unexamined, and the engine's behavior on CPU-starved hardware (e.g. midrange
Android) where work production outpaces consumption until the process is killed out-of-memory —
observed during benchmark/stress runs with fast movement. The `OM-*` items are the *consumption-side
and ceiling-side* complement to `P-4` (production-side backpressure in the pipeline doc §3): P-4
stops over-scheduling, OM-* makes sure that even when the backlog wins, the result is degradation
instead of a crash.

**Audit scope note (third pass, 2026-07-02):** the `WG-*` (World Generation) section, `LI-2`, `GS-6`,
and `WS-1` were added after a third review pass targeting gaps the first two passes never examined:
the standard world-generation pipeline (schedule-side buffer churn, the main-thread populate/scan,
managed structure expansion), the post-P-2-Phase-1 lighting gather (full-height copies regardless of
content), draw-call submission architecture, and the world-scaling enablers analyzed in
`WORLD_SCALING_ANALYSIS.md` but never tracked here. `P-1` was re-scoped in place (see the pipeline
table note).

**Audit scope note (fourth pass, 2026-07-02):** the `SL-*` (Serialization & Save/Load), `VQ-*`/`PH-*`
(Voxel Queries, Interaction & Physics), and `SU-*` (Startup & World Load) sections were added after a
fourth review pass over the last runtime systems no prior pass had examined: the disk **read** path
(OM-3 only covered save-burst scheduling), the `GetVoxelState` query layer and its per-frame consumers
(the physics solver, the placement ray march), and the world-load boot sequence. Explicitly exempt
from auditing: `Legacy/` (deprecated), `Serialization/Migration/` (one-shot upgrade code),
`DebugVisualizations/` + editor tooling + benchmarks (not shipped), and UI/Input (event-driven, cold
— `MT-3` already covered the one hot piece).

**Audit scope note (fifth pass, 2026-07-02):** the `DT-*` (Debug Tooling) section lifts the fourth
pass's `DebugVisualizations/` exemption. The rating rationale differs from every other section:
these items are ⚪ *because they only cost while a developer is debugging* — but that is exactly when
measurement fidelity matters most. A visualizer that hitches on toggle or allocates per frame
distorts the very captures it exists to read (the same rationale that justified `MT-3`), and the
lighting/fluid modes will be pointed at the engine's most perf-sensitive systems during LI-2/GS-5
work. Covered: `VoxelVisualizer`/`VisualizerChunkData` + the `World.HandleVisualization` driver, the
`DebugScreen` + `PerformanceMonitor` + `GraphRenderer` HUD stack, `TerrainGenDebugOverlay`, and
`ChunkBorderVisualizer` (clean — see the section's baseline note).

**Audit scope note (sixth pass, 2026-07-02):** the `ET-*` (Editor Tooling) section covers the
in-editor world tools at the user's request — deep on `Assets/Editor/WorldTools/` (the
`ChunkPreview3DWindow` + `WorldGenPreviewWindow` stacks and `EditorChunkPipelineRunner`, which
drive the *production* generation/lighting/meshing jobs plus their own managed preview paths — and
run under Mono with no IL2CPP boost for the managed halves), quick on the rest. The quick pass came
back largely clean: `BlockIconGenerator`/`AtlasPacker`/`StructurePreviewWindow`/`CaveDensityAnalyzer`/
`BiomeConfigValidator` are on-demand tools using sane patterns (PreviewRenderUtility, real pipeline
jobs, dirty-flag-gated validation); the only recurring-cost nit is
`WorldGenPreviewWindow.PollForAssetChanges` stat-ing a file timestamp every editor-update tick
(throttle to ~0.5 s when convenient). **The validation suites are deliberately excluded — they are
their own future audit pass.** Production-parity scoreboard for the 3D preview: MR-2 ✅ (shares
`SectionRenderer.Layout` with an anti-drift comment), P-2 Phase 1 ✅ (worker-thread halo gather),
MR-6 pre-size ✅ (inherited via constructor) / pooling intentionally absent (TG-6 convention);
MR-5 ❌ (`ET-4`), and the remaining gaps are the `ET-*` items themselves.

**Audit scope note (seventh pass, 2026-07-02):** the `VS-*` (Validation Suites) section covers the
six editor validation suites (Lighting, Meshing, Behavior, Placement, MeshQueue, LightScheduler)
plus the standalone test files (`VoxelMetadataUtilityTests`, `FastNoiseLiteTests`,
`ChunkRelativePositionTests`) — 14 menu entry points, ~13k lines. **The verdict is strongly
positive**: the suites' *testing architecture* is in excellent shape — oracle + differential +
golden-master layering, prove-red discipline written into scenario docstrings, fuzz layers with a
50-seed baseline / 2000-seed nightly split, synthetic block palettes deliberately decoupled from
`BlockDatabase.asset`, shared `ValidationReflection`/`GoldenMaster` framework helpers extracted
exactly where drift had started, and test worlds that exercise production code paths (e.g. B21 via
the real `ChunkData.FillJobVoxelMap`). Coverage backlogs live in the three fidelity docs
(`Architecture/Testing Framework/*_FIDELITY.md`) and are **not** duplicated here — the `VS-*` items
are purely *operational*: runner duplication, automation, and the stale-assembly foot-gun. Minor
notes not worth IDs: the three small suites (Placement/MeshQueue/LightScheduler) have no fidelity
doc (their scope fits their file headers — fine at current size), and `FastNoiseLiteTests` mixes a
30-run benchmark into its validation menu item (harmless, but worth splitting if it ever slows the
gate). **Which currently-uncovered systems deserve suites of their own** — serialization
round-trip, worldgen determinism, pipeline state machine, physics, coordinate math, pool reset —
is ranked with scope sketches in
[`VALIDATION_SUITE_COVERAGE_ROADMAP.md`](VALIDATION_SUITE_COVERAGE_ROADMAP.md) (`NS-1..6`); several
`⚠️`-gated backlog items (`SL-4`, `WG-3`, `ET-2`, `WS-1`) name those suites as their acceptance
gates.

**Relationship to other documents:**

- `CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md` — deep-dive analysis of the chunk generation → lighting →
  meshing *pipeline* (per-job copies, backpressure, edge-check cascade), including implementation and
  incident history. Its open items are **summarized in the master table below (IDs `P-*`)** but their
  full analysis stays in that document — read it before implementing any `P-*` item.
- `CODEBASE_IMPROVEMENTS.md` — non-performance modernization backlog (API cleanups). All performance
  items formerly tracked there have been **absorbed into this report** (IDs noted per entry).
- `Documentation/Archived/CODEBASE_IMPROVEMENTS_COMPLETED.md` — historical record of completed items.
- `Guides/GENERAL_OPTIMIZATION_GUIDE.md` — the *techniques* reference (pooling, stackalloc, inlining).
  This report tracks *specific instances* in the codebase where those techniques are not yet applied.
- `WORLD_SCALING_ANALYSIS.md` — architectural analysis for world height/depth increases, negative
  quadrants / infinite XZ, cubic chunks, and floating origin. Several items in this report (`P-2`,
  `P-4`, `LI-1`, `OM-1`/`OM-2`) are prerequisites for that work and should be designed with its
  requirements in mind (3D-keyed, halo-padded storage; height-parameterized budgets) — see its §6.

---

## Legend

| Field       | Values                                                                                                                                                        |
|-------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Effort**  | 🟢 Low (hours, localized) · 🟡 Medium (days, several files) · 🔴 High (architectural, cross-system)                                                           |
| **Risk**    | 🟢 Low (isolated, easy to verify) · 🟡 Medium (touches shared state or visual output) · 🔴 High (touches pipeline invariants, lighting semantics, or shaders) |
| **Benefit** | 🟢 High (measurable frame-time/GC win in normal play) · 🟡 Medium (situational or smaller win) · ⚪ Low (cleanliness/scalability, negligible today)            |
| **Seed**    | ✅ Safe — cannot change generated terrain for a given seed · ⚠️ — see entry (changes some runtime-deterministic behavior, but never terrain)                   |
| **Save**    | ✅ Safe — no on-disk format change · ⚠️ Format — requires a save-format version bump + AOT migration step (see `serialization-migration` skill)                |

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

### Meshing & Rendering

| ID     | Finding                                                           | Effort | Risk | Benefit | Seed | Save |
|--------|-------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| MR-1 ✅ | Per-vertex `Quaternion.Euler` in standard cube face generation    |   🟢   |  🟢  |   🟡¹   |  ✅   |  ✅   |
| MR-2 ✅ | 60-byte vertex format with a near-constant 16-byte color stream   |   🟡   |  🟡  |   🟢    |  ✅   |  ✅   |
| MR-3 ✅ | `new Material[3]` + `sharedMaterials` set per section mesh update |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |
| MR-4 ✅ | `RecalculateBounds()` per section update despite known bounds     |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |
| MR-5 ✅ | `MeshPostProcessJob` blocks the main thread per chunk apply       |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |
| MR-6 ✅ | Mesh output `NativeList`s start at default capacity               |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |
| MR-7 ✅ | Per-fluid-voxel `Allocator.Temp` arrays in the meshing job        |   🟢   |  🟢  |   🟢²   |  ✅   |  ✅   |
| MR-8   | Greedy meshing (coplanar quad merging)                            |   🔴   |  🔴  |   🟢    |  ✅   |  ✅   |
| MR-9 ✅ | `Clouds.cs` legacy mesh API with `.ToArray()`                     |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |

> ¹ MR-1 benefit downgraded 🟢→🟡 after measurement: implemented and suite-guarded, but the
> throughput delta is within the benchmark's noise floor — a correctness/cleanliness win, not a
> measurable speedup. See the MR-1 detail section for the before/after table.
>
> ² MR-7 benefit confirmed 🟢 by measurement: **−18% on the fluid pattern** (1365 → 1115 μs/chunk),
> controls flat — a real fluid-path win. See the MR-7 detail section.

### Lighting

| ID   | Finding                                                                                                                                          | Effort | Risk | Benefit | Seed | Save |
|------|--------------------------------------------------------------------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| LI-1 | ✅ Branchy 9-map dispatch + hashmap cache → halo-padded volume; layout validated, **shipped net-positive via P-2 Phase 1** (worker-thread gather) |   🟡   |  🟡  |   🟢    |  ⚠️  |  ✅   |
| LI-2 | Halo gather/extract copies the full 128-voxel column height regardless of content (Y-band / section-ranged volume)                               |   🟡   |  🔴  |   🟢    |  ⚠️  |  ✅   |

### World Generation

| ID   | Finding                                                                               | Effort | Risk | Benefit | Seed | Save |
|------|---------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| WG-1 | ~230 KB of Persistent generation buffers allocated + freed per generated chunk        |   🟡   |  🟡  |   ⚪⁴    |  ✅   |  ✅   |
| WG-2 | Main-thread section copy + per-section empty scan in `ChunkData.Populate`             |   🟡   |  🟡  |   🟡    |  ✅   |  ✅   |
| WG-3 | Structure expansion is a managed main-thread iterator over ScriptableObject templates |   🟡   |  🟡  |   🟡    |  ⚠️  |  ✅   |

> ⁴ WG-1 benefit is TG-6-class today (native churn, mostly off the frame) but the byte volume
> multiplies ~5× under `WORLD_SCALING_ANALYSIS.md` Tier A heights — pool sizing should be
> height-parameterized from the start (same rule as OM-1 budgets).

### Tick & Gameplay

| ID      | Finding                                                                                                                | Effort | Risk | Benefit | Seed | Save |
|---------|------------------------------------------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| TG-1 ⏭️ | Double voxel lookup + float-path cross-chunk queries per tick (obviated by TG-4 for fluids; grass residual negligible) |   🟡   |  🟡  |   🟢    |  ✅   |  ✅   |
| TG-2 ✅  | `OnDataPopulated` full-chunk scan through managed `BlockType`s                                                         |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |
| TG-3 ✅  | `UnityEngine.Random` → `Unity.Mathematics.Random` in behaviors                                                         |   🟢   |  🟢  |   🟡    |  ⚠️  |  ✅   |
| TG-4 ✅  | `BlockBehavior` data separation (ECS/DOTS pattern)                                                                     |   🔴   |  🔴  |   🟢    |  ✅   |  ✅   |
| TG-5 ⏭️ | `BlockBehavior` Burst function pointers (lighter alt. to TG-4 — superseded, not needed)                                |   🟡   |  🟡  |   🟡    |  ✅   |  ✅   |
| TG-6 ✅  | Per-chunk `ActiveVoxels` `NativeList<int>` alloc/free churn — pool it (TG-2 follow-up)                                 |   🟡   |  🟡  |   ⚪³    |  ✅   |  ✅   |

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

| ID     | Finding                                                    | Effort | Risk | Benefit | Seed | Save |
|--------|------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| MT-1 ✅ | `List.Insert(0)` / `RemoveAt(i)` O(n) mesh priority queue  |   🟡   |  🟡  |   🟢    |  ✅   |  ✅   |
| MT-2 ✅ | Light scheduler snapshots the full dirty set every frame   |   🟢   |  🟡  |   🟡    |  ✅   |  ✅   |
| MT-3 ✅ | `DebugScreen` intermediate string allocations per refresh  |   🟢   |  🟢  |    ⚪    |  ✅   |  ✅   |
| MT-4 ✅ | Startup `List.Contains`/`.IndexOf` O(n) custom-mesh lookup |   🟢   |  🟢  |    ⚪    |  ✅   |  ✅   |
| MT-5 ✅ | Startup `.ToArray()` intermediates feeding `NativeArray`   |   🟢   |  🟢  |    ⚪    |  ✅   |  ✅   |
| MT-6 ✅ | `CompressionFactory` "GZip" actually writes raw Deflate    |   🟢   |  🟢  |    ⚪    |  ✅   |  ⚠️  |

### GPU & Shaders

| ID   | Finding                                                                           | Effort | Risk | Benefit | Seed | Save |
|------|-----------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| GS-1 | Liquid shader: per-pixel procedural 3D simplex FBM (up to ~30 snoise calls/px)    |   🟡   |  🟡  |   🟢    |  ✅   |  ✅   |
| GS-2 | URP Opaque Texture required globally; `SampleSceneColor` even with refraction off |   🟢   |  🟡  |   🟢    |  ✅   |  ✅   |
| GS-3 | Voxel lighting math (4× `pow`) runs per-fragment on per-vertex data               |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |
| GS-4 | Render pipeline tier audit: shadow variants, TwoSided casting, MSAA, render scale |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |
| GS-5 | Section occlusion culling (underground sections render despite being sealed)      |   🔴   |  🟡  |   🟢    |  ✅   |  ✅   |
| GS-6 | Per-section GameObject + MeshRenderer submission (BatchRendererGroup conversion)  |   🔴   |  🔴  |   🟡    |  ✅   |  ✅   |

### CPU-Starved Device / OOM Hardening

| ID   | Finding                                                                               | Effort | Risk | Benefit | Seed | Save |
|------|---------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| OM-1 | All budgets/caps are desktop-tuned absolute constants — no device-tier scaling        |   🟢   |  🟢  |   🟢    |  ✅   |  ✅   |
| OM-2 | No memory-pressure response: `Application.lowMemory` unused, no resident-chunk budget |   🟡   |  🟡  |   🟢    |  ✅   |  ✅   |
| OM-3 | Unbounded concurrent chunk saves on mass unload (one `Task` per chunk)                |   🟡   |  🟡  |   🟢    |  ✅   |  ✅   |

### Serialization & Save/Load

| ID   | Finding                                                                                           | Effort | Risk | Benefit | Seed | Save |
|------|---------------------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| SL-1 | Per-chunk managed allocations on the load/save path (payload `byte[]`, wrappers, padding)         |   🟡   |  🟡  |   🟡    |  ✅   |  ✅   |
| SL-2 | Disk-load apply path runs unbudgeted on the main thread (no per-frame cap)                        |   🟡   |  🟡  |   🟡    |  ✅   |  ✅   |
| SL-3 | `SaveChunkAsync` snapshots up to ~190 KB per chunk on the main thread at unload                   |   🟡   |  🟡  |   🟡    |  ✅   |  ✅   |
| SL-4 | Whole-file region lock serializes chunk loads behind saves (design: `REGION_FILE_CONCURRENCY.md`) |   🟡   |  🔴  |   🟡    |  ✅   |  ✅   |

### Voxel Queries, Interaction & Physics

| ID   | Finding                                                                                       | Effort | Risk | Benefit | Seed | Save |
|------|-----------------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| VQ-1 | `GetVoxelState` float path: chunk coord computed twice, ~7 `FloorToInt` + nullable per query  |   🟡   |  🟡  |   🟡    |  ✅   |  ✅   |
| VQ-2 | Placement ray uses fixed-increment sampling (~reach/step queries per frame) instead of DDA    |   🟡   |  🟡  |    ⚪    |  ✅   |  ✅   |
| PH-1 | Collision solver re-queries the same voxel neighborhood across up to 7 sweeps × substeps/tick |   🟡   |  🟡  |   ⚪⁵    |  ✅   |  ✅   |

> ⁵ VQ-2/PH-1 benefits are ⚪ with a single player entity — but `VoxelRigidbody` is the collision
> solver any future entity (mobs, items) will reuse, and both scale linearly with entity count.
> VQ-1 is 🟡 because every per-frame consumer funnels through it.

### Startup & World Load

| ID   | Finding                                                                                        | Effort | Risk | Benefit | Seed | Save |
|------|------------------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| SU-1 | Loading screen throttled by gameplay-tuned per-frame budgets                                   |   🟢   |  🟡  |   🟡    |  ✅   |  ✅   |
| SU-2 | Initial load schedules generation + disk loads for the whole radius at once (no in-flight cap) |   🟡   |  🟡  |   🟡    |  ✅   |  ✅   |

### Debug Tooling

| ID   | Finding                                                                                            | Effort | Risk | Benefit | Seed | Save |
|------|----------------------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| DT-1 | Debug visualization refresh has no per-frame budget (full-world burst on toggle, per-edit rescans) |   🟢   |  🟢  |   ⚪⁶    |  ✅   |  ✅   |
| DT-2 | `VisualizerChunkData` per-update Persistent container churn + `ToArray()`/bounds per apply         |   🟢   |  🟢  |   ⚪⁶    |  ✅   |  ✅   |
| DT-3 | Visualization update-set fed on every voxel edit even when the mode is `None`                      |   🟢   |  🟢  |   ⚪⁶    |  ✅   |  ✅   |
| DT-4 | Debug HUD/overlay allocation leftovers post-MT-3 (graph sample arrays, label `Format`, IMGUI)      |   🟢   |  🟢  |   ⚪⁶    |  ✅   |  ✅   |

> ⁶ ⚪ by definition (debug-only) — but these directly protect **measurement fidelity**: DT-1/DT-2
> make the lighting/fluid visualization modes usable *while* profiling the systems they visualize,
> and DT-3/DT-4 keep the disabled/idle debug stack at true zero so it never shows up in a capture.

### Editor Tooling (WorldTools)

| ID   | Finding                                                                                              | Effort | Risk | Benefit | Seed | Save |
|------|------------------------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| ET-1 | Cross-Section preview evaluates terrain columns in serial managed code on the main thread            |   🟡   |  🟢  |   ⚪⁷    |  ✅   |  ✅   |
| ET-2 | Preview replicates production logic (column shaping ~300 lines; replacement rules **diverge**)       |   🔴   |  🟡  |   🟡    |  ⚠️  |  ✅   |
| ET-3 | 3D-preview pipeline: full snapshot copies per job + full-grid ×5 lighting re-passes + dead copy-back |   🟡   |  🟢  |   ⚪⁷    |  ✅   |  ✅   |
| ET-4 | `MeshPostProcessJob` runs `Schedule().Complete()` per chunk in the preview (MR-5 not mirrored)       |   🟢   |  🟢  |   ⚪⁷    |  ✅   |  ✅   |

> ⁷ ⚪ = dev-time only, but these set iteration speed for worldgen authoring: at high preview
> resolutions/radii the managed paths freeze the editor for seconds per regenerate — under Mono,
> with no IL2CPP to hide it. ET-2 is 🟡 because it is also a **correctness** issue: the preview's
> hand-rolled replacement rules can show structures the game would not place (and vice versa).

### Validation Suites

| ID   | Finding                                                                                    | Effort | Risk | Benefit | Seed | Save |
|------|--------------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| VS-1 | Suite-runner scaffolding copy-pasted across all six suites (~90 near-identical lines each) |   🟡   |  🟢  |    ⚪    |  ✅   |  ✅   |
| VS-2 | Suites are human-in-the-loop only: no aggregate run-all, no CI/headless entry point        |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |
| VS-3 | No stale-assembly guard — a suite can silently validate stale code after an edit           |   🟢   |  🟢  |    ⚪    |  ✅   |  ✅   |

### World Scaling Enablers

| ID   | Finding                                                                             | Effort | Risk | Benefit | Seed | Save |
|------|-------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| WS-1 | Truncating / float-roundtrip chunk coordinate math → `ChunkMath` shift/mask helpers |   🟡   |  🟡  |    ⚪    |  ✅   |  ✅   |

### Chunk Pipeline (deep-dive in `CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md`)

These remain fully documented in the pipeline analysis — the table is reproduced here so this report
is the single at-a-glance view. **Read that document (and the `chunk-lifecycle` skill) before
implementing any of these.**

| ID  | Finding (doc section)                                                                                                                                                                                                                                  | Effort | Risk  | Benefit | Seed |   Save    |
|-----|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:------:|:-----:|:-------:|:----:|:---------:|
| P-1 | Border-slab copies instead of full-volume snapshots (§1.2)                                                                                                                                                                                             |   🟡   |  🟡   |   🟢    |  ✅   |     ✅     |
| P-2 | ✅ Worker-thread gather (Layer 1) **SHIPPED 2026-06-22** (banks the LI-1 win, −34/−50 % vs LI-1 POST) + optional persistent zero-copy storage (Layer 2, §1.3, 🔴 profiler-gated — **not** triggered) — **[design doc](PERSISTENT_CHUNK_STORAGE_P2.md)** |  ✅→🔴  | ✅→🔴  |   🟢    |  ✅   |     ✅     |
| P-3 | Jobified lighting merge in `ApplyLightingJobResult` (§2)                                                                                                                                                                                               |   🟡   |  🟡   |   🟢    |  ✅   |     ✅     |
| P-4 | Backpressure: in-flight caps, out-of-range discard, time budgets, panic gate (§3)                                                                                                                                                                      |   🟡   | 🟡→🔴 |   🟢    |  ✅   |     ✅     |
| P-5 | "Lighting stable" save bit to skip edge checks on load (§4.4)                                                                                                                                                                                          |   🟡   |  🟡   |   🟢    |  ✅   | ⚠️ Format |
| P-6 | Smaller observations: O(n) removals, fail-safe scan counter, draw-queue trickle (§5)                                                                                                                                                                   |   🟢   |  🟢   |   🟡    |  ✅   |     ✅     |

> **P-1 re-scope note (2026-07-02):** P-1 was written when the lighting neighborhood was gathered on
> the main thread at schedule time. P-2 Phase 1 moved that gather to worker threads, so P-1's win is
> now worker-side copy bandwidth, not main-thread schedule time. Re-evaluate it together with `LI-2`
> (section-ranged gather) — both attack the same copies on different axes; implement at most one of
> them first and re-measure before touching the other.

---

## Detailed findings — Meshing & Rendering

### MR-1. ✅ DONE (2026-06-15) — Per-vertex `Quaternion.Euler` in standard cube face generation

> **Closed:** implemented, suite-guarded (`B1`/`B4`), benchmarked, and visually confirmed in-game
> (rotated blocks orient correctly at all yaws). Outcome: **marginal — throughput delta within the
> benchmark noise floor**; kept as a correctness/cleanliness win, not a measured speedup. Retained
> here (not deleted) so the dead-end "hoist for a big win" idea isn't re-proposed. Full record below.

**Observed:** `VoxelMeshHelper.GenerateStandardCubeFace` (`VoxelMeshHelper.cs` ~line 194) computes
`Quaternion.Euler(0, rotation, 0)` and a quaternion-vector multiply **inside the 4-vertex loop**,
for **every face of every standard cube voxel** — including the overwhelming majority of blocks
where `rotation == 0`. That is trigonometry plus quaternion math per vertex, in the hottest loop of
the engine. (The remarks in `MeshGenerationJob.GenerateVoxelMeshData` already note precomputed
rotation variants as a Phase 2b idea for *custom meshes*; the standard-cube cost was untracked.)

**Recommendation:**

1. Branch once per face on `rotation == 0` and use the raw vertex position (no math at all) — this
   covers nearly all terrain.
2. For rotated blocks, hoist the rotation out of the vertex loop and use a precomputed `float3x3`
   per cardinal rotation (0/90/180/270) instead of `Quaternion.Euler`.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — localized to one helper, mechanical change.
> - **Risk:** 🟢 Low — verify rotated blocks (e.g. stairs/logs equivalents) still orient correctly.
> - **Benefit:** 🟡 Low/measured — correctness/cleanliness win; throughput delta is below the
    > benchmark's noise floor (see Status). The original "🟢 High — the benchmark will show it" estimate
    > was **not borne out**: oriented blocks are a small fraction of realistic chunks and the per-vertex
    > transcendental is tiny against total meshing cost.
> - **Seed/Save:** ✅ / ✅.

> **Status (2026-06-15): implemented, validated, and benchmarked — effect within noise.**
> The per-vertex `Quaternion.Euler` was hoisted out of `GenerateStandardCubeFace`: `rotation == 0`
> now takes a no-math fast path, and oriented blocks multiply by a single precomputed `float3x3`
> built once per face. Output preservation is guarded by the new **Meshing Validation Suite**
> (`Minecraft Clone/Dev/Validate Meshing`): `B1` asserts the rotated-vertex math is identical to the
> `Quaternion.Euler` ground truth for all 6 faces × {0,90,180,270}°, and `B4` asserts the same
> end-to-end through the real `MeshGenerationJob` for all 4 yaws. All baselines green before and
> after the change.
>
> **Benchmark (player build, IL2CPP, i9-9900K, 156 chunks × 100 runs):** before vs after, on the two
> rotation-exercising patterns —
>
> | Pattern | Before μs/chunk | After μs/chunk | Δ | Notes |
> |---|---|---|---|---|
> | `Solid` *(control)* | 282.1 | 275.6 | −2.3% | tiny run (43→40 ms), noisy |
> | `Checkerboard` *(control)* | 4416.7 | 4365.4 | −1.2% | high-sample, stable |
> | `OrientedCubes` | 288.5 | 243.6 | −15.6% | tiny run (45→38 ms), **not credible** |
> | `OrientedCheckerboard` | 4423.1 | 4365.4 | −1.3% | high-sample, stable |
>
> The whole report drifted ~1–2% faster between runs (system/build variance; near-identical
> wall-clock). The eye-catching −15.6% on `OrientedCubes` is **measurement noise, not MR-1**: (1) its
> high-sample twin `OrientedCheckerboard` — oriented blocks *at scale* — moved only −1.3%, identical
> to the control `Checkerboard`; (2) `OrientedCubes` is a sub-50 ms run where one 1 ms timer tick is
> ~2.6%; (3) post-change `OrientedCubes` (243.6) reads *faster than* `Solid` (275.6), which is
> physically impossible for the rotation path (fast path can at best tie), proving these two patterns'
> absolute numbers aren't comparable. **Net: no reliably measurable throughput change at this
> harness's resolution.** MR-1 is kept as a correctness/cleanliness improvement, permanently guarded
> by `B1`/`B4` against regression.
>
> **Remaining:** in-game visual confirmation of rotated blocks (logs/pillars/directional). Once
> confirmed, this entry may be removed — but note its conclusion is "marginal, keep for hygiene,"
> not "speedup landed."

---

### MR-2. ✅ DONE (2026-06-20) — 60-byte vertex format with a near-constant color stream

> **Closed:** implemented, suite-guarded, in-game confirmed, and measured. The packed layout keeps
> Position at `Float32x3` (fluids carry sub-block surface heights; half precision risked visible
> cracks) and repacks the rest: TexCoord0 → `Float16x4` (8 B), Color → `UNorm8x4` (4 B), Normal →
> `SNorm8x4` (4 B); TexCoord1 (smooth light) is **unchanged** (B11-pinned, byte-identical). **60 B → 32 B
> /vertex.** The GPU unpacks half/unorm/snorm to floats transparently, so the only shader change was
> `LiquidCore.hlsl` recovering the fluid type via `color.r * 255` (it now rides a UNorm8 channel). The
> normal is packed off the main thread in `MeshPostProcessJob` via `PackedNormal` (the writers still emit
> full-precision `Vector3` normals). `SectionRenderer.Layout` is the single shared source of truth for
> the descriptor (the editor preview window references it). Guarded by the full `Validate Meshing` suite
> (B11 proves TexCoord1 stayed byte-identical; B2/B4 UVs under a half tolerance; B5/B10 determinism on
> the packed normal).
>
> **Measured (IL2CPP, before [`MESHING_MR2_2026_06_19_BASELINE.md`](../Performance/MESHING_MR2_2026_06_19_BASELINE.md)
> `0e453e0` → after [`MESHING_MR2_2026_06_20_AFTER_BASELINE.md`](../Performance/MESHING_MR2_2026_06_20_AFTER_BASELINE.md)
> `0e82130`):** vertex **upload −57 %** (1576 → 676 µs/chunk; bytes 15.94 → 8.50 MB; rate 10113 →
> 12571 MB/s — the stride shrink also lifted throughput, so it beat the −47 % byte ratio). **Bonus:** the
> smaller writer buffers (`Uvs` 16→8 B, `Colors` 16→4 B) cut *generation* 25–30 % on the dense
> patterns (Checkerboard/Transparent/MixedTerrain), wall-clock −25 %. **Trade-off:** Fluid generation
> **+6.4 %** (over the 5 % budget, accepted) — the fluid mesher computes UVs per-vertex and now does
> `float→half` conversions; ~74 µs/chunk, dwarfed by the ~900 µs/chunk upload win. Budget for the Fluid
> pattern is treated as intentionally moved for MR-2 (see the after-baseline doc).

**Observed:** `SectionRenderer.s_layout` declares Position `Float32x3` (12 B) + TexCoord0
`Float32x4` (16 B) + Color `Float32x4` (16 B) + Normal `Float32x3` (12 B) + TexCoord1 `UNorm8x4`
(4 B) = **60 bytes per vertex**. But:

- The Color stream is `new Color(1,1,1,1)` for **every non-fluid vertex** — only fluid faces encode
  data there (liquid type, shore mask).
- TexCoord0's `zw` components are fluid-only (shore push); zeroed for everything else.
- Normals are one of ~10 axis/diagonal directions — they don't need 12 bytes of float precision.

**Recommendation:** Split the fluid-only attributes out of the opaque/transparent submesh layout
(fluids already render in their own submesh with their own material), or at minimum: Color →
`UNorm8x4` (4 B), Normal → `UNorm8x4`-encoded direction or an index decoded in the shader. A
realistic target is **~32 bytes/vertex (−45%)**, which cuts `SetVertexBufferData` upload time,
`NativeList` memory in every meshing job, and GPU memory/bandwidth proportionally.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — vertex layout, `MeshDataJobOutput`, meshing job writers, and all three
    > shaders (opaque/transparent/fluid) change together.
> - **Risk:** 🟡 Medium — shader/layout mismatches fail visibly; smooth lighting encoding in
    > TexCoord1 must be preserved exactly.
> - **Benefit:** 🟢 High — under chunk streaming, vertex upload is a recurring main-thread cost and
    > this nearly halves it.
> - **Seed/Save:** ✅ / ✅.

---

### MR-3. ✅ DONE (2026-06-18) — Managed allocations per section mesh update

> **Closed:** implemented and suite-guarded. `UpdateMeshNative` now picks from 8 cached `Material[]`
> combinations by submesh-presence bitmask (`EnsureMaterialCacheCurrent`) and assigns
> `sharedMaterials` **only when the bitmask or cache version changed** since the section's last update —
> no per-update `Material[]` allocation, no redundant renderer-state write. A static cache-version
> counter covers a global material swap; the per-section `_lastMaterialMask`/`_lastMaterialCacheVersion`
> are reset in `Clear()` (pool-reset-safety). Guarded by **B12** (combination-per-bitmask) and the new
> **B15** (no-reassign-when-bitmask-unchanged, sentinel-survival). All baselines green; in-game render
> confirmed.

**Observed:** `SectionRenderer.UpdateMeshNative` (`SectionRenderer.cs` ~line 84) allocates
`new Material[3]`, potentially `Array.Resize`s it, and assigns `_meshRenderer.sharedMaterials` on
**every mesh update of every section** — 8 sections per chunk, up to 10 mesh jobs per frame. That is
GC garbage plus a renderer-state update in the hot apply path, even when the material set didn't
change.

**Recommendation:** There are only 7 possible material combinations (any non-empty subset of
{opaque, transparent, fluid}). Cache 7 static `Material[]` arrays once, pick by bitmask, and only
assign `sharedMaterials` when the combination actually changed since the last update.

> **Impact Analysis:**
> - **Effort:** 🟢 Low.
> - **Risk:** 🟢 Low — materials are global singletons from `World.Instance`.
> - **Benefit:** 🟡 Medium — removes steady GC churn during chunk streaming (exactly the class of
    > hot-path allocation `GENERAL_OPTIMIZATION_GUIDE.md §5` forbids).
> - **Seed/Save:** ✅ / ✅.

---

### MR-4. ✅ DONE (2026-06-18) — `RecalculateBounds()` per section update despite known bounds

> **Closed:** implemented and suite-guarded. `UpdateMeshNative`'s per-update `_mesh.RecalculateBounds()`
> vertex scan is replaced by a constant `s_sectionBounds` (16³ section cell, center (8,8,8)) assigned
> each update — O(1) instead of O(verts). Guarded by **B14** (bounds contain all emitted vertices —
> survives the change) and the new **B16** (bounds *equal* the constant section cell). The "custom mesh
> exceeds the unit cell" caveat is still open via **MH-7** (no custom/cross/lava block in the palette
> yet) — current blocks all stay inside the cell, confirmed in-game. All baselines green.

**Observed:** `UpdateMeshNative` passes `MeshUpdateFlags.DontRecalculateBounds` to every buffer
upload, then ends with `_mesh.RecalculateBounds()` (`SectionRenderer.cs` ~line 110) — a full
main-thread scan over all vertices of the section, per update.

**Recommendation:** A section's geometry is confined to its 16×16×16 cell (fluid surface heights
and cross meshes stay inside block bounds). Assign a constant
`_mesh.bounds = new Bounds(center: 8,8,8, size: 16,16,16)` once. If custom block meshes are ever
allowed to exceed the cell, compute min/max in the meshing job per section (almost free there) and
pass it through `MeshSectionStats`.

> **Impact Analysis:**
> - **Effort:** 🟢 Low.
> - **Risk:** 🟢 Low — verify no custom mesh asset exceeds the unit cell; oversized bounds are safe
    > (slightly conservative culling), undersized bounds cause visible popping.
> - **Benefit:** 🟡 Medium — removes a per-section main-thread vertex scan from the apply path.
> - **Seed/Save:** ✅ / ✅.

---

### MR-5. ✅ DONE (2026-06-18) — `MeshPostProcessJob` blocks the main thread per chunk apply

> **Closed:** implemented and suite-guarded. The chunk-space → section-space rewrite + `InterleavedStream3`
> assembly now chains onto the mesh job at schedule time in `WorldJobManager.ScheduleMeshing`
> (`postJob.Schedule(job.Schedule())`) instead of `Schedule().Complete()` inside `Chunk.ApplyMeshData`.
> By the time `ProcessMeshJobs` completes the combined handle the post-process has already run on a
> worker thread; `ApplyMeshData` only uploads buffers. Guarded by **B10** (chained-vs-separate byte
> equality, incl. `InterleavedStream3`). All baselines green; in-game render confirmed.

**Observed:** `Chunk.ApplyMeshData` (`Chunk.cs` ~line 334) runs
`postProcessJob.Schedule().Complete()` — a synchronous main-thread stall for the chunk-space →
section-space coordinate rewrite — once per completed mesh job, inside the frame's apply budget.

**Recommendation:** Chain `MeshPostProcessJob` onto the mesh job handle at schedule time in
`WorldJobManager.ScheduleMeshing` (`Handle = postJob.Schedule(meshJobHandle)`). By the time
`ProcessMeshJobs` sees the handle completed, the post-process has already run on a worker thread,
and `ApplyMeshData` only uploads buffers.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — move the job construction; `MeshingJobData.Handle` already carries the
    > combined handle pattern.
> - **Risk:** 🟢 Low — the post-process job only touches the output buffers, which already live
    > until `ProcessMeshJobs`.
> - **Benefit:** 🟡 Medium — removes a fixed main-thread cost per mesh completion (up to 10/frame).
> - **Seed/Save:** ✅ / ✅.

---

### MR-6. ✅ IMPLEMENTED (2026-06-20) — Mesh output `NativeList`s start at default capacity

> **Closed:** pre-size **and** pool implemented in one PR, suite-guarded by **B17** (MH-2 pooled-output
> stale-data guard), built against MR-2's final 32 B/vertex layout. Benchmarked (IL2CPP) — see
> [`MESHING_MR6_2026_06_20_AFTER_BASELINE.md`](../Performance/MESHING_MR6_2026_06_20_AFTER_BASELINE.md).
> **Generation: no regression on any pattern** (0 to −5 %, high-vertex patterns moving most as expected
> from reduced realloc — but the upload pass, which MR-6 does not touch, drifted +12 % run-to-run, so the
> generation deltas sit within this run's noise floor; the firm result is "flat, no regression," and the
> Fluid path returned to its pre-MR-2 level, absorbing the ~6 % MR-2 had moved). The **pre-size table**
> shows a **bimodal** output distribution (light ~2 048 verts vs dense 163 k–393 k), so the
> `DefaultVertexCapacity = 24576` hint was **kept low on purpose** — pooling retention self-tunes each
> buffer to its densest chunk, making the constant a cold-start hint and the low value memory-optimal.
> **Pooling's actual win** (eliminating ~10 Persistent native alloc/frees per chunk in steady state) is a
> runtime allocation-rate reduction the per-iteration-allocating benchmark does not measure — confirm via
> in-game profiler GC capture.

**Observed:** `MeshDataJobOutput` (`JobData.cs`) creates all 9 output lists with the
default initial capacity. A typical surface chunk emits tens of thousands of vertices, so every
meshing job pays a chain of grow → reallocate → memcpy cycles inside the job; and the whole struct is
allocated then disposed (Persistent) per chunk, adding native alloc/free churn.

**Recommendation:** Pre-size with a sensible initial capacity (e.g. vertices ≈ 16–24k, triangles
proportional — derive from the meshing benchmark's median), or carry forward the chunk's previous
mesh size as the estimate. Optionally pool whole `MeshDataJobOutput` instances alongside
`ChunkJobArrayPool` so the capacity survives across jobs (note: `NativeList` retains capacity on
`Clear()`, so pooling fully amortizes growth).

> **Impact Analysis:**
> - **Effort:** 🟢 Low (pre-size) → 🟡 Medium (pool the output struct).
> - **Risk:** 🟢 Low — over-sizing only costs memory; pooling must respect the existing
    > "dispose after `ApplyMeshData`" lifecycle.
> - **Benefit:** 🟡 Medium — removes hidden reallocation/memcpy from every meshing job.
> - **Seed/Save:** ✅ / ✅.

> **Status (2026-06-20): implemented, suite-green (B1–B17).**
> **(a) Pre-size.** `MeshDataJobOutput`'s constructor now seeds every per-vertex / per-triangle
> `NativeList` from named capacity constants (`DefaultVertexCapacity = 24576`, opaque tris ×1.5,
> secondary tris 4096) — a typical surface chunk no longer reallocates inside the job. The benchmark and
> editor/preview paths get this for free (a clean pre-size measurement, no pooling involved). The hint
> targets the median, not the dense-Checkerboard worst case (~278k verts); pooling amortizes the rest.
>
> **(b) Pool.** New `Helpers/MeshOutputPool.cs` (mirrors `ChunkJobArrayPool`: `Rent`/`Return(in …)` +
> a `MeshDataJobOutput.FromPool` flag) pools whole output structs for the runtime path.
> `WorldJobManager.ScheduleMeshing` rents instead of `new`-ing; the output is returned **centrally in
> `ProcessMeshJobs`** right after `Chunk.ApplyMeshData` uploads it — symmetric with the existing input
> release (`ReleaseMeshingJobInputs`), so `Chunk` stays pool-agnostic and `ApplyMeshData` no longer owns
> native-memory lifecycle. `NativeList` retains capacity across `Clear()`, so after warm-up no meshing
> job reallocates its output buffers and the per-chunk Persistent alloc/free is eliminated.
>
> **(c) Reset safety.** `MeshOutputPool.Return` calls `MeshDataJobOutput.ClearForReuse()` (clears the 9
> lists, retains capacity) before re-pooling — mandatory because `MeshGenerationJob` *appends* and never
> clears. `SectionStats` is intentionally not reset (overwritten every run). Guarded by **B17** (a
> pooled buffer reused across two scenes == a fresh buffer); verified red→green (reset off → B17 fails
> `Vertices length 120 != 48`; reset on → all 17 green).

---

### MR-7. ✅ DONE (2026-06-15) — Per-fluid-voxel `Allocator.Temp` arrays in the meshing job

> **Closed:** implemented, suite-guarded (`B7`/`B8`), and benchmarked with a **real measured win** —
> **−18% on the fluid pattern** (1365 → 1115 μs/chunk). Full record below; `MR-7b` (stackalloc, no threading) logged as a deeper future option.

**Observed:** `MeshGenerationJob.GenerateVoxelMeshData` (`MeshGenerationJob.cs` ~line 320) allocates
`new NativeArray<OptionalVoxelState>(14, Allocator.Temp)` + `new NativeArray<ushort>(14, Temp)` and
disposes both **per fluid voxel**. An ocean chunk does this thousands of times per job. Temp
allocations are cheap, but not free at that frequency.

**Recommendation:** Hoist both 14-element buffers to `Execute()` scope and reuse them across voxels
(they are fully rewritten per voxel), or replace with fixed-size struct buffers
(`FixedList`/`stackalloc`-style) since the size is a compile-time constant.

> **Impact Analysis:**
> - **Effort:** 🟢 Low.
> - **Risk:** 🟢 Low — buffers are fully overwritten per voxel; no stale-data hazard.
> - **Benefit:** 🟡 Medium — fluid-heavy chunks (oceans, lakes) mesh measurably faster.
> - **Seed/Save:** ✅ / ✅.

> **Status (2026-06-15): implemented, suite-green, benchmarked — measured win.**
> The neighbor scratch arrays were hoisted from per-fluid-voxel to a single `Allocator.Temp`
> allocation per `Execute()` (sized by `s_fluidNeighborOffsets.Length`), threaded as `ref` params
> through `IterateStandardSection`/`IterateSolidSection` → `ProcessVoxel` → `GenerateVoxelMeshData`.
> The fill loop now writes every slot unconditionally (`… ? new OptionalVoxelState(…) : default`) so
> the reused buffer carries no stale neighbor — bit-identical to the old fresh-per-voxel behavior.
> Output preservation is guarded by the **Meshing Validation Suite** `B8` (full probe-output
> differential across a scene where wall-encased fluids prime all neighbor slots before an
> air-surrounded probe) and `B7` (fluid determinism); all 8 baselines green before and after, so no
> in-game visual check is needed (the differential proves byte-identical fluid output).
>
> **Benchmark (player build, IL2CPP, safety checks ON, i9-9900K, 156 chunks × 100 runs):** before
> (pre-MR-7) vs after, WithDiagonals column —
>
> | Pattern | Before μs/chunk | After μs/chunk | Δ | Role |
> |---|---|---|---|---|
> | **Fluid** | 1365.4 | 1115.4 | **−18.3%** | target |
> | Checkerboard | 4365.4 | 4391.0 | +0.6% | control (stable) |
> | OrientedCheckerboard | 4365.4 | 4384.6 | +0.4% | control (stable) |
> | Transparent | 5179.5 | 5205.1 | +0.5% | control (stable) |
> | MixedTerrain | 2384.6 | 2339.7 | −1.9% | control (stable) |
>
> Only the fluid pattern moved; every high-sample control stayed within ±2% noise, so the −18% is a
> genuine fluid-path win, not drift. **Caveat:** the benchmark runs with Burst **safety checks
> enabled**, so part of the gain is `NativeArray` safety-handle setup/teardown that a shipping
> (safety-off) build wouldn't fully pay — the real-world delta is smaller but still positive (the
> bump-allocator calls and per-voxel churn are eliminated regardless). The noisy sub-50 ms `Solid`/
> `OrientedCubes` micro-patterns are not used for attribution.
>
> **Future (deeper) option — MR-7b:** the scratch is still a `NativeArray<Allocator.Temp>` threaded as
> `ref` through four methods, and the per-`Execute` allocation fires even on chunks with no fluid.
> `OptionalVoxelState` is blittable and the slot count is a compile-time constant, so a `stackalloc` /
> `FixedList` scratch local inside the fluid branch would need **zero threading** and **zero
> allocation**. Deferred because it ripples into `VoxelMeshHelper.GenerateFluidMeshData`'s signature
> (and its fluid-helper chain) — `in NativeArray<OptionalVoxelState>` → `ReadOnlySpan`/pointer — with
> Burst's finicky `Span` support; a bigger, riskier change than the throughput win justifies right now.

---

### MR-8. Greedy meshing (coplanar quad merging)

**Observed:** The mesher emits one quad per visible voxel face. Merging coplanar, same-texture,
same-lighting faces into larger quads ("greedy meshing") typically cuts opaque vertex counts by
**60–90%** in natural terrain — the largest structural meshing win available, and previously absent
from every design document.

**Constraints specific to this engine:**

- **Per-vertex smooth lighting** is the hard one: merged quads interpolate light across the merged
  area, which is wrong unless (a) merging is restricted to faces with identical corner light values
  (still merges large uniform areas — most of the win), or (b) lighting moves out of vertex data
  into a per-chunk 3D light texture sampled per-pixel (bigger refactor, also improves light quality).
- **Texture atlas UVs** can't tile across a merged quad. Requires `Texture2DArray` (UV.z = layer
  index, fragment-side `frac()` tiling) — a shader + atlas build change.
- The anisotropy quad-flip (`EmitQuadTriangles`) and AO/light diagonal logic must be re-derived for
  merged quads.
- Sub-chunk section stats (`MeshSectionStats`) and the visibility-culling connectivity work
  (`VISIBILITY_CULLING_ARCHITECTURE.md`) are unaffected — merging happens within a section.

**Recommendation:** Treat as a phased design doc of its own when picked up: Phase 1 opaque cubes
with flat lighting + texture arrays; Phase 2 smooth-lighting-aware merge predicate. Capture a
meshing baseline first (`Performance/README.md`).

> **Impact Analysis:**
> - **Effort:** 🔴 High — mesher core, shaders, atlas pipeline.
> - **Risk:** 🔴 High — visual regressions (lighting seams, texture tiling) are easy to introduce.
> - **Benefit:** 🟢 High — vertex/index counts drop by more than half; helps CPU meshing time, upload
    > bandwidth, GPU vertex load, and memory simultaneously.
> - **Seed/Save:** ✅ / ✅ — purely visual; voxel data unchanged.

---

### MR-9. `Clouds.cs` — legacy mesh API with `.ToArray()` — ✅ IMPLEMENTED (2026-06-20)

*(Absorbed from `CODEBASE_IMPROVEMENTS.md` §2.2.)*

> **Implemented:** Both mesh-build sites (`CreateFastCloudMesh`, `CreateFancyCloudMesh`) now assign
> via `mesh.SetVertices(list)` / `mesh.SetTriangles(list, 0)` / `mesh.SetNormals(list)` instead of
> the three `.ToArray()` round-trips — no temporary managed arrays per cloud-tile (re)generation,
> byte-identical mesh output. The `new List<>()` allocations were left in place: the build methods
> run only at init and on cloud-style change (via `Initialize`/`Reinitialize`), not per frame
> (`UpdateClouds` only moves transforms), so hoisting them to fields buys no steady-state GC win.

**Observed:** Cloud mesh generation builds `List<Vector3>`/`List<int>` and assigns via
`mesh.vertices = vertices.ToArray()` etc. (`Clouds.cs` ~lines 210–212, 266–268) — three temporary
managed arrays per cloud tile creation.

**Recommendation:** Use `mesh.SetVertices(list)` / `mesh.SetTriangles(list, 0)` /
`mesh.SetNormals(list)` (accept `List<T>` directly), or the NativeArray mesh API for parity with
`SectionRenderer`.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — direct API substitution.
> - **Risk:** 🟢 Low — cloud meshes are visually simple.
> - **Benefit:** 🟡 Medium — eliminates GC spikes during cloud tile (re)generation.
> - **Seed/Save:** ✅ / ✅.

---

## Detailed findings — Lighting

### LI-1. ✅ DONE (2026-06-22) — Branchy 9-map dispatch + hashmap cache in the BFS inner loop

> **➡️ UPDATE (2026-06-22): the layout SHIPPED net-positive via P-2 Phase 1** (worker-thread gather, commit
> `e3e1635`) — −34 % to −50 % vs the LI-1 POST full-timing below. The "NOT shipped standalone" rationale in
> this section is the *standalone* (gather-on-main-thread) decision and is retained as the motivation for
> Phase 1. Result: [`Performance/LIGHTING_P2_PHASE1_2026_06_22_BENCHMARK.md`](../Performance/LIGHTING_P2_PHASE1_2026_06_22_BENCHMARK.md).

> **Closed: implemented, bit-identical, suite-guarded, benchmarked — but NOT shipped standalone.**
> The halo-padded layout is a **validated 2.4–3× in-job BFS win**, but the per-chunk **on-demand gather**
> that feeds it costs ~2.6× the old 9-map prep on the main thread, so standalone schedule-time cost is
> flat-to-worse on every scenario except the most BFS-bound. **The validated layout is folded into P-2**
> (persistent halo-padded storage), where the data is already padded and the gather cost vanishes — keeping
> the BFS win for free. The LI-1 branch is the proven foundation for P-2: branch-free accessors,
> `LIGHTING_HALO = MAX_LIGHTING_BFS_REACH = 2`, the gather/extract transcoders, and 47 lighting baselines
> guarding bit-identity across the halo seam. Full numbers + decision:
> [`Performance/LIGHTING_LI1_2026_06_22_BENCHMARK.md`](../Performance/LIGHTING_LI1_2026_06_22_BENCHMARK.md).
> Retained here (not deleted) so the "halo helps → just ship it" idea isn't re-proposed without the gather
> caveat. Key correction from this work: the doc's suggested **1-voxel halo is a correctness bug** — the
> sunlight-darkening path reads ±2 (edges *and* diagonal corners), so **halo = 2** (20×128×20). Full record below.

**Observed:** Every `GetLightData` / `GetPackedData` call inside `NeighborhoodLightingJob`
(`NeighborhoodLightingJob.cs` ~lines 814–891) walks an up-to-9-way branch tree to select the correct
neighbor array (own / N / S / E / W / NE / NW / SE / SW), and any boundary position additionally
pays a `NativeHashMap<long, ulong>` lookup for the write-through cache. This runs **per neighbor,
per BFS node** — millions of times per lighting job — and defeats Burst vectorization in the
innermost loop.

**Recommendation:** Build the job input as a **single padded volume** instead of 9 separate maps —
e.g. an 18×128×18 array with a 1-voxel halo (sufficient for face-neighbor BFS reads), or
48×128×48 if deep cross-chunk propagation reads beyond the halo. The inner loop becomes a
branch-free flat index, and the read side of the write-through hashmap cache disappears (writes to
the halo become plain array writes, harvested into `CrossChunkLightMods` at the end).

**Trade-off note:** This *increases* schedule-time copy work, which runs counter to
`CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md §1.2` (copy *less* per job). They optimize different
costs: §1.2 attacks main-thread schedule time, LI-1 attacks in-job BFS time. The right call needs a
benchmark of both — and the long-term resolution is §1.3/P-2 (persistent native storage), which can
satisfy both if the persistent layout itself is halo-padded.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — touches job input layout, `FillChunkLightMapForJob` fill paths, and the
    > pool (`ChunkJobArrayPool` buffer sizes change).
> - **Risk:** 🟡 Medium — light output must be **bit-identical** before/after; validate with
    > `LightingJobBenchmark` and a fixed-seed world diff of light maps.
> - **Benefit:** 🟢 High — directly attacks lighting job self-time, the engine's dominant background
    > cost during streaming.
> - **Seed/Save:** ⚠️ Seed-safe for terrain, but lighting results **must** remain deterministic and
    > identical — any divergence re-dirties the edge-check cascade (§4 of the pipeline doc) on old
    > saves. Treat "identical light output" as a hard acceptance criterion. / ✅ no format change.

> **Validation prerequisite (cross-border darkening coverage).** "Bit-identical light output" only has
> teeth on the seam if the suite actually exercises a *darkening* wave crossing a chunk border — the
> halo's hardest read. The lighting suite covers cross-chunk *brightening* fuzz (C1/C2, B40–B44) and now the
> *darkening* quadrant too:
> [LIGHTING_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/LIGHTING_VALIDATION_HARNESS_FIDELITY.md)
> **C3 (B54/B55, CLOSED 2026-06-21)** — keep it green when freezing any halo-vs-9-map diff for LI-1.

---

### LI-2. Halo gather/extract copies the full column height regardless of content

*(Surfaced by the 2026-07-02 third-pass audit. This is the concrete, tracked form of
`WORLD_SCALING_ANALYSIS.md` §2.2's "jobs must become section-ranged" Tier A prerequisite.)*

**Observed:** P-2 Phase 1's worker-thread gather fills the full 20×128×20 halo volume (and the
extract walks it back out) for every lighting job, regardless of how much of that height can
actually carry light changes. Most columns are vertically dominated by uniform regions — sky above
the heightmap (which `SectionUniformSkyLevel` already identifies per section) and unlit/uniform
depths — that are copied, seed-scanned, and extracted anyway. The tooling for a bounded copy already
exists and is proven: the TG-4 Y-band ships on `ChunkMath.GatherPaddedRange`, whose `[0,128]` case
*is* the full-height case, and its serial fluid A/B cut worst-tick tails −24…−46%. Notably, the
fluid Y-band came back frame-neutral in-game precisely because the flood frame is **Light-bound
(~66–70%)** — the lighting gather/extract is where the same idea has frame-level payoff, and it is
the next open item on the "lighting line" that TG-4's closing analysis pointed at.

**Recommendation:** Bound the lighting gather/extract (and BFS seed scans) to the Y-range that can
carry non-uniform light, derived conservatively from: the 3×3 neighborhood's column heightmaps,
`SectionUniformSkyLevel` / per-section `IsEmpty` flags, the Y-extent of the queued BFS nodes, and
`MAX_LIGHTING_BFS_REACH` padding. **This is harder than the fluid band:** sunlight propagates
vertically through the whole column and the darkening path reads ±2 across seams — a too-tight band
produces exactly the cross-chunk darkening bugs C3 guards against. Treat the band derivation as the
design problem; the copy mechanics are done.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — band derivation + plumbing through the P-2 Phase 1 gather; the ranged
    > copy machinery already exists.
> - **Risk:** 🔴 High — lighting semantics; a too-tight band truncates sunlight columns or darkening
    > waves. Hard acceptance criterion: **bit-identical light output**, full lighting suite green
    > (incl. C3 darkening baselines B54/B55) plus a fixed-seed in-game light-map diff.
> - **Benefit:** 🟢 High — attacks the dominant sustained cost (lighting, ~66–70% of flood/ocean
    > frames) and is simultaneously the Tier A scaling prerequisite (640-high columns make
    > full-height copies prohibitive — `WORLD_SCALING_ANALYSIS.md` §2.2).
> - **Seed/Save:** ⚠️ same contract as LI-1 (terrain-safe, but light output must remain identical —
    > any divergence re-dirties the edge-check cascade on old saves) / ✅.

---

## Detailed findings — World Generation

> **Context:** the generation pipeline never had a dedicated audit pass (the first two passes
> covered meshing, lighting, tick, GPU, and OOM hardening). These three items are the
> schedule-side, apply-side, and structure-side findings of the 2026-07-02 pass over
> `StandardChunkGenerator.ScheduleGeneration` → `WorldJobManager.ProcessGenerationJobs`.

### WG-1. Per-chunk Persistent generation buffers allocated and freed per chunk

**Observed:** `StandardChunkGenerator.ScheduleGeneration` (`StandardChunkGenerator.cs` ~line 351)
freshly allocates per scheduled chunk, all `Allocator.Persistent`: the 128 KB `outputMap`
(`NativeArray<uint>`, 32,768 voxels), `outputHeightMap` (512 B), `wormMask` (`NativeBitArray`,
4 KB), `caveMask` (32 KB) + `preCaveBlockIDs` (64 KB) when caves are enabled, two `NativeQueue`s
(legacy mods + structure spawns), and the worm-telemetry list — ~230 KB of native alloc/free churn
per generated chunk during streaming. TG-6 pooled exactly one of these (the 8 KB `ActiveVoxels`
list) and measured ~0.95 µs/chunk of main-thread schedule/release time for it; the remaining
buffers are an order of magnitude more bytes through the same allocator, still unpooled — the
repeated alloc/free pattern CLAUDE.md mandates pooling for.

**Recommendation:** Extend the TG-6 pattern to the fixed-size buffers: a `GenerationBufferPool`
mirroring `ChunkJobArrayPool` / `MeshOutputPool` / `ActiveVoxelListPool`, rented in
`ScheduleGeneration` and returned in `WorldJobManager.ReleaseGenerationJobData` — the terminal
release helper the TG-6 double-dispose review established as the single correct release site.
Reset discipline matters (the MR-6/B17 lesson): `wormMask`/`caveMask` are written sparsely and
conditionally, so pooled instances must be cleared on rent or return, or stale bits carve phantom
caves. Keep editor/benchmark callers on the fresh-alloc path via the same optional-pool parameter
convention TG-6 added to `IChunkGenerator.ScheduleGeneration`.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — pool type + rent/return threading + reset discipline across the terminal
    > and shutdown release paths.
> - **Risk:** 🟡 Medium — native-container lifetime (the exact double-dispose class the TG-6 review
    > caught) and stale-data reuse; both have established mitigations (single terminal-release
    > helper, `ClearForReuse` + a B17-style pooled-reuse guard).
> - **Benefit:** ⚪ Low today (native, mostly off-frame, TG-6-class µs/chunk) — but the byte volume
    > multiplies ~5× under Tier A heights, so pool sizing should be height-parameterized from day one.
> - **Seed/Save:** ✅ (buffers fully rewritten per chunk once reset discipline holds) / ✅.

---

### WG-2. Main-thread section copy + per-section empty scan in `ChunkData.Populate`

**Observed:** `WorldJobManager.ProcessGenerationJobs` STAGE 1 calls `ChunkData.Populate` →
`PopulateFromFlattened` (`ChunkData.cs` ~line 335), which per generated chunk, on the main thread:
copies all 32,768 voxels from the job map into the 8 section arrays (128 KB of memcpy), then
**linearly scans each section for a non-zero voxel** to decide pruning. The scan early-exits on the
first non-zero, so occupied sections cost ~1 read — but every *empty* section pays the full 4,096
reads, which makes the worst case the common case (air-dominated sky sections). The comment at the
copy site already flags it as optimizable. This is the generation-path sibling of P-3 (the
lighting-merge main-thread scan).

**Recommendation:** The generation path already ends with a Burst pass over every voxel
(`ActiveVoxelScanJob`) — extend it (or the terrain job) to emit a per-section occupancy summary
(8-bit non-empty mask, or per-section nonAir counts). `PopulateFromFlattened` then skips both the
copy and the scan for empty sections and drops the scan for occupied ones. Load-from-save and
pool-recycle replay paths keep the current scan (the same fallback split TG-2 established). Longer
term this folds into palettes (`Design/CHUNK_PALETTE_MAPPING.md`): uniform sections should never
materialize 4,096-entry arrays at all.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — job output field + populate fast path, load-path fallback kept intact.
> - **Risk:** 🟡 Medium — a wrong empty mask silently prunes real terrain; gate with a TG-2-style
    > differential (jobified summary vs full managed scan over the same finalized maps, zero diff).
> - **Benefit:** 🟡 Medium — removes up to ~32k managed-array reads plus some section copies per
    > chunk from the streaming apply path; scales with section count under Tier A.
> - **Seed/Save:** ✅ / ✅.

---

### WG-3. Structure expansion is managed, main-thread, per-mod work

**Observed:** `StandardChunkGenerator.ExpandStructure` (`StandardChunkGenerator.cs` ~line 847) is a
C# `yield` iterator walking managed `CompositeStructureTemplate` / `StructureComponent`
ScriptableObjects. `ProcessGenerationJobs` STAGE 2 enumerates it per structure marker and feeds
`World.EnqueueVoxelModification` one `VoxelMod` at a time under the `maxStructureModsPerFrame`
budget. Costs, all on the main thread during streaming: an iterator state machine + enumerator per
structure, cache-hostile managed template traversal, per-mod enqueue work — and when the budget
exhausts, the whole generation job parks (`jobFullyProcessed = false`) and is re-visited next
frame, trickling tree-dense chunks across many frames. Every other generator input was flattened
into NativeArrays at `Initialize`; structure templates are the one managed survivor.

**Recommendation:** Profile first — confirm structure expansion registers on tree-dense streaming
captures before paying the complexity. If it does: flatten templates at `Initialize` (component
positions, block IDs, variant tables into NativeArrays — the established pattern), expand markers
in a Burst job emitting a `NativeList<VoxelMod>` chained onto the generation job, and turn STAGE 2
into a bulk application. The rotation/stacking/variant selection logic and its RNG must be ported
verbatim.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium → 🔴 High — template flattening + a faithful RNG port.
> - **Risk:** 🟡 Medium — expansion is deterministic worldgen; a regression changes structures.
> - **Benefit:** 🟡 Medium — removes managed expansion + the per-mod trickle from tree-dense chunk
    > streaming; situational elsewhere.
> - **Seed/Save:** ⚠️ **Seed-sensitive** — the Burst port must reproduce the exact
    > `Unity.Mathematics.Random` seed derivation and call order, or identical seeds place different
    > structures. Hard acceptance criterion: byte-identical mod stream for fixed seeds across
    > representative biomes (this is the exception in the report's seed-breaking note). / ✅.

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
   `BlockBehavior.Active(...)` for every active voxel — each re-fetches the voxel and re-probes the
   same neighbors. The TODO at `Chunk.cs:226` already acknowledges the duplication.
2. Every neighbor probe that crosses a chunk border goes `ChunkData.GetState` →
   `new Vector3` (float) → `WorldData.GetVoxelState` → `IsVoxelInWorld` float compares →
   `Mathf.FloorToInt` ×3 → dictionary lookup (`ChunkData.cs` ~line 840). For fluid simulation —
   where active voxels cluster at chunk borders by nature — this is the hot path, and it also boxes
   through `VoxelState?` nullables and managed `BlockType` property lookups.

**Recommendation:**

1. Make `Behave` return (or out-param) a "still active" flag so the separate `Active` pass
   disappears.
2. Add an integer-math cross-chunk path: `ChunkData` caches its 4 cardinal neighbor `ChunkData`
   references (invalidated on load/unload), and border probes resolve via
   `neighbor.GetVoxel(x & 15, y, z & 15)`-style integer wrapping without touching `Vector3`,
   `Mathf`, or the world dictionary.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — `BlockBehavior` API change plus a neighbor-reference lifecycle (must be
    > cleared in `ChunkData.Reset()` per pool-reset-safety rules).
> - **Risk:** 🟡 Medium — fluid behavior must be verified unchanged (fluid bugs have history here);
    > stale neighbor references after pool recycle would corrupt simulation.
> - **Benefit:** 🟢 High whenever fluids/grass are active at scale — per-tick cost drops by roughly
    > half from item 1 alone, more near borders from item 2.
> - **Seed/Save:** ✅ / ✅.

---

### TG-2. ✅ DONE (2026-06-20) — `OnDataPopulated` full-chunk scan through managed `BlockType` objects

> **Closed:** implemented and differential-verified. Both halves of the recommendation shipped:
> - **Jobified emission (generation path).** A new single-threaded Burst `ActiveVoxelScanJob`
    > (`Assets/Scripts/Jobs/ActiveVoxelScanJob.cs`) runs as the *final* generation pass — scheduled
    > after the cave-isolation filter in `StandardChunkGenerator.ScheduleGeneration` so it reads the
    > finalized voxel map. It walks the map once and appends the flat chunk index
    > (`ChunkMath.GetFlattenedIndexInChunk` convention) of every voxel whose `BlockTypeJobData.IsActive`
    > is set into a new `GenerationJobData.ActiveVoxels` (`NativeList<int>`). On the main thread,
    > `WorldJobManager.ProcessGenerationJobs` STAGE 1 calls `Chunk.RegisterActiveVoxelsFromJob`, which
    > unpacks each index (`ChunkMath.GetLocalPositionFromFlattenedIndex`, the new inverse helper) and
    > registers it — copying a short list instead of dereferencing managed `BlockType` objects up to
    > 32k times per chunk.
> - **Bitmask fallback scan (load + reset-replay paths).** `World.PrepareGlobalJobData` now builds a
    > flat `bool[] World.IsActiveById`. `Chunk.OnDataPopulated` keeps its section-skipping scan but
    > indexes that array instead of `World.Instance.BlockTypes[id].isActive` — a flat read, no object
    > deref. This path serves only **load-from-save** (`World.LoadOrGenerateChunk` → `PopulateFromSave`)
    > and **pool-recycle replay** (`Chunk.Reset` when `ChunkData.IsPopulated`), where no generation job
    > runs. Active voxels are deliberately **not persisted** (see the serialization architecture doc),
    > so these paths must always rescan — the jobified list is unavailable there. Generators that do not
    > run the scan pass (e.g. the legacy generator) leave `ActiveVoxels` uncreated, and STAGE 1 falls
    > back to this scan.
>
> **Verified:** a differential editor check generated chunks (sea level raised to flood them with
> active water) and confirmed the jobified active set is identical — same local positions — to a
> managed full scan of the same finalized map (10k–13k active voxels/chunk, zero set difference),
> plus a synthetic placed-vs-emitted round-trip (6/6, exact). No existing validation suite covers
> active voxels, so the check was a throwaway `[MenuItem]` (RunCommand execution is currently down on
> the dev machine; the bridge `Unity_ManageMenuItem` was used instead) and removed afterward.
>
> **Measured** (editor A/B microbenchmark — `Assets/Editor/Benchmarking/ActiveVoxelScanBenchmark.cs`,
> menu `Minecraft Clone/Benchmarks/Active-Voxel Scan (TG-2)`; 100 chunks × 5 batches, seed 1337,
> Standard world type; best batch-mean µs/chunk over the *same* finalized voxel data). Four scans:
> `T_old` = original managed-deref full scan; `T_bitmask` = current `OnDataPopulated` flat-`bool[]`
> scan (load/replay path); `T_register` = `RegisterActiveVoxelsFromJob` unpacking the job's list
> (new generation main-thread cost); `T_job` = `ActiveVoxelScanJob` Burst time (now off the main
> thread). `T_job` is measured via `.Run()` so it carries scheduling overhead and **overstates** the
> real per-chunk worker cost — the point is only that it is *off* the main thread, not added to it.
>
> | Scan | Land chunk (0 actives) | Flooded chunk (~12k actives) |
> |---|--:|--:|
> | `T_old` (managed deref, all 32k voxels) | 37.7 µs | 400.7 µs |
> | `T_bitmask` (flat `bool[]`, all 32k voxels) | 33.3 µs | 396.0 µs |
> | `T_register` (unpack job list only) | **0.04 µs** | 366.7 µs |
> | `T_job` (Burst, off main thread) | 87.7 µs | 112.7 µs |
>
> - **Part A (generation path) — main-thread cost.** A normal land chunk previously spent **~37.7 µs**
    > iterating all 32 768 voxels on the main thread to find ~0 active blocks (pure overhead); that is
    > now **~0.04 µs** — the scan moved to a Burst job that overlaps the generation jobs already in
    > flight. The reduction is largest exactly where it matters in normal play (sparse actives).
> - **Part B (load/replay path).** Flat `bool[]` vs the managed deref is **~13 % faster** on the scan
    > itself (37.7 → 33.3 µs); free, and the only path available for saves (actives aren't persisted).
> - **Honest caveat.** For *active-heavy* chunks the Part A main-thread reduction shrinks to ~10 %
    > (400.7 → 366.7 µs) because the bottleneck there is `Chunk.AddActiveVoxel` — the
    > `HashSet<Vector3Int>` inserts (~366 µs for 12k actives), which **both** versions pay. The scan
    > over all 32k voxels is only ~32 µs. So if active-heavy chunks ever profile hot, the next target is
    > the active-voxel *container/population* (cf. TG-1, TG-4), not the scan.

**Observed:** `Chunk.OnDataPopulated` (`Chunk.cs` ~lines 177–205) scans every voxel of every
non-empty section on the main thread when a chunk's data arrives, dereferencing
`World.Instance.BlockTypes[id].isActive` — a managed class array → object → field chain per voxel
(up to 32k per chunk) with poor cache behavior.

**Recommendation:** Precompute a `bool[]` (or 64-bit bitmask array) of "is active" per block ID once
at startup and index that instead — flat, cache-friendly, no object dereference. Longer term, emit
the active-voxel list from the generation job itself (it already touches every voxel in Burst) so
the main thread only copies a short list.

> **Impact Analysis:**
> - **Effort:** 🟢 Low (bitmask) → 🟡 Medium (jobified emission).
> - **Risk:** 🟢 Low.
> - **Benefit:** 🟡 Medium — reduces the activation stutter when chunks stream in.
> - **Seed/Save:** ✅ / ✅.

---

### TG-3. ✅ DONE (2026-06-20) — `UnityEngine.Random` → `Unity.Mathematics.Random` in block behaviors

*(Absorbed from `CODEBASE_IMPROVEMENTS.md` §2.3.)*

> **Closed:** Replaced `UnityEngine.Random` with a **local** seeded `Unity.Mathematics.Random` struct
> at every behavior-tick call site (no shared/static RNG state → inherently thread-safe and Burst-ready).
> Seeds are nonzero via `math.max(1u, math.hash(new int3(globalPos)) ^ (uint)(tickSalt * 0x9E3779B1u))`,
> salted by a new monotonic `World._tickCounter` (exposed as `World.TickCounter`, incremented once per
> tick pass in `ProcessTickUpdates`, reset on world load) so rolls vary **per voxel AND per tick** — a
> position-only seed would freeze grass spread / lock lava viscosity forever. BOTH paths were converted:
> grass spread (`BlockBehavior.cs`, three rolls sharing one rng) and lava viscosity / Bug 08 staggering
> (`BlockBehavior.Fluids.cs`, `HandleFluidSpread`). This **unblocks TG-4/TG-5** (jobifying behaviors).
> ⚠️ **Seed note:** the **runtime RNG sequence changes** — grass-spread and lava patterns differ from the
> old implementation for the same world. Cosmetic only; terrain worldgen RNG is untouched; no
> save/migration impact.

**Observed:** `BlockBehavior.cs` uses `UnityEngine.Random` (globally locked, not Burst-compatible)
in the grass-spread tick path. `ChunkLoadAnimation.cs` / `Toolbar.cs` also use it, but only in cold
initialization code (low priority).

**Recommendation:** Use `Unity.Mathematics.Random` seeded per-chunk or per-tick in
`BlockBehavior.cs`. Deterministic, thread-safe, Burst-compilable — a prerequisite for TG-4/TG-5.

> **Impact Analysis:**
> - **Effort:** 🟢 Low.
> - **Risk:** 🟢 Low.
> - **Benefit:** 🟡 Medium — removes global lock contention; unblocks Burst compilation of behaviors.
> - **Seed/Save:** ⚠️ Seed-safe for terrain (worldgen RNG is untouched), but the **runtime RNG
    > sequence changes**: grass-spread and similar behavior patterns will differ from the old
    > implementation for the same world. Cosmetic only — no save/migration impact. / ✅.

---

### TG-4. `BlockBehavior` data separation (ECS/DOTS pattern)

*(Absorbed from `CODEBASE_IMPROVEMENTS.md` §6.1.)*

> **Detailed design:** [TG4_BLOCK_BEHAVIOR_DATA_SEPARATION.md](TG4_BLOCK_BEHAVIOR_DATA_SEPARATION.md) —
> phased plan (BH-D1 infra → per-family storage split → grass Burst → fluid Burst → parallelize + Tier-2),
> with the BH-D1 old-vs-new differential slotted into each phase gate.
>
> **Status (2026-06-27): FULLY IMPLEMENTED — Phases 0–1 + 3 + 4a + 4b + Y-band SHIPPED (all default on); Phase 2
> skipped. Only the flag-gated-fallback cleanup pass remains.** Phase 0 (BH-D1 differential infra) + Phase 1
> (per-family `NativeHashSet<int>` active-voxel buckets — landed on **`ChunkData`**, not `Chunk`; tick orchestration
> stays on `Chunk`) are in-game confirmed. **Phase 3** Burst-ticks Tier-1 interior fluids (`FluidTickJob`, border
> managed) gated by `BH-D1[L|F]`; **Phase 4a** parallelizes those interior jobs across chunks
> (`World.ProcessTickUpdatesParallel`, worker-count guarded) gated by a parallel-vs-serial determinism suite + an
> 8-run IL2CPP A/B; **Phase 4b** closes the Tier-2 border — **every** fluid (interior AND border) is Burst-ticked,
> border voxels reading a per-tick **9-snapshot neighbor halo** via the **§4.2 option (b) per-tick local gather**
> (`ChunkMath.GatherPaddedFluidVoxels`), gated by `BH-D1[L|H]` + a cross-chunk determinism stress + in-game; and the
> **Y-band** (2026-06-27) sizes that gather to the active-fluid Y-extent (height-independent copy), gated by
> `BH-D1[H|HB]`/`[L|HB]` + the Y-band determinism stress + in-game. **Phase 2 (grass) skipped** (negligible cost). The
> new runtime buckets are pool-retained (no per-recycle churn — **TG-6-aligned**; TG-6's own target, the
> `GenerationJobData.ActiveVoxels` hand-off list, is now pooled too — shipped 2026-06-27).
>
> **Important — option (b), NOT a P-2 Layer 2 dependency.** Phase 4b deliberately took the **TG-4-local per-tick halo
> gather** (option (b)), so it ships **standalone** with no chunk-storage commitment — TG-4 does **not** depend on
> [P-2 Layer 2](PERSISTENT_CHUNK_STORAGE_P2.md) (persistent zero-copy storage), which stays 🔴 profiler-gated and is a
> *separate, optional* future optimization of the same gather (it would let the halo read neighbor cores zero-copy).
>
> **Net (attribution gates CLOSED across five captures —**
> [`…FLUID_TICK_2026_06_23`](../Performance/BEHAVIOR_TG4_FLUID_TICK_2026_06_23_BENCHMARK.md) (isolated tick
> ~21 ms/tick), [`…FULLWORLD_FLUID_2026_06_23`](../Performance/BEHAVIOR_TG4_FULLWORLD_FLUID_2026_06_23_BENCHMARK.md)
> (tick owns the **GC-bound ~180 ms dam-break spike**; Phase 3 → ~143 ms; sustained frame **lighting-dominated
> ~66 %**), the [Phase-4a A/B](../Performance/BEHAVIOR_TG4_FULLWORLD_FLUID_PARALLEL_2026-06-24_BENCHMARK.md)
> (interior-parallel shaves a further **~6.6 ms / ~4.6 %** off the spike), the
> [Phase-4b halo A/B](../Performance/BEHAVIOR_TG4_PHASE4B_HALO_AB_2026-06-24_BENCHMARK.md) (Bursting the border makes
> the **tick** 1.70–2.15× faster, GC-spike tail removed), and the
> [Y-band A/B](../Performance/BEHAVIOR_TG4_PHASE4B_YBAND_AB_2026-06-27_BENCHMARK.md) (serial worst-tick tail
> −24–46 %, **frame-neutral** in-game)**): the fluid tick is now fully Burst + parallel with a flat, predictable cost
> — but it was **never the frame bottleneck.** The sustained ocean frame stays **lighting-dominated (~66–70 %)**, so
> ocean smoothness needs the **lighting line** (LI-1 / [P-2](PERSISTENT_CHUNK_STORAGE_P2.md)), not (only) the
> tick. TG-4 removed the stutter *spike* and made the tick scale across cores; the *average* frame cost is the
> lighting engine's to win. The 🔴/🔴 effort/risk ratings below describe the (now-completed) work's nature.

**Observed:** All ticking voxels (fluids, grass, future behaviors) flow through one monolithic
collection and a central `switch` in `BlockBehavior`. As behavior types grow, this forces a single
main-thread tick loop iterating unrelated voxel types.

**Recommendation:** Split active voxels by behavior type into dedicated native collections
(e.g. `_activeFluids`, `_activeGrass`) so each behavior runs as its own independent Burst job —
cache-local, parallelizable, and off the main thread.

> **Impact Analysis:**
> - **Effort:** 🔴 High — re-architects the tick pump and active-voxel registration.
> - **Risk:** 🔴 High — touches the core world ticking engine; fluid parity testing required.
> - **Benefit:** 🟢 High — scales across cores; the only path that gets ticking fully off the main
    > thread. Subsumes TG-1 if done wholesale (TG-1 is the incremental version).
> - **Seed/Save:** ✅ / ✅.

**Parity guard (prerequisite):** the "fluid parity testing required" note above is satisfied by the
behavior-tick validation harness in
[BEHAVIOR_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/BEHAVIOR_VALIDATION_HARNESS_FIDELITY.md)
— **built (Waves 0–2, 8 baselines green, 2026-06-21)**; land the old-vs-new differential baseline (BH-D1) in the
TG-4 PR itself. The harness's seam table (S1–S5) also enumerates the exact `World.Instance` couplings this split
must sever.

---

### TG-5. `BlockBehavior` Burst function pointers (lighter alternative to TG-4)

*(Absorbed from `CODEBASE_IMPROVEMENTS.md` §6.2.)*

> **Status (2026-06-27): ⏭️ SUPERSEDED — not needed.** TG-5 was the *lighter alternative* to be taken **if TG-4 was
> overkill**. TG-4 shipped in full (Phases 0–1 + 3 + 4a + 4b + Y-band, all default-on) with the tick now fully Burst +
> parallel and behavior byte-identical, so the function-pointer-dispatch fallback buys nothing TG-4 hasn't already
> delivered — and the tick is no longer the frame bottleneck (the lighting line is). Kept here for historical context.

**Observed/Recommendation:** If TG-4 is overkill, replace the central `switch` with a
`Unity.Burst.FunctionPointer<T>` registry indexed by voxel ID. Keeps a single active-voxel
collection while decoupling behavior logic and enabling Burst-compiled dispatch.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — function-pointer initialization at Burst startup.
> - **Risk:** 🟡 Medium — mismanaged Burst function pointers hard-crash.
> - **Benefit:** 🟡 Medium — decoupling + Burst dispatch, without TG-4's parallelism win.
> - **Seed/Save:** ✅ / ✅.

**Parity guard (prerequisite):** same as TG-4 — guard the function-pointer dispatch swap with the behavior-tick
harness ([BEHAVIOR_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/BEHAVIOR_VALIDATION_HARNESS_FIDELITY.md))
and the BH-D1 old-vs-new differential. Decoupling the `switch` into a registry must produce a byte-identical `VoxelMod`
stream tick-for-tick.

---

### TG-6 ✅. Per-chunk `ActiveVoxels` `NativeList<int>` alloc/free churn — pool it (TG-2 follow-up)

*(Surfaced by the 2026-06-21 behavior-suite review, finding #4. Shipped 2026-06-27.)*

**Was:** TG-2's jobified emission allocated a fresh `NativeList<int>` per chunk generation —
`new NativeList<int>(StandardChunkGenerator.ActiveVoxelPresizeCapacity, Allocator.Persistent)` (2048 ⇒
8 KB) in `StandardChunkGenerator.ScheduleGeneration`, stored in `GenerationJobData.ActiveVoxels`, and
freed per chunk in `GenerationJobData.Dispose`. During streaming this was per-chunk Persistent
allocate-and-free churn — exactly the repeated-allocation pattern CLAUDE.md says to pool — and the 8 KB
was reserved up front even for the common sparse-actives chunk (which emits ~0 indices).

**Shipped:** new `Helpers/ActiveVoxelListPool.cs` (mirrors **MR-6**'s `MeshOutputPool`: `Rent`/`Return`/
`Dispose`, `Clear()` on return retains capacity, `MAX_RETAINED` cap self-disposes overflow). `NativeList`
retains its allocated capacity across `Clear()`, so a warmed pool also removes the realloc-and-copy growth
a water-heavy chunk (thousands of source voxels) otherwise pays inside the scan.
`IChunkGenerator.ScheduleGeneration` gained an optional `ActiveVoxelListPool` parameter (default `null`):
`WorldJobManager` passes its owned pool on the production path; editor / preview / benchmark callers pass
`null` and keep the fresh-alloc + `Dispose` path. A `GenerationJobData.ActiveVoxelsFromPool` flag routes
the release — `Dispose` frees the list only when **not** pool-owned.

**Release-path design (the part that mattered).** The first cut returned the list mid-pipeline at the
STAGE-1 consume site; a `/code-review` found that left a **stale handle on the lingering job** (a
budget-exhausted job stays enrolled in `GenerationJobs` after STAGE 1), which `WorldJobManager.Dispose`
then **re-returned → double-push → double-dispose** at shutdown. The fix moved the return to a single
terminal release helper, `WorldJobManager.ReleaseGenerationJobData` (mirroring `ReleaseLightingJobData` /
`ReleaseMeshingJobInputs`), co-located with `Dispose` at the terminal completion **and** the shutdown
loop. Because a job is removed from `GenerationJobs` the instant it reaches terminal completion, and
shutdown only releases still-enrolled jobs, each job's list is returned **exactly once** — no stale-handle
window. Native-container lifetime is respected: the return sits strictly after `Handle.Complete()`.

> **Impact Analysis (as shipped):**
> - **Effort:** 🟡 Medium — pool type + threading it through the generator interface + the terminal-release split.
> - **Risk:** 🟡 Medium — native-container lifetime / use-after-free (the double-dispose the review caught);
    > de-risked by routing all release through one post-`Complete()` helper.
> - **Benefit:** ⚪ Low — removes per-chunk 8 KB Persistent alloc/free during streaming and the realloc
    > growth on active-heavy chunks once the pool warms, but this is **native** (not GC) churn, sub-µs and
    > mostly off the main thread; frame-neutral by construction (see footnote ³). No tick-path cost change.
> - **Seed/Save:** ✅ / ✅ — active voxels are not persisted; pooling is an internal allocation concern.

**Validation (no dedicated benchmark — by design).** The win is a `Persistent` (native, not GC) alloc that
no frame benchmark can resolve above its noise floor, so the gate was reframed from "before/after speedup"
to **no-regression on two IL2CPP harnesses**: the full-world fluid stress pass (`FluidStressPass`) and the
isolated tick bench (`FluidTickBenchmark`) both came back frame-neutral across 3 runs each — uniform sub-2%
deltas with no code path linking the pooling change to either hot path (settled/flood frame is Light-bound
~69%; the tick path is `Chunk.TickUpdate`, which TG-6 never touches). Neither validates the *win*; together
they confirm the refactor (incl. the double-dispose fix) is safe. `ActiveVoxelScanBenchmark` was **not**
extended — it is editor/Mono-only and cannot capture IL2CPP.

The win *is* isolated by the runtime `ChunkGenerationBenchmark`, extended (2026-06-27) with a fresh-vs-pooled
leg over Land (sparse) and Ocean (raised sea level → water-heavy, active-list realloc growth) scenarios,
64 chunks/run, and `sched µs/ch` + `free µs/ch` columns narrowed to the main-thread schedule/release window
where the per-chunk alloc lives. Across 3 IL2CPP runs the pooled leg shaves a stable **~0.6 µs/ch off schedule
(~5%)** and **~0.35 µs/ch off release (~14–17%)** — consistent in sign across all scenario×run combinations —
for ~0.95 µs/ch of main-thread time per chunk. `total ms/ch` (~1.58 ms) shows no leg advantage: it is
dominated by the worker-side generation `Complete()`, so the Ocean realloc saving is real but sub-noise
against it. The benchmark is retained as a standing generation-path regression guard and comparison-grade
fixture for any future dedicated-generation work.

**Also closed (the rest of review finding #4):** the `2048` magic number is extracted to
`StandardChunkGenerator.ActiveVoxelPresizeCapacity` (the benchmark pins to it, no drift), and the
dispose-path no-leak invariant is documented on `GenerationJobData.Dispose`.

---

## Detailed findings — Main Thread & Miscellaneous

### MT-1. `List.Insert(0)` / `RemoveAt(i)` — O(n) mesh priority queue ✅ DONE

*(Absorbed from `CODEBASE_IMPROVEMENTS.md` §3.1; overlaps pipeline doc §5.1.)*

**Resolution (2026-07-01):** Replaced the `List<Chunk> _chunksToBuildMesh` + companion
`HashSet<ChunkCoord>` with a single dedicated `Helpers/MeshBuildQueue.cs` — a **pooled intrusive
doubly-linked list** (parallel `next`/`prev`/`chunk`/`coord` arrays threaded by a free-list) plus a
`coord → slot` `Dictionary` serving both duplicate rejection and O(1) removal. Every operation is now
O(1): immediate enqueue links at the head (newest-first / LIFO — matches the old `Insert(0)`), normal
enqueue links at the tail (FIFO — matches `Add`), the scheduling drain removes the current node via a
mutating struct `Enumerator` (replaces mid-list `RemoveAt(i)`), and the unload paths remove by
coordinate (replaces O(n) `Remove(chunk)`). Ordering is **bit-identical** to the old list (all
immediates ahead of all normals; retain-on-not-ready preserved), and slot recycling makes it zero-GC
in steady state. `PriorityQueue<,>` (the distance-keyed option below) was rejected: it is absent from
Unity's Mono/.NET Standard 2.1 runtime and supports neither arbitrary removal nor retain-in-place.
In-game confirmed; the O(n) unload-removal bug (`CHUNK_MANAGEMENT_BUGS.md #01`) is archived.
A **normal→immediate priority promotion** on re-request was identified as a latent behavior gap and
kept out of this no-op refactor, then shipped as a separate follow-up (2026-07-01): an immediate
re-request of an already-queued chunk now promotes it to the head (O(1) `MoveToHead` in `TryEnqueue`),
so a fresh player edit meshes ahead of streaming work it was previously stuck behind. Guarded by
baseline B9 in the Mesh Build Queue suite (prove-red confirmed; B2 narrowed to the surviving
normal-dedup no-reorder guarantee).

**Observed:** The meshing pipeline uses `List<Chunk> _chunksToBuildMesh` as a priority queue —
`Insert(0, chunk)` and mid-list `RemoveAt(i)` are O(n) shifts (`World.cs`, scheduling loop ~line
1270 and the insert/remove sites around lines ~1022/1033/1607, plus unload paths at ~2156). With a
large backlog (exactly the §3 cascade scenario) this goes quadratic.

**Recommendation:** Replace with a real priority structure — `PriorityQueue<Chunk, int>` keyed by
distance, or two queues (priority/normal) if only front-insertion matters. Keep the companion
`HashSet` for dedup.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — iteration/removal patterns around the list must adapt.
> - **Risk:** 🟡 Medium — meshing order affects visual pop-in; test streaming visually.
> - **Benefit:** 🟢 High under backlog; modest in calm play.
> - **Seed/Save:** ✅ / ✅.

---

### MT-2. ✅ DONE (2026-07-02) — Light scheduler snapshots the full dirty set every frame

> **Closed:** ready/waiting split shipped and in-game verified. The dirty set now lives in
> `LightWorkScheduler` (`Assets/Scripts/Helpers/LightWorkScheduler.cs`): the per-frame scan iterates
> only a **ready** set, and a chunk whose readiness gate fails (unpopulated, lighting job in-flight,
> or all schedule branches blocked) is parked in a **waiting** set the scan never visits. Parked
> chunks re-enter ready only on the events that can flip their gate — terrain generation completed
> (`ProcessGenerationJobs` removal sweep), disk load hydrated (`PopulateFromSave` in
> `LoadOrGenerateChunk`), lighting job completed (`ProcessLightingJobs` removal sweep), or the chunk's
> own flag transition (staging callback) — via `World.PromoteLightWorkNeighborhood` → move-only 3×3
> `PromoteNeighborhood`. The 1-second fail-safe scan is retained and now also calls `PromoteAll()`, so
> a missed promotion degrades to ≤1 s of latency instead of a permanent stall; under
> `enableDiagnosticLogs` a recurring non-zero fail-safe-promotion count is logged as a missing-hook
> sentinel. **In-game wave-front stress logged zero fail-safe promotions** — every unblock path is
> event-covered, the backstop never fired. Guarded by the `Validate Light Work Scheduler` editor suite
> (9 baselines, prove-red B2/B4 confirmed); `Validate Lighting Engine` stayed 47/47 green. Docs synced:
> `CHUNK_LIFECYCLE_PIPELINE.md` §4/§9.1/§10, `LIGHTING_SYSTEM_OVERVIEW.md` §3.2,
> `CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md` panic-gate note.

**Observed:** `World.Update` (`World.cs` ~lines 1171–1256) copies the entire
`_chunksNeedingLightWork` set into a pooled list every frame and iterates all of it — even when
`maxLightJobsPerFrame` is exhausted after the first few entries, and even for chunks whose
neighbor-readiness gates will fail identically to last frame. Cheap in calm play; O(dirty) per
frame during exactly the backlog scenarios where frames are already slow (compounds pipeline §3).

**Recommendation:** Split the dirty set into "gate-ready" and "waiting" subsets: chunks enter
gate-ready when the event that could unblock them occurs (neighbor populated / neighbor lit —
hooks already exist at those transitions). The per-frame loop then iterates only schedulable work
and stops at the throttle. ⚠ Respect the flag-pairing invariants in
`CHUNK_LIFECYCLE_PIPELINE.md` — the current full rescan doubles as a self-heal (see also the
1-second fail-safe scan, pipeline doc §5.2), so keep that fail-safe in place.

> **Impact Analysis:**
> - **Effort:** 🟢 Low→🟡 Medium depending on how event-driven the ready set becomes.
> - **Risk:** 🟡 Medium — a chunk that never enters the ready set stalls lighting (deadlock
    > history!); the fail-safe scan must remain as backstop.
> - **Benefit:** 🟡 Medium — trims fixed per-frame overhead precisely when FPS is lowest.
> - **Seed/Save:** ✅ / ✅.

---

### MT-3. ✅ DONE (2026-06-27) — `DebugScreen` intermediate string allocations per refresh

> **Closed:** zero-alloc refresh implemented and in-game verified. All `.ToString()`/`$"..."` sites
> replaced: numeric `Append` overloads + a shared `Helpers/UI/StringBuilderFormat.cs` (`AppendFixed`,
> `AppendFixedPadded`, `AppendIntPadded`, `AppendBytes`, `AppendMs`, `AppendHex2`, `AppendElapsedTime`),
> TMP `SetText(StringBuilder)` at the assignment seam, the constant `graphicsDeviceType` cached once,
> and the `[Flags]` `BlockTags` + `DebugVisualizationMode` enum `ToString()` boxing replaced with
> declaration-order appenders / literal mappers (output-parity confirmed against both enum definitions).
> `World.GetMeshQueueDebugInfo()` → `AppendMeshQueueDebugInfo(StringBuilder)`. `BenchmarkHUD`'s three
> private formatters were folded into the shared helper (single source of truth). Player/IL2CPP builds
> are zero-alloc; under `UNITY_EDITOR` TMP's `SetText` still materializes one inspector string (compiled
> out of player builds).

**Observed:** Despite the cached `StringBuilder`s, each refresh allocates dozens of temporaries:
`.ToString()` calls on numbers feeding `Append` (`DebugScreen.cs` ~lines 383–396), plus `$"..."`
interpolation inside `AppendLine(...)`. Only costs while the debug screen is visible.

**Recommendation:** Use the numeric `Append(int)`/`Append(float)` overloads and replace
interpolated `AppendLine($"...")` with chained `Append` calls. Zero-alloc refresh is achievable.

> **Impact Analysis:**
> - **Effort:** 🟢 Low (tedious but mechanical).
> - **Risk:** 🟢 Low.
> - **Benefit:** ⚪ Low — debug-only; worth doing so the debug overlay doesn't distort GC profiling.
> - **Seed/Save:** ✅ / ✅.

---

### MT-4. Startup `List.Contains` / `.IndexOf` — O(n) custom-mesh lookup ✅ DONE

*(Absorbed from `CODEBASE_IMPROVEMENTS.md` §3.2.)*

**Resolution (2026-07-01):** The flatten logic had since moved out of `World.PrepareGlobalJobData`
into `JobDataManagerFactory.Create` (`JobDataManagerFactory.cs`) — the shared SoT for runtime, editor
tools, and the OM-1 calibrator. Added a `Dictionary<VoxelMeshData, int>` (`meshToIndex`) built in
Step 1 alongside `uniqueCustomMeshes`, with value == list index. The dedupe check (Step 1) and the
mesh→index resolve (Step 4) are now O(1) `ContainsKey`/indexer lookups instead of O(n)
`List.Contains`/`IndexOf`. The list is retained for ordered iteration (Step 2's offset accumulation).
Output is byte-identical: same insertion order, and `Dictionary` uses the same
`EqualityComparer<VoxelMeshData>.Default` as the old `List` scans, so dedupe semantics are unchanged.

**Observed:** `World.PrepareGlobalJobData` collects unique custom meshes into a `List` and searches
with `.Contains()` / `.IndexOf()` — O(n) each (`World.cs` ~lines 1338–1346). Startup-only.

**Recommendation:** `Dictionary<VoxelMeshData, int>` mapping mesh → index; O(1) both ways.

> **Impact Analysis:** Effort 🟢 / Risk 🟢 / Benefit ⚪ (startup-only, scales with block DB growth).
> **Seed/Save:** ✅ / ✅.

---

### MT-5. Startup `.ToArray()` intermediates feeding `NativeArray` ✅ DONE

*(Absorbed from `CODEBASE_IMPROVEMENTS.md` §4.2.)*

**Resolution (2026-07-01):** The flatten logic had since moved out of `World.PrepareGlobalJobData`
into `JobDataManagerFactory.Create` (`JobDataManagerFactory.cs`, Step 3). The four
`new NativeArray<T>(list.ToArray(), Allocator.Persistent)` calls now route through a private
`ToPersistentArray<T>(List<T>)` helper that allocates at `list.Count` and fills via a loop
(mirroring the existing `blockTypesJobData` pattern in Step 4) — no throwaway managed array. Copy is
element-order- and allocator-identical; startup-only, so no runtime path changed.

**Observed:** `new NativeArray<T>(list.ToArray(), Allocator.Persistent)` ×4 in
`JobDataManagerFactory.Create` (`JobDataManagerFactory.cs` ~lines 75–82) — temporary managed arrays
immediately discarded.

**Recommendation:** Allocate the `NativeArray` at `list.Count` and fill via `CopyFrom`/loop, or
build in a `NativeList<T>` from the start.

> **Impact Analysis:** Effort 🟢 / Risk 🟢 / Benefit ⚪ (startup-only).
> **Seed/Save:** ✅ / ✅.

---

### MT-6. `CompressionFactory` "GZip" actually writes raw Deflate ✅ DONE

**Resolution (2026-07-01):** Renamed enum member `CompressionAlgorithm.GZip` → `Deflate`, keeping the
on-disk value `= 1`. Since the region format stores the numeric byte (not the name) and settings
persist the enum as an integer via `JsonUtility`, this is a source-only rename with **zero save
breakage** — no format-version bump or migration step. All call sites, the settings tooltip, and
`INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md` (§3.2/§3.3, v1.8) updated. Value `3` is
reserved for a *true* GZip codec (header/CRC) should it ever be wanted, added via AOT migration.

**Observed:** `CompressionFactory.CreateOutputStream`/`CreateInputStream`
(`CompressionFactory.cs` ~lines 65–66, 93–94) construct `DeflateStream` for
`CompressionAlgorithm.GZip`. Not a performance bug (Deflate is the same codec minus the GZip
header/CRC), but the label is wrong: payloads tagged "GZip" on disk are **raw Deflate**, which will
bite any future external tool, migration, or interop that trusts the name.

**Recommendation:** Do **not** "fix" this by swapping to `GZipStream` — that silently breaks every
existing save written with the current code (the fallback path when LZ4 is unavailable). Instead:
rename the enum member to `Deflate` (save formats store the enum value, not the name — verify
before renaming) or document the discrepancy at the enum and in
`INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md`. If true GZip is ever wanted, add it as
a **new** enum value via the AOT migration protocol.

> **Impact Analysis:**
> - **Effort:** 🟢 Low (documentation/rename).
> - **Risk:** 🟢 Low if rename-only; 🔴 High if anyone changes the stream class — hence this entry.
> - **Benefit:** ⚪ Low — correctness/clarity insurance, no runtime change.
> - **Seed/Save:** ✅ / ⚠️ **Save-format sensitive** — the bytes must not change without a format
    > version bump + migration step (`serialization-migration` skill).

---

## Detailed findings — GPU & Shaders

### GS-1. Liquid shader: per-pixel procedural 3D simplex FBM

**Observed:** `LiquidCore.hlsl` evaluates Ashima-style 3D simplex noise (`snoise`, ~60+ ALU ops
each) in **FBM loops per fragment**. At the High tier with dual-phase and refraction, one water
pixel evaluates roughly: 2 phases × (wave FBM 4-oct + ripple FBM 4-oct + stream FBM 3-oct) plus
2 × 3-oct refraction-normal FBMs ≈ **25–30 `snoise` calls per pixel**. Lava is comparable (plus
crust/spark FBMs). An ocean or lava lake covering half the screen is by far the most expensive
thing the GPU does — on a midrange Android GPU this alone can blow the entire frame budget.

The existing quality-tier keywords (`_FLUID_QUALITY_LOW/MED`, refraction opt-out) are the right
mechanism and already help, but even the Low tier pays 2-oct procedural simplex per pixel, and the
tier system reduces octaves rather than changing the *kind* of work.

**Recommendation (in increasing effort):**

1. **Pre-baked noise textures.** Replace procedural `snoise` FBM with 1–2 samples of a tiling,
   pre-baked FBM noise texture (scrolled/blended exactly like the current coordinates — the
   dual-phase flow-mapping logic is unchanged, only the noise *source* changes). Texture fetches
   are what mobile GPUs are good at; this typically cuts liquid fragment cost by 5–10×. A small
   3D texture (or 2 blended 2D samples to fake the third dimension) preserves the "boiling"
   vertical animation. The bake can be generated offline via `Tools/Python/` or an editor tool.
2. **Derive refraction normals from existing results.** The two extra FBM evaluations per phase
   (`normal_dx`/`normal_dz` finite differences) can come from the noise texture's precomputed
   gradient channels (RGBA: value + xy-gradient) for free instead of 2 more FBM evaluations.
3. **Cheaper dual-phase.** With texture-based noise, consider whether the Low tier can drop to a
   single phase with a time-sliced texture swap, removing the 2× multiplier entirely.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — noise bake asset + shader change; tier macros stay.
> - **Risk:** 🟡 Medium — visual character of water/lava will shift slightly (tile period,
    > gradient quality); needs eyes-on comparison per tier.
> - **Benefit:** 🟢 High — largest single GPU win available; transforms the worst-case mobile frame.
> - **Seed/Save:** ✅ / ✅.

---

### GS-2. Opaque Texture required globally; scene color sampled even without refraction

**Observed:** Two compounding costs:

1. The URP asset (`Assets/settings/Rendering/VoxelEngine-URP-Asset.asset`) sets
   `m_RequireOpaqueTexture: 1` globally — URP performs a **full-screen color copy every frame**,
   whether or not any liquid is visible. On mobile tile-based GPUs this also forces a render-target
   resolve/store, one of the most expensive operations on those architectures.
2. `UberLiquidShader.shader` calls `SampleSceneColor(distortedUV)` and composites manually via
   `lerp(background, color, alpha)` **even when `_FLUID_REFRACTION_OFF` is set** — with refraction
   off, `distortedUV` is just the undistorted screen UV, so the manual composite is mathematically
   equivalent to standard hardware alpha blending and the opaque texture isn't needed at all.

**Recommendation:** When refraction is off (which should be the mobile default), switch the liquid
pass to hardware alpha blending (`Blend SrcAlpha OneMinusSrcAlpha`, output alpha = the current lerp
factor) inside the `_FLUID_REFRACTION_OFF` variant — no `SampleSceneColor`, no opaque-texture
dependency. Then toggle `UniversalRenderPipelineAsset.supportsCameraOpaqueTexture` from
`GraphicsSettingsController` so the full-screen copy only exists when the refraction tier is
active. (Note `m_OpaqueDownsampling` is already set — keep downsampled opaque texture for the
refraction-on path; refracted water doesn't need full resolution.)

> **Impact Analysis:**
> - **Effort:** 🟢 Low — one shader variant + a settings hook.
> - **Risk:** 🟡 Medium — blending semantics for overlapping fluid faces must be checked (the
    > current manual composite reads pre-liquid opaque color; hardware blending composites over
    > whatever is in the framebuffer, including other transparent geometry — verify against the
    > transparent-blocks submesh ordering).
> - **Benefit:** 🟢 High on mobile — removes a per-frame full-screen copy + resolve; also a real
    > win on desktop at high resolutions.
> - **Seed/Save:** ✅ / ✅.

---

### GS-3. Voxel lighting math runs per-fragment on purely per-vertex data

**Observed:** `ApplyVoxelLightingRGB` (`VoxelLighting.hlsl`) computes 4 independent shade curves,
each ending in `pow(x, 2.2)` — **4 `pow` calls per fragment** in the opaque, transparent, and
liquid shaders. Every input (per-vertex light data + global uniforms) is available in the vertex
shader; only the final `color * multiplier` needs the fragment stage.

**Recommendation:** Compute the sun multiplier (`sunShadow * skyColor`) and block multiplier
(`half3` of the three channel shadows) in the vertex shader and interpolate them; the fragment
does `col.rgb *= max(sunContrib, blockContrib)` (or interpolate the combined `max` directly —
verify the visual difference across a face is acceptable; interpolating the two contributions
separately and taking `max` per-pixel is the closer match). Pixels vastly outnumber vertices in
voxel scenes, so this moves the `pow` chain to the cheap stage.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — shared include + V2F struct change.
> - **Risk:** 🟢 Low — minor interpolation differences across large faces; compare side-by-side
    > with the `DEBUG_LIGHTDATA` view.
> - **Benefit:** 🟡 Medium — meaningful fragment ALU reduction on mobile; small on desktop.
> - **Seed/Save:** ✅ / ✅.

---

### GS-4. Render pipeline tier audit (shadows, MSAA, render scale, shadow casting mode)

**Observed (current URP asset + code state):**

- `m_MainLightShadowsSupported: 1` with `m_ShadowDistance: 0` — shadows never *render* (distance
  0), but the support flag still compiles shadow shader variants and keeps the shadow-map keyword
  plumbing active. If this is permanent (the voxel sky-light system replaces shadows), set
  supported = 0 to strip variants; if shadows are ever enabled, note that…
- `SectionRenderer` sets `ShadowCastingMode.TwoSided` on **every section** — with shadows actually
  on, the entire voxel world would render twice-sided into a 2048 shadow map; that needs its own
  tiered decision (e.g. shadows only from a small radius, or baked/none on mobile).
- `m_MSAA: 2` — MSAA on a voxel world of opaque cubes buys little; on mobile it costs bandwidth
  (though tilers handle it relatively well). Should be a quality-tier setting, not baked into the
  asset.
- `m_RenderScale: 1` — no resolution scaling hook for mobile; exposing render scale in
  `GraphicsSettingsController` is the single most effective GPU lever on phones.

**Recommendation:** Make these per-tier: a mobile URP asset (or runtime overrides via
`UniversalRenderPipelineAsset` properties) with shadows-unsupported, MSAA off/2×, render scale
exposed as a setting, plus the GS-2 opaque-texture toggle. Desktop keeps the current values.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — settings/asset configuration, no engine code.
> - **Risk:** 🟢 Low.
> - **Benefit:** 🟡 Medium — variant stripping (build size + load time), bandwidth savings, and a
    > render-scale escape hatch on weak GPUs.
> - **Seed/Save:** ✅ / ✅.

---

### GS-5. Section occlusion culling — underground sections render despite being sealed

**Observed:** Fully empty sky sections are already skipped (no mesh, GameObject disabled), but
**every meshed subsurface section renders** even when completely sealed from the camera by solid
terrain — the "underground overdraw" problem. While walking on the surface above cave systems
(or being inside one), the majority of rendered sections are invisible. A previous count-based
attempt ("render only if connected to the section above, relative to the player") caused major
rendering corruption and was removed — scalar air/opaque counts cannot represent connectivity
topology, so any count heuristic both over-culls (holes) and under-culls. The sound solution is
graph-connectivity culling per `VISIBILITY_CULLING_ARCHITECTURE.md`, whose Phase 0 prerequisites
(section renderers, `nonAirCount`/`opaqueCount`, empty-section skipping) are complete; Phases 1–3
are open.

**Recommendation:** Implement the design doc's connectivity-mask + BFS architecture **with the
corrections in its new §7** (added alongside this entry): accumulated entry-face sets instead of
single-entry visited marks, Checchi direction restriction, `forceRenderingOff` ownership split
from `SetActive` (the likely cause of the old corruption), mask publication synchronized with mesh
apply, conservative defaults, and a position-only PVS without per-step frustum checks. Expected
win: the largest single rendering-side improvement available (draw calls, vertex work, Unity
culling overhead scale with loaded sections), growing further with taller worlds
(`WORLD_SCALING_ANALYSIS.md` Tier A) and carrying over unchanged to cubic chunks (Tier C).

> **Impact Analysis:**
> - **Effort:** 🔴 High — dedicated system (in-job flood fill + visibility manager + ownership
    > refactor), though cleanly phased in the design doc.
> - **Risk:** 🟡 Medium — over-culling bugs are visible holes; §7's rules + debug overlay make
    > them testable. Conservative failure direction (over-render) is designed in.
> - **Benefit:** 🟢 High — most subsurface sections stop rendering in normal play.
> - **Seed/Save:** ✅ / ✅ — masks are derived data, never persisted.

---

### GS-6. Per-section GameObject + MeshRenderer submission — BatchRendererGroup conversion

*(Surfaced by the 2026-07-02 third-pass audit — the structural complement to GS-5.)*

**Observed:** Every 16³ section is a pooled GameObject with its own `MeshFilter` + `MeshRenderer`
(`SectionRenderer`). At normal view distances that is thousands of live renderers, each paying
Unity's per-renderer overhead every frame: main-thread culling bookkeeping, transform/hierarchy
management, and per-object draw submission. GS-5 reduces *how many* sections render; this item
changes *what each section costs* to exist and be submitted. The two compound — but they also
interact (see below).

**Recommendation:** Long-horizon only; needs its own design doc when picked up. Convert section
rendering to `BatchRendererGroup` (BRG): meshes registered with a batch group, per-section
matrices and visibility handled in BRG's culling callback instead of per-GameObject renderers.
**Ordering interaction with GS-5:** BRG has no `forceRenderingOff` — visibility is expressed in the
culling callback's index output. Design the GS-5 `VisibilityManager` to *output a visible-section
set* consumed by a thin, swappable presentation layer (today: `forceRenderingOff` toggles; under
BRG: the culling callback), so the culler survives a later BRG conversion unchanged. A matching
note lives in `VISIBILITY_CULLING_ARCHITECTURE.md` §8.

> **Impact Analysis:**
> - **Effort:** 🔴 High — replaces the renderer layer (`SectionRenderer`, pooling, material paths).
> - **Risk:** 🔴 High — bespoke rendering path; per-platform validation, and every
    > renderer-adjacent behavior (mesh upload, bounds, layers, shadow-casting mode) must be
    > re-derived.
> - **Benefit:** 🟡 Medium on desktop today → 🟢 High at scale (thousands of sections, weak CPUs,
    > and any Tier A height increase that multiplies section counts).
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
— sized for desktop concurrency per the pipeline doc §1.1 notes), pool prune targets, and default
view/load distances. None of them consult the device. A phone with 3–4 GB of RAM and 4 slow cores
gets the same in-flight memory envelope as a 64 GB desktop — and *lower* caps are actually needed
there twice over: less RAM to hold the backlog **and** fewer cores to drain it.

**Recommendation:** Introduce a device-tier profile resolved once at startup from
`SystemInfo.systemMemorySize`, `SystemInfo.processorCount`, and platform: it scales the per-frame
job budgets, the in-flight job caps, `ChunkJobArrayPool` retention (e.g. `min(512, f(memory))`),
pool prune targets, and clamps the maximum selectable view distance. Per-frame budgets should also
become time-based rather than count-based where P-4 lands (the two compose: tier sets the budget,
P-4 enforces it per-second instead of per-frame).

> **Impact Analysis:**
> - **Effort:** 🟢 Low — a profile struct + plumbing into existing constants.
> - **Risk:** 🟢 Low — conservative tiers can only under-use fast devices until tuned.
> - **Benefit:** 🟢 High on mobile — shrinks every queue and pool ceiling to what the device can
    > actually drain and hold.
> - **Seed/Save:** ✅ / ✅.

---

### OM-2. No memory-pressure response: `Application.lowMemory` unused, no resident budget

**Observed:** Nothing in the codebase subscribes to `Application.lowMemory` (Unity's callback for
the OS memory-pressure signal on Android/iOS), and no system tracks total resident chunk memory.
The engine's only ceiling is "whatever the unloader manages to free" — and the unloader is exactly
what the documented §3.3 pinning problem disables under load. When the backlog wins, there is no
last line of defense between "degraded" and "killed by the OS".

**Recommendation:** Two layers:

1. **Resident-chunk budget (proactive).** Track loaded `ChunkData` count (a cheap proxy for memory;
   optionally refine with per-chunk section counts) against a tier-derived budget (OM-1). Crossing
   the budget triggers the §3.5 panic gate *keyed on memory, not queue length*: stop scheduling new
   generation, shrink the effective load radius, and let consumption catch up. This generalizes the
   pipeline doc's panic gate into the resource that actually kills the process.
2. **`Application.lowMemory` handler (reactive).** On the OS signal: halt generation scheduling,
   force the unload pass with a reduced radius (honoring pipeline invariants — prefer the §3.3 fix
   of persisting pending light columns so pinned chunks become unloadable), set all pool retention
   targets to zero and prune immediately, then `GC.Collect()` + `Resources.UnloadUnusedAssets()`.
   ⚠ Force-unload paths MUST go through the existing unload machinery — bypassing the
   `wouldStrandNeighbor` / pending-lighting checks trades an OOM crash for a lighting deadlock
   (see `chunk-lifecycle` skill).

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — the budget/gate is simple; the emergency unload must respect pipeline
    > invariants, which is where the care goes.
> - **Risk:** 🟡 Medium — interacts with the deadlock-prone unload gates; test with the benchmark
    > stress run on a memory-capped device/emulator.
> - **Benefit:** 🟢 High — converts the observed hard crash into a visible degradation (shorter
    > view distance, slower streaming).
> - **Seed/Save:** ✅ / ✅.

---

### OM-3. Unbounded concurrent chunk saves on mass unload

**Observed:** `World.UnloadChunks` fires `StorageManager.SaveChunkAsync(data, …)` for every
unloaded chunk (`World.cs` ~line 1986; same pattern at ~3135), each of which snapshots the chunk
and queues a `Task.Run` to the ThreadPool. During fast movement, a single unload pass can launch
**hundreds of concurrent save tasks**: each holds a pooled snapshot until its turn (a memory spike
proportional to the burst, on top of the already-stressed heap), and the ThreadPool spawns/queues
threads that compete with Unity's job workers for the few cores a CPU-starved device has — slowing
down exactly the lighting/meshing drain that the backlog needs.

**Recommendation:** Replace fire-and-forget saves with a **bounded producer-consumer save queue**:
a fixed small number of writer workers (1–2; region files are lock-serialized anyway per
`REGION_FILE_CONCURRENCY.md`, so more writers mostly just contend) consuming from a channel with a
bounded snapshot count. When the bound is hit, defer the unload of further chunks to the next frame
(natural backpressure — the chunk simply stays loaded a little longer) rather than queueing
unboundedly. Shutdown flushes the queue synchronously (the existing cancellation-token path
already models this).

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — a save-queue service in `ChunkStorageManager` + unload-path change.
> - **Risk:** 🟡 Medium — must not lose saves on quit/crash (flush ordering), and deferred unload
    > must not fight the OM-2 emergency path (emergency mode should raise the writer count/priority,
    > not bypass the queue).
> - **Benefit:** 🟢 High on weak CPUs — caps the unload-burst memory spike and stops ThreadPool
    > oversubscription from starving the job system.
> - **Seed/Save:** ✅ / ✅ — same bytes written, only scheduling changes.

---

## Detailed findings — Serialization & Save/Load

> **Context:** the disk **read** path had never been audited (OM-3 covers only the save-*burst*
> scheduling side; MT-6 was a naming fix). These items are the 2026-07-02 fourth-pass findings over
> `RegionFile` → `ChunkSerializer` → `ChunkStorageManager` → `World.LoadOrGenerateChunk`. All edits
> here are byte-layout-neutral — but this is save-system code, so the `serialization-safety` rules
> apply to every change regardless.

### SL-1. Per-chunk managed allocations on the load/save path

**Observed:** Each streamed-in chunk allocates on the load path: the compressed payload `byte[]`
(`RegionFile.LoadChunkData`, `RegionFile.cs` ~line 147 — typically tens of KB), a 4-byte length
header array, a 512 B `reader.ReadBytes(...)` heightmap array (`ChunkSerializer.cs` ~line 209 —
inconsistent with the sections, which correctly stream into pooled arrays via `ReadBulkData`),
`Enum.IsDefined` reflection per load (`RegionFile.cs` ~line 139), plus per-load
decompression-stream/`BinaryReader` wrapper objects and the `Task.Run` closure. Each saved chunk
allocates: two `BitConverter.GetBytes` arrays, a zero `pad` array up to ~4 KB
(`RegionFile.cs` ~line 231), a `new ChunkSection[8]` snapshot array (`WriteChunkInternal`), and
`MemoryStream`/`BinaryWriter`/compression-stream wrappers. The `SerializationBufferPool` exists but
covers only the serialize-side output buffer. All of this runs on ThreadPool threads, but GC is
process-wide — the allocation rate scales with streaming speed and contributes to the collections
that pause the main thread.

**Recommendation:** Extend `SerializationBufferPool` with a length-aware rent for the read payload
(`Deserialize` already takes `ReadOnlySpan<byte>`, so a pooled oversized buffer slices for free);
read the heightmap via the existing `ReadBulkData` span path into a pooled/stack buffer; replace
`Enum.IsDefined` with a range check against the known enum values; keep a static zero-pad buffer;
write the two 4-byte headers via stackalloc spans (`Stream.Write(ReadOnlySpan<byte>)`).

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — mechanical, but spread across three files and both directions.
> - **Risk:** 🟡 Medium — save-system code (bytes must stay identical — verify with a
    > round-trip diff of a saved world before/after); pooled-buffer lifetime across `Task.Run`.
> - **Benefit:** 🟡 Medium — removes the dominant steady-state GC source outside the main thread
    > during streaming; biggest on weak devices where GC pauses are longest.
> - **Seed/Save:** ✅ / ✅ — identical bytes, allocation strategy only.

---

### SL-2. Disk-load apply path runs unbudgeted on the main thread

**Observed:** After `await StorageManager.LoadChunkAsync(...)`, the continuation of
`World.LoadOrGenerateChunk` (`World.cs` ~lines 779–941) runs on the main thread and performs, per
loaded chunk: `PopulateFromSave` (section ownership transfer + light-queue re-enqueue),
`OnDataPopulated` (the TG-2 bitmask scan — up to 32k reads on this path by design), pending-mod
replay, pending-blocklight replay, a `new HashSet<Vector2Int>` for restored lighting columns (the
generation twin in `ProcessGenerationJobs` uses `HashSetPool` — this path doesn't), and — when
neighbors are ready — `RecalculateSunLightLight()`, a full 16×16-column sunlight seed walk.
**There is no per-frame budget:** every load whose I/O completes gets its continuation the same
frame. The generation path drains through `ProcessGenerationJobs` under `maxStructureModsPerFrame`;
the load path has no equivalent, so a fast flight over saved terrain produces uncapped
multi-chunk apply bursts in single frames.

**Recommendation:** Instead of applying in the continuation, push loaded `ChunkData` into a
completion queue drained by a budgeted per-frame pump (mirror `ProcessGenerationJobs`, which
already handles the identical staging steps for generated chunks — potential to share the code).
Pool the lighting-columns `HashSet` while there. ⚠ The apply steps fire pipeline events
(`PromoteNeighborhood`, staging callbacks) — respect the flag-pairing invariants
(`chunk-lifecycle` skill) when moving them.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — a queue + pump; the steps themselves move verbatim.
> - **Risk:** 🟡 Medium — pipeline-adjacent (deferred apply changes when neighbor-readiness flips);
    > the unload-during-await guard at `World.cs:781` must carry over to the queued form.
> - **Benefit:** 🟡 Medium — converts load-burst frame spikes into bounded per-frame work, exactly
    > like the generation side already does; most visible when re-visiting saved terrain fast.
> - **Seed/Save:** ✅ / ✅.

---

### SL-3. `SaveChunkAsync` snapshots up to ~190 KB per chunk on the main thread

**Observed:** `ChunkStorageManager.CreateSerializationSnapshot` (`ChunkStorageManager.cs` ~line 214)
runs on the calling (main) thread before each async save: per non-null section it rents a pooled
section and copies 16 KB of voxels plus (for non-compact sections) 8 KB of LightData — up to
~190 KB of memcpy per chunk — plus both BFS queues under lock. During a mass-unload burst this
multiplies by OM-3's unbounded save count: hundreds of snapshots in one frame, each also renting
pooled sections that stay checked out until the ThreadPool worker finishes.

**Recommendation:** Solve together with OM-3's bounded save queue: enqueue the *chunk reference*
and take the snapshot at **dequeue** time inside the bounded writer's main-thread slot (a few per
frame), so both the memcpy and the pooled-section retention are capped by the queue bound instead
of the unload burst size. Independent extra: skip the LightData copy for compact sections is
already implemented — the remaining copy is voxels, which a dirty-section mask (sections unchanged
since load need no save at all) would shrink further; that needs per-section dirty tracking and
should be its own follow-up if profiling justifies it.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — folds into the OM-3 implementation; snapshot-at-dequeue needs a
    > "chunk still loaded & unchanged" revalidation.
> - **Risk:** 🟡 Medium — a chunk can be modified between unload-request and snapshot; the dequeue
    > slot must snapshot the *current* state (which is also more correct than today's
    > frozen-at-burst state).
> - **Benefit:** 🟡 Medium — caps the unload-burst main-thread memcpy and pool pressure; pairs with
    > OM-3's memory-spike cap.
> - **Seed/Save:** ✅ / ✅ — same bytes, taken later.

---

### SL-4. Whole-file region lock serializes chunk loads behind saves

**Observed:** All `RegionFile` reads and writes share one `lock (_fileLock)`
(`RegionFile.cs` ~line 25 — the TODO there already names the problem): a chunk load stalls behind
any in-flight save to the same region, and concurrent loads of neighboring chunks (which cluster
in the same region file by construction) serialize each other. During streaming-while-saving the
read path — which gameplay is waiting on — queues behind write I/O.

**Recommendation:** The full analysis and the recommended design (concurrent reads via
`System.IO.RandomAccess` stateless offset reads or a `FileStream` pool + single-writer discipline,
with the metadata tables under an exclusive lock) already exists in
**[`REGION_FILE_CONCURRENCY.md`](REGION_FILE_CONCURRENCY.md)** — this entry tracks it in the master
backlog. Implement the hybrid (§3 of that doc) or `RandomAccess` (§4) variant; keep every
`_offsets`/`_sectorUsage` mutation exclusive.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — the read side is a contained change; the invariants are documented.
> - **Risk:** 🔴 High — concurrency bugs here corrupt saves; the doc's §"Critical Requirements"
    > (metadata sync, resize safety, atomic offset-table update) are hard gates, and a
    > corruption-focused stress test (parallel load/save hammering one region) must exist first.
> - **Benefit:** 🟡 Medium — removes load-behind-save stalls during streaming; compounds with SL-2
    > (budgeted apply) and OM-3 (bounded writers, which also shrink the write side of the contention).
> - **Seed/Save:** ✅ / ✅ — same bytes; only lock granularity changes.

---

## Detailed findings — Voxel Queries, Interaction & Physics

> **Context:** every per-frame gameplay consumer — the physics solver, the interaction ray, the
> placement probe, pending-mod application, and the managed grass tick (TG-1's residual) — funnels
> through one query API. TG-1/TG-4 fixed this *for the fluid tick* by bypassing it; the API itself
> and its remaining consumers were never audited until this fourth pass.

### VQ-1. `GetVoxelState` float path — duplicated chunk math, nullable + managed deref per query

**Observed:** `WorldData.GetVoxelState(Vector3)` (`WorldData.cs` ~line 189) costs, per query:
float world-bounds compares (`IsVoxelInWorld`), `GetChunkCoordFor` (2 float divides + 2
`FloorToInt`), a dictionary `TryGetValue`, then `GetLocalVoxelPositionInChunk` — which **calls
`GetChunkCoordFor` again** (the chunk coord is computed twice per query) — plus 3 more
`FloorToInt`, a `VoxelState?` nullable wrap, and at most callers a managed `BlockType` array deref.
Integer-coordinate callers (`CheckPhysicsCollision` passes `Vector3Int` voxel positions) round-trip
int → float → floored int. Per-frame call volume: the physics solver (12–18 cells × up to 7 sweeps
× substeps per FixedUpdate — see PH-1), the placement march (~reach/checkIncrement calls per frame
— see VQ-2), pending-mod apply, and the grass tick.

**Recommendation:** Add an integer fast path — `bool TryGetVoxel(int x, int y, int z, out
VoxelState state)` — built on the WS-1 shift/mask helpers (this item is the *runtime API half of
WS-1*; implement them together): one chunk-coord computation, no floats, no nullable. Add a
one-entry "last chunk" cache (query bursts — an AABB scan, a ray march — overwhelmingly hit the
same chunk, turning the dictionary lookup into a compare). Keep the `Vector3` overload as a
floor-then-delegate wrapper. Migrate the hot consumers (physics, march, mods) first.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — new overload + WS-1 helpers + consumer migration.
> - **Risk:** 🟡 Medium — the float→int floor semantics at negative-fraction boundaries must be
    > preserved exactly (guard with an equivalence sweep, same harness as WS-1); the placement
    > suite (13 baselines) covers the interaction consumers.
> - **Benefit:** 🟡 Medium — cuts the constant per-frame query tax for every consumer at once, and
    > removes the last float coordinate path standing in Tier B's way.
> - **Seed/Save:** ✅ / ✅.

---

### VQ-2. Placement ray marches by fixed increment instead of DDA

**Observed:** `PlacementController.MarchRay` (`PlacementController.cs` ~line 88) samples the ray at
fixed `checkIncrement` steps, calling `World.CheckForVoxel` → `GetVoxelState` per step —
~reach/checkIncrement queries per call, and `PlayerInteraction.PlaceCursorBlocks` probes **every
frame**. Fixed-step sampling also has two correctness edges: a step can skip a cell clipped
diagonally (block-corner misses at any increment), and the entered-face normal is *derived after
the fact* from the hit point's fractional offsets (`FaceNormal`), which can name the wrong face on
near-corner hits.

**Recommendation:** Replace the march with a DDA voxel traversal (Amanatides–Woo): visits exactly
the cells the ray crosses (≤ ~3 × reach queries instead of reach/increment), never skips a cell,
and yields the entered face as a byproduct (deleting the `FaceNormal` fractional heuristic).
`checkIncrement` disappears as a setting. ⚠ This intentionally *changes* behavior on the edge
cases (more correct hits); the placement validation suite's 13 baselines gate the change, and any
baseline that encoded a sampling artifact needs re-derivation with eyes on it — treat baseline
diffs as findings, not failures.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — a contained, well-known algorithm; the decision layer above is untouched.
> - **Risk:** 🟡 Medium — player-facing targeting feel; corner-case behavior changes by design.
> - **Benefit:** ⚪ Low as pure perf (one ray/frame) — the win is correctness + removing a tuning
    > knob; perf becomes real if rays multiply (mobs, projectiles).
> - **Seed/Save:** ✅ / ✅.

---

### PH-1. Collision solver re-queries the same voxel neighborhood across sweeps and substeps

**Observed:** `VoxelRigidbody.ResolveMovement` (`VoxelRigidbody.cs` ~line 224) calls
`World.CheckPhysicsCollision` up to ~7 times per resolve (horizontal pre-pass ×2, step-up probe ×2

+ downward sweep, per-axis resolve ×2, vertical/ground check), and each call independently rescans
  the entity's AABB voxel range (typically 12–18 cells) through the full VQ-1 float path — nullable
  unwrap, managed `BlockType` deref, and (for custom-bounds blocks) a rotation-matrix computation
  per cell *per sweep*. Fast movement multiplies the whole resolve by up to
  `ceil(displacement / 0.125)` substeps (`CalculateVelocity`), each also writing
  `transform.position` twice. Worst case is a few hundred voxel queries per FixedUpdate for one
  entity.

**Recommendation:** Gather once, sweep many: at the top of `ResolveMovement` (or once per
substep chain over the union AABB), collect the overlapped cells into a stack buffer of
`(blockBounds, isSolid)` entries — computing each cell's custom-bounds rotation exactly once — and
run all sweeps against that buffer. Combine with VQ-1's integer path for the gather itself. The
substep transform writes can accumulate into a local and apply once.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — restructures the solver's query pattern; the resolution math is untouched.
> - **Risk:** 🟡 Medium — the step-up sweep reads *lifted* AABBs (cells outside the initial range —
    > the gather must cover the step-height envelope); physics feel regressions are subtle, so
    > verify with the sub-voxel collision doc's test scenarios (`SUB_VOXEL_COLLISION_SYSTEM.md`).
> - **Benefit:** ⚪ Low with one player — linear with future entity count; this is the solver every
    > mob/item will run.
> - **Seed/Save:** ✅ / ✅.

---

## Detailed findings — Startup & World Load

> **Context:** MT-4/MT-5 fixed two specific startup allocations and OM-1 added device calibration,
> but the world-load coroutine (`World.cs` STEP 2/3 + `ForceCompleteDataJobsCoroutine`) was never
> audited end-to-end. The existing per-phase stopwatch instrumentation is good — keep it; these two
> items are about *throughput*, not measurement.

### SU-1. Loading screen throttled by gameplay-tuned per-frame budgets

**Observed:** The blocking startup phases run through the same per-frame budgets that protect
gameplay frame time: `ForceCompleteDataJobsCoroutine` PHASE 1 yields a frame per sweep with
`ProcessGenerationJobs` bounded by `maxStructureModsPerFrame`, and after STEP 3 hands off to
`Update()`, the initial *meshing* wave drains at `maxMeshRebuildsPerFrame` (10) and the in-flight
mesh cap (20) — budgets tuned to preserve 60 FPS for a player who, at this moment, is looking at a
loading screen. Nothing during the load screen needs frame-rate protection; the budgets purely
stretch time-to-playable.

**Recommendation:** Introduce a loading-mode budget multiplier (e.g. ×4–8 on the per-frame counts,
or switch to a time-sliced ~100 ms/frame budget) active while `_isWorldLoaded == false`, reverting
on handoff. OM-1's device tier supplies the safe ceiling (a phone's loading mode is smaller than a
desktop's). Keep the safety-break iteration caps — scale them with the multiplier so the timeout
semantics don't tighten.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — a multiplier read at the existing budget sites.
> - **Risk:** 🟡 Medium — bigger bursts stress the same queues P-4 wants to bound; the lighting
    > fail-safes and safety breaks must scale with the multiplier, not race it.
> - **Benefit:** 🟡 Medium — directly cuts time-to-playable, the most user-visible startup metric.
> - **Seed/Save:** ✅ / ✅.

---

### SU-2. Initial load schedules generation + disk loads for the whole radius at once

**Observed:** STEP 2 (`World.cs` ~lines 630–665) fires `LoadOrGenerateChunk` for every chunk in
the `(initialLoadRadius + 1)` square simultaneously: each disk miss immediately calls
`JobManager.ScheduleGeneration` — there is no in-flight cap on this path — so a radius-10 start
allocates ~440+ concurrent `GenerationJobData` buffer sets (~230 KB each per WG-1: ≈ **~100 MB of
native buffers live at once**), and each disk hit spawns a ThreadPool load task in the same burst
(the read-side mirror of OM-3's write burst). On memory-tight devices the startup burst is the
first OOM opportunity, before streaming ever begins.

**Recommendation:** Schedule the initial wave ring-by-ring (inner rings first — they're also the
ones `chunksToWaitFor` blocks on) with a bounded in-flight count. P-4's in-flight caps give this
for free if implemented globally — implement SU-2 as "P-4's caps also apply during startup" rather
than a separate mechanism, sized by the OM-1 tier and raised by SU-1's loading-mode multiplier.
WG-1's pooling then bounds the buffer memory to the cap × per-chunk size.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — folds into P-4; standalone ring scheduling is also simple.
> - **Risk:** 🟡 Medium — ordering interacts with the lighting-neighbor gates (the +1 buffer ring
    > must still land before the wait ring finishes lighting); the startup coroutine's convergence
    > loop already tolerates arbitrary completion order.
> - **Benefit:** 🟡 Medium — caps startup native-memory and ThreadPool bursts; prerequisite-grade
    > on mobile (pairs with OM-1/OM-2).
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

**Observed:** Switching `visualizationMode` queues **every active chunk** for visualization
(`World.HandleVisualization`, `World.cs` ~line 2734), and the processing loop (~line 2767) drains
**all ready chunks in a single frame**: per chunk, a full section scan (`Sunlight`/`Blocklight`/
`FluidLevel` visit every voxel of every non-empty section and insert every lit/non-air voxel into a
`Dictionary<Vector3Int, Color>` — thousands of entries per chunk), then the DT-2 conversion + job
schedule; `VoxelVisualizer.LateUpdate` then completes and applies every finished mesh, also
unbudgeted. At a few hundred active chunks the toggle is a multi-hundred-ms hitch. Worse, **while a
mode is active** every voxel modification re-queues the chunk plus border neighbors
(`World.cs` ~line 1853) for a *full rescan* — an ocean flood with the FluidLevel overlay on
re-scans the entire flood front every tick batch, precisely when you're trying to watch it.

**Recommendation:** Drain the update set through a small per-frame budget (K chunks/frame,
nearest-player first — the `MeshBuildQueue` pattern at debug scale), and rate-limit re-visualization
of the same chunk (minimum interval, e.g. 250 ms) so tick-driven churn coalesces instead of
rescanning per edit. Apply the same budget to the `LateUpdate` apply loop.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — a counter + interval check around existing loops.
> - **Risk:** 🟢 Low — debug-only; slightly stale overlays are acceptable by design (the readiness
    > gate already skips chunks mid-lighting).
> - **Benefit:** ⚪ — but converts the overlay from "unusable during heavy simulation" to a real
    > diagnostic tool for exactly those scenarios (fluid floods, lighting waves).
> - **Seed/Save:** ✅ / ✅.

---

### DT-2. `VisualizerChunkData` per-update native churn and apply-path allocations

**Observed:** Every chunk visualization update allocates **eight `Allocator.Persistent`
containers** (5 `NativeHashMap` + 3 `NativeList`, `VisualizerChunkData.PrepareJobData`) and
disposes them after apply — the exact alloc/free-per-use pattern MR-6/TG-6/WG-1 eliminate
elsewhere, at ~N-chunks-per-refresh frequency under DT-1's churn. The apply path adds:
`Triangles.AsArray().ToArray()` — a **managed index array per apply** (`VisualizerChunkData.cs`
~line 138; `SetIndices`/`SetIndexBufferData` accept the `NativeArray` directly) — and
`RecalculateBounds()` per apply despite the constant 16×128×16 chunk cell (the MR-4 twin). Finally,
`VoxelVisualizer.UpdateChunkVisualization` (~line 127) calls `JobHandle.Complete()` on re-entry — a
synchronous stall whenever a chunk is re-visualized while its previous job is still running (DT-1's
churn makes that common).

**Recommendation:** Retain the containers across updates on the pooled `VisualizerChunkData`
(allocate once, `Clear()` per use — capacity survives; dispose only in `Destroy()`, per the
pool-reset-safety rules for native containers). Replace `ToArray()` with
`_mesh.SetIndices(Triangles.AsArray(), MeshTopology.Triangles, 0)`, and assign the constant chunk
bounds instead of recalculating. On re-entry, skip-and-requeue instead of blocking on the in-flight
job.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — established patterns, one class.
> - **Risk:** 🟢 Low — debug-only; retained containers must follow pool-reset-safety (clear on
    > reuse, dispose in `Destroy()`).
> - **Benefit:** ⚪ — removes native churn + GC from active-overlay sessions so captures taken with
    > an overlay up stay representative.
> - **Seed/Save:** ✅ / ✅.

---

### DT-3. Visualization update-set fed on every voxel edit even when disabled

**Observed:** The voxel-modification path calls `AddChunksToUpdateVisualization` unconditionally
(`World.cs` ~lines 1853–1859) — including when `visualizationMode == None`, which is every frame of
normal play. The `_chunksToUpdateVisualization` set only drains while a mode is active, so during
normal play it just accumulates (a `HashSet` op per modified chunk per tick batch on the hot
modification path, plus growth to every-chunk-ever-touched, including long-unloaded coords that the
next mode activation then processes as dead lookups).

**Recommendation:** Gate the adds on `visualizationMode != None` (one branch — the mode-switch
handler already queues all active chunks, so nothing is lost while disabled) and clear the set when
switching to `None`.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — a guard + a `Clear()`.
> - **Risk:** 🟢 Low.
> - **Benefit:** ⚪ — makes the disabled debug stack genuinely zero-cost on the modification hot
    > path (fluid ticks), and keeps stale coords out of the first activation.
> - **Seed/Save:** ✅ / ✅.

---

### DT-4. Debug HUD/overlay allocation leftovers post-MT-3

**Observed:** MT-3 made the `DebugScreen` text refresh zero-alloc, but three neighbors missed the
pass: (1) `DebugScreen.HandleNewMetrics` allocates two temp `float[]`s per metrics sample
(`new[] { snapshot.CpuTimeMs, ... }`, ~20 Hz while the perf panel is visible — allocations that
appear **in the GC graph being displayed**); (2) `GraphRenderer` label refreshes go through
`string.Format(yFormat, …)` / `string.Format(xFormat, …)` per label (`GraphRenderer.cs` lines
235/258/311/334); (3) `TerrainGenDebugOverlay.OnGUI` builds interpolated strings per IMGUI event
(layout + repaint ≥2×/frame while active) for its ~10 labels. Related always-on note:
`PerformanceMonitor` samples its phase stopwatches every frame regardless of HUD visibility —
**this is deliberate and must stay**: the history ring buffer is what makes a hitch that happened
*while the HUD was closed* still visible when it is opened afterwards (`SyncGraphsWithHistory` →
`InjectHistory`). Cost is ~µs/frame, accepted by design — do not gate it on HUD visibility.

**Recommendation:** Give `GraphRenderer.AddSamples` a fixed-arity overload (or a reused sample
buffer); route graph labels through the shared `StringBuilderFormat` helpers MT-3 created (and only
on value change — grid labels rarely change); convert the overlay's static labels to cached strings

+ `StringBuilderFormat` for the dynamic ones (or migrate the panel off IMGUI onto the DebugScreen's
  TMP stack). `PerformanceMonitor`'s always-on sampling is out of scope (deliberate, see above).

> **Impact Analysis:**
> - **Effort:** 🟢 Low — MT-3's helpers already exist; this is finishing the sweep.
> - **Risk:** 🟢 Low.
> - **Benefit:** ⚪ — the perf HUD stops polluting its own GC metric; overlay sessions stop adding
    > IMGUI noise to captures.
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

**Observed:** `WorldGenPreviewWindow.CrossSection`'s `GenerateThreePanelPreview` evaluates every
column of up to three panels via the managed `EvaluateColumn` (`WorldGenPreviewWindow.CrossSection.cs`
~line 1068) — serial, on the main thread, span up to 2048 columns × 128 voxels each, per panel, per
regeneration (debounced to 0.1 s, so effectively per slider tick with live update on). Per-column
managed allocations compound it (`new ushort[128]` per column, `new byte[128]`×2 with the cave
filter, a `Color[span×128]` per panel — 16 B/pixel), and the result goes through the slow
`SetPixels(Color[])` path. The sibling tabs already solved this: `NoisePreviewJob` /
`WorldBlendingPreviewJob` are `IJobParallelFor` Burst jobs writing RGBA32. At X512+ the
Cross-Section tab visibly freezes the editor per regenerate; higher resolutions are seconds.

**Recommendation:** Port the column evaluation to an `IJobParallelFor` over columns (the input
structs — `CrossSectionNativeData`, `FastNoiseLite`, `BurstSpline`, `BiomeBlender` — are already
Burst-compatible; the worm masks are already `NativeBitArray`), write `Color32` into a
`NativeArray` uploaded via `LoadRawTextureData`, and keep the flora/crosshair annotations as a
managed post-pass. Best implemented **on top of ET-2's shared evaluator** so the port doesn't
duplicate the logic a third time.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — the job pattern exists in-repo; the evaluator port is the work (see ET-2).
> - **Risk:** 🟢 Low — preview-only output; compare screenshots before/after.
> - **Benefit:** ⚪ (dev-time) — seconds → tens of ms per regenerate at high resolution; makes live
    > slider scrubbing actually live.
> - **Seed/Save:** ✅ / ✅.

---

### ET-2. Preview replicates production logic — column shaping and replacement rules diverge

**Observed:** Two replications, different severity:

1. **Terrain column shaping.** `EvaluateColumn` is a ~300-line managed re-implementation of
   `StandardChunkGenerationJob`'s per-column logic (its own docstring says "replicating
   StandardChunkGenerationJob logic"): biome selection, multi-noise height, density band, strata,
   caves, lodes, water. It shares the *primitives* (`BiomeBlender`, `BurstSpline`, `FastNoiseLite`)
   but not the *sequence* — every generator change must be hand-mirrored or the Cross-Section
   preview silently drifts from what the game generates. This is the same drift class the meshing
   suite exists to prevent, with no guard.
2. **Replacement rules (live divergence).** `ChunkPreview3DWindow.ApplyVoxelModToMap`
   (`ChunkPreview3DWindow.Pipeline.cs` ~line 205) hand-rolls the structure-mod replacement decision
   (`Default` ≈ "replace unless solid && !transparent-for-mesh"), while production routes
   `VoxelModSource.WorldGen` mods through the `worldGenCanReplaceTags` tag mask. **The 3D preview
   can therefore show structure placements the game would reject, and vice versa** — a correctness
   gap in the authoring tool, not just hygiene.

**Recommendation:** Extract shared single-source implementations callable from both sides, the
`BiomeBlender` pattern scaled up: (a) a static Burst-compatible **single-column evaluator** that
`StandardChunkGenerationJob` calls per column and the preview calls per pixel-column — gated on
**byte-identical generation output** (fixed-seed differential over representative chunks, plus the
`ChunkGenerationBenchmark` as regression canary); (b) a shared **worldgen replacement-rule
resolver** used by `ProcessGenerationJobs`' apply path and the preview's `ApplyVoxelModToMap`.
Add a small editor validation ("preview column == job column for N random columns") so the drift
class stays dead.

> **Impact Analysis:**
> - **Effort:** 🔴 High — restructures the generation job's inner loop into a shared evaluator;
    > the replacement-rule share (b) is 🟢-sized and can ship first.
> - **Risk:** 🟡 Medium — touching the generation job carries seed risk; the differential gate is
    > mandatory, not optional.
> - **Benefit:** 🟡 Medium — kills a permanent hand-sync tax and an active preview-vs-game
    > correctness gap; ET-1's Burst port then comes almost for free.
> - **Seed/Save:** ⚠️ **Seed-sensitive** — same contract as WG-3: the extraction must be
    > output-preserving, byte-identical for fixed seeds (this is the second exception in the
    > report's seed-breaking note). / ✅.

---

### ET-3. 3D-preview pipeline: snapshot copies, full-grid lighting re-passes, dead copy-back

**Observed:** Three compounding costs in `ChunkPreview3DWindow.Pipeline` + `EditorChunkPipelineRunner`,
all `Allocator.Persistent` traffic on the editor main thread:

1. **Full snapshot copies per job.** `ScheduleLighting` copies the center + 8 neighbor voxel maps,
   heightmap, and 9 light maps into fresh Persistent arrays (~18 full-chunk copies ≈ ~2.5 MB per
   job); `ScheduleMeshing` does the same 19-buffer dance with a disposal-handle array. The sources
   are the window's own `_chunkMaps`/`_chunkLightMaps` dictionaries, which are **stable during each
   phase** — the copies exist only as lifetime insurance.
2. **Full-grid ×5 lighting fixpoint.** `ScheduleAllLighting` re-schedules **every** chunk each
   iteration (up to `MAX_LIGHTING_ITERATIONS = 5`) regardless of which chunks reported
   `IsStable` — production re-lights only dirty chunks. A radius-4 preview is ~100 chunks × up to
   5 passes × the item-1 copies ≈ **~1.5 GB of transient native allocations per preview build**.
3. **Dead voxel-map copy-back.** `PollLighting` (~line 321) disposes and re-copies the *voxel* map
   from the completed job every pass — but the lighting job never writes voxels (light lives in
   the ushort light map since the RGB split). 128 KB × chunks × passes of pure waste. Similarly,
   `PollGeneration` copies `data.Map` into storage instead of taking ownership of the job's buffer
   it is about to dispose.

**Recommendation:** In order of value: drop the copy-back (3 — one-line class of fix); track
per-chunk stability and re-light only unstable chunks + mod-touched neighbors (2); transfer
ownership of generation outputs instead of copying, and let lighting/meshing jobs read the stored
dictionaries directly with the phase acting as the lifetime fence (1) — falling back to a pooled
copy only where aliasing is real. The runner also allocates the two padded halo volumes (~306 KB)
fresh per lighting job — reuse per-slot buffers across the passes.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — lifetime reasoning in (1) is the care point; (2)/(3) are contained.
> - **Risk:** 🟢 Low — editor-only; wrong lifetimes fail loudly with the safety system on.
> - **Benefit:** ⚪ (dev-time) — preview builds drop from multi-GB churn + long waits to roughly
    > production-shaped costs; radius stops being capped by patience.
> - **Seed/Save:** ✅ / ✅.

---

### ET-4. `MeshPostProcessJob` runs synchronously per chunk in the preview (MR-5 not mirrored)

**Observed:** `ChunkPreview3DWindow.ConvertMeshOutput` (`ChunkPreview3DWindow.Rendering.cs` ~line 37)
runs `postProcessJob.Schedule().Complete()` on the main thread per meshed chunk — the exact
pattern MR-5 removed from production, where the post-process is chained onto the mesh job at
schedule time and is already done by the time the poll sees the handle complete. Minor sibling:
`mesh.RecalculateBounds()` per section (~line 122) despite the constant 16³ section cell (MR-4's
constant-bounds fix applies; the clip-bounds feature only shrinks geometry, so the constant cell
stays a valid conservative bound).

**Recommendation:** Chain the post-process inside `EditorChunkPipelineRunner.ScheduleMeshing`
(`postJob.Schedule(meshJobHandle)`), exactly as `WorldJobManager.ScheduleMeshing` does, and return
the combined handle; assign constant section bounds in `ConvertMeshOutput`.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — mirror an existing production change.
> - **Risk:** 🟢 Low — same data-flow guarantees as production (B10 proved the chaining
    > byte-identical there).
> - **Benefit:** ⚪ (dev-time) — removes a per-chunk main-thread stall from the preview's meshing
    > phase.
> - **Seed/Save:** ✅ / ✅.

---

## Detailed findings — Validation Suites

> **Context:** what these suites already do right is the seventh-pass audit note's list (top of this
> report) — the testing architecture itself needs no rework, and coverage gaps stay tracked in the
> fidelity docs. Suites that *don't exist yet* (serialization, worldgen determinism, pipeline state
> machine, physics, coordinate math, pool reset) are ranked in
> [`VALIDATION_SUITE_COVERAGE_ROADMAP.md`](VALIDATION_SUITE_COVERAGE_ROADMAP.md). The three items
> below are the operational layer around the existing tests: the runner, the way the suites are
> invoked, and one documented foot-gun. All three are behavior-preserving for
> the scenarios themselves — after VS-1, every suite must produce the same pass/fail verdicts it
> does today (run each before/after as its own gate).

### VS-1. Suite-runner scaffolding copy-pasted across all six suites

**Observed:** Every suite entry file re-declares the same private `Scenario` struct and the same
`RunAll` body — scenario loop, try/catch, baseline vs known-bug counting, colorized summary — as
near-byte-identical copies (~90 lines × 6: `LightingValidationSuite.cs`,
`MeshingValidationSuite.cs`, `BehaviorValidationSuite.cs`, `PlacementValidationSuite.cs`,
`MeshBuildQueueValidationSuite.cs`, `LightWorkSchedulerValidationSuite.cs` — diff the first two to
see the drift already starting: "may be fixed → archive" vs "may be implemented → promote").
Per-suite `Check(label, condition)` PASS/FAIL logging primitives repeat the same way, and the three
standalone test files use a third ad-hoc pattern each. The shared `Framework/` folder already
proves the extraction works (`ValidationReflection` was created precisely because two harness
copies were drifting; `GoldenMaster` likewise).

**Recommendation:** Extract a `Framework/ValidationSuiteRunner`: public `Scenario` type
(name, body, known-bug id), the categorized run loop, the summary formatting, and — while there —
**per-scenario and total wall-clock timing** in the summary (today a scenario that becomes
pathologically slow gives no signal; the lighting suite's 55 baselines including 50-seed fuzzes
would get a per-line ms column for free). Each suite's entry file shrinks to its menu item + suite
name + scenario registration. VS-2 and VS-3 then land in one place instead of six.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — mechanical but touches all six entry files + three standalone tests.
> - **Risk:** 🟢 Low — behavior-preserving; gate = every suite reports identical verdicts
    > before/after.
> - **Benefit:** ⚪ (dev-time) — ~500 duplicated lines gone, message drift ended, timing signal
    > gained, and the next suite (there will be one — six exist) starts from a real framework.
> - **Seed/Save:** ✅ / ✅.

---

### VS-2. Suites are human-in-the-loop only — no aggregate run, no CI entry point

**Observed:** Running the full regression surface means manually clicking **14 menu items** (six
suites, three standalone test files, two nightly fuzz deep-runs, three fluid-determinism
variants), reading colorized console output per run. There is no "run everything" aggregate, and no
headless mode: `RunAll` returns `void` with console-only results, so
`-batchmode -executeMethod` has nothing to exit non-zero on. Consequences: a cross-cutting change
(`ChunkData`, pooling, a `Helpers/` refactor) relies on the developer remembering which suites
apply; the 2000-seed nightly fuzzes only run when someone thinks of them.

**Recommendation:** On top of VS-1's shared runner: (a) a `Validate All` menu item running every
registered suite with one combined summary (suites self-register with the runner so new ones are
included automatically); (b) a CI/headless entry point that runs the same set and calls
`EditorApplication.Exit(1)` on any baseline failure — making scheduled runs (including the nightly
fuzz tier) possible without a human; (c) keep the individual menu items for focused iteration.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — registration list + two entry points over the shared runner.
> - **Risk:** 🟢 Low — additive; individual workflows unchanged.
> - **Benefit:** 🟡 Medium — the regression gate becomes one click for cross-cutting changes and
    > automatable for nightly fuzz depth; "which suites did you run?" stops being a review question.
> - **Seed/Save:** ✅ / ✅.

---

### VS-3. No stale-assembly guard — a suite can silently validate stale code

**Observed:** A documented operational foot-gun (workflow memory + the `dotnet build` notes in
CLAUDE.md): after editing code, the menu-item suites can execute against the *previous* compiled
assembly if Unity's script compilation didn't actually run (`dotnet build` alone never recompiles
the editor domain; even `IsCompiling == false` has produced stale runs). A green suite on stale
code is worse than no run — it launders a regression. Today the only defense is tribal knowledge
("confirm with a fresh `Unity_RunCommand` wave").

**Recommendation:** Make the runner self-checking (one place, via VS-1): at `RunAll` start, warn
loudly if `EditorApplication.isCompiling` or if pending script updates exist
(`EditorApplication.isUpdating` / `CompilationPipeline` state), and print the validation assembly's
load timestamp vs its on-disk `Library/ScriptAssemblies` write time — a mismatch means the loaded
code is not the code on disk. Cheap, and it converts the documented gotcha into an automatic,
visible warning on every run.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — a preamble in the shared runner.
> - **Risk:** 🟢 Low — diagnostic only; false-positive warnings are acceptable (they prompt a
    > recompile, which is the safe action anyway).
> - **Benefit:** ⚪ (dev-time) — eliminates the "suite passed on stale code" failure mode that has
    > already cost debugging sessions.
> - **Seed/Save:** ✅ / ✅.

---

## Detailed findings — World Scaling Enablers

### WS-1. Truncating / float-roundtrip chunk coordinate math → `ChunkMath` shift/mask helpers

*(Promoted from `WORLD_SCALING_ANALYSIS.md` §3.2/§6, which analyzed it but never tracked it in this
backlog. It is the only part of the world-scaling work with zero save/seed risk that can ship early
and independently — and it is a micro-optimization win on its own.)*

**Observed:** Chunk/region coordinate math currently mixes three idioms (48 `FloorToInt` sites
across 13 files as of 2026-07-02, plus the truncating `/`/`%` sites): float-roundtrip floors
(`Mathf.FloorToInt((float)x / 16)` — correct today but silently wrong beyond ±2²⁴), truncating
integer division (wrong for negative coordinates — one latent instance is already live in
`RegionAddressCodec.V2Codec` step 1), and ad-hoc correct forms. All-positive coordinates hide the
differences today; Tier B (negative quadrants) turns every wrong site into a silent
world-corruption bug.

**Recommendation:** Centralize into `ChunkMath` shift/mask helpers (`voxel >> 4`, `voxel & 15`,
`chunk >> 5`, `chunk & 31` — simultaneously the fastest and the only always-correct option),
migrate every call site, forbid inline chunk math by convention, and fix the region codec as V3.
Full audit checklist and grep targets: `WORLD_SCALING_ANALYSIS.md` §3.2/§5.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — the audit is the work; each individual fix is mechanical.
> - **Risk:** 🟡 Medium — a single wrong mask silently corrupts chunk/region addressing; guard with
    > an exhaustive old-vs-new equivalence sweep over representative coordinate ranges (trivially
    > scriptable) before swapping call sites.
> - **Benefit:** ⚪ Low today (removes float conversions from every chunk lookup) — but it is the
    > first Tier B prerequisite and the cheapest insurance against the negative-coordinate bug class.
> - **Seed/Save:** ✅ / ✅ — outputs are identical for all-positive coordinates; the defensive
    > region-codec V3 version bump is format-adjacent (see the scaling doc §3.2).

---

## Suggested implementation order

Grouped into waves by value-for-effort; within a wave, order is free. Capture the relevant
benchmark baseline (`Performance/README.md`) before each wave that touches meshing or lighting.

1. **Quick wins, near-zero risk (one sitting each):**
   ~~MR-1 (Euler hoist) ✅ done — marginal~~, ~~MR-5 ✅ done — chain post-process~~, ~~MR-3 + MR-4 ✅ done — SectionRenderer~~, ~~MR-6 ✅ done — pre-size + pool~~, ~~MR-7 ✅ done — −18% fluid~~,
   ~~MR-9 ✅ done — clouds SetVertices/SetTriangles/SetNormals~~, ~~TG-2 ✅ done — jobified emission + bitmask fallback~~, ~~TG-3 ✅ done — seeded Unity.Mathematics.Random (grass + lava)~~, ~~MT-3 ✅ done — zero-alloc DebugScreen refresh~~, ~~MT-5 ✅ done — ToPersistentArray helper, no .ToArray() intermediates~~, ~~MT-4 ✅ done — Dictionary<VoxelMeshData,int> O(1) mesh-index lookup~~, ~~MT-6 ✅ done — enum rename GZip→Deflate, no save breakage~~. All MT-* items complete.
   GPU side: GS-3 (vertex-stage lighting) and GS-4 (pipeline tier audit) belong here too.
2. **Android-survivability wave (prerequisite for shipping on weak hardware):**
   OM-1 (device-tier scaling) → P-4 backpressure (pipeline doc §3 — production side; **SU-2** rides
   along: apply the same in-flight caps to the startup wave) →
   OM-2 (memory budget + `lowMemory` handler) → OM-3 (bounded save queue; **SL-3** rides along:
   snapshot at dequeue inside the bounded writer) → SL-2 (budgeted load-apply pump — the load-side
   twin of the generation pump) → SL-1 (pooled load/save buffers) →
   GS-2 (opaque-texture opt-out — the biggest mobile GPU lever after GS-1).
   SU-1 (loading-mode budget multiplier) slots anywhere after OM-1 supplies the tier ceiling.
3. **Pipeline stabilization (from the pipeline doc, already ordered there):**
   P-5 stable-save bit (⚠️ save migration) → P-3 jobified merge.
4. **Benchmark-gated structural work:**
   ~~MR-2 ✅ done — vertex format (60 B → 32 B/vertex, upload −57%)~~.
   ~~TG-6 ✅ done — pooled the per-chunk `ActiveVoxels` `NativeList` (`ActiveVoxelListPool`); benefit ⚪ (native, off-main-thread, frame-neutral), shipped as no-regression + CLAUDE.md/MR-6 pooling mandate~~ →
   GS-1 (baked-noise liquid shader) →
   LI-2 (section-ranged lighting gather — the next lighting-line item after P-2 Phase 1; hard gate:
   bit-identical light output, C3 darkening baselines B54/B55 stay green) →
   WG-1/WG-2 (generation-path buffer pooling + jobified section occupancy — gate with
   `ChunkGenerationBenchmark` + a TG-2-style differential) →
   WG-3 (structure expansion — profile a tree-dense streaming capture first; byte-identical mod
   stream is the acceptance gate) →
   ~~LI-1 ✅ done — padded lighting volume; layout validated (2.4–3× in-job BFS) but on-demand gather is the cost → NOT shipped standalone, folded into P-2~~ →
   ~~TG-1 (tick path) / TG-4 (full split) — ✅ TG-4 done (Phases 0–1+3+4a+4b+Y-band, all default-on); TG-1 ⏭️ obviated for the fluid hot path (grass residual negligible)~~.
   The GS-5 §7.3 ownership split (`forceRenderingOff` vs `SetActive`) is a small, independently
   harmless PR — now unblocked (MR-3/MR-4 done); do it early so GS-5 stays unblocked. *(Verified
   still open 2026-07-02 — no `forceRenderingOff` exists in the codebase yet.)*
5. **Long-horizon architecture:**
   **P-2 Layer 1 (worker-thread gather) ✅ SHIPPED 2026-06-22 — banks the LI-1 win net-positive ([benchmark](../Performance/LIGHTING_P2_PHASE1_2026_06_22_BENCHMARK.md)); P-2 Layer 2 (persistent zero-copy storage) remains 🔴 profiler-gated, not triggered ([design](PERSISTENT_CHUNK_STORAGE_P2.md))** →
   GS-5 (section occlusion culling — phased plan in `VISIBILITY_CULLING_ARCHITECTURE.md` §5+§7) →
   GS-6 (BatchRendererGroup conversion — own design doc; decide its ordering against GS-5 first,
   see the GS-6 entry) →
   MR-8 (greedy meshing — own design doc first).

WS-1 (chunk-math shift/mask centralization) is wave-independent: zero save/seed risk, ships any
time, and is the first Tier B enabler (`WORLD_SCALING_ANALYSIS.md` §6). **VQ-1** (integer voxel
query fast path) is WS-1's runtime-API half — implement the two together, then PH-1
(gather-once collision sweeps) and VQ-2 (DDA ray march) build on it. SL-4 (region-file read
concurrency, design in `REGION_FILE_CONCURRENCY.md`) is benchmark-gated and corruption-risk 🔴 —
schedule it only with its stress test in place, after SL-1/SL-2 land the cheap wins.

DT-1..4 (debug tooling) are also wave-independent: all 🟢/🟢, batchable into one small PR. Land
DT-1/DT-2 *before* the next debugging session that points the lighting/fluid overlays at a
perf-sensitive investigation (LI-2, GS-5's wireframe overlay) — that is when their ⚪ rating
temporarily stops being ⚪.

ET-1..4 (editor tooling) are wave-independent dev-time items with one internal ordering: ET-4 and
ET-3's items (2)/(3) are cheap standalone wins; ET-2's replacement-rule share (its part b) is
🟢-sized and fixes the preview-vs-game correctness gap — do it early; ET-2's shared column
evaluator (part a, 🔴, seed-gated) should be scheduled like any generator change (fixed-seed
differential mandatory) and ideally alongside the next planned worldgen feature work, with ET-1's
Burst port landing on top of it.

VS-1..3 (validation suites) form one small dependency chain: VS-1's shared runner first (each suite
re-verified against its own pre-refactor verdicts), then VS-2's aggregate + CI entry points and
VS-3's stale-assembly preamble land in that runner in one place. Worth scheduling before the next
multi-suite regression campaign (LI-2 and GS-5 will both lean on several suites at once).

---

## Verification

- **Benchmarks:** `MeshGenerationBenchmark` for MR-*, `LightingJobBenchmark` for LI-1/P-3,
  `ChunkGenerationBenchmark` as a regression canary (no item here should move it).
- **Meshing correctness (regression guard for MR-*):** the **Meshing Validation Suite**
  (`Minecraft Clone/Dev/Validate Meshing`, `Assets/Editor/Validation/Meshing/`) asserts that an
  output-preserving meshing optimization does not change the generated geometry — it runs the real
  `MeshGenerationJob` against a standard-cube geometry oracle plus structural/determinism invariants.
  Capture-free: keep all baselines green through any MR-* change. Built test-first per the
  `validation-driven-bugfix` skill (the lighting suite's sibling). Fluid/custom-mesh/cross-mesh and
  UV/light *values* are not yet oracle-covered — extend the suite before optimizing those paths.
  **Which harness capability each open MR-* item needs first** (and the phased build order) is
  catalogued in
  [`Architecture/Testing Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md`](../Architecture/Testing%20Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md):
  e.g. MR-2 needs light/UV value oracles, MR-5 needs `MeshPostProcessJob` section-space coverage,
  MR-3 needs a `SectionRenderer` apply-path harness, MR-8 needs a merge-invariant oracle.
- **GC:** Profiler GC-allocation capture during sustained streaming (fly in a straight line at max
  speed) before/after waves 1 and 3 — MR-3/MR-9/TG-3/MT-* should drive steady-state allocations to
  ~zero outside debug UI.
- **Determinism:** For LI-1 and P-3: dump light maps for a fixed-seed test world before/after and
  diff — must be byte-identical. For TG-3: confirm worldgen output unchanged (it must be — the
  change is runtime-only); grass-spread pattern differences are expected and acceptable.
- **Visual:** MR-1/MR-2/MR-4 visual checks (rotated blocks, fluid rendering, section-culling
  bounds, smooth-lighting gradients) are **confirmed in-game**. MR-8 still needs eyes-on checks
  when implemented (merged-quad lighting seams, texture tiling). GS-1/GS-3 need side-by-side
  comparisons per quality tier (water/lava character, lighting gradients via `DEBUG_LIGHTDATA`).
- **GPU:** For GS-*: profile with the Frame Debugger + platform GPU profiler (Android GPU
  Inspector / Snapdragon Profiler on device) — record liquid-pass GPU time over a water-heavy view
  and total frame bandwidth before/after GS-1/GS-2. Desktop GPU timings will *understate* the
  opaque-texture and ALU wins; only on-device numbers count for mobile decisions.
- **OOM stress test:** For OM-*: run the benchmark fast-movement scenario on the weakest target
  device (or a memory-capped Android emulator). Pass criteria: resident memory plateaus instead of
  climbing, `GenerationJobs`/dirty-set counts stay bounded, no `lowMemory`-driven crash, and the
  failure mode under sustained overload is reduced view distance — not process death.
