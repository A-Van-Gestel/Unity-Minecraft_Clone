# Underwater & Submersion Rendering

**Version:** 1.0  
**Date:** 2026-09-05  
**Status:** **Implemented (Stable)** — UW-0…UW-4 and UW-6 shipped and confirmed in game
2026-09-04/05. UW-5 (a wobbling waterline) is ⏸️ paused and describes no code here; its resume plan
is [`../Design/ANIMATED_LIQUID_SURFACE.md`](../Design/ANIMATED_LIQUID_SURFACE.md).  
**Target:** Unity 6.6 (Mono for dev; IL2CPP for production)

> What the player sees and hears while the camera is inside a fluid. A fluid body renders from
> inside it (`Cull Off` on the liquid pass), and a URP fullscreen pass tints the screen in the
> fluid's authored color and fogs it exponentially against scene depth. **The pivotal decision:
> submersion is one shared, sub-cell, surface-height-aware query — `World.GatherEyeSubmersion` —
> which both the overlay and the ambience low-pass filter read, so the tint and the muffling switch
> on the same boundary.** The second: **fog is charged per pixel, over the part of each ray that
> lies below the surface and inside the body's horizontal box**, so a view split by the waterline
> splits rather than switching wholesale.

**Audited:** 2026-09-05, at commit `cb1508ed` (branch `feat/fluid-physics`).
Every claim below was re-verified against current code this session, from the code rather than from
the Design doc: `Helpers/EyeSubmersion.cs`, `Helpers/FluidSurfaceResolver.cs`,
`Rendering/SubmersionOverlay.cs`, `Rendering/UnderwaterOverlayRendererFeature.cs`,
`Shaders/UnderwaterOverlay.shader`, `Shaders/UberLiquidShader.shader`,
`Shaders/Includes/LiquidCore.hlsl`, `World.cs` (`OnEnable`/`OnDestroy`/`OnBeginCameraRendering`,
`PublishSubmersionGlobals`, `GatherEyeSubmersion`, `TryResolveEyeCell`, `MeasureHorizontalExtent`,
`FluidReachCells`, `TopOfFluidBody`, the submersion constants and property IDs),
`Data/BlockType.cs`, `Helpers/VoxelMeshHelper.cs` (the fluid meshing path's resolver calls),
`Audio/SoundManager.cs:395-412`, `Assets/Editor/Validation/UnderwaterRender/` (all six files) and
`Assets/Editor/Validation/Framework/ValidationSuiteRegistry.cs`. Renderer-feature order, the depth
copy mode and the URP texture requirements were read out of
`Assets/settings/Rendering/VoxelEngine-URP-Renderer.asset` and `VoxelEngine-URP-Asset.asset` by
resolving `m_RendererFeatures`' `fileID`s to their scripts. Authored fluid values were read from
`Assets/Resources/Data/BlockDatabase.asset`. Constants were extracted from source, never
transcribed. No runtime capture was taken this session; the in-game confirmations cited are the
dated ones from the design's play passes.

**Relationship to other documents:**

- [`../Design/ANIMATED_LIQUID_SURFACE.md`](../Design/ANIMATED_LIQUID_SURFACE.md) — **UW-5**, paused.
  Holds the mesh-displacement plan and the three findings the reverted screen-space band banked.
- [`FLUID_SHORELINE_RENDERING.md`](FLUID_SHORELINE_RENDERING.md) — owns the liquid shader's
  vertex-channel contract and shore math. This system changes that shader's `Cull` state and
  nothing else.
- [`SUB_VOXEL_COLLISION_SYSTEM.md`](SUB_VOXEL_COLLISION_SYSTEM.md) — §7 owns `FluidContact`, the
  *body*-AABB fluid query. The eye query is its sibling, and the two use different surface-height
  sources on purpose (§1.5).
- [`SKY_AND_CELESTIAL_RENDERING.md`](SKY_AND_CELESTIAL_RENDERING.md) — owns `VoxelFog.hlsl` and
  `World.SetGlobalLightValue`. The submersion globals are published beside the sky's, from a
  different hook (§4).
- [`UI_BLUR_BACKDROP_SYSTEM.md`](UI_BLUR_BACKDROP_SYSTEM.md) — shares this system's injection point;
  renderer-feature order between the two is load-bearing (§3.1).
- [`../Design/SOUND_ENGINE_DESIGN.md`](../Design/SOUND_ENGINE_DESIGN.md) — the ambience low-pass
  filter is the eye query's second consumer (§6).
- [`../Design/VOLUMETRIC_AND_RAYTRACED_EFFECTS_REPORT.md`](../Design/VOLUMETRIC_AND_RAYTRACED_EFFECTS_REPORT.md)
  — `VX-3` on `VX-5` is what replaces the horizontal box with an exact per-pixel march (§7).
- [`../Bugs/_FIXED_BUGS.md`](../Bugs/_FIXED_BUGS.md) — **Fluid #21**, the bug this system closed.

---

## ID index

Every ID the `UW-*` space ever issued. IDs are never recycled and never dropped: commit messages
and code comments cite them.

| ID | Scope | Where it now lives |
|----|-------|--------------------|
| **UW-0** | `submersionColor` + `submersionDensity` on `BlockType`, surfaced in the `BlockEditor` | §5 |
| **UW-1** | `Cull Off` on `UberLiquidShader`'s `LiquidForward` pass | §2 |
| **UW-2** | `EyeSubmersion`, `Helpers/FluidSurfaceResolver`, `World.GatherEyeSubmersion` | §1 |
| **UW-3** | The ambience low-pass filter reading the shared query | §6 |
| **UW-4** | `UnderwaterOverlayRendererFeature`, `UnderwaterOverlay.shader`, `SubmersionOverlay`, `World.PublishSubmersionGlobals` | §3, §4 |
| **UW-5** | A wobbling waterline. ⏸️ **Paused 2026-09-05, no code.** A screen-space band was built and reverted; the mesh-displacement route is the one that remains | [`../Design/ANIMATED_LIQUID_SURFACE.md`](../Design/ANIMATED_LIQUID_SURFACE.md); its consequence is §7's hard-edge limitation |
| **UW-6** | Lava's authored look, the publish moved to `beginCameraRendering`, the authored tint's color space | §4, §5 |

---

## 1. The eye query

`World.GatherEyeSubmersion(Vector3 unityEyePos, out EyeSubmersion, bool measureExtent = true)` is
the single answer to "what is the camera looking through". It takes a world-space point rather than
a camera or a player, so a future spectator or cutscene eye needs no second query.

```
RenderPipelineManager.beginCameraRendering (player camera)
        │
        └─▶ World.PublishSubmersionGlobals                    ── SoundManager (~4 Hz, measureExtent: false)
                    │                                                       │
                    ▼                                                       ▼
        World.GatherEyeSubmersion(unityEyePos, out EyeSubmersion) ◀──────────┘
                    │   voxel lookups + managed BlockType palette live here
                    ├── World.TopOfFluidBody         ── walks up to the drawn top face
                    ├── World.MeasureHorizontalExtent ── 4 × World.FluidReachCells
                    └── Helpers.FluidSurfaceResolver  ── pure static math over value types
                            ├── SmoothedCornerHeights(...)  ── shared with ──▶ VoxelMeshHelper
                            ├── SurfaceCornerHeights(...)        GenerateFluidMeshData
                            └── SampleSurfaceAt(corners, fracX, fracZ)
                    │
        ┌───────────┴─────────────────────┐
        ▼                                 ▼
  SubmersionOverlay.Pack →          SoundManager
  7 shader globals +                ambience low-pass
  SubmersionOverlay.Active                (§6)
        │
        ▼
  UnderwaterOverlayRendererFeature  ──▶  UnderwaterOverlay.shader
  AfterRenderingTransparents, index 0         (§3, §4)
```

### 1.1 `EyeSubmersion`

`Helpers/EyeSubmersion.cs`, a `struct`:

| Field | Meaning |
|---|---|
| `Type` | The fluid at the eye, or `FluidType.None`. |
| `SurfaceY` | Unity-space Y of the fluid's **drawn** surface at the eye's XZ. The top of the whole **body**, not of the eye's own cell (§1.3). |
| `EyeDepth` | `SurfaceY − eyeY`. **Signed**: negative when the eye is above the surface, reported anyway so a waterline has a plane to track as the eye breaks through. |
| `SubmersionColor` | The fluid's authored **sRGB** tint, or `default` in air. Converted to linear at pack time (§4.1). |
| `SubmersionDensity` | Authored extinction per block. |
| `HorizontalExtent` | Distance to the body's edge in blocks: `x` = −X · `y` = +X · `z` = −Z · `w` = +Z (§1.4). |
| `IsSubmerged` | `Type != None && EyeDepth > 0f`. The one boolean the tint and the muffling both switch on. |

An eye in air carries `default`, so "am I under a surface" is always the same test.

### 1.2 Cell search and soft failure

The query opens with two disposal guards — `FluidVertexTemplates` and `JobDataManager`, both on
`IsDisposed` rather than on the arrays' `IsCreated`, which stays true after disposal — and returns
`default` when either has gone. Edit-mode fixtures build a `World` without `StartWorld`, and the
publish path can outlive a world unload.

WS-4: the Unity → voxel conversion happens once, here, through `WorldOrigin.UnityToVoxelCell`. The
fractional part is read straight off the Unity position, because the origin is whole cells.

The search is **two cells deep**:

1. The eye's own cell owns the answer when it holds a fluid.
2. Otherwise the cell **below** is probed. It cannot submerge the eye — its surface is at most its
   own ceiling — but it supplies the surface a waterline tracks while the eye sits just above
   water, and `EyeDepth` comes back negative.

### 1.3 Resolving the drawn surface

Once a fluid cell is found, `World.TopOfFluidBody` walks **up** the column while the cell above
holds the same fluid, and the corner smoothing is evaluated on that topmost cell — the one whose
top face is actually drawn. An interior cell has its drawn corners forced flat, so reading the
surface off the eye's own cell would report that cell's ceiling and make `EyeDepth` reset at every
boundary a sinking eye crosses. The walk terminates at the first non-matching cell or where
`TryGetVoxel` fails above the world, so it is bounded by the depth the player has swum to, at one
voxel read per cell.

The geometry itself is `Helpers/FluidSurfaceResolver`, and it is the **single source** of that
surface: `VoxelMeshHelper.GenerateFluidMeshData` builds its top-face vertices from the same three
functions (`SmoothedCornerHeights` → `HasSameFluidAbove` → `SurfaceCornerHeights`), so the surface
the query reports and the surface the player sees cannot drift apart by re-implementation.

- `SmoothedCornerHeights` averages the cell with its orthogonal and diagonal same-fluid neighbors
  per corner, each clamped up to `FluidSurfaceResolver.MinSurfaceHeight` (`0.005f`). Corner naming
  follows the mesher's top-face vertex order: `BL` = the cell's `(0,0)` XZ corner, `BR` = `(1,0)`,
  `TL` = `(0,1)`, `TR` = `(1,1)`.
- `SurfaceCornerHeights` forces all four corners to `1.0` when the same fluid sits directly above,
  which is what lets a submerged cell connect seamlessly to the one above it. On the eye path this
  is always false — `TopOfFluidBody` stopped exactly where the fluid ends — but it is computed
  anyway so both callers run the same shape.
- `SampleSurfaceAt` is **bilinear**: `lerp(BL, BR, fracX)` and `lerp(TL, TR, fracX)`, then between
  those two by `fracZ`.

`SurfaceY` is `surfaceCell.y + SampleSurfaceAt(...)`. Voxel Y carries no origin offset (WS-4), so
the cell floor doubles as its Unity-space Y.

The resolver is pure static math over value types and `NativeArray`, with no managed references —
the same posture as `Physics/FluidContactResolver`, so it stays job-callable.

### 1.4 The body is a box, not a half-space

`SurfaceY` describes a **plane**, and a plane runs to the horizon while a pool may be three blocks
wide. Two separate bounds keep the medium inside the actual body.

**Above the surface, nothing is charged at all.** The shader's `SubmergedRayLength` returns zero
whenever `eyeDepth <= 0`, and `_SubmersionColor.a` gates on `IsSubmerged`. That is exact rather
than a simplification: a ray that *does* reach water ends at the water, because the liquid mesh is
a closed shell that writes depth — so the pixel shows the surface as the liquid shader drew it,
never a column of water seen through.

**From inside, the plane is only the body's lid**, and the sides need bounding too. The depth
buffer cannot do it: at a shoreline the nearest boundary face sits centimetres from the eye,
**inside the near clip plane**, so it is never rasterized and depth reports the terrain beyond it.
`World.MeasureHorizontalExtent` therefore measures the body's reach along ±X and ±Z at the eye's
own height, and the fragment clamps each ray with a slab-exit test.

Each direction is one call to `World.FluidReachCells`, which scans up to
`World.FluidExtentScanCells` (**32**) cells and reports the **farthest** cell still holding the
fluid — not the first gap. The two differ whenever something stands in the water, and the
difference is severe: a single block six cells out was measured in game shortening that side from
23 cells to 6, thinning the medium across a quadrant of the view. Passing over a **solid** gap is
correct rather than lenient, because a solid block inside the body is an *occluder* and
`rayDistance` already stops each ray at it, while every other ray on that side stops being starved
of the water genuinely there.

**Air ends the scan.** That reasoning turns entirely on the block being an occluder, and nothing
stops a ray crossing a dry gap — so counting a second pool beyond one would charge every ray on
that side for water it never enters and fog the dry air between them. `FluidReachCells` breaks on
`BlockIDs.Air` and passes over everything else, so a submerged torch or plant still cannot shorten
the medium around it. An unreadable or out-of-world cell also ends the scan, at the last distance
proven to be fluid — the medium is understated while a chunk loads, never overstated.

A direction that runs the full scan without leaving the fluid returns `-1`, which
`MeasureHorizontalExtent` publishes as `World.UnboundedFluidExtent` (`1e6f`) rather than as the
scanned distance, so open water is never clamped to the scan's reach. Only distances leave the
method, never coordinates, so it is indifferent to the world origin (WS-4).

The extent scan is the expensive half of the query and runs only when `measureExtent` is true
**and** the eye is actually submerged. `SoundManager` passes `false`; it reads only `IsSubmerged`.

### 1.5 Which surface height, and why it differs from physics

Two queries answer different questions and are supposed to differ:

| Query | Consumer | Height source | Why |
|---|---|---|---|
| `World.GatherFluidContact` | Physics | Logical per-cell template | A body's buoyancy must not depend on the smoothing its neighbors happen to induce. |
| `World.GatherEyeSubmersion` | Rendering, audio | Corner-smoothed, bilinear | The tint boundary must sit where the **drawn** surface is. |

The logical template can sit up to about half a block off the drawn surface at a sloped pool edge,
and the waterline is precisely the effect that makes that visible.

---

## 2. Seeing a fluid body from inside it

`UberLiquidShader`'s `LiquidForward` pass declares `Cull Off`. The fluid mesher emits a shell whose
faces all point *away* from an interior camera — top face unless the same fluid is above, bottom
only over transparent non-same-fluid, sides culled against effectively-full-height same-fluid
neighbors — so back-face culling was what hid the water from a submerged player.

The unconditional `Cull Off` is safe because of a property the shader already had: the fragment's
only two readers of `worldNormal` are `GetShoreData`'s axis routing and `RouteFlowTo3D`, and
**both apply `abs()` first** (`LiquidCore.hlsl:283`, `:322`). A negated normal is a no-op through
the whole fragment — shore-mask decoding, flow routing, lighting (which reads vertex `lightData`,
not the normal) and fog are all unaffected. No fragment code changed.

Correct layering survives because the pass writes opaquely with `ZWrite On`: where a front and a
back face of the same body both cover a pixel, the depth test keeps the nearer one outright. The
cost is raster setup on triangles previously discarded at the culling stage; the fragments are
mostly early-Z rejected, since the pass contains no `clip`/`discard` and writes no depth from the
fragment.

No varying was added — `LiquidV2F` still uses 11 of 15 interpolators
(`TEXCOORD0`–`TEXCOORD10`; `SV_POSITION` does not count toward the budget).

---

## 3. The overlay pass

`Rendering/UnderwaterOverlayRendererFeature`, a RenderGraph `ScriptableRendererFeature`.

`Create()` follows the `UIBlurRendererFeature` contract — it runs again on every domain reload and
inspector edit with no matching `Dispose`, so it both clears stale state and stays idempotent, and
warns and disables itself when no shader is assigned. It builds the pass at
`RenderPassEvent.AfterRenderingTransparents` and declares
`ConfigureInput(ScriptableRenderPassInput.Depth)`.

`AddRenderPasses` enqueues nothing unless all three hold: the camera is `CameraType.Game` (unlike
the UI blur, which also runs in the scene view — the active flag is driven by the *player's* eye),
the material and pass exist, and `SubmersionOverlay.Active` is true. That last gate is why the
feature needs **no graphics setting**: "off" and "submerged in nothing" already cost the same, so a
toggle would only restore the bug.

`RecordRenderGraph` reads `resourceData.cameraDepthTexture`, and when it is invalid logs **once**
per pass instance and returns — the fog degrades to nothing rather than erroring, and the URP
asset's Depth Texture option being off is the only way to reach it. The warn flag is an instance
field, not a static, because `Create()` builds a fresh pass on every domain reload.

The composite is **one raster pass with no copy of the camera color**. The effect is
`lerp(scene, tint, fog)`, which is exactly what the shader's `SrcAlpha OneMinusSrcAlpha` blend
computes against the attachment — so the pass reads only depth and needs neither a fullscreen temp
nor a second blit. The attachment is declared `AccessFlags.ReadWrite`: the blend reads the
destination, so a write-only declaration would let the graph treat the prior contents as
expendable.

### 3.1 Ordering, and why this event

`BeforeRenderingPostProcessing` would let lava's glow earn bloom, but
`GraphicsSettingsController.ApplyBloom` disables `renderPostProcessing` outright whenever bloom is
off or the scene has no `Volume` — the submersion look would then differ across a setting that has
nothing to do with it. Running after transparents makes the overlay identical in every
configuration; lava's glow is carried by its authored color instead.

`VoxelEngine-URP-Renderer.asset` lists three features, and the order is load-bearing:

| Index | Feature | Event |
|---|---|---|
| 0 | `UnderwaterOverlayRendererFeature` | `AfterRenderingTransparents` |
| 1 | `UIBlurRendererFeature` | `AfterRenderingTransparents` |
| 2 | `CloudPrepassRendererFeature` | `AfterRenderingSkybox` |

URP records same-event custom passes in renderer-feature **list order**, and the blur samples
`activeColorTexture` to build the HUD's frosted backdrop — a blur recorded first would show an
untinted world behind every panel while the rest of the screen is tinted. `B17` asserts the
**index**, not just membership; a membership-only check passes with that bug present.

The renderer runs `m_CopyDepthMode: 1` (`AfterTransparents`), and the URP asset has
`m_RequireDepthTexture: 1`, `m_RequireOpaqueTexture: 1`, `m_OpaqueDownsampling: 1`, `m_MSAA: 2`.
URP derives the depth-copy schedule from the earliest *declared* depth reader, which is what the
pass's `ConfigureInput` keeps true if the asset's depth requirement ever changes.

Two consequences of the copy mode are permanent, and §7 records them: `_CameraDepthTexture`
contains the liquid surface, so the fog measures to the nearest fluid face rather than to the
terrain behind it.

---

## 4. The publish path and the shader globals

`World.PublishSubmersionGlobals` runs from `RenderPipelineManager.beginCameraRendering`, subscribed
in `OnEnable` and unsubscribed in `OnDisable`. URP raises that event inside the scope that also
calls `AddRenderPasses`, so the globals **and** the pass's active flag describe the frame about to
be drawn. The handler's guard is `if (_playerCamera != null && camera != _playerCamera) return;` —
a lost player camera still falls through, because the publish's own null check is what disarms the
pass; returning early on any non-matching camera would leave the overlay armed with the last
frame's tint.

It is independent of `PublishSkyGlobals`, which stays under `SetGlobalLightValue` and returns
early without a clock or authored `TimeOfDaySettings` — and the medium the player is swimming
through has nothing to do with the time of day.

Without a camera the publish disarms the pass and drops the easing's primed flag, but leaves the
globals as they are rather than zeroing them — edit-mode fixtures build a `World` without
`StartWorld`, and a suite that set the globals deliberately must not have them stomped
mid-scenario. `World.OnDestroy` disarms `SubmersionOverlay.Active` too, for the same reason
`RestoreSkyRenderSettings` runs there: quitting to the menu while submerged would otherwise leave
the pass enqueueing over the menu with the last frame's tint still in the globals.

A dry result publishes exactly as unconditionally as a submerged one — the overlay's whole answer
to "am I under water" is the alpha it reads, so stopping at the surface would leave the screen
tinted after the player climbs out.

### 4.1 The seven globals

Packed by `SubmersionOverlay.Pack` into a `SubmersionGlobals` readonly struct, then pushed through
cached `Shader.PropertyToID`s. All of them live in **Unity/render space** (WS-4), matching every
other global the block and liquid shaders consume.

| Global | Contents |
|---|---|
| `_SubmersionColor` | `rgb` = the authored fluid tint **converted to linear** · `a` = 1 while the eye is under a fluid surface, 0 otherwise |
| `_SubmersionParams` | `x` = extinction per block · `y` = the eye's **signed** depth below the drawn surface, positive submerged · `z` = meniscus half-width (UW-5, 0 today) · `w` = distortion amount (v2, 0 today) |
| `_SubmersionRayParams` | `xy` = the view frustum's half-extents at unit depth (`tan(fov/2) · aspect`, `tan(fov/2)`) · `zw` unused |
| `_SubmersionRayBasisX/Y/Z` | The rows of the camera's world rotation — `xyz` = the world-space X, Y and Z components of its right, up and forward axes. A row at a time, because the fragment consumes them as dot products against a camera-space ray |
| `_SubmersionBounds` | Distance to the body's edge in blocks: `x` = −X · `y` = +X · `z` = −Z · `w` = +Z. `World.UnboundedFluidExtent` means "no edge within the scan" |

A zero `_SubmersionColor.a` means "not submerged", which is what uninitialized globals give — the
same fail-safe convention `VoxelFog.hlsl` uses for its zero-width range.

**The alpha is a gate, not a fade.** It is 1 while `EyeSubmersion.IsSubmerged` and 0 otherwise,
never an intermediate value. How much medium a pixel looks through is decided per pixel, in the
shader, from the eye depth and that ray's direction. Gating on `IsSubmerged` is also what puts the
tint and the ambience low-pass filter on exactly the same boundary; `B16` asserts that with
`ExactValue` rather than a tolerance, because an epsilon would accept the very intermediate value
the baseline exists to forbid.

**Only the eye's signed depth crosses the wire, never `SurfaceY` itself.** The shader solves the
crossing from `surfaceY − eyeY` alone, so the camera's world position never enters the fragment and
no large-magnitude world coordinate does either.

**The ray basis is published rather than derived in the shader.** The obvious route is
`ComputeWorldSpacePosition`/`UNITY_MATRIX_I_VP` and would need no global at all, but that matrix is
unsettable outside a real camera render, so the distance reconstruction could then only be
validated behind an `#ifdef` — gating a *different* code path than the one that ships. Publishing a
handful of floats makes the arc's most error-prone arithmetic testable against the real fragment.
It is also resolution-independent, so it survives the render-scale changes
`GraphicsSettingsController` makes at runtime. All three rows are published because the horizontal
box needs the ray's world XZ; `Y` alone would carry only the surface plane.

**The authored tint is converted at pack time.** `submersionColor` is authored through a color
picker, so it is sRGB, but `Shader.SetGlobalColor` performs no conversion and the overlay blends
against a linear target — consumed raw, every tint reached the screen lighter and less saturated
than its swatch. `Pack` converts with `.linear`, so the picker's swatch means what it shows. The
error is invisible on a color already tuned by eye, because the tuning absorbs it, and worst where
a channel is small and the curve steepest: it surfaced on lava, where an authored deep red
`(0.80, 0.11, 0.094)` rendered as salmon `(0.91, 0.37, 0.34)`.

### 4.2 Easing the extents

The extents are re-measured from whichever cell the eye occupies, so they **step** at every cell
boundary — and crossing one *vertically* re-scans all four directions at once, which reads as the
whole medium jumping. Measured on a terraced pool: `EyeDepth` stayed continuous across the boundary
(0.855 → 0.895) while the extents stepped 2.50 → 6.50 together.

`SubmersionOverlay.StepExtents` therefore eases the published values with a framerate-independent
exponential approach over `SubmersionOverlay.ExtentDampTime` (`0.2f` seconds), and **snaps** on the
first publish after the eye enters a fluid — easing from a stale body is worse than stepping, since
entering water would sweep the fog in from whatever pool was last swum in. The primed flag is
dropped whenever the eye is dry.

Two details are load-bearing rather than incidental:

- **The easing lives in `World.PublishSubmersionGlobals`, never in the query.**
  `GatherEyeSubmersion` must stay pure, because `SoundManager` polls it on its own cadence and a
  stateful query would let the audio layer perturb what the screen shows. The state
  (`_submersionExtent`, `_submersionExtentPrimed`) is instance state on `World`.
- **It interpolates `1 / (1 + d)`, not the raw distance.** `World.UnboundedFluidExtent` is
  enormous, so easing linearly from open water into a two-block channel would spend seconds at
  values that bound nothing — the very over-fogging the extents exist to prevent. The reciprocal
  also matches how the distances read on screen, where 30 blocks and 300 look identical but 1 and 3
  do not. The result is clamped to the sentinel's own reciprocal so an eased extent stays inside
  the range the shader's `SUBMERSION_UNBOUNDED` declares.

Easing buys **no accuracy** — the box is exactly as wrong as before, just no longer wrong
discontinuously — and it adds a deliberate artifact: the bound lags a fast swimmer, so entering a
narrow channel briefly over-fogs. That trade is taken because a lag reads as the medium settling
while a step reads as a bug.

### 4.3 The fragment

`Assets/Shaders/UnderwaterOverlay.shader`, `Hidden/Voxel/UnderwaterOverlay`. Render state:
`ZTest Always ZWrite Off Cull Off`, `Blend SrcAlpha OneMinusSrcAlpha`, `#pragma target 4.5`.

```
strength = _SubmersionColor.a            // uniform across the draw: a free branch, and it
if (strength <= 0) return 0              //   skips the depth fetch entirely when dry

ndc         = uv * 2 - 1                 // plain, NO UNITY_UV_STARTS_AT_TOP compensation
viewRay     = float3(ndc * _SubmersionRayParams.xy, 1)
rayLength   = length(viewRay)            // the true view ray, not the forward axis
rayDistance = LinearEyeDepth(SampleSceneDepth(uv)) * rayLength

worldRayDir = dot(viewRay / rayLength, _SubmersionRayBasis{X,Y,Z}.xyz)
submerged   = SubmergedRayLength(_SubmersionParams.y, worldRayDir, rayDistance)
fog         = saturate(1 - exp(-_SubmersionParams.x * submerged))

return half4(_SubmersionColor.rgb, fog * strength)
```

`SubmergedRayLength` is the per-pixel solve. Submersion is a property of a **ray**, not of the
camera: whether a pixel looks through water depends on where its ray goes, so gating the whole
effect on a scalar derived from the eye alone let a player at the waterline clear the fog while
half the screen was underwater.

```
eyeDepth <= 0                       →  0                     (the plane-versus-body gate, §1.4)
rayUpwardness <= 0 (level/falling)  →  rayDistance           (never exits through the surface)
rayUpwardness  > 0 (rising)         →  min(eyeDepth / rayUpwardness, rayDistance)
then clamped by SlabExitDistance on X and on Z               (the box's sides, §1.4)
```

`SlabExitDistance` returns `SUBMERSION_UNBOUNDED` (`1e6`, matching `World.UnboundedFluidExtent`)
for a ray with no travel on that axis, which can never cross either of its faces.

The waterline emerges as the locus where the submerged length reaches zero — it is not drawn, it
falls out. That is why the split is geometrically exact and tracks pitch *and* roll.

**The fog law is deliberately not `VoxelFog.hlsl`'s.** That fog is horizontal-XZ, radial and
back-loaded by `pow(t, exponent)` because its job is to conceal the loaded-chunk boundary without
dissolving the ground under a flying player. Underwater fog is a *medium*: it attenuates along the
actual view ray, in all three axes, from zero distance — so Beer–Lambert `1 - exp(-density · d)`.
Both are simultaneously live and that is correct. Sky pixels sit at the far plane and saturate
without a special case.

⚠️ **Do not add a UV flip here.** `Blit.hlsl`'s `GetFullScreenTriangleTexCoord` **already** flips V
on platforms whose textures start at the top, so a shader that compensates a second time under
`UNITY_UV_STARTS_AT_TOP` inverts the result: it fogs the **sky** and leaves the water clear,
visible as a plane across the view within roughly ±20–30° of level. The correct mapping is the
plain `uv * 2 - 1`, and it is pinned by `B20`, which *measures* rather than reasons — it draws a
marker across the bottom half of **clip space**, where `y = -1` is the bottom of the view by
definition, and asserts the fogged rows are the marker's rows. A flip anywhere in the
texture/readback chain moves both together, so the assertion needs no platform assumption.

---

## 5. Authoring

Two `public` fields on `BlockType`, under the existing `[Header("Fluid Properties")]`, tuned
through the `BlockEditor` into `BlockDatabase.asset` exactly as the fluid physics coefficients are:

| Field | Meaning | Range |
|---|---|---|
| `submersionColor` | The medium's color; the fog target and the tint at zero depth. | `Color`, sRGB off the picker |
| `submersionDensity` | Beer–Lambert extinction per block of view distance. | `[Range(0f, 4f)]` |

Authored values as they stand:

| Fluid | `submersionColor` (sRGB) | `submersionDensity` |
|---|---|---|
| Water | `(0.31332, 0.527146, 0.735358)` | `0.05` |
| Lava | `(0.85, 0.30, 0.05)` | `1.5` |

Water's value is the sRGB **re-expression** of the linear color it had been rendering with before
the color-space fix, which round-trips to within 3 × 10⁻⁵ — below one 8-bit step — so its confirmed
look was preserved rather than re-tuned. Lava is a hot orange authored after the fix, near-opaque
within about a block: sinking into a lava pool loses sight of the surface.

These are **not** save data. `BlockType` is a ScriptableObject; new serialized fields take their
initializers on load. No chunk-format change, no `level.dat` bump, no AOT migration step. The
`BlockType` defaults still carry the pre-tuning water values (`(0.11, 0.30, 0.42)` at `0.14`),
which is what a newly authored fluid starts from.

The tuning itself is unbaselinable by construction — the suite builds its own densities and colors
and reads nothing from `BlockDatabase.asset`, which is also why retuning a fluid cannot red it.

---

## 6. The audio consumer

`SoundManager` resolves the listener's head from `Camera.main.transform` at ~4 Hz and calls
`world.GatherEyeSubmersion(_listener.position, out EyeSubmersion, false)`, reading only
`IsSubmerged` to drive the ambience low-pass filter. `measureExtent: false` keeps the audio path
from paying for the four extent scans it would discard.

The whole runtime delta from the earlier per-cell test is *which predicate* produces
`SoundManager`'s `bool submerged` — the low-pass sweep, the crossfades, the dwell filtering and the
cadence are untouched. Sampling at the audio layer's own rate is safe because the query is pure and
carries no physics timing; that was the stated reason for keeping ambience off
`VoxelRigidbody.FluidContact`, and the eye query is neither a collider nor physics-timed.

The gain is sub-cell: a head just under a partly-filled surface now reads submerged, where the
per-cell test read dry until the head entered the cell below. Because both consumers read the same
`IsSubmerged`, the tint and the muffling are driven by one boolean rather than by two agreeing
tests — `B16` pins that bit-exactly.

---

## 7. Limitations and accepted trade-offs

Consequences to state plainly, not open questions.

- **The fluid body is approximated by a box, so an L-shaped or terraced body under-fogs.** The four
  extents are single 1-D probes at the eye's height, so a pool that bends, or that widens below the
  eye, reports narrower than it is and the medium thins out early down that arm. The error is
  one-directional by design — under-fogging reads as "the water is clear here", where over-fogging
  reads as a bug. **The exact fix has a home:** `VX-3` (volumetric water) riding on `VX-5` (voxel
  DDA trace substrate). Marching fluid occupancy per pixel removes the box's shape error *and* its
  per-cell stepping in one move — the answer stops depending on the eye's cell at all.
  `_SubmersionBounds` and `World.MeasureHorizontalExtent` are the pieces that would be deleted. No
  further tuning of the box is planned; the next move on this axis is the volumetric path.
- **The eased bound lags a fast swimmer**, so entering a narrow channel is briefly over-fogged for
  about `ExtentDampTime` (§4.2). Deliberate: a lag reads as the medium settling, a step reads as a
  bug.
- **One edge of the extent scan is left open:** fluid at exactly `FluidExtentScanCells` with solid
  in between still reports the body as unbounded on that side.
- **The four extent scans re-run every rendered frame while the eye is submerged.** Reviewed
  2026-09-05 and deliberately left alone: memoizing them would be exactly output-equivalent, but it
  needs invalidation on every fluid edit, and that is a staleness surface bought against an
  unmeasured cost.
- **The waterline is a hard edge.** The per-pixel solve gives a geometrically exact split, but
  nothing softens it — no meniscus band, no wobble, so at the surface the boundary is one pixel
  wide. ⏸️ **A limitation by choice, not by consequence**, and the only entry here with a known way
  out: see [`../Design/ANIMATED_LIQUID_SURFACE.md`](../Design/ANIMATED_LIQUID_SURFACE.md) (UW-5).
- **A strongly sloped surface under the eye is approximated flat.** The screen splits on a flat
  plane at `SurfaceY`; where the smoothed surface tilts steeply — a shallow shore cell between a
  full cell and dry land — the drawn line sits off the true surface, and at shallow depth the gap
  is worse than "slightly". Measured, not predicted: it was the first of the two things that sank
  the reverted screen-space band.
- **The overlay's fog measures to the nearest fluid face, not to the terrain behind it.** This
  renderer copies depth after transparents, so the liquid surface is in `_CameraDepthTexture`.
  Accepted rather than fixed: fog that ends where the medium ends is the more physical reading, and
  flipping `m_CopyDepthMode` would force an earlier depth copy on every frame of the whole project
  for a look that is arguably worse.
- **Any other transparent geometry shortens the fog the same way.** Nothing else currently writes
  depth from the transparent queue, but a future transparent effect that does will pull the
  underwater fog in front of the terrain without any change to this system.
- **The resolver's bilinear surface can differ from the rasterized two-triangle surface along the
  quad diagonal**, whose orientation the mesher may flip by light value (§1.3).
- **Two fluid layers never blend.** The liquid pass writes opaquely and composites against
  `_CameraOpaqueTexture`, so a distant water wall seen from underwater shows one layer, as it does
  from outside. Pre-existing, not introduced here.
- **Transparent geometry other than clouds is still invisible through a water surface** — glass and
  leaves — because the liquid fragment's `SampleSceneColor` reads `_CameraOpaqueTexture`, which URP
  fills after the skybox but before transparents. Clouds were fixed by `CL-9`'s pre-transparent
  cloud pass, not by anything here; everything else would need the same treatment. Owned by the
  `CL-*` cloud backlog.
- **The overlay is a camera effect.** It does not change what the chunk shaders draw, so an
  individual block half in and half out of water is not treated per-block.
- **The fog starts at zero.** Pure Beer–Lambert means a block held right up to the eye is
  essentially untinted, and only distance thickens the medium — a flat floor is what would make it
  a filter rather than a medium. The feel pass did not call for a minimum weight, so there is none.
- **`SubmersionOverlay.Active` is a mutable static with two owners** — republished every frame
  while a world lives, cleared on play-mode entry via `[RuntimeInitializeOnLoadMethod]`, and
  cleared again in `World.OnDestroy`. A third teardown path that bypasses `OnDestroy` would reopen
  the "armed over the menu" case.
- **The distortion seat (`_SubmersionParams.w`) costs nothing today but is not free later.** The
  single-pass composite never reads the camera color, so a v2 distortion that offset-samples the
  scene needs a source texture the pass does not have: either `_CameraOpaqueTexture` (already
  enabled, and losing only the liquid surface from the distortion source) or restructuring the pass
  into copy-then-blit at that point. No C# plumbing has to change.

---

## 8. Validation

`Minecraft Clone/Dev/Validate Underwater Render` —
`Assets/Editor/Validation/UnderwaterRender/`, namespace `Editor.Validation.UnderwaterRender`,
registered in `ValidationSuiteRegistry` as `"Underwater Render"` so `Validate All` and the CI entry
point pick it up. **26 baselines, `B1`–`B26`.** Following the `UIBlurRenderValidationSuite` model:
arithmetic assertions against computed values, never checked-in golden images (GPU output is not
bit-reproducible across drivers), and **INCONCLUSIVE** under `-nographics` for the device-bound
ones.

| Group | Harness | What it pins |
|---|---|---|
| `B1`–`B3` | `LiquidFaceRenderer` (device) | The liquid material draws at all; **both** windings of a fluid quad survive, neither culled; a negated normal shades identically. |
| `B4`–`B9` | `FluidSurfaceFixture` / `EyeSubmersionFixture` (device-free) | Not the smoothing arithmetic — the mesher and the query share one resolver, so asserting the values would assert a helper against itself. What sharing does *not* fix: which resolver corner lands on which **emitted** vertex and which axis each of `SampleSurfaceAt`'s fractions addresses (`B4`, read off real `GenerateFluidMeshData` output over a neighborhood smoothed to four different heights, so a transposed assignment is observable), the interior sample, the fluid-above override, the minimum-height floor, the two-cell search, and the disposed-world soft failure. |
| `B10`–`B26` | `OverlayFragmentRenderer` + packing (mixed) | The overlay's fragment and wire format. |

Baselines that carry a specific lesson:

- **`B10` is the positive control and must be read first** — "the overlay drew nothing" is the same
  reading the pass-through scenarios (`B13`, `B15`) call success.
- **`B12` earns its keep.** At one uniform depth it measures three screen radii, because the
  ray-length scale is this system's most error-prone arithmetic and a center-only check passes with
  it missing entirely — proven by mutation, center green while edge and corner went red.
- **`B16` and `B17` are deliberately not device-gated**, so a headless run still asserts something.
  `B16` pins the packing: the gate never takes an intermediate value, opens exactly when
  `IsSubmerged` does, and pitch and roll reach the published basis. `B17` reads back
  `m_RendererFeatures` and asserts the overlay is present, its shader is **assigned**, and its
  index is below `UIBlurRendererFeature`'s — three silent failures no render scenario can see.
- **`B18`** sinks an eye across two cell boundaries and pins that `SurfaceY` does not move, that
  `EyeDepth` deepens monotonically, and that the published depth tracks it.
- **`B19` and `B20` are separate failures and need separate baselines.** `B19` pins the split's
  *structure* (pitched down every ray submerged, pitched up none, level at the surface it splits,
  rolled 180° the halves swap, deep it stops splitting); every one of its assertions passes with
  the vertical sign backwards, which is the gap `B20`'s clip-space marker closes.
- **`B21`** asserts the shader's own above-surface guard with the gate forced open, so the fragment
  is covered independently of C# declining to draw.
- **`B22`** reproduces the measured shoreline frame — a body ending 2 cm west and unbounded east —
  with an open-water control proving it is the bound that changed rather than the sampling.
- **`B23`** is device-free because the easing is a pure function, which is what makes the two
  things most likely to be wrong reachable at all: the **snap** on entering water, and the
  reciprocal **space**, where a linear interpolation would still read ~630 000 blocks one time
  constant out of open water.
- **`B24` and `B25` are two halves of one rule** and must be read together: an obstruction changes
  the reach by *nothing*, while a dry gap ends the body — filling that same gap with solid restores
  the far reach, so the fix for one cannot trade away the other.
- **`B26`** asserts the sRGB→linear packing against literals computed **outside** the engine;
  asserting against `Color.linear` would only restate the call under test. It carries its own
  tolerance, because the suite's normal `COLOR_EPSILON` (half-float quantization) is *coarser* than
  the gap between the exact sRGB curve and a `pow(2.2)` approximation on green and blue — only red
  separates them there.

`ExactValue` rather than a tolerance is used for the gate, for the fields `Pack` copies through
untouched, and for `B24`'s "changes by nothing"; the measured composites keep their epsilon,
because a half-float render target genuinely has one.

**Not baselined, and why:**

- **The assembled pipeline.** The render suites exercise the shaders and `B17` exercises the
  wiring; only in-game play exercises the two together.
- **`AddRenderPasses`' "enqueue nothing while dry" gate.** Asserting it needs a real
  `ScriptableRenderer` and a populated `RenderingData`, neither fabricable in edit mode. `B16`
  covers the strength that drives it, and the shader's zero-strength early-out (`B15`) means a
  wrongly-enqueued pass still draws nothing — the exposure is a wasted fullscreen triangle, not a
  visual defect.
- **The publish point and the camera-lost guard.** `PublishSubmersionGlobals` is private and needs
  a live camera.
- **The authored look.** Judged by eye, in game.

---

## 9. Rejected alternatives

The standing "do not re-litigate" list, including the options refuted by *measurement*.

| Alternative | Why rejected | Date |
|---|---|---|
| `Cull [_LiquidCull]` toggled from C# | Strands render state on the shared `BlockDatabase.asset` liquid material when a session ends abnormally; buys a cost saving the `abs()`-safe unconditional `Cull Off` does not need. §2 | 2026-09-03 |
| A second `Cull Front` pass on the liquid shader | Doubles fluid draw calls unconditionally for a state one line of render state provides. §2 | 2026-09-03 |
| Reuse the per-cell `IsSubmerged` test for the visuals | No sub-cell surface height, so the tint snaps at cell boundaries and there is nothing to split the screen on. §1 | 2026-09-03 |
| Drive the tint boundary from the logical per-cell template | Can sit ~0.5 block off the drawn surface at a sloped pool edge — visible precisely where the waterline lives. §1.5 | 2026-09-03 |
| Flat screen tint with no depth fog | Leaves the seabed crisp at any distance and lava see-through; a colored pane of glass rather than a medium. §4.3 | 2026-09-03 |
| A UI `Canvas` image tint instead of a render pass | Cannot read depth, so no medium fog and no waterline; composites over the HUD rather than under it. | 2026-09-03 |
| Extend `VoxelFog.hlsl` to fog terrain underwater from the block shaders | Needs a keyword or branch in every block shader, still cannot tint the sky, and gives no waterline. Also fights the XZ-radial law that fog was deliberately given. §4.3 | 2026-09-03 |
| Place the overlay at `BeforeRenderingPostProcessing` | `GraphicsSettingsController.ApplyBloom` disables `renderPostProcessing` when bloom is off or no `Volume` exists, so submersion would look different across an unrelated setting. §3.1 | 2026-09-03 |
| Copy the camera color and blit it back through the overlay material | A fullscreen temp and a second fullscreen pass every submerged frame, to buy an offset-sampling capability only v2's distortion needs. `SrcAlpha` blending already performs the lerp against the attachment. §3 | 2026-09-04 |
| A screen-wide strength ramp on `_SubmersionColor.a` (0.25 block) | **Built, played, withdrawn.** Let a player floating at the waterline fade the medium to nothing while the lower half of the view was entirely underwater — submersion is a **per-ray** property being gated on a per-camera scalar. §4.1 | 2026-09-04 |
| A hard `IsSubmerged` switch as the *screen-wide* answer | Removes the fade-to-nothing exploit but not the defect behind it: an eye a centimetre above the surface still leaves a fully submerged lower half unfogged, and it reinstates the full-screen pop. §4.3 | 2026-09-04 |
| Floor the strength while any fluid is near (`max(0.5, ramp)`) | Cheapest way to kill the exploit, but it tints the **sky** half of the screen at 50 % while the eye is at the surface — wrong in the other direction, and it still cannot produce a waterline. §4.3 | 2026-09-04 |
| Reconstruct the view ray in the shader from `UNITY_MATRIX_I_VP` | Needs no published global, but that matrix cannot be set outside a real camera render, so the distance reconstruction would only be testable behind an `#ifdef` — gating a different code path than ships. §4.1 | 2026-09-04 |
| Bound the fluid body's sides with the depth buffer alone | **Refuted by a live frame.** At a shoreline the nearest boundary face sits inside the near clip plane and is never rasterized: an eye 2.4 cm under the surface at a body's western edge crossed **zero** water westward and was charged **3.9 blocks** — 42 % fog on dry cave. §1.4 | 2026-09-04 |
| Stop the extent scan at the first non-fluid cell | **Measured:** one voxel six cells out cut the +Z side from 23 cells to 6.47, thinning the medium across a quadrant, and swimming past obstructions made the body appear to breathe. A solid block inside the body is an occluder the depth buffer already bounds. §1.4 | 2026-09-04 |
| Ease the extents linearly in distance | `UnboundedFluidExtent` is 1e6, so easing from open water into a narrow channel would spend seconds at values that bound nothing — the over-fogging the extents exist to prevent. §4.2 | 2026-09-04 |
| Memoize the per-frame extent scans | Exactly output-equivalent, but it needs invalidation on every fluid edit — a staleness surface bought against an unmeasured cost. §7 | 2026-09-05 |
| A screen-space meniscus band in the overlay fragment | **Built in full, played, and reverted the same day.** Drawn on the surface plane's horizon, which is not where the corner-smoothed mesh the player sees actually is; and even aligned, a sine band against a straight mesh edge must cross it, leaving a gap the width of the wave amplitude. The wobble has to move the geometry. [`../Design/ANIMATED_LIQUID_SURFACE.md`](../Design/ANIMATED_LIQUID_SURFACE.md) | 2026-09-05 |
| Draw the meniscus where the fog's submerged length falls off | That locus is real, but it depends on the authored density and the eye's depth as well as on the geometry — no closed form for a baseline to assert, and the line moves when someone retunes the water's color. | 2026-09-05 |
| Key a waterline wobble to screen position | One fewer dot product, but the wave then stays glued to the view and slides sideways whenever the player turns their head. Still true for a mesh-based wave. | 2026-09-05 |
| Leave the authored tint consumed as linear and document the mismatch | Costs nothing today, but leaves the `BlockEditor`'s swatch permanently lying to whoever authors the next fluid — in a system whose values are tuned by eye. The conversion is one line. §4.1 | 2026-09-05 |
| Re-tune water after the color-space fix | It does not have to change what water looks like: setting the authored value to the sRGB encoding of the linear color it was already rendering reproduces the confirmed frame to within 3 × 10⁻⁵. Re-tuning would have reopened a confirmed look for nothing. §5 | 2026-09-05 |
| Flip `m_CopyDepthMode` to `AfterOpaques` so the fog measures to the terrain | Forces an earlier depth copy on every frame of the whole project, for a look that is arguably worse — fog should end where the medium ends. §7 | 2026-09-04 |

---

## Document History

* **v1.0** - Promoted from `Design/UNDERWATER_AND_SUBMERSION_RENDERING.md` **v2.5** (2026-09-05) —
  the content that was merged. That same commit then bumped it to **v2.6** while freezing it, so the
  file recovered below reads v2.6; the design was deleted at `DG-4` per `DG-*`, and
  `git show 5cbc13f3:Documentation/Design/UNDERWATER_AND_SUBMERSION_RENDERING.md` returns it. Every claim was
  re-verified against code at `cb1508ed`; the design's phase structure is merged into current-state
  sections and its `UW-*` IDs carried across in the index above. UW-5's resume material moved to
  [`../Design/ANIMATED_LIQUID_SURFACE.md`](../Design/ANIMATED_LIQUID_SURFACE.md), since it is a plan
  for work not done and this document describes only shipped code.

---

**Last Updated:** 2026-09-05  
**Next Review:** when `VX-3`/`VX-5` replaces the horizontal box (§7), or when UW-5 resumes
