# Volumetric & Ray-Traced Effects Report

**Version:** 1.2
**Date:** 2026-07-20
**Status:** Open backlog. Items are removed (archived) when implemented and verified.
**Target:** Unity 6.5 (Mono for dev; IL2CPP for production), URP 17.5

> The backlog for **volumetric and ray-traced rendering effects** — volumetric fog/god rays,
> volumetric water, colored light transmission through tinted voxels, voxel-traced GI and
> reflections — all gated behind an explicit **Experimental/Expensive settings tier** (VX-0).
> The single most important finding: **hardware ray tracing (DXR) is off the table (URP does not
> support it), but this engine is unusually well-positioned for the *software* equivalents** —
> the voxel grid is its own ray-acceleration structure, and the shipped BFS RGB light field is a
> precomputed radiance cache. Most items below are therefore "upload existing CPU data into GPU
> volumes + raymarch," not new lighting simulation. Self-contained ranking (§Recommended order);
> deliberately **not** folded into the combined TF/RF roadmap, following the `CL-*` precedent.

**Audited:** 2026-07-20, at commit `f6b67f5` (branch `feat/world-scaling`).
Findings are from static review of the URP configuration
(`Assets/settings/Rendering/VoxelEngine-URP-Asset.asset` + `VoxelEngine-URP-Renderer.asset`),
the shader stack (`VoxelLighting.hlsl`, `VoxelCommon.hlsl`, `LiquidCore.hlsl`, the three block
shaders, `CloudShader.shader`), `ProjectSettings.asset` graphics APIs, `SettingsManager.cs` +
`SettingFieldAttribute.cs`, and the sibling design reports (`RF-*`, `CL-*`, `FL-*`, `GS-*`).
Runtime state was **verified in code, not assumed** — see each item's "What exists today".
**Amended:** 2026-07-20 — second gap sweep added VX-8 (per-fragment voxel lighting — the item
that unblocks MR-8's smooth-lighting constraint), VX-9 (heat-haze distortion), VX-10
(interactive water ripples).

**Relationship to other documents:**

- [`LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md`](LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md)
  (`RF-*`) — RF-6 rejected GI-class features *for the default tier* and said "revisit only
  alongside a future lighting overhaul"; **this report is that revisit**, under experimental
  gating (VX-6 does not overturn RF-6's default-tier verdict). RF-2's fog sync is the cheap
  distance fog VX-2 layers on; RF-3's post stack enablement shares the Volume/renderer-asset
  surface; RF-1's `SkyDarken`/`SunElevation` feed VX-2's sun phase term. The gap sweep's
  non-volumetric ideas were **routed there, not here** (2026-07-20): sky ambience content
  (aurora/meteors/flare → RF-2 §6), extra post effects (vignette/DoF/motion blur → RF-3 §5),
  lightning (RF-7 §6), and animated block textures (new **RF-8**).
- [`CLOUD_RENDERING_IMPROVEMENTS_REPORT.md`](CLOUD_RENDERING_IMPROVEMENTS_REPORT.md) (`CL-*`) —
  CL-5 (raymarched cloud slab) **stays owned there**; it is this report's closest shipped-design
  relative and shares the raymarch/depth-composite watchpoints. CL-7 (cloud shadows) can later
  read VX-1's sky channel instead of its own 2D upload, but has no dependency on it.
- [`FOLIAGE_LIVELINESS_IMPROVEMENTS_REPORT.md`](FOLIAGE_LIVELINESS_IMPROVEMENTS_REPORT.md)
  (`FL-*`) — no direct coupling; FL-6's fireflies pair visually with VX-2's night fog.
- [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) — `GS-2` (opaque
  texture) and `GS-4` (render-tier audit) constrain every render-feature addition here; `MR-8`'s
  "per-chunk 3D light texture" aside describes the same data structure as VX-1.
- [`../Architecture/SMOOTH_AND_RGB_LIGHTING.md`](../Architecture/SMOOTH_AND_RGB_LIGHTING.md) —
  the `ushort` light model (sky 4b + RGB 3×4b) that VX-1 uploads and VX-4 extends.
- [`../Architecture/LIGHTING_SYSTEM_OVERVIEW.md`](../Architecture/LIGHTING_SYSTEM_OVERVIEW.md) —
  the BFS engine VX-4 modifies (per-channel propagation is the extension point).
- [`WORLD_SCALING_FLOATING_ORIGIN.md`](WORLD_SCALING_FLOATING_ORIGIN.md) — all
  GPU volumes are camera-following and sampled in **voxel space** via `_WorldOriginOffset`
  (the LiquidCore/FL-2 precedent); WS-4 coordinate rules apply to every item.
- [`OM1_DEVICE_CALIBRATION.md`](OM1_DEVICE_CALIBRATION.md) — the experimental tier defaults
  derive from its device-tier model (VX-0).
- [`../Architecture/DATA_DRIVEN_SETTINGS_UI.md`](../Architecture/DATA_DRIVEN_SETTINGS_UI.md) —
  VX-0's tooltip/tier plumbing extends this system; every knob ships as a `SettingFieldAttribute`
  field.

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
> Every VX item except VX-0 and VX-4 is *expected to cost real GPU milliseconds* — that is the
> premise of the experimental tier, not a finding against the items.

---

## Feasibility verdicts (read first)

### Why this engine is a good host for these effects

1. **The voxel grid is a free acceleration structure.** Hardware RT exists to trace rays through
   *unstructured triangle soup* via BVHs. A voxel world needs none of that: a 3D-DDA march
   through an occupancy volume visits exactly the cells a ray crosses, in order, with integer
   math — the technique behind every "ray-traced voxel" renderer. Software tracing here is not a
   consolation prize; it is the architecturally native approach.
2. **The BFS light field is a precomputed radiance cache.** Per-voxel sky exposure + RGB
   blocklight (the shipped `ushort` model) is exactly the data a volumetric scatter or a GI
   gather wants at a sample point. No shadow maps are needed for god rays: **stored skylight
   *is* the sun-occlusion field** (a canopy or cave ceiling already casts a "volumetric shadow"
   in the data). The expensive part of volumetrics elsewhere — computing visibility — is
   already paid for on the CPU.
3. **The precedent stack exists.** A custom `ScriptableRendererFeature` already ships
   (`UIBlurRendererFeature` on `VoxelEngine-URP-Renderer.asset`); CL-5 has a designed raymarch
   shader; `_WorldOriginOffset` gives shaders re-anchor-safe voxel-space positions; HDR is on.

### Hardware ray tracing (DXR / `RayTracingAccelerationStructure`) — ❌ rejected

- ❌ **URP has no ray-tracing support** — Unity's RT pipeline integration (RTGI, RT reflections,
  RT shadows) is HDRP-only, and URP 17.5 exposes no ray-tracing pass. Using the raw
  `RayTracingShader` API under URP means hand-building the entire dispatch/denoise/composite
  stack with zero pipeline help.
- ❌ Switching to HDRP is rejected outright: the whole shader stack (5 custom voxel shaders +
  includes) is URP-authored, HDRP drops the mobile/WebGL targets, and the per-vertex voxel
  lighting model would fight HDRP's lighting loop.
- ❌ Even ignoring the pipeline, a BLAS/TLAS over per-section meshes that are *rebuilt constantly
  by chunk streaming* churns acceleration-structure builds — the wrong tool for dynamic voxel
  geometry (see verdict 1: we don't need BVHs at all).
- ✅ **Verdict:** every "ray tracing" item below is *software voxel tracing* (fragment/compute
  DDA over GPU-resident voxel data). This also keeps effects working on D3D11, which the
  Windows build still targets alongside D3D12/Vulkan.

### Compute shaders — first use in this repo

No `.compute` file exists in the project today (verified by glob). VX-1's volume maintenance and
VX-6's GI passes want compute; VX-2/VX-3 can ship as pure fragment passes. The graphics targets
(D3D11+, Vulkan, Metal) all support compute; WebGL does not — experimental effects are
desktop-first by VX-0's tier defaults, so this is acceptable. First-compute plumbing (asset
conventions, dispatch helpers, tier fallbacks) lands with VX-1.

### Cost-honesty table (order-of-magnitude, 1080p desktop, to be re-measured per `perf-benchmark`)

| Effect                              | Expected GPU cost                       | Tier                        |
|-------------------------------------|-----------------------------------------|-----------------------------|
| VX-4 colored light (BFS filter)     | ~0 (CPU-side, one-off)                  | Default-eligible            |
| VX-1 light volume upload            | <0.2 ms amortized                       | With any consumer           |
| VX-2 volumetric fog (half-res, 24×) | 1–2.5 ms                                | Experimental                |
| VX-3 underwater volumetrics         | 0.5–1.5 ms (submerged)                  | Experimental                |
| VX-7 SSR water reflections          | 0.5–1.5 ms                              | Experimental                |
| VX-7b voxel-traced reflections      | 2–4 ms                                  | Experimental                |
| VX-6 voxel-traced GI (¼-res + TAA)  | 4–10 ms                                 | Experimental                |
| VX-8 per-fragment lighting          | 0.3–1 ms (replaces vertex-light interp) | Experimental → quality tier |
| VX-9 heat haze (near lava)          | 0.2–0.5 ms                              | Experimental                |
| VX-10 ripple sim (256² ping-pong)   | 0.2–0.5 ms                              | Experimental                |

---

## Master summary table

| ID    | Finding                                                                                      | Effort | Risk | Benefit | Seed | Save |
|-------|----------------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| VX-0  | No "Experimental/Expensive" settings tier exists — no tooltips, no tier-gated defaults       |   🟢   |  🟢  |   🟢    |  ✅   |  ✅   |
| VX-1  | GPU light+occupancy volume — the shared substrate every volumetric/traced item samples       |   🔴   |  🟡  |   🟢    |  ✅   |  ✅   |
| VX-2  | Volumetric fog & god rays — raymarched scattering lit by the BFS field                       |   🔴   |  🟡  |   🟢    |  ✅   |  ✅   |
| VX-3  | Volumetric water — depth absorption, underwater light shafts, procedural caustics            |   🟡   |  🟡  |   🟡    |  ✅   |  ✅   |
| VX-4  | Colored light through tinted voxels — per-channel BFS filters (stained glass), zero GPU cost |   🟡   |  🔴  |   🟢    |  ✅   |  ✅   |
| VX-5  | Voxel DDA trace substrate — GPU occupancy/albedo volume + shader trace library               |   🔴   |  🟡  |   🟡    |  ✅   |  ✅   |
| VX-6  | Voxel-traced diffuse GI — 1-bounce gather over VX-5, temporally accumulated (experimental)   |   🔴   |  🔴  |   🟡    |  ✅   |  ✅   |
| VX-7  | Water/glass reflections — SSR first, voxel-traced upgrade via VX-5                           |   🟡   |  🟡  |   🟡    |  ✅   |  ✅   |
| VX-8  | Per-fragment voxel lighting sampled from VX-1 — smoother light, unblocks MR-8's constraint   |   🔴   |  🔴  |   🟢    |  ✅   |  ✅   |
| VX-9  | Heat-haze / distortion media — screen-space shimmer above lava (and desert air later)        |   🟡   |  🟢  |    ⚪    |  ✅   |  ✅   |
| VX-10 | Interactive water surface — camera-local ripple sim (rain, player wake, splashes)            |   🟡   |  🟡  |   🟡    |  ✅   |  ✅   |

---

## Detail sections

### VX-0 — Experimental/Expensive settings tier

**Classification:** Foundation. Everything else in this report ships behind it.

**What exists today:** The settings system is attribute-driven (`SettingFieldAttribute`:
`Label`/`Format`/`Order`/`DebugOnly`, `SettingsManager.cs` Graphics tab rows 0–8). There is
**no `Tooltip` property** on the attribute, no visual "expensive/experimental" labeling, and no
device-tier-aware defaulting (OM-1 is itself still open backlog). `DebugOnly` is the only
precedent for a gated class of settings.

**Gap / finding:** the user-facing contract for this whole report is "expensive effects, clearly
marked, off by default" — the settings UI cannot currently express any of that.

**Proposal:**

1. `SettingFieldAttribute` gains `Tooltip` (string, rendered as hover/hold text by the
   data-driven UI) and `Experimental` (bool → the UI renders a ⚠/flask badge next to the label
   and appends a standard "expensive/experimental — may cost significant performance" suffix to
   the tooltip). Coordinate with the noted future `Group` property idea (settings-group memory)
   so the attribute grows once, not twice.
2. New Graphics-tab block (high `Order` values so it sits last): one master
   `enableExperimentalEffects` toggle + per-effect toggles/enums (fog quality, GI on/off, …)
   that only apply when the master is on. Defaults: **all off**, on every tier.
3. Runtime gating helper on `World` (or a small `ExperimentalEffects` service) that render
   features query — features must free their GPU resources when toggled off, not just skip
   rendering (the `OnSettingsChanged` path the clouds already use is the pattern).
4. When OM-1 ships, experimental defaults stay off everywhere; OM-1 only decides whether the
   *section is shown* on low-tier devices.

**Dependencies / cross-links:** `DATA_DRIVEN_SETTINGS_UI.md`; the `Group` property idea;
OM-1 (soft — degrade gracefully without it).

---

### VX-1 — GPU light + occupancy volume (the substrate)

**Classification:** Core enabler — the keystone of this report. Ships together with its first
consumer (VX-2), never alone.

**What exists today:** Per-voxel light lives CPU-side in per-section `ushort` arrays (sky 4b +
RGB 3×4b, `SMOOTH_AND_RGB_LIGHTING.md`) and reaches the GPU only baked per-vertex at mesh time.
No 3D texture of any kind exists in the project. Shaders already receive re-anchor-safe
voxel-space coordinates via `_WorldOriginOffset` (used by LiquidCore, FL-2 sway, planned CL-7).
`MR-8`'s constraints section already floated "lighting moves out of vertex data into a per-chunk
3D light texture" as a future direction — this item is that data structure, engine-wide.

**Gap / finding:** every volumetric or traced effect needs to answer "how much light (and what
color) is at world position P?" *per sample, on the GPU*. Without a resident light volume, each
item would invent its own upload path.

