# Shader Conventions Guide

Conventions for the project's **hand-written HLSL** — the `.shader` files under `Assets/Shaders/` and the
shared includes in `Assets/Shaders/Includes/`. `CODING_STYLE_GUIDE.md` covers C# and does not apply here;
this guide is where shader-side rules live. **New shader code must follow these; code reviews may cite
this document.**

Vendored shaders (`Assets/TextMesh Pro/`) and package shaders are out of scope — never edit them to
satisfy a rule here.

Related: `Assets/Shaders/Includes/VoxelLighting.hlsl` and `LiquidCore.hlsl` (the two shared includes that
define the vertex/interpolator contracts), `Documentation/Architecture/SMOOTH_AND_RGB_LIGHTING.md`
(the lighting varyings), `Documentation/Architecture/FLUID_SHORELINE_RENDERING.md` (the liquid path).

---

## 1. `#pragma target` — declare the lowest tier that works

`#pragma target` is **not** a feature level to opt into. It is the **minimum GPU capability imposed on the
player**: raising it never makes a shader faster or more capable, it only makes Unity drop the SubShader on
hardware below the line. The correct value is the **lowest tier that covers what the shader actually uses**.

This inverts the instinct that serves you well for package versions, where "latest" is usually right.

### 1.1 The project floor is 3.5

Per Unity 6.6's [HLSL pragma target reference](https://docs.unity3d.com/6000.6/Documentation/Manual/SL-Pragma-target.html),
the `interpolators` requirement appears at exactly two tiers — **everything above 3.5 inherits 3.5's
guarantee unchanged**:

| Target  | Interpolators  | Also requires                                     | OpenGL ES support |
|---------|----------------|---------------------------------------------------|-------------------|
| 2.0     | 8              | —                                                 | all platforms     |
| 2.5     | 8              | `derivatives`                                     | —                 |
| 3.0     | **10**         | `samplelod fragcoord`                             | ES 3.0+           |
| **3.5** | **15**         | `mrt4 integers 2darray instancing`                | **ES 3+**         |
| 4.0     | 15 (no gain)   | `geometry`                                        | ES 3.1            |
| 4.5     | 15 (no gain)   | `compute randomwrite msaatex`                     | **ES 3.1**        |
| 5.0     | 15 (no gain)   | `compute randomwrite msaatex tesshw tessellation` | **ES 3.1 + AEP**  |

**Declare `#pragma target 3.5` unless you have a specific, stated reason not to.** It is the tier that
lifts the interpolator guarantee to 15, and its platform support list is **identical to 3.0's** — DX11
feature level 10+, OpenGL 3.2+, OpenGL ES 3+, Vulkan, Metal. Adopting it costs no reach whatsoever.

Omitting the directive is not neutral: Unity defaults to **2.5**, which guarantees only 8 interpolators.
Declare it explicitly.

As of 2026-08-14 **every project-owned shader declares 3.5** — a new shader that departs from it should
say why in a comment at the pragma.

### 1.2 Do not raise above 3.5 for interpolators — it does not help

4.5 and 5.0 grant **zero** additional interpolators over 3.5. Unity deliberately excludes `interpolators32`
from its 5.0 definition "for broader compatibility" — if you genuinely need more than 15, the directive is
`#pragma require interpolators32`, not a higher target.

Both also require OpenGL ES **3.1**, which contradicts this project's own Player Settings:

- `openGLRequireES31: 0` and `openGLRequireES31AEP: 0`
- Android `m_BuildTargetGraphicsAPIs` = **Vulkan** with **OpenGLES3 as the fallback**

5.0 goes further, demanding ES 3.1 **+ AEP** (the Android Extension Pack — an extension bundle many ES 3.1
devices never shipped) plus `tesshw`/`tessellation` that a vertex+fragment pass never uses. Unity strips
`geometry` and `tessellation` at compile time when the shader defines no such stage, but **`tesshw`,
`compute`, `randomwrite` and `msaatex` are not stripped** — the shader would advertise needing compute and
tessellation hardware in order to draw water.

