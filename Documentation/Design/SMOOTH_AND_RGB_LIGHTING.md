# Smooth Lighting & Full RGB Light Engine

- **Status:** Planned
- **Current Implementation:** Flat per-face lighting (single scalar per quad), single-channel sunlight + single-channel blocklight
- **Phase 1 Target:** Smooth (ambient-occlusion-style) vertex-averaged lighting with full RGB data layout
- **Phase 2 Target:** Full RGB propagation for both sunlight and blocklight
- **Depends On:** None (can be implemented on the current codebase)

---

## 1. Current Implementation

### 1.1 Light Storage

Light is packed into each voxel's 32-bit `uint` data alongside block ID and metadata:

| Field      | Bits  | Range   | Purpose                              |
|------------|-------|---------|--------------------------------------|
| Block ID   | 0-15  | 0-65535 | Block type                           |
| Sunlight   | 16-19 | 0-15    | Light from the sky                   |
| Blocklight | 20-23 | 0-15    | Light emitted by torches, lava, etc. |
| Metadata   | 24-31 | 0-255   | Orientation / fluid level / schema   |

The final light value used for rendering is `max(sunlight, blocklight)`, yielding a single scalar 0-15 per voxel. Extraction is done via `BurstVoxelDataBitMapping.GetLight()`.

### 1.2 Mesh Generation (Light → Vertex)

In `MeshGenerationJob`, each visible face samples the **neighbor voxel's** light level (the block adjacent to the face, not the block owning the face). This value is converted to a float via `VoxelState.LightAsFloat` (`light * (1/16)`), producing a value in `[0.0, 1.0]`.

All 4 vertices of a face quad receive the **same** light value, stored in `Color.a`:

```csharp
// VoxelMeshHelper.cs — standard cube face
colors.Add(new Color(1f, 1f, 1f, lightLevel));  // All 4 vertices get identical lightLevel
```

The `Color` channel is used differently per shader:

| Shader              | Color.r      | Color.g           | Color.b           | Color.a |
|---------------------|--------------|-------------------|-------------------|---------|
| Standard Blocks     | Tint R (1.0) | Tint G (1.0)      | Tint B (1.0)      | Light   |
| Transparent Blocks  | Tint R (1.0) | Tint G (1.0)      | Tint B (1.0)      | Light   |
| Liquid (UberLiquid) | Liquid Type  | Packed Shore Mask | Shadow Multiplier | Light   |

### 1.3 Vertex Buffer Layout

The mesh uses 4 separate vertex streams (`SectionRenderer.cs`):

| Stream | Attribute | Format     | Data                                                |
|--------|-----------|------------|-----------------------------------------------------|
| 0      | Position  | Float32 x3 | Vertex position (12 bytes)                          |
| 1      | TexCoord0 | Float32 x4 | xy = atlas UV or flow vector, zw = shore (16 bytes) |
| 2      | Color     | Float32 x4 | RGBA — usage varies by shader (16 bytes)            |
| 3      | Normal    | Float32 x3 | Face normal (12 bytes)                              |

**Total per vertex: 56 bytes.** No additional UV channels (TexCoord1, TexCoord2) are currently used by voxel shaders.

### 1.4 Shader Pipeline

All block shaders read `i.color.a` as the light level and pass it to `ApplyVoxelLighting()` in `VoxelLighting.hlsl`:

```hlsl
float shade = (maxLight - minLight) * globalLight + minLight;
shade *= lightLevel;
shade = clamp(1.0 - shade, minLight, maxLight);
return color * pow(lerp(1.0, 0.10, shade), 2.2);  // Gamma-corrected shadow
```

Global uniforms (`GlobalLightLevel`, `minGlobalLightLevel`, `maxGlobalLightLevel`) are set by `World.cs` and modulate sunlight for the day/night cycle. Blocklight is not affected by these globals — but this distinction is invisible in the current system because sunlight and blocklight are merged into a single scalar via `max()` before reaching the mesh.

### 1.5 The Problem

Because all 4 vertices of a face share the same light value, the GPU rasterizer has nothing to interpolate — every fragment receives the identical value. This produces **hard, blocky light boundaries** at every face where adjacent blocks have different light levels. Transitions from light 15 to light 14 are visible as sharp lines.

This is especially noticeable:

- **Underwater:** Light attenuates by more than 1 level per step (opacity > 0), creating steep staircase patterns.
- **Cave entrances:** The transition from sunlight 15 to darkness is a series of discrete bands.
- **Around light sources:** Torches create visible concentric "shells" of brightness.

Additionally, the single-scalar merge `max(sunlight, blocklight)` means the shader cannot distinguish between sunlight and blocklight. This prevents:

- Tinting sunlight based on time of day (blue moonlight at night, red tones during a blood moon event).
- Colored blocklight from different sources (warm torches, cyan soul lanterns, red redstone).
- Correct day/night modulation — blocklight should remain at full intensity regardless of the day/night cycle, but currently both channels are merged before the shader sees them.

---

## 2. Phase 1: Smooth Lighting with RGB Data Layout

### 2.1 Overview

Phase 1 adds **per-vertex light averaging** (the classic Minecraft "Smooth Lighting" technique) while laying the data foundation for Phase 2's full RGB light propagation. The core idea: instead of giving all 4 vertices of a face the same light value, each vertex averages the light of the blocks that share that corner. The GPU then interpolates between 4 different values, producing smooth gradients.

The vertex data layout uses full RGB for **both** sunlight and blocklight from the start, so Phase 2 (RGB propagation) slots in without changing the vertex format, mesh upload code, or shader inputs.

### 2.2 Corner Averaging Algorithm

For each vertex of a visible face, sample the light values of the blocks sharing that corner and average them. A face's normal direction determines which neighbors to sample per corner.

**Example: Top face (normal = +Y), vertex at corner (x, z):**

The vertex at the back-left corner of a top face at block position `(bx, by, bz)` is shared by 4 blocks in the Y+1 layer:

```
vertexLight = average(
    light(bx,   by+1, bz  ),   // Direct neighbor (already sampled today)
    light(bx-1, by+1, bz  ),   // Side A
    light(bx,   by+1, bz-1),   // Side B
    light(bx-1, by+1, bz-1)    // Diagonal
)
```

Each face direction (6 total) has its own set of corner offsets. For each of the 4 corners of a face, we need the 1 direct neighbor + 2 side neighbors + 1 diagonal neighbor. The direct neighbor is the same block for all 4 corners (the block the face looks toward); the sides and diagonal differ per corner.

#### 2.2.1 Solid Block Handling and Ambient Occlusion

Opaque blocks contribute `0` light AND count toward the average denominator (count is always 4). This naturally darkens corners and edges where solid blocks meet — producing ambient-occlusion-like shadows for free.

