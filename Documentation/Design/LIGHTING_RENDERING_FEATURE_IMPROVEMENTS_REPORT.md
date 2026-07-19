# Lighting & Rendering Feature Improvements Report

> The master backlog for **lighting and rendering features** in the VoxelEngine — the
> feature-and-design counterpart to [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md),
> which owns lighting/GPU *performance* items (`LI-*`, `GS-*`). Sibling report to
> [`WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md`](WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md) (`TF-*`);
> the **combined ranked roadmap lives at the end of that document**.
>
> Status: **Open backlog.** Items are removed (archived) when implemented and verified.

**Audited:** 2026-07-02, at commit `a458173` (branch `main`).
**Amended:** 2026-07-03 — second gap sweep added RF-7 (weather), alongside TF-10..TF-14 in the
sibling worldgen report.
**Amended:** 2026-07-03 — RF-1 extended with the effective-light query layer + subtractive shader
parity (§9–§10, `SkyDarken` model): stored skylight is time-invariant *sky exposure*; gameplay
reads derived effective light, never raw storage. Second pass: §3 gained the blue-moonlight
authoring rules (global sky tint is exact, brightness-in-curve/color-in-gradient split) and §4's
event tint changed from multiply to lerp/replace.
**Amended:** 2026-07-19 — cross-linked the new `CLOUD_RENDERING_IMPROVEMENTS_REPORT.md` (`CL-*`):
CL-2 absorbs RF-2 §5 (clouds tint); RF-7 §4's cloud knobs are received by CL-4.
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
- [`CLOUD_RENDERING_IMPROVEMENTS_REPORT.md`](CLOUD_RENDERING_IMPROVEMENTS_REPORT.md) (`CL-*`) —
  cloud-layer liveliness backlog: **CL-2 absorbs RF-2 §5** (clouds tint), and RF-7 §4's cloud
  color/density storm knobs are received by CL-4 there.

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
| RF-7 | Weather: no rain/snow of any kind; precipitation type gated on TF-3's temperature axis    |   🟡   |  🟡  |   🟡    |  ✅   |  ✅   |

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

**Storage semantics (important):** the stored 0–15 sky-light value is hereby defined as
**sky exposure** — a *time-invariant structural property* of the terrain, computed once by the
BFS and never mutated for time of day. At night a fully sky-exposed voxel still *stores* 15;
darkening is applied at read time (shader: §10; gameplay: §9). Gameplay systems (mob spawning,
plant growth, etc.) must therefore **never read raw skylight** for time-dependent decisions —
they read the §9 effective-light query. Two storage-mutating alternatives were evaluated and
rejected:

- *Full sunlight re-BFS at source `15 − N`:* dusk crosses ~15 discrete levels; each step is a
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
      sources, so tinting the sky channel via `SkyLightColor` produces the identical result that
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
   multiplier**: `SkyLightColor = lerp(gradientColor, _activeSkyEvent.tint, _activeSkyEvent.weight)`
   (identity when no event is active). A multiply (`SkyLightColor *= tint`) would compose with
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
8. **TF-4 tie-in:** dimensions with `hasSkyLight = false` ignore the time system and use their
   profile's `fixedGlobalLightLevel` (see the `TF-4` lighting-profile design).
9. **Effective-light query layer (gameplay reads — required):** `WorldTimeManager` exposes
   `int SkyDarken` in `[0, 11]` (**Minecraft parity**: 0 at day, 11 at deepest night → moonlight
   floor `15 − 11 = 4`), derived from the *same* curve that drives `globalLightLevel` — one
   source of truth, so rendering and gameplay can never disagree about how dark it is. Query
   helpers (next to `LightBitMapping`, or on `World`):
    - `GetEffectiveSkyLight(pos) = max(0, storedSkyLight − SkyDarken)`
    - `GetEffectiveLight(pos) = max(effectiveSkyLight, maxRGBBlocklightChannel)` — the value all
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
   growing the vertex). **Coordinate with TF-11 (climate foliage tint), which claims the RGB
   channels of the same stream** — together they exactly fill it; allocate before either ships.
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
   weather rerolls on load (Save ✅). If persistence is wanted later, one `level.dat` field rides
   the next migration bump (RF-1/TF-4/TF-12 coordination).
2. **Precipitation rendering:** a camera-following particle volume (GPU particles or a scrolling
   textured shell — prototype both; the shell is the mobile-safe option). **Under-cover culling**
   uses the existing highest-voxel heightmap (`GetHighestVoxel` path): sample the heightmap around
   the camera into a small texture each frame and discard precipitation fragments below it — no
   per-particle voxel queries.
3. **Type by climate:** at the camera position, sample the TF-9 Layer-2 temperature axis (with
   TF-11's altitude lapse) → rain vs. snow. Degrades gracefully pre-TF-3: a single global type
   toggle until the climate axis exists.
4. **Storm sky:** drive RF-1's event multiplier (`SkyLightColor` darkening) + RF-2 fog density +
   cloud plane color/density from the weather state — all existing or planned uniforms; zero
   lighting-engine contact (the BFS/per-voxel light is untouched, same shader-only contract as
   the blood moon).
5. **Out of scope for v1 (state explicitly):** snow-layer accumulation and ice formation as
   *block changes* (that is worldgen/tick territory — accumulation would need a budgeted behavior
   like RF-5's cap), lightning strikes, and gameplay effects (crop growth, mob behavior).

**Dependencies / ordering.** Rendering rides RF-1 (event tinting) + RF-2 (fog/sky) — build after
both. Precipitation-by-climate wants TF-3/TF-11 but degrades gracefully. Quality-tier gate the
particle cost (OM-1 budgets), like RF-3.

**Risks.** 🟡 — purely visual, but the under-cover culling and mobile particle cost need real
tuning; no pipeline, storage, or lighting-semantics contact. Seed ✅ / Save ✅ (v1 transient).

---

## Roadmap

See the **combined ranked roadmap** at the end of
[`WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md`](WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md) — RF items
rank: RF-1 (#1), RF-2 (#5), RF-7 (#17), RF-4 (#18), RF-3 (#19), RF-6 (#20), RF-5 (#21).
