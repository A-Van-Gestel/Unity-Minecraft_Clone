# Sky & Celestial Rendering

**Version:** 1.7  
**Date:** 2026-08-15  
**Status:** **Implemented (Stable)** — RF-2 phases 1 and 2, the `Distance Fog` setting, the richer sun/moon discs, and the Sky Editor are shipped and confirmed (2026-08-11, discs and tool 2026-08-12). Guarded by the `Validate Sky` suite (**15** baselines, model only) and `Validate Sky Render` (**8** baselines on rendered pixels) — see §7. Promoted from [`../Design/LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md`](../Design/LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md), whose RF-2 entry now carries only the deferred remainder.  
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
| `Assets/Editor/WorldTools/SkyMaterialCreator.cs` | `Minecraft Clone/Create Sky Material`. Owns the sky material's asset path. |
| `Assets/Editor/WorldTools/SkyGradientDefaults.cs` | `Minecraft Clone/Dev/Reset Sky Gradients To Code Defaults`. |
| `Assets/Editor/WorldTools/Libraries/SkyPreviewRenderer.cs` | Renders the skybox to a texture in edit mode, so sky work is judged by pixels rather than by a swatch (§8). |
| `Assets/Editor/WorldTools/SkyEditorWindow.cs` | `Minecraft Clone/Sky Editor` — authors the sky against a live render. |
| `Assets/Editor/Validation/Celestial/SkyRenderValidationSuite.cs` | `Validate Sky Render` — the shader half, asserted on rendered pixels. |

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

### 2.5 The gradients key dawn on the crossing; the curve keys it on the named time

`TimeOfDaySettings` carries two dawn constants, and the split is deliberate. `SUNRISE = 0.2083` is
Minecraft's named `/time` target (tick 23000) and shapes the **light curve**;
`DAWN_HORIZON_CROSSING = 0.25` is §2.2's true horizon crossing (tick 0) and keys the **gradients**.

They differ because Minecraft's named `sunrise` falls 1000 ticks *before* the sun actually rises, while
its named `sunset` (tick 12000) lands exactly on the crossing. The gradients originally inherited both
named times, so dusk got the crossing for free and dawn did not: the sky finished its sunrise while the
sun was still **10.55° below** the horizon, at 0.528 horizon luminance against 0.827 at noon — 82% of
full daylight, which read in game as the sky brightening for whatever happened to be near the horizon.

A **dawn/dusk mirror measurement** is what localized it — comparing day fraction `d` against `1 − d`,
since the sun's arc is symmetric about noon. It showed the asymmetry confined to the 0.15–0.2917 band and
the **global light level symmetric at those same instants**, which proved the defect lived in the gradient
keys rather than the curve, and kept the fix clear of RF-1's locked "curve in GLL units" decision. Moving
that one key restored an exact mirror (the other seven already matched) and cut the dawn/dusk luminance
delta at −10.55° from **+0.2242 to +0.0101**.

The residual 0.0174 at the crossing is **not** error: it is exactly the luminance difference between the
authored dawn and dusk colours. Dawn is pinker and cooler, dusk warmer and more orange — the two halves
mirror in *shape*, not in hue. `Gradient` permits only eight keys and all eight are used, so a new sky
moment costs an existing one; that ceiling is why the fix had to move a key rather than add one.

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

**The skybox is opt-out, and the flat background is its fallback — not dead code.**
`World.ApplySkyRenderSettings` returns early when `_skyMaterial` is unassigned, so the clear flags are
never switched and the camera keeps clearing to `TimeOfDaySettings._backgroundOverDay`; the field's
tooltip offers exactly that ("Leave empty to keep the flat background color"). The shipped `World.prefab`
does bind `Sky.mat`, which makes the background colour invisible **in the default configuration only** —
it still renders whenever the material is cleared, whenever `Camera.main` is null at `StartWorld`, after
teardown restores the scene's original flags, and under Unity's own degradation when a skybox material is
missing or its shader failed to compile. That last case is why the path is worth keeping: it is what
stands between a broken sky shader and an undefined screen. Consequence for authoring: the background
gradient must be kept in step with the zenith/horizon gradients (§2.5) rather than treated as legacy,
because it is what a player sees on that fallback.

