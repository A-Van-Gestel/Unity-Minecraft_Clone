# Foliage & Flora Liveliness Improvements Report

**Version:** 1.1
**Date:** 2026-07-19
**Status:** Open backlog. Items are removed (archived) when implemented and verified.
Shipped and archived so far: **FL-1 wind sway** (v1.1, 2026-07-19, in-game verified) — see Document
History for the shipped shape every remaining sway item (FL-2/FL-8) builds on.

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
| FL-2 | Leaf-block sway/shimmer — per-BlockType "sways" flag reusing the FL-1 weight channel       |   🟢   |  🟡  |   🟢    |  ✅   |  ✅   |
| FL-3 | Flora variety — new CrossMesh block types + per-biome minor-flora palettes                 |   🟡   |  🟢  |   🟢    |  ⚠️  |  ✅   |
| FL-4 | Per-voxel cross-mesh variation — deterministic hash offset / mirror / scale at mesh time   |   🟢   |  🟢  |   🟢    |  ✅   |  ✅   |
| FL-5 | Two-block-tall plants (tall grass, large fern) — paired-half placement/removal semantics   |   🟡   |  🟡  |   🟡    |  ⚠️  |  ✅   |
| FL-6 | Ambient particles — falling leaves, drifting motes/pollen, fireflies at night              |   🟡   |  🟡  |   🟡    |  ✅   |  ✅   |
| FL-7 | Block interaction particles — break/place crumbs sampled from the atlas tile               |   🟡   |  🟢  |   🟡    |  ✅   |  ✅   |
| FL-8 | Player rustle — flora near the player pushes away (shader global), optional audio hook     |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |
| FL-9 | Flora life-cycle behaviors — grass-blades spread/decay, sapling growth (tick system)       |   🔴   |  🟡  |   🟡    |  ✅   |  ✅   |

**Suggested order:** FL-4 → FL-2 (both extend the shipped FL-1 substrate; same meshing-suite arc)
→ FL-3 (content) → FL-8 (trivial now that FL-1's vertex path exists) → FL-6/FL-7 (particles, one
budgeting pass) → FL-5 → FL-9. TF-11 (tint) is the missing color half of the same goal and ranks
alongside these in the combined roadmap.

---

## What exists today (shipped FL-1 substrate)

FL-1 shipped 2026-07-19 (in-game verified); every remaining sway item builds on this shape:

| Area            | Shipped state                                                                                                                                                                                          |
|-----------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Channel encoding | Cross-mesh verts carry `uv.z` = sway weight (1 top / 0 bottom — roots planted) and `uv.w` = per-voxel phase; every other emission path writes `zw = 0` (fluid shore push has its own submesh meaning) |
| Phase hash      | `VoxelMeshHelper.VoxelHash01` (lowbias32-style) over the **voxel-space** cell (`ChunkPosition + pos` in the meshing job) — deterministic across re-mesh and floating-origin re-anchors                  |
| Shader          | `ApplyFoliageSway` in `VoxelCommon.hlsl` (primary sine + slower gust, phase-de-synced), called only by `TransparentBlockShader`'s vertex stage; `VoxelAppdata.uv` widened `float2 → float4`             |
| Wind ownership  | **Promoted `Clouds` → `World`**: `World._windBlocksPerSecond` (+ public `WindBlocksPerSecond`) is the single wind source; `Clouds.LayerWind` and foliage both read it; RF-7 later drives the value      |
| Driver          | `FoliageSway` component on the `World` prefab — amplitude/frequency/gust/reference-speed knobs, pushes `FoliageWindVector`/`FoliageSwayParams` globals per frame, zeroes wind when disabled              |
| Setting         | `enableFoliageSway` (Graphics → Effects, default on, `SettingsManager.cs`)                                                                                                                              |
| Suite guard     | Meshing baseline **B22** (+ `CrossFlora` palette entry): weight split, phase uniform/deterministic/cell-distinct, standard cubes keep `zw = 0`; prove-red witnessed                                     |

---

## Detail sections

