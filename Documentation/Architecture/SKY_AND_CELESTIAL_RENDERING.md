# Sky & Celestial Rendering

**Version:** 1.0  
**Date:** 2026-08-11  
**Status:** **Implemented (Stable)** — RF-2 phases 1 and 2 plus the `Distance Fog` setting are shipped and in-game confirmed (2026-08-11). Guarded by the `Validate Sky` suite (**15** baselines). Promoted from [`../Design/LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md`](../Design/LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md), whose RF-2 entry now carries only the deferred remainder.  
**Target:** Unity 6.5 (Mono for dev; IL2CPP for production)

> The procedural sky: a zenith/horizon gradient, a sun and moon on **real celestial arcs** driven by a
> small C# simulation, a star field that rides the same celestial sphere, and distance fog that conceals
> the loaded-chunk boundary. **The pivotal decision: every time-varying quantity is computed in plain,
> testable C# and published as a shader global — the shaders own no state and derive no time.** That is
> what makes an unobservable subsystem (nothing here can be asserted by a validation suite once it
> reaches the GPU) still carry 15 baselines: the suite tests the model, and only the picture is
> capture-verified.

**Relationship to other documents:**

- [`SMOOTH_AND_RGB_LIGHTING.md`](SMOOTH_AND_RGB_LIGHTING.md) — the RGB light engine this renders
  alongside. The sky never touches voxel light.
- [`LIGHTING_SYSTEM_OVERVIEW.md`](LIGHTING_SYSTEM_OVERVIEW.md) — the BFS that computes the sky
  *exposure* the day/night cycle darkens at read time.
- [`../Design/LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md`](../Design/LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md)
  — RF-1 (the world clock, shipped) and the RF-2 remainder still open.
- [`DATA_DRIVEN_SETTINGS_UI.md`](DATA_DRIVEN_SETTINGS_UI.md) — how `Distance Fog` surfaces in the menu.

---

## 1. Components

| File | Role |
|------|------|
| `Assets/Scripts/Sky/CelestialMath.cs` | Pure static celestial model. No Unity objects, no state. |
| `Assets/Scripts/Sky/AtmosphericFog.cs` | Pure static fog range/curve + the `FogStyle` enum. |
| `Assets/Scripts/WorldTimeManager.cs` | Owns world time (RF-1); exposes the derived celestial properties. |
| `Assets/Scripts/World.cs` | `PublishSkyGlobals()` / `PublishFogGlobals()`; camera and `RenderSettings` wiring. |
| `Assets/Scripts/Data/WorldTypes/TimeOfDaySettings.cs` | The authored sky asset, linked from `WorldTypeDefinition`. |
| `Assets/Shaders/SkyboxShader.shader` | Gradient, sun, moon, stars. |
| `Assets/Shaders/Includes/VoxelFog.hlsl` | Shared fog, included by the block, transparent and liquid shaders. |
| `Assets/Editor/WorldTools/SkyMaterialCreator.cs` | `Minecraft Clone/Create Sky Material`. |
| `Assets/Editor/WorldTools/SkyGradientDefaults.cs` | `Minecraft Clone/Dev/Reset Sky Gradients To Code Defaults`. |

---

## 2. The celestial model

### 2.1 Equinox model, and why declination is pinned at zero

Solar declination is a named constant fixed at 0 (`CelestialMath.SolarDeclinationRadians`), which fixes
day length at exactly half a cycle year-round. This is a **deliberate architectural choice, not an
unimplemented feature**:

RF-1's light curve is authored against `DayFraction` and is the single source for both
`GlobalLightLevel` and gameplay's `SkyDarken`. A seasonally varying declination would move sunrise away
from the time the curve brightens at — in winter the sun disc would sit below the horizon while the
world was still lit. Reconciling that needs either a curve remap (which moves the World Clock suite's
B3/B4/B7/B9) or altitude-driven brightness (which re-opens RF-1's locked "curve in GLL units"
decision). Declination enters the horizon equations at exactly one place, so seasons remain a
one-parameter change here plus a decision there.

### 2.2 Horizon geometry

With declination zero the equatorial-to-horizon transform collapses to four trig calls. For hour angle
`H = (DayFraction − 0.5)·2π` and latitude `φ`:

```
sunDir = ( −sin H ,  cos φ · cos H ,  −sin φ · cos H )     // (+x east, +y up, +z north)
```

Unit length is structural — the components are direction cosines of a point on the celestial equator —
so it holds at the poles with no guard. Behaviour that falls out rather than being authored:

| Time | Direction | Reads as |
|------|-----------|----------|
| `DayFraction` 0.25 | `(1, 0, 0)` | Sunrise, due east, every latitude |
| 0.50, φ = 45°N | `(0, 0.707, −0.707)` | Noon, up and due **south** |
| 0.75 | `(−1, 0, 0)` | Sunset, due west |
| Noon altitude | — | Exactly `90° − |φ|` |

**Coordinate space.** Every direction is a unit vector in Unity render space. These are *directions,
not positions*, so the floating-origin shift (`WorldOrigin`) does not apply — see
[`../Guides/COORDINATE_SPACES_GUIDE.md`](../Guides/COORDINATE_SPACES_GUIDE.md).

### 2.3 The moon is one model, not two

Position and phase both derive from a single elongation angle
`E = 2π · frac((days + MoonPhaseEpochDays) / SynodicDays)`; the moon rides the same horizon formula at
hour angle `H − E`, and its lit fraction is `(1 − cos E)/2`.

Because phase *is* elongation, the classic couplings are structural rather than tuned: a full moon
necessarily peaks at midnight, a new moon at noon, and moonrise necessarily slips one synodic fraction
of a day per day (measured: successive moon peaks 1.035053 days apart against a theoretical 1.035050).
The identity `illuminatedFraction == (1 − dot(sunDir, moonDir)) / 2` holds exactly, and B9 asserts it.

**`MoonPhaseEpochDays = SynodicDays/2 − 1`** places a **full moon on the world's first night**
(Minecraft parity). Without it the cycle starts on a new moon — which is correctly beside the sun, so
up by day and below the horizon all night, leaving a fresh world with no visible moon for ~10 nights.
That shipped once and read as a bug despite the geometry being right; B13 now pins it.

### 2.4 The star field rides the same sphere

`SkyRotation(dayFraction, latitude)` is a rotation about the celestial pole `(0, sin φ, cos φ)` by the
hour angle. The sun *is* that rotation applied to the noon direction — B11 asserts it — so the stars and
the sun are one sphere rather than two effects that merely look similar. Stars are sampled in celestial
space, so the field turns overhead instead of being pinned to the world.

One simplification: the sphere turns once per **solar** day rather than per sidereal day, so the star
field does not slowly precess against the calendar over a long-lived world.

---

## 3. The globals contract

`World.PublishSkyGlobals()` runs every frame from `SetGlobalLightValue()`. It **early-outs when
`TimeManager` or the settings asset is null** — edit-mode fixtures construct a `World` without
`StartWorld`, and a half-published sky (a stale sun direction against a fresh horizon colour) would be
worse than the shaders' own defaults.

| Global | Type | Source |
|--------|------|--------|
| `_SunDirection` | `float3` | `WorldTimeManager.SunDirection` |
| `_MoonDirection` | `float3` | `WorldTimeManager.MoonDirection` |
| `_MoonPhase` | `float` | Lit fraction, 0 = new, 1 = full |
| `_SkyRotation` | `float4x4` | Celestial sphere orientation |
| `_ZenithColor` / `_HorizonColor` | `half3` | `TimeOfDaySettings` gradients at `DayFraction` |
| `_SunAngularRadius` / `_MoonAngularRadius` | `float` | Authored, degrees |
| `_StarBrightness` | `float` | Authored |
| `_VoxelFogRange` | `float4` | `(start, end, curveExponent, 0)` |
| `_VoxelFogColor` | `half3` | The horizon colour |

`WorldTimeManager.ContinuousDays` is defined so its fractional part is exactly `DayFraction`. That is
what keeps the moon's phase and the sun's position on one clock.

---

## 4. The skybox shader

Camera clear flags and `RenderSettings.skybox` are set **from code** at `StartWorld`, never by editing
`World.unity`. Both are captured and restored in `OnDestroy` — they are *scene* state, and a leaked
skybox would follow the user into the Scene view.

