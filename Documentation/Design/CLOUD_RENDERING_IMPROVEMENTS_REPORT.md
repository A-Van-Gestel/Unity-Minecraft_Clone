# Cloud Rendering Improvements Report

**Version:** 1.2
**Date:** 2026-07-19
**Status:** Open backlog. Items are removed (archived) when implemented and verified.
**Target:** Unity 6.5 (Mono for dev; IL2CPP for production)

> The backlog for making the **cloud layer feel alive** — a procedural (optionally infinite)
> pattern, slow shape evolution, extra layers, a volumetric quality tier, and cloud shadows.
> Ranked internally (§Recommended order); deliberately **not** folded into the combined TF/RF
> roadmap — clouds are a self-contained cosmetic system. The original v1 pair — **CL-1 wind
> drift + CL-2 shading/tint/fade — SHIPPED 2026-07-19** (archived from this backlog, v1.1);
> every remaining item builds on the drift-space plumbing that work introduced.

**Audited:** 2026-07-19, at commit `c7eabd6` (branch `feat/world-scaling`).
Findings are from a full read of `Assets/Scripts/Clouds.cs` (same-session rework, so current by
construction), `Assets/Shaders/CloudShader.shader`, the `CloudStyle` setting in
`SettingsManager.cs`, and the cloud call sites in `World.cs` (`Initialize`/`Reanchor`/
`OnSettingsChanged`/`CheckViewDistance`). Runtime behavior was **verified in play mode** during
the render-distance-scaling session that produced `c7eabd6`, not assumed.

**Relationship to other documents:**

- [`LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md`](LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md)
  — RF-2 §5 (clouds tint by `SkyLightColor`) is **absorbed by CL-2** here; RF-7 §4 names cloud
  color/density as storm knobs — CL-4's density parameter is the receiving end.
- [`WORLD_SCALING_FLOATING_ORIGIN.md`](WORLD_SCALING_FLOATING_ORIGIN.md) — §5.1 owns the cloud
  coordinate rules every item must respect: tiles re-derive through `VoxelToUnity`, pattern
  wrap stays **integer** (v1.11 records the render-distance-scaled tile system CL-* builds on).
- [`../Architecture/DATA_DRIVEN_SETTINGS_UI.md`](../Architecture/DATA_DRIVEN_SETTINGS_UI.md) —
  every new user-facing knob (drift speed, layer count, volumetric tier) ships as a
  `SettingFieldAttribute` field, not bespoke UI.

---

## Legend

| Field       | Values                                                                                                                                         |
|-------------|------------------------------------------------------------------------------------------------------------------------------------------------|
| **Effort**  | 🟢 Low (hours, localized) · 🟡 Medium (days, several files) · 🔴 High (architectural, cross-system)                                            |
| **Risk**    | 🟢 Low (isolated, easy to verify) · 🟡 Medium (touches shared state or visual output) · 🔴 High (touches pipeline invariants or semantics)     |
| **Benefit** | 🟢 Core — high value or unlocks other planned work · 🟡 Situational / polish · ⚪ Minor                                                         |
| **Seed**    | ✅ Safe — cannot change generated terrain for a given seed · ⚠️ Terrain-affecting                                                               |
| **Save**    | ✅ Safe — no on-disk format change · ⚠️ Format — requires a save-format version bump + AOT migration step (see `serialization-migration` skill) |

---

## What exists today (shared baseline)

All remaining CL-* items build on the render-distance-scaled tile system (`c7eabd6`) plus the
CL-1/CL-2 drift + shader work (`d52b089`, `12e6cf6`, both in-game verified 2026-07-19):

