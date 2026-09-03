# Underwater & Submersion Rendering (UW-*)

**Version:** 1.0  
**Date:** 2026-09-03  
**Status:** Proposed design — not implemented.  
**Target:** Unity 6.6 (Mono for dev; IL2CPP for production)

> Closes the last open bullet of `FLUID_BUGS` **#02** — the one the 2026-09-03 physics ship left
> behind: a submerged player gets no visual signal at all. Three separate defects hide under that
> sentence: the liquid pass never renders from **inside** a fluid body, there is no screen-space
> **medium** (tint + fog) while the eye is under a surface, and there is no **waterline** when the
> eye sits at one. **The decision this document settles: submersion becomes one shared, sub-cell,
> surface-height-aware query — `World.GatherEyeSubmersion` — and both the new overlay pass and the
> already-shipped ambience low-pass filter read it, so what the player sees and what they hear
> switch on the same block boundary.** The overlay is a URP `ScriptableRendererFeature` that fogs
> exponentially against `_CameraDepthTexture`; the backface fix is one `Cull Off` line, safe
> because the liquid fragment reads its normal only through `abs()`.

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
5. **Per-fluid authoring.** Water and lava differ by authored values on `BlockType`, tuned in the
   `BlockEditor`, exactly as the 2026-09-03 physics coefficients are.

### Non-goals (v1)

- **Per-fluid screen distortion** (water wobble, lava heat shimmer) — planned as a **v2
  extension**, see the §7 extension roadmap. The authoring hooks (`_DistortionAmount`,
  `_HeatDistortionAmount`, already scaled by `GraphicsSettingsController`) are reserved for it.
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
| Liquid render state         | `UberLiquidShader.shader`'s `LiquidForward` pass declares **no `Cull`, no `Blend`, no `ZWrite`** → Unity defaults: `Cull Back`, opaque write, `ZWrite On`. **This is the root cause of goal 1.** The pass self-composites against `_CameraOpaqueTexture`.        |
| Liquid normal use           | `LiquidCore.hlsl` reads `worldNormal` in exactly two places — `GetShoreData` and `RouteFlowTo3D` — and **both take `abs()` first**. A back-facing (negated) normal is therefore a no-op through the whole fragment. `LiquidV2F` uses 11 of 15 interpolators.       |
| Fluid face emission         | `VoxelMeshHelper.GenerateFluidMeshData:1044/1117/1298` — top face unless the same fluid is above; bottom only over transparent, non-same-fluid; sides culled against effectively-full-height same-fluid neighbors. A submerged camera sits inside a **shell whose faces all point away from it**: the geometry exists, back-face culling hides it. |
| Corner surface height       | `GetSmoothedCornerHeight` averages the cell with up to three same-fluid neighbors, then the caller forces all four corners to `1.0` when fluid is above and clamps to `kMinFluidSurfaceHeight` (0.005). Vertices land at the cell's four XZ corners; the rasterizer interpolates between them. `private static` today. |
| Body fluid query            | `World.GatherFluidContact:4910` scans the body AABB for the highest overlapping surface, using `FluidContactResolver.SurfaceHeight` — the **logical per-cell template**, deliberately *not* the smoothed height, so a body's waterline does not depend on neighbor smoothing. Guarded on `FluidVertexTemplates`/`JobDataManager` disposal. |
| Eye/head submersion         | **Already exists, in audio.** `SoundManager.cs:409` resolves the listener's head cell from `Camera.main.transform` via `WorldOrigin.UnityToVoxelCell` at ~4 Hz and calls `AmbienceResolution.IsSubmerged`, a **per-cell** `fluidType != None` test. Its own docstring states the consequence: *"a head just under a partly-filled surface reads dry until it enters the cell below."* |
| Shader-global publish point | `World.SetGlobalLightValue` → `PublishSkyGlobals` → `PublishFogGlobals`, every frame, already null-guarded for edit-mode fixtures that build a `World` without `StartWorld`. `_playerCamera = Camera.main` (`World.cs:783`).                                       |
| Distance fog                | `VoxelFog.hlsl` — **horizontal (XZ) radial**, back-loaded by `pow(t, exponent)`, explicitly chosen to conceal the loaded-chunk radius without dissolving the ground under a flying player. A zero-width range reads as fog-off, which is what uninitialized globals give. |
| URP configuration           | `VoxelEngine-URP-Asset.asset`: `m_RequireDepthTexture: 1`, `m_RequireOpaqueTexture: 1`, `m_OpaqueDownsampling: 1`, `m_MSAA: 2`, HDR on. Depth **and** opaque are available to a fullscreen pass.                                                                 |
| Renderer features           | `UIBlurRendererFeature` is the **only** one, RenderGraph-based, listed in `VoxelEngine-URP-Renderer.asset`'s `m_RendererFeatures`. Its `Create()` is documented as idempotent across domain reload and inspector edits.                                            |
| Post-processing             | `GraphicsSettingsController.ApplyBloom:171` sets `data.renderPostProcessing = enabled && FindAnyObjectByType<Volume>() != null` — post-processing is **off** whenever bloom is off or no Volume exists.                                                            |
| Liquid material             | `World.LiquidMaterial => _blockDatabase.liquidMaterial` — a **shared project asset**, already mutated at runtime by `GraphicsSettingsController` (quality keywords, refraction keyword, distortion floats).                                                        |
| Per-fluid authoring slot    | `BlockType.cs:93-114`, `[Header("Fluid Properties")]`: `buoyancy`, `verticalDrag`, `submergedSpeedMultiplier`, `pushStrength`, `swimAscendSpeed` — all **`public` fields**, tuned in `BlockDatabase.asset` via the `BlockEditor`.                                  |
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