```
lightSum = 0
count = 4    // Always 4 — opaque blocks still count

For each of the 4 neighbors (direct, sideA, sideB, diagonal):
    if block is opaque → lightSum += 0
    else               → lightSum += block.light

vertexLight = lightSum / count
```

#### 2.2.2 Diagonal Occlusion Rule (Critical)

When **both** side neighbors of a corner are opaque, the diagonal neighbor is fully occluded and must be treated as opaque (contributing 0) **without being sampled**. This prevents light from leaking through L-shaped solid corners:

```
sideA_opaque = isOpaque(sideA)
sideB_opaque = isOpaque(sideB)

if (sideA_opaque AND sideB_opaque):
    // Diagonal is fully occluded — skip the read, count as opaque
    diagonal_light = 0
else:
    diagonal_light = sample(diagonal)  // May still be opaque (contributes 0)
```

Without this rule, an air block diagonally behind two solid walls can contribute light to a vertex that should be fully dark, creating bright spots at inside corners.

#### 2.2.3 Precomputed Corner Offset LUT

The 3 neighbor offsets per corner per face direction are constant. Instead of computing them at runtime, precompute them as a static lookup table: 6 faces × 4 corners × 3 offsets = 72 `int3` values. This avoids redundant arithmetic in the hot mesh loop:

```csharp
// BurstVoxelData (SharedStatic)
// Layout: [faceIndex * 12 + cornerIndex * 3 + offsetIndex]
// Each entry is an int3 offset from the block position to the neighbor to sample.
// Offset 0 = Side A, Offset 1 = Side B, Offset 2 = Diagonal
public static readonly SharedStatic<NativeArray<int3>> CornerOffsets = ...;
```

The direct neighbor (the face-adjacent block) is the same for all 4 corners and is already known from the existing face culling check — it does not need to be in the LUT.

### 2.3 Anisotropy Fix (Quad Diagonal Flip)

When the 4 corner lights of a quad have a "checkerboard" pattern (e.g., corners 00 and 11 are bright, corners 01 and 10 are dark), the visual result depends on which diagonal the quad is split along for triangulation. One split produces a bright X shape, the other a dark X shape.

**Fix:** Before emitting the quad's triangle indices, compare the two possible diagonal splits and choose the one with lower contrast:

```csharp
// If the 00↔11 diagonal has more contrast than the 01↔10 diagonal, flip the split.
// Use the luminance (x component = sunlight in Phase 1) for the comparison.
if (light00.x + light11.x > light01.x + light10.x)
{
    // Emit triangles as: (0,1,2), (2,1,3)  — default winding
}
else
{
    // Emit triangles as: (0,1,3), (0,3,2)  — flipped diagonal
}
```

This applies to all quad-emitting paths: standard cube faces, custom mesh faces, and fluid top/bottom faces. Cross meshes are handled separately (see Section 2.5).

### 2.4 Vertex Data Layout (Full RGB)

Phase 1 introduces a new UV channel (`TexCoord1`) carrying an `RGBA` value with the light breakdown, using `UNorm8 x4` format for compactness:

| Component | Phase 1 Value                              | Phase 2 Value (future)                    |
|-----------|--------------------------------------------|-------------------------------------------|
| `R`       | Averaged sunlight luminance (0-255)        | Averaged sunlight Red (0-255)             |
| `G`       | `0` (reserved for sunlight G / blocklight) | Averaged sunlight Green (0-255)           |
| `B`       | `0` (reserved for sunlight B / blocklight) | Averaged sunlight Blue (0-255)            |
| `A`       | Averaged blocklight luminance (0-255)      | Averaged max blocklight luminance (0-255) |

In Phase 1, sunlight is scalar (stored in R, with G=B=0) and blocklight is scalar (stored in A). The shader treats `lightData.r` as sunlight intensity and `lightData.a` as blocklight intensity. G and B are reserved for Phase 2's per-channel sunlight tinting and RGB blocklight.

> **Why `UNorm8 x4` instead of `Float32 x4`?**
>
> Light levels are integers 0-15, mapped to floats 0.0-1.0 — only 16 discrete values per channel. `Float32` provides ~16 million levels of precision per channel, which is extreme overkill. `UNorm8` provides 256 levels per channel (16x more than needed), with perfect reconstruction: encode as `(byte)(light * 17)` on the CPU (yields 0, 17, 34, ..., 255), the GPU decodes back to `[0.0, 1.0]` automatically.
>
> - `Float32 x4`: 16 bytes/vertex — **+28.6%** vertex buffer size
> - `UNorm8 x4`: 4 bytes/vertex — **+7.1%** vertex buffer size
>
> At ~3000 vertices per section × thousands of loaded sections, this 4x difference is significant for both CPU→GPU upload bandwidth and GPU vertex fetch performance.

**Why a separate UV channel instead of packing into `Color`?**

- The liquid shader uses `Color.rgba` for liquid type, shore mask, shadow multiplier, and (currently) light — there is no room for 4 light channels.
- Block shaders use `Color.rgb` for tinting (used by `BlockIconGenerator` for editor shadows).
- A dedicated channel cleanly separates lighting data from per-shader-type color semantics.

**Phase 1 removes `Color.a` as the light carrier.** All shaders are updated simultaneously — there is no transitional period where some shaders read from `Color.a` and others from `TexCoord1`. The `Color` channel retains its existing per-shader uses (tinting for blocks, liquid type / shore mask / shadow multiplier for fluids) but no longer carries light data.

### 2.5 Cross Mesh and Custom Mesh Handling

#### 2.5.1 Cross Meshes (Flora)

Cross meshes (flowers, tall grass) consist of two intersecting diagonal quads within a single block space. Their vertices do not sit on block grid intersections, so the standard corner-averaging algorithm (designed for axis-aligned face vertices at block corners) does not directly apply.

**Approach:** Use a simplified version of the same `CalculateCornerLight` sampling function. The owning block's 4 horizontal neighbors and 4 diagonal neighbors are sampled at the Y+1 layer (the layer above the block, since light shines *down* onto flora). Each cross mesh vertex maps to the nearest block corner, and the averaged light from that corner is assigned.

This reuses the exact same `CalculateCornerLight` function as standard cube faces, just called with the Top face direction and the 4 corner indices. The result is that cross meshes naturally pick up the same smooth gradients and AO darkening as surrounding blocks. Since `CalculateCornerLight` takes a face index and corner index, no special-case code is needed — the mesh job calls it with `faceIndex = Top` and assigns each cross mesh vertex the light of its nearest top-face corner.

#### 2.5.2 Custom Meshes

