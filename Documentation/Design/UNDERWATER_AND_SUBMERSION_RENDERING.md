# Underwater & Submersion Rendering (UW-*)

**Version:** 2.4  
**Date:** 2026-09-05  
**Status:** Partially implemented — UW-0 through UW-4 all shipped and confirmed in game 2026-09-04, the overlay after eight in-game passes and accepted as a **proxy** whose remaining imprecision is owned by `VX-3`/`VX-5` (§3.2). **UW-5 was built as a screen-space band on 2026-09-05, failed its in-game pass, was reverted whole and is ⏸️ paused on cost/benefit at current priority** — not abandoned: §3.6 records why the band cannot work at all, and what a mesh-displacement version would have to answer when it is picked up. **UW-6 is unblocked and is the only phase active**, with nothing gating it.  
**Target:** Unity 6.6 (Mono for dev; IL2CPP for production)

> Closes the last open bullet of `FLUID_BUGS` **#02** — the one the 2026-09-03 physics ship left
> behind: a submerged player gets no visual signal at all. Three separate defects hide under that
> sentence: the liquid pass never renders from **inside** a fluid body, there is no screen-space
> **medium** (tint + fog) while the eye is under a surface, and there is no **waterline** when the
> eye sits at one. **The decision this document settles: submersion becomes one shared, sub-cell,
> surface-height-aware query — `World.GatherEyeSubmersion` — and both the new overlay pass and the
> already-shipped ambience low-pass filter read it, so what the player sees and what they hear
> switch on the same block boundary.** The overlay is a URP `ScriptableRendererFeature` that fogs
> exponentially against `_CameraDepthTexture` — per pixel, over the part of each ray that lies below
> the surface, so a partly submerged view splits rather than switching wholesale; the backface fix is
> one `Cull Off` line, safe because the liquid fragment reads its normal only through `abs()`.

**Audited:** 2026-09-03, at commit `356329eb` (branch `feat/fluid-physics`).
Findings are from static review of `UberLiquidShader.shader`, `Includes/LiquidCore.hlsl`,
`Includes/VoxelFog.hlsl`, `Helpers/VoxelMeshHelper.GenerateFluidMeshData`,
`World.GatherFluidContact` / `SetGlobalLightValue` / `PublishFogGlobals`,
`Physics/FluidContact.cs`, `Physics/FluidContactResolver.cs`, `Rendering/UIBlurRendererFeature.cs`,
`Audio/AmbienceResolution.cs`, `Audio/SoundManager.cs:385-425`, `SectionRenderer.cs`,
`UI/GraphicsSettingsController.cs`, `Data/BlockType.cs`, the URP asset/renderer assets under
`Assets/settings/Rendering/`, and the `UIBlur` / `Celestial` / `Meshing` validation suites.
Render state was read off the shader source (the pass declares no `Cull`, `Blend` or `ZWrite`, so
Unity's defaults apply); no runtime capture was taken.

**Relationship to other documents:**

- [`../Bugs/FLUID_BUGS.md`](../Bugs/FLUID_BUGS.md) — **#02**, whose remaining bullet this design
  closes. The entry is archived by `archive-fixed-bug` only after UW-6's in-game confirmation.
- [`../Architecture/FLUID_SHORELINE_RENDERING.md`](../Architecture/FLUID_SHORELINE_RENDERING.md) —
  owns the liquid shader's vertex-channel contract and shore math. UW-1 changes that shader's
  render state and nothing else; the channel layout is untouched.
- [`../Architecture/SUB_VOXEL_COLLISION_SYSTEM.md`](../Architecture/SUB_VOXEL_COLLISION_SYSTEM.md) —
  §7 owns `FluidContact`, the *body*-AABB fluid query. UW-2 adds a sibling *eye* query; the two
  deliberately do not share a surface-height source (§3.4).
- [`../Architecture/SKY_AND_CELESTIAL_RENDERING.md`](../Architecture/SKY_AND_CELESTIAL_RENDERING.md) —
  owns `VoxelFog.hlsl` and `World.SetGlobalLightValue`, the every-frame shader-global publish point
  UW-4 extends.
- [`SOUND_ENGINE_DESIGN.md`](SOUND_ENGINE_DESIGN.md) — owns `AmbienceResolution.IsSubmerged`, the
  existing per-cell submersion test that UW-3 replaces with the shared query.

---

## 1. Goals & non-goals

### Goals

1. **A fluid body is visible from inside it.** Looking up while submerged shows the surface, not
   the sky.
2. **Submersion reads as a medium, not a filter.** A tint plus distance-attenuating fog in the
   fluid's own color, so the seafloor fades and lava is near-opaque within about a block.
3. **One authoritative answer to "is the eye submerged".** Rendering and audio consume the same
   sub-cell query, so the tint and the low-pass filter engage together.
4. **A waterline when the eye is at the surface.** The screen splits along the fluid plane, with a
   meniscus band, and the split tracks camera pitch and roll.
   ⏸️ **Met in half; the other half is paused, not dropped (2026-09-05).** UW-4's per-pixel solve
   delivers the split, geometrically exact and tracking pitch and roll. The **meniscus band is not
   built** — softening that edge turns out to require animating the liquid mesh rather than the overlay
   (§3.6), which is more than the polish is worth at current priority. The boundary stays hard until
   UW-5 is picked back up.
5. **Per-fluid authoring.** Water and lava differ by authored values on `BlockType`, tuned in the
   `BlockEditor`, exactly as the 2026-09-03 physics coefficients are.

### Non-goals (v1)

- **Per-fluid screen distortion** (water wobble, lava heat shimmer) — planned as a **v2
  extension**, see the §7 extension roadmap. The authoring hooks (`_DistortionAmount`,
  `_HeatDistortionAmount`, already scaled by `GraphicsSettingsController`) are reserved for it.
  ⚠️ Not the same thing as UW-5's **waterline** wobble, which moves where the water's edge *is* rather
  than resampling the image. The two shared one word until 2026-09-05 and the ambiguity cost a build —
  §3.6.
- **Caustics, god rays, bubble trails, surface splash particles** — `FLUID_BUGS` **#15** owns
  fluid particles and audio; this design must not grow a second particle path.
- **Blending two fluid layers.** The liquid pass writes opaquely and composites against
  `_CameraOpaqueTexture`, so a water surface seen through another water surface already shows one
  layer only. That is pre-existing and out of scope; §8 records it as an accepted limitation.
- **Screen-space refraction of the waterline itself** — the meniscus is a color band, not a
  refracting lens.

---

## 2. Current state (what exists today)

| Area                        | State                                                                                                                                                                                                                                                            |
|-----------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Liquid render state         | The SubShader is tagged `Queue="Transparent"`, and `LiquidForward` declared **no `Cull`, no `Blend`, no `ZWrite`** → Unity defaults: `Cull Back`, opaque write, `ZWrite On`. `Cull Back` was the root cause of goal 1 (fixed by UW-1). The pass self-composites against `_CameraOpaqueTexture`. **The transparent queue matters beyond sorting:** with `ZWrite On` it puts the fluid surface into `_CameraDepthTexture` under this project's copy mode — see the URP row. |
| Liquid normal use           | `LiquidCore.hlsl` reads `worldNormal` in exactly two places — `GetShoreData` and `RouteFlowTo3D` — and **both take `abs()` first**. A back-facing (negated) normal is therefore a no-op through the whole fragment. `LiquidV2F` uses 11 of 15 interpolators.       |
| Fluid face emission         | `VoxelMeshHelper.GenerateFluidMeshData:1044/1117/1298` — top face unless the same fluid is above; bottom only over transparent, non-same-fluid; sides culled against effectively-full-height same-fluid neighbors. A submerged camera sits inside a **shell whose faces all point away from it**: the geometry exists, back-face culling hides it. |
| Corner surface height       | `GetSmoothedCornerHeight` averages the cell with up to three same-fluid neighbors, then the caller forces all four corners to `1.0` when fluid is above and clamps to `kMinFluidSurfaceHeight` (0.005). Vertices land at the cell's four XZ corners; the rasterizer interpolates between them. `private static` today. |
| Body fluid query            | `World.GatherFluidContact:4910` scans the body AABB for the highest overlapping surface, using `FluidContactResolver.SurfaceHeight` — the **logical per-cell template**, deliberately *not* the smoothed height, so a body's waterline does not depend on neighbor smoothing. Guarded on `FluidVertexTemplates`/`JobDataManager` disposal. |
| Eye/head submersion         | **Already exists, in audio.** `SoundManager.cs:409` resolves the listener's head cell from `Camera.main.transform` via `WorldOrigin.UnityToVoxelCell` at ~4 Hz and calls `AmbienceResolution.IsSubmerged`, a **per-cell** `fluidType != None` test. Its own docstring states the consequence: *"a head just under a partly-filled surface reads dry until it enters the cell below."* |
| Shader-global publish point | `World.SetGlobalLightValue` → `PublishSkyGlobals` → `PublishFogGlobals`, every frame, already null-guarded for edit-mode fixtures that build a `World` without `StartWorld`. `_playerCamera = Camera.main` (`World.cs:783`).                                       |
| Distance fog                | `VoxelFog.hlsl` — **horizontal (XZ) radial**, back-loaded by `pow(t, exponent)`, explicitly chosen to conceal the loaded-chunk radius without dissolving the ground under a flying player. A zero-width range reads as fog-off, which is what uninitialized globals give. |
| URP configuration           | `VoxelEngine-URP-Asset.asset`: `m_RequireDepthTexture: 1`, `m_RequireOpaqueTexture: 1`, `m_OpaqueDownsampling: 1`, `m_MSAA: 2`, HDR on. Depth **and** opaque are available to a fullscreen pass. `GraphicsSettingsController` mutates render scale and MSAA at runtime, so neither is a fixed property of the build. |
| Depth copy timing           | `VoxelEngine-URP-Renderer.asset`: **`m_CopyDepthMode: 1` = `AfterTransparents`** (`UniversalRendererData.cs:16-24`). URP schedules the copy as late as it can while still preceding the earliest depth reader (`UniversalRendererRenderGraph.cs:946-978`, `ScriptableRenderer.cs:1015-1023`), so a pass at `AfterRenderingTransparents` reads valid depth — **provided it declares `ConfigureInput(ScriptableRenderPassInput.Depth)`**. The consequence for UW-4 is that `_CameraDepthTexture` **contains transparent geometry, the liquid surface included**. |
| Renderer features           | `UIBlurRendererFeature` is the **only** one, RenderGraph-based, listed in `VoxelEngine-URP-Renderer.asset`'s `m_RendererFeatures`. Its `Create()` is documented as idempotent across domain reload and inspector edits. It runs at **`AfterRenderingTransparents`** and samples `resourceData.activeColorTexture` — the same injection point UW-4 wants, so list order decides which sees the other's output (§3.5). |
| Post-processing             | `GraphicsSettingsController.ApplyBloom:171` sets `data.renderPostProcessing = enabled && FindAnyObjectByType<Volume>() != null` — post-processing is **off** whenever bloom is off or no Volume exists.                                                            |
| Liquid material             | `World.LiquidMaterial => _blockDatabase.liquidMaterial` — a **shared project asset**, already mutated at runtime by `GraphicsSettingsController` (quality keywords, refraction keyword, distortion floats).                                                        |
| Per-fluid authoring slot    | `BlockType.cs`, `[Header("Fluid Properties")]`: `buoyancy`, `verticalDrag`, `submergedSpeedMultiplier`, `pushStrength`, `swimAscendSpeed` — all **`public` fields**. As of 2026-09-03 none of them had `BlockEditor` UI; they were tuned in the raw Inspector on `BlockDatabase.asset`. UW-0 gave all seven fluid coefficients sliders in the `BlockEditor` and closed the matching gap in `DuplicateSelectedBlock`, which silently dropped every one of them. |
| Render-suite precedent      | `UIBlurRenderValidationSuite` + `SkyRenderValidationSuite` render a shader in edit mode and assert **arithmetic**, never checked-in golden images ("GPU output is not bit-reproducible across drivers"), and report **INCONCLUSIVE** under `-nographics`. Both are listed in `ValidationSuiteRegistry`. |

---

## 3. Decisions

### 3.1 How the liquid pass becomes visible from inside

#### Option A — `Cull Off`, unconditional ✅ **CHOSEN**

One line in `UberLiquidShader.shader`'s pass. It is safe *because of a property the shader already
has*: the fragment's only two readers of `worldNormal` (`GetShoreData`'s axis routing and
`RouteFlowTo3D`) both apply `abs()` before use, so a negated normal changes nothing downstream.
Shore mask decoding, flow routing, lighting (which reads vertex `lightData`, not the normal) and
fog are all unaffected. **No fragment code changes at all.**