`GetSmoothedCornerHeight` and the `hasFluidAbove` / `kMinFluidSurfaceHeight` post-steps become
callable outside the mesher (see §4.2). **The risk this creates is a re-implementation drift** —
which is why UW-2's gate is an oracle against real mesh output, never against a re-typed copy of
the smoothing expression (§7).

### 3.5 Where the overlay pass runs

`RenderPassEvent.AfterRenderingTransparents` — a mechanical call, recorded because the obvious
alternative is wrong here. `BeforeRenderingPostProcessing` would let lava's glow earn bloom, but
`GraphicsSettingsController.ApplyBloom` disables `renderPostProcessing` outright whenever bloom is
off or the scene has no `Volume`, so the submersion look would differ between two settings that
have nothing to do with each other. Running after transparents makes the overlay identical in every
configuration; lava's glow is carried by its authored color instead of borrowed from the post
stack.

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

Surface height at the eye reproduces the mesher exactly:

- Four smoothed corner heights from the shared `GetSmoothedCornerHeight` path;
- forced to `1.0` when the same fluid is directly above, clamped up to `kMinFluidSurfaceHeight`;
- **bilinear** between them at `frac(eyeX)`, `frac(eyeZ)`, with the mesher's corner assignment
  (`bl=(0,0)`, `br=(1,0)`, `tl=(0,1)`, `tr=(1,1)`).

Bilinear is an approximation of a quad that the GPU rasterizes as **two triangles**, and
`EmitQuadTriangles` may flip the diagonal by light value — so the resolver and the drawn surface can
differ by a small amount along that diagonal. §8 records the bound.

### 4.3 Shader globals

Two, published next to the fog globals. Both live in **Unity/render space**, matching every other
global the block and liquid shaders consume.

| Global               | Contents                                                                            |
|----------------------|-------------------------------------------------------------------------------------|
| `_SubmersionColor`   | `rgb` = authored fluid tint; `a` = submerged weight, 0–1, for the fade at the edge.  |
| `_SubmersionParams`  | `x` = fog density (per block) · `y` = `SurfaceY` (Unity space) · `z` = meniscus half-width · `w` = reserved |

A zero `_SubmersionColor.a` means "not submerged", which is what uninitialized globals give — the
same fail-safe convention `VoxelFog.hlsl` uses for its zero-width range.

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
- **Reserved seat — per-fluid distortion (v2).** The overlay shader takes its distortion amount
  from a global from the first version, wired to `0` until v2 fills it, so adding the wobble later
  touches the shader and `GraphicsSettingsController` only.
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
| **UW-0 — Authored look**       | `submersionColor` + `submersionDensity` on `BlockType`; surfaced in `BlockEditor`; first-pass water and lava values.                                        |   🟢   | —            | —      |
| **UW-1 — Backfaces**           | `Cull Off` on `UberLiquidShader`'s `LiquidForward` pass. Shader only, no C#.                                                                                |   🟢   | —            | —      |
| **UW-2 — Eye query**           | `EyeSubmersion`, `Helpers/FluidSurfaceResolver`, `World.GatherEyeSubmersion`; open the mesher's corner-height path for sharing.                              |   🟡   | UW-0         | —      |
| **UW-3 — Audio adopts it**     | `AmbienceResolution.IsSubmerged` → the shared query; `SoundManager.cs:409` call site; SoundEngine baselines updated for sub-cell behavior.                   |   🟢   | UW-2         | —      |
| **UW-4 — Overlay pass**        | `Rendering/UnderwaterOverlayRendererFeature` + `Shaders/UnderwaterOverlay.shader`; `PublishSubmersionGlobals`; wire into `VoxelEngine-URP-Renderer.asset`; Graphics setting. |   🟡   | UW-2         | —      |
| **UW-5 — Waterline**           | Per-pixel near-plane test against the surface plane, meniscus band, wobble.                                                                                 |   🔴   | UW-4         | —      |
| **UW-6 — Lava pass & closure** | Lava density/color tuning, in-game confirmation, `docs-sync`, `FLUID_BUGS` #02 archived.                                                                     |   🟢   | UW-1…UW-5    | —      |