Custom meshes use axis-aligned face culling identical to standard cubes (each custom mesh face maps to one of 6 face directions). The corner averaging logic applies identically to standard cube faces — `CalculateCornerLight` is called with the same face index and corner indices. Custom mesh vertices may not sit exactly at block corners, but the light values are assigned per-face-corner and interpolated by the GPU, which produces correct results for any vertex position within the face.

### 2.6 Mesh Pipeline Changes

#### 2.6.1 `MeshDataJobOutput` (JobData.cs)

Add a new `NativeList<Color32>` for the light UV channel:

```csharp
public NativeList<Color32> LightData;  // TexCoord1: UNorm8 (sun, -, -, block) in Phase 1
```

Using `Color32` (4 bytes, maps to UNorm8x4) instead of `Vector4` (16 bytes) for the compact format.

#### 2.6.2 `SectionRenderer` Vertex Layout

Add `TexCoord1` as stream 4 with `UNorm8` format:

```csharp
private static readonly VertexAttributeDescriptor[] s_layout =
{
    new(VertexAttribute.Position,  VertexAttributeFormat.Float32, 3, stream: 0),  // 12 bytes
    new(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4, stream: 1),  // 16 bytes
    new(VertexAttribute.Color,     VertexAttributeFormat.Float32, 4, stream: 2),  // 16 bytes
    new(VertexAttribute.Normal,    VertexAttributeFormat.Float32, 3, stream: 3),  // 12 bytes
    new(VertexAttribute.TexCoord1, VertexAttributeFormat.UNorm8,  4, stream: 4),  //  4 bytes — NEW
};
// Total: 60 bytes/vertex (up from 56, +7.1%)
```

#### 2.6.3 `VoxelMeshHelper` Corner Sampling

A single Burst-compatible helper function computes the averaged light for one vertex corner. All mesh types (standard cubes, custom meshes, cross meshes, fluids) call this same function:

```csharp
/// <summary>
/// Calculates the smooth-lit light value for a single vertex corner by averaging
/// the light of the 4 blocks sharing that corner. Handles diagonal occlusion.
/// </summary>
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public static Color32 CalculateCornerLight(
    int faceIndex, int cornerIndex,
    in int3 blockPos,
    /* voxel data access delegate/params */)
{
    // 1. Read direct neighbor light (already known from face culling)
    // 2. Read side A, side B using precomputed LUT offsets
    // 3. Apply diagonal occlusion rule: if both sides opaque, skip diagonal
    // 4. Average all 4 values (count always = 4, opaques contribute 0)
    // 5. Encode: sunlight → R, blocklight → A (Phase 1)
    //    Future:  sunR → R, sunG → G, sunB → B, maxBlock → A (Phase 2)
    // 6. Return Color32 with UNorm8 encoding: (byte)(avgLight * 17)
}
```

#### 2.6.4 `MeshGenerationJob`

For each visible face, call `CalculateCornerLight` 4 times (once per vertex). The 4 `Color32` results are written to the `LightData` NativeList and used for the anisotropy check.

For cross meshes, call with `faceIndex = Top` for all 4 corners (see Section 2.5.1).

### 2.7 Shader Changes

#### 2.7.1 Unified Shade Curve

Both sunlight and blocklight use the **same** gamma-corrected shade function (`CalculateLinearVoxelShadow`). This ensures consistent visual falloff — a torch at light level 14 looks identical in brightness to sunlight at level 14.

The key difference: sunlight intensity is **modulated by the day/night cycle** (the `globalLight` uniform from `World.cs`), while blocklight is **always at full intensity** (effectively `globalLight = 1.0`).

When a block emits no blocklight (luminance = 0), its blocklight contribution is exactly zero — the shade curve maps 0.0 input to full darkness (shade = 1.0, shadow multiplier = `pow(0.10, 2.2)` ≈ 0.006). This is then `max()`'d with sunlight, so zero blocklight never overrides any sunlight contribution.

```hlsl
/// Applies the shared shade curve to a single light channel (scalar, 0..1).
/// Returns a linear-space brightness multiplier.
float VoxelLightToShadow(float lightLevel, float globalLight, float minLight, float maxLight)
{
    float shade = CalculateVoxelShade(lightLevel, globalLight, minLight, maxLight);
    return CalculateLinearVoxelShadow(shade);
}
```

#### 2.7.2 `VoxelCommon.hlsl`

Add `TexCoord1` to the vertex structures:

```hlsl
struct VoxelAppdata
{
    float4 vertex : POSITION;
    float2 uv : TEXCOORD0;
    half4 color : COLOR;
    half4 lightData : TEXCOORD1;  // UNorm8: (sunR, sunG, sunB, blockLuminance)
};

struct VoxelV2F
{
    float4 vertex : SV_POSITION;
    float2 uv : TEXCOORD0;
    half4 color : COLOR;
    half4 lightData : TEXCOORD1;
};
```

The `VoxelVert` function passes `lightData` through unchanged.

#### 2.7.3 `VoxelLighting.hlsl`

Replace the scalar `ApplyVoxelLighting()` with an RGB-aware version. In Phase 1, sunlight is monochrome (`lightData.r` only) and blocklight is monochrome (`lightData.a` only). The function is written to handle RGB from the start:

```hlsl
/// Applies the voxel lighting model with separate sunlight and blocklight channels.
/// Both channels use the same gamma-corrected shade curve.
///
/// @param color         Base texture color (RGB).
/// @param sunRGB        Per-vertex sunlight as RGB (Phase 1: r=luminance, gb=0).
/// @param blockLuminance Per-vertex blocklight luminance (0..1).
/// @param globalLight   Day/night cycle (0..1) — modulates sunlight only.
/// @param minLight      Minimum ambient (0.15).
/// @param maxLight      Maximum light (1.0).
half3 ApplyVoxelLightingRGB(half3 color,
                            half3 sunRGB, float blockLuminance,
                            float globalLight, float minLight, float maxLight)
{
    // --- Sunlight ---
    // Phase 1: sunRGB = (luminance, 0, 0) → monochrome white light
    // Phase 2: sunRGB = (R, G, B) → tinted by time of day (blue night, red blood moon)
    float sunLuminance = max(sunRGB.r, max(sunRGB.g, sunRGB.b));
    float sunShadow = VoxelLightToShadow(sunLuminance, globalLight, minLight, maxLight);
    // In Phase 1, sunTint is (1,1,1) since only .r has a value.
    // In Phase 2, sunTint carries the color ratio (e.g., bluish for moonlight).
    half3 sunTint = sunLuminance > 0 ? sunRGB / sunLuminance : half3(1, 1, 1);
    half3 sunContrib = color * sunShadow * sunTint;

    // --- Blocklight ---
    // Blocklight uses the same shade curve but is NOT modulated by day/night
    // (globalLight = 1.0 effectively). Zero blocklight → zero contribution.
    float blockShadow = VoxelLightToShadow(blockLuminance, 1.0, minLight, maxLight);
    half3 blockContrib = color * blockShadow;

    // Per-channel max: the brighter source wins per RGB channel
    return max(sunContrib, blockContrib);
}
```