Correct layering survives because the pass writes opaquely with `ZWrite On`: when a front and a
back face of the same body both cover a pixel, the depth test keeps the nearer one outright rather
than blending them, which is the behavior already in place today. The cost is raster setup on
fluid triangles that were previously discarded at the culling stage; the fragments themselves are
mostly early-Z rejected, since the pass contains no `clip`/`discard` and writes no depth from the
fragment.

#### Option B — `Cull [_LiquidCull]`, toggled from C# (rejected)

- ✅ Genuinely free when the eye is dry — the state only flips on submersion.
- ❌ **Render state would live on a shared project asset.** `World.LiquidMaterial` is
  `BlockDatabase.asset`'s material, not an instance. A crash, an aborted play session, or a
  teardown path that misses the restore strands the asset at `Cull Off` — the exact failure the
  unconditional option cannot have. `GraphicsSettingsController` already accepts this risk for
  keywords, but those are re-applied from settings on every start; a cull mode toggled by
  transient player position has no such re-assertion.

#### Option C — a second `Cull Front` pass (rejected)

- ❌ Doubles every fluid draw unconditionally, to buy a state that one `Cull Off` line gives for
  the same raster cost and none of the draw-call cost.

### 3.2 What the overlay pass carries

**Tint + depth-based exponential fog** ✅ **CHOSEN**, reading `_CameraDepthTexture` (already
enabled). A flat tint alone leaves the seabed perfectly crisp at any distance and lava fully
see-through — a colored pane of glass, not a medium; goal 2 is specifically the thing a flat tint
cannot deliver.

**The fog law is deliberately not `VoxelFog.hlsl`'s.** That fog is horizontal-XZ, radial and
back-loaded because its job is to conceal the loaded-chunk boundary without dissolving the ground
under a flying player. Underwater fog is a *medium*: it attenuates along the actual view ray, in
all three axes, from zero distance. So the overlay uses full 3D distance and Beer–Lambert
`1 - exp(-density * d)` rather than the banded `pow` ramp. Both are simultaneously live and that
is correct — terrain arrives already distance-fogged by the block shaders, and the overlay then
attenuates it again through the water column.

Sky pixels (depth at the far plane) take full density, so the surface far above and any visible
sky read as fully submerged rather than punching a clear hole in the effect.

⚠️ **Corrected 2026-09-04.** The paragraphs above assumed `_CameraDepthTexture` holds opaque geometry
only. It does not: this renderer copies depth **after transparents** (§2), and the liquid pass writes
depth from the transparent queue, so the sampled distance is to the nearest *fluid face* rather than to
the terrain behind it. That is arguably the better medium — fog should end where the water does, and a
fluid body's boundary is exactly where it ends — but two stated consequences do not survive: terrain is
**not** attenuated twice through the water column when a fluid face lies in front of it, and looking up
at the sky through a surface fogs to the *surface*, not to full density. The shader arithmetic in the
§7 baselines is unaffected; the in-game reading of it is. Flipping `m_CopyDepthMode` to `AfterOpaques`
would restore the original description at the cost of an earlier copy every frame, project-wide; §8
records taking the depth as it is.

**The fog is charged per pixel, for the submerged part of that pixel's ray** ✅ **CHOSEN 2026-09-04,
after the first in-game pass.** Submersion is a property of a **ray**, not of the camera: whether a
pixel looks through water depends on where its ray goes. Gating the whole effect on a scalar derived
from the eye alone meant a partly submerged view switched the medium off wholesale — reported in game
as a player at the waterline being able to clear the fog while half the screen was underwater.

Each pixel now solves where its ray meets the surface plane and fogs only the segment below it:

```
y(t) = eyeY + rayUpwardness · t,   t ∈ [0, rayDistance]
submerged where y(t) < surfaceY  →  crossing = clamp(eyeDepth / rayUpwardness, 0, rayDistance)
   rising ray  (upwardness > 0):  submerged length = crossing
   falling ray (upwardness < 0):  submerged length = rayDistance − crossing
   level ray:                     all of it, or none, by the sign of eyeDepth
```

Because the crossing depends only on `surfaceY − eyeY`, **only the eye's signed depth crosses the
wire** — no camera world position, and no large-magnitude world coordinate in the fragment. The
waterline then emerges as the locus where the submerged length reaches zero, which is why this
retires the ramp above rather than sitting beside it, and why UW-5 shrinks to the meniscus band and
the wobble.

**The surface is a plane; the fluid is a body — and the difference has to be modelled, twice.**

**From outside**, the plane runs to the horizon while the pool may be three blocks wide, so every ray
that misses the water gets charged for the whole distance to whatever it does hit.
`SubmergedRayLength` therefore returns zero whenever the eye is above the surface, and
`_SubmersionColor.a` gates on `EyeSubmersion.IsSubmerged`. **That much is exact, not a
simplification:** a ray that does reach water *ends* at the water, so the pixel shows the surface as
the liquid shader drew it, never a column of water seen through. Reported in game 2026-09-04 as a
shallow pool painting the medium across a dry cave (`B21`).

**From inside, the plane is only the body's lid**, and the sides need bounding too.
⚠️ It was recorded here that the depth buffer bounded them for free — the liquid mesh is a closed
shell that writes depth, so a ray leaving sideways should terminate on a side face. **That was wrong,
and a live frame disproved it.** With the eye 2.4 cm under the surface at the body's western edge, a
westward ray crossed **zero** water and was charged **3.9 blocks** — 42 % fog on dry cave — while
eastward rays through 3.9 blocks of real water came out correct to within 3 %. At a shoreline the
nearest boundary face sits centimetres from the eye, **inside the near clip plane**, so it is never
rasterized and the depth buffer reports the terrain beyond it.

So the half-space became a **box**: `EyeSubmersion.HorizontalExtent` carries the body's reach in
±X and ±Z, measured by `World.MeasureHorizontalExtent` at the eye's own height, and the fragment
clamps the submerged length with a slab-exit test (`B22`). A direction that runs the full
`World.FluidExtentScanCells` reports `World.UnboundedFluidExtent` instead of the scanned distance, so
open water is never clamped to the scan's reach.

**The extents measure where the water ends, not where the first obstruction is** — and the difference is
not a nicety. Each extent is a single 1-D probe along a world axis, so a lone block standing in the water
used to truncate that whole side of the box. Measured in game: one voxel six cells out cut the +Z side
from 23 cells to 6.47, thinning the medium across a quadrant of the view, and swimming past such blocks
was what made the fog look unstable — the body appeared to breathe as obstructions moved in and out of
the four probes.

Reading *past* a **solid** gap is correct rather than lenient, and the reason is the depth buffer.
`_SubmersionBounds` bounds where the **water** ends; a solid block inside the body is an **occluder**, and
`rayDistance` already stops each ray at it. A ray aimed at that block is charged for the water in front of
it whatever the extent says — while every *other* ray on that side stops being starved of the water that
is genuinely there. `World.FluidReachCells` therefore scans past it and reports the farthest fluid
cell (`B24`).

**Air is the opposite case and ends the scan** (`B25`, added 2026-09-05). The reasoning above turns
entirely on the block being an occluder, and nothing stops a ray crossing a dry gap — so counting a second
pool beyond one charges every ray on that side for water it never enters, and the overlay fogs the dry air
between them. `FluidReachCells` breaks on `BlockIDs.Air` and passes over everything else, so a submerged
torch or plant still cannot shorten the medium around it. One edge is left open: fluid at exactly
`FluidExtentScanCells` with solid in between still reports the body as unbounded on that side.

**The extents are eased, not published raw.** They are re-measured from whichever cell the eye occupies,
so they **step** at every cell boundary — and crossing one *vertically* re-scans all four directions at
once, which reads as the whole medium jumping. Measured on a terraced pool: `EyeDepth` stayed continuous
across the boundary (0.855 → 0.895) while the extents stepped 2.50 → 6.50 together.
`SubmersionOverlay.StepExtents` therefore eases the published values over
`SubmersionOverlay.ExtentDampTime`, and **snaps** on the first publish after the eye enters a fluid so
the fog cannot sweep in from the last body swum through.

Two details that are load-bearing rather than incidental. The easing lives in
`World.PublishSubmersionGlobals` and **never in the query**, which must stay pure — `SoundManager` polls
`GatherEyeSubmersion` on its own cadence, and a stateful query would let the audio layer perturb what the
screen shows. And it interpolates `1 / (1 + d)` rather than the raw distance, because
`World.UnboundedFluidExtent` is enormous: easing linearly from open water into a two-block channel would
spend seconds at values that bound nothing, which is the over-fogging the extents exist to prevent.

Easing buys **no accuracy** — the box is exactly as wrong as before, just no longer wrong
discontinuously — and it adds a deliberate artifact of its own: the bound lags a fast swimmer, so
entering a narrow channel briefly over-fogs. That trade is taken because a lag reads as the medium
settling while a step reads as a bug.

**Still an approximation, and deliberately a conservative one.** A box cannot describe an L-shaped
pool, and the extents are read at one height, so a body that widens lower down reads narrow. Both
errors point at *under*-fogging, which goes unnoticed; over-fogging is the defect that keeps being
reported.

**The exact fix has a home: `VX-3` (volumetric water) riding on `VX-5` (voxel DDA trace substrate)**,
in [`VOLUMETRIC_AND_RAYTRACED_EFFECTS_REPORT.md`](VOLUMETRIC_AND_RAYTRACED_EFFECTS_REPORT.md).
Marching fluid occupancy per pixel integrates the water actually crossed, which removes the box's
shape error **and** its per-cell stepping in one move — the answer stops depending on the eye's cell
at all. That report now records `UW-4` as having shipped its cheap half and names this box as what
`VX-5` supersedes; `_SubmersionBounds` and `World.MeasureHorizontalExtent` are the pieces that would
be deleted.