**Proposal:**

1. **A camera-following 3D texture pair** covering a configurable radius (default ≈ 160×128×160
   voxels ≈ 10×8×10 chunks):
    - `_VoxelLightVolume` — `R16_UInt` (the raw `ushort` light word; decode sky/RGB nibbles in
      shader exactly like `ApplyVoxelLightingRGB` does per-vertex) ≈ 6.3 MB.
    - `_VoxelOccupancyVolume` — `R8` (0 = air, else opacity class; VX-5 later widens this) ≈
      3.1 MB.
2. **Toroidal (wrapping) addressing** — the volume never scrolls; world voxel → texel is
   `voxelCell mod volumeSize`. Player movement only dirties the newly-entered slab, and origin
   re-anchors are free because addressing is in voxel space (WS-4 rules; `_WorldOriginOffset`
   converts fragment positions).
3. **Update path:** hook the existing per-section apply points (lighting completion + mesh
   apply already know which 16³ sections changed) → enqueue dirty sections → budgeted per-frame
   upload (N sections/frame) from a pooled staging `NativeArray` via `Texture3D.SetPixelData`
   sub-updates, or a compute-shader scatter if `SetPixelData` sub-region granularity proves too
   coarse (measure first — this is the item's main unknown, flagged for its plan's Step-1
   verification). CPU-side gather of a section's light array into staging is a Burst job.