> **Note on zero blocklight:** When `blockLuminance = 0`, `VoxelLightToShadow(0, 1.0, 0.15, 1.0)` produces `shade = clamp(1.0 - 0, 0.15, 1.0) = 1.0`, then `shadow = pow(lerp(1.0, 0.10, 1.0), 2.2) = pow(0.10, 2.2) ≈ 0.006`. This near-zero value is effectively invisible and always loses the `max()` against any non-trivial sunlight. The `minLight` floor in the shade curve only applies within the curve itself — it prevents sunlight from going fully black (ambient), but does NOT add a baseline to blocklight. A block with `blockLuminance = 0` contributes
> ≈0.006, which is overridden by even the darkest ambient sunlight (~0.15 equivalent). No light is "invented" where none exists.

#### 2.7.4 `StandardBlockShader.shader` / `TransparentBlockShader.shader`

```hlsl
half4 fragFunction(VoxelV2F i) : SV_Target
{
    half4 col = SampleBlockTexture(i.uv);

    col.rgb = ApplyVoxelLightingRGB(col.rgb,
                                     i.lightData.rgb, i.lightData.a,
                                     GlobalLightLevel, minGlobalLightLevel, maxGlobalLightLevel);
    col.rgb *= i.color.rgb;  // Tinting (usually white)
    return col;
}
```

#### 2.7.5 `LiquidCore.hlsl` / `UberLiquidShader.shader`

The liquid shader has its own vertex structures (`LiquidAppdata`, `LiquidV2F`) separate from `VoxelCommon.hlsl`. These need a parallel update:

- Add `half4 lightData : TEXCOORD1` to `LiquidAppdata` (new input).
- Add a new interpolator in `LiquidV2F` for the light data (reuse an existing TEXCOORD slot or add TEXCOORD9).
- Replace the current `i.lightLevel` (from `Color.a`) with `i.lightData.r` (sunlight) and `i.lightData.a` (blocklight).
- The `Color.a` slot in `LiquidAppdata` is no longer used for light — `Color.rgba` remains `(liquidType, shoreMask, shadowMultiplier, unused)`.

The liquid fragment shader's `CalculateVoxelShade` call updates to use `ApplyVoxelLightingRGB` (or its constituent parts, since the liquid shader applies shade manually for water deep/shallow color blending).

#### 2.7.6 Editor Preview and Debug Shaders

The `DebugVoxelShader.shader` and any editor-only preview shaders (e.g., `LiquidEditorPreview`) also need updating. These shaders can simply read `lightData.r` as a scalar luminance for simplicity, ignoring RGB — their purpose is debugging, not visual fidelity.

### 2.8 Settings Toggle

Add a **Smooth Lighting** toggle to the Graphics settings tab via the existing data-driven settings UI:

```csharp
[SettingField("Smooth Lighting", SettingsTab.Graphics)]
public bool smoothLighting = true;
```