⚠️ **The hazard this introduces, and how it bit.** The *sign* of the vertical NDC is now load-bearing,
where for the ray's length only its magnitude mattered. `Blit.hlsl`'s `GetFullScreenTriangleTexCoord`
**already** flips its V on platforms whose textures start at the top, so a shader that compensates a
second time under `UNITY_UV_STARTS_AT_TOP` inverts the result: it fogs the **sky** and leaves the water
clear. That is exactly what shipped and what play reported — the visible symptom is a plane across the
view, fogged above and clear below, appearing only within roughly ±20–30° of level because outside that
range the split plane is off-screen.

**Do not add a flip here.** The correct mapping is the plain `uv * 2 - 1`, and it is now pinned by a
baseline that *measures* rather than reasons: `B20` (§7) draws a marker across the bottom half of
**clip space**, where `y = -1` is the bottom of the view by definition, and asserts the fogged rows are
the marker's rows. A flip anywhere in the texture/readback chain moves both together, so the assertion
holds without assuming any platform convention — which is what makes it trustworthy where two rounds of
reasoning about `UNITY_UV_STARTS_AT_TOP` were not.

**How it composites: one alpha-blended pass, no copy of the camera color** ✅ **CHOSEN 2026-09-04.**
The effect is `lerp(scene, tint, fogFactor)`, and that is *exactly* what a `SrcAlpha OneMinusSrcAlpha`
blend computes against the attachment. So the pass writes the camera color directly, reads only
depth, and needs neither a fullscreen temp nor a second blit. Both the source color and the source
alpha are per-fragment, so this does **not** constrain UW-5: the waterline can output full tint below
the split and a fading alpha above it — and even a differently-colored meniscus band — in the same
single pass.

The rejected alternative was to copy the camera color and blit it back through the material (§9).
What that would have bought is the one thing this option genuinely forecloses: **a shader that
offset-samples the scene**, i.e. v2's distortion. See §5 for the corrected reserved-seat wording.

### 3.3 Who owns "is the eye submerged"

**A new surface-aware query, which the audio layer then adopts** ✅ **CHOSEN**.

`AmbienceResolution.IsSubmerged` already answers this per-cell at ~4 Hz for the ambience low-pass
filter. Leaving it in place would mean the tint and the muffling engage up to a full block apart —
plainly wrong while treading water, which is exactly where a player looks for the effect.

Its docstring's stated rationale for staying per-cell survives inspection and does **not** block
this: the reason given is that routing audio through `VoxelRigidbody.FluidContact` would tie
ambience to the player's *collider* and to *physics timing*. `GatherEyeSubmersion` is neither — it
is a pure query over the eye point, callable at whatever rate its consumer wants. Audio keeps its
4 Hz cadence and its dwell filtering; only the underlying test gets sharper.

Rejected: reusing the per-cell test for the visuals as well. It carries no sub-cell surface height,
so the tint would snap at cell boundaries and **UW-5 would be unbuildable** — there is nothing to
split the screen on.

### 3.4 Which surface height the tint boundary follows

**The mesher's corner-smoothed height** ✅ **CHOSEN** — the tint boundary must agree with the
surface the player can actually see. The logical per-cell template (what `FluidContact` uses) can
sit up to about half a block off the drawn surface at a sloped pool edge, and the waterline is
precisely the effect that makes that visible.

This is the arc's most fragile decision and it is deliberately *not* a divergence from §2's
physics rule. The two queries answer different questions and should differ:

| Query                       | Consumer         | Height source                | Why                                                                            |
|-----------------------------|------------------|------------------------------|--------------------------------------------------------------------------------|
| `World.GatherFluidContact`  | Physics          | Logical per-cell template     | A body's buoyancy must not depend on the smoothing its neighbors happen to induce. |
| `World.GatherEyeSubmersion` | Rendering, audio | Corner-smoothed, bilinear     | The tint boundary must sit where the drawn surface is.                          |

**Shipped 2026-09-04 as a move, not an exposure.** `GetSmoothedCornerHeight` and the `hasFluidAbove` /
`kMinFluidSurfaceHeight` post-steps left `VoxelMeshHelper` for `Helpers/FluidSurfaceResolver`, and the
mesher now calls them there. Re-implementation drift is therefore impossible by construction rather than
guarded against — one function computes both answers. What remains observable, and what UW-2's baselines
actually pin, is the *mapping*: which resolver corner lands on which emitted vertex, and which axis each
of `SampleSurfaceAt`'s two fractions addresses. A transposed assignment leaves every averaged quantity
identical while putting the tint boundary on the wrong slope, so the gate reads corner values off real
`GenerateFluidMeshData` output (§7).

### 3.5 Where the overlay pass runs

`RenderPassEvent.AfterRenderingTransparents` — a mechanical call, recorded because the obvious
alternative is wrong here. `BeforeRenderingPostProcessing` would let lava's glow earn bloom, but
`GraphicsSettingsController.ApplyBloom` disables `renderPostProcessing` outright whenever bloom is
off or the scene has no `Volume`, so the submersion look would differ between two settings that
have nothing to do with each other. Running after transparents makes the overlay identical in every
configuration; lava's glow is carried by its authored color instead of borrowed from the post
stack.

**`UIBlurRendererFeature` already occupies that event**, and URP runs same-event custom passes in
renderer-feature **list order**. The overlay must therefore be listed **before** it in
`VoxelEngine-URP-Renderer.asset`: the blur samples `activeColorTexture` to build the HUD's frosted
backdrop, so a blur recorded first would show an untinted world behind every panel while the rest of the
screen is tinted. UW-4's read-back baseline asserts the index, not just membership — a check for
"the feature is listed" passes with this bug present.

The pass must also declare `ConfigureInput(ScriptableRenderPassInput.Depth)`. URP derives the depth-copy
schedule from the earliest declared depth reader, and a pass that reads `_CameraDepthTexture` without
saying so is not counted (§2).

### 3.6 The waterline cannot be drawn in screen space — built, played, reverted

⛔ **A screen-space meniscus band, 2026-09-05.** Implemented in full and reverted the same day after one
in-game pass. Recorded here rather than deleted, because the reason it fails is a property of the
problem and not of the build: **a future session that reads §7's "meniscus band" and reaches for the
overlay fragment will rebuild exactly this.**

**What was built.** `meniscusWidth` / `meniscusWobble` authored on `BlockType`, published through
`_SubmersionParams.z` and a new `_SubmersionWaveParams`, consumed by a `MeniscusBand` function in
`UnderwaterOverlay.shader`. The band was drawn on the surface plane's **horizon** — the locus where the
view ray runs level, `dot(rayDirection, worldUp) = 0`, which is an infinite plane's exact image at any
eye depth — displaced by a world-anchored sine, gated by the fog's own horizontal bound and by eye
depth. Four baselines (`B25`–`B28`), all proven red by mutation; `Validate All` green at 724.

**What play said: worse than no UW-5 at all.** Two findings, and the second is the fatal one.

1. **It did not line up with the water.** The band sat a wide margin above the drawn surface. The
   horizon is where the plane's image goes *asymptotically*, and the surface the player sees is the
   **corner-smoothed mesh** a fraction of a block over their eye — those are not the same line, and at
   shallow depth they are nowhere near each other. Aligning them would mean solving against the
   smoothed mesh height rather than a flat plane at `SurfaceY`.
2. **⚠️ Even perfectly aligned, it cannot close.** The drawn surface edge is a **straight** line — the
   mesh is flat-topped quads. A sine band drawn against it must cross it: where the wave crests the band
   paints water over sky, where it dips a strip of bare surface shows through. **The gap is the wave
   amplitude, by construction.** Centring the band perfectly does not remove the error, it halves it in
   each direction. No screen-space band can put a wavy edge on a straight one.

**So the wobble belongs to the geometry, not to the overlay.** For the waterline to undulate, the
*surface* has to undulate — local vertex displacement on the liquid mesh, with the overlay's band (if it
survives at all) reading the same wave so the two agree by construction rather than by tuning.

⏸️ **And on that finding the phase was paused, 2026-09-05 — paused, not dropped.** Animating the
liquid mesh is UW-4-sized work — a vertex-stage displacement that keeps the lid welded to the walls, an
eye query that follows the wave, and an answer for what an oscillating `EyeDepth` does to the ambience
gate — bought against a boundary that is already *geometrically correct* and merely hard-edged. At
current priority that trade does not pay; it is a cost/benefit call at a moment in time, not a verdict
that the effect is unwanted or unreachable. UW-6 does not wait on it. Everything below is what UW-5
starts from when it resumes.

**Three things that design owes an answer to**, found while establishing the above and recorded so the
next pass starts from them rather than rediscovering them:

- **The lid and the walls have to stay welded.** Top-face vertices are identifiable in `LiquidVert`
  (`Includes/LiquidCore.hlsl:140`) by their `+Y` normal, but the **side** faces' top edges carry
  horizontal normals and share no marker with the lid they meet — displacing only the lid tears it off
  the walls. Displacing every vertex by an XZ-keyed wave welds them but moves the whole water column,
  sliding the body against terrain at every shoreline.
- **There is nowhere to put a "distance from the surface" vertex channel.** MR-2's 32-byte layout has
  all four color channels, both UVs and `lightData` spoken for
  ([`../Architecture/FLUID_SHORELINE_RENDERING.md`](../Architecture/FLUID_SHORELINE_RENDERING.md)), so
  the weight has to be derived, not carried.
- **A wobbling surface reaches the audio.** §3.4 requires the eye query to follow the *drawn* surface,
  so `SurfaceY` would have to include the displacement — and then `EyeDepth` oscillates through zero for
  a player floating at the surface. `IsSubmerged` is the same boolean the ambience low-pass switches on
  (`B16` pins that bit-exactly, §4.3), so the muffling would chatter with the waves. Hysteresis on the
  gate, or letting audio keep the unwobbled height and reopening the divergence UW-3 closed, are both
  choices with costs; neither is a detail.

**What survived the revert:** nothing in code. UW-4's per-pixel split is untouched and still gives a
geometrically exact boundary — the edge is simply hard, which §8 continues to record as a limitation.

---

## 4. Architecture

```
World.SetGlobalLightValue (every frame)
        │
        ├─▶ PublishSkyGlobals ─▶ PublishFogGlobals          (existing)
        │
        └─▶ PublishSubmersionGlobals                        (UW-4, new)
                    │
                    ▼
        World.GatherEyeSubmersion(unityEyePos, out EyeSubmersion)   (UW-2, new)
                    │   voxel lookups + managed palette live here
                    ▼
        Helpers.FluidSurfaceResolver                                 (UW-2, new)
                    │   pure static math over value types
                    ├── SmoothedCornerHeights(...)  ── shares ──▶ VoxelMeshHelper
                    └── SampleSurfaceAt(cornerHeights, fracX, fracZ)
                    │
        ┌───────────┴────────────┬─────────────────────────┐
        ▼                        ▼                         ▼
  _SubmersionColor         SoundManager (4 Hz)     UnderwaterOverlayState.Active
  _SubmersionParams          (UW-3)                        │
        │                                                  ▼
        ▼                              UnderwaterOverlayRendererFeature  (UW-4)
  UnderwaterOverlay.shader  ◀──────────  AfterRenderingTransparents
        │                                 enqueues nothing when inactive
        ├── _CameraDepthTexture ─▶ Beer–Lambert fog in fluid color
        └── near-plane ray test  ─▶ waterline split + meniscus   (UW-5)
```