4. **Out-of-volume fallback:** samples outside the covered radius return "full sky exposure,
   no blocklight" so distant fog degrades to plain height fog instead of black.
5. Suite guard: a validation scenario asserting a synthetic section's light word round-trips
   CPU→staging→texel exactly (readback in editor), plus a re-anchor determinism case.

**Dependencies / cross-links:** VX-0 (allocated only while a consumer is enabled); consumed by
VX-2/VX-3/VX-6/VX-7b and offered to CL-7; `chunk-lifecycle` skill for the apply-point hooks;
WS-4 coordinate rules.

---

### VX-2 — Volumetric fog & god rays

**Classification:** Flagship experimental visual — the item that makes the tier worth building.

**What exists today:** Fog is entirely disabled (`m_Fog: 0`); RF-2 §4 plans classic distance fog
synced to the sky gradient (cheap, per-vertex/per-fragment analytic — that item stays as the
default-tier fog). No post-processing volume exists (RF-3). Torch/lava light reaches the eye
only off surfaces; air is never lit.

**Gap / finding:** light shafts through a forest canopy at dawn, torch glow hanging in cave air,
a lit doorway spilling into night fog — the single largest "next-gen voxel" read — all require
in-scattering along the view ray, which no analytic fog can fake.

**Options:**

#### Option A — Screen-space radial "god ray" blur (sun shafts only) (❌ rejected as the main item)