`RenderSettings.ambientMode` is pinned to `Flat` while a world is live. Ambient light is skybox-derived
by default and this skybox changes every frame, which would re-bake the ambient probe continuously; the
block shaders read BFS light, not ambient, so pinning costs nothing visually. It is snapshotted and
restored on teardown alongside the skybox and clear flags, for the same reason those are: with domain
reload disabled a pinned mode would otherwise follow the user out of play mode and into the Scene view.

**Gradient falloff.** The horizon-to-zenith blend is `1 − (1 − |viewDir.y|)^3.5`, *not*
`|viewDir.y|^(1/2.2)`. Both concentrate colour near the horizon, but an exponent below 1 has **infinite
slope at zero** — it packs an eighth of the whole gradient into the first half-degree above the horizon
and renders as a hard bright line along the horizon. Measured at 0.29° elevation: the old form reached
0.090, the shipped form 0.017.

**The moon is opaque.** The disc composites by its mask alone, with a near-black night side; folding the
lit term into the mask made the unlit side transparent and let stars show *through* the moon. The
terminator is the correct **ellipse** `x > (1 − 2·phase)·√(1 − y²)`, which is what makes a quarter moon
read as a crescent rather than a half-disc. **Three** degeneracies are guarded, each a `normalize(0)`:
the disc centre; a sun collinear with the moon, at new and full; and — added later — world up collinear
with the moon at the **zenith**, which collapses the surface frame the markings live in. The third fires
only at *exact* collinearity (a ten-thousandth of a degree away is indistinguishable from any other
angle), but unguarded it flattens the disc to featureless grey, measured at a detail spread of 0.05
against a normal 0.35.

**Surface detail is procedural, never sampled.** Four analytic maria patches give the large-scale
structure; three octaves of value noise from the same hash the stars use perturb *which* terrain type a
point reads as, so the patch edges break up instead of staying clean circles; and a hashed crater field
over a 3×3 cell neighbourhood adds a darkened floor with a raised bright rim — a plain dark disc reads as
a stain, and it is the rim that reads as a crater. A texture fetch was rejected: at ~1.7° angular radius
it would buy an authored asset, a sampler in a `Background`-queue pass, and mip decisions, for detail
that no suite can check either way. All of it multiplies into the **lit** surface only, which is what
keeps the night side and the disc's opacity correct by construction rather than by retesting them.
The sun, previously a flat fill, has limb darkening with a warmer rim — the real effect is stronger at
short wavelengths, so the edge reddens rather than merely dimming.

**Atmosphere in front of the discs is one model in two halves.** The moon's own light is *extinguished*
by the horizon haze, and the sky's airlight is then *added* back. Together a fully hazed disc resolves to
exactly the sky beside it, so a low moon settles into the horizon instead of standing out against it —
and a daylight new moon disappears, which is what it does in reality, since the unlit side is seen
through the whole atmosphere and reflects nothing of its own. Applying these in the other order is a real
trap: haze blending toward the fog colour and airlight then added *on top* pays for the same air twice,
and measured, it lit a low moon to 1.24 against a 0.60 sky. The tell that the model is coherent is that
the unlit disc holds a **constant** ratio to the sky at every elevation.

`MOON_NIGHT_SIDE` is a stand-in for **earthshine**, exaggerated roughly 200× over the real thing so the
unlit side reads against a night sky, and it fades out as the sky brightens. The fade is keyed to sky
*luminance*, not to sun elevation: elevation is a poor proxy at exactly the wrong moment, because at
sunrise the sun sits near zero while the sky has already reached 0.5 luminance. What survives by day is a
deliberate slight **silhouette** — the disc carries a few percent less airlight than the open sky. That
direction is chosen, not incidental: darker-than-sky is noticed calmly, where brighter-than-sky demands
attention, and the daytime moon is meant to be a detail a player finds rather than one that announces
itself.

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