### 4.1 The query result

```csharp
/// <summary>
/// What fluid, if any, the eye point is inside — and where that fluid's <b>drawn</b> surface sits
/// above or below it.
/// </summary>
/// <remarks>
/// The rendering and audio counterpart to <see cref="Physics.FluidContact"/>. That struct answers
/// "what is the fluid doing to this body"; this one answers "what is the camera looking through".
/// The two use different surface-height sources on purpose — see the design doc §3.4.
/// </remarks>
public struct EyeSubmersion
{
    /// <summary>The fluid the eye is in, or <see cref="FluidType.None"/> when it is in none.</summary>
    public FluidType Type;

    /// <summary>Unity-space Y of the drawn fluid surface at the eye's XZ, when one was found.</summary>
    /// <remarks>The top of the whole fluid <b>body</b>, not of the eye's own cell — see §4.2.</remarks>
    public float SurfaceY;

    /// <summary>
    /// How far the eye sits below <see cref="SurfaceY"/>. Negative when the eye is above the
    /// surface — reported anyway, so the waterline has a plane to track as the eye breaks through.
    /// </summary>
    public float EyeDepth;

    /// <summary>Authored tint of the fluid at the eye, or <c>default</c> in air.</summary>
    public Color SubmersionColor;

    /// <summary>Authored fog density of the fluid at the eye, in per-block extinction.</summary>
    public float SubmersionDensity;

    /// <summary>Whether the eye is under the surface.</summary>
    public bool IsSubmerged => Type != FluidType.None && EyeDepth > 0f;
}
```

### 4.2 Resolving the surface at the eye

`World.GatherEyeSubmersion` mirrors `GatherFluidContact`'s shape — the voxel lookups and the
managed `BlockType` palette stay in `World`, the geometry is pure static math in
`Helpers/FluidSurfaceResolver.cs`. It inherits the same disposal guards
(`FluidVertexTemplates`/`JobDataManager` null-or-disposed → return `default`), because
`SetGlobalLightValue` runs in edit-mode fixtures and can outlive a world unload.

The cell search is two cells deep, not one:

1. Take `cell = floor(unityEyePos)` (WS-4: convert Unity → voxel space through `WorldOrigin`, at
   this boundary only, exactly as `SoundManager.cs:402` already does).
2. If that cell holds a fluid, it owns the answer.
3. Otherwise probe the cell **below**. It cannot submerge the eye — its surface is at most its own
   ceiling — but it supplies the `SurfaceY` the waterline needs while the eye is just above water.
   `EyeDepth` comes back negative.

**Then the surface is resolved at the top of the body, not at the cell that matched.** Once a fluid
cell is found, `World.TopOfFluidBody` walks **up** the column while the cell above holds the same
fluid, and the corner smoothing is evaluated on that topmost cell — the one whose top face is
actually drawn. The walk terminates at the first non-matching cell or at the world ceiling, so it is
bounded by the depth the player has swum to; the cost is one voxel read per cell, once per frame.

⚠️ **This was wrong until 2026-09-04 and shipped that way.** The surface was read off the eye's *own*
cell. An interior cell has its drawn corners forced flat (§3.4), so `SurfaceY` came back as the eye's
cell **ceiling** — meaning it stepped down by one every time the eye sank past a boundary, and
`EyeDepth` collapsed to nearly zero at each one. Reported in game as the overlay re-running its fade
once per voxel cell while sinking. Two things hid it: audio only ever read `IsSubmerged`, whose sign
stayed correct throughout, and no baseline pinned `SurfaceY`'s *value* for a submerged eye — B8
asserted only that the depth was positive. `B18` now pins the value, the monotonic deepening, and the
strength staying saturated across boundaries. It would also have put UW-5's split plane a cell too
low, so this is a prerequisite for the waterline and not only an overlay fix.

Surface height at that cell reproduces the mesher exactly:

- Four smoothed corner heights from the shared `GetSmoothedCornerHeight` path;
- forced to `1.0` when the same fluid is directly above, clamped up to `kMinFluidSurfaceHeight`;
- **bilinear** between them at `frac(eyeX)`, `frac(eyeZ)`, with the mesher's corner assignment
  (`bl=(0,0)`, `br=(1,0)`, `tl=(0,1)`, `tr=(1,1)`).

Bilinear is an approximation of a quad that the GPU rasterizes as **two triangles**, and
`EmitQuadTriangles` may flip the diagonal by light value — so the resolver and the drawn surface can
differ by a small amount along that diagonal. §8 records the bound.

### 4.3 Shader globals

**Seven**, published next to the fog globals by `World.PublishSubmersionGlobals` — a **sibling** of
`PublishSkyGlobals` under `SetGlobalLightValue`, not a step inside it: that method returns early
without a clock or authored `TimeOfDaySettings`, and the medium the player is swimming through has
nothing to do with the time of day. All of them live in **Unity/render space**, matching every other
global the block and liquid shaders consume.

| Global                 | Contents                                                                            |
|------------------------|-------------------------------------------------------------------------------------|
| `_SubmersionColor`     | `rgb` = authored fluid tint; `a` = 1 when a fluid is at the eye, 0 in air (a gate, not a fade). |
| `_SubmersionParams`    | `x` = fog density (per block) · `y` = the eye's **signed depth** below the drawn surface, positive submerged · `z` = meniscus half-width (UW-5) · `w` = distortion (v2) |
| `_SubmersionRayParams` | `xy` = the view frustum's half-extents at unit depth (horizontal, vertical) · `zw` = unused |
| `_SubmersionRayBasisX/Y/Z` | The rows of the camera's world rotation — `xyz` = the world-space X, Y and Z components of its right, up and forward axes. A row at a time, because the fragment consumes them as dot products against a camera-space ray. `Y` alone carried the surface plane; `X` and `Z` arrived with the horizontal bound. |
| `_SubmersionBounds`    | Distance to the fluid body's edge, in blocks: `x` = −X · `y` = +X · `z` = −Z · `w` = +Z. `World.UnboundedFluidExtent` means "no edge within the scan". |

A zero `_SubmersionColor.a` means "not submerged", which is what uninitialized globals give — the
same fail-safe convention `VoxelFog.hlsl` uses for its zero-width range.

**Why the ray basis is published rather than derived in the shader.** The obvious route is
`ComputeWorldSpacePosition`/`UNITY_MATRIX_I_VP`, and it would need no global at all. It is rejected
because that matrix is unsettable outside a real camera render, so the fragment's distance
reconstruction could then only be validated behind an `#ifdef` — gating a *different* code path
than the one that ships. Publishing a handful of floats makes the arc's most error-prone arithmetic
testable against the real fragment (§7, UW-4). It is also resolution-independent, so it survives the
render scale `GraphicsSettingsController` changes at runtime.

The basis rows track pitch *and* roll, which the waterline must. `Y` alone sufficed while the only
bound was the surface plane; the horizontal box needs the ray's world XZ as well, hence all three.

**`_SubmersionColor.a` is a gate, not a fade.** It is 1 while the eye is **under** a fluid surface
(`EyeSubmersion.IsSubmerged`) and 0 otherwise — it never takes an intermediate value. How much medium
a pixel looks through is decided **per pixel**, in the shader, from the eye depth and that ray's
direction (§3.2). Gating on `IsSubmerged` also means the tint and the ambience low-pass filter switch
on exactly the same boundary, which is §3.3's promise; `B16` asserts the two agree rather than assuming
it.

⛔ **Superseded 2026-09-04 — a 0.25-block depth ramp on `_SubmersionColor.a`.** Chosen earlier the
same day to avoid the full-screen pop at the surface, and withdrawn after the first in-game pass:
a screen-wide strength is the wrong shape for the problem. It let a player floating at the waterline
fade the medium to nothing while the lower half of their view was still entirely underwater, and a
hard switch has the identical hole — just binary rather than adjustable. The ramp also introduced the
audio/visual divergence recorded here as a ⚠️, which the per-pixel solve removes: continuity is now
geometric, so there is no band in which the tint lags the muffling, and `IsSubmerged` remains the
audio boundary without the visuals having to agree with it screen-wide.

### 4.4 Authoring

Two `public` fields on `BlockType`, under the existing `[Header("Fluid Properties")]`, matching the
2026-09-03 physics coefficients in style and tuning path (`BlockEditor` → `BlockDatabase.asset`):

| Field                | Meaning                                                        | Water (first pass) | Lava (first pass) |
|----------------------|----------------------------------------------------------------|--------------------|-------------------|
| `submersionColor`    | The medium's color; the fog target and the tint at zero depth.  | Deep blue-green    | Dark orange-red   |
| `submersionDensity`  | Beer–Lambert extinction per block of view distance.             | Low — metres of visibility | High — near-opaque within about a block |

These are **not** save data. `BlockType` is a ScriptableObject; new serialized fields take their
initializers on load. No chunk-format change, no `level.dat` bump, no AOT migration step.

---

## 5. Prerequisites & integration points

- **No blocking prerequisite.** Every mechanism this design needs already exists and was verified:
  depth and opaque textures are on in the URP asset, the RenderGraph feature pattern ships in
  `UIBlurRendererFeature`, the frame-rate global publish point is `SetGlobalLightValue`, and the
  eye-cell resolution already runs in `SoundManager`.
- ⚠️ **The overlay's fog depends on `m_RequireDepthTexture: 1`.** If that is ever turned off, the
  fog silently degrades to a flat tint with no error. UW-4 logs a warning once when the depth
  texture is unavailable rather than failing quietly.
- **Reserved seat — per-fluid distortion (v2).** The overlay shader takes its distortion amount from
  `_SubmersionParams.w`, wired to `0` until v2 fills it.
  ⚠️ **Corrected 2026-09-04.** The original claim that adding the wobble "touches the shader and
  `GraphicsSettingsController` only" does not survive the single-pass composite chosen in §3.2. That
  pass never reads the camera color, so a distortion that offset-samples the scene needs a source
  texture the pass does not currently have. Two ways out when v2 arrives, neither costing anything
  today: sample `_CameraOpaqueTexture`, which is already enabled (`m_RequireOpaqueTexture: 1`) and
  loses only the liquid surface from the distortion source; or restructure the pass into
  copy-then-blit at that point. The reserved global still means no C# plumbing has to change.
- **Reserved seat — non-player eyes.** `GatherEyeSubmersion` takes a world-space point, not a
  `Player`, so a future spectator or cutscene camera needs no new query. There is no freecam or
  third-person camera in `Assets/Scripts` today; `Camera.main` is the only consumer.
- **Seam with `FLUID_BUGS` #15.** Bubbles, splashes and submerged ambience *content* belong to #15.
  UW-3 changes only which test drives the existing low-pass filter; it adds no audio content.

---

## 6. Constraint compliance checklist

