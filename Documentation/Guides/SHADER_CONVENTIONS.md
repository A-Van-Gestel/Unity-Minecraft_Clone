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

### 1.1 The project floor is 4.5

Per Unity 6.6's [HLSL pragma target reference](https://docs.unity3d.com/6000.6/Documentation/Manual/SL-Pragma-target.html),
the `interpolators` requirement appears at exactly two tiers — **everything above 3.5 inherits 3.5's
guarantee unchanged**. So the column that decides a tier is not "how many interpolators" but **what
hardware it excludes**:

| Target  | Interpolators | Also requires                                     | DirectX   | Desktop OpenGL | OpenGL ES |
|---------|---------------|---------------------------------------------------|-----------|----------------|-----------|
| 2.0     | 8             | —                                                 | all       | all            | all       |
| 2.5     | 8             | `derivatives`                                     | FL9+      | 3.2+           | —         |
| 3.0     | **10**        | `samplelod fragcoord`                             | FL10+     | 3.2+           | 3.0+      |
| 3.5     | **15**        | `mrt4 integers 2darray instancing`                | FL10+     | 3.2+           | 3+        |
| 4.0     | 15 (no gain)  | `geometry`                                        | FL10+     | 3.x            | 3.1       |
| `gl4.1` | 15 (no gain)  | `cubearray tesshw tessellation msaatex`           | —         | 4.1            | —         |
| **4.5** | 15 (no gain)  | `compute randomwrite msaatex`                     | **FL11+** | **4.3+**       | **3.1**   |
| 4.6     | 15 (no gain)  | `cubearray tesshw tessellation msaatex`           | FL11+     | 4.1+           | 3.1 + AEP |
| 5.0     | 15 (no gain)  | `compute randomwrite msaatex tesshw tessellation` | FL11+     | 4.3+           | 3.1 + AEP |

**Declare `#pragma target 4.5`.** As of 2026-09-01 all **12** project-owned shaders do — a new shader
that departs from it should say why in a comment at the pragma.

Omitting the directive is not neutral: Unity defaults to **2.5**, which guarantees only 8 interpolators.
Declare it explicitly.