### FL-2 — Leaf-block sway / canopy shimmer

**Classification:** Core (paired with FL-1 — trees are the biggest on-screen foliage mass).

**What exists today.** `OakLeaves` (`BlockIDs.cs:38`) is a standard **cube** with
`renderNeighborFaces = true`, rendered through the same `TransparentBlockShader` cutout pass as
every other transparent block (glass-like blocks, cactus). There is no per-block way to say
"this block's vertices may move" — the shader cannot distinguish a leaf vert from a glass vert.

**Gap / finding:** even with FL-1, tree canopies stay rigid — and canopies dominate the visual
field far more than ground tufts.

**Proposal.**

1. Add a **`swayStrength` (byte or 0–1 float) field to `BlockType`** (authored in the
   BlockEditor, carried into `BlockTypeJobData`), defaulting to 0. `OakLeaves` gets a small
   value; future flora/leaf types opt in per block. No raw-ID special-casing in the mesher.
2. The cube-face emission path writes `uv.z = swayStrength` for **all 4 verts of a face** (cubes
   are not rooted — the whole block shimmers with a much smaller amplitude than grass bend) and
   the same voxel-hash phase in `uv.w`. Same `ApplyFoliageSway()` as FL-1; amplitude scales with
   `uv.z` so one shader path serves bend (grass, weight 1 top / 0 bottom) and shimmer (leaves,
   uniform small weight).
3. **Clipping trade-off (explicit):** leaf verts displacing means faces adjacent to solid blocks
   can micro-gap or interpenetrate. Keep amplitude ≤ ~0.03 blocks; verdict ✅ acceptable — the
   cutout texture hides sub-pixel seams. ❌ *Rejected:* culling-aware sway masks per face
   (neighbor-dependent weights would re-couple meshing to neighbor state for a cosmetic).
4. Suite guard: baseline asserting `uv.z` equals the palette block's authored `swayStrength` on
   cube faces and `0` for non-sway blocks.

**Dependencies / cross-links:** FL-1 ✅ shipped (weight channel + `ApplyFoliageSway` available); BlockEditor +
`Generate Block IDs` workflow for the new BlockType field (no ID changes, so no regen needed —
field-only change to `BlockDatabase.asset`).

---

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

### FL-4 — Per-voxel cross-mesh variation (offset / mirror / scale)

**Classification:** Polish with outsized payoff (cheapest item in the report).

**What exists today.** Every cross mesh is emitted at the exact voxel corner with identical
1×1×1 geometry (`GenerateCrossMesh` hardcodes the eight plane corners,
`VoxelMeshHelper.cs:638-652`) — grids of perfectly aligned, identical X-shapes read as
artificial at a glance.

**Gap / finding:** Minecraft-style flora gets most of its organic feel from deterministic
per-position jitter, not from animation.

**Proposal.** Inside `GenerateCrossMesh`, derive a per-voxel hash (Burst,
`Unity.Mathematics` — reuse the lowbias32-style hash pattern from `CloudPatternJob`; hash the
**voxel-space cell**, never Unity-space, for re-anchor determinism) and apply:

- XZ offset ∈ ±0.15 blocks (keeps the cross inside the cell with margin at max scale),
- uniform scale ∈ [0.85, 1.1] (anchored at the base — `y=0` stays on the ground),
- mirror flip (swap the two planes' diagonal) for a free 2× visual variant.

Same hash family as FL-1's `uv.w` phase (`VoxelMeshHelper.VoxelHash01`, already shipped) — one
hash call per flora voxel. Deterministic across re-mesh, so no popping when a chunk rebuilds.
Suite guard: fixture asserting exact vertices for a fixed cell (determinism) + bounds assertion
(never escapes the cell).

**Dependencies / cross-links:** none hard; extends the meshing-suite arc FL-1 established (B22).

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

**Last Updated:** 2026-07-19
**Next Review:** when FL-2 or FL-4 starts (re-verify the shipped-substrate table against `VoxelMeshHelper`/`VoxelCommon.hlsl`) or on the next gap sweep