**`Minecraft Clone/Sky Editor`** is where that art is authored. It edits the four gradients and the six
celestial/fog scalars through `SerializedObject`, against a **live render of the real skybox shader** —
which is why it is a window and not a custom inspector. An Inspector alone cannot show what is being
authored here, because the swatch lies (§8); the render is the feature, and the fields are incidental.
Its time scrubber's *Sunrise* button targets day fraction 0.25, the celestial horizon crossing — which
is now also where the gradients key dawn (§2.5), the two having been one tick-offset apart until that
seam was closed.

**Moon phases are browsed by moving the clock, not by setting the phase.** A phase selector could simply
write `_MoonPhase`, and the state struct allows it — but phase and position come from one elongation
(§2.3), so that would paint a full moon beside the sun, a sky the engine cannot produce. Instead the
requested phase is *solved*: elongation is `2π · frac((days + epoch) / synodic)`, so a cycle fraction `u`
occurs exactly at `days = synodic · (m + u) − epoch` for any whole `m`. Only the choice of `m` is
searched, and it optimizes what the phase cannot determine — how high the moon rides, since a correct
phase below the horizon shows nothing. Measured across all eight named phases: exact on illumination, and
every one lands at the maximum altitude the latitude allows. A separate, explicitly labelled *Free Phase*
toggle does decouple the two, for studying the terminator, and warns that the result is not a real sky.

Two commands support it:

- `Minecraft Clone/Create Sky Material` — authors `Assets/Materials/Sky.mat` from the shader, so a
  fresh clone reproduces it without hand-wiring.
- `Minecraft Clone/Dev/Reset Sky Gradients To Code Defaults` — pushes the code-authored gradients into
  existing assets (see §8). It covers **all four** gradients, exactly what the Sky Editor can change, so
  "reset" undoes everything that tool touches and nothing else — the RF-1 light curve is deliberately
  outside both. Because it discards authored art across every asset at once, it confirms first, naming
  each file, and auto-accepts under `Application.isBatchMode` where a modal would hang a headless run.

`SkyPreviewRenderer` supports it from the other side: it renders the sky to a texture in edit mode, given
either a settings asset and a world time or a hand-authored `SkyPreviewState`. The struct exists so a
caller can build a sky the clock cannot produce — a moon parked at the zenith, a sky with no stars —
which is the only way the degenerate cases above are reachable at all. It is a **mirror** of
`World.PublishSkyGlobals`, in the same sense as `AtmosphericFog.EvaluateFogFactor` (§7): the game
publishes those globals from a private method on a live `World` that edit-mode tooling cannot reach, so a
global added there must be added here too.

Two properties of it are load-bearing. It renders to a **half-float, linear** target — the format, not
the `RenderTextureReadWrite` argument, is what keeps the round trip linear; dropping to 8-bit reproduces
the §8 colour-space lie inside the measuring tool itself, reading back an authored 0.075 as 0.302. And it
snapshots and restores every shader global it drives plus `RenderSettings.skybox`/`ambientMode` in a
`finally`, because all of that is process-wide and a preview would otherwise leave the user's Scene view
at whatever hour was last previewed.

It keeps **two** target pairs, and the distinction is the interesting part. `Render` produces the linear
half-float pair above, for measurement. `RenderForDisplay` produces an **8-bit sRGB** pair — the very
configuration that would corrupt a measurement — because there the GPU's linear-to-sRGB conversion on
write is exactly the work the GUI needs, and it is free. Converting per pixel in C# instead was measured
at **27 ms at 640×260 and 302 ms at 1920×900**, against 3 ms and 17 ms of actual rendering: the CPU
conversion, not the rendering, is what would force a preview to debounce rather than track a slider.

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

### 7.1 `Validate Sky Render` — the shader half