- ✅ Very cheap (~0.3 ms), no volume data needed.
- ❌ Sun-only (no torch glow), vanishes when the sun leaves the frame, no fog body. Doesn't use
  the engine's actual strength (the light field). May ride along later as a low-tier extra if
  ever wanted — not designed here.

#### Option B — Raymarched froxel-style scatter sampling VX-1 (✅ **CHOSEN**)

1. A `ScriptableRendererFeature` pass after opaques at **half resolution**: per pixel, DDA-free
   fixed-step march (16–32 steps, blue-noise jittered) from camera to the opaque depth.
2. Per step: `scatter += density(P) × (skyTerm + blockTerm)` where
    - `skyTerm` = decoded sky nibble × `SkyLightColor` × RF-1's `SkyDarken` parity × a
      Henyey–Greenstein-ish phase toward `_SunDirection` (RF-1/RF-2 provide both; until RF-1
      ships, a fixed noon sun keeps the item testable) — **this is what makes canopy/cave light
      shafts emerge with zero shadow maps** (verdict 2),
    - `blockTerm` = decoded RGB nibbles (isotropic phase) — torches visibly light the air,
    - `density(P)` = height falloff × RF-7 weather multiplier (constant until RF-7) × optional
      noise wisp.
3. Bilateral depth-aware upsample to full res, composite additively + transmittance.
4. **Transparent-pass watchpoint:** fog composites after opaques, so water/glass surfaces need
   the fog applied in their own shaders or accept slight mismatch — same class of issue as
   CL-5's depth compositing and GS-2's opaque-texture reliance; resolve in the item's plan with
   A/B captures (`Unity_Camera_Capture`).
5. Knobs (all `SettingFieldAttribute`, experimental block): quality (steps/resolution),
   density, enable.

**Dependencies / cross-links:** VX-1 (hard); RF-1/RF-2 (sun direction + colors — soft,
degrades to fixed noon); RF-7 (weather density — soft); GS-4 tier audit for the pass cost.

