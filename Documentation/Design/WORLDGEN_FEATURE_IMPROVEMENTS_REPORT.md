# World Generation Feature Improvements Report

> The master backlog for **terrain & world-generation features and design changes** in the
> VoxelEngine — the feature-and-design counterpart to
> [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md), which owns worldgen
> *performance* items (`WG-1..3`). Every finding shows, at a glance: implementation effort,
> regression risk, player-facing benefit, and whether it changes world-generation determinism
> (**Seed**) or the on-disk save format (**Save**). Unlike the perf report, several items here are
> *deliberately* seed-breaking — acceptable while the Standard world type is still WIP; see
> §"Seed-stability note".
>
> Status: **Open backlog.** Items are removed (archived) when implemented and verified.

**Audited:** 2026-07-02, at commit `a458173` (branch `main`).
**Amended:** 2026-07-03 — second gap sweep added TF-10..TF-14 (structures, climate surface
effects, per-world generation options, worldgen versioning, world border) plus RF-7 (weather) in
the sibling report, and folded minor notes into TF-3 (sub-biome variants) and TF-9 (bedrock,
pre-generation tool).
**Amended:** 2026-07-13 — TF-14 decision taken (Tier B unbounded XZ is now scheduled as WS-2, see
`WORLD_SCALING_IMPLEMENTATION.md`): skip the interim hard-wall treatment; TF-14 becomes the
**per-world configurable border** (gameplay fence, terrain generates past it). Save flips ✅ → ⚠️
(level.dat field, rides the TF-12/TF-13 v12 wave).
**Amended:** 2026-07-13 (later) — TF-14 **fully shipped** (Phase 1 persistence + player clamp + minimap;
Phase 2 `Minecraft/BorderWall` shader + `BorderWallRenderer`). Landed as a **standalone level.dat
v11 → v12** `borderRadius` (int; 0 = disabled) — *not* the TF-12/13 wave — with existing worlds
upgrading border-disabled. Item is complete and ready to archive from the open backlog.
Findings are from static code review of the Standard generation pipeline
(`StandardChunkGenerator` → `StandardWormCarverJob` → `StandardChunkGenerationJob` →
`CaveIsolationFilterJob`) plus the shipped authoring/editor tooling. The Legacy pipeline
(`Assets/Scripts/Legacy/`) is frozen and explicitly out of scope.

**Relationship to other documents:**

- [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) — worldgen *performance*
  backlog (`WG-1..3`), GPU/shader backlog (`GS-*`), world-scaling enabler (`WS-1`). This report
  cross-links those IDs instead of restating them.
- [`WORLD_SCALING_ANALYSIS.md`](WORLD_SCALING_ANALYSIS.md) — Tier A/B/C world-scaling analysis.
  **TF-5 (Amplified) is gated on its Tier A1**, and TF-6 (Farlands) references its Tier B.
- [`../Architecture/World Generation/PROCEDURAL_TERRAIN_GENERATION.md`](../Architecture/World%20Generation/PROCEDURAL_TERRAIN_GENERATION.md)
  — authoritative doc for the *current* multi-noise/3D-density terrain shape. TF-1/2/3 propose
  changes to systems it describes; it must be updated (or superseded per world type) when they ship.
- [`../Architecture/World Generation/MODULAR_WORLD_GENERATION_&_WORLD_TYPES.md`](../Architecture/World%20Generation/MODULAR_WORLD_GENERATION_&_WORLD_TYPES.md)
  — the world-type/strategy architecture (its §12.4 sketched Amplified/Far Lands; TF-5/TF-6
  supersede those sketches with concrete decisions).
- [`../Architecture/World Generation/CAVE_GENERATION.md`](../Architecture/World%20Generation/CAVE_GENERATION.md)
  — cave carving source of truth. No TF item changes cave *behavior*, but TF-1/TF-3 change the
  biome identity the cave layers key off (see the sampler-consistency risk in TF-1).
- [`../Architecture/INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md`](../Architecture/INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md)
    + [`../Architecture/AOT_WORLD_MIGRATION_SYSTEM.md`](../Architecture/AOT_WORLD_MIGRATION_SYSTEM.md)
      — TF-4 (dimensions) is a save-format change and must go through the `serialization-migration`
      skill / AOT migration protocol.
- [`LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md`](LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md)
  — the `RF-*` sibling report (day/night cycle, sky rendering, lighting effects). The **combined
  ranked roadmap across both reports lives at the end of this document**.
- [`FOLIAGE_LIVELINESS_IMPROVEMENTS_REPORT.md`](FOLIAGE_LIVELINESS_IMPROVEMENTS_REPORT.md) — the
  `FL-*` sibling report (foliage sway, flora variety, particles). **TF-11's climate foliage tint
  is the color half of that report's goal** and stays owned here; FL-3's per-biome flora
  palettes re-key onto TF-3's climate axes when they ship.

---

## Legend

| Field       | Values                                                                                                                                                        |
|-------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Effort**  | 🟢 Low (hours, localized) · 🟡 Medium (days, several files) · 🔴 High (architectural, cross-system)                                                           |
| **Risk**    | 🟢 Low (isolated, easy to verify) · 🟡 Medium (touches shared state or visual output) · 🔴 High (touches pipeline invariants, lighting semantics, or shaders) |
| **Benefit** | 🟢 Core — high player-facing value or unlocks other planned work · 🟡 Situational / polish · ⚪ Minor (cleanliness, enabler-only)                              |
| **Seed**    | ✅ Safe — cannot change generated terrain for a given seed · ⚠️ Terrain-affecting — changes the terrain a given seed produces (see §"Seed-stability note")     |
| **Save**    | ✅ Safe — no on-disk format change · ⚠️ Format — requires a save-format version bump + AOT migration step (see `serialization-migration` skill)                |

> **Benefit redefinition:** this report is a *feature* audit, so Benefit here means player-facing /
> design value — **not** the frame-time/GC meaning used in `PERFORMANCE_IMPROVEMENTS_REPORT.md`.
> Do not compare Benefit ratings across the two reports.

---

## Master summary table

### Terrain & World Generation Features

| ID    | Finding                                                                                                              | Effort | Risk | Benefit | Seed | Save |
|-------|----------------------------------------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| TF-1  | Voronoi biome borders are near-straight lines — add selection-coordinate domain warping                              |   🟡   |  🟡  |   🟢    |  ⚠️  |  ✅   |
| TF-2  | Biome-owned terrain height → hybrid "shared macro field + per-biome residual"                                        |   🔴   |  🔴  |   🟢    |  ⚠️  |  ✅   |
| TF-3  | No climate model — biome placement is a uniform hash; add parameter-space selection                                  |   🔴   |  🟡  |   🟢    |  ⚠️  |  ✅   |
| TF-4  | Multi-dimension support (registry, per-dimension storage, generator + lighting profile)                              |   🔴   |  🔴  |   🟢    |  ✅   |  ⚠️  |
| TF-5  | Amplified world type (world-level height amplification; gated on world-scaling Tier A1)                              |   🟡   |  🟡  |   🟡    |  ✅   |  ✅   |
| TF-6  | Farlands world type (distance-ramped extreme domain warp)                                                            |   🟡   |  🟢  |   🟡    |  ✅   |  ✅   |
| TF-7  | Rivers (world-level channel carving, Stage 1 at sea level)                                                           |   🔴   |  🟡  |   🟢    |  ⚠️  |  ✅   |
| TF-8  | Biome-selection noise config is silently taken from `biomes[0]` — move to world type                                 |   🟢   |  🟢  |    ⚪    |  ✅   |  ✅   |
| TF-9  | No macro world layout — add a world orchestration layer (continents, oceans, coasts)                                 |   🔴   |  🟡  |   🟢    |  ⚠️  |  ✅   |
| TF-10 | Multi-piece / large structures (villages, ruins) — one template per grid cell today                                  |   🔴   |  🟡  |   🟢    |  ⚠️  |  ✅   |
| TF-11 | Climate-driven surface effects: snow line, ice, biome/foliage tint (gated on TF-3)                                   |   🟡   |  🟡  |   🟢    |  ⚠️  |  ✅   |
| TF-12 | Generation feature flags read from global Settings, not the world — persist in level.dat                             |   🟢   |  🟢  |   🟢    |  ✅   |  ⚠️  |
| TF-13 | No worldgen version stamp — post-freeze terrain changes produce silent seams                                         |   🟢   |  🟢  |    ⚪    |  ✅   |  ⚠️  |
| TF-14 | ✅ SHIPPED 2026-07-13 — per-world gameplay fence: persist + clamp + minimap + animated border wall (ready to archive) |   🟡   |  🟢  |   🟡    |  ✅   |  ⚠️  |

---

## Seed-stability note (TF-1/2/3/7/9/10/11)

Seven items are ⚠️ terrain-affecting. **The Standard world type is still WIP and carries no
seed-stability promise, so these may land directly on `Standard`** — no new world type, no asset
duplication, no migration. The ⚠️ markers remain as information: after each landing, existing
(dev) worlds will show generation seams at old/new chunk borders — regenerate test worlds rather
than reading the seams as bugs, and re-baseline any worldgen golden-master snapshots.

Two residual ordering rules still apply:

1. **TF-2 and TF-3 ship together, with TF-9's sampler skeleton landing first in the same wave** —
   TF-2/TF-3 are design-coupled (TF-2 removes biome-owned mountains; TF-3 is what puts mountains
   back via climate assignment), and TF-9 is the structural home for both items' world-level
   fields. Landing TF-2 or TF-3 alone leaves the terrain in a visibly worse intermediate state.