`RenderSettings.ambientMode` is pinned to `Flat`. Ambient light is skybox-derived by default and this
skybox changes every frame, which would re-bake the ambient probe continuously; the block shaders read
BFS light, not ambient, so pinning costs nothing visually.

**Gradient falloff.** The horizon-to-zenith blend is `1 − (1 − |viewDir.y|)^3.5`, *not*
`|viewDir.y|^(1/2.2)`. Both concentrate colour near the horizon, but an exponent below 1 has **infinite
slope at zero** — it packs an eighth of the whole gradient into the first half-degree above the horizon
and renders as a hard bright line along the horizon. Measured at 0.29° elevation: the old form reached
0.090, the shipped form 0.017.

**The moon is opaque.** The disc composites by its mask alone, with a near-black night side; folding the
lit term into the mask made the unlit side transparent and let stars show *through* the moon. The
terminator is the correct **ellipse** `x > (1 − 2·phase)·√(1 − y²)`, which is what makes a quarter moon
read as a crescent rather than a half-disc. Both degeneracies are guarded: the disc centre and, at new
moon, a sun collinear with the moon would each `normalize(0)` into a NaN.

**Stars are points, not cells.** `floor(dir · density)` lighting a whole cell paints axis-aligned
squares; each star is a jittered point inside its cell with a smooth radial falloff.

**Discs are hazed toward the fog colour** by view elevation, gated on `_VoxelFogRange` being non-empty.
Without it the sun and moon read as sitting *in front of* the fog, since the sky draws behind everything.

---

## 5. Distance fog

### 5.1 Why this is not Unity's fog

The engine drives its own fog through `_VoxelFogRange` / `_VoxelFogColor` rather than
`RenderSettings.fog`, for three reasons:

1. **No shader variants.** `multi_compile_fog` would multiply every block/liquid variant — the cost
   RF-2 §4 flagged for the `GS-4` audit. That concern is dissolved, not deferred.
2. **No scene state.** `RenderSettings` is serialized into the scene; driving it at runtime risks
   leaking fog into the editor's Scene view.
3. **Safe by default.** A zero-width range means *fog off*, which is exactly what uninitialized globals
   give — so editor preview shaders and anything that never receives these render **unfogged** rather
   than solid fog colour.

### 5.2 Horizontal, curved, and always complete

Distance is **horizontal (XZ) only**. The boundary the fog conceals is the loaded-chunk radius, which is
itself horizontal, so matching it is the accurate model — and as a direct consequence, climbing does not
fog the ground directly below the player, which full 3D distance did.

Accumulation is **back-loaded**: `factor = pow(t, curveExponent)`, default exponent 3, starting at 0.15
of the fog end. A linear ramp spreads 0→1 evenly over a short band, so anything large enough to span
that band — a mountain — gets the ramp painted across its face as a visible gradient. The fix was not
less fog but an earlier, softer start; the artifact was the hard *onset*, not the density.

`FogEndFraction = 0.92` keeps full opacity **inside** the loaded radius. Fog that completed at or beyond
the edge would let the player watch terrain end against clear sky, which is the whole point of having it.

### 5.3 The `Distance Fog` setting

`FogStyle` is `Off` / `Light` / `Full`, surfaced on the Graphics tab through the reflection-based
settings UI.

**`Light` doubles the curve exponent; it does not scale opacity.** Because `t^p` equals 1 at `t = 1` for
every exponent, full concealment at the boundary is preserved *by construction*. A strength slider was
considered and rejected for exactly this reason: any level capping terminal opacity below 1 leaves
terrain visibly ending against open sky — reintroducing the artifact fog exists to hide.

---

## 6. Authoring surface

All sky art lives on the `TimeOfDaySettings` asset, linked from `WorldTypeDefinition`, so a world type
(and later a dimension) ships its own sky: observer latitude, zenith/horizon gradients, sun and moon
angular radii, star brightness, fog start fraction and fog curve power.

Two editor commands support it:

- `Minecraft Clone/Create Sky Material` — authors `Assets/Materials/Sky.mat` from the shader, so a
  fresh clone reproduces it without hand-wiring.
