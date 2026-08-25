# Lighting & Rendering Feature Improvements Report

**Version:** 2.5  
**Date:** 2026-08-25  
**Status:** **Open backlog.** Items are removed (archived) when implemented and verified. Owns lighting
and rendering *features* (`RF-*`); the *performance* counterparts (`LI-*`, `GS-*`) live in
[`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md), and the combined ranked
roadmap lives at the end of the sibling worldgen report.  
**Target:** Unity 6.5 (Mono for dev; IL2CPP for production)

> The master backlog for **lighting and rendering features** in the VoxelEngine — the
> feature-and-design counterpart to [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md),
> which owns lighting/GPU *performance* items (`LI-*`, `GS-*`). Sibling report to
> [`WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md`](WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md) (`TF-*`);
> the **combined ranked roadmap lives at the end of that document**.
>
> Status: **Open backlog.** Items are removed (archived) when implemented and verified.

**Audited:** 2026-07-02, at commit `a458173` (branch `main`).  
**Amended:** 2026-08-25 — **RF-10 filed**: the skylight tint gradient ships flat white, so RF-1's
tinting mechanism is built, shipped and confirmed working but never authored. Content only, no code.  
**Amended:** 2026-07-03 — second gap sweep added RF-7 (weather), alongside TF-10..TF-14 in the
sibling worldgen report.  
**Amended:** 2026-07-03 — RF-1 extended with the effective-light query layer + subtractive shader
parity (§9–§10, `SkyDarken` model): stored skylight is time-invariant *sky exposure*; gameplay
reads derived effective light, never raw storage. Second pass: §3 gained the blue-moonlight
authoring rules (global sky tint is exact, brightness-in-curve/color-in-gradient split) and §4's
event tint changed from multiply to lerp/replace.  
**Amended:** 2026-07-19 — cross-linked the new `CLOUD_RENDERING_IMPROVEMENTS_REPORT.md` (`CL-*`):
CL-2 absorbs RF-2 §5 (clouds tint); RF-7 §4's cloud knobs are received by CL-4.  
**Amended:** 2026-07-19 (later) — cross-linked the new `FOLIAGE_LIVELINESS_IMPROVEMENTS_REPORT.md`
(`FL-*`): FL-1/FL-2 foliage sway reads the shared wind vector RF-7 will own; the RF-3 §2 vertex-
channel allocation (`Color32` = TF-11 RGB + RF-3 emissive) is complemented by FL's claim on the
spare `uv.zw` half2; RF-1 gates FL-6's fireflies.  
**Amended:** 2026-07-20 — cross-linked the new
`VOLUMETRIC_AND_RAYTRACED_EFFECTS_REPORT.md` (`VX-*`): RF-6's "revisit only alongside a future
lighting overhaul" deferral now has a home (VX-6, experimental-tier voxel-traced GI); RF-2 §4's
distance fog stays the default-tier fog under VX-2's experimental volumetric fog.  
**Amended:** 2026-07-20 (later) — the VX gap sweep's non-volumetric ideas were routed here:
RF-2 gained §6 (sky ambience content v2 — aurora, shooting stars, sun flare), RF-3 gained §5
(vignette/DoF/motion-blur overrides), RF-7 gained §6 (lightning v2 sketch), and **RF-8 (animated
block textures via atlas blitting) was added** as a new item (#22 in the combined roadmap).
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
  `SkylightColor` design; RF-5's feasibility analysis derives from its storage decisions.
- [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) — cross-linked items:
  `GS-2` (opaque texture), `GS-3` ⏸️ (per-fragment lighting math — analyzed and **deferred**
  2026-08-15: the vertex-stage move is irreducibly non-neutral, so it is not the free win it reads
  as), `GS-4` ✅ (render-tier audit — shipped 2026-08-15, after RF-3), `GS-5`/`GS-6`
  (culling/submission), `GS-7` (cloud shader uniform hoist — exact, split out of GS-3's analysis),
  `LI-1`/`LI-2`.
- [`OM1_DEVICE_CALIBRATION.md`](OM1_DEVICE_CALIBRATION.md) — device-tier budgets; RF-3 (post
  processing) must be quality-tier-gated per its model.
- [`../Architecture/DATA_DRIVEN_SETTINGS_UI.md`](../Architecture/DATA_DRIVEN_SETTINGS_UI.md) —
  where RF-1's day length / RF-3's quality toggles surface as settings.
- [`CLOUD_RENDERING_IMPROVEMENTS_REPORT.md`](CLOUD_RENDERING_IMPROVEMENTS_REPORT.md) (`CL-*`) —
  cloud-layer liveliness backlog: **CL-2 absorbed RF-2 §5** (clouds tint — shipped 2026-07-19),
  and RF-7 §4's cloud color/density storm knobs are received by CL-4 there.
- [`VOLUMETRIC_AND_RAYTRACED_EFFECTS_REPORT.md`](VOLUMETRIC_AND_RAYTRACED_EFFECTS_REPORT.md)
  (`VX-*`) — experimental-tier volumetrics + software voxel tracing: VX-6 is the gated home for
  the GI class RF-6 rejected at default tier (RF-6's SSAO verdict stands); VX-2 (volumetric fog)
  layers above RF-2 §4's default distance fog and consumes RF-1's `SunElevation`/`SkyDarken`.

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
| RF-1 | ~~Day/night cycle: shader support is wired & modern but nothing advances time~~ ✅ **SHIPPED** (both phases) |   🟡   |  🟢  |   🟢    |  ✅   |  ⚠️  |
| RF-2 | ~~Sky rendering: skybox, sun/moon, stars, fog, disc detail~~ ✅ **SHIPPED**; §6 + 4 riders open |   🟡   |  🟢  |   🟢    |  ✅   |  ✅   |
| RF-3 | ~~Bloom / post-processing: URP post stack present but disabled; no HDR emissive path~~ ✅ **SHIPPED**; §1 tonemapping + §5 effects open |   🟡   |  🟡  |   🟡    |  ✅   |  ✅   |
| RF-4 | Flickering light sources: shader-side global flicker with per-position phase              |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |
| RF-5 | Animated light sources: RGB emission already shipped; *animation* is BFS-bounded          |   🟡   |  🟡  |    ⚪    |  ✅   |  ✅   |
| RF-6 | "Some form of GI": SSAO is the pragmatic option; colored sky-bounce rejected with reasons |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |
| RF-7 | Weather: no rain/snow of any kind; precipitation type gated on TF-3's temperature axis    |   🟡   |  🟡  |   🟡    |  ✅   |  ✅   |
| RF-8 | Animated block textures: every non-fluid tile is static — flipbook via atlas blitting     |   🟡   |  🟢  |   🟡    |  ✅   |  ✅   |
| RF-9 | Vertex AO crushes to black at night — occlusion is baked in before the sky darkening       |   🟡   |  🟡  |   🟡    |  ✅   |  ✅   |
| RF-10 | The skylight tint gradient ships flat white — RF-1's tinting mechanism is built but unauthored |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |

---

## Detail sections

### RF-1 — Day/night cycle driven by a real time system

**Classification:** Core. Rank #1 in the combined roadmap.

> **Phase 1 and Phase 2 (§9 + §10) both SHIPPED + confirmed in game 2026-08-10.** The shader half cannot
> be observed by any validation suite, so it stays capture-verified only. That confirmation is what filed
> RF-9, which remains open.
>
> **Phase 2 — what changed:**
>
> - **§9 effective-light query.** `LightBitMapping.GetEffectiveSkylight/GetEffectiveLight(lightData,
>   skyDarken)` — Burst-safe integer math, `skyDarken` passable as job data — with `World.GetEffectiveSkylight/
>   GetEffectiveLight(voxelPos)` wrappers over `World.CurrentSkyDarken`. No BFS, remesh, or save impact.
> - **§10 subtractive shader parity.** `ApplySkyDarken` in `VoxelLighting.hlsl`:
>   `max(skyExposure − (1 − GlobalLightLevel), 0)`, then the shade curve at full intensity. All five
>   terrain/liquid consumers route through `ApplyVoxelLightingRGB`, so no call site changed.
> - **Clouds rewired** (`CloudShader.shader`): the day/night term moved from the curve's `globalLight`
>   slot into the exposure slot. Under the subtractive model those are no longer the same number
>   (`0.85·g + 0.15` vs `g`), so leaving it would have left clouds brighter than the terrain beneath.
> - **Editor preview parity holds with no change** — both preview shaders and `ChunkPreview3DWindow`
>   pin `globalLight = 1.0`, where `ApplySkyDarken` reduces to the identity. Verified by reading the
>   call sites, not assumed. **Noon is bit-identical to the pre-RF-1 render**; the visible delta is night.
> - `DebugScreen` shows raw sky exposure *and* effective light, plus the clock and its darken.
>
> **Phase 1 — what shipped:**
>
> - `WorldTimeManager` (`Assets/Scripts/WorldTimeManager.cs`) — a plain manager owned by `World`, ticked
>   from `World.Update()`. Time is a `long` tick count (24000/day, tick 0 = sunrise, Minecraft parity) with
>   a bounded sub-tick residue, so elapsed time never drifts.
> - `TimeOfDaySettings` ScriptableObject, linked from `WorldTypeDefinition` (so a future dimension ships its
>   own sky) with a `World` fallback. The curve outputs **sky darken 0–11**, not brightness — one sample
>   feeds both `GlobalLightLevel` and the §9 `SkyDarken`, which is what makes them unable to disagree.
> - **`GlobalLightLevel` is reused, not replaced**, redefined as `1 − skyDarken/15`. Its range narrows to
>   `[0.27, 1]`: under the moonlight floor a fully sky-exposed voxel never drops below effective 4.
> - `/time` regrammared to `set day|noon|sunset|night|midnight|<ticks>` + `add`/`freeze`/`resume`/query.
> - Save **v14 → v15**: `worldState.time {ticks, frozen}` replaces `worldState.timeOfDay`; `environment`
>   re-parented under `worldState`. See the AOT doc for the one authorized revision of the shipped v13→v14 step.
> - New **World Clock validation suite** (8 baselines, prove-red verified), `Validate All` 19 suites / 452 baselines.
>
> The clock holds while the pause menu is open, which makes it the project's *only* pause-aware
> system — nothing writes `Time.timeScale`, so chunk streaming and fluids run on behind the menu.
> That inconsistency is scoped in
> [`PAUSE_AND_SIMULATION_HALT.md`](PAUSE_AND_SIMULATION_HALT.md) (`PA-*`), filed from this work.
>
> Deliberately **not** shipped in Phase 1: §4's `SkyEvent` tint (no gameplay system produces events yet —
> the lerp seam is left open), §7's day-length *user setting* (it is world/art state and lives on the asset),
> §8's TF-4 tie-in (`hasSkylight` does not exist), and the blue-moonlight night keys — the tint gradient
> ships flat white, exactly as the retired `_skyLightGradient` was, so Phase 1's only visual delta is brightness.

**What existed before Phase 1 (verified — the support was *wired and functional, but static*):**

- `World.globalLightLevel` — a `[Range(0,1)]` inspector field (`World.cs:50-53`), set to `1` in
  `World.prefab`. Companion fields: `Color day`, `Color night`, and a
  `Gradient _skyLightGradient` ("Evaluated at globalLightLevel (0=midnight, 1=noon)",
  `World.cs:55-60`).
- `World.SetGlobalLightValue()` (`World.cs:1363-1370`) pushes three things: the
  `GlobalLightLevel` shader global, `_playerCamera.backgroundColor = lerp(night, day, level)`,
  and `SkylightColor` from the gradient.
- **It is called exactly twice, ever:** once at world start (`World.cs:587`) and once on save-load
  (`SaveSystem.cs:157`). Nothing advances `globalLightLevel` at runtime — there is no clock, no
  sun position, no time-of-day progression. The `worldState.timeOfDay` save field
  (`SaveDataTypes.cs:63`, written from `world.globalLightLevel` at `SaveSystem.cs:84`) stores a
  *light level*, not a time.
- **The shader chain is modern and already does the right thing** (this part of the task premise
  is stale — it is neither old nor non-functional): `ApplyVoxelLightingRGB`
  (`Assets/Shaders/Includes/VoxelLighting.hlsl:86-102`) modulates **only the per-vertex sky-light
  channel** by `GlobalLightLevel`, tints it by `SkylightColor`, runs blocklight RGB through the
  same shade curve at full intensity, and combines with per-channel `max()`. All three block
  shaders + the liquid shader consume it. Editor previews hardcode daylight
  (`ChunkPreview3DWindow.cs:350-352`).

**What this means for the design:** the requested "cycle driven by actual light *levels*" is
**already the shipped model** — every voxel's stored 0–15 sky light is what gets scaled, so a
torch-lit room stays bright at midnight while sky-lit terrain darkens (per-channel `max` picks the
blocklight contribution). No BFS or light-storage change is needed or wanted: the missing feature
is purely **time**: a driver that animates `globalLightLevel`/`SkylightColor`, correct save
semantics, and sky visuals (RF-2).

**Storage semantics (important):** the stored 0–15 sky-light value is hereby defined as
**sky exposure** — a *time-invariant structural property* of the terrain, computed once by the
BFS and never mutated for time of day. At night a fully sky-exposed voxel still *stores* 15;
darkening is applied at read time (shader: §10; gameplay: §9). Gameplay systems (mob spawning,
plant growth, etc.) must therefore **never read raw skylight** for time-dependent decisions —
they read the §9 effective-light query. Two storage-mutating alternatives were evaluated and
rejected:

- *Full skylight re-BFS at source `15 − N`:* dusk crosses ~15 discrete levels; each step is a
  full-world removal + re-propagation pass (removal is the expensive direction) that dirties
  every sky-lit section → repeated full-world remesh, twice a day. It is also semantically wrong:
  sky columns propagate downward without attenuation only at level 15, so a re-flood at 14
  disables the column rule and ravine bottoms decay toward black with depth. Saved light would
  additionally depend on wall-clock time at save.
- *In-place subtraction written back to storage:* `max(x − N, 0)` is not invertible — every voxel
  clamped to 0 at night (originals `1..N`) cannot be restored at dawn without keeping the
  original anyway; every write still dirties sections and forces remeshes.

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
   midday, fast falloff at dusk, and a **moonlight floor of effective sky light 4 — Minecraft
   parity** (see §9: `SkyDarken` caps at 11, so `15 − 11 = 4`; visually `4/15 ≈ 0.27`, and with
   the shader's `MinLightLevel = 0.15` ambient floor, `VoxelData.cs:11`, full black is already
   impossible). Matching MC's floor keeps its well-tested gameplay thresholds (e.g. hostile
   spawns at light ≤ 7 vs. moonlight 4) directly reusable.
3. **Re-anchor the sky gradient to time:** `_skyLightGradient` is currently evaluated at the light
   *level* (`World.cs:1368`) which collapses dawn and dusk onto the same colors. Evaluate it at
   `DayFraction` instead — then blue-shifted moonlight (night keys), warm sunrise (~0.25), white
   noon (0.5), red-orange dusk (~0.75) are just gradient authoring. Same for the
   `lerp(night, day, ...)` background color → replace with a background gradient over
   `DayFraction` (or derive from RF-2's skybox horizon color).

   **Blue moonlight — authoring rules (pure content, no code):** the night keys carry a
   desaturated Purkinje-style blue (≈ `RGB(0.65, 0.75, 1.0)`), and this is the architecturally
   *correct* mechanism, not a shortcut:
    - *Global tint is exact, not an approximation:* moonlight color is uniform across all sky
      sources, so tinting the sky channel via `SkylightColor` produces the identical result that
      per-voxel RGB skylight storage would — at zero storage/BFS cost (RGB skylight was already
      rejected in `SMOOTH_AND_RGB_LIGHTING.md`: 4b→12b, 3× sky BFS). Per-voxel data only ever
      needs *intensity* (the stored exposure).
    - *Torches stay warm and caves stay neutral for free:* the tint multiplies only the sky
      contribution before the per-channel `max()` in `ApplyVoxelLightingRGB`
      (`VoxelLighting.hlsl:86-102`), so torch-lit interiors take the torch's R/G channels, and at
      sky exposure 0 the untinted blocklight ambient floor wins the `max()` — no special-casing.
    - *Brightness lives in the curve, color lives in the gradient:* author night keys with
      **B held at 1.0 and only R/G reduced** — never scale all three channels down, which would
      double-dip with the §2 brightness curve and push the moonlight floor (effective 4) below
      readable. Tint applies after the §10 subtractive shade, so it recolors but never re-darkens
      the effective level.
    - *RF-2 coordination:* author the night background/fog color in the same blue family so the
      horizon doesn't clash with the terrain tint.
4. **Blood moon / event tinting:** `SetGlobalLightValue()` gains an event **override, not a
   multiplier**: `SkylightColor = lerp(gradientColor, _activeSkyEvent.tint, _activeSkyEvent.weight)`
   (identity when no event is active). A multiply (`SkylightColor *= tint`) would compose with
   §3's blue moonlight — red × blue = muddy purple instead of blood red — so the event tint must
   replace/lerp over the gradient output. The `SkyEvent` (blood moon: deep red tint + optionally
   a raised `globalLightLevel` floor) is set by gameplay for the night, and `weight` gives a
   smooth fade-in for free. Because tinting is shader-only (per `SMOOTH_AND_RGB_LIGHTING.md`'s
   sky-tint decision), a blood moon costs zero relighting — this is exactly the payoff of that
   architecture.
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
8. **TF-4 tie-in:** dimensions with `hasSkylight = false` ignore the time system and use their
   profile's `fixedGlobalLightLevel` (see the `TF-4` lighting-profile design).
9. **Effective-light query layer (gameplay reads — required):** `WorldTimeManager` exposes
   `int SkyDarken` in `[0, 11]` (**Minecraft parity**: 0 at day, 11 at deepest night → moonlight
   floor `15 − 11 = 4`), derived from the *same* curve that drives `globalLightLevel` — one
   source of truth, so rendering and gameplay can never disagree about how dark it is. Query
   helpers (next to `LightBitMapping`, or on `World`):
    - `GetEffectiveSkylight(pos) = max(0, storedSkylight − SkyDarken)`
    - `GetEffectiveLight(pos) = max(effectiveSkylight, maxRGBBlocklightChannel)` — the value all
      time-sensitive gameplay (mob spawning, growth, …) consumes.

   Pure integer math on the existing `ushort` — zero relighting, zero remeshing, no save impact,
   Burst-safe (pass `SkyDarken` in as job data if a job ever needs it). The subtraction is also
   exactly MC's `skyDarken` model, so its gameplay rules transfer verbatim. `DebugScreen` (which
   reads raw skylight at `DebugScreen.cs:585`) should display both values: raw ("exposure") and
   effective.
10. **Shader parity (subtractive — required, not optional):** switch the sky term in
    `ApplyVoxelLightingRGB` from multiplicative (`sky × GlobalLightLevel`) to subtractive
    (`max(sky − SkyDarken/15, 0)` on the normalized channel) so **a voxel that looks like level 4
    *is* effective level 4** — visual darkness and the §9 gameplay value agree exactly at every
    time of day. The `GlobalLightLevel` shader global then carries the normalized `SkyDarken`
    (or is replaced by a `SkyDarken` global); `globalLightLevel` remains the C#-side curve
    output that both derive from. Shader-only change; the sky-channel-only invariant (see Risks)
    is unchanged.

**Dependencies / ordering.** None — fully independent. RF-2 consumes its outputs.

**Risks.** 🟢 — no lighting-job, storage, or meshing change; two invariants to respect:
(1) the time-of-day darkening stays a *sky-channel-only* modulator (never apply `SkyDarken` /
`GlobalLightLevel` to blocklight — that would break the torches-at-night contract that
`ApplyVoxelLightingRGB` currently guarantees); (2) gameplay code must never read raw stored
skylight for time-dependent logic — always the §9 effective-light query (raw skylight = sky
*exposure*, permanently 15 under open sky). Verify editor preview parity (previews keep
hardcoded noon, i.e. `SkyDarken = 0`). Save ⚠️ as described.

---

### RF-2 — Sky rendering: procedural skybox, sun/moon, stars, fog sync

**Classification:** Core companion to RF-1. **§1–§5 SHIPPED 2026-08-11** — see
[`../Architecture/SKY_AND_CELESTIAL_RENDERING.md`](../Architecture/SKY_AND_CELESTIAL_RENDERING.md),
which is now authoritative for the sky. Only §6 and the deferred items below remain open.

> **What shipped** (commits `4a6fa38d` → `c471766b`, all in-game confirmed):
>
> - **Procedural skybox** with zenith/horizon gradients over `DayFraction`, authored on the RF-1
>   `TimeOfDaySettings` asset. Camera clear flags and `RenderSettings.skybox` are set **from code** and
>   restored on teardown, so no scene asset is edited.
> - **A real celestial simulation** (`Sky/CelestialMath.cs`) rather than the `±SunDirection` sketch
>   below: an equinox model parameterized by observer latitude, so the sun rises due east, sets due
>   west, and peaks at `90° − |φ|` to the south. The moon rides the same arc at a lagged hour angle,
>   with position **and** phase derived from one elongation — full-moon-at-midnight falls out rather
>   than being authored. A phase epoch puts a full moon on the world's first night (MC parity).
> - **Stars** as hash-placed points sampled in celestial space, so the field rotates with the sky.
> - **Distance fog** (§4) using the engine's **own** globals, not `RenderSettings.fog` — no
>   `multi_compile_fog`, hence **no shader variants** (the `GS-4` concern §4 raised is dissolved, not
>   deferred), no scene state, and a zero-width range as a natural "off". Horizontal (XZ) distance and
>   a back-loaded curve; a `Distance Fog` setting offers Off / Light / Full.
> - **15-baseline `Validate Sky` suite.** Note the standing limit: it guards the *model*; the shader
>   half is capture-verified only.
>
> **Corrections to the pre-implementation notes below** (they were written 2026-07-02 and were stale):
> a skybox material *was* assigned in `RenderSettings` (Unity's built-in default) — only the camera's
> clear flags stopped it being seen; **no shader in the project supported fog at all**, so §4 was a
> five-shader edit rather than enabling a checkbox; `SunElevation` was a flat, latitude-free sine that
> the celestial model supersedes rather than builds on; §2's blood-moon tint has no hook, because
> RF-1 §4's `SkyEvent` was deliberately never shipped; and the line references had drifted
> (clear flags `World.unity:3634`, background colour `World.cs:1964`).

**What existed before implementation (verified 2026-07-02).**

- The camera cleared to a **solid color** (`m_ClearFlags: 2`) — the "sky" was
  `backgroundColor = lerp(night, day, level)`.
- No sun, no moon, no stars anywhere in the project.
- Fog was disabled (`m_Fog: 0`, `World.unity:17`) *and unsupported by every shader*.
- Clouds exist and are respectable: `Clouds.cs` builds a textured cloud plane at
  `cloudHeight = 100` from a pattern texture (recently modernized — perf item MR-9 ✅).

**Proposed design.** §1–§4 are shipped; the as-built system differs from these sketches where noted
above, and the Architecture doc is authoritative.

1. ✅ **Procedural gradient skybox** — shipped.
2. ✅ **Sun + moon** in the skybox shader — shipped, on a real celestial model.
3. ✅ **Stars** — shipped, celestial-space hash points.
4. ✅ **Fog sync** — shipped, as engine-owned fog rather than `RenderSettings.fog`.
5. **Clouds tint:** ✅ **SHIPPED 2026-07-19** via CL-2 in
   [`CLOUD_RENDERING_IMPROVEMENTS_REPORT.md`](CLOUD_RENDERING_IMPROVEMENTS_REPORT.md) — the
   cloud shader samples the `SkylightColor` global directly (no `material.SetColor` needed);
   already responsive to `/time`, and upgrades further when RF-1's cycle drives the gradient.
   *Clouds are deliberately **not** fogged: at the default view distance the fog completes well inside
   the cloud plane's extent, so fogging it would erase the clouds. Terrain is what pops in.*
6. **Sky ambience content (v2 — routed here from the VX-* gap sweep, 2026-07-20):** pure
   content additions to the §1–§3 skybox shader, explicitly *after* the core cycle ships:
    - **Aurora:** a night-only scrolling noise ribbon in a horizon-zenith band (two octaves of
      the CL-3 hash-noise family over view direction + time), green-teal gradient, faded by
      `saturate(-SunElevation)` like the stars; gated rare (elapsedDays hash) or by a
      `SkyEvent`, and later by climate (cold biomes) when TF-3 ships.
    - **Shooting stars / meteors:** an occasional seconds-scale streak — hash-seeded start
      direction + time window in the §3 star-field code; zero state, pure shader function of
      (elapsedDays, DayFraction).
    - **Sun lens flare:** ~~cheapest viable = let RF-3's bloom catch the HDR sun disc (free once
      both ship)~~ — **this estimate was wrong and is superseded by
      [`SUN_APPEARANCE_IMPROVEMENTS.md`](SUN_APPEARANCE_IMPROVEMENTS.md)**. Both shipped and it is
      not free: nothing in the sun path was ever made HDR, so the disc's output ceiling is exactly
      1.0 against an effective linear bloom threshold of ≈1.23 (that doc's §2.1 has the
      arithmetic). The sprite-chain rejection below still stands, but URP's *screen-space* lens
      flare escapes it — it reads rendered pixels, so occlusion is inherent.
      A classic sprite-chain flare remains deliberately *not* proposed (occlusion
      queries against voxel depth for a stylistic mismatch — skip).

**Still open (the RF-2 remainder).** Each is deliberately deferred, not forgotten:

| Item | Note |
|------|------|
| §6 sky ambience v2 (aurora, shooting stars) | Pure content on the shipped skybox shader. |
| Sun appearance (aureole, sunset reddening) | **Own design: [`SUN_APPEARANCE_IMPROVEMENTS.md`](SUN_APPEARANCE_IMPROVEMENTS.md)**. `SN-0` (aureole) and `SN-1` (per-channel extinction) **shipped and confirmed in game 2026-08-15**. `SN-2` (HDR core for bloom) was **built and refuted** — reverted in full — and `SN-3` (screen-space lens flare) falls with it, because URP's one global `Bloom` sizes its halo for RF-3's block emitters and the sun wants a different answer from the same setting (that doc's §7.3). **This retires the RF-2 §6 sun-flare bullet entirely**: the answer is not a flare. Everything stays in **LDR**; the Neutral-tonemapping upgrade remains out of scope and owed its own doc. |
| Per-biome sky color override | The editor-tool half **shipped 2026-08-12** as `Minecraft Clone/Sky Editor` (Architecture §6); only the per-biome override remains. It **needs a design pass first**: sky color is screen-wide but biomes are per-column, so something must define the boundary rule (blend over distance? sample at the camera? weight nearby columns?). Same class of question as TF-3's climate axis. Route to `create-design-doc`, not to implementation. |
| Seasonal declination | Blocked on RF-1's curve coupling — see the Architecture doc §2.1 for why zero is load-bearing rather than lazy. |
| Blood-moon disc tint | Waits on RF-1 §4's `SkyEvent`, which was never shipped. |

**Dependencies / ordering.** RF-1 first (needs `DayFraction`) — satisfied. Shipped as pure
shader/scene work with no voxel pipeline contact.

**Risks.** 🟢 — borne out. The liquid shader's opaque-texture refraction (`GS-2`) was the one
watchpoint under a changed clear flag; verified in game and accepted (stars and the sky gradient refract
through water, which reads correctly). Seed/Save ✅.

**Interaction with RF-9 (important).** RF-2's properly dark night makes RF-9 **more** visible, not less.
Measured 2026-08-11 at midnight: a fully sky-exposed flat surface renders at brightness 0.0932 while a
30%-occluded face renders 0.0063 — **14.8× darker, and identical to 50% occlusion** (both clamped to the
floor, so all shape information is lost). At noon the same pair differs by only 1.5×. Water looked
"too blue" in game; water was correct and the terrain was crushed. Fog masks this at distance only.

---

### RF-3 — Bloom & post-processing enablement (HDR emissive path)

**Classification:** Polish. **§1 (stack) + §2 (HDR emissive) + §3 (gating) SHIPPED 2026-08-12**, confirmed in
game. Tonemapping (§1's second half) and the §5 effects remain open, each still its own art decision.

> **As built.** Four commits on `feat/world-scaling`: `b981ec44` (emissive path, default-inert),
> `3b246bc2` (alpha-sentinel fix), `c1748d15` (liquid emissive read), `95bae9a0` (bloom + setting).
>
> - **Channel:** emissive strength lives in **`Color32.a`** of the mesh colour stream — the only channel
>   free on all three submeshes. Block emission (0-15) is scaled ×17 and stamped by
>   `MeshGenerationJob.StampEmissiveStrength`, a single pass at the shape router, so standard cubes,
>   custom meshes, cross meshes and fluids are all covered by one code path. RGB is untouched and
>   remains TF-11's; see the allocation registry note below.
> - **Shader:** `ApplyVoxelEmissive` in `VoxelLighting.hlsl`, applied after lighting and **before fog**
>   (so distant emitters fade into haze rather than glowing through it), in the standard, transparent and
>   liquid shaders. Driven by the global `_EmissiveBoost`.
> - **Post stack:** a global `Volume` (priority 1) in `World.unity` → `VoxelEngine-Post-Profile.asset`
>   with Bloom (threshold **1.1**, intensity **0.25**, scatter 0.6). `DefaultVolumeProfile.asset` was
>   deliberately left untouched so editor previews, MainMenu and the Sky Render suite are unaffected.
> - **Gating:** one `Bloom` Graphics setting drives the camera's `renderPostProcessing` **and**
>   `_EmissiveBoost` (1.0 on, 0 off) together — emissive above 1.0 is only meaningful because bloom
>   catches it, so the two must never disagree. Default on.
>   **The camera half is additionally gated on a `Volume` existing in the loaded scenes**
>   (`GraphicsSettingsController.ApplyBloom`): `MainMenu.unity` hosts the same controller but has no
>   `Volume`, so without the gate the default-on setting forced a full-screen post pass and an
>   intermediate target there for no visual effect. `_EmissiveBoost` stays unconditional — it is inert
>   without emissive geometry. Confirmed in game 2026-08-14: no main-menu regression, bloom unchanged
>   in world.
> - **Shader model:** the liquid path went to `#pragma target 3.5` (from 3.0) because `LiquidV2F` now
>   carries 11 interpolators and 3.0 only guarantees 10 (`interpolators10`); 3.5 is the tier that raises
>   it to `interpolators15`. Free on this project's targets — 3.0 and 3.5 have identical platform support
>   lists. Rule: [`../Guides/SHADER_CONVENTIONS.md`](../Guides/SHADER_CONVENTIONS.md) §1. Every other
>   project-owned shader was swept to 3.5 the same day (archived `CODEBASE_IMPROVEMENTS` §1.4).
> - **Guard:** meshing baseline **B61**; `Validate All` 477/477 across 21 suites.

**Corrections to this entry's original analysis** (verified against code 2026-08-12):

- The `Color32` stream was **not** free tint. Its RGB is white for blocks but the *fluid* submesh already
  encodes `(FluidShaderID, shoreMask, shadowMultiplier)` there. Only **alpha** was free across all three
  submeshes — which is also what let lava glow without competing with TF-11.
- The meshing job needed **no new input**: `BlockTypeJobData.LightEmission` was already available.
- A `Volume` component existed in no scene, but `Assets/DefaultVolumeProfile.asset` (wired into URP
  Global Settings) **did** exist, carrying every override neutralized.
- `m_ColorGradingMode: 0` (**LDR**) is unmentioned by the original entry and still in force: HDR
  highlights past the bloom hard-clip rather than rolling off. Deliberate — see "open" below.
- The camera flag is at `World.unity:3714`, not `:3694`.

**Open / known limitations.**

1. **Tonemapping + HDR colour grading not adopted.** Shipped with `m_ColorGradingMode` at LDR and no
   tonemapper, so exactly one variable changed and the A/B captures stayed readable. ACES visibly shifts
   every existing colour and still needs its own capture pass and sign-off.
2. **Bloom does not appear in the UI blur backdrop.** `UIBlurRendererFeature` injects at
   `RenderPassEvent.AfterRenderingTransparents` (`UIBlurRendererFeature.cs:73`), and URP composites the
   post stack *after* that — so the blur snapshots scene colour one stage before bloom exists. Verified in
   game and **accepted**; changing it means moving the injection point past post-processing.
3. **No performance capture was taken.** Bloom ships default-on without a measured frame cost; the
   post stack adds a full-screen pass plus an intermediate target. Waived by the user for desktop.
4. **§5 effects** (vignette, DoF, motion blur) remain unstarted — the Volume now exists, so each is one
   override plus a sign-off.
5. **The UI blur target must stay a persistent per-camera resource** — never a render graph texture.
   Enabling bloom turned the pause-menu backdrop near-black with the lava still blazing through it
   (fixed 2026-08-14, confirmed in game). `UIBlurRenderPass` published its blurred result as the global
   `_UIBlurTexture`, but that result was a graph-created texture, and the render graph returns
   non-imported resources to its pool **at their last-used pass** mid-frame
   (`NativePassCompiler.cs:1930-1949`). Every canvas here is Screen Space - Overlay, so the UI samples
   `_UIBlurTexture` *after* the graph has finished. With bloom off nothing else claimed that memory and
   the blur survived by luck; with bloom on, URP's bloom **prefilter** — same half resolution, same
   `B10G11R11_UFloatPack32` — was handed the identical texture, so the UI sampled the scene thresholded
   at 1.1: emitters intact, everything below gone. Proven by dropping `threshold` to 0, which made the
   backdrop show the full scene, sharp and unblurred. The target is now `UIBlurHistory`, a
   `CameraHistoryItem` imported into the graph each frame, keyed per camera so a Game and a Scene view
   at different sizes do not reallocate each other's; a feature-owned handle covers cameras with no
   history manager. Independent of limitation 2 — the blur still samples one stage before bloom.

**What exists today.** *(pre-implementation analysis, retained for context)*

- URP is configured with HDR on (`m_SupportsHDR: 1`,
  `Assets/Settings/Rendering/VoxelEngine-URP-Asset.asset`) and the renderer has the default
  `postProcessData` assigned — the post stack is *available*.
- But: the camera has post-processing **off** (`m_RenderPostProcessing: 0`, `World.unity:3694`)
  and no `Volume` component exists in any scene → no bloom, no tonemapping, nothing.
- Emissive-looking blocks (Lava, the DebugLamp01–15 family — `BlockIDs.cs:44-79`) output ≤ 1.0:
  they are *lit* by their own blocklight via the standard shade curve, never HDR-bright. Bloom
  enabled today would only ever catch the sky/sun.

**Proposed design.** *(the original proposal — §1–§3 shipped, with the deviations noted in the as-built
block above; §2's "reuse the tint stream" reasoning in particular was corrected to alpha-only. Retained
because §1's tonemapping half and §5 are still open and still describe intended work.)*

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
   growing the vertex). **Coordinate with TF-11 (climate foliage tint), which claims the RGB
   channels of the same stream** — together they exactly fill it; allocate before either ships.
   Allocation registry note: the *other* spare stream, `TexCoord0.zw` (half2), is **consumed
   since 2026-07-19** by FL-1 foliage sway (z = sway weight, w = per-voxel phase; fluid submesh
   keeps its own shore-push meaning) — the `Color32` stream is the only spare capacity left.

   > **ALLOCATION REGISTRY — `Color32` stream, as of 2026-08-12.** The single source of truth is the
   > pair of comments on `SectionRenderer.Layout` and `Data.MeshDataJobOutput.Colors`; this is the
   > design-side mirror.
   >
   > | Channel | Blocks (opaque + transparent) | Fluid submesh | Owner |
   > |---|---|---|---|
   > | R, G, B | white (`255`) — **unclaimed** | `FluidShaderID`, `shoreMask`, `shadowMultiplier` | TF-11 claims the block side |
   > | A | **RF-3 emissive strength** (emission 0-15 ×17) | **RF-3 emissive strength** | RF-3 — **SHIPPED** |
   >
   > Consequences for anyone reading this next: **alpha is gone.** TF-11 can still take block-side RGB,
   > and doing so fills the stream exactly. **RF-9 must find capacity elsewhere** — its entry calls this
   > stream "double-claimed", which was speculative when written and is now half true in fact.
   > A new attribute grows the MR-2 32-byte vertex and re-pins the B-series baselines; budget for that.
   >
   > **Alpha's zero value is load-bearing.** Non-emitters seed `Color32(255, 255, 255, 0)`, not the
   > historical `…, 255`. The shader reads this channel as emissive strength, so a 255 fill renders every
   > ordinary block at full emissive boost — this shipped briefly and washed out the whole world
   > (`3b246bc2`). Any future writer added to `VoxelMeshHelper` must seed alpha 0.
3. **Quality gating:** bloom + the post stack cost real GPU time on mobile — gate behind the
   settings/device-tier system (`OM1_DEVICE_CALIBRATION.md` budgets; `DATA_DRIVEN_SETTINGS_UI`
   for the toggle). Desktop default on, mobile default off.
4. ✅ **Done alongside `GS-4`** (render-pipeline tier audit, shipped 2026-08-15) — same files, same
   testing pass. `GS-2`'s opaque-texture concern is still open and still interacts with any post pass
   that needs scene color.
5. **Other post effects (routed here from the VX-* gap sweep, 2026-07-20):** once the Volume
   exists, each is one override — and each is a *separate deliberate art decision* with the
   same A/B capture sign-off as tonemapping: **vignette** (subtle, cheap, likely the first
   yes), **depth of field** (screenshot/menu mode only — gameplay DoF fights block reading),
   **motion blur** (default-off; fast yaw over hard voxel edges reads as smear — adopt only
   if a capture pass proves otherwise). All tier-gated with the rest of the stack (§3).

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

1. Global uniform `_BlocklightFlicker` set each frame by `World` (piggyback on
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
- Flat ambient floor `MinLightLevel = 0.15`; no SSAO; no realtime shadow maps — `GS-4` (2026-08-15)
  settled that state rather than leaving it open: main-light shadows are now *unsupported* in the URP
  asset, so switching shadows on means undoing four coupled settings (see its archived entry).

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
tuned to ~0.5–1 block. Quality-tier gate (off on mobile) — ⚠ **that gate has no existing home**: RF-3
and `GS-4` have both shipped (2026-08-12 / 2026-08-15) and neither added a device-tier mechanism, so this
item must bring its own (OM-1 budgets + a `DATA_DRIVEN_SETTINGS_UI` toggle).

**Dependencies / ordering.** None hard; the RF-3/`GS-4` pairing has passed.

**Deferral home (2026-07-20):** the rejected GI-class options now have an explicit
experimental-tier landing zone — `VOLUMETRIC_AND_RAYTRACED_EFFECTS_REPORT.md` VX-6
(voxel-traced 1-bounce GI, off-by-default, desktop-only). This section's default-tier verdict
(SSAO, and stop there) is unchanged.

**Risks.** 🟢 — additive renderer feature; the only visual risk is stacking with vertex AO
(tune, capture, sign off). Seed/Save ✅.

---

### RF-7 — Weather (rain, snow, storm skies)

**Classification:** Polish (but a large ambience gap — there is currently zero weather of any
kind in the project).

**What exists today.** Nothing: no precipitation rendering, no weather state, no storm sky
treatment. The relevant hooks all exist elsewhere: RF-1's `SkyEvent` tint mechanism (storm
darkening is exactly a sky event), RF-2's fog/sky gradients, the `Clouds.cs` cloud plane
(density/color are natural storm knobs), and — for precipitation *type* — TF-3/TF-11's
temperature axis in the sibling worldgen report (rain vs. snow by climate, snow above the
TF-11 snow line).

**Proposed design (v1 — transient, render-only).**

1. **Weather state machine** on `World` (plain manager, `WorldTimeManager` pattern):
   `Clear / Rain / Storm` with seeded random durations. v1 is deliberately **not persisted** —
   weather rerolls on load (Save ✅). *Update 2026-08-10: the persistence hook now exists — the
   `/wind` command shipped a `level.dat` `environment` section (save v14) holding the shared wind
   vector, created explicitly as the home for these weather fields. Persisting the weather state is
   now an additive field in that section, not a new migration design.*
2. **Precipitation rendering:** a camera-following particle volume (GPU particles or a scrolling
   textured shell — prototype both; the shell is the mobile-safe option). **Under-cover culling**
   uses the existing highest-voxel heightmap (`GetHighestVoxel` path): sample the heightmap around
   the camera into a small texture each frame and discard precipitation fragments below it — no
   per-particle voxel queries.
3. **Type by climate:** at the camera position, sample the TF-9 Layer-2 temperature axis (with
   TF-11's altitude lapse) → rain vs. snow. Degrades gracefully pre-TF-3: a single global type
   toggle until the climate axis exists.
4. **Storm sky:** drive RF-1's event multiplier (`SkylightColor` darkening) + RF-2 fog density +
   cloud plane color/density from the weather state — all existing or planned uniforms; zero
   lighting-engine contact (the BFS/per-voxel light is untouched, same shader-only contract as
   the blood moon).
5. **Out of scope for v1 (state explicitly):** snow-layer accumulation and ice formation as
   *block changes* (that is worldgen/tick territory — accumulation would need a budgeted behavior
   like RF-5's cap) and gameplay effects (crop growth, mob behavior).
6. **Lightning (v2 sketch — routed here from the VX-* gap sweep, 2026-07-20; still out of v1
   scope):** three decoupled pieces, none touching the light engine — **flash** = the RF-1 §4
   `SkyEvent` override driven for 2–3 frames (white-blue tint + a raised `globalLightLevel`
   floor; the same zero-relighting contract as the blood moon — a 100 ms event must never
   BFS-flood light), **bolt** = a one-off emissive billboard/polyline mesh at a seeded strike
   point near the player during `Storm`, **thunder** = a delayed audio hook that lands in
   `SOUND_ENGINE_DESIGN.md` when the sound engine ships.

**Dependencies / ordering.** Rendering rides RF-1 (event tinting) + RF-2 (fog/sky) — build after
both. Precipitation-by-climate wants TF-3/TF-11 but degrades gracefully. Quality-tier gate the
particle cost (OM-1 budgets), like RF-3.

**Risks.** 🟡 — purely visual, but the under-cover culling and mobile particle cost need real
tuning; no pipeline, storage, or lighting-semantics contact. Seed ✅ / Save ✅ (v1 transient).

---

### RF-8 — Animated block textures (flipbook atlas animation)

**Classification:** Polish — the Minecraft-parity ambience gap for surfaces (fire, portals,
sea-lantern-style blocks, magma crust). Routed here from the `VX-*` gap sweep (2026-07-20).

**What exists today.**

- Block face textures are static tiles in a single atlas built by the `AtlasPacker` editor tool
  (`VoxelData.TextureAtlasSizeInBlocks` grid); all three block materials share that `_MainTex`.
  Face texture IDs are baked into UVs at mesh time.
- The **only** animated surfaces are the fluids, whose motion is *procedural* in
  `LiquidCore.hlsl` (the GS-1 noise) — nothing frame-animates a texture anywhere.

**Analysis — where the animation can live:**

| Approach                                                    | Verdict                                                                                                                                                               |
|-------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Per-vertex "animated" flag + shader-side UV frame cycling   | ❌ Rejected — the vertex format has no spare capacity (`uv.zw` = FL sway, `Color32` reserved TF-11 + RF-3), and per-tile frame metadata would need yet another channel |
| Re-mesh on animation tick                                   | ❌ Rejected outright — remeshing as an animation driver is the anti-pattern every report here exists to prevent                                                        |
| **Animate the atlas itself (MC's approach)** (✅ **CHOSEN**) | ✅ Frames authored as strips; a fixed tick GPU-blits the current frame into the tile's atlas slot (`Graphics.CopyTexture`). Zero vertex/mesh/shader change             |

**Proposed design.**

1. `AtlasPacker` gains per-source-texture animation metadata (frame count, frame time; frames
   authored as a vertical strip, MC-convention). The packer emits the strip frames into a
   staging texture (or keeps the strip asset) plus a manifest of animated tile slots.
2. A small `AtlasAnimator` (plain manager on `World`, `WorldTimeManager` pattern): on a fixed
   tick (~2–10 fps per tile, per-texture rate), `Graphics.CopyTexture` the next frame region
   into the atlas slot — GPU-side region copy, no CPU pixel work, a handful of tile-sized
   copies per tick.
3. **Mip watchpoint (the one real gotcha):** the atlas has a mip chain; `CopyTexture` must copy
   each mip level of the frame (author strips pre-mipped, or copy from a mipped staging
   texture) or animated tiles shimmer at distance.
4. Every consumer inherits the animation for free — all three block shaders, editor previews,
   and block icons sample the same atlas.

**Dependencies / ordering.** None hard — independent of RF-1..7. Content lands whenever the
first animated block (fire, portal, …) is authored via the BlockEditor pipeline.

**Risks.** 🟢 — isolated to the atlas asset + one manager; no meshing, lighting, or pipeline
contact. Verify `CopyTexture` format/mip compatibility on the compressed atlas (may require the
atlas uncompressed or same-format frame strips). Seed ✅ / Save ✅.

---

### RF-9 — Vertex AO crushes to black at night (composition order)

**Classification:** Polish, but a *regression surfaced by RF-1 §10* rather than a pre-existing gap —
filed 2026-08-10 from the in-game confirmation of RF-1 Phase 2.

**Symptom (observed in game).** At midnight, corner/contact AO becomes so pronounced that whole faces
read as solid black, losing the shape information they carry during the day. Daylight is unaffected.

**What exists today (verified in code).**

- Smooth-lighting AO is applied to the vertex light value **at mesh time**, before the value is
  encoded: `float4 shaded = light * (1f - occlusion);` (`MeshGenerationJob.cs:1517`). The vertex
  therefore stores the *product* `exposure × (1 − occlusion)`.
- RF-1 §10 then **subtracts** the day/night darkening from that product in the fragment shader:
  `max(skyExposure − (1 − GlobalLightLevel), 0)` (`ApplySkyDarken`, `VoxelLighting.hlsl`).
- At the deepest night the subtraction is `11/15 ≈ 0.733`, leaving only `0.267` of range. **Any vertex
  whose occlusion exceeds ~27% therefore clamps to zero** — and every such vertex clamps to the *same*
  zero, which is why differentiation is lost rather than merely dimmed.

Measured shadow multipliers at midnight (shade curve + gamma, `MinLightLevel = 0.15`):

| Vertex                | Pre-RF-1 (multiplicative) | Shipped (subtractive) |
|-----------------------|---------------------------|-----------------------|
| Unoccluded, open sky  | 0.1635                    | 0.0932                |
| 30% occluded          | 0.0916                    | **0.0063 — the floor** |
| Fully sealed (cave)   | 0.0063                    | 0.0063                |

A 30%-occluded face is now **exactly as dark as a sealed cave face**, whereas before it was as bright
as the new model's fully-open sky. Note the floor itself is *unchanged* — `MinLightLevel` behaves
identically in both models; what changed is that the usable range above it collapsed.

**Root cause.** AO and time-of-day darkening are composed in the wrong order. AO is a *geometric
visibility* factor and darkening is a *source-intensity* factor, so they should multiply with the
subtraction applied to the source — `max(exposure − darken, 0) × (1 − occlusion)` — not
`max(exposure × (1 − occlusion) − darken, 0)`. Under the correct order the same 30% vertex reads
`0.267 × 0.70 = 0.187` of the sky's night intensity: dimmer than its neighbours, still legible, and
the AO ratio stays constant at every hour.

**Why it is not a one-line fix.** The vertex holds only the product, so the shader cannot recover the
two terms. Separating them needs occlusion in its own vertex channel, and **there is no spare
capacity**: `TexCoord0.zw` (half2) went to FL-1 foliage sway (2026-07-19), and the `Color32` tint
stream's **alpha is now spent** — RF-3 shipped emissive strength there on 2026-08-12, leaving only the
block-side RGB, which TF-11 (climate foliage tint) claims. **This is no longer a decision RF-9 can share
with RF-3; RF-3 is decided.** RF-9's realistic options are now (a) take the block-side RGB ahead of
TF-11, or (b) grow the MR-2 32-byte vertex with a new attribute. See RF-3 §2's allocation registry for
the channel-by-channel state. `SectionRenderer.Layout` is the single source of truth, and any change
rides the meshing suite's B-series baselines.

**Options (not yet evaluated — this entry is the analysis, not the decision).**

| Option                                                            | Note                                                                                                                                                        |
|-------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------|
| Move occlusion to its own vertex channel, reorder the composition | The correct fix; blocked on the RF-3/TF-11 allocation. Also makes AO strength runtime-tunable, which SS-* would benefit from                                |
| Soften AO strength as a function of `GlobalLightLevel`            | Cannot be done in the shader (not separable from the product). Would have to be applied at **mesh time**, which makes the mesh time-dependent — rejected on sight: it reintroduces remeshing as an animation driver |
| Compress the subtraction near zero (e.g. a soft floor)            | Shader-only and cheap, but it breaks the §9/§10 exactness — the rendered level would stop equalling the queried one, which is the whole point of §10        |
| Accept as Minecraft-parity                                        | MC's AO is likewise more visible at night. The honest fallback if the channel never frees up; re-judge against a capture rather than from the numbers        |

**Dependencies / ordering.** Shares RF-3's vertex-channel decision; no dependency on RF-2. Nothing
here touches the light engine, storage, or save format.

**Risks.** 🟡 — vertex-format edits are regression-prone without the meshing suite baselines, and the
change alters every rendered surface's night appearance. Seed ✅ / Save ✅.


### RF-10 — The skylight tint gradient ships flat white (RF-1's mechanism is unauthored)

**Classification:** Content gap, not a code defect — surfaced 2026-08-25 while verifying LP-7's
Sun→Sky sweep. Every piece of the tinting path is built, shipped, and now *confirmed working*; the
gradient it reads has simply never been authored.

**What exists today (verified in code and in the asset).**

- `DefaultTimeOfDaySettings.asset` is the only `TimeOfDaySettings` in the project, and its skylight
  gradient evaluates to **pure white `(1.00, 1.00, 1.00)` at all nine sampled day fractions**
  (0.00 → 1.00). It carries two colour keys, both `r:1 g:1 b:1`.
- `BuildDefaultSkylightGradient()` (`TimeOfDaySettings.cs`) is white→white by construction. The asset
  never overrode it, so the shipped content *is* the placeholder default.
- The transport around it runs every frame: `World.SetGlobalLightValue()` (`World.cs:2163-2164`) reads
  `TimeManager.SkylightColor` and pushes it as the `SkylightColor` shader global, which
  `StandardBlockShader`, `TransparentBlockShader`, `UberLiquidShader` and `CloudShader` all consume.
- **White is the identity** under the shader's multiply. The feature is therefore fully wired and
  completely invisible.

**Confirmed working (2026-08-25, in game).** The skylight gradient was set to red and the world
rendered red, exercising gradient → `EvaluateSkylightColor` → `WorldTimeManager.SkylightColor` →
`SetGlobalColor("SkylightColor")` → the four shader declarations. **This item costs no code.** It is
an asset-authoring task in the Sky Editor.

**Why it sat unnoticed for three months.** RF-1 Phase 1 shipped this deliberately — its
"deliberately not shipped" note records that "the tint gradient ships flat white … so Phase 1's only
visual delta is brightness". The deferral was written down but never given a backlog ID, so it never
entered the master summary table and dropped out of view. The generalizable half is worth keeping:
**an identity value is indistinguishable from a broken path during ordinary play.** Nothing short of a
deliberate non-identity test tells you whether a multiply-by-white pipeline works or is dead, which is
exactly why the red-gradient check above was necessary rather than belt-and-braces.

**What to author.** RF-1 §3 already carries the full authoring spec — do not re-derive it. In brief:
blue-shifted Purkinje-style night keys (≈ `RGB(0.65, 0.75, 1.0)`), warm sunrise near 0.25, white noon
at 0.5, red-orange dusk near 0.75. The rule that matters most is §3's: **hold B at 1.0 and reduce only
R/G** on the night keys — scaling all three channels down double-dips with §2's brightness curve and
pushes the moonlight floor below readable. Torches stay warm and caves stay neutral for free, because
the tint multiplies only the sky contribution before the per-channel `max()` in `ApplyVoxelLightingRGB`.

**Options.**

| Option | Note |
|---|---|
| Author the skylight gradient alone | The minimum that delivers moonlit nights. Self-contained, reversible, no code |
| Author the background/fog gradient in the same blue family | RF-1 §3's "RF-2 coordination" bullet — without it the horizon clashes with the newly-tinted terrain. Recommended as the same sitting |
| Leave flat white | Honest only if the intent is a deliberately colourless night. Should then be recorded as a decision, because it currently reads as an oversight |

**Dependencies / ordering.** RF-1 (shipped) supplies the mechanism and the spec; RF-2 (shipped)
supplies the horizon colours to coordinate against. **Distinct from RF-1 §4's `SkyEvent` blood-moon
tint**, which is a genuine *code* gap (the lerp seam is left open, no gameplay system produces events)
— RF-10 is content only and does not unblock or depend on it. Interacts with RF-9 only in that the tint
applies after §10's subtractive shade, so it recolours without re-darkening.

**Risks.** 🟢 — no code, no pipeline invariants, no seed or save impact (the gradient lives on a
ScriptableObject asset, not in the save; `worldState.timeOfDay` is unaffected). The visual reach is
wide — every sky-lit surface at night — so judge it from an in-game capture at several day fractions
rather than from the gradient swatch. Seed ✅ / Save ✅.

Note the field key moved in LP-7's sweep: `_skyLightOverDay` → `_skylightOverDay`. The asset was
reserialized and its values verified intact, so authoring proceeds normally.

---

## Roadmap

See the **combined ranked roadmap** at the end of
[`WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md`](WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md) — RF items
rank: RF-1 (#1 — **shipped 2026-08-10**; RF-2 no longer depends on its unshipped sections, so it is
archivable — but note RF-2 §2's blood-moon tint still waits on its §4 `SkyEvent`),
RF-2 (#5 — **§1–§5 shipped 2026-08-11**; the §6 remainder is unranked polish),
RF-7 (#17), RF-4 (#18), RF-3 (#19), RF-6 (#20), RF-5 (#21),
RF-8 (#22 — added 2026-07-20), RF-9 (unranked — added 2026-08-10; schedule **with RF-3**, whose
vertex-channel allocation it shares, rather than on its own merit).
RF-10 (unranked — added 2026-08-25; pure asset authoring with no code, so it does not compete for
engineering time and can land whenever someone opens the Sky Editor).

---

## Document History

* **v2.5** - **RF-10 filed** (2026-08-25). LP-7's Sun→Sky sweep needed the `SkylightColor` shader
  binding proven end-to-end, and proving it exposed that the gradient behind it is **flat white at every
  hour** — `DefaultTimeOfDaySettings.asset` still carries `BuildDefaultSkylightGradient()`'s white→white
  placeholder. Since white is the identity under the shader's multiply, RF-1's tint has been shipping as a
  no-op since Phase 1. RF-1's Phase-1 notes *do* record the deferral, but it never got a backlog ID and so
  never reached the summary table; this entry gives it one. **No scope change to any existing item, and no
  code work — RF-10 is asset authoring against RF-1 §3's existing spec.** Confirmed the path itself is
  sound by setting the gradient red in game and observing a red world.
* **v2.4** - **`RF-*` status sweep + one correction** (2026-08-15, no scope change). RF-1's detail banner
  and summary row still read "Phase 2 awaiting one in-game confirmation", contradicting **this document's
  own** v1.3 entry and RF-9 §, both of which record that confirmation on 2026-08-10 — both now say
  shipped. Also corrects v2.3, which attributed the homeless SSAO quality-tier gate to **RF-5**; the SSAO
  item is **RF-6**.
* **v2.3** - **`GS-4` cross-references de-staled** (2026-08-15). `GS-4` shipped and closed, so the four
  sites telling a future session to "do this together with `GS-4`" are now historical. The substantive
  correction: **`GS-4` added no device-tier gating mechanism** (a second mobile URP asset was explicitly
  out of its scope), so RF-6's SSAO quality-tier gate has no existing home and must bring its own. Also
  records what `GS-4` settled about shadows — main-light shadows are now *unsupported* in the URP asset,
  making "switch shadows on" a four-setting undo. No scope change to any `RF-*` item.
* **v2.2** - **The sun-flare bullet is retired, not deferred** (2026-08-15). `SN-0` and `SN-1` of
  [`SUN_APPEARANCE_IMPROVEMENTS.md`](SUN_APPEARANCE_IMPROVEMENTS.md) shipped; `SN-2` and `SN-3` were
  **refuted**. The HDR core met every number the design asked for — the sun's disc went from a flat
  0.9941 ceiling to 4.64 at noon, clearing URP's ~1.23 threshold at every hour — and the resulting
  picture was still wrong, because one global `Bloom` override sizes the halo for RF-3's lava and lamps
  and a 3-degree disc wants a different radius from the same `scatter`. It was reverted in full,
  volume-profile asset included. RF-2 §6's "let bloom catch the HDR sun disc" is therefore closed as
  **the wrong answer**, having already been corrected once in v2.1 as the wrong *cost*. The successor
  is glare produced in the skybox shader itself.
* **v2.1** - **Sun appearance split into its own design** (2026-08-15). New
  [`SUN_APPEARANCE_IMPROVEMENTS.md`](SUN_APPEARANCE_IMPROVEMENTS.md) (`SN-0`..`SN-3`) takes over the
  RF-2 §6 sun-flare bullet, which is **struck through and corrected**: "free once both ship" was
  wrong. RF-3's bloom shipped and the sun path was never made HDR, so the disc's ceiling of exactly
  1.0 sits below URP's ≈1.23 effective linear threshold and contributes ~6 % through the soft knee —
  visually nothing, and untunable from the shader because the disc is already at its own ceiling.
  Two findings worth carrying beyond that doc: the skybox gradient is a function of `viewDir.y`
  **alone**, so there is no aureole and the whole horizon ring warms at sunset; and the sun still
  runs the blend-toward-fog atmosphere model the RF-2 polish arc refuted and replaced on the moon.
  The sprite-chain flare rejection stands — URP's screen-space flare simply is not one, since
  reading rendered pixels makes occlusion inherent.
* **v2.0** - **RF-3 limitation 5 added** (2026-08-14): the UI blur target must remain a persistent
  per-camera resource. Bloom being enabled made the pause-menu backdrop near-black because
  `UIBlurRenderPass` published a render-graph-pooled texture as `_UIBlurTexture`, and the bloom
  prefilter — identical descriptor — was handed that memory after the pool released it at its last-used
  pass, while Overlay canvases sample the global *after* the graph. Diagnosed by dropping bloom's
  `threshold` to 0 (backdrop then showed the full sharp scene), fixed with a per-camera `UIBlurHistory`
  imported into the graph, confirmed in game. Limitation 2 is unchanged and unrelated; its line
  reference was corrected (`:69` → `:73`).
* **v1.9** - **RF-3 shipped** (2026-08-12; `b981ec44`, `3b246bc2`, `c1748d15`, `95bae9a0`). §1 stack,
  §2 HDR emissive and §3 gating are in game behind one `Bloom` Graphics setting; tonemapping and the §5
  effects stay open. The entry gained an as-built block, five corrections to its original analysis (the
  `Color32` stream was never free tint — only **alpha** was; the meshing job already had
  `LightEmission`; a global default volume profile did exist; `m_ColorGradingMode` is LDR; the camera
  line number moved), a **channel allocation registry** for the `Color32` stream, and four stated
  limitations — of which the load-bearing one is that **alpha's zero value is a contract with the
  shader**, briefly violated in `b981ec44` and fixed in `3b246bc2`. RF-9's entry and the Next Review
  were corrected: the vertex-channel decision is no longer shared with RF-3, because RF-3 made it.
* **v1.8** - **The dawn-runs-ahead-of-the-sun row shipped and is removed** (2026-08-12). The sky
  gradients now key dawn on the celestial horizon crossing (0.25) via a new `DAWN_HORIZON_CROSSING`
  constant, while the light curve keeps Minecraft's named `/time` target (0.2083) — see
  [`../Architecture/SKY_AND_CELESTIAL_RENDERING.md`](../Architecture/SKY_AND_CELESTIAL_RENDERING.md)
  §2.5. The dawn/dusk luminance delta at −10.55° sun altitude fell from **+0.2242 to +0.0101**, and the
  remaining 0.0174 at the crossing is exactly the authored pink-dawn/orange-dusk hue difference, kept on
  purpose: the halves mirror in **shape, not hue**. Three things this row got right and one it framed
  loosely. Right: the mirror measurement localized the defect to the gradient keys; `SUNSET` really was
  already correct; and the trap it flagged was real — the fix reached the game only through
  `SkyGradientDefaults.Reset` writing the `.asset`, not through the code defaults. Loosely framed: the
  row called a gradient-only fix "plausibly" baseline-safe and demanded the suite be run. It was run,
  before and after, and came back **bit-identical** — but the reason is stronger than "plausible" and
  weaker as evidence: `WorldClockValidationSuite` builds its settings via `CreateInstance` and reads only
  the curve, so it is **structurally incapable** of observing a gradient change. That green is a
  no-regression signal and nothing more. `Validate All` held at 21 suites / 475 baselines / 0 failed.
* **v1.7** - **Moon phase browsing + `Validate Sky Render`** (2026-08-12). The Sky Editor can now step
  through all eight named phases, and the sky's *shader* half has automated coverage for the first time —
  6 rendered-pixel baselines, `Validate All` now **21 suites / 475 baselines**. See
  [`../Architecture/SKY_AND_CELESTIAL_RENDERING.md`](../Architecture/SKY_AND_CELESTIAL_RENDERING.md) §7.1
  v1.3. Recorded there because it generalizes beyond the sky: **three of the six baselines passed the exact
  mutation they were written to catch** — a sample too small to contain a star, a sample box wider than the
  disc it measured, and a haze scenario running with fog disabled. All three were found by running the
  mutations, none by reading the code.
* **v1.6** - **Sky Editor shipped 2026-08-12** (`Minecraft Clone/Sky Editor`), so the "sky colors in an
  editor tool" row narrows to the per-biome override alone — which still needs its design pass and is
  explicitly not an implementation task. Two findings from building it are recorded in
  [`../Architecture/SKY_AND_CELESTIAL_RENDERING.md`](../Architecture/SKY_AND_CELESTIAL_RENDERING.md) v1.2:
  the per-pixel CPU colour conversion, not the GPU render, is what would force a preview to debounce
  (27 ms vs 3 ms at 640×260); and backlog IDs had leaked into user-facing strings, with an `RF-2` header
  reaching the tool's own UI. **Editor tooling must not surface backlog IDs** — several remain elsewhere
  (`BlockEditorWindow`, `Clouds`, `SettingsManager`, benchmark headers) and are unswept.
* **v1.5** - **RF-2's richer sun and moon discs shipped and confirmed in game 2026-08-12** — procedural
  craters and mottling, sun limb darkening, a third degeneracy guard, and an atmosphere model for the
  discs (extinction + airlight). The "richer moon shader" row leaves the remainder table; see
  [`../Architecture/SKY_AND_CELESTIAL_RENDERING.md`](../Architecture/SKY_AND_CELESTIAL_RENDERING.md) v1.1.  
  **A new row was added rather than removed:** the sky's dawn runs ahead of the sun by design accident —
  the `SUNRISE` gradient key is at day fraction 0.2083 while the sun crosses the horizon at 0.25, so the
  sky reaches 82% of noon brightness while the sun is still 10.55° down. Found only by looking at the game;
  no suite could have. Two claims made during this work were **wrong and are recorded as such**: a "new-moon
  limb ring" defect that turned out to be a probe measuring the sun's sub-pixel disc, and a shader-target
  bump justified by a compile limit that did not exist.
* **v1.4** - **RF-2 §1–§5 shipped and confirmed in game 2026-08-11** (commits `4a6fa38d` → `c471766b`),
  promoted to [`../Architecture/SKY_AND_CELESTIAL_RENDERING.md`](../Architecture/SKY_AND_CELESTIAL_RENDERING.md);
  the entry here keeps only §6 and four deferred riders. The as-built system departs from the sketch in
  three ways worth recording: the sun/moon ride a **real equinox celestial model** parameterized by
  latitude rather than `±SunDirection`, with moon position and phase derived from one elongation; fog is
  **engine-owned** rather than `RenderSettings.fog`, which removes the `FOG` shader-variant cost §4 had
  handed to `GS-4`; and fog uses **horizontal** distance on a back-loaded curve, because a linear ramp
  painted its own gradient across mountains. Five stale claims in the pre-implementation notes were
  corrected (a skybox material *was* assigned; **no shader supported fog at all**; `SunElevation` is
  latitude-free; `SkyEvent` never shipped; line references drifted). **RF-9 confirmed in game with
  measured numbers** — RF-2's darker night makes it more visible, not less.

*Entries below the newest are reconstructed from git history — this document predates the
project's Document History convention, so they record what the commits changed rather than
contemporaneous notes.*

* **v1.3** - RF-1 Phase 2 shipped and confirmed in game (2026-08-10): §9 effective-light query +
  §10 subtractive sky term + the cloud rewire. **RF-9 added** from that confirmation — the subtractive
  model exposed that vertex AO is baked in *before* the darkening, so at night any face past ~27%
  occlusion clamps to the same black. Analysis and measured numbers are in the entry; it shares RF-3's
  vertex-channel decision and is unranked pending that.
* **v1.2** - RF-1 Phase 1 shipped (2026-08-10): world clock + `TimeOfDaySettings` asset + `/time` regrammar +
  save v15. Corrected five stale claims in the RF-1 entry while implementing it — `SetGlobalLightValue` had a
  third call site (`/time`, shipped 2026-07-18), the save version was already v14 (not 11), the `environment`
  section already existed, `hasSkylight`/`fixedGlobalLightLevel` were never built, and the line references had
  drifted. §9/§10 remain open as Phase 2.
* **v1.1** - Mandatory header completed (2026-07-26): `Version`/`Date`/`Status`/`Target` lifted out of
  the summary blockquote into proper fields, including the RF-vs-LI/GS ownership split that keeps this
  report from overlapping the performance backlog. No findings or rankings changed.
* *(2026-07-20, `6728bee0`)* - Cross-linked the new `VX-*` volumetric/ray-traced report; RF-2 §6 gained
  the sky-ambience v2 ideas (aurora, shooting stars) routed from that sweep, and RF-8 was added at #22.
* *(2026-07-19, `cf425bae` · `0cbd46c8` · `505ce646` · `e2b2cb0c`)* - `CL-*` cloud and `FL-*` foliage
  reports split out as siblings: **CL-2 absorbed RF-2 §5** (cloud tinting) and RF-7 §4's cloud knobs
  were handed to CL-4; RF-3 §2 re-pointed at the shipped `uv.zw` sway channel.
* *(2026-07-03, `2dde457e`)* - **RF-1 substantially amended**: the `SkyDarken` effective-light query
  layer (§9) and subtractive shader parity (§10) — stored skylight is time-invariant *sky exposure*,
  and gameplay reads a derived effective light rather than raw storage. Blue-moonlight authoring rules
  added to §3; §4's event tint changed from multiply to lerp/replace.
* *(2026-07-03, `7e99e6f7` · `95b2cbc1`)* - Second gap sweep added RF-7 (weather) alongside the sibling
  worldgen report's TF-10…TF-14.
* *(2026-07-02)* - Initial report at commit `a458173`: the `RF-*` lighting/rendering feature backlog.

---

**Last Updated:** 2026-08-15 (RF-1 status swept + RF-5/RF-6 correction; `GS-4` cross-refs de-staled; SN-0/SN-1 shipped; SN-2/SN-3 refuted — the RF-2 §6 flare bullet is retired)  
**Next Review:** **RF-9 is the most visible open item**, its severity measured in game (a 30%-occluded
face is 14.8× darker than flat ground at midnight and indistinguishable from a sealed cave face). Its
vertex-channel question is **no longer shared with RF-3** — RF-3 spent `Color32.a` on 2026-08-12, so
RF-9 now chooses between taking block-side RGB ahead of TF-11 or growing the MR-2 vertex; see RF-3 §2's
allocation registry for the current channel state rather than re-deriving it. Note that no validation
suite can observe the shader half of RF-1 §10, any of RF-2's rendering, or RF-3's emissive *render* (B61
guards only the vertex data) — all are capture-verified only.