2. **Re-author the six biome assets once, not per item** — sequence the asset-touching work
   (TF-2's spline moves, TF-3's climate envelopes, TF-8's field deprecation) into a single
   authoring pass.

If/when Standard is declared stable (real player worlds exist), any *later* terrain-affecting
change reverts to needing the world-type-freeze pattern (`WorldTypeID` Legacy=0 precedent: frozen
code path + duplicated biome assets under `Assets/Data/WorldGen/Biomes/Legacy/`) — that is what
the strategy architecture is for. **TF-13 (worldgen version stamp) is what makes that freeze
enforceable** — without a stamp there is no way to even detect that a world predates a terrain
change.

---

## Detail sections

### TF-1 — Biome border domain warping

**Classification:** Core.

**What exists today.** Biome identity is a single world-level cellular (Voronoi) noise:

- `StandardChunkGenerator.Initialize()` builds `_biomeSelectionNoise` from
  `_standardBiomes[0].biomeWeightNoiseConfig` with `normalizeToZeroOne = true`
  (`StandardChunkGenerator.cs:332-347`; see TF-8 for the `biomes[0]` problem).
- Height blending resolves the N nearest Voronoi cells via the custom
  `FastNoiseLite.GetCellularEdgeData` extension (`FastNoiseLite.cs:1377`,
  `CellularEdgeData.MaxCells` cells with hashes + distances) and IDW-blends per-biome heights with
  per-biome `blendRadius` / `blendWeight` / `blendCurve` (`BiomeBlender.cs:39-107`;
  authoring fields `StandardBiomeAttributes.cs:26-45`).
- An "organic wiggle" already exists — but it only modulates the **blend radius**, not the border
  position: `noise.snoise(...) * 0.5f * localBlendRadius` (`BiomeBlender.cs:67-70`).
- Surface-block transitions are additionally dithered by jittering the *sample position* of the
  selection noise (`surfaceBlockDitheringWidth`, `StandardChunkGenerationJob.cs:234-253`) —
  note this is exactly a small, local domain warp, so the technique is already proven in-engine.

**What's broken.** Voronoi cell boundaries are, by construction, straight segments of the
perpendicular bisectors between neighboring feature points. `cellularJitter = 1.0` randomizes the
*endpoints* of those segments but every segment remains a straight line; the blend-radius wiggle
widens/narrows the transition band along that line without bending it. The result is long,
ruler-straight biome seams that read as artificial at map scale — inherent to the approach, not a
tuning problem.

**Proposed design.** Warp the *input coordinates* of every biome-identity sample with a shared,
world-level domain warp — bending the boundary lines themselves:

1. **Authoring:** add to `WorldTypeDefinition` (`Assets/Scripts/Data/WorldTypes/WorldTypeDefinition.cs`):
   `bool enableBiomeBorderWarp` + `FastNoiseConfig biomeBorderWarpConfig`. Use FastNoiseLite's
   fractal domain warp (`DomainWarpType.OpenSimplex2`, `FractalType.DomainWarpProgressive`,
   2–3 octaves) so one instance provides both large meanders and small wobble. Starting point:
   frequency ≈ `0.004`, `domainWarpAmp` ≈ 40 (blocks). The infrastructure exists — `FastNoiseConfig`
   already carries `domainWarpType`/`domainWarpAmp` and `FastNoiseFactory` already wires them
   (used today by `densityWarpConfig` and cave `warpConfig`).
2. **One shared sampler helper.** Add a Burst-compatible static helper (suggested:
   `Assets/Scripts/Jobs/Helpers/BiomeCoordinateWarp.cs`) with
   `Warp(ref FastNoiseLite warpNoise, ref float x, ref float z)` (a thin `DomainWarp` wrapper) and
   route **every** biome-identity sample through it. This is the critical part —
   `Documentation/Bugs/WORLD_GENERATION_BUGS.md` ("Noise Evaluation Duplication — Worm Carver Seek
   Is a 4th Unsynchronized Path") already documents that biome identity is sampled on several
   independent paths. The full inventory to convert in **one commit**:
    - `BiomeBlender.CalculateBlendedTerrainHeight` — warp `(globalX, globalZ)` before
      `GetCellularEdgeData` (`BiomeBlender.cs:39`);
    - `StandardChunkGenerationJob.Execute` — column biome pick (`:226`) and the surface-dither pick
      (`:250`; dither jitter applies **after** the warp, on the warped coords);
    - `StandardWormCarverJob.GetBiomeIndex` (`:320-326`, used at `:168`, `:493`, `:578`);
    - Editor parity: `WorldBlendingPreviewJob.cs:164`, `WorldGenPreviewWindow.CrossSection.cs:672`
      (and any `NoisePreviewJob` biome-map channel), `CaveDensityAnalyzer` (runs the production jobs
      — verify it inherits the warp automatically rather than re-implementing).
3. **Job plumbing:** both jobs gain a `FastNoiseLite BiomeWarpNoise` field (72-byte pass-by-value,
   same pattern as every other noise field) + a `[MarshalAs(UnmanagedType.U1)] bool` enable flag,
   populated by `StandardChunkGenerator.ScheduleGeneration()`.
4. **Tuning workflow:** the biome-map view in `WorldGenPreviewWindow` is the iteration loop;
   validate warp amp does not exceed ~½ cell size (cells at freq 0.005 are ~200 blocks apart;
   amp > ~80 starts producing detached biome islands — which may even be desirable, but should be a
   deliberate choice).

**Performance.** Biome identity is sampled ~3×/column in the generation job (blend + column pick +
surface pick) and per worm step at `BIOME_CACHE_INTERVAL`; each warp is roughly one extra noise
evaluation per sample. Expect low-single-digit % on `StandardChunkGenerationJob` — verify with
`ChunkGenerationBenchmark` (same harness the `WG-*` items use).

**Dependencies / ordering.** Requires TF-8 first (selection config must live on the world type
before adding sibling warp fields there). Lands directly on `Standard` — the seed break is
acceptable while Standard is WIP (see §"Seed-stability note"). TF-6 (Farlands) reuses this exact
helper with a distance-ramped amplitude.

**Architecture/serialization risks.**

- *Sampler divergence* (the one real risk): any biome-identity consumer that misses the warp
  produces mismatched biome assignment — e.g. surface blocks from biome A on terrain heights
  blended for biome B, or cave layers keyed to the wrong biome. Mitigation: the single shared
  helper + a worldgen-determinism golden-master check (the `NS-*` worldgen suite proposed in
  [`VALIDATION_SUITE_COVERAGE_ROADMAP.md`](VALIDATION_SUITE_COVERAGE_ROADMAP.md) is the right
  acceptance gate; TF-1 is a strong reason to build `NS`'s worldgen suite first).
- Burst-safety: `DomainWarp` is already used inside Burst jobs today (density warp) — no new class
  of risk.
- Save ✅ — no on-disk change; ⚠️ Seed by design.

---

### TF-2 — Hybrid terrain ownership: shared macro height field + per-biome residual

**Classification:** Core. The single highest-impact terrain-quality change in this report.

**What exists today.** Terrain height is entirely biome-owned ("biome → terrain"):

- Each biome authors its own full multi-noise stack: `baseTerrainHeight`,
  continentalness/erosion/peaks-&-valleys noise configs + `AnimationCurve` splines
  (`StandardBiomeAttributes.cs:47-68`), flattened into per-biome arrays
  (`StandardChunkGenerator.cs:179-186`, `:301-316`) and grouped as `MultiNoiseData`.
- Per-column height = `BaseTerrainHeight + contSpline(cont) + pvSpline(pv) * erosionSpline(erosion)`
  evaluated **per biome**, then IDW-blended across the N nearest Voronoi cells
  (`BiomeBlender.EvaluateMultiNoiseHeight`, `BiomeBlender.cs:125-138`; blend loop `:79-106`).
- Because bordering biomes may disagree about height by *tens of blocks*, two mitigations exist:
  per-biome `blendWeight`/`blendCurve` tuning (e.g. Mountains authored with low blend weight), and
  `borderFade`, which multiplies `DensityAmplitude` to zero at borders so 3D-density cliffs can't
  tear at transitions (`BiomeBlender.cs:72-77`, consumed at `StandardChunkGenerationJob.cs:272-278`).

**What's broken.** The mitigations *are* the problem: any tall biome next to a flat biome resolves
to one long, monotone interpolated slope the width of the blend radius, with 3D overhangs faded out
exactly where dramatic terrain would be most visible. The terrain silhouette *is* the Voronoi
diagram. Mountains are also placed wherever their cell lands (uniform hash — TF-3), unrelated to
any larger landform logic, so ranges never emerge — only isolated round mountain cells with glacis
edges.

**Trade-off analysis.**

| Model                                 | Pros                                                                                                                               | Cons                                                                                                                         |
|---------------------------------------|------------------------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------|
| **Biome → terrain** (current)         | Full per-biome authoring freedom (shape, splines, density, strata all in one asset); simple mental model; per-biome editor preview | Border height disagreement must be blended away → slope monotony + border density fade; terrain shape leaks the cell diagram |
| **Terrain → biome** (Minecraft 1.18+) | One continuous height field — border seams impossible by construction; biomes follow landforms (coasts, peaks); proven at scale    | Biomes lose height ownership; shape authoring becomes one global knob set; per-biome "look" reduced to surface + decoration  |
| **Hybrid** (proposed)                 | Macro landforms continuous & biome-independent; biomes keep bounded local shape, density, strata, caves, flora                     | Two-level authoring (world + biome); one-time re-authoring of all six biome assets; residual clamp is a new invariant        |

**Proposed design.** Split height into a world-owned macro field and a biome-owned bounded residual:

1. **World-level macro channels** (structurally owned by TF-9's `WorldLayoutConfig` — build them
   there, not as loose `WorldTypeDefinition` fields):
   `FastNoiseConfig worldContinentalnessNoiseConfig` + `AnimationCurve worldContinentalnessCurve`,
   and the same pair for erosion. One `FastNoiseLite` + `BurstSpline` each, evaluated **once per
   column, biome-independent**:
   `macroHeight(x,z) = worldBaseHeight + contSpline(cont(x,z)) * erosionSpline(erosion(x,z))`
   (erosion as a multiplier on relief, MC-style: high erosion → flat, low → mountainous).
2. **Per-biome residual**: biomes keep their peaks-&-valleys noise + spline as *local character*,
   but its output is clamped by a new `float residualAmplitude` (suggested max ±12 blocks) —
   `residual_i(x,z) = clamp(pvSpline_i(pv_i(x,z)), -residualAmplitude, +residualAmplitude)`.
   Per-biome `baseTerrainHeight`, `continentalnessNoiseConfig`, `erosionNoiseConfig` and their
   splines are **removed** from `StandardBiomeAttributes` — deprecate first with `[Obsolete]` +
   `[HideInInspector]` (the `subSurfaceBlockID` precedent, `StandardBiomeAttributes.cs:92-96`),
   delete once the re-authoring pass completes.
3. **Blend only the residual**: `finalHeight = macroHeight + Σ w_i * residual_i` with the existing
   `GetCellularEdgeData` weights — `BiomeBlender` keeps its cell resolution and weighting code;
   only `EvaluateMultiNoiseHeight` changes. Because the blended quantity is now bounded (±12
   instead of ±50+), border blending becomes visually invisible and `borderFade` can be relaxed
   (keep it, but it rarely engages — restoring 3D density overhangs near borders).
4. **Mountains come back via TF-3**: the Mountain biome no longer *creates* mountains; it is
   *assigned* where the macro field is mountainous (low erosion + high PV/continentalness bands in
   climate space). This is why TF-2 and TF-3 should ship together — TF-2 without TF-3 gives
   consistent-but-bland placement; TF-3 without TF-2 keeps border slopes.
5. **3D density, strata, lodes, caves, flora: unchanged.** They already key off the final height +
   biome identity and are unaffected by who owns the height math.

**Blast radius** (verify before implementing — from the code graph):
`BiomeBlender.CalculateBlendedTerrainHeight` has 9 callers across
`StandardChunkGenerationJob.cs`, `StandardWormCarverJob.GetTerrainHeight` (`:328-334`),
`WorldBlendingPreviewJob.cs`, `WorldGenPreviewWindow.CrossSection.cs`, and
`EditorChunkPipelineRunner` — the signature keeps working if `MultiNoiseData` is re-shaped in
place, but every caller must supply the new world-level channels. `StandardChunkGenerator.GetVoxel`
(spawn-point fallback) uses the same path and stays correct automatically.

**Dependencies / ordering.** TF-8 → TF-1 → (TF-2 + TF-3 together), directly on `Standard`.
Re-authoring the six biome assets (`Assets/Data/WorldGen/Biomes/*.asset`) is the long pole — the
`WorldGenPreviewWindow` cross-section + biome map are the iteration tools; extend
`BiomeConfigValidator` to flag residuals exceeding `residualAmplitude` and missing world-level
configs.

**Architecture/serialization risks.**

- 🔴 risk is authoring/visual, not structural: the job math change is small, but every biome needs
  retuning and the "does this still look good" loop is long. Mitigation: golden-master heightmap
  snapshots per seed in the worldgen validation suite (`NS-*`) — re-baselined at landing (the
  suite guards *subsequent* refactors, not the deliberate break).
- The `PROCEDURAL_TERRAIN_GENERATION.md` height formula (§3.6/§4) changes —
  update it in the same change (docs-sync).
- Save ✅ (no format change); Seed ⚠️ by design (acceptable while Standard is WIP).

---

### TF-3 — Climate/parameter-space biome selection

**Classification:** Core.

**What exists today.** Biome placement has no climate model at all:

- A Voronoi cell's biome is `hash * (1/2^31) → [0,1] → floor(n * biomeCount)` — a **uniform random
  pick over the biome array** (`BiomeBlender.GetBiomeIndex`, `BiomeBlender.cs:109-119`; same
  mapping in `StandardChunkGenerationJob.cs:226-229` and `StandardWormCarverJob.cs:320-326`).
- Consequences: every biome has equal area share; adjacency is random (Desert next to Snow-capped
  Mountain next to Ocean); the **Ocean biome spawns as isolated random cells** — landlocked
  single-cell "oceans" instead of coherent seas with coastlines; biome region size is exactly one
  cell (no large climate zones).
- The per-biome `biomeWeightNoiseConfig` field (`StandardBiomeAttributes.cs:23-24`) suggests
  per-biome weighting exists — it does not (only `biomes[0]`'s config is read, as the global
  selection noise; see TF-8).

**Proposed design.** Multi-noise climate selection at **Voronoi-cell granularity** (keeps every
piece of the existing blending machinery):

1. **World-level climate noises** (hosted in TF-9's Layer 2 / `WorldLayoutConfig`):
   `temperatureNoiseConfig` and `humidityNoiseConfig` (very low frequency, ≈0.0008–0.002 —
   climate zones should span many cells). With TF-2 shipped, **continentalness and erosion become
   two more climate axes for free** (the same world-level instances double as selection inputs —
   this is exactly MC's multi-noise design). Degrades gracefully to 2 axes (T/H) if TF-3 ships
   before TF-2.
2. **Per-biome climate envelope** on `StandardBiomeAttributes`:
   `[Range(-1,1)] float temperatureMin/Max, humidityMin/Max`, optional
   `continentalnessMin/Max` (Ocean: `continentalness < −0.4`), optional `erosionMin/Max`
   (Mountains: low erosion), plus `float selectionPriority` for overlap tie-breaks.
3. **Cell-anchored evaluation** (the key decision): sample the climate noises at the **cell's
   feature point**, not at the current column. Every column in a cell then agrees on the cell's
   climate → one biome per cell, and the existing `GetCellularEdgeData` N-cell blending works
   unchanged (it just blends biomes that are now spatially *correlated*: neighboring cells in the
   same climate zone usually resolve to the same biome, so most internal cell borders vanish and
   biome regions become multi-cell climate zones with organic sizes).
    - *Required extension:* `FastNoiseLite.CellularEdgeData` currently exposes cell `Hashes` and
      `Distances` (`FastNoiseLite.cs:1377`) but **not feature-point coordinates**. Extend the
      custom `GetCellularEdgeData` to also output the N feature points (they are computed
      internally during the cellular evaluation — this is an additive struct field + assignment,
      same pattern as the existing project extension). Fallback if that's rejected: derive a
      deterministic pseudo-center from the cell hash — worse (approximate), avoid.
4. **Resolution algorithm** (Burst, per cell): evaluate the 2–4 climate axes at the feature point;
   pick the biome whose envelope contains the climate point (highest `selectionPriority` wins
   ties); if none contains it, pick the biome with the smallest normalized distance to its
   envelope (guarantees total coverage without authoring gymnastics). O(biomeCount) per cell,
   ≤ `MaxCells` cells per column — trivially Burst-friendly (flatten envelopes into a
   `NativeArray<ClimateEnvelopeJobData>` alongside the existing flattened biome arrays in
   `StandardChunkGenerator.Initialize()`).
5. **Swap sites:** `BiomeBlender.GetBiomeIndex(hash, length)` → `ResolveBiomeForCell(...)` plus the
   two other samplers (generation job column/surface picks, worm carver) — same inventory and
   same one-commit rule as TF-1. Editor: `WorldGenPreviewWindow` gains temperature/humidity noise
   channels and the resulting biome map; `BiomeConfigValidator` gains a climate-coverage check
   (sample a grid over climate space; report holes — zero-biome points — and heavy overlaps).
6. **Ocean/coastlines:** with the continentalness axis, Ocean claims all `cont < threshold` cells →
   coherent seas and actual coastlines emerge. Pair with `WorldTypeDefinition.seaLevel` (exists,
   `WorldTypeDefinition.cs:38-39`) — with TF-2, the macro curve should put `cont < threshold`
   terrain below sea level so the Ocean *biome* and underwater *terrain* coincide.

**Folded minor note — sub-biome variants.** The flower-forest-in-forest class of variant is a
natural extension of this design, not a separate item: author a variant biome with a *nested,
tighter* climate envelope inside the parent's (e.g. Flower Forest = Forest's envelope shrunk plus
a narrow humidity band) and a higher `selectionPriority` so it wins where both match. Because
resolution is per cell, variants appear as occasional single/few-cell patches inside the parent
region — exactly the desired look. Zero new machinery; defer authoring until the base six-biome
envelope pass is stable.

**Dependencies / ordering.** TF-8 (config ownership) strictly first. Ship together with TF-2 —
design-coupled (see TF-2 §4 and §"Seed-stability note"). TF-1's warp applies to the selection
coordinates *before* cell resolution and composes cleanly.

**Architecture/serialization risks.**

- Same sampler-consistency risk as TF-1 (shared resolver helper + `NS-*` worldgen suite as gate).
- Authoring risk: climate envelopes are harder to reason about than "one noise per biome" —
  the validator coverage check and preview channels are not optional extras; build them in the
  same pass.
- Biome *count* scaling: with 6 biomes, envelope authoring is easy; the design scales to ~20+
  before the per-cell O(B) scan or authoring overlap management needs revisiting.
- Save ✅; Seed ⚠️ by design (acceptable while Standard is WIP).

---

### TF-4 — Multi-dimension support + save-system changes

**Classification:** Core (unlocks Nether/End-class content, superflat/void debug dimensions, and
gives TF-5/TF-6 a home as dimension-flavored variants if ever wanted).

**What exists today** (all single-dimension):

- **Runtime:** one `WorldData.Chunks` dictionary keyed `Vector2Int`; one `ChunkStorageManager`
  constructed per world (`World.cs:522` — `new ChunkStorageManager(worldName, IsVolatileMode,
  SaveSystem.CURRENT_VERSION)`); one `IChunkGenerator` resolved from the single
  `WorldSaveData.worldType` in `StartWorld`; `VoxelData.Seed` is a global static.
- **Disk:** `Saves/{World}/level.dat` + `pending_mods.bin` + `pending_lighting.bin` +
  `pending_blocklight.bin` at the root, `Region/r.{x}.{z}.bin` files (32×32 chunks, Anvil-style
  sectors) — see `INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md` §3/§4 + Appendix B.
  Save version **11** (`SaveSystem.CURRENT_VERSION`, `SaveSystem.cs:39`) with a 10-step AOT
  migration chain (`MigrationManager.cs:25-37`).
- **Lighting precedent:** the lighting-disabled mode (`LIGHTING_SYSTEM_OVERVIEW.md` §6) documents
  every pipeline bypass point — exactly the gate map a "no-skylight dimension" needs.

**Proposed design.**

1. **Registry:** `DimensionDefinition : ScriptableObject` —
   `{ string dimensionId; WorldTypeDefinition worldType; int seedSalt; DimensionLightingProfile
   lighting; }` where `DimensionLightingProfile` = `{ bool hasSkyLight; Gradient skyLightGradient;
   float fixedGlobalLightLevel (used when hasSkyLight = false); }`. Plus a `DimensionRegistry`
   ScriptableObject (same pattern as `WorldTypeRegistry`); index/id `"overworld"` is the default
   and maps to the world's existing `worldType`.
2. **Single-active-dimension runtime** (the load-bearing simplification): exactly one dimension is
   loaded at a time. A dimension switch is a mini world-reload: run the existing unload safety
   protocol (flush pending mods/lighting — `INFINITE_WORLD_STORAGE...` §6.3), `JobManager.Dispose()`
   (completes all in-flight jobs), swap `ChunkStorageManager` to the dimension's folder,
   re-Initialize the generator with the dimension's `WorldTypeDefinition` and
   `seed = math.hash(int2(worldSeed, seedSalt))`, then re-enter the initial-load coroutine path with
   a loading screen. This avoids every hard problem (cross-dimension chunk keys, split pools,
   concurrent generators) and matches how the engine already boots. Portals are then "teleport +
   switch" — no simultaneous simulation.
    - *Rejected alternative:* concurrent dimensions via `(dimensionId, Vector2Int)` chunk keys —
      touches every dictionary, pool invariant, and job-scheduling gate in the chunk-lifecycle
      pipeline (see `.agents/rules/chunk-pipeline.md`); not worth it without a gameplay requirement
      for cross-dimension ticking.
3. **Disk layout:** per-dimension subfolders
   `Saves/{World}/dimensions/{dimensionId}/{Region/, pending_mods.bin, pending_lighting.bin,
   pending_blocklight.bin}`. **Migrate the overworld into `dimensions/overworld/`** (Option B)
   rather than grandfathering root files: one folder move in the migration step buys uniform path
   code forever (no "root = overworld" special case in `ChunkStorageManager`, backup tooling, or
   future dimension iteration). The migration system already takes a full backup before running
   (`MigrationManager` "Creating Backup..." phase), which de-risks the move.
4. **Save format v11 → v12** (`MigrationV11ToV12Dimensions`, via the `serialization-migration`
   skill):
    - `level.dat`: add `player.dimensionId` (string, default `"overworld"`), optional
      `perDimensionSpawn` list; bump `version`.
    - File move per §3. Region file *format* is untouched — the codec, sectors, and chunk blobs
      are dimension-agnostic already.
    - Older builds opening a v12 world are already refused by the existing
      "version too new" guard (`MigrationManager.cs:64-67`).
5. **Per-dimension lighting:** `hasSkyLight = false` skips the sunlight column fill at generation
   and sunlight BFS seeding, using the §6 bypass map; `World.SetGlobalLightValue()` reads the
   active profile (ties into `RF-1`'s time system — a no-sky dimension uses
   `fixedGlobalLightLevel` and ignores time).
6. **Content:** a first non-overworld dimension is then pure data: a new `WorldTypeDefinition`
   with its own biome set (e.g. a cavern dimension: no sky, `seaLevel` as lava via biome surface
   blocks, aggressive cave layers + trunk worms — all existing knobs).

**Dependencies / ordering.** Independent of TF-1/2/3 (works with any world type). Coordinate the
v12 bump with RF-1's `timeOfDay` migration if both land near each other — one migration step, not
two. The chunk-lifecycle skill invariants apply to the switch sequence (flag clearing on pool
recycle across a dimension swap must be verified — `ChunkData.Reset()` is dimension-agnostic
today and must stay that way).

**Architecture/serialization risks.**

- 🔴 effort is breadth, not depth: `World` singleton assumptions (spawn logic, debug tools, UI)
  each need a "which dimension" audit. Mitigation: keep dimension state entirely inside
  `World`/`ChunkStorageManager`; nothing outside them learns about dimensions in v1.
- In-flight job leakage across a switch (a lighting job completing after the storage swap) —
  mitigation: the switch runs the same `Dispose()`/complete-all path as world exit, which is
  already proven.
- Seed ✅ for existing worlds (overworld salt = 0 → identical generation); Save ⚠️ (v12 + folder
  move + level.dat fields).

---

### TF-5 — Amplified world type

**Classification:** Nice-to-have (headline feature value, but gated).

**What exists today.** `WorldTypeID.Amplified = 2` is reserved (`WorldTypeDefinition.cs:19`) and
`World.StartWorld` already warns-and-falls-back if it's selected. The registry + world-select
dropdown auto-populate from registered definitions (`WorldSelectMenu.cs:423-446`), so a new type is
UI-free. `WorldTypeDefinition` is data-only; `StandardChunkGenerator` reads all shape parameters
from biome assets.

**Decision: world type, not a bonus setting, not a biome.** It changes global terrain scale, not
biome identity (biome would be wrong); the enum slot, registry, save field (`worldType` byte in
`level.dat`), and fallback path all exist (a settings toggle would need a new save field and a
second code path for no benefit); and Minecraft precedent matches player expectations.

**Proposed design.**

1. Add `float terrainAmplification = 1.0f` to `WorldTypeDefinition`, applied in
   `BiomeBlender.EvaluateMultiNoiseHeight` to the **offset terms only** (not the base):
   `height = BaseTerrainHeight + amp * (cont + pv * erosion)` — keeps sea level and biome anchors
   stable while scaling relief. Also scale `DensityAmplitude` by `amp` (more dramatic overhangs).
   With TF-2, this becomes even cleaner: amplify `macroHeight` relief + residuals.
2. Register an `Amplified` `WorldTypeDefinition` asset reusing the **same biome assets** as
   Standard with `terrainAmplification ≈ 2.0–2.5` — no asset duplication needed under this
   design (contrast with the older §12.4 sketch in `MODULAR_WORLD_GENERATION_&_WORLD_TYPES.md`,
   which assumed duplicated biome assets).
3. Remove the `StartWorld` fallback warning for Amplified once registered.

**Dependencies / ordering — the gate.** At `ChunkHeight = 128` (`VoxelData.cs:7`), amplified
relief clips against the world ceiling (density band + heightmap clamp) → flat-topped mesa
plateaus everywhere, which defeats the purpose. **Gate on
[`WORLD_SCALING_ANALYSIS.md`](WORLD_SCALING_ANALYSIS.md) Tier A1** (taller bounded world, e.g.
0…512) — and note its §2.2 prerequisites (`LI-2` section-ranged lighting gather, height-parameterized
pools per `WG-1`, `P-3`) plus `WORLD_GENERATION_BUGS.md` #06 (section bitmask ≤ 32 sections — 512
is exactly 32 sections, so A1 at 512 fits; anything taller needs that fix). Sea level, lode/cave Y
ranges, and trunk-worm bounds are authored against `[0,128)` and need the re-anchoring pass Tier A
already plans.

**Risks.** Amplified worlds stress every per-height cost (see the WG-1 footnote in the perf
report); benchmark with the Tier A harness before shipping. Seed ✅ / Save ✅ (new type only; the
`worldType` byte already exists — older builds refuse unknown types via the registry lookup throw,
same failure class as version-too-new, but verify the error surfaces as a friendly message in
`WorldListItem` rather than an exception).

---

### TF-6 — Farlands world type

**Classification:** Nice-to-have (novelty/marketing feature; cheap).

**What exists today.** Nothing Farlands-specific. Relevant constraints: the world is **bounded** at
`WorldSizeInChunks = 100` → 1600×1600 voxels (`VoxelData.cs:8`), so Minecraft's authentic
"coordinate overflow at extreme distance" cannot occur naturally (and per
`WORLD_GENERATION_BUGS.md` #01/#04 + `WORLD_SCALING_ANALYSIS.md` §1.13-14, float-precision noise
degradation is treated as a *bug class here*, not a feature). `MODULAR_WORLD_GENERATION...` §12.4
sketched "Far Lands = extreme domain warp world type".

**Decision: world type with a distance-ramped warp — not a biome, not a bonus setting.**

- *Not a biome:* biomes are Voronoi cells placed by noise (or climate, post-TF-3) — a Farlands
  *biome* would appear as random patches, not a "beyond the edge of the normal world" zone.
  Distance-based placement doesn't fit the (current or proposed) selection model.
- *World type:* it is a different generation contract, exactly what the strategy pattern is for,
  and it costs one registry asset.
- The *distance ramp* delivers the authentic experience within the bounded world: spawn terrain is
  normal, and terrain degrades into vertical warped madness as you travel outward — reachable in
  a ~800-block walk instead of 12.5 million.

**Proposed design.**

1. Reuse the TF-1 `BiomeCoordinateWarp` helper mechanism, but applied to the **terrain height
   sampling coordinates** (and optionally the biome-selection coordinates) with a distance-ramped
   amplitude: `amp(d) = maxAmp * saturate((d − startRadius) / rampWidth)` where
   `d = length(worldXZ − worldCentre)` (`VoxelData.WorldCentre` exists, `VoxelData.cs:35`).
   Suggested defaults: `startRadius = 600`, `rampWidth = 300`, `maxAmp = 200+`, warp frequency
   high enough to fold terrain vertically (the classic "corrugated wall" look emerges from warp
   amplitude ≫ feature wavelength).
2. Authoring on `WorldTypeDefinition`: `bool enableFarlandsRamp` + `FastNoiseConfig
   farlandsWarpConfig` + the three ramp floats. Register a `Farlands` definition reusing the
   Standard biome set.
3. 3D density biomes (`enable3DDensity`) get the effect for free since density sampling shares the
   warped column height; no cave/structure changes needed (they follow the heightmap via
   `surfaceFadeMargin` / placement height bounds).

**Dependencies / ordering.** After TF-1 (shares the warp helper). Independent of everything else.
If Tier B (unbounded XZ) ever ships, the ramp generalizes trivially (`startRadius` grows).

**Risks.** Extreme warp amplitudes sample noise far outside the local cell — worm-carver
cross-chunk discovery and structure grid election still operate on *unwarped* chunk coordinates, so
no search-radius invariants break (carving/placement happens in warped-height terrain but at
normal XZ addressing). Seed ✅ / Save ✅ (new type only).

---

### TF-7 — Rivers (Stage 1: sea-level channels)

**Classification:** Core (largest missing landform; every reference voxel game has them).

**What exists today.** No river system of any kind. Water exists only as: "air below sea level
becomes water" (`density <= 0 && y < SeaLevel → Water`, `StandardChunkGenerationJob` volumetric
pass; sea level from `WorldTypeDefinition.seaLevel`, default 45), the Ocean biome, and the
underwater surface-block swap (`underwaterSurfaceBlockID`, `StandardBiomeAttributes.cs:105-107`).
The fluid *simulation* (TG-4 Burst fluid tick) handles placed water but worldgen never places
flowing water. `MODULAR_WORLD_GENERATION...` §12.1.D sketched cellular-distance river carving.

**Proposed design (staged — ship Stage 1, defer Stage 2).**

*Stage 1 — sea-level rivers:*

1. World-level river channel field (hosted in TF-9's Layer 3 / `WorldLayoutConfig`): either
   `NoiseType.Cellular` + `CellularReturnType.Distance` (rivers along cell edges — good networks,
   pairs naturally with the biome cell structure) or `1 − |snoise|` ridged isoband (meandering
   independent channels). Both are single 2D evaluations per column; prototype both in
   `WorldGenPreviewWindow` and pick visually.
2. In the height pipeline (post-blend, pre-density-band): where
   `riverMask(x,z) > threshold` **and** `finalHeight` is within a band above sea level (e.g.
   `SeaLevel..SeaLevel+12`), depress the height smoothly to `SeaLevel − depth(riverMask)`
   (2–4 blocks deep, smoothstep banks). The existing water-fill rule then floods the channel with
   zero new water logic, and the existing `y < SeaLevel − 1` underwater surface swap produces
   sandy riverbeds for free.
3. The height-band gate is what keeps rivers out of mountains without hydrology: channels only
   exist where terrain is already near sea level. With TF-2's macro field, the band test moves to
   `macroHeight` (biome-independent → rivers cross biome borders cleanly); without TF-2 it works
   on the blended height but channel walls will wobble at biome borders — acceptable for Stage 1.
4. Suppress structure placement on river columns (add a river-mask check next to the existing
   `y >= SeaLevel && voxelValue != Water` structure gate) and suppress cave surface-breakthrough
   into riverbeds via the existing `surfaceFadeMargin` (already terrain-heightmap-relative — no
   change needed, but verify with the cave suite).

*Stage 2 — elevation-following rivers (deferred, honestly hard):* rivers above sea level need a
per-column water-surface level distinct from `SeaLevel`, a new output channel through the job, and
an equilibrium contract with the TG-4 fluid tick (worldgen-placed above-sea water must not
immediately re-simulate and drain — MC sidesteps this with source-block semantics; our
`FluidLevel` metadata model would need "generation-stable" source marking). True source-to-sea
*networks* additionally need non-local flow knowledge — that is TF-9's Stage-2 macro-grid
(graph-traced rivers), not more per-column noise. Do not attempt in Stage 1; document as
follow-up gated on a fluid-team decision.

**Dependencies / ordering.** Soft dependency on TF-2 (clean cross-biome valleys); hard dependency
on nothing. Seed ⚠️ — acceptable while Standard is WIP (see §"Seed-stability note").

**Risks.** Interaction with the fluid tick at world load (a carved channel filled to exactly
`SeaLevel` is stable today because ocean fill is — same rule, same stability); cave carve guard
already refuses to carve fluid blocks (`FluidType` guard in the carving pass), so rivers won't be
punctured from below at generation time. Benchmark: +1 noise eval/column ≈ noise-floor.

---

### TF-8 — Biome-selection noise ownership (quick win / enabler)

**Classification:** Minor (enabler for TF-1/TF-3; authoring-trap removal).

**What exists today.** `StandardChunkGenerator.Initialize()` builds the single world-level
selection noise from **the first biome's** `biomeWeightNoiseConfig`
(`StandardChunkGenerator.cs:332-347`; hardcoded defaults only when the biome list is empty). Every
other biome's `biomeWeightNoiseConfig` (`StandardBiomeAttributes.cs:23-24`) is dead data. This is
an authoring trap: editing Desert's config does nothing; *reordering the biome array* silently
changes the entire world's biome layout (a seed-breaking landmine disguised as a cosmetic change).

**Proposed design.**

1. Add `FastNoiseConfig biomeSelectionNoiseConfig` to `WorldTypeDefinition`; copy the current
   values from the first Standard biome asset **byte-for-byte** into `Standard.asset`
   (`Assets/Data/WorldGen/WorldTypes/Standard.asset`) — this is what keeps the change Seed-✅.
   Add a `BiomeConfigValidator` assertion that the world-type config exists.
2. `Initialize()` reads the world-type field; delete the `biomes[0]` fallback path (keep the
   empty-list hardcoded default for editor/preview robustness).
3. Deprecate `StandardBiomeAttributes.biomeWeightNoiseConfig` with `[Obsolete]` + `[HideInInspector]`
   (exact precedent: `subSurfaceBlockID`, `StandardBiomeAttributes.cs:92-96`). Remove it entirely
   once TF-3 replaces the selection model.

**Risks.** None beyond copy fidelity — add a one-shot editor assertion (or NS-suite check)
comparing the new config against `biomes[0]`'s at first run. If the values are *changed* rather
than copied, the item flips to Seed-⚠️ — harmless while Standard is WIP, but copy verbatim anyway
so this enabler stays decoupled from the deliberate terrain changes.

---

### TF-9 — World orchestration layer ("WorldLayout"): continents, oceans, coastlines

**Classification:** Core. This is the *architectural home* for every world-level field that
TF-2/3/7 introduce, plus the macro features none of them covers on its own (continent shapes,
coherent oceans, coastlines/beaches, ocean depth, spawn placement). Build its skeleton **first in
the TF-2/3 wave** — building those items without it means an immediate refactor.

**What exists today — there is no macro layout of any kind:**

- Biome placement is a uniform random hash per Voronoi cell (TF-3's subject) — nothing above the
  ~200-block cell scale exists. **"Continentalness" exists in name only at the biome level**: each
  biome samples its *own* continentalness instance (`_biomeContinentalnessNoises[i]`,
  `StandardChunkGenerator.cs:179`, `:311`), so it shapes height *inside* a biome but cannot define
  where landmasses are — biome placement precedes it and is random.
- **The Ocean is a regular biome asset placed as random cells** — and not even a reliably
  underwater one: `Ocean.asset` authors `baseTerrainHeight: 50` against the world-type
  `seaLevel = 45` (`WorldTypeDefinition.cs:38-39`), so whether an Ocean cell actually dips below
  sea level depends entirely on its per-biome curves. There are no continents, no coastlines, no
  beach transitions, no shelf/deep-ocean depth structure — only "air below sea level becomes
  water" wherever any biome's terrain happens to fall.
- **Spawn placement is layout-blind:** the spawn XZ is hard-anchored to `VoxelData.WorldCentre`
  and only the *height* is resolved (`World.cs:611`, `ResolveSpawnHeight` →
  `GetHighestVoxel`, `World.cs:3210`). If the center cell rolls Ocean, the player spawns in
  water.
- **One genuine orchestration precedent exists and is the pattern to generalize:** the
  trunk-worm layer. `TrunkWormConfig` lives on `WorldTypeDefinition`, and `StandardWormCarverJob`
  deterministically *re-derives* world-level worm systems per chunk from
  `math.hash(cell, seed)` scatter grids — stateless, Burst-safe, cross-chunk consistent, zero
  serialization. TF-9 applies the same philosophy to world layout.

**Proposed design — a layered, stateless, Burst-compatible macro sampler.**

*Authoring:* a `WorldLayoutConfig` ScriptableObject referenced by `WorldTypeDefinition` (separate
asset so world types can share or override layouts; Amplified reuses Standard's, an "Island" type
swaps only this). It owns all world-level noise configs + curves, replacing the ad-hoc accretion
of world fields that TF-1/2/3/7 would otherwise each add to `WorldTypeDefinition` directly.

*Runtime:* one `WorldLayoutSampler` Burst struct — a bag of `FastNoiseLite` instances +
`BurstSpline`s + thresholds built once in `StandardChunkGenerator.Initialize()` (exactly the
existing pattern for biome noise tables), passed by value into jobs, with a single entry point:

```csharp
public struct WorldLayoutPoint
{
    public float Continentalness;   // [-1,1], warped — Layer 0
    public float CoastSignal;       // signed distance-to-coast proxy — Layer 1
    public byte  CoastBand;         // DeepOcean | Shelf | Beach | Inland (thresholded)
    public float Temperature;       // Layer 2 (TF-3 axes live here)
    public float Humidity;
    public float Erosion;           // shared with TF-2's macro height
    public float RiverField;        // Layer 3 (TF-7's channel field lives here)
    public float MacroHeight;       // TF-2's biome-independent base height
}
public readonly WorldLayoutPoint Sample(float x, float z);
```

*The layers:*

1. **Layer 0 — Continents:** one domain-warped, very-low-frequency continent field (this *is*
   TF-2's world continentalness — single instance, owned here; warped via the TF-1 helper so
   coastlines are ragged, with fjord/island character controlled by warp amplitude). An authored
   land-ratio curve maps field value → land/ocean threshold. Ocean *terrain* comes from the
   macro-height spline mapping low continentalness below `seaLevel` — the Ocean *biome* is then
   assigned by TF-3's continentalness axis instead of random cells, making terrain and biome
   coincide by construction.
2. **Layer 1 — Coast metrics:** derive a coast proximity signal and band classification from the
   continent field. v1: threshold bands on the field value itself (cheap, zero extra samples) —
   `Beach` band just above the land threshold drives sand surface override (reuse the
   `underwaterSurfaceBlockID` swap mechanic plus a new above-water beach band in the surface
   pass); `Shelf`/`DeepOcean` bands drive an ocean depth curve (gentle shelf → abyss) instead of
   raw noise. v2 (optional): a gradient-based signed-distance estimate (2 extra field samples)
   if band edges look too contour-like.
3. **Layer 2 — Climate:** TF-3's temperature/humidity noises live here, so biome selection and
   any climate-aware feature sample the *same* instances (no drift between selection and
   decoration).
4. **Layer 3 — Hydrology:** TF-7's river channel field lives here; Stage-2 river *networks*
   (below) are this layer's upgrade path.

*Consumers (the point of the exercise — one source of truth):* `StandardChunkGenerationJob`
(height, water fill, surface bands, structure gates), `BiomeBlender`/the TF-3 resolver (climate +
continentalness axes), `StandardWormCarverJob` (its `GetTerrainHeight`/`GetBiomeIndex` paths),
spawn resolution, and the editor preview jobs (`WorldGenPreviewWindow` gains
continent/coast/climate map channels — extend the existing NoiseChannels tab; this is the tuning
loop, not optional). This collapses the known "N unsynchronized noise paths" problem
(`WORLD_GENERATION_BUGS.md` TODO) for all *world-level* fields.

*Spawn orchestration (small, high-value):* replace the fixed `WorldCentre` anchor with a
deterministic spiral search over `Sample()` from the centre outward — first point with
`CoastBand == Inland` (or `Beach`), sampled on the main thread before generation starts (the
sampler is a plain struct; no job needed). Fixes ocean spawns and gives "spawn near coast"
authoring for free.

**Explicit v1 non-goal — stateful world maps.** v1 is a pure function of `(x, z, seed)`: no
precomputed layout grid, no serialization, Tier-B/unbounded-XZ-friendly, trivially deterministic
(the worm-carver philosophy). **Stage 2 (optional, separately gated):** a coarse macro-grid cache
(1 cell ≈ 4×4 chunks) computed lazily per macro region in a Burst prepass job and held in a
generator-owned read-only `NativeHashMap`, enabling graph algorithms that pure per-column noise
cannot express: true source-to-sea river networks (flow tracing on the grid), continent labeling,
and region-level structure budgets (villages). Because the cache is deterministic from the seed it
is *recomputed on load, never saved* — Save stays ✅. Its cost is lifecycle complexity: the
prepass must complete before dependent chunk jobs schedule (one more handle in
`ScheduleGeneration`'s chain, like `wormHandle`), and the cache must be disposed/rebuilt on
dimension switch (TF-4).

**Bounded-world sizing note.** At `WorldSizeInChunks = 100` (1600×1600 voxels, `VoxelData.cs:8`)
a single continent barely fits — layout frequencies must be authored against world size, and the
honest v1 target is "one continent with real coasts and offshore islands", not multi-continent
geography. An **Island world type** (radial falloff term added to Layer 0, centred on
`WorldCentre`) is a nearly-free layout variant worth shipping as the showcase. True
multi-continent worlds want Tier B (unbounded XZ) — `WORLD_SCALING_ANALYSIS.md` §3.

**Dependencies / ordering.** Skeleton (Layers 0–1 + the sampler API + preview channels) lands
**first in the TF-2/3 wave**: TF-2's macro height and TF-3's climate axes are defined as living
inside it. TF-1's warp helper feeds Layer 0. TF-7 Stage 1 plugs into Layer 3 afterwards. Stage 2
(macro-grid) is gated on wanting river networks or villages — do not build it speculatively.

**Architecture/serialization risks.**

- *Scope creep is the main risk* — this is an umbrella. Mitigation: v1 is strictly Layers 0–1 +
  the struct API + preview tooling; climate and hydrology arrive with their own items (TF-3,
  TF-7).
- Per-column cost: the full stack adds roughly 4–8 noise evaluations per column (warped continent
  field, 2 climate axes, river field) on top of the existing multi-noise stack — same cost class
  as TF-1/TF-2 changes; verify with `ChunkGenerationBenchmark` and keep the layers individually
  toggleable during tuning.
- Burst-compatibility is by construction (value-type sampler, `FastNoiseLite` by value,
  `BurstSpline`) — no managed state anywhere in the sampling path.
- Save ✅ (v1 stateless; Stage 2 cache deliberately non-serialized); Seed ⚠️ by design
  (acceptable while Standard is WIP — see §"Seed-stability note").

**Folded minor notes (no own IDs — ride adjacent waves):**

- *Jagged bedrock:* bedrock is currently a single flat layer (`y == 0 → Bedrock` in
  `StandardChunkGenerationJob`). A 1–3-layer jagged floor is a ~five-line job tweak
  (`y <= (hash(x,z) % 3)` style threshold on a per-column hash) — fold into whichever
  terrain-affecting wave lands next (TF-2/TF-9); pointless to seed-break for alone.
- *Pre-generation tool:* a "prepare world" spinner / dev pregen-radius command (generate N chunks
  around spawn before entering the world). Pure QoL orchestration on top of the existing pipeline;
  ties into the `SU-*` startup items in `PERFORMANCE_IMPROVEMENTS_REPORT.md` and pairs naturally
  with TF-9's spawn-search (both run before first player control).

---

### TF-10 — Multi-piece / large structures (villages, ruins, dungeons)

**Classification:** Core (the largest missing *content* system; every reference voxel game has
multi-piece structures).

**What exists today.** The structure system is strictly **one template per grid cell**:

- `StructurePoolEntry` grid election runs inside the generation job (deterministic per-cell hash
  pick), emits a `StructureSpawnMarker`, and the main thread stamps a single
  `CompositeStructureTemplate` via `ExpandStructure()` (`StandardChunkGenerator.cs:847`).
- Cross-chunk spill is handled by the pending-mods system — proven infrastructure that a larger
  system can keep using unchanged.
- There is no multi-piece planning (jigsaw-style assembly), no terrain-adaptive layout (roads,
  foundations, ruin scattering), and no "locate nearest structure" query for gameplay/dev use.
- `MODULAR_WORLD_GENERATION_&_WORLD_TYPES.md` §12.3.C sketches only the neighbor-aware decoration
  pass prerequisite; TF-9's Stage-2 macro grid mentions *region-level structure budgets* but
  nothing owns the structure system itself. This item does.

**Proposed design (staged).**

1. **Stage A — structure plan as data:** introduce a `StructurePlan` — a deterministic, seeded
   layout of N template pieces (position + rotation + template ref) produced by a planner from a
   structure *start point*, before any stamping. The existing single-template path becomes the
   trivial one-piece plan; `ExpandStructure()` generalizes to iterate plan pieces (each piece
   stamps exactly like today, pending-mods spill included).
2. **Stage B — jigsaw-style planner:** templates gain authored connection sockets (pool of
   compatible next-pieces per socket, MC jigsaw model); the planner does a bounded random walk
   (max pieces / max radius) with collision rejection. Deterministic from
   `math.hash(startCell, seed)` — same stateless philosophy as the trunk-worm layer.
3. **Terrain adaptation:** per-piece ground resolution via the existing terrain-height path
   (`BiomeBlender.CalculateBlendedTerrainHeight`), plus a foundation fill (pillar down to terrain)
   and a cut-into-slope rule; roads as spline-following surface overrides between piece anchors.
   Villages want *flat-enough* sites — a site-quality check (height variance over the plan
   footprint) that rejects or shrinks plans on steep terrain.
4. **Placement + budgets:** start points come from a coarser election grid than the current
   per-cell structures (villages are rare); **region-level budgets and biome gating live in TF-9's
   Stage-2 macro grid** — this is the concrete consumer that justifies building Stage 2.
5. **Locate query:** a `LocateNearestStructure(worldPos, structureId)` dev/gameplay query — with
   deterministic election grids this is a spiral scan over candidate cells re-running the
   election hash, no storage needed (worm-carver discovery precedent).

**Dependencies / ordering.** Stage A is independent and cheap-ish (refactor + plan abstraction).
Stage B/roads want TF-9 Stage-2 (macro grid) for budgets/placement — build them in the same wave.
Biome gating benefits from TF-3 (climate-sensible village biomes).

**Architecture/serialization risks.**

- Plans must be **recomputed, never saved** (deterministic from seed) to keep Save ✅ — if plan
  determinism is ever compromised (e.g. planner reads loaded-chunk state), half-generated
  structures appear at world edges. The planner must depend only on `(seed, startCell)` + noise.
- Main-thread stamping cost scales with plan size — large villages should stamp incrementally
  (pieces whose chunks are loaded; the pending-mods path already gives this for free).
- Seed ⚠️ (new structures change worlds; acceptable while Standard is WIP); Save ✅.

---

### TF-11 — Climate-driven surface effects: snow line, ice, foliage tint

**Classification:** Core (cheap visible payoff of the TF-3 climate work; without it the climate
model is invisible at the surface).

**What exists today.**

- Surface block choice is uniform per biome: `surfaceBlockID` + the underwater swap
  (`underwaterSurfaceBlockID`) in the `StandardChunkGenerationJob` surface pass — nothing varies
  by altitude or temperature.
- `Snow` and `GrassSnowy` blocks already exist (`BlockIDs.cs:22,24`) but nothing places them;
  there is no snow line on the Mountain biome and no ice on cold water.
- There is **no biome/climate tinting at all** — the vertex `Color32` tint stream is constant 1.0
  (MR-2 layout), so grass/foliage color cannot vary by climate.

**Proposed design.**

1. **Snow line (altitude + temperature):** in the surface pass, override the surface block with
   `GrassSnowy`/`Snow` where `effectiveTemperature(x, z, height) < snowThreshold`, with
   `effectiveTemperature = temperature(x,z) − height * lapseRate` (TF-9 Layer-2 temperature axis +
   a per-world lapse-rate constant). Dither the boundary with the existing surface-dither jitter
   mechanic so the snow line is ragged, not a contour.
2. **Ice:** where the water-fill rule places surface water (`y == SeaLevel − 1` top block) and
   `effectiveTemperature < iceThreshold`, place Ice instead (new block via the `BlockEditor`
   workflow — do not hand-author the ID). Rivers (TF-7) inherit this for free since they flood via
   the same rule.
3. **Climate foliage tint:** map `(temperature, humidity)` → a grass/foliage color (authored 2D
   gradient or small LUT), written into the per-vertex `Color32` tint stream at meshing time for
   tint-flagged blocks (grass top, leaves, tall grass). **Vertex-format constraint:** the MR-2
   packed 32-byte layout is the contract (`SectionRenderer.Layout` is the SoT), and **RF-3 §2
   already plans to claim a spare channel of the same `Color32` stream for emissive strength** —
   coordinate the channel allocation between the two items *before* either ships (tint needs RGB;
   emissive wants one channel; together they exactly fill the stream). The meshing job needs the
   climate sample per column — cheapest is passing the two climate values per column alongside the
   existing per-column data rather than re-sampling noise in the meshing job.
4. **Editor parity:** `WorldGenPreviewWindow` snow-line overlay on the biome map channel;
   `ChunkPreview3DWindow` renders tinted vertices as-is (tint rides the normal vertex path).

**Dependencies / ordering.** Hard-gated on TF-3/TF-9 Layer 2 (the temperature/humidity axes —
building a separate temperature noise just for snow would recreate the unsynchronized-sampler
problem). Land in the wave right after TF-2/TF-3. Coordinate the tint channel with RF-3.

**Architecture/serialization risks.**

- The tint change touches meshing + all three block shaders (read the tint stream where it is
  currently ignored) — guard with the meshing validation suite (MH pattern, new baseline for a
  tinted fixture).
- Surface overrides are generation-time only — no retro-tinting of existing chunks (acceptable:
  same seam class as every other ⚠️ item while Standard is WIP).
- Seed ⚠️ (surface blocks change); Save ✅ (tint is derived at mesh time, never stored).

---

### TF-12 — Per-world generation options vs. user settings (correctness fix)

**Classification:** Core (a determinism bug-class fix disguised as a feature; cheap).

**What exists today (verified 2026-07-03).** `GenerationFeatureFlags` (caves, worm carvers, cave
modes — `JobData.cs:604`) is populated **from the global `Settings` object** at schedule time
(`WorldJobManager.cs:166`: `flags.EnableCaves = settings.enableCaves`), not from `level.dat`.
Consequence: a user flipping a settings toggle changes how *new chunks of an existing world*
generate — e.g. a caveless band appears around previously generated terrain. World generation is
currently a function of `(seed, settings-at-generation-time)` instead of `(seed, world options)`.

**Proposed design.**

1. **Persist generation options per world:** add a `worldGenOptions` block to `level.dat`
   (`enableCaves`, `enableWormCarvers`, cave-mode enum — mirror the `GenerationFeatureFlags`
   fields), captured **once at world creation** from the world-create UI (new toggles there,
   defaulting from current settings for continuity).
2. **Read path:** `WorldJobManager` populates `GenerationFeatureFlags` from the loaded
   `WorldSaveData`, never from `Settings`.
3. **Demote the settings entries to a dev override:** keep the settings toggles but re-label and
   gate them (dev/debug section) as an explicit override that *warns* it breaks world consistency
   — or remove them from the user-facing settings screen entirely (preferred; the world-create UI
   is the right home).
4. **Migration:** old worlds get `worldGenOptions` stamped from the *current* settings values at
   migration time (best available guess of what generated them). Level.dat-only AOT step —
   **coordinate the v12 bump with RF-1's `timeOfDay` and TF-4's dimension fields** (one migration
   step, not three) and with TF-13 (same block, see below).

**Dependencies / ordering.** Independent; do together with TF-13 (same `level.dat` touch, one
migration). High rank: it is 🟢 effort and closes a live correctness hole.

**Risks.** 🟢 — plumbing only; the one subtlety is every read site of the affected settings
(verify via Grep that nothing else reads `settings.enableCaves`-class fields at generation time).
Seed ✅ (it *restores* seed determinism; terrain for a given seed + options is unchanged);
Save ⚠️ (level.dat fields + migration).

---

### TF-13 — Worldgen version stamping / retrogen policy

**Classification:** Minor effort, strategic value (enabler for every post-1.0 terrain change).

**What exists today.** There is **no generator-version stamp** per world or per chunk — only the
save *format* version (`SaveSystem.CURRENT_VERSION`), which tracks serialization layout, not
generator behavior. Any post-freeze terrain change therefore produces silent seams with no way to
detect, blend, or regenerate old chunks. `WORLD_SCALING_ANALYSIS.md` touches retrogen only for its
Tier-A2 depth case; the §"Seed-stability note" freeze policy is currently unenforceable.

**Proposed design.**

1. **World-level stamp:** a `worldgenVersion` (byte) in `level.dat`, written at world creation,
   bumped manually whenever a deliberate terrain-affecting change ships *after* Standard is
   frozen. While Standard is WIP it simply stays at 0 — the point is that the field exists in the
   format *before* any world players keep.
2. **Optional per-chunk stamp:** one byte in the chunk blob header recording the
   `worldgenVersion` that generated it. This is the prerequisite for MC-1.18-style **terrain
   blending** (detect old/new chunk borders and blend heights across them) and for targeted
   retrogen ("regenerate ungenerated-feature X in old chunks"). Defer the blending itself — the
   stamp is what must exist early because it cannot be reconstructed later.
3. **Policy hook:** on world load, `worldgenVersion` mismatch between world and current generator
   → log + (post-freeze) route to a declared policy per bump: `accept-seams` (default) /
   `blend` (future) / `refuse`. The stamp makes the choice possible; no behavior ships now.

**Dependencies / ordering.** Do together with TF-12 (same `level.dat` migration wave / v12
coordination). The per-chunk byte is a chunk-blob format touch — run through the
`serialization-migration` skill; cheapest if it rides an already-planned chunk-format bump.

**Risks.** 🟢 — write-only metadata until a policy consumes it. Seed ✅; Save ⚠️ (level.dat field;
per-chunk byte if taken now).

---

### TF-14 — World border experience

**Classification:** Nice-to-have (polish, but the current edge actively reads as broken).

**✅ FULLY IMPLEMENTED (2026-07-13) — Phases 1 & 2 shipped, in-game confirmed.** Ready to archive from
the open backlog. Both halves landed:

- **Phase 1 — functional fence.** A per-world `borderRadius` (int voxels, `0` = disabled) persisted in
  level.dat via a standalone **v11 → v12** migration; a player-position clamp in `VoxelRigidbody` (the
  pipeline stays border-blind); a create-menu input; and an origin-centered square on the
  `WorldInfoUtility` minimap.
  **Amended 2026-07-17 — edit gate.** The fence now also gates player **edits**: place/break outside the
  border is refused via `World.IsVoxelInsideBorder` ([-radius, radius) cell semantics, matching the wall)
  applied at the interaction boundary — `PlacementController.CanPlaceAt` (place preview + gate) and the
  destroy click in `PlayerInteraction` — guarded by two TF-14 baselines in the Placement suite. The
  pipeline (and all reads) stays border-blind; `IsVoxelInWorld`/`IsChunkInWorld` remain untouched.
- **Phase 2 — visual wall.** `Minecraft/BorderWall` URP transparent shader (world-anchored scrolling
  bands + camera-distance fade) driven by `BorderWallRenderer` — four camera-following quads that slide
  along the border edges, clamp to the extent so corners meet, cull beyond the terrain draw distance,
  and carry a small `_depthOffset` off the voxel boundary to avoid Z-fighting. Wired as `World.borderWall`,
  initialized beside the clouds; no voxel-pipeline contact.

**Decisions taken at build time:** standalone v12 bump (NOT the TF-12/13 wave — migrations chain cleanly
and the field needs no TF-12 plumbing); existing worlds upgrade with the border **disabled**;
**radius-only, origin-centered square** (per-world center reserved, not persisted); **camera-following
sliding quads** with procedural bands (over a static box / textured wall). The optional RF-2 fog pairing
remains a separate item.

**What exists today.** The bounded 100-chunk world (`WorldSizeInChunks = 100`, `VoxelData.cs:8`)
just *stops*: `IsVoxelInWorld` fails, chunks end, and the player walks to a bare void edge — with
a known edge-lighting caveat at the boundary
(`INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md` §10.3). There is no visual or gameplay
border treatment (wall, fog, pushback) and no document deciding whether there should be.

**The decision this item exists to force:** either build a small border system now, or explicitly
declare the problem "resolved by Tier B unbounded XZ" (`WORLD_SCALING_ANALYSIS.md` §3) and accept
the raw edge until then. Recommendation: the cheap treatment below — Tier B is not scheduled, and
the raw edge is every player's first impression of the world limit.

**Proposed design (cheap treatment).**

1. **Visual:** an MC-style translucent animated border wall — a camera-following quad set rendered
   at the world AABB faces within some draw distance (a single unlit scrolling shader; no voxel
   pipeline contact). Alternatively/additionally: distance fog (RF-2 §4) tuned so the edge is
   fog-obscured at default view distance.
2. **Gameplay:** soft pushback — clamp the player controller's position to
   `[margin, worldSize − margin]` with a small inward force near the boundary, so the void and the
   §10.3 edge-lighting artifacts are simply unreachable. The clamp lives in the player controller,
   not the voxel systems.
3. If Tier B ships later, delete the wall, keep the shader as a Tier-B "world border" gameplay
   feature seed (configurable-radius border is a standard survival-world feature anyway).

**Dependencies / ordering.** Independent of everything; pairs visually with RF-2's fog. 🟡 effort
only because of the border shader; the clamp is an hour.

**Risks.** 🟢 — additive rendering + a controller clamp; nothing touches generation, lighting, or
storage. Seed ✅ / Save ✅ *(original bounded-world treatment; superseded below)*.

**DECIDED 2026-07-13 — per-world configurable border (post-WS-2).** With Tier B scheduled
(`WORLD_SCALING_IMPLEMENTATION.md`, WS-2 plan approved), the interim hard-wall treatment is
skipped and this item resolves to its own item-3 variant: a **per-world border setting persisted
in level.dat** (radius/center, off by default is one option — see open question below). Semantics:

- **Gameplay fence only.** Terrain still generates past the border (generation, lighting, meshing,
  and storage are border-blind); the *player* is clamped (soft pushback in the player controller,
  per the original design) and the translucent wall shader renders at the configured radius. Must
  **not** be implemented in `IsVoxelInWorld`/`IsChunkInWorld` — that would re-block the pipeline.
- **Save:** ⚠️ level.dat field + migration. **Shipped as a standalone v11 → v12 bump** (the earlier
  "ride the TF-12/13 wave" framing was a bundling preference, not a dependency — the `spawnPosition`
  field already proved per-world level.dat fields need no TF-12 plumbing). TF-12/13 can later bump
  v12 → v13 independently.
- **Minimap:** `WorldInfoUtility` draws an origin-centered border square from the per-world setting
  when present. *(WS-3 removed the old west/south floor walls entirely, so this is a fresh draw, not
  a redraw of surviving walls; no schema coupling.)*
- **Resolved (build time):** existing worlds upgrade with **no border** (disabled) — consistent with
  WS-2/WS-3, which already dropped their walls, so the border is purely opt-in. The "pre-set at the
  legacy 100-chunk extent" alternative was rejected: WS-2/WS-3 already changed that experience, so
  re-introducing a fence would surprise more than it preserves.

---

## Ranked roadmap (combined across TF-* and RF-*)

Ordering optimizes for: player-visible value early, design-coupled items landed together, gated
items last. RF items are detailed in
[`LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md`](LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md).

| Rank | Item                                        | Why here                                                                                              |
|------|---------------------------------------------|-------------------------------------------------------------------------------------------------------|
| 1    | **RF-1** Day/night cycle                    | Highest value-per-effort in either report; independent; unlocks RF-2 and blood-moon events            |
| 2    | **TF-8** Selection-noise ownership          | 🟢 hours; removes an authoring landmine; prerequisite plumbing for TF-1/TF-3                          |
| 3    | **TF-12** Per-world generation options      | 🟢 correctness fix (live determinism hole); pair with TF-13; coordinate v12 with RF-1/TF-4            |
| 4    | **TF-13** Worldgen version stamp            | 🟢 write-only metadata that cannot be reconstructed later; same level.dat wave as TF-12               |
| 5    | **RF-2** Sky rendering (sun/moon/stars/fog) | Completes RF-1 into a shippable player feature; no worldgen coupling                                  |
| 6    | **TF-1** Biome border domain warp           | Biggest visible terrain win per effort; seed break acceptable while Standard is WIP                   |
| 7    | **TF-9** World orchestration layer (v1)     | Layers 0–1 + sampler API + preview channels — opens the TF-2/3 wave and owns their world-level fields |
| 8    | **TF-3** Climate biome selection            | Core identity feature; ship together with TF-2 (design-coupled); climate axes live in TF-9            |
| 9    | **TF-2** Hybrid terrain ownership           | Largest terrain-quality change; ship together with TF-3; macro channels live in TF-9                  |
| 10   | **TF-11** Climate surface effects           | The visible payoff of TF-3's climate axes; coordinate the tint channel with RF-3                      |
| 11   | **TF-7** Rivers (Stage 1)                   | Plugs into TF-9 Layer 3; benefits from TF-2's macro field (soft dependency)                           |
| 12   | **TF-10** Multi-piece structures            | Stage A anytime; Stage B (jigsaw + budgets) wants TF-9 Stage-2 macro grid — build together            |
| 13   | **TF-4** Dimensions + save changes          | Parallel serialization track (no seed risk); coordinate v12 bump with RF-1's migration                |
| 14   | **TF-6** Farlands world type                | Cheap novelty once TF-1's warp helper exists                                                          |
| 15   | **TF-5** Amplified world type               | Gated on `WORLD_SCALING_ANALYSIS.md` Tier A1 — do not start before the height work                    |
| 16   | ~~**TF-14** World border~~ ✅ SHIPPED        | Done 2026-07-13 (per-world fence + animated wall); pairs optionally with RF-2's fog                   |
| 17   | **RF-7** Weather                            | Needs TF-3/TF-11's temperature axis for precipitation type; rendering rides RF-1/RF-2 machinery       |
| 18   | **RF-4** Torch flicker                      | Polish; 🟢 shader-side, needs a Torch block authored first                                            |
| 19   | **RF-3** Bloom / post-processing            | Polish; pair with the GS-4 render-tier audit; tint-channel coordination with TF-11                    |
| 20   | **RF-6** SSAO ("GI")                        | Polish; drop-in URP feature                                                                           |
| 21   | **RF-5** Animated light sources             | Polish with an architectural ceiling — budgeted block-swap animation only                             |