| Area          | Current state (verified)                                                                                                                                                                                                                                                             |
|---------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Coverage      | `max(viewDistance × 2, 8)` chunks radius; 64-block tiles keyed by **cloud-space tile index** (drift-corrected), pooled GameObjects, one shared `Mesh` per unique pattern tile (`Clouds.GetTileMesh`)                                                                                  |
| Pattern       | Static 512×512 `clouds.png` (the Minecraft pattern), alpha-thresholded into `bool[,] _cloudData` once at init (`LoadCloudData`); repeats every 512 blocks via integer `WrapToPattern`                                                                                                |
| Styles        | `CloudStyle` enum: `Off` / `Fast` (down-facing quads only) / `Fancy` (1-block-tall extruded hull, corners inflated by `_depthOffset` against Z-fighting)                                                                                                                             |
| Shader        | `Minecraft/CloudShader` — unlit; MC-style per-face shading (Fancy-only via `_CloudFaceShading` global), `SkyLightColor` day/night tint, coverage-edge fade (`_CloudFadeParams`). Transparent with **`ZWrite On`**: depth resolves overlapping faces (see the v1.1 history entry)     |
| Motion        | **Wind drift (was CL-1):** cloud-space tiles on a drift-carrying root — per-frame cost is one root transform move; re-key sweep only on cloud-tile crossing; accumulator wraps at the pattern period; `_windBlocksPerSecond` inspector vector (default `(−0.6, 0)`), RF-7 owns it later |
| Lighting/time | **Face shading + tint + edge fade (was CL-2, absorbs RF-2 §5):** top 1.0 / bottom 0.7 / X 0.9 / Z 0.8 on Fancy, flat on Fast; hue follows `SkyLightColor`, brightness follows the shared `VoxelLightToShadow` curve at `sunLuminance = 1`, **normalized to noon** (noon look = authored `_Color` exactly; night matches terrain's relative darkening)              |
| Update driver | Per-frame `Clouds.Update` drift tick (root move only, allocation-free); the re-key sweep runs on cloud-tile crossing and from `CheckViewDistance` / `Reanchor()` / `OnSettingsChanged` → `Reinitialize()` (drift survives reinit)                                                     |
| Layers        | One layer at `cloudHeight = 100`; `cloudDepth` field exists but is unused (hull is hardcoded 1 block tall via `VoxelVerts`)                                                                                                                                                          |

---

## Master summary table

| ID   | Finding                                                            | Effort | Risk | Benefit | Seed | Save |
|------|--------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| CL-3 | Static ripped pattern — procedural seeded (optionally infinite)    |   🟡   |  🟢  |   🟡    |  ✅   |  ✅   |
| CL-4 | Frozen shapes — slow density evolution + weather-driven coverage   |   🟡   |  🟡  |   🟡    |  ✅   |  ✅   |
| CL-5 | `Volumetric` quality tier — raymarched slab above the voxel styles |   🔴   |  🟡  |   🟡    |  ✅   |  ✅   |
| CL-6 | Single layer — second high-altitude parallax layer                 |   🟢   |  🟢  |    ⚪    |  ✅   |  ✅   |
| CL-7 | Cloud shadows — pattern projected as terrain sunlight attenuation  |   🟡   |  🟡  |   🟡    |  ✅   |  ✅   |
| CL-8 | Flying through clouds does nothing — in-cloud screen fog           |   🟢   |  🟢  |    ⚪    |  ✅   |  ✅   |

---

## Detail sections

### CL-3 — Procedural cloud pattern (seeded, optionally infinite)

**Classification:** Polish with identity value — replaces the ripped Minecraft `clouds.png`
with an own-engine pattern; kills visible 512-block repetition at high render distance.

**What exists today:** `LoadCloudData` thresholds `clouds.png` once into a 512×512 bool grid;
the pattern repeats every 512 blocks (deliberately exact via `WrapToPattern`).

**Proposal — two tiers, explicit verdict:**

#### Option A — Seeded periodic noise pattern (✅ **CHOSEN for v1 of this item**)

Generate the 512×512 bool grid at init from thresholded FBM value noise made **periodic at the
grid width** (lattice wraps mod 512 — textbook tileable noise), seeded from the world seed.

- ✅ Drop-in: everything downstream (`_cloudData`, wrap, mesh cache) is untouched.
- ✅ Per-world unique skies; threshold = future weather-coverage knob (CL-4).
- ✅ Trivial to generate in a Burst job or a one-shot managed loop at init (<1 ms class).
- ❌ Still repeats every 512 blocks (same as today, so no regression).

#### Option B — Infinite non-repeating pattern (deferred follow-up, not rejected)

Drop periodicity: sample unbounded noise per 64-block pattern tile on demand, keyed by world
tile; mesh cache becomes LRU-evicted instead of 64-entry-max.

- ✅ No repetition at any render distance.
- ❌ Mesh cache grows with travel → needs eviction policy + far-coordinate noise-precision care
  (the ±2²⁴ float degradation class from WS-4 §9 applies to noise inputs).
- ❌ Interacts with the shipped wind drift (cache thrash along the wind axis) — design for it.

**Dependencies / cross-links:** none hard for Option A. Option B wants a
look at WS-4 §9's noise-precision notes. Seed ✅ per the legend (terrain is untouched; the
*sky* varying by seed is the feature).

---

### CL-4 — Slow shape evolution + weather-driven coverage

**Classification:** Polish. The "clouds are weather" item — RF-7 §4's cloud knobs terminate here.

**What exists today:** The pattern is immutable after init; shapes never change. RF-7 (weather)
names cloud color/density as storm levers but has nothing to drive.

**Proposal:**