---

### VX-3 — Volumetric water

**Classification:** Polish, high-visibility (every swim). Mostly independent of VX-1 — ships
its cheap half without it.

**What exists today:** `UberLiquidShader`/`LiquidCore.hlsl` render the *surface* (waves, GS-1
procedural FBM, refraction via the camera opaque texture — GS-2). Being underwater applies no
fog, no light attenuation, no caustics; the underwater camera sees air-clear water.

**Gap / finding:** water reads as a surface, not a medium.

**Proposal (two halves, independently shippable):**

1. **Cheap half (no VX-1):** when the camera's voxel cell is water (VQ-1 integer query, the
   CL-8 pattern), enable an underwater state: full-screen depth-based Beer–Lambert absorption
   tint (per-channel: red dies first), reduced far plane feel via fog density, and a
   depth-scaled darkening from the camera cell's *own* stored skylight (one CPU voxel query —
   deep water is dark water for free). Procedural caustics: project the existing GS-1 noise
   family onto surfaces via a light-space scroll in the block shaders, masked to underwater
   fragments, sky-exposure-scaled so caves get none.
2. **Experimental half (VX-1):** underwater god rays — the VX-2 march with water-specific
   density/phase (stronger forward scatter, wavelength-dependent extinction) run inside the
   water medium; shafts wobble by the same surface-noise family. Shares VX-2's pass and most
   of its shader — build as a medium-parameter variant, not a second feature.

**Dependencies / cross-links:** VQ-1 (shipped); GS-1 (don't stack two expensive noise fields —
coordinate if GS-1's LUT optimization lands first); VX-2 (experimental half); CL-8 precedent
for the camera-in-medium state machine.

---

### VX-4 — Colored light transmission through tinted voxels (stained glass)

**Classification:** Core — the exact "colored lighting after passing through colored voxels"
wish, and the report's best value-per-cost: **zero GPU cost, no volumes, no raymarching** — it
is a lighting-engine feature, not a rendering feature. Default-tier eligible (not experimental).

**What exists today:** Light propagation treats every non-opaque block identically: the BFS
steps light down by 1 per voxel regardless of what it passes through
(`LIGHTING_SYSTEM_OVERVIEW.md`); `BlockType` has emission RGB but **no transmission filter**.
RGB *emission* is shipped and proven (per-channel BFS); there is no tinted-transparent block
content yet.

**Gap / finding:** torch light through blue glass comes out torch-colored. The per-channel BFS
already does all the hard work — it just lacks a per-block, per-channel attenuation input.

**Proposal:**

1. `BlockType` gains `lightFilter` (RGB, authored in BlockEditor as a color; 1,1,1 = neutral =
   today's behavior; mirrored into `BlockTypeJobData` — **mind the member-wise copy-initializer
   trap** noted when `swayStrength` shipped).
2. In the per-channel blocklight BFS propagation step, when light *enters* a voxel, the step
   cost per channel becomes `max(1, round((1 − filter[c]) × 15))`-style attenuation (exact
   formula chosen in the item's plan; must preserve `≥1` decay so propagation always
   terminates). Neutral filter compiles to today's `−1` — **bit-identical for all existing
   content by construction**, which is the differential the lighting suite proves.
3. **Sky light is explicitly out of scope:** sky storage is 4-bit mono by design (RGB sky was
   rejected in `SMOOTH_AND_RGB_LIGHTING.md`); sunlight through stained glass attenuates mono,
   it does not colorize. The visible wish (torch/lamp light tinted by glass) is blocklight, so
   this limitation costs little; state it in the settings-free feature (there is no toggle —
   neutral blocks are free).
4. Removal/darkness BFS must apply the same per-channel costs symmetrically (the Bug 16/17
   per-channel removal-veto machinery is the guard rail — reuse its suite patterns, prove-red
   a stained-glass removal scenario before shipping).
5. Content: a StainedGlass block family (BlockEditor + `Generate Block IDs`), transparent-pass,
   filter = its color.

**Risk note (why 🔴):** this touches BFS semantics — the engine's most invariant-laden system
(boundary rules, removal parity, RGB fidelity gaps C10/C12 still open). Effort is genuinely
medium; risk is what demands the full validation-driven-bugfix discipline (differential
baselines: neutral-world bit-identity + filtered-scenario oracles).

**Save note:** stored light values in existing saves stay valid (no filter blocks exist in
them); the format is unchanged — Save ✅.

**Dependencies / cross-links:** lighting validation suite (differential + prove-red);
`SMOOTH_AND_RGB_LIGHTING.md`; VX-6 later inherits colored bounce for free wherever the light
field already carries the tint.

---

### VX-5 — Voxel DDA trace substrate (software "RTX" foundation)

**Classification:** Enabler for VX-6/VX-7b. Build only when its first consumer is committed.

**What exists today:** Nothing traces rays anywhere. Voxel data is CPU-side packed `uint`s;
VX-1 (when built) contributes occupancy but not albedo.

**Gap / finding:** GI and world-space reflections need "cast a ray from P along D, return hit
voxel + its surface color" on the GPU.

**Proposal:**

1. Widen VX-1's occupancy volume to an **albedo volume** (`RGBA8`: RGB = average tile color,
   A = opacity class). Per-block average colors are baked once from the atlas at startup (the
   block's face-texture mean — an editor-time LUT via the `python-scripting`/atlas tooling is
   fine); the same dirty-section upload path fills both textures.
