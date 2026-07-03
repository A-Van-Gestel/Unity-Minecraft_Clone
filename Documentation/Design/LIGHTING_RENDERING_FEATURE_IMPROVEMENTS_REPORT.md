# Lighting & Rendering Feature Improvements Report

> The master backlog for **lighting and rendering features** in the VoxelEngine — the
> feature-and-design counterpart to [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md),
> which owns lighting/GPU *performance* items (`LI-*`, `GS-*`). Sibling report to
> [`WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md`](WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md) (`TF-*`);
> the **combined ranked roadmap lives at the end of that document**.
>
> Status: **Open backlog.** Items are removed (archived) when implemented and verified.

**Audited:** 2026-07-02, at commit `a458173` (branch `main`).
Findings are from static review of the light engine (`ushort LightData` RGB model, BFS jobs,
`LightWorkScheduler`), the shader stack (`VoxelLighting.hlsl` + the three block shaders +
`UberLiquidShader`), the URP configuration (`Assets/Settings/Rendering/`), and the `World.cs`
lighting/sky driver code. Runtime state was **verified in code, not assumed** — see each item's
"What exists today".

**Relationship to other documents:**

- [`../Architecture/LIGHTING_SYSTEM_OVERVIEW.md`](../Architecture/LIGHTING_SYSTEM_OVERVIEW.md) —
  authoritative BFS lighting doc (dual-phase flood fill, sky-light column model, async job loop,
  §6 lighting-disabled bypass map).
- [`../Architecture/SMOOTH_AND_RGB_LIGHTING.md`](../Architecture/SMOOTH_AND_RGB_LIGHTING.md) — the
  shipped RGB light engine (Phases 1/2/B/3): per-section `ushort` light storage (sky 4b +
  blocklight RGB 3×4b), per-channel BFS, shader-only sky tinting. RF-1 builds directly on its
  `SkyLightColor` design; RF-5's feasibility analysis derives from its storage decisions.
- [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) — cross-linked items:
  `GS-2` (opaque texture), `GS-3` (per-fragment lighting math), `GS-4` (render-tier audit — do
  together with RF-3), `GS-5`/`GS-6` (culling/submission), `LI-1`/`LI-2`.
- [`OM1_DEVICE_CALIBRATION.md`](OM1_DEVICE_CALIBRATION.md) — device-tier budgets; RF-3 (post
  processing) must be quality-tier-gated per its model.
- [`../Architecture/DATA_DRIVEN_SETTINGS_UI.md`](../Architecture/DATA_DRIVEN_SETTINGS_UI.md) —
  where RF-1's day length / RF-3's quality toggles surface as settings.

---

## Legend

| Field       | Values                                                                                                                                                        |
|-------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Effort**  | 🟢 Low (hours, localized) · 🟡 Medium (days, several files) · 🔴 High (architectural, cross-system)                                                           |
| **Risk**    | 🟢 Low (isolated, easy to verify) · 🟡 Medium (touches shared state or visual output) · 🔴 High (touches pipeline invariants, lighting semantics, or shaders) |
| **Benefit** | 🟢 Core — high player-facing value or unlocks other planned work · 🟡 Situational / polish · ⚪ Minor                                                          |
| **Seed**    | ✅ Safe — cannot change generated terrain for a given seed · ⚠️ Terrain-affecting                                                                              |
| **Save**    | ✅ Safe — no on-disk format change · ⚠️ Format — requires a save-format version bump + AOT migration step (see `serialization-migration` skill)                |

> **Benefit redefinition:** as in the `TF-*` report, Benefit here means player-facing / design
> value — **not** the frame-time/GC meaning used in `PERFORMANCE_IMPROVEMENTS_REPORT.md`.

---

## Master summary table

### Lighting & Rendering Features