1. Make the pattern a **thresholded scalar field**: keep a `float[,]` density grid (CL-3's
   noise pre-threshold), derive `_cloudData = density > _coverage`. `_coverage` becomes the
   weather knob: clear ≈ 0.6, overcast ≈ 0.35, storm ≈ 0.2 (tune in play).
2. **Evolution:** advance the field by sampling 3D noise at `(x, z, t)` with `t` in
   minutes-scale. Re-derive changed tiles' meshes on a **budget** (≤1 tile mesh/frame,
   round-robin) so shapes morph imperceptibly with zero hitches; the shared-mesh cache entry is
   rebuilt in place and every instance updates for free.
3. Coverage transitions (weather change) reuse the same budgeted sweep — clouds visibly thicken
   as a storm rolls in, which is most of RF-7 §4's storm-sky read.

**Dependencies / cross-links:** CL-3 Option A (scalar field) is the natural substrate; RF-7
supplies the weather state (this item degrades to a dev-console knob until then). Risk 🟡 only
because per-frame remeshing needs the budget honored (no hot-path GC — pool the mesh lists per
`GENERAL_OPTIMIZATION_GUIDE.md`).

---

### CL-5 — `Volumetric` quality tier

**Classification:** Polish, flagship-visual. A fourth `CloudStyle` above `Fancy`.

**What exists today:** `CloudStyle` has `Off/Fast/Fancy`; all mesh-based, hard-edged, uniform
interiors.

**Proposal — scope the tier deliberately small (options with verdict):**

#### Option A — Full-sky raymarched FBM skybox clouds (rejected for now)

- ✅ The prettiest possible sky.
- ❌ A per-pixel raymarch bill unrelated to the voxel aesthetic; large shader project; poor fit
  for the engine's mobile-conscious tiers (GS-4 audit would gate it hard).

#### Option B — Raymarched **slab** over the existing pattern (✅ **preferred direction**)

Render one camera-following quad (or shallow box) spanning the cloud layer
`[cloudHeight, cloudHeight + thickness]`; the fragment shader raymarches a small fixed step
count (8–16) through a density function = the CL-3/CL-4 scalar field uploaded as a
`Texture2D` (R8, 512², refreshed only when the field changes) × a vertical profile. Soft
edges, wispy tops, interior light falloff — while sampling the *same* pattern the mesh styles
use, so switching tiers keeps the same sky layout.

- ✅ Bounded cost (slab intersection is analytic; step count fixed); one draw call replaces
  thousands of tile draws at this tier.
- ✅ Reuses the shipped drift offset (UV scroll) and CL-4 coverage (threshold uniform) directly.
- ❌ New shader complexity class for this repo (raymarch loop, depth compositing against
  terrain via scene depth); needs the transparent-queue/fog interactions verified against the
  liquid shader's camera-opaque-texture reliance (same watchpoint as RF-2 §Risks).

**Dependencies / cross-links:** wants CL-3's field-as-texture and the drift offset as a
uniform; ships behind `CloudStyle.Volumetric` so regressions are opt-in. Quality-tier defaulting folds into
the GS-4 render-tier audit.

---

### CL-6 — Second high-altitude cloud layer

**Classification:** Minor polish, very cheap now that the drift plumbing is shipped.

**What exists today:** One layer at `cloudHeight = 100`. The `Clouds` component is effectively
singleton-shaped but nothing structurally prevents a second instance.

**Proposal:** generalize `Clouds` config into a small per-layer struct (height, tile scale,
drift multiplier, opacity, style clamp) and run 1–2 layers: e.g. main at 100, a sparser
higher layer at ~170 with 2× pattern scale, 1.5× drift, 60% opacity, `Fast` style always
(distant layer never needs `Fancy` hulls). Different speeds give genuine parallax — a strong
"alive sky" cue for almost no code.

**Dependencies / cross-links:** the shipped drift (a per-layer drift multiplier is the point).
Layer configs are inspector data on the existing `Clouds` object — no settings-UI change
needed for v1.

---

### CL-7 — Cloud shadows on terrain

**Classification:** Polish, high-impact ambience (ground visibly responds to the sky).

**What exists today:** Nothing — terrain sunlight is the BFS skylight field; clouds and
terrain lighting are fully decoupled.