| Project constraint                              | How this design complies                                                                                                                                                          |
|-------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Voxels are packed `uint`s, no per-voxel objects | The eye query reads `VoxelState` through `worldData.TryGetVoxel` and the `NativeArray<BlockTypeJobData>` palette. Nothing is stored per voxel; `EyeSubmersion` is one struct per frame. |
| Burst jobs 100 % Burst-compatible               | No new jobs. `FluidSurfaceResolver` is static math over value types and `NativeArray`, with no managed references — the same posture as `FluidContactResolver`, so it stays job-callable if one ever needs it. |
| No GC / LINQ in hot paths                       | One query per frame into a `struct` out-param; no allocation, no LINQ. The overlay pass caches its material and RenderGraph resources like `UIBlurRendererFeature` and enqueues nothing while inactive. |
| Pooling conventions                             | Nothing per-frame is collected, so no pool is introduced; the feature's material follows `UIBlurRendererFeature`'s create-once/`ReleaseResources` lifecycle.                        |
| No `BinaryFormatter`/JSON for terrain           | Nothing here reaches disk. **Tripwire: zero on-disk change** — the two new fields sit on a ScriptableObject, not in the chunk or `level.dat` schema.                                |
| `BlockIDs` constants, no raw IDs                | No block is named anywhere in the design; it reads `fluidType` off whatever block occupies the eye cell.                                                                            |
| WS-4 coordinate spaces                          | The eye arrives in **Unity space**; conversion to voxel space happens once, at the `WorldOrigin` boundary in `GatherEyeSubmersion`. `SurfaceY` is published to shaders in Unity space, named for it, and never mixed with voxel Y. |
| `#pragma target 4.5` shader floor               | The new overlay shader declares it. UW-1 adds no varying, so `LiquidV2F` stays at 11 of 15 interpolators.                                                                           |

---

## 7. Phased implementation plan

| Phase                          | Scope                                                                                                                                                     | Effort | Depends on   | Status |
|--------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------|:------:|--------------|--------|
| **UW-0 — Authored look**       | `submersionColor` + `submersionDensity` on `BlockType`; surfaced in `BlockEditor`; first-pass water and lava values.                                        |   🟢   | —            | ✅ 2026-09-04 |
| **UW-1 — Backfaces**           | `Cull Off` on `UberLiquidShader`'s `LiquidForward` pass. Shader only, no C#.                                                                                |   🟢   | —            | ✅ 2026-09-04 |
| **UW-2 — Eye query**           | `EyeSubmersion`, `Helpers/FluidSurfaceResolver`, `World.GatherEyeSubmersion`; the mesher's corner-height path moved there wholesale.                          |   🟡   | UW-0         | ✅ 2026-09-04 |
| **UW-3 — Audio adopts it**     | `AmbienceResolution.IsSubmerged` → the shared query; `SoundManager.cs:409` call site; SoundEngine baselines updated for sub-cell behavior.                   |   🟢   | UW-2         | ✅ 2026-09-04 |
| **UW-4 — Overlay pass**        | `Rendering/UnderwaterOverlayRendererFeature` + `Shaders/UnderwaterOverlay.shader` + `Rendering/SubmersionOverlay`; `World.PublishSubmersionGlobals`; wired into `VoxelEngine-URP-Renderer.asset` **above `UIBlurRendererFeature`** (§3.5). **No Graphics setting** — decided 2026-09-04: the pass enqueues nothing unless the eye is submerged, so "off" buys no measurable frame time and only restores the bug. |   🟡   | UW-2         | ✅ 2026-09-04 |
| **UW-5 — Waterline**           | **Rescoped 2026-09-05 after a failed build, then paused (§3.6).** The screen split is *not* part of this — it shipped with UW-4's per-pixel solve. What remains is making the drawn surface itself undulate: local vertex displacement on the liquid mesh, welded lid-to-walls, with the eye query following the wave and an answer for what that does to the ambience gate. A screen-space band was built and reverted — do not rebuild *that*. The mesh version is still the open route; it is paused on cost/benefit, and wants a fresh plan when it resumes. |   🔴   | UW-4         | ⏸️ 2026-09-05 |
| **UW-6 — Lava pass & closure** | Lava density/color tuning, in-game confirmation, `docs-sync`, `FLUID_BUGS` #02 archived. **Unblocked 2026-09-05** — pausing UW-5 dropped it from this list, so nothing gates UW-6 and it does not wait on the waterline resuming. |   🟢   | UW-1…UW-4    | —      |

*Status: `—` not started · `In progress` · `✅ YYYY-MM-DD` complete (dated at in-game
confirmation) · `⏸️ YYYY-MM-DD` deliberately not implemented · `⛔ Superseded YYYY-MM-DD — <by
what>`.*

**UW-0 + UW-1 + UW-2 + UW-4 is the minimal set that delivers standalone value**: a visible,
fogged, correctly-tinted submerged view. UW-3 and UW-5 are polish on top of a working effect, and
UW-6 is closure. That framing held up under test: **UW-5 was paused without disturbing anything, and
UW-6 did not have to wait for it** — which is exactly what "polish on top of a working effect" is
supposed to buy.

**UW-1 confirmed in game 2026-09-04.** A fluid body renders correctly from inside it, **including at
distance under the atmospheric fog** — worth stating because `ApplyVoxelFog` is live in this pass and the
`UW-1` baselines deliberately force the fog range to zero to keep the culling measurement clean, so the
fogged case is confirmed by play and not by the suite.

**Landed 2026-09-04 (UW-0, UW-2, UW-3).** `submersionColor` /
`submersionDensity` on `BlockType` with water and lava authored and all seven fluid coefficients given
`BlockEditor` sliders; `Cull Off` on the liquid pass; `Helpers/FluidSurfaceResolver` +
`Helpers/EyeSubmersion` + `World.GatherEyeSubmersion`, with the mesher rewired to the shared resolver;
`SoundManager` reading the shared query and `AmbienceResolution.IsSubmerged` deleted. A new
`Minecraft Clone/Dev/Validate Underwater Render` suite carries nine baselines and is registered
(`ExpectedSuiteCount` 27 → 28). B2 and B3 were confirmed **red before** the `Cull Off` line and green
after. UW-0's authored values and UW-2's surface boundary were not visible until UW-4 drew them, so all
three waited on UW-4's confirmation, and **all three are dated by it**.

UW-3 is dated by it for a specific reason rather than by association. Its whole runtime delta is *which
predicate* produces `SoundManager`'s `bool submerged` — the low-pass sweep, the crossfades, the dwell
filtering and the ~4 Hz cadence are untouched (§5). That predicate is `EyeSubmersion.IsSubmerged`, and
`B16` asserts bit-exactly that the overlay's gate opens exactly when it does, so the tint and the
muffling are driven by one boolean rather than by two agreeing tests. The in-game passes that confirmed
the tint's boundary — standing unsubmerged in a pool, and the partial-submersion cases of §3.2 —
therefore confirmed this predicate too. What the ramp's withdrawal removed along with it was the
audio/visual divergence that would have needed hearing separately (§9).