`Minecraft Clone/Dev/Validate Sky Render` — 8 baselines in
`Assets/Editor/Validation/Celestial/SkyRenderValidationSuite.cs`, the first coverage of the sky that
observes **pixels**. It renders through `SkyPreviewRenderer` and asserts: a linear color survives the
round trip (B1); the disc occludes the star field (B2); no degenerate configuration renders a NaN, and the
zenith moon keeps its surface detail (B3); the sun outshines the sky (B4); the gradient is the right way
up (B5); the unlit moon is a constant silhouette by day and still visible at night (B6); the lit moon
carries the sky's airlight at full strength at every elevation (B7); and the sky glows toward the sun
while that glow dies with it (B8, added with the sun aureole — see
[`../Design/SUN_APPEARANCE_IMPROVEMENTS.md`](../Design/SUN_APPEARANCE_IMPROVEMENTS.md) §7.1).

**B7 pins a trade rather than a correctness property.** The moon's airlight is added without being scaled
by the haze that models it (§4), so a lit daytime disc brightens with elevation — accepted, because
scaling it is what turns a daytime new moon into a hole punched in the sky. B6 measures phase 0 only,
where the disc's own reflectance drops out, so the lit half went unmeasured until B7. It is measured as a
differential against two sky brightnesses, which cancels the lunar surface constants and isolates the
airlight term: 0.941 overhead against 0.940 at the horizon today, collapsing to 0.017 overhead if the term
is ever haze-scaled.

**No reference images.** GPU output is not bit-reproducible across drivers, machines or engine versions,
so checked-in goldens would fail for reasons unrelated to the sky. Every assertion is instead a property
that must hold on any correct renderer — and each corresponds to a defect that actually happened here.

Under `-nographics` every scenario reports **INCONCLUSIVE** and passes, matching the meshing suite's
convention for a runtime that cannot measure. That is a real coverage hole in headless CI, stated rather
than hidden.

**Three of the first six baselines were wrong when first written, and only prove-red revealed it** — each
passed the very mutation it existed to catch. B2 sampled a region too small to contain any star, so
"unchanged" meant "nothing was there". B3 sampled a square box wider than the disc, so sky pixels
dominated its min/max and a collapsed surface frame was invisible. B6 ran with **fog disabled**, and since
the horizon haze is gated on a non-empty fog range, the correct and double-counting orderings were
literally the same expression. Predicting those mutations instead of running them would have shipped three
baselines that could never fail.

**Then it happened twice more, on the aureole work (2026-08-15).** B4 — "the sun disc is brighter than
the sky around it" — **passed a shipped regression that was obvious in a screenshot**, the sun rendering
as a hole in its own glow, while reporting `centre 0.9682 outshines sky 0.4803`. Three independent
fixture faults, any one sufficient: it built its state with `SkyPreviewState.Uniform`, which zeroes
`FogRange` and made the disc's haze term a **no-op** (the same fog-disabled trap as B6, met a second
time); its sun sat at mid elevation, where the haze it needed to exercise is weak; and it sampled the sky
at a **frame corner** rather than beside the disc, where the aureole has already fallen off. And B8's own
night assertion passed the twilight-fade mutation on its first draft, because probes placed at a fixed
elevation sit more than 90° from a sun 80° below the horizon, where `saturate(dot(view, sun))` is zero
whatever the fade does.

**The generalizable rule from all of this: place probes by TRUE ANGULAR ROTATION from the direction under
test, never by azimuth or elevation.** An azimuth step shrinks by cos(elevation), so near the poles a
nominal 3° probe covers almost no arc — during this work that put a probe *inside* the 1.5° sun disc and
reported the disc as though it were sky, the same class of error as the phantom limb ring in §8.
`AngularOffset` in the suite exists for exactly this and should be preferred by any new scenario.

What is still capture-verified only: whether the sky *looks* right. The suite pins invariants, not
aesthetics, and every visual defect in this system's history was caught by eye.

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

To measure rendered colour without entering play mode, use `SkyPreviewRenderer` (§6) — it is that recipe,
packaged with the linear round trip and the global save/restore already correct.