| ID   | Finding                                                                                   | Effort | Risk | Benefit | Seed | Save |
|------|-------------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| RF-1 | Day/night cycle: shader support is wired & modern but nothing advances time               |   🟡   |  🟢  |   🟢    |  ✅   |  ⚠️  |
| RF-2 | Sky rendering: no skybox (solid clear color), no sun/moon/stars, fog disabled             |   🟡   |  🟢  |   🟢    |  ✅   |  ✅   |
| RF-3 | Bloom / post-processing: URP post stack present but disabled; no HDR emissive path        |   🟡   |  🟡  |   🟡    |  ✅   |  ✅   |
| RF-4 | Flickering light sources: shader-side global flicker with per-position phase              |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |
| RF-5 | Animated light sources: RGB emission already shipped; *animation* is BFS-bounded          |   🟡   |  🟡  |    ⚪    |  ✅   |  ✅   |
| RF-6 | "Some form of GI": SSAO is the pragmatic option; colored sky-bounce rejected with reasons |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |

---

## Detail sections

### RF-1 — Day/night cycle driven by a real time system

**Classification:** Core. Rank #1 in the combined roadmap.

**What exists today (verified — the support is *wired and functional, but static*):**

- `World.globalLightLevel` — a `[Range(0,1)]` inspector field (`World.cs:50-53`), set to `1` in
  `World.prefab`. Companion fields: `Color day`, `Color night`, and a
  `Gradient _skyLightGradient` ("Evaluated at globalLightLevel (0=midnight, 1=noon)",
  `World.cs:55-60`).
- `World.SetGlobalLightValue()` (`World.cs:1363-1370`) pushes three things: the
  `GlobalLightLevel` shader global, `_playerCamera.backgroundColor = lerp(night, day, level)`,
  and `SkyLightColor` from the gradient.
- **It is called exactly twice, ever:** once at world start (`World.cs:587`) and once on save-load
  (`SaveSystem.cs:157`). Nothing advances `globalLightLevel` at runtime — there is no clock, no
  sun position, no time-of-day progression. The `worldState.timeOfDay` save field
  (`SaveDataTypes.cs:63`, written from `world.globalLightLevel` at `SaveSystem.cs:84`) stores a
  *light level*, not a time.
- **The shader chain is modern and already does the right thing** (this part of the task premise
  is stale — it is neither old nor non-functional): `ApplyVoxelLightingRGB`
  (`Assets/Shaders/Includes/VoxelLighting.hlsl:86-102`) modulates **only the per-vertex sky-light
  channel** by `GlobalLightLevel`, tints it by `SkyLightColor`, runs blocklight RGB through the
  same shade curve at full intensity, and combines with per-channel `max()`. All three block
  shaders + the liquid shader consume it. Editor previews hardcode daylight
  (`ChunkPreview3DWindow.cs:350-352`).

**What this means for the design:** the requested "cycle driven by actual light *levels*" is
**already the shipped model** — every voxel's stored 0–15 sky light is what gets scaled, so a
torch-lit room stays bright at midnight while sky-lit terrain darkens (per-channel `max` picks the
blocklight contribution). No BFS or light-storage change is needed or wanted: the missing feature
is purely **time**: a driver that animates `globalLightLevel`/`SkyLightColor`, correct save
semantics, and sky visuals (RF-2).

**Proposed design.**

1. **`WorldTimeManager`** (plain C# class owned by `World`, ticked from `World.Update()` — not a
   MonoBehaviour, matching the manager pattern of `WorldJobManager`):
    - State: `float DayFraction` in `[0,1)` (0 = midnight, 0.5 = noon) + `long ElapsedDays`.
    - Advance: `DayFraction += Time.deltaTime / dayLengthSeconds`, default `dayLengthSeconds = 1200`
      (20-minute days, MC parity). Do **not** couple to `VoxelData.TickLength` (the 1 Hz block
      behavior tick) — visual time must be frame-smooth.
    - Expose `SunElevation` (`= sin((DayFraction − 0.25) * 2π)` — noon at 0.5) for RF-2.
2. **Light curve, designer-owned:** `globalLightLevel = _lightLevelOverDay.Evaluate(DayFraction)`
   — a new `AnimationCurve` on `World` (or a small `TimeOfDaySettings` ScriptableObject, preferred
   so day length + curves + gradients travel together). Author it with a plateau at 1.0 through
   midday, fast falloff at dusk, and a **moonlight floor ≈ 0.25** at night (with the shader's
   `MinLightLevel = 0.15` ambient floor, `VoxelData.cs:11`, full black is already impossible;
   0.25 keeps night readable).