**Proposal:** shader-only projection, **zero BFS/lighting-engine contact** (same contract as
RF-1's blood-moon tinting): upload the cloud density field as the CL-5 texture, and in the
block shaders attenuate only the *sky-light contribution* by a sample at the fragment's
voxel XZ (+ the drift offset), softened (bilinear + slight blur) and scaled by a strength
uniform (~0.15–0.25). Under a drifting cloud the ground darkens subtly and moves with it.

- Requires the fragment's **voxel-space** XZ in the block shaders — available via the
  existing `_WorldOriginOffset` global (WS-4 §4.6), so origin shifts are already handled.
- ❌-watch: interacts visually with smooth lighting/AO — tune strength low, capture A/B
  screenshots per `perf-benchmark` visual-verification habits before shipping.

**Dependencies / cross-links:** CL-3/CL-4's field texture (or a one-off upload of today's
bool grid), the drift offset. Cost: one texture sample in opaque/transparent block shaders —
screen it with a GS-4-style tier capture on target hardware.

---

### CL-8 — In-cloud screen fog

**Classification:** Minor. Flying (or `/teleport`-ing) through the layer currently clips
through paper-thin geometry with no feedback.

**Proposal:** when the camera is inside the layer band **and** the density field at the
camera's cell is positive, blend a fullscreen fog tint (cheap: drive fog color/density or a
tiny overlay alpha; strength eased over ~0.5 s). One sample of the same field per frame on the
CPU — no shader work strictly required.

**Dependencies / cross-links:** density field lookup (works against today's `_cloudData`
too); pairs naturally with CL-5 where the slab shader gives the effect for free from inside.

---

## Recommended order

| Wave | Items                  | Rationale                                                                            |
|------|------------------------|----------------------------------------------------------------------------------------|
| v1   | ~~CL-1 → CL-2~~        | ✅ **SHIPPED 2026-07-19** (`d52b089` + `12e6cf6`, in-game verified; archived in v1.1) |
| v2   | CL-3 (Option A) → CL-6 | Own-engine seeded pattern, then parallax layers (trivial on the drift plumbing)       |
| v3   | CL-4 → CL-7            | Field substrate + weather knobs (waits on/degrades without RF-7), shadows             |
| v4   | CL-5, CL-8             | Volumetric tier once the field/texture path exists; CL-8 rides along                  |

---

## Constraint compliance

| Constraint             | How CL-* complies                                                                                                                 |
|------------------------|-----------------------------------------------------------------------------------------------------------------------------------|
| Packed-`uint` voxels   | Untouched — clouds never enter voxel data                                                                                         |
| Burst job rules        | Only CL-3/CL-4 generation may use jobs; pure `Unity.Mathematics` noise over native arrays if so                                   |
| No hot-path GC         | The drift re-key uses the existing pool + mesh cache; CL-4 remesh budget reuses pooled lists; per-frame paths allocation-free     |
| Pooling                | Tile GameObject pool + shared-mesh cache from `c7eabd6` are the substrate for every item                                          |
| Serialization          | Nothing on disk (weather persistence explicitly belongs to RF-7, not here) — Save ✅ across the table                              |
| WS-4 coordinate spaces | Drift accumulator wrapped mod pattern width; pattern math integer; placement via `VoxelToUnity`; shadows via `_WorldOriginOffset` |

---

## Document History

* **v1.2** - **Night-brightness fix** (2026-07-19, in-game verified): `SkyLightColor` is hue-only
  (brightness lives in the shade curve per the RF-1 split), so clouds rendered full-bright at
  night — the shader now applies the shared `VoxelLightToShadow` curve at `sunLuminance = 1`,
  normalized to noon (daytime look unchanged by construction). Baseline row updated.
* **v1.1** - **CL-1 + CL-2 SHIPPED & archived** (2026-07-19, `d52b089` + `12e6cf6`, in-game
  verified): wind drift (cloud-space tiles on a drift-carrying root, pattern-period-wrapped
  accumulator, exact `VoxelToUnity(Vector3Int)` anchor, inspector wind vector) + shader
  (MC-style face shading Fancy-only, `SkyLightColor` tint absorbing RF-2 §5, coverage-edge
  fade). Baseline table updated to the shipped state. Implementation deviation worth keeping:
  the planned "keep the stencil `IncrSat` guard" was **wrong once faces shade differently** —
  the stencil picks a draw-order-arbitrary winner (visible interior patchwork), and a
  depth-prepass + `ZTest Equal` replacement did not execute as URP multi-pass (clouds vanished);
  the shipped answer is single-pass **`ZWrite On`** alpha blending (vanilla-MC parity, nearest
  face wins by depth).
* **v1.0** - Initial report (CL-1..CL-8, recommended order, RF-2 §5 absorption noted)

---

**Last Updated:** 2026-07-19
**Next Review:** when CL-3 or CL-6 starts (re-verify `Clouds.cs`/`CloudShader.shader` against the v1.1 baseline) or on the next RF-7 design pass (wind/weather seam)