A third trap, from measuring rather than from authoring: **a probe over a block of disc pixels can be
measuring the sun.** At a new moon the sun is by definition collinear with the moon, and its feathered
mask covers a few pixels at the disc's dead centre even at a sub-pixel angular radius. A min/max over
that block reported a "limb ring" defect that did not exist, and re-running the same flawed probe against
the pre-change shader produced matching numbers that looked like confirmation. Print a pixel map before
believing a summary statistic.

Editor previews are unaffected by all of the above — `MeshPreviewWidget` and `BlockIconGenerator` use
`PreviewRenderUtility` with `CameraClearFlags.SolidColor`, so a global skybox cannot leak into them.

---

## 9. Deferred work

These live in the RF-2 entry of
[`../Design/LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md`](../Design/LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md):
sky colours in an editor tool with per-biome override (needs a design pass — sky colour is screen-wide
while biomes are per-column), §6 ambience v2 (aurora, shooting stars, sun flare), seasonal declination,
and the blood-moon disc tint that waits on RF-1 §4's unshipped `SkyEvent`.

**RF-9 interacts with this system.** The darker night sky RF-2 ships makes the crushed-AO defect *more*
visible, not less: at midnight a 30%-occluded face renders 14.8× darker than flat ground and identical
to a sealed cave face, where at noon the same pair differs by 1.5×. Fog masks it at distance only. Do
not read the atmospheric improvement as having fixed it.

---

## Document History

* **v1.7** - **The sun aureole and B8** (2026-08-15). SN-0 of
  [`../Design/SUN_APPEARANCE_IMPROVEMENTS.md`](../Design/SUN_APPEARANCE_IMPROVEMENTS.md) gave the sky a
  forward-scattered glow around the sun, which the elevation-only gradient of §3 could not express — the
  air beside the sun had rendered identically to the air 180 degrees away. §7.1 gains **B8** and, more
  importantly, two more false greens: **B4 passed a regression visible in a screenshot** for three
  independent fixture reasons, and B8's own night assertion passed its mutation on the first draft. The
  rule those produced is worth more than the phase — **place probes by true angular rotation, never by
  azimuth or elevation** — and is now stated in §7.1 for any future scenario. Also recorded there: the
  glow had to be a **blend** rather than an addition, because the authored sky beside the sun already
  occupies 0.78-0.88 of an LDR range with no tonemapper, leaving no headroom to add into.
* **v1.6** - **Review follow-ups: the ambient-mode restore and the lit moon's airlight** (2026-08-12).
  From a code review of the RF-2 commits. §4's teardown now restores `RenderSettings.ambientMode`
  alongside the skybox and clear flags — with domain reload disabled, a pinned `Flat` followed the user
  out of play mode and into the Scene view. New baseline **B7** (§7.1) pins the lit moon's airlight,
  the half of that model B6's phase 0 cannot observe. Two lessons worth carrying: the airlight's
  independence from haze is a **trade, not a defect**, and it was undocumented and unguarded until a
  reviewer computed the 3x elevation swing from the shader source; and a **differential** fixture (two
  sky brightnesses, same geometry) is what let B7 isolate that term without pinning the lunar surface
  constants, so re-tuning the moon's albedo cannot false-red it.
* **v1.5** - **§4 records that the flat background is a fallback, not vestigial code** (2026-08-12).
  Traced while checking whether `World.cs`'s per-frame `_playerCamera.backgroundColor` assignment had
  been superseded by the skybox. It has not: `_skyMaterial` is an advertised opt-out, and the background
  colour is also what Unity falls back to when a skybox material is missing or its shader failed. The
  suspicion that prompted the trace — that the assignment was now dead — was **wrong**, and it is
  recorded because the wrong conclusion is the tempting one: the field looks superseded from the call
  site, and deleting it would have removed a documented option and the degradation path that keeps a
  broken sky shader from rendering an undefined screen. It also retroactively justifies moving the
  background gradient's dawn key together with the sky gradients in v1.4 (§2.5).
