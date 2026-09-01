# Foliage & Flora Liveliness Improvements Report

**Version:** 1.4  
**Date:** 2026-08-27  
**Status:** Open backlog. Items are removed (archived) when implemented and verified.
Shipped and archived so far: **FL-1 wind sway** (v1.1), **FL-2 leaf shimmer + the coherent
traveling-wave sway model** (v1.2), both 2026-07-19, **FL-4 per-voxel cross-mesh variation** and its
**FL-4b** per-block authoring follow-up (v1.3/v1.4, 2026-08-27), all verified in the running editor —
FL-1/FL-2/FL-4 in-game, FL-4b's authoring path end-to-end through the BlockEditor. The "What exists
today" table below is the substrate every remaining flora item (FL-8, FL-3, FL-5) builds on.  
**Target:** Unity 6.6 (Mono for dev; IL2CPP for production)

> The master backlog for making the **grass / foliage layer feel alive** in the VoxelEngine —
> wind sway (vertex animation), per-voxel visual variation, flora variety, ambient and
> interaction particles, and flora gameplay life-cycles. Sibling report to
> [`LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md`](LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md)
> (`RF-*`), [`WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md`](WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md)
> (`TF-*`), and [`CLOUD_RENDERING_IMPROVEMENTS_REPORT.md`](CLOUD_RENDERING_IMPROVEMENTS_REPORT.md)
> (`CL-*`). The single most important design decision: **all sway/animation is shader-side vertex
> displacement driven by per-vertex weights baked at mesh time into the spare `uv.zw` half2
> channels — the mesh is never re-built for animation, and the contested `Color32` tint stream
> (claimed by TF-11 + RF-3) is left untouched.**

**Audited:** 2026-07-19, at commit `3b729a2` (branch `feat/world-scaling`).
Findings are from static review of the meshing path (`MeshGenerationJob.GenerateVoxelMeshData`
cross-mesh arm, `VoxelMeshHelper.GenerateCrossMesh`/`AddTexture`, `SectionRenderer.Layout`), the
shader stack (`VoxelCommon.hlsl`, `StandardBlockShader`, `TransparentBlockShader`), the block
database surface (`BlockIDs.cs`, `RenderShape`, `BlockType`), the worldgen flora pass
(`GenerationFlags.EnableMajorFlora/EnableMinorFlora`, `WorldJobManager.ExpandStructure`), and the
cloud wind driver (`Clouds.cs`). Runtime state was **verified in code, not assumed** — see each
item's "What exists today".

**Relationship to other documents:**

- [`../Architecture/SUB_CHUNK_MESHING_ARCHITECTURE.md`](../Architecture/SUB_CHUNK_MESHING_ARCHITECTURE.md) —
  the section-meshing pipeline every mesh-time item (FL-1/FL-2/FL-4) rides on; changes are guarded
  by the meshing validation suite (MH pattern, B-series baselines).
- [`LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md`](LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md) —
  RF-7 (weather) becomes the owner of the shared wind vector FL-1/FL-2 read; RF-3 claims a
  `Color32` channel for emissive (FL deliberately avoids that stream); RF-1 (day/night) gates the
  firefly variant of FL-6.
- [`WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md`](WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md) — TF-11
  (climate foliage **tint**) is the color half of "alive foliage" and stays owned there; FL-3's
  biome flora palettes get strictly better once TF-3's climate axes exist. The combined ranked
  roadmap lives at the end of that document.
- [`CLOUD_RENDERING_IMPROVEMENTS_REPORT.md`](CLOUD_RENDERING_IMPROVEMENTS_REPORT.md) — the shared
  wind vector was promoted to `World.WindBlocksPerSecond` when FL-1 shipped; cloud drift and
  foliage sway both read it, so grass, leaves, and clouds visibly agree on wind direction.
- [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) — the MR-2 32-byte
  packed vertex layout (`SectionRenderer.Layout` is the single source of truth) constrains every
  per-vertex encoding choice in this report.