When disabled, the mesh job skips corner averaging and emits the current flat per-face light value to all 4 vertices (identical to today's behavior). The shader, vertex layout, and data format remain unchanged — the toggle only controls whether the CPU-side averaging runs. This provides:

- A fallback for lower-end hardware where the extra mesh job work is noticeable.
- A debugging aid to isolate smooth-lighting-related visual issues.
- User preference — some players prefer the chunky aesthetic.

### 2.9 Performance Considerations

#### 2.9.1 Mesh Job Cost

**Additional work per face:** The current system does 1 neighbor lookup per face. Smooth lighting adds up to 12 more (4 corners × 3 extra neighbors each). Each "lookup" involves:

1. Compute neighbor position (integer add from LUT)
2. Boundary check — same section? same chunk? (conditional branch)
3. Read the `uint` from the correct data source (`Map` or `NeighborX` NativeArray)
4. Extract light value (bit shift + mask)
5. Read block type from `BlockTypes` array (second memory access) to check opacity
6. Branch on opacity result + diagonal occlusion check

This is approximately 24 memory accesses per face (12 voxel reads + 12 block type lookups). In Burst with aggressive inlining and cache-friendly access patterns (all reads are within a 1-block radius), this is fast — but it should be profiled. The precomputed corner offset LUT (Section 2.2.3) eliminates redundant arithmetic.

**Chunk boundary data:** The mesh job already has all 8 neighbor chunk maps (4 cardinal + 4 diagonal) and `GetVoxelStateFromLocalPos` already handles cross-chunk and cross-section lookups including diagonal chunks. No additional data needs to be passed into the mesh job.

#### 2.9.2 Vertex Data Increase

With `UNorm8 x4` for `TexCoord1`: **4 bytes/vertex** additional.

- Current: 56 bytes/vertex
- New: 60 bytes/vertex (+7.1%)
- For a typical section with ~3000 vertices: +12 KB per section
- At 2000 loaded sections: +24 MB total GPU memory

This is modest. Using `Float32 x4` instead would be +16 bytes/vertex (+28.6%), adding ~96 MB — nearly 4x worse for zero quality benefit.

#### 2.9.3 Underwater Gradient Quality

With water opacity = 2, the BFS formula (`sourceLight - max(1, opacity)`) attenuates light by 2 per step: 15 → 13 → 11 → 9 → 7 → 5 → 3 → 1 → 0. Adjacent blocks differ by 2 light levels. After corner averaging with 4 neighbors, the gradient smooths to approximately 1-level differences between adjacent vertices — producing a gentle, natural-looking underwater falloff.

> **Prerequisite completed:** The BFS attenuation formula was aligned with the Starlight/Moonrise `max(1, opacity)` formula (see `LIGHTING_SYSTEM_OVERVIEW.md` Section 4.2). All three attenuation sites (`PropagateLight`, `RecalculateSunlightForColumn`, `CheckEdgeVoxel`) now use the consistent formula, eliminating the previous 1-level shadow line artifact at chunk borders underwater.

### 2.10 Scope

**Files modified:**

| File                                    | Change                                                                                                          |
|-----------------------------------------|-----------------------------------------------------------------------------------------------------------------|
| `Data/JobData.cs`                       | Add `LightData` (`NativeList<Color32>`) to `MeshDataJobOutput`                                                  |
| `Jobs/BurstData/BurstVoxelData.cs`      | Add `CornerOffsets` SharedStatic LUT (72 × `int3`)                                                              |
| `Helpers/VoxelMeshHelper.cs`            | Add `CalculateCornerLight`, update all vertex-emitting methods to use it, add anisotropy-aware triangle winding |
| `Jobs/MeshGenerationJob.cs`             | Call `CalculateCornerLight` per vertex for all mesh types, wire smooth lighting toggle                          |
| `SectionRenderer.cs`                    | Add `TexCoord1` (UNorm8x4) to vertex layout, pass `LightData` to mesh                                           |
| `Chunk.cs`                              | Thread `LightData` through `ApplyMeshData`                                                                      |
| `Shaders/Includes/VoxelCommon.hlsl`     | Add `lightData` to vertex structs, pass through in `VoxelVert`                                                  |
| `Shaders/Includes/VoxelLighting.hlsl`   | Replace `ApplyVoxelLighting` with `ApplyVoxelLightingRGB`, add `VoxelLightToShadow`                             |
| `Shaders/StandardBlockShader.shader`    | Use `lightData` for lighting                                                                                    |
| `Shaders/TransparentBlockShader.shader` | Use `lightData` for lighting                                                                                    |
| `Shaders/Includes/LiquidCore.hlsl`      | Add `lightData` to liquid vertex structs, remove `Color.a` as light carrier                                     |
| `Shaders/UberLiquidShader.shader`       | Use `lightData` for lighting                                                                                    |
| `Shaders/DebugVoxelShader.shader`       | Read `lightData.r` as scalar luminance                                                                          |
| `Settings.cs`                           | Add `smoothLighting` toggle to Graphics tab                                                                     |

**Files NOT modified:** `NeighborhoodLightingJob.cs`, `BurstVoxelDataBitMapping.cs`, `ChunkData.cs` — the lighting BFS and voxel data format are unchanged in Phase 1.

---

## 3. Phase 2: Full RGB Light Engine

### 3.1 Overview

Phase 2 makes **both** sunlight and blocklight fully RGB-aware in the lighting engine itself. This means:

- **Sunlight RGB:** `World.cs` sets a sunlight color that varies with time of day. Dawn = warm orange, noon = white, dusk = orange/red, night = cool blue (moonlight), blood moon = deep red. The BFS propagates RGB sunlight instead of a single scalar.
- **Blocklight RGB:** Each light-emitting block defines an emission color (torches = warm orange, soul lanterns = cyan, redstone = red). The BFS propagates three independent color channels.

The smooth-lighting vertex averaging from Phase 1 already produces `Color32` light data per vertex. Phase 2 simply populates all 4 channels with real RGB values instead of the monochrome placeholders used in Phase 1. **No vertex format, shader, or mesh upload changes are needed.**

### 3.2 Voxel Data Changes

#### 3.2.1 The Storage Problem

The current 32-bit `uint` allocates 4 bits each for sunlight and blocklight (8 bits total). Full RGB for both channels needs:

- Sunlight: 3 channels × 4 bits = 12 bits
- Blocklight: 3 channels × 4 bits = 12 bits
- Total: 24 bits (up from 8), requiring 16 additional bits

```
Current:  [ID: 16][Sun: 4][Block: 4][Meta: 8] = 32 bits — fully used
RGB need: [ID: 16][SunR:4][SunG:4][SunB:4][BlockR:4][BlockG:4][BlockB:4][Meta: 8] = 56 bits
```

#### 3.2.2 Proposed Solutions

**Option A: Separate light storage array (`NativeArray<uint>` per chunk).**

Keep the existing `uint` voxel data for ID + metadata. Add a parallel `uint` per voxel for all light channels:

```
Existing uint: [ID: 16][Sun(legacy): 4][Block(legacy): 4][Meta: 8]
New uint:      [SunR: 4][SunG: 4][SunB: 4][Unused: 4][BlockR: 4][BlockG: 4][BlockB: 4][Unused: 4]
               — 24 bits of light data packed into 32
```

- **Pros:** No change to the existing `uint` layout — ID, metadata, serialization, fluid logic, and all non-lighting systems are untouched. Memory increase is 32,768 × 4 bytes = 128 KB per chunk. Can be allocated lazily per section. Clean separation of concerns: one array for block identity, one for light state.
- **Cons:** Two arrays to keep in sync. The lighting BFS reads/writes both arrays per neighbor. The mesh job needs both arrays. Cache coherence suffers from touching two memory regions in tight loops.

**Mitigating factor:** Light data is accessed in a different phase than block data in most hot paths. The BFS loop primarily reads block types (from `BlockTypes[]` via the ID) and light values. With a separate light array, the BFS can operate on a contiguous light-only buffer without polluting the cache with ID/metadata bits it doesn't need. This may actually *improve* cache utilization for the BFS compared to the current interleaved layout.

**Legacy scalar fields:** The sunlight (bits 16-19) and blocklight (bits 20-23) in the existing `uint` become **derived values**. After the BFS writes RGB to the light array, it also writes `max(R,G,B)` to the legacy bits. This maintains compatibility with systems that only need scalar light (mob spawning, face culling heuristics). If this dual-write cost is unacceptable, the legacy bits can be deprecated and `GetLight()` can read from the light array instead.

**Option B: Move to a 64-bit voxel (`ulong`).**

```
[ID: 16][SunR:4][SunG:4][SunB:4][BlockR:4][BlockG:4][BlockB:4][Meta: 8][Reserved: 16] = 64 bits
```

- **Pros:** Single source of truth — one array, one read/write per voxel. 16 reserved bits for future features (biome tint, damage state, etc.). Simpler code with no sync issues.
- **Cons:** Doubles memory per voxel (128 KB → 256 KB per chunk). Doubles memory bandwidth for the entire pipeline. Requires updating `NativeArray<uint>` to `NativeArray<ulong>` across every system — wide blast radius touching serialization, generation, meshing, fluid logic, and all helper functions. Impacts cache performance for systems that only need ID or metadata but must fetch 8 bytes regardless.

#### 3.2.3 Recommendation: Benchmark Both

Neither option is clearly superior on theoretical grounds alone. Option A has better cache behavior for the BFS (smaller light-only working set) but worse code complexity. Option B has a simpler programming model but larger memory footprint.

**Before committing to either, benchmark both approaches** against the current baseline:

1. **Baseline:** Profile the current `NeighborhoodLightingJob` and `MeshGenerationJob` execution times. Record frame time, job duration (via Unity Profiler markers), and memory usage.
2. **Option A prototype:** Add a parallel `NativeArray<uint>` for light data. Modify the lighting BFS to write to both arrays. Measure BFS job duration vs baseline.
3. **Option B prototype:** Change the voxel array to `NativeArray<ulong>`. Update the BFS and measure. Compare against Option A.
4. **Decision criteria:** If BFS performance differs by <10%, prefer Option B for simplicity. If Option A is measurably faster (better cache utilization), accept the complexity.

### 3.3 Sunlight RGB: Time-of-Day Tinting

#### 3.3.1 Sky Color Uniform

`World.cs` already sets `GlobalLightLevel` (0-1) as a day/night cycle uniform. Phase 2 extends this to a color:

```csharp
// World.cs
private Color _skyLightColor = Color.white;  // Updated per frame based on time of day

// Example curve (configurable via AnimationCurve or gradient):
// Dawn:       (1.0, 0.85, 0.6)   — warm orange
// Noon:       (1.0, 1.0, 1.0)    — pure white
// Dusk:       (1.0, 0.7, 0.4)    — deep orange
// Night:      (0.6, 0.7, 1.0)    — cool blue (moonlight)
// Blood moon: (1.0, 0.2, 0.15)   — deep red
```

#### 3.3.2 Sunlight BFS Changes

Currently, the sunlight BFS propagates a single value (0-15). With RGB, it propagates three independent channels. The sky color from `World.cs` seeds the sunlight columns:

```
Initial sunlight above heightmap:
    sunR = (byte)(15 * skyColor.r)
    sunG = (byte)(15 * skyColor.g)
    sunB = (byte)(15 * skyColor.b)
```

The BFS attenuates each channel independently through opacity. Opacity is still a single scalar per block type — it reduces all three channels equally:

```
targetR = sourceR - max(1, opacity)
targetG = sourceG - max(1, opacity)
targetB = sourceB - max(1, opacity)
```

A neighbor is enqueued if **any** channel increased.

#### 3.3.3 Time-of-Day Update Strategy

When the sky color changes (every few seconds, not every frame), all sunlight must be recalculated. This is expensive but can be amortized:

- **Option 1: Shader-only tinting (recommended for Phase 2 launch).** Keep the sunlight BFS monochrome (propagate scalar 0-15 as today). Apply the sky color as a per-frame shader uniform that tints the monochrome sunlight. This is zero-cost in the BFS and achieves the same visual result for uniform sky lighting. The `lightData.rgb` carries `(sunLuminance, 0, 0)` and the shader multiplies by `_SkyLightColor`.
- **Option 2: Full BFS RGB.** Propagate RGB in the BFS. When sky color changes, mark all chunks as needing re-lighting. Process progressively (N chunks per frame). More physically correct but much more expensive — primarily useful if different *regions* of the sky have different colors (e.g., sunset gradient), which our engine doesn't currently model.

**Recommendation:** Option 1 for launch. It delivers the visual goal (blue moonlight, red blood moons) with zero lighting engine cost. Option 2 can be added later if per-region sky coloring becomes a requirement.

### 3.4 Blocklight RGB Changes

#### 3.4.1 Block Type Changes

Add an emission color to `BlockType` / `BlockTypeJobData`:

```csharp
// In BlockType (ScriptableObject / inspector-editable)
public Color32 lightEmissionColor;  // RGB emission color (alpha ignored)

// In BlockTypeJobData (Burst-safe job copy)
public readonly byte EmissionR;  // 0-15
public readonly byte EmissionG;  // 0-15
public readonly byte EmissionB;  // 0-15
```

The existing `lightEmission` scalar (byte, 0-15) is derived as `max(EmissionR, EmissionG, EmissionB)` and kept for backwards compatibility with systems that only need scalar luminance (mob spawning, etc.). Or it can be deprecated entirely, with callers reading `max(R,G,B)` on demand.

#### 3.4.2 BFS Propagation Changes

The blocklight BFS propagates three channels independently. Each channel attenuates via the block's opacity:

```
targetR = sourceR - max(1, opacity)
targetG = sourceG - max(1, opacity)
targetB = sourceB - max(1, opacity)
```

A neighbor is enqueued if **any** channel increased. At the destination voxel, each channel is stored as `max(existing, incoming)` — the brightest source wins per-channel, enabling correct additive color mixing.

#### 3.4.3 Queue Entry Format

The current BFS queue entries carry a position and a single light level. With RGB, each entry needs position + 3 channel levels. Options:

- **Pack into `ulong`:** Position (20 bits) + R (4) + G (4) + B (4) = 32 bits. Fits in a `uint`, or use `ulong` for additional flags / direction bitmask.
- **Struct queue:** `NativeQueue<BlocklightRGBEntry>` where `BlocklightRGBEntry` is a small blittable struct `(int3 pos, byte r, byte g, byte b)`. Cleaner, slightly more memory per entry.

For the darkness removal BFS, entries carry the **old** RGB values so the removal phase can correctly identify which channels were dependent on the removed source.

**Recommended:** Bitpack queue entries to keep them at 32 bits. A chunk-local position fits in 16 bits (X: 4, Z: 4, Y: 8). The remaining 16 bits hold RGB + flags: `[Pos: 16][R: 4][G: 4][B: 4][Flags: 4]`. This matches the current `uint` queue entry size, avoiding any increase in queue memory or cache pressure.

#### 3.4.3.1 Per-Channel Darkness Removal (Worked Example)

RGB darkness removal is more nuanced than scalar removal because channels are independent. When a light source is removed, only the channels it contributed to should be cleared — other sources' contributions must be preserved.

**Example:** A red torch `(12, 0, 0)` and a blue soul lantern `(0, 0, 10)` both illuminate a voxel. The voxel stores `(12, 0, 10)` (per-channel max from both sources). When the red torch is removed:

1. **Removal entry:** Position + old values `(12, 0, 0)` enqueued.
2. **At the voxel:** Compare each channel against the removal entry's old value.
    - R: current `12` equals old `12` → this channel was dependent on the removed source. Set to `0`, enqueue for further removal.
    - G: current `0` equals old `0` → no change.
    - B: current `10` > old `0` → this channel has light from a *different* source (the soul lantern). Do NOT clear. Enqueue in the re-spreading queue so the BFS can verify and re-propagate if needed.
3. **Result:** Voxel becomes `(0, 0, 10)` — the blue light survives, the red light is correctly removed.

This follows the same dual-phase pattern as the current scalar BFS (Phase 1: darkness removal, Phase 2: re-spreading), extended to operate per-channel. The existing `PropagateDarkness` / `PropagateLight` structure remains; the comparison and update logic simply runs on 3 values instead of 1.

#### 3.4.4 Cross-Chunk Light Modifications

`LightModification` structs in `CrossChunkLightMods` grow to carry RGB values instead of a single scalar. The write-through cache (`NativeHashMap<long, uint>`) needs its value type expanded to hold the RGB light state (either the full light `uint` from the separate array, or a packed representation).

### 3.5 Mesh Job Changes (Minimal)

The `CalculateCornerLight` function from Phase 1 already returns `Color32`. In Phase 1, it reads scalar sunlight and blocklight and encodes them as `(sunLuminance, 0, 0, blockLuminance)`. In Phase 2, it reads the RGB channels:

**If using shader-only sky tinting (Section 3.3.3 Option 1):**

```csharp
// Sunlight is still scalar, tinted in the shader
byte sun = BurstVoxelDataBitMapping.GetSunLight(packedData);

// Blocklight is now RGB from the separate light array
byte blockR = LightBitMapping.GetBlocklightR(lightData);
byte blockG = LightBitMapping.GetBlocklightG(lightData);
byte blockB = LightBitMapping.GetBlocklightB(lightData);

// Encode into Color32 for TexCoord1
// R = sunlight luminance, G = unused, B = unused, A = max blocklight luminance
Color32 result = new Color32(
    (byte)(sun * 17),                            // R: sun (shader tints this)
    0,                                            // G: reserved
    0,                                            // B: reserved
    (byte)(math.max(blockR, math.max(blockG, blockB)) * 17)  // A: block luminance
);
```

> **Open question for Phase 2:** With RGB blocklight, the shader needs the full RGB breakdown, not just the luminance in `A`. The `UNorm8 x4` layout may need to be revisited — options include packing blocklight RGB into a second UV channel (`TexCoord2`), using `UNorm8 x8` (not supported as a single attribute — would need two x4 attributes), or upgrading to `Float16 x4` with packed channels. This is a Phase 2 design decision that depends on whether shader-only sky tinting (Section 3.3.3 Option 1) is adopted, which would free up the RGB slots for
> blocklight instead.

**If sky tinting is shader-only (recommended):**

The `TexCoord1` layout becomes:

| Component | Value                                                    |
|-----------|----------------------------------------------------------|
| `R`       | Sunlight luminance (0-255, monochrome — shader tints it) |
| `G`       | Blocklight Red (0-255)                                   |
| `B`       | Blocklight Green (0-255)                                 |
| `A`       | Blocklight Blue (0-255)                                  |

This fits cleanly into `UNorm8 x4` with no additional channels needed. The shader reads `lightData.r` for sunlight (applies `_SkyLightColor` tint) and `lightData.gba` for RGB blocklight.

### 3.6 Shader Changes (Phase 2)

With shader-only sky tinting and RGB blocklight in `lightData.gba`:

```hlsl
// New global uniform from World.cs
half3 SkyLightColor;  // Updated per frame based on time of day

half3 ApplyVoxelLightingRGB(half3 color,
                            float sunLuminance, half3 blockRGB,
                            half3 skyColor,
                            float globalLight, float minLight, float maxLight)
{
    // Sunlight: scalar luminance × sky color tint × day/night modulation
    float sunShadow = VoxelLightToShadow(sunLuminance, globalLight, minLight, maxLight);
    half3 sunContrib = color * sunShadow * skyColor;

    // Blocklight: RGB channels × same shade curve, always full intensity
    float blockR_shadow = VoxelLightToShadow(blockRGB.r, 1.0, minLight, maxLight);
    float blockG_shadow = VoxelLightToShadow(blockRGB.g, 1.0, minLight, maxLight);
    float blockB_shadow = VoxelLightToShadow(blockRGB.b, 1.0, minLight, maxLight);
    half3 blockContrib = color * half3(blockR_shadow, blockG_shadow, blockB_shadow);

    return max(sunContrib, blockContrib);
}
```

> **Note on blocklight shade curve at zero:** Each blocklight channel goes through `VoxelLightToShadow` independently. A channel at 0.0 produces shadow ≈ 0.006 (near-zero). A channel at 1.0 produces shadow = 1.0 (full brightness). Channels at intermediate values follow the same gamma curve as sunlight — ensuring a red torch `(0.8, 0.1, 0.0)` has the same perceived brightness falloff as sunlight at the same numeric level.

### 3.7 Serialization Impact

The RGB light data (both sun and block) is **runtime-only** and does **not** need to be saved to disk. Light is fully reconstructed from block placements and sky exposure during chunk loading (the BFS recomputes all values from emitting blocks and heightmap). This means:

- No changes to the region file format.
- No world migration needed.
- The RGB light array is allocated during chunk generation and freed on unload.

The existing scalar sunlight/blocklight bits in the `uint` can be kept for non-rendering uses (mob spawning rule: `max(sun, block) >= threshold`) or deprecated.

### 3.8 Scope

**Files modified (beyond Phase 1):**

| File                                         | Change                                                                                  |
|----------------------------------------------|-----------------------------------------------------------------------------------------|
| `Data/BlockType.cs`                          | Add `lightEmissionColor` field                                                          |
| `Data/JobData.cs` (`BlockTypeJobData`)       | Add `EmissionR`, `EmissionG`, `EmissionB`                                               |
| `Data/ChunkSection.cs`                       | Add `NativeArray<uint>` for RGB light storage (Option A) or widen to `ulong` (Option B) |
| `Jobs/BurstData/BurstVoxelDataBitMapping.cs` | Add RGB pack/unpack for the light data                                                  |
| `Jobs/NeighborhoodLightingJob.cs`            | Triple-channel BFS for blocklight propagation                                           |
| `Data/ChunkData.cs`                          | RGB-aware blocklight queues, cross-chunk mod handling                                   |
| `WorldJobManager.cs`                         | Pass RGB light arrays into/out of lighting jobs, sky color propagation                  |
| `World.cs`                                   | Add `SkyLightColor` uniform, time-of-day color curve                                    |
| `Jobs/MeshGenerationJob.cs`                  | Read RGB blocklight channels (minor — `CalculateCornerLight` updates)                   |
| `Helpers/VoxelMeshHelper.cs`                 | Populate `lightData.gba` from RGB blocklight values                                     |
| `Shaders/Includes/VoxelLighting.hlsl`        | Update `ApplyVoxelLightingRGB` for per-channel blocklight + sky tint                    |
| `Shaders/StandardBlockShader.shader`         | Add `SkyLightColor` uniform                                                             |
| `Shaders/TransparentBlockShader.shader`      | Add `SkyLightColor` uniform                                                             |
| `Shaders/UberLiquidShader.shader`            | Add `SkyLightColor` uniform                                                             |

**Files NOT changed from Phase 1 state:** `SectionRenderer.cs`, `Chunk.cs`, vertex layout, `VoxelCommon.hlsl` — the rendering pipeline is fully prepared by Phase 1.

### 3.9 Visual Examples

**Single white torch (current behavior, preserved):** `emission = (15, 15, 15)`. All three channels attenuate equally. Shader produces the same brightness as the current scalar system.

**Warm torch:** `emission = (15, 10, 4)`. Nearby blocks get a warm amber tint. As distance increases, all channels fade through the gamma curve — the warm hue is preserved at all distances because all channels use the same attenuation rate.

**Red redstone torch:** `emission = (12, 2, 0)`. Strong red tint nearby. At distance, the green channel hits zero first (only 2 levels of range), leaving a purer red that fades to darkness.

**Overlapping colored lights:** A cyan soul lantern `(2, 8, 15)` and a warm torch `(15, 10, 4)` both illuminate the same block. Per-channel max in the BFS produces `(15, 10, 15)` at that voxel. The shader applies the gamma curve to each channel independently, producing a bright warm-magenta blend. This is physically plausible for additive light mixing.

**Blue moonlight:** At night, `SkyLightColor = (0.6, 0.7, 1.0)`. The shader multiplies sunlight luminance by this color — outdoor areas get a cool blue tint. Indoor areas lit by warm torches remain warm because blocklight is unaffected by the sky color. The transition at doorways naturally blends between blue outdoor light and warm indoor light through the smooth vertex averaging.

**Blood moon event:** `SkyLightColor = (1.0, 0.2, 0.15)`. Everything outdoors takes on a deep red hue. The lighting engine supports this natively — `World.cs` just changes the `SkyLightColor` uniform, no re-lighting needed.

---

## 4. Migration Path & Risk Assessment

### 4.1 Phased Rollout

| Step         | Change                                                            | Risk                                                          | Rollback                                              |
|--------------|-------------------------------------------------------------------|---------------------------------------------------------------|-------------------------------------------------------|
| **Phase 1a** | Add `TexCoord1` (UNorm8x4) to vertex layout + all shaders         | Low — additive change, remove stream 4 to revert              | Remove stream 4 from layout                           |
| **Phase 1b** | Implement corner averaging + diagonal occlusion + LUT in mesh job | Medium — affects every visible face, ~24 extra reads per face | Set `smoothLighting = false` (flat per-face fallback) |
| **Phase 1c** | Add anisotropy fix (quad diagonal flip)                           | Low — only changes triangle winding order when needed         | Revert to default winding                             |
| **Phase 1d** | Add smooth lighting settings toggle                               | Low — UI-only                                                 | Remove setting field                                  |
| **Phase 2a** | Add RGB light storage (benchmark Option A vs B first)             | Medium — new allocation per chunk, pool reset rules apply     | Remove array, fall back to scalar                     |
| **Phase 2b** | Triple-channel blocklight BFS                                     | High — core lighting engine change                            | Feature flag to use scalar BFS                        |
| **Phase 2c** | Add `SkyLightColor` shader uniform + time-of-day curve            | Low — shader-only, no BFS change                              | Set `SkyLightColor = white`                           |
| **Phase 2d** | Populate `lightData.gba` with RGB blocklight in mesh job          | Low — just reading new data                                   | Write zeros (Phase 1 behavior)                        |

### 4.2 Key Risks

- **Phase 1 performance:** ~24 memory accesses per face (12 voxel reads + 12 block type lookups) in Burst. Should be profiled against the baseline. The precomputed LUT and diagonal occlusion early-out reduce constant factors. The settings toggle provides a user-facing fallback if performance impact is unacceptable on target hardware.

- **Phase 2 BFS complexity:** Three-channel blocklight propagation roughly triples the comparison work per neighbor in the BFS inner loop (3 channel comparisons instead of 1). However, the memory access pattern is similar — the main cost is the voxel read, not the comparison. Darkness removal is more nuanced: removing a red source `(12, 0, 0)` should clear only the red channel, not blue light `(0, 0, 15)` at the same voxel from a different source. This requires the removal BFS to track per-channel old values.

- **Phase 2 cross-chunk mods:** The `LightModification` struct grows to carry RGB values. The write-through cache value type expands. Memory impact is proportional to the number of cross-chunk border voxels with blocklight — typically small but should be monitored.

- **UNorm8 quantization:** 256 levels per channel for 16 discrete light values is more than sufficient. However, if the corner averaging produces fractional results (e.g., average of 12 and 15 = 13.5), the UNorm8 encoding rounds to the nearest integer: `(byte)(13.5 * 17) = 229`, which decodes to `229/255 ≈ 0.898`. The true value `13.5/15 = 0.900` differs by 0.2% — imperceptible.

### 4.3 Benchmarking Plan

**Baseline (before any changes):**

1. Profile `MeshGenerationJob` duration via Profiler markers across a representative world (surface, caves, underwater, torch-lit areas).
2. Profile `NeighborhoodLightingJob` duration for the same world.
3. Record GPU frame time and vertex buffer memory usage.

**Phase 1 (smooth lighting):**

4. Profile `MeshGenerationJob` with smooth lighting enabled vs disabled (settings toggle).
5. Target: < 20% increase in mesh job duration with smooth lighting enabled.
6. Measure vertex buffer memory increase (should be ~7.1% with UNorm8x4).

**Phase 2 (RGB storage — Option A vs B):**

7. Prototype Option A (separate `uint` array): measure `NeighborhoodLightingJob` with triple-channel BFS.
8. Prototype Option B (`ulong` voxels): measure the same job.
9. Compare BFS duration, memory usage, and cache miss rates (via CPU profiler if available).
10. Decision: if difference < 10%, prefer Option B for simplicity. Otherwise, prefer Option A.

### 4.4 Testing Strategy

- **Visual regression:** Capture screenshots of known scenes (cave with torches, surface day/night, underwater, chunk borders) before and after Phase 1. Smooth lighting should only improve gradients — never change the average brightness of a face.
- **Boundary correctness:** Place torches at section boundaries and chunk boundaries. Verify no light seams or discontinuities at the 16-block section edges and the chunk edges (all 8 diagonal neighbors are sampled correctly).
- **Diagonal occlusion:** Build an L-shaped wall with a torch behind it. Verify that the inner corner is dark (AO effect) and no light leaks through the diagonal.
- **Anisotropy:** Build a 2×2 checkerboard of torch/dark blocks. Verify no visual "pinwheel" artifacts — the quad diagonal flip should produce a symmetric diamond pattern.
- **Settings toggle:** Toggle smooth lighting off and verify the rendering matches the pre-Phase-1 flat lighting exactly.
- **Phase 2 color mixing:** Place red and blue light sources near each other. Verify additive per-channel blending and correct attenuation with distance.
- **Phase 2 sky tinting:** Set time to night, verify blue moonlight tint outdoors. Enter a torch-lit room, verify warm indoor light is unaffected by sky color. Stand in a doorway, verify smooth gradient between blue outdoor and warm indoor.
- **Performance:** Compare profiler snapshots against baseline. Document results in this section after benchmarking.