**Landed and confirmed in game 2026-09-04 (UW-4), after seven in-game passes.** `Rendering/SubmersionOverlay` (the shader
wire format, the ramp, and the pass's active flag), `World.PublishSubmersionGlobals` as a sibling of
`PublishSkyGlobals`, `Shaders/UnderwaterOverlay.shader`, and
`Rendering/UnderwaterOverlayRendererFeature` — wired into `VoxelEngine-URP-Renderer.asset` at index 0,
ahead of `UIBlurRendererFeature`, with the shader assigned. The suite carries **24** baselines and
`Validate All` stands at **720 across 28 suites**. Nine of the new baselines were confirmed able to
fail: `B12` by dropping the shader's ray-length scale (center stayed green, edge and corner went red),
`B17` by moving the feature below the UI blur while leaving it present, `B18` against the pre-fix
per-cell `SurfaceY` (§4.2), `B19` by charging every ray its full length instead of its submerged part,
`B20` against the pre-fix inverted vertical sign, `B21` against the ungated build that fogged from
above the surface, `B22` against the unbounded half-space, `B23` against a build whose extents never snap, and `B24` against the first-gap scan rule (all §3.2).

**First in-game pass, 2026-09-04 — the medium renders; four items came back, all addressed.**

1. **The fade re-ran once per voxel cell while sinking.** A UW-2 surface-resolution defect, not an
   overlay one: fixed at §4.2 and pinned by `B18`. ✅ confirmed fixed in game.
2. **Water read too cyan.** Retuned `(0.11, 0.30, 0.42)` → `(0.08, 0.24, 0.50)`: green-to-blue ratio
   0.71 → 0.48 at roughly held luminance (0.27 → 0.23), so a hue shift rather than a darkening.
   ✅ confirmed in game.
3. **The fog was too strong.** Density `0.14` halved the scene color at **5 blocks** and reached 94 %
   at 20, washing out a pond floor three blocks down. Reduced to `0.05` — half-obscured at ~14 blocks,
   90 % at ~46. Authoring, so the final call belongs to UW-6's feel pass.
4. **A partly submerged view could switch the medium off entirely.** The defect that reshaped §3.2:
   fog is now charged per pixel over each ray's own submerged length, and `_SubmersionColor.a` became
   a gate rather than a fade. Pinned by `B19`.

**Third in-game pass, 2026-09-04 — one item.** The per-pixel fog shipped with an **inverted vertical
sign**: it fogged the sky and left the water clear, visible as a plane across the view within roughly
±20–30° of level (outside that range the split is off-screen, which is why it read as "breaks between
-20 and +20"). Cause and fix at §3.2 — `Blit.hlsl` already flips its texcoord, so the shader's own
`UNITY_UV_STARTS_AT_TOP` compensation was a second flip. Now pinned by `B20`, which measures the
orientation against a clip-space marker instead of reasoning about it.

**Fourth in-game pass, 2026-09-04 — the sign fix confirmed, two more items.** Standing in a shallow
pool with the head clear of the water painted the medium over a dry cave: the plane-versus-body error,
fixed and pinned by `B21` (§3.2). And clouds are not visible through a water surface — an opaque-copy
timing limitation recorded in §8, owned by the cloud backlog rather than by this arc.

**Fifth in-game pass, 2026-09-04.** Standing at a shoreline with the eye just under the surface still
fogged the dry half of the view. Diagnosed by probing the live frame rather than by inspection — the
numbers are quoted in §3.2 — and fixed by bounding the body horizontally (`B22`). The same session
recorded that the plane's *sides* had been wrongly assumed to be bounded by the depth buffer.

**Sixth in-game pass, 2026-09-04.** The shoreline fix confirmed; the medium then read *unstable* as the
player swam, worst on vertical cell crossings. Diagnosed on a terraced pool in edit mode — the extents,
not `EyeDepth` — and eased in the publish path (`B23`).

**Seventh in-game pass, 2026-09-04, and the better diagnosis.** The instability was mostly **not** cell
quantization: each extent is a single 1-D probe, so any block standing in the water truncated that side
of the box, and swimming past obstructions made the body breathe. A live probe put numbers on it — one
voxel cutting +Z from 23 cells to 6.47. Fixed by measuring the body's **reach** rather than the first gap
(`B24`); the easing from the sixth pass stays, now handling only the residual cell-boundary step it was
always meant for.

**Eighth in-game pass, 2026-09-04 — accepted.** The reach fix confirmed, and with it the whole set that
had accumulated unseen: items 3 and 4, the plane-versus-body gate, the horizontal box and its easing.
UW-4 is dated by this pass. It was accepted explicitly as **not fully perfect** — the box remains a proxy
for a voxel body, and the residual imprecision is the one `VX-3` on `VX-5` removes rather than tunes
(§3.2). Beyond the `B25` air-gap correction below, no further tuning of `MeasureHorizontalExtent` is
planned; the next move on this axis is the volumetric path, not a better box. Its cost is accepted as it
stands: the four scans re-run every rendered frame while the eye is submerged, which was reviewed on
2026-09-05 and deliberately left alone — memoizing them would be exactly output-equivalent, but it needs
invalidation on every fluid edit, and that is a staleness surface bought against an unmeasured cost. The
one change made was to stop the audio ambience paying for it: `GatherEyeSubmersion` now takes
`measureExtent`, and `SoundManager` passes `false` because it reads only `IsSubmerged`.

**Review pass, 2026-09-05 - confirmed in game, no regressions.** A full-tree code review returned ten
findings against the arc. Fixed: the overlay stayed armed when its camera vanished mid-dive; the easing
floor now derives from the unbounded sentinel rather than a bare literal; the validation harness
snapshots and restores `_CameraDepthTexture_TexelSize` and rebinds the depth global even when it was
previously unset; and four comments were corrected to describe the code as it stands. Deferred with its
reason in the limitations below: the frame-late publish. Outside this arc but caused by it, the Block
Editor's copy paths were dropping seven of `BlockType`'s fields and had flattened **Lava's** five
body-physics coefficients as well as the submersion values - restored from `c7f14147`, with
`BlockTypeCloner` now covering private fields too so a `[SerializeField] private` addition cannot
reopen it.

**Validation is built alongside, not after.** A new `Minecraft Clone/Dev/Validate Underwater
Render` suite (`Assets/Editor/Validation/UnderwaterRender/`, namespace
`Editor.Validation.UnderwaterRender`) gains baselines as each phase lands, following the
`UIBlurRenderValidationSuite` model: arithmetic assertions rather than golden images, and
**INCONCLUSIVE** under `-nographics`. It is registered in `ValidationSuiteRegistry` so `Validate
All` and the CI entry point pick it up.

| Phase | What its baselines pin                                                                                                                                                                                                                                                     |
|-------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| UW-1  | **Prove-red first.** A reversed-winding fluid quad rendered with the liquid material returns the backdrop before the change and fluid color after. `FLUID_BUGS` #02 is a documented bug, so `validation-driven-bugfix`'s red→green→promote order applies.                     |
| UW-2  | **Pins the mapping, not the arithmetic.** With the smoothing moved into the shared resolver (§3.4) the heights agree by construction, so the baselines assert what sharing does not fix: corner values are read off the **real** `GenerateFluidMeshData` output and matched against `SampleSurfaceAt` at all four corner fractions, over a neighborhood deliberately smoothed to four *different* heights so a transposed assignment is observable. Plus the interior sample, the fluid-above override, the minimum-height floor, the two-cell search, and the soft-failure guards. |
| UW-3  | Existing SoundEngine ambience baselines extended: a head just under a partly-filled surface now reads submerged, where the per-cell test read dry.                                                                                                                            |
| UW-4  | **Shipped as `B10`–`B24`.** `B10` is the positive control (a saturated medium reaches the authored tint) and must be read first, because "the overlay drew nothing" is the same reading the pass-through scenarios call success. `B11` cross-checks the shader's depth decode against a CPU inversion of `LinearEyeDepth`. **`B12` is the one that earns its keep:** at one uniform depth it measures three screen radii, because the ray-length scale is the arc's most error-prone arithmetic and a center-only check passes with it missing entirely — proven by mutation, center green while edge and corner went red. Then density 0 and strength 0 pass-throughs, far-plane saturation, and `B16` on the packing — which pins the strength as a **gate** that never takes an intermediate value and opens exactly when `IsSubmerged` does. That one is asserted with `ExactValue`, not a tolerance: an epsilon would accept the very intermediate value the baseline exists to forbid, and the gate is a stored literal, so exactness is the contract. The same holds for the fields `Pack` copies through untouched and for `B24`'s claim that an obstruction changes the reach by *nothing*; the measured composites keep their epsilon, because a half-float render target genuinely has one. `B16` also pins so the tint and the ambience filter share one boundary, and that pitch and roll reach the published camera basis. **`B17` is the read-back** of `m_RendererFeatures`, asserting the overlay is present, its shader is **assigned**, and its **index is below `UIBlurRendererFeature`'s** — all three are silent failures the render scenarios cannot see, and a membership-only check catches none of the last two. `B16`/`B17` are deliberately **not** device-gated, so a headless run still asserts something about UW-4. **`B18`** sinks an eye across two cell boundaries inside a body and pins that `SurfaceY` does not move, that `EyeDepth` deepens monotonically, and that the published depth tracks it — the in-game fade-per-cell defect of §4.2, which `B8` could not see because it asserted only the depth's sign. **`B19`** pins the per-pixel submerged length (§3.2): pitched straight down every ray is submerged, pitched straight up none is, level at the surface the screen splits, rolling 180° swaps which half is fogged, and a deep eye stops splitting at all. It asserts the split's **structure** and is proven by mutation — charging every ray its full length reddened exactly the pitched-up and split assertions. **`B20` pins the orientation `B19` leaves out**, which is the gap an inverted vertical sign shipped through: it draws a marker across the bottom half of **clip space** and asserts the fogged rows are the marker's rows, so a flip anywhere in the texture or readback chain moves both together and the assertion needs no platform assumption. Confirmed red against the inverted shader before the fix. Structure and orientation are separate failures and need separate baselines — every `B19` assertion passes with the sign backwards. **`B21`** pins the plane-versus-body gate of §3.2: an eye above the surface fogs neither half, however far below it the geometry sits. It asserts the shader's own guard with the gate forced open, so the fragment is covered independently of C# declining to draw, and confirmed red against the ungated build — the lower half read fully fogged where the backdrop should have survived. **`B22`** pins the horizontal box of §3.2, reproducing the measured shoreline frame: a body ending 2 cm to the west and unbounded east must leave a westward ray nearly clear while an eastward one stays saturated, with an open-water control proving it is the bound that changed rather than the sampling. Proven red by dropping the slab clamp. **`B23`** pins the easing, and is device-free because the step is a pure function — which is what makes the two things most likely to be wrong reachable at all: the **snap** on entering water (proven red by removing it) and the reciprocal **space** the easing happens in, where a linear interpolation would still read ~630 000 blocks one time constant out of open water. |
| UW-5  | **Rewritten 2026-09-05 for the rescoped phase (§3.6).** The reverted screen-space attempt carried four baselines that were all green and all proven red by mutation — and the feature still failed in play, because every one of them asserted the band against the *plane* the shader was given rather than against the surface the mesher **drew**. That is the gap to close: a mesh-displacement UW-5 wants the displaced vertex height read off real `GenerateFluidMeshData` output and matched against whatever `FluidSurfaceResolver` reports at the same XZ and time, so the drawn surface and the eye query cannot drift — the `B4` pattern, extended with time as an input. Plus: the lid and the side faces' top edges agree at a shared corner (the tearing case), the wave is a pure function of world position and time (so chunk borders cannot seam), and physics' `GatherFluidContact` height is **unaffected** (§3.4's split must survive, or buoyancy oscillates). None of that is measurable from a fullscreen readback, so the harness is `FluidSurfaceFixture`'s, not `OverlayFragmentRenderer`'s. |

The final look — how water *feels* to swim through, whether the lava density is right — stays
verified in game (UW-6).

### Extension roadmap (post-UW-6, in intended order)

| Version | Extension                                                                                                                             |
|---------|----------------------------------------------------------------------------------------------------------------------------------------|
| **v2**  | Per-fluid screen distortion — water wobble and lava heat shimmer, through the reserved global (§5) and the existing distortion sliders. |
| **v3+** | Caustics and light shafts through the surface; submerged vignette/blur; a fluid-specific FOV nudge on entry — each gets its own design doc when it becomes concrete. Bubbles and splash audio remain `FLUID_BUGS` #15's. |

---

## 8. Open questions

1. **MSAA and the sampled depth texture.** The URP asset runs `m_MSAA: 2` with
   `m_OpaqueDownsampling: 1`, and `GraphicsSettingsController` changes **both MSAA and render scale at
   runtime** from the graphics settings — so this is not one configuration to verify but a range the
   player moves through. URP resolves depth for `_CameraDepthTexture`, but the interaction of that
   resolve with the downsampled opaque texture at non-100 render scale is unverified here. Resolves at
   UW-4's first in-editor render, and must be re-checked at a non-default render scale rather than only
   at 100%; if the depth read is wrong the fog banding will be obvious immediately.
   **Narrowed 2026-09-04, not closed.** Two of the three moving parts are now resolution-independent by
   construction: the view-ray basis is published from FOV and aspect (§4.3), and the depth UV comes from
   `Blit.hlsl`'s `texcoord`, which is normalized. The overlay also samples the *resolved*
   `_CameraDepthTexture`, never an MSAA target, while writing to an MSAA camera color — a combination the
   baselines cannot exercise, since the harness renders single-sample. **Still open, and still a range
   rather than one configuration: this needs looking at in game at a non-default render scale.**
2. **A strongly sloped surface under the eye.** ⚠️ **Sharpened 2026-09-05 by the reverted build (§3.6),
   which measured it rather than predicted it: the flat-plane assumption was the *first* of the two
   things that sank the band, and it is worse than "slightly off" at shallow depth.** UW-5 was to split
   the screen on a **flat** plane at
   `SurfaceY`. Where the smoothed surface tilts steeply — a shallow shore cell between a full cell
   and dry land — the true surface is not flat, and the drawn line will be slightly off. Resolves
   in game at UW-5; the fallback is to tilt the plane using the same four corner heights the
   resolver already has.

**Closed 2026-09-04 — which screen half the split puts the water on.** Opened with the per-pixel fog and
closed the same day by play: the shipped shader compensated for `UNITY_UV_STARTS_AT_TOP` on top of a
flip `Blit.hlsl` had already applied, and fogged the sky. The lesson is scoped narrowly and recorded at
§3.2 and §7: an orientation this arc reasoned about wrongly twice is now **measured** by `B20` against a
clip-space marker, not argued from convention. The earlier judgement that a render-texture harness could
not pin it was wrong — it cannot pin *absolute* orientation, but it does not need to; it only needs the
overlay and a clip-space reference to travel through the same target.

**Accepted limitations** (not questions — consequences to state plainly):

- Two fluid layers still never blend. The liquid pass writes opaquely and composites against
  `_CameraOpaqueTexture`, so a distant water wall seen from underwater shows one layer, as it does
  today from outside.
- **Clouds through a water surface — FIXED by `CL-9` (2026-09-05), not a UW change.** Reported in game
  2026-09-04, and never an underwater-overlay defect but a consequence of where URP takes its opaque
  copy. The liquid fragment reads what is behind it with `SampleSceneColor`
  (`UberLiquidShader.shader:131/179`), which samples `_CameraOpaqueTexture`. URP fills that texture
  **after the skybox but before transparents** (`UniversalRendererRenderGraph.cs:1292`), and
  `CloudShader` was `Queue="Transparent"` — so the sky was in the copy and the clouds never were. The
  clouds could not simply move to the opaque queue: they are `Blend SrcAlpha OneMinusSrcAlpha` with
  `ZWrite On` for the vanilla-parity overlap strategy (`CloudShader.shader:15-22`), so they need the
  frame behind them. The shipped shape gives them both — `CloudPrepassRendererFeature` draws them at
  `RenderPassEvent.AfterRenderingSkybox`, after the skybox and the opaque terrain are down but before
  `m_CopyColorPass`, filtered by a custom `LightMode` that keeps URP's own transparent draw off them.
  Confirmed in game 2026-09-05. **Everything else transparent seen through water — glass, leaves — is
  still missing for the original reason**, and would each need the same treatment. The fix also trades one
  direction for the other: a fluid surface viewed *from above the cloud layer* is now invisible through the
  cloud, since it fails the depth test the cloud's `ZWrite` laid down. Owned by the `CL-*` cloud backlog;
  see that report for why every fix shape shares that trade.
- The overlay is a camera effect. It does not change what the chunk shaders draw, so an individual
  block half in and half out of water is not treated per-block.
- The resolver's bilinear surface can differ from the rasterized two-triangle surface along the
  quad diagonal (§4.2).
- **The overlay's fog measures to the nearest fluid face, not to the terrain behind it.** This
  renderer copies depth after transparents, so the liquid surface is in `_CameraDepthTexture` (§2, §3.2).
  Accepted rather than fixed: fog that ends where the medium ends is the more physical reading, and
  changing `m_CopyDepthMode` to restore the originally-described behavior would force an earlier depth
  copy on every frame of the whole project for a look that is arguably worse.
- **Any other transparent geometry shortens the fog the same way.** Nothing else currently writes depth
  from the transparent queue, but a future transparent effect that does will pull the underwater fog in
  front of the terrain without any change to this system.
- No suite validates the assembled pipeline. The render suites exercise the shaders; the read-back
  check exercises the wiring; only in-game play exercises the two together.
- **The globals describe the eye one frame before the one being drawn.** `PublishSubmersionGlobals`
  runs from `World.Update`, and nothing pins `World` after whatever drives the camera, so the ray basis,
  eye depth and extents are a frame stale. Deferred to `UW-6` rather than fixed: at a high refresh rate
  it is a few milliseconds of camera lag that the extent easing's own time constant already dwarfs, and
  moving the publish to `RenderPipelineManager.beginCameraRendering` — the hook that removes the
  ordering question entirely — is a lifecycle change to a system confirmed in game. It is most visible
  on a fast look while swimming at a low framerate.
- **A camera lost while submerged is handled by disarming, not by clearing.** `PublishSubmersionGlobals`
  returns early without a camera and drops the easing's primed flag, so the pass stops enqueueing while
  the last frame's values stay in the globals. No baseline covers it: the publish is private and needs a
  live camera, so the guard is asserted only by reading it.
- **The overlay's fog starts at zero.** Pure Beer–Lambert means a block held right up to the eye is
  essentially untinted, and only distance thickens the medium. That is §3.2's decision working as
  intended — a flat floor is what makes a filter rather than a medium — but if water ends up reading
  too clear up close, the fix is a small minimum weight and it belongs to UW-6's feel pass, not here.
  The first in-game pass moved the opposite way: the fog was too *strong*, and the density came down
  from `0.14` to `0.05` (§7).
- **The fluid body is approximated by a box, so an L-shaped or terraced body under-fogs**, and the
  box's dimensions are re-measured per eye cell, so they step as the player swims — eased rather than
  removed, which leaves a **lag**: a swimmer entering a narrow channel is briefly over-fogged for about
  `SubmersionOverlay.ExtentDampTime` (§3.2; `VX-3`/`VX-5` is the exact replacement). The four
  extents are measured at the eye's height along the world axes (§3.2), so a pool that bends, or that
  widens below the eye, reports narrower than it is and the medium thins out early down that arm. The
  error is one-directional by design — under-fogging reads as "the water is clear here", where
  over-fogging reads as a bug. Exact bounding is a per-pixel voxel march, which is `VX-*` work.
- **The waterline is a hard edge for as long as UW-5 stays paused.** The per-pixel solve gives a
  geometrically exact split, but nothing softens it: there is no meniscus band and no wobble, so at the
  surface the boundary is one pixel wide. ⏸️ **Live with it for now rather than a pending phase** — a
  screen-space band was built and reverted on 2026-09-05 (it read *worse* than the hard edge, and §3.6
  records why the approach cannot work at all), and the mesh-displacement route that remains is priced
  above what the polish is worth today. Unlike the other entries here this one has a known way out, so
  it is a limitation by choice rather than by consequence.
- **`AddRenderPasses`' "enqueue nothing while dry" gate is not baselined.** Asserting it needs a real
  `ScriptableRenderer` and a populated `RenderingData`, neither of which can be fabricated in edit
  mode. `B16` covers the strength that drives it, and the shader's zero-strength early-out (`B15`)
  means a wrongly-enqueued pass would still draw nothing — so the exposure is a wasted fullscreen
  triangle, not a visual defect.
- **`SubmersionOverlay.Active` is a mutable static with two owners.** It is republished every frame
  while a world lives, cleared on play-mode entry, and cleared again in `World.OnDestroy` — that last
  one because quitting to the menu while submerged would otherwise leave the pass armed with the final
  frame's tint still in the globals. A third teardown path that bypasses `OnDestroy` would reopen it.

---

## 9. Rejected alternatives

| Alternative                                                              | Why rejected                                                                                                                                                                          | Date       |
|--------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|------------|
| `Cull [_LiquidCull]` toggled from C#                                     | Strands render state on the shared `BlockDatabase.asset` liquid material when a session ends abnormally; buys a cost saving that `abs()`-safe unconditional `Cull Off` does not need. §3.1 | 2026-09-03 |
| A second `Cull Front` pass on the liquid shader                          | Doubles fluid draw calls unconditionally for a state one line of render state provides. §3.1                                                                                            | 2026-09-03 |
| Reuse `AmbienceResolution.IsSubmerged` (per-cell) for the visuals         | No sub-cell surface height, so the tint snaps at cell boundaries and UW-5 has nothing to split the screen on. §3.3                                                                       | 2026-09-03 |
| Drive the tint boundary from the logical per-cell template                | Can sit ~0.5 block off the drawn surface at a sloped pool edge — visible precisely where the waterline effect lives. §3.4                                                                | 2026-09-03 |
| Flat screen tint with no depth fog                                       | Leaves the seabed crisp at any distance and lava see-through; produces a colored pane of glass rather than a medium. §3.2                                                                | 2026-09-03 |
| A UI `Canvas` image tint instead of a render pass                        | Cannot read depth, so no medium fog and no waterline; composites over the HUD rather than under it.                                                                                     | 2026-09-03 |
| Extend `VoxelFog.hlsl` to fog terrain underwater from the block shaders   | Needs a keyword or branch in every block shader, still cannot tint the sky, and gives no waterline. Also fights the XZ-radial law that fog was deliberately given.                       | 2026-09-03 |
| Place the overlay at `BeforeRenderingPostProcessing`                     | `GraphicsSettingsController.ApplyBloom:171` disables `renderPostProcessing` when bloom is off or no `Volume` exists, so submersion would look different across an unrelated setting. §3.5 | 2026-09-03 |
| Copy the camera color and blit it back through the overlay material      | A fullscreen temp and a second fullscreen pass every submerged frame, to buy an offset-sampling capability only v2's distortion needs. The tint-plus-fog effect is a lerp toward a constant color, which `SrcAlpha` blending already performs against the attachment — and per-fragment color and alpha leave UW-5's waterline fully expressible in one pass. §3.2, §5 | 2026-09-04 |
| A hard `IsSubmerged` switch for `_SubmersionColor.a`                     | Cheaper and puts the tint on literally the same test as the audio, but pops the whole screen in one frame at the surface. A 0.25-block ramp is preferred until UW-5 makes that boundary legible; the residual audio/visual divergence inside the band is recorded at §4.3 and §8. | 2026-09-04 |
| Reconstruct the view ray in the shader from `UNITY_MATRIX_I_VP`          | Needs no published global, but that matrix cannot be set outside a real camera render, so the fog's distance reconstruction would only be testable behind an `#ifdef` — gating a different code path than ships. Published floats keep the real fragment measurable. §4.3 | 2026-09-04 |
| A hard `IsSubmerged` switch for the medium (revisited after play)        | Removes the fade-to-nothing exploit but not the defect behind it: an eye a centimetre above the surface still leaves a fully submerged lower half unfogged, and it reinstates the full-screen pop. The gap is that submersion is a **per-ray** property being gated on a per-camera scalar. §3.2 | 2026-09-04 |
| Floor the strength while any fluid is near (`max(0.5, ramp)`)           | Cheapest way to kill the exploit, but it tints the **sky** half of the screen at 50 % while the eye is at the surface — wrong in the other direction, and it still cannot produce a waterline. §3.2 | 2026-09-04 |
| **A screen-space meniscus band in the overlay fragment (UW-5, v1)**     | **Built in full, played, and reverted the same day.** Drawn on the surface plane's horizon, which is not where the corner-smoothed mesh the player sees actually is; and even aligned, a sine band against a straight mesh edge leaves a gap the width of the wave amplitude, because it must cross the edge it is meant to decorate. The wobble has to move the geometry. §3.6 | 2026-09-05 |
| Draw the meniscus where the fog's submerged length falls off            | Weighed while building the above. That locus is real, but it depends on the authored density and the eye's depth as well as on the geometry — no closed form for a baseline to assert, and the line moves when someone retunes the water's color. §3.6 | 2026-09-05 |
| Key the waterline's wobble to screen position                           | One fewer dot product, but the wave then stays glued to the view and slides sideways whenever the player turns their head. Still true for a mesh-based wave: it must be a function of world position and time, which is also what keeps chunk borders from seaming. §3.6 | 2026-09-05 |

---

## Document History

* **v2.4** - **Code-review findings closed** (2026-09-05). A full-tree review of the branch returned five
  items; three were in this arc's shipped code. (1) **`FluidReachCells` now breaks on air.** §3.2's rule
  that reading past a gap is correct turns entirely on the block being an **occluder** the depth buffer
  already stops rays at — true of stone, false of air. An eye in a cave pool with a second pool across a
  dry floor counted the far pool and fogged the gap. New baseline **`B25`** (suite 24 → 25) pins both
  halves: a dry gap ends the body, and filling that same gap with solid restores the far reach, so the fix
  cannot trade `B24`'s rule for this one. Prove-red confirmed the honest way — the first attempt was
  **invalid** and is recorded as such: the fixture placed its far pool at x = 18 in a 16-wide harness
  world, so `B25` went red on an out-of-range throw rather than on the defect. Regeometried inside the
  chunk, it reds on the measurement itself (extent 9.50, the far pool, where the near edge is 2.50).
  (2) **`RunB9DisposedWorld` leaked its fixture.** It was the only scenario not using `using`, and its
  guard's bare `return false` skipped the `Dispose` that restores `World.Instance` and the floating-origin
  anchor — so a future failure of that one assertion would have contaminated every later suite in a
  `Validate All`. Proven, not argued: with the old shape and the guard forced, `World.Instance` was left
  holding `PhysicsSolver_StubWorld` instead of null. (3) **The audio path stopped paying for an extent it
  discards** — see §3.2. The per-frame cost of the scan itself was reviewed and **deliberately not
  optimized**; the reasoning is recorded in §3.2 rather than left as silent debt.
* **v2.3** - **The "clouds are not visible through a water surface" limitation is resolved** — by `CL-9`
  in the cloud backlog, not by any UW change; no code in this document's scope moved. The bullet in §8 is
  rewritten as shipped and its stale `UberLiquidShader.shader:134/182` reference corrected to `131/179`.
  Two claims made when the limitation was first written are corrected with it. First, ownership: the entry
  said the work was "owned by the `CL-*` cloud backlog", but it had never been filed there — that report
  had no row for it, and CL-9 is the entry that closes the gap. Second, the two "workable shapes" were not
  equivalent: a second color copy *after the clouds* still has to pull them out of the transparent queue
  first, so it inherits the identical sorting change and pays for an extra full-screen copy on top. Only
  moving the **liquid** to a post-transparent pass preserves cloud sorting, and that reorders water against
  the other transparents and disturbs the UW-4 depth-copy ordering confirmed in game. The pre-transparent
  cloud pass was therefore the only shape worth building, and it is smaller than "neither is a small edit"
  implied. Glass and leaves through water are unchanged and still missing.
* **v2.2** - **UW-5 built as a screen-space meniscus band, failed its in-game pass, reverted whole, and
  then marked `⏸️ 2026-09-05` — paused on cost/benefit, not abandoned.** No code from it survives. New §3.6
  records the attempt in full, because the reason it fails belongs to the problem rather than to the
  build: a sine band drawn against a **straight** mesh edge must cross it, so the gap it leaves *is* the
  wave amplitude and centring the band only halves the error in each direction. It also missed the
  surface outright, having been solved against the flat plane's horizon rather than the corner-smoothed
  mesh the player sees — which sharpens §8's open question 2 from a predicted "slightly off" into a
  measured miss. The route that remains is **local vertex displacement on the liquid mesh**, which is
  UW-4-sized work bought against a boundary that is already geometrically correct and merely hard-edged;
  at current priority that does not pay, so the phase is paused on the trade rather than closed on
  impossibility, and §3.6 is written as what it resumes from. Consequences: §1's goal 4 is half-met by
  UW-4 and half-paused, §8's hard-edge entry becomes a **limitation by choice** with a known way out,
  and UW-6 drops UW-5 from its dependencies — unblocked, and not waiting on the waterline coming back.
  §7's baseline row is rewritten to say why four green,
  mutation-proven baselines did not catch any of this — every one asserted the band against the plane the
  shader was handed rather than against the surface the mesher drew. §3.6 also banks three findings a
  resumed UW-5 would otherwise rediscover: the lid tears off the side faces' top edges, MR-2's vertex has no
  room for a surface-distance channel, and a wobbling `SurfaceY` makes `EyeDepth` cross zero on a floating
  player, which would chatter the ambience low-pass `B16` pins to it. §1's non-goal now separates the two
  senses of "wobble" that shared one word.
* **v2.1** - **UW-3 marked `✅ 2026-09-04`**, closing the last phase this document held open on a
  technicality. Re-read against the shipped code rather than the plan: the audio delta is four lines
  swapping a per-cell voxel test for the shared query, and the boolean it produces is the *same* one
  `B16` pins the overlay gate to, bit-exactly. So UW-4's confirmed tint boundary is evidence for the
  ambience boundary rather than merely adjacent to it, and the divergence that once separated them
  retired with the 0.25-block ramp. Corrects the v2.0 claim that "a visual confirmation is not evidence
  about the ambience filter's boundary" — true had the two been parallel tests, false now they are one
  value. Everything UW-3 does not touch, notably the low-pass sweep and the 4 Hz cadence, belongs to the
  sound engine's own confirmed arc.
* **v2.0** - **UW-4 confirmed in game on the eighth pass and dated `✅ 2026-09-04`**, carrying UW-0 and
  UW-2 with it — both were unobservable until the overlay drew them. UW-3 stays `In progress`: its
  audible half is still unheard, and a visual confirmation says nothing about the ambience boundary.
  Accepted as a **proxy**, explicitly imperfect, with the residual owned by `VX-3`/`VX-5` rather than by
  further tuning of the box. Also records a data-loss defect found the same day *outside* this arc but
  destroying its authored values: the Block Editor's load path copied `BlockType` with a hand-written
  initializer list that had fallen 7 fields behind, so opening the editor and saving wrote defaults over
  `submersionColor`, `submersionDensity` and the five 2026-09-03 fluid coefficients. Both copy sites now
  use `Editor/BlockEditor/Helpers/BlockTypeCloner`; water's values were re-authored from §7's first pass.
* **v1.9** - Seventh in-game pass on UW-4, and a sharper diagnosis of v1.8's instability: each horizontal
  extent is a single 1-D probe, so a lone block standing in the water truncated that whole side of the box
  — measured live at one voxel cutting +Z from 23 cells to 6.47. `World.FluidReachCells` now reports the
  farthest fluid cell rather than stopping at the first gap, which is correct because a solid block inside
  the body is an occluder the depth buffer already bounds. §3.2 gains that reasoning, `B24` added and
  proven red against the old rule.
* **v1.8** - Sixth in-game pass on UW-4: the medium shifted as the player swam. The box's extents are
  re-measured from the eye's cell, so they step at every boundary — all four at once crossing vertically.
  Measured on a terraced pool (`EyeDepth` continuous, extents 2.50 → 6.50). §3.2 gains
  `SubmersionOverlay.StepExtents`: eased over `ExtentDampTime`, snapping on entry, interpolated in
  `1/(1+d)` so the unbounded sentinel is a finite endpoint, and living in the publish path so
  `GatherEyeSubmersion` stays pure for the audio poll. The extent scan also now runs only for a
  *submerged* eye. §8 records the lag the easing introduces; `B23` added and proven red.
* **v1.7** - Fifth in-game pass on UW-4, and a retraction. §3.2's claim that the depth buffer bounded the
  fluid body's **sides** for free was **wrong**: at a shoreline the nearest boundary face sits inside the
  near clip plane and is never rasterized, so a ray crossing zero water was charged 3.9 blocks. Measured
  on the live frame rather than argued. The half-space became a **box** — `EyeSubmersion.HorizontalExtent`
  plus a slab-exit clamp in the fragment — with `_SubmersionRayBasisX/Z` and `_SubmersionBounds` added to
  §4.3 and `B22` proven red. §8 records the box's own limitation (L-shaped bodies under-fog) and points
  exact bounding at the `VX-*` volumetric backlog.
* **v1.6** - Fourth in-game pass on UW-4. §3.2 gains the **plane-versus-body** rule: the surface plane is
  only valid from *inside* the water, because from outside it runs to the horizon while the pool does not
  — a shallow pool was painting the medium across a dry cave. `SubmergedRayLength` now returns zero above
  the surface and the gate is `IsSubmerged` again, which is exact rather than a simplification (a ray
  reaching water ends at the water) and restores §3.3's single boundary with audio. §4.3's gate wording
  follows, `B21` added and proven red, `B16` re-pointed at the new contract. §8 records the **clouds not
  visible through water** limitation with its cause (URP's opaque copy predates transparents) and what a
  fix would take, assigned to the cloud backlog.
* **v1.5** - Third in-game pass on UW-4: the per-pixel fog's **vertical sign was inverted**, fogging the
  sky and leaving the water clear. `Blit.hlsl`'s `GetFullScreenTriangleTexCoord` already flips its V on
  `UNITY_UV_STARTS_AT_TOP` platforms, so the shader's own compensation was a second flip; the correct
  mapping is a plain `uv * 2 - 1`. §3.2's hazard note rewritten as a "do not add a flip here" with the
  symptom named, §8's open question 2 **closed**, and `B20` added — it measures the orientation against a
  marker drawn in **clip space**, which needs no platform assumption because a flip in the texture or
  readback chain moves the marker and the fog together. The earlier claim that a render-texture harness
  could not pin this was wrong, and is retracted at §8.
* **v1.4** - Second in-game pass on UW-4. **§3.2 reshaped: the fog is now charged per pixel, over the
  part of each ray below the surface** — a screen-wide strength let a player at the waterline clear the
  medium while half the view was underwater, because submersion is a per-ray property. §4.3's
  `_SubmersionColor.a` became a gate rather than a fade, the 0.25-block ramp is marked ⛔ superseded
  (its audio-divergence ⚠️ retired with it), `_SubmersionParams.y` now carries the eye's **signed
  depth** instead of `SurfaceY`, and `_SubmersionRayBasisY` was added as a fourth global. §7 gains
  `B19`; §8 gains the UV-flip orientation question and the hard-edge waterline, and records the fog
  density coming down `0.14` → `0.05` after the pond-floor screenshot; §9 gains the two alternatives
  weighed against the per-pixel solve.
* **v1.3** - First in-game pass on UW-4. §4.2 corrected: the eye surface is resolved at the top of the
  fluid **body** via `World.TopOfFluidBody`, not at the eye's own cell, whose forced-flat corners made
  `SurfaceY` step down and `EyeDepth` reset at every boundary a sinking eye crossed — the overlay
  re-ran its fade once per cell. `EyeSubmersion.SurfaceY`'s contract tightened to match, `B18` added to
  pin it, and §7 records why `B8` could not see it. Water's `submersionColor` retuned away from cyan.
* **v1.2** - UW-4 implemented. §3.2 gains the single-pass composite decision (no camera-color copy) and
  why it still leaves UW-5 expressible; §4.3 grows to **three** globals, records `PublishSubmersionGlobals`
  as a *sibling* of `PublishSkyGlobals` rather than a step inside it, documents the ramped
  `_SubmersionColor.a` and states the audio/visual divergence it introduces inside the band, and explains
  why the view-ray basis is published rather than derived from `UNITY_MATRIX_I_VP`; §5's distortion
  reserved-seat claim corrected — the single-pass composite does not leave v2 to "the shader and
  `GraphicsSettingsController` only"; §7 records what `B10`–`B17` actually pin, including the two proven
  by mutation; §8 narrows the MSAA/render-scale question without closing it and gains four limitations;
  §9 gains the three alternatives weighed on the day.
* **v1.1** - UW-0…UW-3 implemented. Corrected §2 against the code: the liquid SubShader is
  `Queue="Transparent"`, the renderer copies depth `AfterTransparents` (so `_CameraDepthTexture` holds the
  fluid surface), `UIBlurRendererFeature` shares UW-4's injection point, and the fluid physics coefficients
  were never in the `BlockEditor`. §3.2's double-fogging and sky-density consequences withdrawn accordingly;
  §3.4 records the smoothing as moved rather than exposed; §3.5 gains the feature-ordering and
  `ConfigureInput` requirements; §8 gains three limitations. UW-1 confirmed in game the same day,
  including the fogged distance case its baselines cannot cover.
* **v1.0** - Initial design

---

**Last Updated:** 2026-09-05  
**Next Review:** at UW-6 — lava's feel pass, in-game confirmation and `FLUID_BUGS` #02's archival, which
UW-5's pause unblocked; or earlier, if UW-5 is picked back up