**What the 4.5 floor costs, stated plainly.** It buys this project **nothing today**: no interpolators
over 3.5 (§1.2), and no shader here uses `compute`, `randomwrite` or `msaatex`. What it does is raise the
hardware floor from DX11 **feature level 10+** to **11+**, and desktop OpenGL from **3.2+** to **4.3+** —
dropping the SubShader on GPUs Unity 6.6 still supports for Windows players ("DX10, DX11, DX12 or Vulkan
capable GPUs") and on the Linux `OpenGLCore` path. It was adopted as one deliberate project-wide decision
(§1.2), **not** derived from what the shaders use — so do not cite the floor as evidence that a shader
needs those features.

### 1.2 Do not raise above 4.5 — no tier grants more interpolators

4.5, 4.6 and 5.0 all grant **zero** additional interpolators over 3.5. Unity deliberately excludes
`interpolators32` from its 5.0 definition "for broader compatibility" — if you genuinely need more than 15,
the directive is `#pragma require interpolators32`, not a higher target.

4.6 and 5.0 demand ES 3.1 **+ AEP** (the Android Extension Pack — an extension bundle many ES 3.1 devices
never shipped) plus `tesshw`/`tessellation` that a vertex+fragment pass never uses. Unity strips `geometry`
and `tessellation` at compile time when the shader defines no such stage, but **`tesshw`, `compute`,
`randomwrite` and `msaatex` are not stripped** — the shader would advertise needing compute and
tessellation hardware in order to draw water.

**Need a feature, not a tier.** When a shader genuinely requires a capability, name the capability instead
of climbing tiers — `#pragma require` raises only that one requirement, where a target drags the DirectX
feature level and desktop OpenGL version along with it:

```hlsl
#pragma require randomwrite   // UAV writes — not: #pragma target 5.0
#pragma require compute       // structured buffers / atomics
#pragma require msaatex       // Texture2DMS reads
```

> **Unity 6.6 upgrade item — EXECUTED 2026-09-01.** Unity 6.6 drops OpenGL ES 3.0 on Android (ES 1.0/1.1/
> 2.0/3.0 are all unsupported; the player minimum is "OpenGL ES 3.1+, Vulkan"), so 4.5's ES 3.1 requirement
> now matches the platform minimum rather than exceeding it. The floor was moved **3.5 → 4.5 across all 12
> project-owned shaders in one commit**. The other two parts of the originally-planned change turned out to
> carry **no behavioral weight**:
>
> - **`PlayerSettings.openGLRequireES31` is deprecated in 6.6** — *"OpenGL ES 3.1 is now the minimum
>   supported version on Android. This setting has no effect."* The checkbox is gone from the Android
>   Player Settings UI (only `Require ES3.1+AEP` and `Require ES3.2` remain), and the getter returns `true`
>   regardless of the serialized value. `ProjectSettings.asset` carries `openGLRequireES31: 1` as a
>   cosmetic marker only — **it is inert; do not cite it as evidence of anything.**
> - **The Android graphics API list needed no change.** It is already `[Vulkan, OpenGLES3]` with
>   `m_Automatic: 1`, and `OpenGLES3` is the only ES value in `GraphicsDeviceType` — there is no ES 3.1
>   member. Unity 6.6: *"GLES3.1 is the minimum supported version when using OpenGLES3."*
>
> **Known cost, accepted.** The move was not driven by shader need — see §1.1. It trades DX11 FL10 and
> desktop OpenGL 3.2–4.2 reach for nothing currently used, and **no gate available to this project can
> detect that regression**: the dev machine is FL11+, so a clean local reimport proves only that FL11+
> hardware is fine (cf. §1.3, where Unity likewise failed to enforce the interpolator cap on every platform
> this project builds for). If a future build must reach FL10-era hardware, reverting is a one-line sweep.

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
- Both compile clean at the project floor (verified on desktop D3D11 at 3.5 on 2026-08-15, re-verified at
  4.5 on 2026-09-01 — all 12 shaders reimported with zero shader messages; GLES3/Vulkan untested).

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
| 1.3     | 2026-09-01 | **Moved the project floor 3.5 → 4.5** across all 12 project-owned shaders, executing the Unity 6.6 upgrade item filed in v1.0. Verified 6.6 drops OpenGL ES 3.0 on Android (player minimum is now "OpenGL ES 3.1+, Vulkan"), which removes the objection the item was waiting on. Two planned parts proved to be no-ops and were dropped: `openGLRequireES31` is **deprecated with no effect** in 6.6 (its getter returns `true` regardless, and the UI checkbox is gone), and the Android API list already reads `[Vulkan, OpenGLES3]` where `OpenGLES3` *is* ES 3.1+. Rebuilt §1.1's tier table around **DirectX feature level and desktop OpenGL version** — the columns the old table omitted, and the ones that actually price a tier — and recorded 4.5's accepted cost (DX11 FL10 and desktop GL 3.2–4.2 reach, for no capability currently used, with no gate able to detect the regression). Added the `#pragma require` escape hatch to §1.2 so a future compute/UAV need does not climb tiers. |
| 1.2     | 2026-08-15 | **Corrected §1.4's trigger.** v1.1 stated the rule as "anything sampling a texture or driving lighting is not exempt", generalized from a single instance. A review-driven audit of the other shaders showed the real trigger is a **hard discontinuity across the primitive edge** — atlas UVs, packed bit fields, discriminators — while smooth ramps stay exempt whether or not they drive lighting. Added `nointerpolation` as the sibling modifier for flat/constant data and applied it to `LiquidCore.hlsl`'s `packedShoreMask`; recorded the per-shader audit so the next author does not re-derive it. |
| 1.1     | 2026-08-15 | Added §1.4: shading inputs must be `centroid` now that `GS-4` made MSAA user-selectable. Written after 8x MSAA drew one-pixel wrong-block seams along every silhouette edge, worsening at low render scale; `uv`/`color`/`lightData` in `VoxelV2F` were marked centroid and the artifact went away. Costs no interpolators, so §1.3 is unaffected. |
| 1.0     | 2026-08-14 | Initial guide: `#pragma target` floor of 3.5, why higher tiers do not help, the Unity 6.6 / 4.5 item, and the interpolator-counting rule. Extracted from `CODEBASE_IMPROVEMENTS.md` §1.4 after RF-3's liquid emissive read pushed `LiquidV2F` to 11 interpolators against a declared `target 3.0`. |