* **v1.4** - **The dawn/sun seam closed** (2026-08-12). New §2.5: the gradients now key dawn on the
  celestial crossing (`DAWN_HORIZON_CROSSING = 0.25`) while the light curve keeps Minecraft's named
  `/time` target (`SUNRISE = 0.2083`), which restores an exact dawn/dusk mirror and cuts the luminance
  delta at −10.55° sun altitude from +0.2242 to +0.0101. The §9 deferred entry is retired. Two things
  are worth carrying forward. The **dawn/dusk mirror measurement** is the technique that made a vague
  "the sky brightens too early" into a one-key defect with a bounded blast radius — comparing a quantity
  against its own reflection localizes an asymmetry without needing a reference to be right about.
  And the World Clock suite re-ran **bit-identical**, which was the expected result precisely because
  that suite builds its settings with `CreateInstance` and reads only the curve: it is *structurally
  blind* to gradients, so its green proved no regression and said nothing whatever about the fix. The
  fix was judged by rendered pixels, as everything else in this document has been.
* **v1.3** - **Moon phase browsing and the first rendered-pixel coverage** (2026-08-12). The Sky Editor
  gained a phase selector that moves the *clock* rather than writing the phase, keeping position and phase
  on the one elongation §2.3 describes — measured exact on illumination at all eight phases, each at the
  maximum altitude the latitude allows — plus an opt-in Free Phase override that says it is unphysical.
  New §7.1: `Validate Sky Render`, 6 baselines on rendered pixels, taking `Validate All` to **21 suites /
  475 baselines**. It records that **three of those six baselines passed the mutation they existed to
  catch** until prove-red exposed them, which is the strongest argument in this document for running
  mutations rather than predicting them.
* **v1.2** - **Sky Editor shipped** (2026-08-12): the sky's colours are now authored against a live render
  rather than through Inspector swatches that misreport them by a factor of four. §6 rewritten around it;
  the reset command widened from two gradients to all four so it undoes exactly what the tool can change,
  and gained a confirmation naming each asset (batch-mode exempt). §1 lists the window. The renderer gained
  a second, deliberately **sRGB** target pair for display — the configuration that is wrong for measurement
  is the right one for the GUI, and moving that conversion to the GPU took the preview from 27 ms to 1.9 ms
  at 640×260, which is the difference between a debounced preview and one that tracks a slider. Backlog IDs
  were also removed from user-facing strings in this system (an `RF-2` header reached the tool's own UI).
* **v1.1** - Richer sun and moon discs shipped and confirmed in game (2026-08-12): procedural craters and
  mottling, sun limb darkening, and a **third** degeneracy guard for the moon's surface frame at the
  zenith — §4 had claimed both were guarded. §4 also gained the atmosphere model the in-game passes
  forced into existence: extinction of the disc's own light plus additive airlight, as one model rather
  than two, after the original haze-then-add order was found to pay for the same air twice and light a low
  moon to 1.24 against a 0.60 sky. Earthshine now fades by sky luminance rather than sun elevation, since
  elevation is a poor proxy at sunrise. `SkyPreviewRenderer` added (§1, §6) — edit-mode rendered pixels,
  which is what makes every number in §4 measured rather than argued; §7 records that no baseline observes
  one yet. §8 gained a third trap: a summary statistic over disc pixels can be measuring the sun, which
  produced a confidently-reported defect that did not exist. §9 gained the dawn-runs-ahead-of-the-sun
  finding.
* **v1.0** - Promoted from the RF-2 Design entry (2026-08-11) after phases 1 and 2 and the
  `Distance Fog` setting shipped and were confirmed in game. Records the equinox model and why
  declination is pinned, the one-elongation moon and its first-night epoch, the own-globals fog
  decision and its three consequences, the `Light`-steepens-the-curve rule, and the two authoring traps
  (ScriptableObject initializers vs. existing assets; linear colour space vs. `SetGlobalColor`).