- [`OM1_DEVICE_CALIBRATION.md`](OM1_DEVICE_CALIBRATION.md) — particle items (FL-6/FL-7) must be
  budgeted per device tier, like RF-7's precipitation.
- [`SOUND_ENGINE_DESIGN.md`](SOUND_ENGINE_DESIGN.md) — FL-8's rustle audio hook lands there when
  the sound engine ships.

---

## Legend

| Field       | Values                                                                                                                                         |
|-------------|------------------------------------------------------------------------------------------------------------------------------------------------|
| **Effort**  | 🟢 Low (hours, localized) · 🟡 Medium (days, several files) · 🔴 High (architectural, cross-system)                                            |
| **Risk**    | 🟢 Low (isolated, easy to verify) · 🟡 Medium (touches shared state or visual output) · 🔴 High (touches pipeline invariants or semantics)     |
| **Benefit** | 🟢 Core — high value or unlocks other planned work · 🟡 Situational / polish · ⚪ Minor                                                         |
| **Seed**    | ✅ Safe — cannot change generated terrain for a given seed · ⚠️ Terrain-affecting                                                               |
| **Save**    | ✅ Safe — no on-disk format change · ⚠️ Format — requires a save-format version bump + AOT migration step (see `serialization-migration` skill) |

> **Benefit meaning:** player-facing / design value (feature-report convention), not frame time.

---

## Master summary table

| ID   | Finding                                                                                    | Effort | Risk | Benefit | Seed | Save |
|------|--------------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| FL-3 | Flora variety — new CrossMesh block types + per-biome minor-flora palettes                 |   🟡   |  🟢  |   🟢    |  ⚠️  |  ✅   |
| FL-5 | Two-block-tall plants (tall grass, large fern) — paired-half placement/removal semantics   |   🟡   |  🟡  |   🟡    |  ⚠️  |  ✅   |
| FL-6 | Ambient particles — falling leaves, drifting motes/pollen, fireflies at night              |   🟡   |  🟡  |   🟡    |  ✅   |  ✅   |
| FL-7 | Block interaction particles — break/place crumbs sampled from the atlas tile               |   🟡   |  🟢  |   🟡    |  ✅   |  ✅   |
| FL-8 | Player rustle — flora near the player pushes away (shader global), optional audio hook     |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |
| FL-9 | Flora life-cycle behaviors — grass-blades spread/decay, sapling growth (tick system)       |   🔴   |  🟡  |   🟡    |  ✅   |  ✅   |

**Suggested order:** FL-3 (content — every new flora type inherits FL-1/FL-2/FL-4 for free, and
FL-4b's per-block envelope is already there to tune each one) → FL-8 (trivial now that the sway
vertex path exists) → FL-6/FL-7 (particles, one budgeting pass) → FL-5 → FL-9. TF-11 (tint) is the
missing color half of the same goal and ranks alongside these in the combined roadmap.

---

## What exists today (shipped FL-1 + FL-2 + FL-4 substrate)

FL-1 and FL-2 shipped 2026-07-19 and FL-4 on 2026-08-27 (all in-game verified); every remaining
flora item builds on this shape:

| Area            | Shipped state                                                                                                                                                                                                                                            |
|-----------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Channel encoding | Cross-mesh verts carry `uv.z` = sway weight (1 top / 0 bottom — roots planted, FL-1); sway-flagged cubes carry their authored `BlockType.swayStrength` on **every** vert via a per-voxel post-pass in `GenerateVoxelMeshData` (FL-2 — covers all six schema arms in one place; custom meshes excluded). `uv.w` = per-voxel hash. Every other path writes `zw = 0` |
| Phase hash      | `VoxelMeshHelper.VoxelHash01` (lowbias32-style) over the **voxel-space** cell (`ChunkPosition + pos` in the meshing job) — deterministic across re-mesh and floating-origin re-anchors                                                                    |
| Sway model      | **Spatially coherent traveling wave** (`ApplyFoliageSway`, `VoxelCommon.hlsl`): the dominant phase is `distance-along-wind` through voxel-space XZ (re-anchor-safe; **as shipped this uses `Helpers/FoliagePhase`**, which reduces the origin's whole contribution mod 2π on the CPU — the shader no longer reads any origin global), so gusts ripple across canopies/meadows; the baked `uv.w` is a small jitter only. Plus a slower broad gust wave and a `wave²` vertical settle so extremes read as bending. Transparent shader only; `VoxelAppdata.uv` is `float4` |
| Block authoring | `BlockType.swayStrength` (`[Range(0,1)]`, BlockEditor slider; carried into `BlockTypeJobData`); OakLeaves = 0.25. Only transparent-pass blocks visibly sway (opaque shader ignores the channel — documented in the tooltip)                              |
| Wind ownership  | **Promoted `Clouds` → `World`**: `World._windBlocksPerSecond` (+ public `WindBlocksPerSecond`) is the single wind source; `Clouds.LayerWind` and foliage both read it; RF-7 later drives the value                                                        |
| Driver          | `FoliageSway` component on the `World` prefab — amplitude/frequency/gust/reference-speed + wave-coherence knobs (wavelength 14 blocks, phase jitter 0.2, vertical bob 0.3, gust spatial 0.35), pushes `FoliageWindVector`/`FoliageSwayParams`/`FoliageSwayParams2` per frame |
| Setting         | `enableFoliageSway` (Graphics → Effects, default on, `SettingsManager.cs`)                                                                                                                                                                                |
| Per-voxel variation | `CrossMeshVariation` (`Data/JobData.cs`) — hashed XZ offset, base-anchored uniform scale and texture-U mirror, built in the meshing job from the **voxel-space** cell and applied inside `GenerateCrossMesh`. Its salted hash (`VoxelMeshHelper.VoxelHashU32`) is de-correlated from the FL-1 phase; `CrossMeshVariation.Identity` keeps BlockEditor preview icons centred |
| Variation authoring | `BlockType.crossMeshVariation` (`CrossMeshVariationSettings`: offset / scaleMin / scaleMax / allowMirror, FL-4b) — a CrossMesh-only BlockEditor section, mirrored into `BlockTypeJobData`. `CrossMeshVariationSettings.Default` (0.15 / 0.85 / 1.1 / mirror on) reproduces FL-4's engine-wide look and is authored explicitly on Grass Blades. `CrossMeshVariation.SanitizeEnvelope` is the single choke point: it falls back to `Default` for a never-authored (zeroed) struct, orders an inverted range, floors the scale at `MinAuthoredScale`, and clamps against `MaxCellEscape` so no authored value can leave the section's culling volume. **Vertical is the binding direction** — scaling is centred in XZ but anchored at the base in Y, so the ceiling is `MaxSanitizedScale = 1 + MaxCellEscape`, not `1 + 2 × MaxCellEscape` |
| Section bounds  | `SectionRenderer`'s constant MR-4 bounds are padded by `CrossMeshVariation.MaxCellEscape` on every side, the only distance geometry may leave its section (a border tuft offset + upscaled)                                                     |
| Suite guard     | Meshing baselines **B22** (cross-mesh, + `CrossFlora` palette entry), **B23** (cube shimmer, + `SwayingLeafCube` entry), **B62** (FL-4 variation vs. the `FromCell` oracle: base-planted, inside the padded cell, cell-distinct, deterministic) and **B63** (FL-4b: a zero-envelope `RigidFlora` palette entry lands exactly on its unit-cell corners, a default-envelope block in the same chunk does not, and an over-authored `ExtremeFlora` in a section's **top row** stays under the padded section top); **B16** pins the padded bounds. All prove-red witnessed |

---

## Detail sections

### FL-3 — Flora variety: new CrossMesh block types + per-biome palettes

**Classification:** Core content. "More types" is half the user-visible richness.

**What exists today.**

- Exactly **one** minor-flora block exists: `GrassBlades`. No flowers, ferns, dead bushes,
  mushrooms, or saplings. `RenderShape.CrossMesh` and the `PLANT` placement tag
  (`PlacementRules.cs:27`) are generic and ready.
- The worldgen already has a working two-tier flora pass: structure markers gated by
  `GenerationFlags.EnableMajorFlora` / `EnableMinorFlora` (`JobData.cs:618-624`), expanded via
  `WorldJobManager.ExpandStructure` (`WorldJobManager.cs:829`), with per-biome zone/placement
  noise controls on the biome attributes.
- The placement suite already guards flora rules (REQUIRES_SUPPORT gate, canReplaceTags split —
  see the placement validation suite).

**Gap / finding:** the engine's flora *machinery* outstrips its flora *content* by a wide
margin. One tuft type makes every biome read as the same biome.

**Proposal.**

1. **Author blocks via the standard pipeline** (BlockEditor → `BlockDatabase.asset` →
   `Minecraft Clone/Generate Block IDs`): tall-grass variants (2–3 heights of tuft texture),
   flowers (3–5 colors), fern, dead bush (desert), red/brown mushrooms (low-light). All
   CrossMesh, `PLANT`-tagged, REQUIRES_SUPPORT, opacity 0.
2. **Per-biome minor-flora palettes:** extend the biome minor-flora config from "one block" to a
   weighted list (block ID + weight + optional density noise), selected by the existing
   deterministic placement hash. Mushrooms additionally constrain on low sky exposure at
   placement time.
3. Flowers/mushrooms are natural **bonemeal / pick-up item** hooks later — out of scope here
   (no item system yet); FL-9 covers growth.

**Seed note:** ⚠️ new placements change generated decoration for a given seed. Standard is WIP
and seed-breakers land directly on it (per the TF-report convention), so this is acceptable —
but land the palette change in one commit, not dribbled.

**Dependencies / cross-links:** TF-3 (climate axes make palette selection principled — don't
wait for it, but re-key palettes when it ships); TF-11 (tint makes one flower texture serve many
biomes); FL-4/FL-1 apply to all new types automatically.

---

### FL-5 — Two-block-tall plants

**Classification:** Polish / content depth. Gated on FL-3.

**What exists today.** Nothing spans blocks: flora is strictly one cell. The metadata system
(`MetadataSchema`) can encode a top/bottom half bit; the placement pipeline resolves
worldGen-vs-player sources (placement suite).

**Gap / finding:** tall grass, large ferns, and sunflowers are the classic "lush" reads; all
need paired-half semantics.

**Proposal.** A `TallPlant` metadata schema (bit 0 = upper half): placement writes both halves
atomically (player placement validates two cells; worldgen emits two mods), breaking either half
removes both (extend the removal path the same way REQUIRES_SUPPORT already cascades), light/
mesh treat each half as an independent CrossMesh voxel (upper half gets `uv.z = 1` on *all*
verts under FL-1 — the whole top sways, hinged at the plant's midpoint). Placement-suite
baselines for the paired invariants (no orphan halves, support cascade).

**Dependencies / cross-links:** FL-3 (content pipeline), FL-1 (sway weights), placement
validation suite, `PER_BLOCK_METADATA_SCHEMAS.md` for the schema addition.

---

### FL-6 — Ambient particles (falling leaves, motes, fireflies)

**Classification:** Polish. The "air is alive" layer.

**What exists today.** **Zero particle systems exist anywhere in `Assets/Scripts`** (verified by
search). RF-7's precipitation design already specifies the correct pattern: a camera-following
particle volume with voxel-aware culling, budgeted per device tier (OM-1).

**Gap / finding:** even with sway, the air between blocks is sterile.

**Proposal.** One pooled, camera-local ambient-particle service (a single `ParticleSystem` per
effect type, emission points scattered in a radius around the camera — never per-block emitters,
which would violate the no-per-voxel-objects constraint):

1. **Falling leaves:** emit only under/near leaf blocks — sample candidate cells via the VQ-1
   integer fast path (`TryGetVoxel`), spawn at leaf-block undersides, drift with the FL-1 wind
   global. Density scales with nearby leaf count.
2. **Grass motes / pollen:** sparse bright specks over grass-surface cells, daytime only.
3. **Fireflies:** night-time (needs RF-1's time system) wandering point sprites near flora;
   optionally reuse the RF-4 flicker trick for glow pulsing. **No blocklight contribution** —
   purely emissive sprites; re-flooding light for particles is rejected for the same reason
   RF-5 rejects BFS-animated light.

Spawn queries run on a slow tick (a few cells per frame), never per-particle-per-frame voxel
queries (RF-7's stated constraint). Tier-gate counts via OM-1 budgets; zero-alloc pooling.

**Dependencies / cross-links:** RF-7 (shares the volume/culling pattern — build whichever lands
first, reuse for the second); RF-1 (fireflies); VQ-1 (shipped — spawn-validity queries); OM-1.

---

### FL-7 — Block interaction particles (break/place crumbs)

**Classification:** Polish. Engine-wide (all blocks), listed here because foliage interaction
sells the effect most.

**What exists today.** Breaking/placing a block is visually instant — no debris, no feedback
beyond the voxel change. No particle infrastructure (see FL-6).

**Gap / finding:** the classic Minecraft break-crumbs are a large perceived-quality win for a
small system.

**Proposal.** A pooled one-shot burst service (shared infrastructure with FL-6): on
break/place, emit 8–16 crumb quads whose UVs sample a random sub-rect of the broken block's
**atlas tile** (the block's face texture ID is already known at the interaction site), simple
gravity + bounce, lifetime < 1 s. Custom particle shader samples `_MainTex` (the block atlas)
with per-particle UV offset — one material, one draw, zero per-block assets. Hook into the
existing `PlacementController`/`PlayerInteraction` seam (single call site each for break and
place).

**Dependencies / cross-links:** FL-6 (shared pooled-particle service — build the service once);
OM-1 tier budgets.

---

### FL-8 — Player rustle (proximity displacement + audio hook)

**Classification:** Polish. Nearly free once FL-1 ships.

**What exists today.** Nothing reacts to the player moving through flora (cross-mesh blocks are
non-solid, so the player already walks through them silently and rigidly).

**Gap / finding:** walking through tall grass that doesn't move breaks the fiction FL-1
establishes.

**Proposal.** `World` pushes a `FoliagePlayerPos` shader global (Unity/render-space, updated
per frame — re-anchor-safe because both the vertex position and the global live in the same
space and re-anchor together). In `ApplyFoliageSway()`, add a radial push-away term:
`push = normalize(vertexWS.xz - playerPos.xz) * saturate(1 - dist / radius) * uv.z * k`, radius
≈ 1.5 blocks. Verts already carry the sway weight, so roots stay planted. Optional later: a
rustle SFX trigger when the player's cell transitions into a `PLANT`-tagged voxel
(`SOUND_ENGINE_DESIGN.md` owns the audio side).

**Dependencies / cross-links:** FL-1 ✅ shipped (weight channel + `ApplyFoliageSway`);
SOUND_ENGINE_DESIGN (audio half, when that ships).

---

### FL-9 — Flora life-cycle behaviors (spread, decay, growth)

**Classification:** Polish / gameplay depth. The only 🔴-effort item; explicitly v2 material.

**What exists today.** The block-behavior tick system is live (TG-4/TG-5): grass-*block* spread
runs as a managed behavior; fluids tick in Burst; the behavior validation suite guards parity.
No behavior touches minor flora.

**Gap / finding:** the world's plants are static state — they never grow, spread, or die, so
"alive" stops at the visual layer.

**Proposal (sketch — needs its own design pass before implementation, per the CMD-§8
convention for v2 items):**

1. **Grass-blades spread/decay:** a low-rate managed behavior — grass surface cells sprout
   `GrassBlades` neighbors; flora on cells that lose support/light decays. Must respect the
   effective-light query (RF-1 §9) — never raw skylight — for any light-gated rule.
2. **Sapling → tree growth:** sapling block (FL-3) ticks toward expanding the existing
   major-flora tree structure at its cell, reusing `ExpandStructure`'s `VoxelMod` path so grown
   trees match generated ones.
3. All rules deterministic-seeded and rate-limited through the existing behavior scheduler; the
   behavior suite gains differential baselines per rule.

**Dependencies / cross-links:** FL-3 (saplings/flowers exist first); behavior validation suite;
RF-1 effective-light queries; TG-4 cleanup (pending) touches the same scheduler.

---

## Constraint compliance

| Constraint                                 | How this report complies                                                                                                                                            |
|--------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Packed-`uint` voxels, no per-voxel objects | All animation is shader-side; variation/phase is baked per-vertex at mesh time; particles are pooled camera-local services, never per-block emitters or components. |
| Burst rules in `Assets/Scripts/Jobs/`      | FL-1/FL-2/FL-4 mesh-time work uses `Unity.Mathematics` hashes inside the existing Burst meshing job; no managed types.                                              |
| No hot-path GC / pooling                   | Particle services (FL-6/FL-7) are pooled one-shot systems; spawn queries amortized over frames via VQ-1.                                                            |
| MR-2 vertex layout is the contract         | No layout change anywhere: sway data lives in already-allocated spare `uv.zw`; the `Color32` stream stays reserved for TF-11 + RF-3.                                |
| Meshing changes ride the suite             | FL-1/FL-2/FL-4 each name their B-series baseline (channel writes, determinism, bounds) before shipping.                                                             |
| Serialization                              | No on-disk change in any item (FL-5's metadata bit uses the existing per-voxel meta byte — no format bump).                                                         |

---

## Document History

* **v1.4** - **FL-4b SHIPPED & archived** (2026-08-27, Validate All 578/578, BlockEditor authoring
  path confirmed end-to-end): per-block variation moved from engine constants to
  `BlockType.crossMeshVariation`, a `CrossMeshVariationSettings` struct (offset / scaleMin / scaleMax
  / allowMirror) mirrored into `BlockTypeJobData` and read by `CrossMeshVariation.FromCell`. Design
  choices, all user decisions: a **nested settings struct** rather than four flat fields (each of the
  BlockEditor's two hand-maintained `BlockType` copy initializers gains one line, not four — the
  omission FL-2 already had to fix once); the envelope is **clamped in the job-data mirror**
  (`SanitizeEnvelope`) with a BlockEditor `HelpBox` showing the clamped result, so MR-4's padded
  bounds hold by construction whatever is authored, and no `MaxCellEscape` change was needed; and
  Grass Blades was **authored explicitly** in `BlockDatabase.asset` (additive re-serialize, 190
  insertions / 0 deletions) rather than left on initializer defaults. New constants
  `CrossMeshVariation.Default*` / `MaxSanitizedScale` replace the old `MaxOffset`/`MinScale`/`MaxScale`.
  New meshing baseline **B63** with a zero-envelope `RigidFlora` palette entry (prove-red witnessed:
  making `FromCell` ignore its envelope fails B63 alone, B62 stays green). Post-review fixes, all
  found by `review-changes` after the first green run: the scale ceiling was clamping only the
  **XZ** overhang, so an authored `scaleMax` above 1.2 would have pushed a top-row plant past the
  padded section bounds (base-anchored scaling sends the whole of `scale - 1` upward) — corrected to
  `MaxSanitizedScale = 1 + MaxCellEscape` and guarded by a third B63 leg placing an over-authored
  `ExtremeFlora` at a section's top row. That fixture pins min = max at the ceiling deliberately: the
  first attempt left a range, the per-cell hash sampled 1.04, and the prove-red came back **green** —
  the clamp was never exercised. Also: both scale `[Range]` attributes widened to match the
  BlockEditor's slider limits, and a zeroed envelope now falls back to `Default` instead of
  sanitizing to a quarter-size plant. Verified in passing that
  Unity's initializer defaults survive deserialization of asset entries written before the field existed.
* **v1.3** - **FL-4 SHIPPED & archived** (2026-08-27): `CrossMeshVariation` (`Data/JobData.cs`)
  bakes a hashed XZ offset (±0.15), uniform base-anchored scale ([0.85, 1.1]) and texture-U mirror
  into every cross mesh, built in `MeshGenerationJob` from the voxel-space cell and applied by
  `GenerateCrossMesh`; a new salted `VoxelMeshHelper.VoxelHashU32` keeps the variation de-correlated
  from the FL-1 sway phase, and `CrossMeshVariation.Identity` keeps BlockEditor preview icons static.
  Deviations from the sketch: (1) the hash is derived in the **job**, not inside `GenerateCrossMesh`,
  which never sees the voxel-space cell; (2) "mirror = swap the two planes' diagonal" is a geometric
  no-op (the cross already contains both diagonals) and shipped as a texture-U flip instead; (3) the
  sketch's "never escapes the cell" guard is impossible alongside scale 1.1 — instead
  `SectionRenderer`'s constant MR-4 bounds are padded by `CrossMeshVariation.MaxCellEscape` (user
  decision), and baseline **B16** now pins the padded box. New meshing baseline **B62** (oracle,
  base-planted, padded-cell bounds, cell-distinct, deterministic; prove-red witnessed) and B22's
  helper made variation-aware. Per-block ranges deferred to the new **FL-4b**.
* **v1.2** - **FL-2 SHIPPED & archived** (2026-07-19, in-game verified, Validate All 281/281):
  `BlockType.swayStrength` (`[Range(0,1)]`, BlockEditor slider, `BlockTypeJobData` mirror) written
  to `uv.zw` by a per-voxel **post-pass** in `GenerateVoxelMeshData` (deviation from the sketch's
  per-face threading — one site covers all six cube schema arms; custom meshes excluded), OakLeaves
  authored 0.25, meshing baseline B23 + `SwayingLeafCube` palette entry (prove-red witnessed).
  Second deviation, after the first in-game pass read as disjointed per-voxel wobble: the shared
  sway model was **reworked to a spatially coherent traveling wave** — dominant phase =
  distance-along-wind through voxel-space XZ (shipped via `Helpers/FoliagePhase`, not an origin
  global), baked phase demoted to a small jitter, plus a broad gust wave and a `wave²` vertical
  settle; new `FoliageSwayParams2` global + wave-coherence knobs on `FoliageSway` (wavelength /
  jitter / bob / gust-spatial). Drive-by fix: BlockEditor `DuplicateSelectedBlock` no longer drops
  `infiniteSourceRegeneration`/`spreadChance`. Substrate table updated to the combined FL-1+FL-2 shape.
* **v1.1** - **FL-1 SHIPPED & archived** (2026-07-19, in-game verified, Validate All 280/280):
  `uv.zw` sway weight/phase baked in `GenerateCrossMesh`/`AddCrossQuad` (top 1 / bottom 0,
  `VoxelHash01` voxel-space phase), `ApplyFoliageSway` in `VoxelCommon.hlsl` (transparent shader
  only, `VoxelAppdata.uv` → float4), `FoliageSway` component on the World prefab, `enableFoliageSway`
  graphics setting, meshing baseline B22 (prove-red witnessed). Implementation deviation from the
  sketch: the wind vector was **promoted from `Clouds` to `World`** in the same change (user
  decision — RF-7's ownership seam now lives on `World.WindBlocksPerSecond`), and the sway knobs
  live on a dedicated `FoliageSway` component rather than `World` fields. Summary table, order, and
  a "What exists today" substrate table updated.
* **v1.0** - Initial report (FL-1..FL-9, gap sweep of meshing/shader/worldgen/particle surfaces)

---

**Last Updated:** 2026-08-27  
**Next Review:** when FL-3 starts (re-verify the shipped-substrate table against `VoxelMeshHelper`/`VoxelCommon.hlsl`) or on the next gap sweep