3. **Re-anchor the sky gradient to time:** `_skyLightGradient` is currently evaluated at the light
   *level* (`World.cs:1368`) which collapses dawn and dusk onto the same colors. Evaluate it at
   `DayFraction` instead — then blue-shifted moonlight (night keys), warm sunrise (~0.25), white
   noon (0.5), red-orange dusk (~0.75) are just gradient authoring. Same for the
   `lerp(night, day, ...)` background color → replace with a background gradient over
   `DayFraction` (or derive from RF-2's skybox horizon color).
4. **Blood moon / event tinting:** `SetGlobalLightValue()` gains an event multiplier:
   `SkyLightColor *= _activeSkyEvent?.tint ?? white`, where a `SkyEvent` (blood moon: deep red
   tint + optionally a raised `globalLightLevel` floor) is set by gameplay for the night. Because
   tinting is shader-only (per `SMOOTH_AND_RGB_LIGHTING.md`'s sky-tint decision), a blood moon
   costs zero relighting — this is exactly the payoff of that architecture.
5. **Per-frame update:** call `SetGlobalLightValue()` every frame — it is two `Shader.SetGlobal*`
    + one gradient eval; epsilon-gate if profiling ever cares. Remove the two one-shot call sites'
      uniqueness assumption.
6. **Save semantics (the ⚠️):** redefine `worldState.timeOfDay` as the day fraction and add
   `elapsedDays`. Old saves store a light level (default 1.0) → level.dat-only AOT migration maps
   old value → `0.5` (noon). Precedent: `MigrationV3ToV4WorldTypes` was level.dat-only. Bump
   `SaveSystem.CURRENT_VERSION` 11 → 12 — **coordinate with TF-4's v12 bump if both land close
   together** (one migration step is better than two).
7. **Dev affordances:** a `set time` debug command / DebugScreen readout; settings entry for day
   length (`DATA_DRIVEN_SETTINGS_UI` reflection pattern picks it up from `Settings`).
8. **TF-4 tie-in:** dimensions with `hasSkyLight = false` ignore the time system and use their
   profile's `fixedGlobalLightLevel` (see the `TF-4` lighting-profile design).

**Dependencies / ordering.** None — fully independent. RF-2 consumes its outputs.

**Risks.** 🟢 — no lighting-job, storage, or meshing change; the one invariant to respect is that
`GlobalLightLevel` stays a *sky-channel-only* modulator (never multiply blocklight by it — that
would break the torches-at-night contract that `ApplyVoxelLightingRGB` currently guarantees).
Verify editor preview parity (previews keep hardcoded noon). Save ⚠️ as described.

---

### RF-2 — Sky rendering: procedural skybox, sun/moon, stars, fog sync

**Classification:** Core companion to RF-1 (without it, night is just a darker gray screen).

**What exists today.**

- The camera clears to a **solid color** (`m_ClearFlags: 2`, `Assets/Scenes/World.unity:3614`) —
  there is no skybox material at all; the "sky" is `backgroundColor = lerp(night, day, level)`
  (`World.cs:1366`).
- No sun, no moon, no stars anywhere in the project.
- Fog is disabled (`m_Fog: 0`, `World.unity:17`).
- Clouds exist and are respectable: `Clouds.cs` builds a textured cloud plane at
  `cloudHeight = 100` from a pattern texture (recently modernized — perf item MR-9 ✅).

**Proposed design.**

1. **Procedural gradient skybox** (URP unlit skybox shader, ~40 lines): zenith + horizon colors
   sampled from two designer gradients over `DayFraction` (same `TimeOfDaySettings` asset as
   RF-1); camera `ClearFlags → Skybox`. This replaces the flat background color and gives
   dawn/dusk horizon banding.
2. **Sun + moon:** rendered *in the skybox shader* (cheapest, no scene objects): a disc at
   `+SunDirection` and `−SunDirection` where `_SunDirection` is a global set by
   `WorldTimeManager` from `SunElevation`. Moon gets a phase mask if desired (elapsedDays % 8).
   During a blood moon (RF-1 §4), tint the moon disc from the event.
3. **Stars:** hash-based star field in the same shader (screen-stable via view direction),
   faded in by `saturate(-SunElevation)`. Zero textures needed.
4. **Fog sync:** enable distance fog; `RenderSettings.fogColor` = horizon color each frame
   (extend `SetGlobalLightValue()`), fog end ≈ view distance — this doubles as chunk pop-in
   concealment, a rendering win independent of the cycle. (Per-shader cost of `FOG` variants:
   fold into the `GS-4` render-tier audit.)
5. **Clouds tint:** multiply the cloud material color by `SkyLightColor` so clouds darken/tint
   with time (one `material.SetColor` in `SetGlobalLightValue()`).

**Dependencies / ordering.** RF-1 first (needs `DayFraction`/`SunDirection`). Ships as pure
shader/scene work — no voxel pipeline contact.

**Risks.** 🟢 — isolated new shader + scene settings. Watch the liquid shader's reliance on the
camera opaque texture (`GS-2`) when changing clear flags — verify refraction still samples
correctly with a skybox behind transparents. Seed/Save ✅.

---

### RF-3 — Bloom & post-processing enablement (HDR emissive path)

**Classification:** Polish.

**What exists today.**

- URP is configured with HDR on (`m_SupportsHDR: 1`,
  `Assets/Settings/Rendering/VoxelEngine-URP-Asset.asset`) and the renderer has the default
  `postProcessData` assigned — the post stack is *available*.
- But: the camera has post-processing **off** (`m_RenderPostProcessing: 0`, `World.unity:3694`)
  and no `Volume` component exists in any scene → no bloom, no tonemapping, nothing.
- Emissive-looking blocks (Lava, the DebugLamp01–15 family — `BlockIDs.cs:44-79`) output ≤ 1.0:
  they are *lit* by their own blocklight via the standard shade curve, never HDR-bright. Bloom
  enabled today would only ever catch the sky/sun.

**Proposed design.**

1. **Enable the stack:** camera `renderPostProcessing = true` + a global `Volume` with Bloom
   (threshold ≥ 1.1 so nothing LDR blooms) and — as a *separate, deliberate art decision* —
   Tonemapping (ACES visibly changes every existing color; get user sign-off with A/B captures
   via `Unity_Camera_Capture` before adopting).
2. **HDR emissive path for blocks** (what makes bloom worth it): emitter *faces* need output > 1.
   The meshing job knows the block type per face, so bake an emissive flag/strength per vertex
   and boost in the fragment shader (`finalColor += albedo * emissiveStrength * k`, k ≈ 2–4).
   **Vertex-format constraint:** the MR-2 packed 32-byte layout is the contract —
   `SectionRenderer.Layout` is the single source of truth for vertex streams; any new attribute
   or repurposed bits must be coordinated there (and with the meshing validation suite's B-series
   baselines). Cheapest viable encoding: reuse spare bits in the `Color32` tint stream (tint is
   constant 1.0 for standard blocks today — one channel can carry emissive strength without
   growing the vertex).
3. **Quality gating:** bloom + the post stack cost real GPU time on mobile — gate behind the
   settings/device-tier system (`OM1_DEVICE_CALIBRATION.md` budgets; `DATA_DRIVEN_SETTINGS_UI`
   for the toggle). Desktop default on, mobile default off.
4. **Do together with `GS-4`** (render-pipeline tier audit) — same files, same testing pass; and
   note `GS-2`'s opaque-texture concern interacts with any post passes that need scene color.

**Dependencies / ordering.** Independent; nice after RF-1/RF-2 so night torch-glow bloom lands
with the cycle. The emissive vertex work should ride a meshing-suite-guarded change (MH pattern).

**Risks.** 🟡 — global visual change (tonemapping especially); vertex-layout edits are
regression-prone without the meshing suite baselines; mobile cost. All mitigable, none
architectural. Seed/Save ✅.

---

### RF-4 — Flickering light sources (torch-style)

**Classification:** Polish. Feasibility within the light model: **fully feasible, shader-side.**

**What exists today.**

- No torch block exists — the only emitters are Lava and the DebugLamp test family
  (`BlockIDs.cs`). Block emission is a static per-BlockType RGB (0–15/channel) authored in the
  BlockDatabase (color-picker UI per `SMOOTH_AND_RGB_LIGHTING.md`).
- Light values are baked per vertex at mesh time; shaders have zero time-based variation.

**Analysis — where flicker can live:**

- *CPU/BFS re-flood per flicker tick:* **rejected.** Each emission change re-runs darkness
  removal + re-spread over a ~15-radius volume and re-meshes affected sections; N torches
  flickering at a few Hz would saturate `LightWorkScheduler` with pure cosmetics. This is the
  anti-pattern the architecture constraints exist to prevent.
- *Shader-side modulation:* **correct home.** The blocklight contribution is already isolated in
  `ApplyVoxelLightingRGB` (`VoxelLighting.hlsl:95-99`) — scale it by a time-varying factor and
  every torch-lit surface breathes, at zero CPU/lighting cost.

**Proposed design.**

1. Global uniform `_BlockLightFlicker` set each frame by `World` (piggyback on
   `SetGlobalLightValue()`): a smooth pseudo-noise in `[0.92, 1.0]` (sum of two incommensurate
   sines is fine; keep amplitude subtle).
2. **Per-position phase (the trick that sells it):** in the shader, offset the flicker phase by a
   hash of the vertex's world position band —
   `flicker = f(t + hash(floor(worldPos.xz / 8)) * 2π)` — so different rooms/areas flicker
   out of sync and it reads as per-source, without any per-source data. Pure ALU, no textures.
   (Fragment world position already exists in the block shaders for `GS-3`-related math.)
3. Gate by a small uniform so it can be disabled (settings toggle; also keeps editor previews
   deterministic — pass 1.0 like `ChunkPreview3DWindow` does for `GlobalLightLevel`).
4. **Prerequisite content:** an actual Torch block (custom mesh + emission) — authored via the
   in-editor `BlockEditor` → `BlockDatabase.asset` → regenerate `BlockIDs.cs` (per CLAUDE.md
   block-workflow rules; do not hand-author IDs). The flicker feature itself is block-agnostic —
   it animates *all* blocklight.

**Caveat to state honestly:** the flicker is a *global* modulation of received blocklight —
overlapping light from two sources flickers as one field, and sky light is untouched. This is the
same simplification Minecraft's light-texture flicker makes; nobody notices.

**Dependencies / ordering.** None. 🟢 across the board; Seed/Save ✅.

---

### RF-5 — Animated / RGB light sources

**Classification:** Minor / nice-to-have — with an explicit architectural ceiling.

**What exists today.** **RGB light sources are already shipped and proven** — per-channel 4-bit
RGB blocklight storage, independent per-channel BFS, color-picker emission authoring, and the
DebugLamp12–15 (Green/Blue/Red/White) blocks exercising it (`SMOOTH_AND_RGB_LIGHTING.md` Phases
2/B; `LIGHTING_SYSTEM_OVERVIEW.md` §2.1). The open half of this item is only **animated** (time-
varying) emission — e.g. color-cycling lamps, pulsing beacons.

**Analysis.** Emission is a per-BlockType static; changing a voxel's light means a real lighting
update (remove + re-spread BFS, then re-mesh) — the same cost as placing/removing a torch. Three
approaches:

| Approach                                                                      | Verdict                                                                                                                                     |
|-------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------|
| Shader-side hue-cycling of blocklight                                         | ❌ Impossible for per-source animation — per-vertex `blockRGB` is the *mixed* result of all sources; a shader shift recolors everything      |
| Per-voxel emission metadata (via `PER_BLOCK_METADATA_SCHEMAS.md` schema bits) | Viable for *variants* (lamp brightness/color set at placement) but still needs a BFS pass per change — doesn't make animation cheaper       |
| **Block-state swap driven by the behavior tick** (recommended)                | ✅ A behavior (TG-4 tick system) swaps between pre-authored block variants (e.g. `DebugLamp12` → `13`); each swap is one normal light update |

**Proposed design (budgeted block-swap animation).**

1. Author animated lamps as N block variants (existing BlockDatabase workflow; the DebugLamp
   family is literally already this).
2. A `BlockBehavior` (TG-4 data-separated pattern) advances the variant on a slow schedule
   (≥ 1–2 s per step) — each step goes through the normal `SetVoxel` → light-queue path, which
   `LightWorkScheduler` (MT-2 ready/waiting split) already absorbs.
3. **Hard budget:** cap light-changing behavior events per tick (suggested: 8/tick globally,
   drop-oldest) so a player building a disco floor degrades to slow animation instead of
   saturating the lighting queue. Surface the counter in the perf HUD (`DT-*` stack) during
   tuning.
4. Combine with RF-4's shader flicker for "animated-feeling" light at zero BFS cost — in most
   cases that is the better tool, and it should be tried first for any given effect.

**Dependencies / ordering.** After RF-4 (which covers most of the visual demand cheaply). Uses
TG-4 behavior infrastructure (shipped).

**Risks.** 🟡 — lighting-queue pressure is the only real one; the budget cap is the mitigation.
Storage/serialization untouched (light data + queues already serialize per v8/v9 formats).
Seed/Save ✅.

---

### RF-6 — "Some form of GI"

**Classification:** Polish. Recommendation: **SSAO, and stop there** (for now).

**What exists today.**

- The BFS light engine *is* a coarse diffuse-GI approximation: light floods around corners with
  distance falloff, in RGB, per voxel — most of what players read as "GI" in voxel games.
- Smooth per-vertex lighting (Phase 1) already provides AO-style corner darkening
  (vertex-averaged light values — `SMOOTH_AND_RGB_LIGHTING.md` §Phase 1).
- Flat ambient floor `MinLightLevel = 0.15`; no SSAO; no realtime shadow maps (the render-tier
  state is `GS-4`'s subject).

**Options evaluated.**

| Option                                           | Verdict                                                                                                                                                                                                                                                                                                             |
|--------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **URP SSAO renderer feature** (recommended)      | ✅ Drop-in, no pipeline changes; adds fine contact occlusion the 16³-granular vertex AO can't express; ~0.5–1 ms @1080p desktop → quality-tier-gate it (OM-1). Verify interaction with vertex AO (double-darkening — tune intensity ≤0.5)                                                                            |
| Colored sky-bounce ("red carpet tints the room") | ❌ Rejected: requires albedo-aware re-injection seeds in the BFS **and** RGB sky light — sky is 4-bit mono by design; widening `LightData` `ushort`→`uint` doubles light memory + save format bump + touches every lighting job. Not worth it for a subtle effect; revisit only alongside a future lighting overhaul |
| Realtime directional sun shadows (shadow maps)   | ❌ Rejected: per-voxel sky light already encodes sun occlusion (that's what the BFS computes); shadow maps would double-darken every overhang, cost heavily at voxel draw-call counts (pre-`GS-6`), and fight the art style                                                                                          |
| Light probes / RTGI / APV                        | ❌ Rejected: dynamic destructible voxel world + Mono/IL2CPP mobile targets; wrong tool class                                                                                                                                                                                                                         |

**Proposed design (SSAO).** Add the URP Screen Space Ambient Occlusion renderer feature to
`VoxelEngine-URP-Renderer.asset` (depth-normals mode; the block shaders are standard URP-lit-style
enough — verify normals output post-MR-2's `SNorm8x4` packed normals), intensity ~0.4, radius
tuned to ~0.5–1 block. Quality-tier gate (off on mobile). Do in the same pass as `GS-4` and RF-3
(same asset, same A/B capture workflow).

**Dependencies / ordering.** None hard; pairs with RF-3/`GS-4`.

**Risks.** 🟢 — additive renderer feature; the only visual risk is stacking with vertex AO
(tune, capture, sign off). Seed/Save ✅.

---

## Roadmap

See the **combined ranked roadmap** at the end of
[`WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md`](WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md) — RF items
rank: RF-1 (#1), RF-2 (#3), RF-4 (#12), RF-3 (#13), RF-6 (#14), RF-5 (#15).