*Status: `—` not started · `In progress` · `✅ YYYY-MM-DD` complete (dated at in-game
confirmation) · `⏸️ YYYY-MM-DD` deliberately not implemented · `⛔ Superseded YYYY-MM-DD — <by
what>`.*

**UW-0 + UW-1 + UW-2 + UW-4 is the minimal set that delivers standalone value**: a visible,
fogged, correctly-tinted submerged view. UW-3 and UW-5 are polish on top of a working effect, and
UW-6 is closure.

**Validation is built alongside, not after.** A new `Minecraft Clone/Dev/Validate Underwater
Render` suite (`Assets/Editor/Validation/UnderwaterRender/`, namespace
`Editor.Validation.UnderwaterRender`) gains baselines as each phase lands, following the
`UIBlurRenderValidationSuite` model: arithmetic assertions rather than golden images, and
**INCONCLUSIVE** under `-nographics`. It is registered in `ValidationSuiteRegistry` so `Validate
All` and the CI entry point pick it up.

| Phase | What its baselines pin                                                                                                                                                                                                                                                     |
|-------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| UW-1  | **Prove-red first.** A reversed-winding fluid quad rendered with the liquid material returns the backdrop before the change and fluid color after. `FLUID_BUGS` #02 is a documented bug, so `validation-driven-bugfix`'s red→green→promote order applies.                     |
| UW-2  | **An oracle, not a tautology.** Build a fluid neighborhood, run the **real** `GenerateFluidMeshData`, read the emitted top-face vertex Ys, and assert `GatherEyeSubmersion`'s `SurfaceY` equals their bilinear sample at the eye's XZ. Asserting it against a re-typed copy of the smoothing expression would agree by construction and prove nothing. Plus: eye above/below surface, unloaded chunk, disposed-world guards, zero allocations. |
| UW-3  | Existing SoundEngine ambience baselines extended: a head just under a partly-filled surface now reads submerged, where the per-cell test read dry.                                                                                                                            |
| UW-4  | Overlay arithmetic — density 0 is a pass-through, full density reaches the tint, sky-depth pixels take full density. **Plus an explicit read-back** that `VoxelEngine-URP-Renderer.asset` actually lists the feature: the render scenarios pass on the shader alone and cannot observe an unwired pipeline. |
| UW-5  | The split row asserted against the analytically computed row at a known camera pose — **and a second scenario with the camera pitched and rolled**, so math that ignores camera orientation goes red. A "submerged is tinted / dry is clear" check alone would pass with the waterline entirely wrong. |

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
   `m_OpaqueDownsampling: 1`. URP resolves depth for `_CameraDepthTexture`, but the interaction of
   that resolve with the downsampled opaque texture at non-100 render scale is unverified here.
   Resolves at UW-4's first in-editor render; if the depth read is wrong the fog banding will be
   obvious immediately.
2. **A strongly sloped surface under the eye.** UW-5 splits the screen on a **flat** plane at
   `SurfaceY`. Where the smoothed surface tilts steeply — a shallow shore cell between a full cell
   and dry land — the true surface is not flat, and the drawn line will be slightly off. Resolves
   in game at UW-5; the fallback is to tilt the plane using the same four corner heights the
   resolver already has.

**Accepted limitations** (not questions — consequences to state plainly):

- Two fluid layers still never blend. The liquid pass writes opaquely and composites against
  `_CameraOpaqueTexture`, so a distant water wall seen from underwater shows one layer, as it does
  today from outside.
- The overlay is a camera effect. It does not change what the chunk shaders draw, so an individual
  block half in and half out of water is not treated per-block.
- The resolver's bilinear surface can differ from the rasterized two-triangle surface along the
  quad diagonal (§4.2).
- No suite validates the assembled pipeline. The render suites exercise the shaders; the read-back
  check exercises the wiring; only in-game play exercises the two together.

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

---

## Document History

* **v1.0** - Initial design

---

**Last Updated:** 2026-09-03  
**Next Review:** when UW-0 starts