2. A shared HLSL include (`VoxelTrace.hlsl`): branchless 3D-DDA (Amanatides–Woo) over the
   volume with a step cap (64–128), returning hit cell, face normal (from the DDA axis), albedo,
   and the light word from `_VoxelLightVolume` at the hit — i.e. **hit radiance ≈ albedo ×
   decoded BFS light**, no secondary rays needed (verdict 2 again: the light field is the
   radiance cache).
3. Optional level-2 acceleration if the step cap hurts: a 1/8-res "any solid in this 8³ brick"
   mip for empty-space skipping — decide by measurement, not up front.

**Dependencies / cross-links:** VX-1 (hard — same textures, same upload path); consumers VX-6,
VX-7b, and (cross-report) a future CL-5 interplay is possible but not designed.

---

### VX-6 — Voxel-traced diffuse GI (experimental)

**Classification:** The maximal experiment. Explicitly **does not overturn RF-6**: SSAO remains
the default-tier answer; this is the "future lighting overhaul" tier RF-6 deferred to, and it
ships desktop-only, off by default, behind VX-0.

**What exists today:** The BFS field is itself a coarse diffuse-GI approximation for *emitters*
(light floods around corners), and RF-6 will add SSAO. What no current or planned system
provides: **surface-color bounce** (red carpet tints the ceiling; grass-green shade under
trees) and sky-bounce into overhangs — exactly what RF-6 rejected as too expensive to store
per-voxel on the CPU. GPU-side, it doesn't need storing.

**Proposal (screen-space gather, world-space trace):**