> **Unity 6.6 upgrade item.** Unity 6.6 drops OpenGL ES 3.0 support on Android; the minimum becomes ES 3.1.
> At that point **4.5 becomes a coherent floor**, since its ES 3.1 requirement would match the platform
> minimum rather than exceed it. It still buys no interpolators — the reason to take it would be
> `compute`/`randomwrite` if a shader ever needs them. Treat it as **one deliberate project-wide decision**
> (Player Settings `openGLRequireES31` + the Android API list + every shader's pragma moved together), not
> something to arrive at one shader at a time.

### 1.3 Count your interpolators when adding a varying

Exceeding the declared budget is silent in practice: `UberLiquidShader` shipped `LiquidV2F` at 11
interpolators under `#pragma target 3.0` (budget 10) and compiled clean with zero shader messages on
**both** desktop D3D11 and the Android target (Vulkan + OpenGLES3, tested 2026-08-14). Unity did not
enforce the cap on any platform this project builds for. Treat the budget as a contract you keep because
the declaration should describe what the shader uses — **not** as something the compiler will catch for
you. Whether a stricter backend or a real player build would reject it is untested.

When adding a field to a `v2f` struct:

- **`SV_POSITION` does not count**, but **`COLOR` does** — it occupies a slot exactly like a `TEXCOORD`.
- A `half`/`float` scalar costs a **whole** slot, same as a `float4`. Prefer widening a neighbouring vector
  and using its spare channel over claiming a new `TEXCOORD` for one scalar — but see below.
- If you pack, **say so at the declaration** (`half4 blockRGB; // .rgb = blocklight, .w = emissive`).
  An unlabelled packed channel is worse than an extra interpolator.

Current state: `LiquidV2F` (`LiquidCore.hlsl`) uses **11** interpolators, which is what moved
`UberLiquidShader` and `Editor/FluidPreviewShader` from 3.0 to 3.5. `VoxelV2F` (`VoxelCommon.hlsl`) uses 4.

### 1.4 Interpolation modifiers — MSAA is user-selectable

Since `GS-4` shipped MSAA as a Graphics setting, a partially-covered edge pixel is still shaded at the
pixel **center**, which can lie outside the covered primitive. Plain interpolation therefore
*extrapolates* past the vertex data. The rule follows from what the extrapolated value is used for:

**The trigger is a hard discontinuity across the primitive edge, not "is it lighting".** A value that
varies smoothly tolerates extrapolation — being slightly off is invisible. A value with a cliff in it
does not.

| Varying carries | Modifier | Why |
|---|---|---|
| A texture-**atlas** UV | `centroid` | The canonical failure. Extrapolation walks the UV off the quad's tile and, with the project atlas point-filtered and mip-free (`packed_texture_atlas.png`), snaps to the neighboring tile's texels — a one-pixel seam of the **wrong block** along every silhouette edge, widening with render scale (at 30 % it is one render-target pixel upscaled ~3×) |
| A packed bit field, an ID, a discriminator — anything constant per primitive | `nointerpolation` | A bit field has no meaningful in-between. Flat-passing states the contract instead of interpolating and rounding back. `packedShoreMask` (`LiquidCore.hlsl`) is the worked example |
| A smooth ramp — distance, per-vertex light, a normal, a flow vector | *(none)* | A sub-pixel overshoot is invisible. `fogDistance` in `VoxelV2F` and the liquid shader's `skylight`/`blockRGB`/`emissive`/`shadowMultiplier` are deliberately plain |

- `centroid` moves the sample to the centroid of the covered samples, always inside the primitive.
- Both are **modifiers, not varyings** — they cost **zero** interpolators, so §1.3's budget is unaffected.
- Both compile clean at `#pragma target 3.5` (verified on desktop D3D11, 2026-08-15; GLES3/Vulkan untested).

**Audit as of 2026-08-15.** `VoxelV2F` marks `uv`, `color` **and** `lightData` centroid. Only `uv` is
required by the table above; the other two are belt-and-braces at zero cost and are the configuration
confirmed in game, so they stay. `LiquidV2F` needs nothing beyond `packedShoreMask`'s
`nointerpolation` — the liquid shader is procedural and samples no atlas, and its `liquidType`
discriminator is constant across every triangle, where interpolation of a constant is exactly that
constant inside **or** outside the primitive. (`packedShoreMask` is provably constant per quad:
`VoxelMeshHelper` builds one `Color32` and writes it to all four verts. Shore foam — the thing that mask
drives — was confirmed unchanged in game after the modifier landed; no suite covers the liquid shader's
rendered half.) `BorderWallShader`'s `uv` feeds a `frac()` band pattern
that could in principle discontinue by one pixel at the wall's silhouette; no artifact has been observed
and it is left alone.

Found the expensive way: the artifact is invisible without MSAA (the pixel center is always inside the
primitive), so it lay latent for as long as the engine had no MSAA path.

---

## Document History

| Version | Date       | Changes                                                                                                                                                                             |
|---------|------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| 1.2     | 2026-08-15 | **Corrected §1.4's trigger.** v1.1 stated the rule as "anything sampling a texture or driving lighting is not exempt", generalized from a single instance. A review-driven audit of the other shaders showed the real trigger is a **hard discontinuity across the primitive edge** — atlas UVs, packed bit fields, discriminators — while smooth ramps stay exempt whether or not they drive lighting. Added `nointerpolation` as the sibling modifier for flat/constant data and applied it to `LiquidCore.hlsl`'s `packedShoreMask`; recorded the per-shader audit so the next author does not re-derive it. |
| 1.1     | 2026-08-15 | Added §1.4: shading inputs must be `centroid` now that `GS-4` made MSAA user-selectable. Written after 8x MSAA drew one-pixel wrong-block seams along every silhouette edge, worsening at low render scale; `uv`/`color`/`lightData` in `VoxelV2F` were marked centroid and the artifact went away. Costs no interpolators, so §1.3 is unaffected. |
| 1.0     | 2026-08-14 | Initial guide: `#pragma target` floor of 3.5, why higher tiers do not help, the Unity 6.6 / 4.5 item, and the interpolator-counting rule. Extracted from `CODEBASE_IMPROVEMENTS.md` §1.4 after RF-3's liquid emissive read pushed `LiquidV2F` to 11 interpolators against a declared `target 3.0`. |