- `Minecraft Clone/Dev/Reset Sky Gradients To Code Defaults` — pushes the code-authored gradients into
  existing assets (see §8).

---

## 7. Validation coverage

`Minecraft Clone/Dev/Validate Sky` — 15 baselines (B1–B15) in
`Assets/Editor/Validation/Celestial/SkyValidationSuite.cs`.

> The validation namespace is `Editor.Validation.**Celestial**`, deliberately not `...Sky` — the latter
> would shadow the `Sky` namespace it references.

Coverage: unit/finite directions across seven latitudes including both poles (B1); horizon crossings
(B2); sunrise due east and sunset due west (B3); noon altitude against hand-computed `90° − |φ|` (B4);
monotonic arc (B5); **agreement with RF-1's `SunElevation` about whether the sun is up** (B6); moon lag
by numerically located peaks (B7); phase cycle (B8); **phase/position consistency via the dot-product
identity** (B9); full-moon-at-midnight and new-moon-at-noon (B10); rigid daily sky rotation that carries
the sun (B11); purity under reverse-order re-evaluation (B12); **full moon on the first night** (B13);
fog range and back-loaded curve shape (B14); fog levels all still concealing the boundary (B15).

**What this suite cannot do.** Nothing here observes the skybox shader or the fog shader. It proves the
sun goes where the model says; that it *looks* right is capture-verified only — the same limitation
RF-1 §10's subtractive sky term carries. `AtmosphericFog.EvaluateFogFactor` is a deliberate C# **mirror**
of `VoxelFog.hlsl`'s `VoxelFogFactor`; changing one without the other silently desynchronizes them, and
only the C# half is guarded.

---

## 8. Traps for future work

Both of these cost real debugging time during RF-2 and will recur:

- **A `ScriptableObject` field initializer never touches an existing `.asset`.** Initializers run only
  when an instance is *created*; an asset that already exists keeps whatever was serialized into it.
  Editing a default in code therefore has **no effect in game**, and a probe built on
  `ScriptableObject.CreateInstance` will show the code default and mislead you. Use the
  `Reset Sky Gradients To Code Defaults` command, or push the field explicitly via `SerializedObject`.
- **The project renders in linear colour space and `Shader.SetGlobalColor` does not convert.** Values
  declared as bare globals (outside a `Properties` block) are consumed as *linear*, so an authored 0.075
  reaches the screen at roughly sRGB 0.30 — four times brighter than the Inspector swatch. **The sky
  gradients are authored in linear values**; judge them by the render, never by the swatch.

To measure rendered colour without entering play mode: create a temporary camera and `RenderTexture` in
edit mode, set `RenderSettings.skybox`, push the globals, `cam.Render()`, and `ReadPixels`.

Editor previews are unaffected by all of the above — `MeshPreviewWidget` and `BlockIconGenerator` use
`PreviewRenderUtility` with `CameraClearFlags.SolidColor`, so a global skybox cannot leak into them.

---

## 9. Deferred work

These live in the RF-2 entry of
[`../Design/LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md`](../Design/LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md):
a richer moon shader, sky colours in an editor tool with per-biome override (needs a design pass — sky
colour is screen-wide while biomes are per-column), §6 ambience v2 (aurora, shooting stars, sun flare),
seasonal declination, and the blood-moon disc tint that waits on RF-1 §4's unshipped `SkyEvent`.

**RF-9 interacts with this system.** The darker night sky RF-2 ships makes the crushed-AO defect *more*
visible, not less: at midnight a 30%-occluded face renders 14.8× darker than flat ground and identical
to a sealed cave face, where at noon the same pair differs by 1.5×. Fog masks it at distance only. Do
not read the atmospheric improvement as having fixed it.

---

## Document History

* **v1.0** - Promoted from the RF-2 Design entry (2026-08-11) after phases 1 and 2 and the
  `Distance Fog` setting shipped and were confirmed in game. Records the equinox model and why
  declination is pinned, the one-elongation moon and its first-night epoch, the own-globals fog
  decision and its three consequences, the `Light`-steepens-the-curve rule, and the two authoring traps
  (ScriptableObject initializers vs. existing assets; linear colour space vs. `SetGlobalColor`).