1. Quarter-res pass: per pixel, N (4–8) cosine-weighted hemisphere rays from the G-buffer-less
   reconstructed position/normal (depth + derived normal; URP depth-normals texture if enabled
   for RF-6's SSAO — shared prerequisite), traced via `VoxelTrace.hlsl`.
2. Each hit returns `albedo × decodedLight` (VX-5) — a genuine 1-bounce path trace whose bounce
   lighting is the BFS field; miss returns sky radiance (RF-1 gradient × `SkyDarken`).
3. Temporal accumulation (exponential history, depth/normal-rejection) + edge-aware upsample;
   composite as additional ambient into the lighting term (shader-side add in
   `ApplyVoxelLightingRGB`'s consumers, scaled so `MinLightLevel`'s floor doesn't double-count).
4. Honest failure modes, stated up front: ghosting on fast light changes (BFS updates + history
   lag), quarter-res edge shimmer, and cost (§cost table: 4–10 ms) — acceptable *because* the
   tier's contract says experimental.

**Dependencies / cross-links:** VX-5 (hard), VX-1 (hard), RF-6 SSAO's depth-normals
prerequisite (do together), RF-1 (sky radiance — soft), VX-4 (colored transmission enriches
bounce color for free).

---

### VX-7 — Water & glass reflections

**Classification:** Polish. Two rungs; the first does not need any VX substrate.

**What exists today:** The liquid shader refracts via the camera opaque texture (GS-2) but
reflects nothing — no SSR, no probes; sky is a flat clear color until RF-2.

**Proposal:**

1. **VX-7a — SSR (✅ first rung):** a raymarch through the *depth buffer* (not voxel data) in
   the liquid shader or a renderer feature; reflects whatever is on screen, falls back to the
   RF-2 skybox color on miss. URP has no built-in SSR — this is a custom pass, but a
   well-trodden one. Fresnel-weighted blend with the existing refraction.
2. **VX-7b — voxel-traced upgrade (experimental):** on SSR miss (or entirely), trace the
   reflected ray via `VoxelTrace.hlsl` — off-screen world reflects correctly (the classic SSR
   artifact killer). Gated behind VX-0 like everything VX-5-derived.

**Dependencies / cross-links:** GS-1/GS-2 (same shader, same opaque-texture economics —
coordinate); RF-2 (sky fallback color); VX-5 (7b only).

---

### VX-8 — Per-fragment voxel lighting from the light volume

**Classification:** Core quality upgrade *and* a structural unblock: `MR-8` (greedy meshing)
names per-vertex smooth lighting as its hard constraint and floats "lighting moves out of
vertex data into a per-chunk 3D light texture" as the escape hatch — **VX-8 is that escape
hatch, built on VX-1**. Starts experimental; graduates to a regular quality tier if it proves
out, because it can eventually *save* more than it costs (via MR-8's 60–90 % vertex cut).

**What exists today:** All voxel light is baked per-vertex at mesh time (smooth lighting =
vertex-averaged corner values, `SMOOTH_AND_RGB_LIGHTING.md` Phase 1) and interpolated across
faces. Consequences: light changes force remeshes (the entire remesh-on-relight economy),
light resolution is capped at vertex density, and MR-8 can't merge quads whose corner lights
differ.

**Gap / finding:** with VX-1 resident, the fragment shader can know the light at its exact
position — better light than the baked path, from data the volume already holds.

**Proposal:**

1. A **filterable companion volume** to VX-1: `RGBA8_UNorm` (RGB = decoded blocklight, A =
   sky exposure), written by the same dirty-section upload (decode nibbles CPU/Burst-side or
   a tiny compute blit from the `R16` volume). Hardware trilinear filtering replaces vertex
   interpolation — `R16_UInt` cannot be filtered, which is why VX-1 alone doesn't suffice
   (~13 MB extra at default radius).
2. Block shaders (behind a multi_compile / VX-0 toggle): sample the volume at
   `fragmentVoxelPos + normal × 0.5` (the half-voxel normal offset is the standard fix for
   light bleeding through thin walls) and feed `ApplyVoxelLightingRGB` from the sample instead
   of the interpolated vertex value. **Vertex AO stays vertex-baked** (it is geometric, not
   light — keep the existing corner-darkening term).
3. Fallback contract: outside the volume radius (or feature off), shaders use the baked
   per-vertex path unchanged — distant chunks keep today's look, so the volume radius is a
   quality radius, not a correctness boundary. The seam between the two is a visual watchpoint
   (A/B capture at the boundary).
4. What this buys beyond looks: torch place/remove could eventually skip the *remesh* for
   light-only changes inside the volume (light updates become a texture upload) — a real perf
   direction, but **explicitly out of scope for v1** (the remesh economy has many dependents;
   do not touch scheduling until the visual path is proven).
5. Risk 🔴 is honest: this forks the lighting read path in every block shader, interacts with
   smooth-lighting baselines (meshing suite B-series pins TexCoord1 — untouched, but visual
   parity needs captures), and editor previews need the fallback path.

**Dependencies / cross-links:** VX-1 (hard); `MR-8` (this is its named prerequisite (b) —
coordinate if/when greedy meshing starts); GS-3 (per-fragment lighting math cost — same
audit); meshing suite (baked channels stay byte-identical — differential guard).

---

### VX-9 — Heat-haze / distortion media

**Classification:** Minor ambience, cheap. The "air above lava is hot" read; extends to desert
heat shimmer once TF climate exists.

**What exists today:** Nothing distorts the screen anywhere; lava is visually inert beyond its
emissive tint. The camera opaque texture already exists globally (GS-2), so the sampling
economics are already paid.

**Gap / finding:** lava pools read as orange floors; a subtle refraction column above them is
one of the highest ambience-per-millisecond effects available.

**Proposal:** camera-local detection + screen-space distortion:

1. A slow amortized scan (VQ-1 integer queries, FL-6's few-cells-per-frame pattern) maintains
   a small set of exposed lava-surface cells near the camera.
2. Pooled distortion quads (or one merged mesh) hover over those cells rendering **UV-offset
   noise into a low-res distortion RT** (scrolling upward, GS-1's noise family); a final blit
   (or the transparent-pass shaders) offsets the opaque-texture sample by the RT — the same
   mechanism the liquid refraction already uses, so no new pipeline concept.
3. Strength fades with distance and screen coverage is clamped (a lava ocean must not turn the
   whole frame to soup). Desert/heat-over-sand variant is a later content knob riding TF-3's
   temperature axis.

**Dependencies / cross-links:** GS-2 (opaque texture — shared economics with liquid
refraction); VQ-1 (shipped); FL-6/FL-7 pooled-service pattern; TF-3 (desert variant, later).

---

### VX-10 — Interactive water surface (ripple simulation)

**Classification:** Polish, high-visibility. The water *surface* counterpart to VX-3's medium
work: today nothing the player (or weather) does disturbs water.

**What exists today:** The liquid surface animates with GS-1's procedural noise — beautiful but
oblivious: no player wake, no splash on entry, no rain rings (RF-7 will render falling rain
that vanishes into flat water).

**Gap / finding:** water that ignores you reads as a texture; water that ripples when touched
reads as a fluid. This is the classic small GPU sim with outsized perceived-quality payoff.

**Proposal:**

1. A camera-following **height-field ripple sim**: 256² ping-pong RT pair over ~a 32–64 block
   radius, integrating the discrete wave equation per frame (one cheap fragment/compute pass;
   toroidal, voxel-space anchored per WS-4 — re-anchor-safe like every volume here).
2. Impulse writers: player entry/exit + swim strokes (position from `VoxelRigidbody` state),
   block splashes (FL-7's break/place hook when the cell is water), rain drops (random
   impulses under RF-7 rain, giving rain rings for free).
3. The liquid shader adds a normal perturbation sampled from the sim inside the covered
   radius, fading to zero at the edge — outside it, today's look, unchanged. Purely visual:
   **no gameplay/physics reads the sim** (stated limitation, keeps it render-only).
4. Interacts with GS-1: the sim's normal perturbs *on top of* the procedural waves — tune so
   the two don't double-animate; if GS-1's LUT optimization lands first, ride its texture
   plumbing.

**Dependencies / cross-links:** GS-1 (same shader); RF-7 (rain impulses — soft); FL-7
(splash hook — soft); VX-0 gating; first ping-pong-RT pattern in the repo (document it — CL-4's
budgeted-update discipline applies).

---

## Recommended order

| Wave | Items                    | Rationale                                                                                       |
|------|--------------------------|-------------------------------------------------------------------------------------------------|
| v1   | VX-0 → VX-4              | Tier plumbing is hours of work; VX-4 is the wish-list headline at zero GPU cost (CPU/BFS only)  |
| v2   | VX-1 + VX-2              | Substrate + its flagship consumer land together and prove the volume path                       |
| v3   | VX-3, VX-7a, VX-9, VX-10 | Water as a medium + SSR + the two small ambience sims — all mostly independent, high-visibility |
| v4   | VX-8                     | Per-fragment lighting once VX-1 is proven in the field — coordinate with any MR-8 start         |
| v5   | VX-5 → VX-6, VX-7b       | The tracing substrate only when GI/world-reflections are committed; the maximal experiments     |

RF-1/RF-2 (day/night + sky) remain the top-ranked features overall (combined TF/RF roadmap) and
make every VX item look better — VX-2's dawn shafts need a dawn. Nothing here blocks on them,
but v2+ is best scheduled after RF-1 ships.

---

## Constraint compliance

| Constraint                                 | How VX-* complies                                                                                                                                        |
|--------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------|
| Packed-`uint` voxels, no per-voxel objects | GPU volumes are *copies* of existing packed data; no CPU-side voxel representation changes (VX-4 adds a per-BlockType field, not per-voxel state)        |
| Burst job rules                            | Staging gathers (VX-1/VX-5) and the VX-4 BFS change are Burst jobs over native arrays; no managed types in `Assets/Scripts/Jobs/`                        |
| No hot-path GC / pooling                   | Staging buffers pooled (`ChunkJobArrayPool` pattern); dirty-section queues pooled; render passes allocation-free per frame                               |
| Sub-chunk meshing                          | Untouched — no vertex-format or meshing change anywhere in this report (the `Color32` stream stays reserved for TF-11 + RF-3)                            |
| Async BFS lighting                         | VX-4 extends propagation *inside* the existing per-channel BFS with suite-proven bit-identity for neutral content; boundary/removal invariants preserved |
| Serialization                              | Nothing on disk changes in any item — Save ✅ across the table                                                                                            |
| WS-4 coordinate spaces                     | All volumes voxel-space toroidal, sampled via `_WorldOriginOffset`; no Unity-space world coordinates stored anywhere                                     |
| Settings via `DATA_DRIVEN_SETTINGS_UI`     | Every knob is a `SettingFieldAttribute` field; VX-0 extends the attribute rather than building bespoke UI                                                |

---

## Document History

* **v1.2** - Routing note: the gap sweep's non-volumetric ideas were documented in their owning
  reports (RF-2 §6 sky ambience, RF-3 §5 extra post effects, RF-7 §6 lightning sketch, new RF-8
  animated block textures at combined-roadmap #22) — this report deliberately stays
  volumetric/traced-only
* **v1.1** - Second gap sweep added VX-8 (per-fragment voxel lighting from the VX-1 volume —
  the MR-8 smooth-lighting unblock), VX-9 (heat-haze distortion media), VX-10 (interactive
  water ripple sim); recommended order re-waved (v3 gains VX-9/VX-10, VX-8 = v4, tracing = v5)
* **v1.0** - Initial report (VX-0..VX-7, hardware-RT rejection, feasibility verdicts, recommended order)

---

**Last Updated:** 2026-07-20
**Next Review:** when VX-0 or VX-4 starts (re-verify `SettingsManager`/`SettingFieldAttribute` and the lighting-suite baseline count) or on the next RF/CL gap sweep
