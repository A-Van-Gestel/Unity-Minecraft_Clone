# Smooth Lighting & Full RGB Light Engine

- **Status:** Phase 1 & Phase 2 Implemented (2026-06-07)
- **Current Implementation:** Per-vertex smooth lighting with per-channel RGB blocklight BFS + shader-only sky tinting
- **Phase 1 Target:** Smooth (ambient-occlusion-style) vertex-averaged lighting with full RGB data layout — **Implemented**
- **Phase 2 Target:** RGB blocklight propagation (per-channel independent BFS) + shader-only sunlight sky tinting — **Implemented**
- **Depends On:** None (can be implemented on the current codebase)
- **Prerequisites Completed:** BFS attenuation formula fix (`max(1, opacity)`) across all three call sites in `NeighborhoodLightingJob.cs`
- **Key Decisions (2026-06-06):**
    - Storage: Separate `NativeArray<ushort>` per section (Option A) — not widened `ulong` voxels
    - Sunlight: Shader-only sky tinting (monochrome BFS, tinted via `SkyLightColor` uniform) — not full BFS RGB
    - Blocklight: Per-channel independent propagation (3×4 bits) — not single emission + tint
    - Legacy `uint` light bits: Dual-write during Phase 2, deprecated in post-cleanup step
    - Queue serialization: Upgraded to RGB format with AOT migration — not reconstructed at runtime
    - Emission UI: Color picker with synced RGB sliders (0-15 per channel)

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

**Fix:** Before emitting the quad's triangle indices, compare the two possible diagonal splits and choose the one with lower contrast. All quad-emitting paths (standard cube faces, fluid top/side/bottom) share a single helper — `VoxelMeshHelper.EmitQuadTriangles` — so the luminance comparison logic is defined once:

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private static void EmitQuadTriangles(
    Color32 l0, Color32 l1, Color32 l2, Color32 l3,
    int vertexIndex, ref NativeList<int> triangles, bool reverseWinding = false)
{
    int lum0 = math.max(l0.r, (int)l0.a);
    int lum1 = math.max(l1.r, (int)l1.a);
    // If the 0↔3 diagonal has more luminance than 1↔2, flip the split.
    bool flip = lum0 + lum3 > lum1 + lum2;
    // Emit 6 indices with the chosen diagonal; reverseWinding flips CW↔CCW for bottom faces.
}
```

The `reverseWinding` parameter handles downward-facing quads (fluid bottom face) that need reversed CW winding when viewed from below. Cross meshes are handled separately (see Section 2.5).

### 2.4 Vertex Data Layout (Full RGB)

Phase 1 introduces a new UV channel (`TexCoord1`) carrying an `RGBA` value with the light breakdown, using `UNorm8 x4` format for compactness:

| Component | Phase 1 Value                         | Phase 2 Value (future)                    |
|-----------|---------------------------------------|-------------------------------------------|
| `R`       | Averaged sunlight luminance (0-255)   | Averaged sunlight Red (0-255)             |
| `G`       | Averaged sunlight luminance (0-255)   | Averaged sunlight Green (0-255)           |
| `B`       | Averaged sunlight luminance (0-255)   | Averaged sunlight Blue (0-255)            |
| `A`       | Averaged blocklight luminance (0-255) | Averaged max blocklight luminance (0-255) |

In Phase 1, sunlight luminance is replicated across all three RGB channels (`Color32(sun, sun, sun, block)`). This ensures the shader's `sunRGB / sunLuminance` tint extraction always yields `(1, 1, 1)` (pure white) rather than `(1, 0, 0)` (pure red), which would cause incorrect red tinting on all sunlit surfaces. Blocklight is scalar, stored in A. The shader reads `lightData.rgb` as sunlight and `lightData.a` as blocklight intensity.

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

Cross meshes (flowers, tall grass) consist of two intersecting diagonal quads within a single block space. All 8 unique vertex positions sit exactly at block corners (all coordinates are 0 or 1), so no bilinear interpolation is needed — each vertex maps directly to a corner light value.

**Phase 1 implementation:** Cross meshes use **smooth lighting** via a precomputed `CrossMeshCornerLights` struct (32 bytes, 2 levels × 4 corners). The quality depends on the `SmoothLightingQuality` setting:

- **Off:** Flat lighting — all vertices receive the same `Color32` from the flora block's own light level via `BuildFlatLight`. The struct is uniformly populated by the caller.
- **Standard:** Top-face corner assignment — `CalculateCornerLights(faceIndex=Top, pos)` computes 4 corner lights sampling the horizontal ring around the flora block. Top and bottom vertices share the same values (no vertical gradient).
- **High:** Two-level sampling — top vertices use `CalculateCornerLights(faceIndex=Top, pos)` (light at flora head height), bottom vertices use `CalculateCornerLights(faceIndex=Top, pos + down)` (light at ground level, where the direct neighbor is the flora block itself). This produces vertical gradients visible in caves and near directional light sources.

The vertex-to-corner mapping uses the XZ position: `L0=(0,0)`, `L1=(0,1)`, `L2=(1,0)`, `L3=(1,1)`, combined with Top-level for `y=1` vertices and Bot-level for `y=0` vertices. `AddCrossQuad` uses `EmitQuadTriangles` for anisotropy-aware triangle splitting, preventing checkerboard artifacts at light boundaries.

#### 2.5.2 Custom Meshes

Custom meshes use axis-aligned face culling identical to standard cubes (each custom mesh face maps to one of 6 face directions).

**Phase 1 implementation:** Custom meshes use **smooth lighting** via `CalculateCornerLights` when the setting is enabled, with per-vertex **bilinear interpolation** of the 4 corner light values. Unlike standard cubes (whose 4 vertices sit exactly at block corners), custom mesh vertices may occupy arbitrary positions within the face plane (e.g., a half-slab's top face vertices sit at y=0.5 instead of y=1.0).
The bilinear approach maps each vertex's rotated block-local position to (u, v) on the world face's perpendicular axes via `GetCornerUV`, then blends the 4 corner lights with `BilinearLerpLight`. This correctly handles sub-block geometry (half slabs, fences, stairs) — narrow meshes receive position-weighted gradients rather than hard corner snaps.
For standard-cube-shaped vertices at exact block corners, the bilinear result degenerates to the pure corner value, matching standard cube smooth lighting exactly. Both the legacy (`Quaternion.Euler`) and schema-aware (`float3x3` matrix) rotation paths are supported, with the world face index used to select the correct perpendicular axes for UV mapping.
No corner permutation is needed (unlike standard cubes) because the bilinear interpolation uses the vertex's actual rotated world position rather than a fixed index-based assignment. With smooth lighting disabled, the flat lighting fallback path with separate sun/block channels (`BuildFlatLightData`) remains unchanged.

#### 2.5.3 Fluids

Fluid meshes generate three face types: **top** (the visible water surface), **side** (vertical walls at pool edges and waterfall curtains), and **bottom** (underside visible when swimming beneath). Each face type has unique vertex position characteristics that affect how smooth lighting is applied.

**Phase 1 implementation:** Fluid meshes use **smooth lighting** via a precomputed `FluidCornerLights` struct when the setting is enabled, with per-face strategies matched to each face type's geometry. When smooth lighting is disabled, the flat fallback path with separate sun/block channels (`BuildFlatLight`) is used.

##### 2.5.3.1 `FluidCornerLights` Struct (Option A — Precomputed)

Corner lights for all 6 faces are precomputed in `MeshGenerationJob` (which has access to `CalculateCornerLights` and neighbor maps) and passed into `GenerateFluidMeshData` via a blittable struct:

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct FluidCornerLights
{
    // 6 faces × 4 corners = 24 × Color32 = 96 bytes (stack-friendly, Burst-safe).
    // Layout mirrors face index order: 0=Back, 1=Front, 2=Top, 3=Bottom, 4=Left, 5=Right.
    // Within each face, (L0, L1, L2, L3) match CalculateCornerLights output order.
    public Color32 BackL0, BackL1, BackL2, BackL3;
    public Color32 FrontL0, FrontL1, FrontL2, FrontL3;
    public Color32 TopL0, TopL1, TopL2, TopL3;
    public Color32 BottomL0, BottomL1, BottomL2, BottomL3;
    public Color32 LeftL0, LeftL1, LeftL2, LeftL3;
    public Color32 RightL0, RightL1, RightL2, RightL3;

    /// Returns the 4 corner lights for a given face index (0-5).
    public readonly void GetFace(int faceIndex,
        out Color32 l0, out Color32 l1, out Color32 l2, out Color32 l3);

    /// Stores the 4 corner lights for a given face index (0-5).
    public void SetFace(int faceIndex, Color32 l0, Color32 l1, Color32 l2, Color32 l3);
}
```

**Caller site in `MeshGenerationJob`** (fluid dispatch, before calling `GenerateFluidMeshData`):

```csharp
FluidCornerLights cornerLights = default;
if (SmoothLighting)
{
    // Reuse the already-fetched 14-neighbor array instead of re-calling GetVoxelStateFromLocalPos.
    // Mapping: Back(-Z)→2(S), Front(+Z)→0(N), Top(+Y)→8(Above), Bottom(-Y)→9(Below), Left(-X)→3(W), Right(+X)→1(E)
    for (int face = 0; face < 6; face++)
    {
        int neighborIdx = face switch { 0 => 2, 1 => 0, 2 => 8, 3 => 9, 4 => 3, 5 => 1, _ => 0 };
        OptionalVoxelState cached = neighbors[neighborIdx];
        VoxelState? directNeighbor = cached.HasValue ? new VoxelState?(cached.State) : null;
        CalculateCornerLights(face, pos, directNeighbor,
            out Color32 l0, out Color32 l1, out Color32 l2, out Color32 l3);
        cornerLights.SetFace(face, l0, l1, l2, l3);
    }
}
```

The 6 direct face neighbors are already present in the 14-element `neighbors` array (fetched for fluid height smoothing and face culling). The switch maps `BurstVoxelData.FaceChecks` face indices to `s_fluidNeighborOffsets` array indices, avoiding 6 redundant `GetVoxelStateFromLocalPos` calls per fluid block — each of which involves bounds checks and potential cross-chunk lookups.

**Trade-off:** All 6 face sets are computed upfront (72 neighbor lookups via `CalculateCornerLights`) even though a typical fluid block renders only 1–3 visible faces after culling. This wastes ~2–6 `CalculateCornerLights` calls per fluid block. The waste is bounded and small — these are cache-local reads within a 1-block radius in Burst-compiled code. At a few hundred fluid blocks per section, this is negligible compared to total mesh job cost. The architectural benefit (keeping all culling logic in `GenerateFluidMeshData`, clean struct boundary,
testable in isolation) outweighs
the bounded redundancy. If profiling later shows this is hot, the caller could precompute only the faces that survive a fast visibility pre-pass.

##### 2.5.3.2 Top Face — Direct Corner Assignment

The 4 top face vertices sit at block-corner XZ positions `(0,0)`, `(0,1)`, `(1,0)`, `(1,1)` with varying smoothed Y heights. Since XZ positions align exactly with block corners, `GetCornerUV` with `worldFaceIndex = 2 (Top)` would produce pure corner values — bilinear interpolation is unnecessary. The 4 corner lights are assigned directly:

```
BL vertex (x=0, z=0) ← TopL0   (corner 0)
TL vertex (x=0, z=1) ← TopL1   (corner 1)
BR vertex (x=1, z=0) ← TopL2   (corner 2)
TR vertex (x=1, z=1) ← TopL3   (corner 3)
```

**Light sampling direction:** `CalculateCornerLights(Top, pos, ...)` samples the `y+1` layer — the air or blocks above the fluid. This is correct: the top face normal points upward, so the illuminating light arrives from above. Light propagated *through* the fluid (attenuated by opacity) lives in the fluid block itself and affects side/bottom faces, not the top surface. When the block above is opaque (e.g., stone ceiling over an underwater cave), the opaque block contributes 0 to the average, naturally producing AO darkening under the ceiling.

**Submerged case:** When a fluid block of the same type is directly above, the top face is culled entirely (`!hasFluidAbove` check), so the corner light values for the top face are unused. The precomputed waste is accepted per Section 2.5.3.1.

**Anisotropy fix:** Applies identically to standard cubes — compare the two diagonal sums of the 4 corner lights and flip the triangle winding if needed.

##### 2.5.3.3 Bottom Face — Direct Corner Assignment with Remap

The 4 bottom face vertices sit at `y=0` with XZ at block corners. `CalculateCornerLights(Bottom, pos, ...)` samples the `y-1` layer — the blocks below the fluid. However, unlike the top face, the bottom face requires a **corner remap** because the `VoxelTris` LUT for face 3 (Bottom) defines corners in a different order than the vertex emission order:

```
VoxelTris LUT for Bottom: corners resolve to (1,0,0), (1,0,1), (0,0,0), (0,0,1)
  LUT corner 0 = (1,0,0) = BR
  LUT corner 1 = (1,0,1) = TR
  LUT corner 2 = (0,0,0) = BL
  LUT corner 3 = (0,0,1) = TL

Fluid vertex emission order: BL(0,0,0), TL(0,0,1), BR(1,0,0), TR(1,0,1)

Remap: BL ← corner 2, TL ← corner 3, BR ← corner 0, TR ← corner 1
```

This X-mirror is consistent with `GetCornerUV` for Bottom (face 3) using `u = 1 - x` (the X axis is flipped relative to Top). The remap ensures that the corner light at world position `(0,0,0)` ends up on the vertex at `(0,0,0)`, not on the opposite side.

**Light sampling direction:** The bottom face normal points downward. If there's air below (waterfall dropping into a cave), those air blocks carry light from nearby sources. If there's solid ground below (lake floor), the stone contributes 0, producing natural AO darkening at the floor.

**Anisotropy fix:** Same diagonal comparison and winding flip as top faces, applied after the remap.

##### 2.5.3.4 Side Faces — Bilinear Interpolation

Side face vertices come from the standard cube vertex lookup (`BurstVoxelData.VoxelVerts`), but their Y coordinates are overridden:

- **Top vertices** (`y > 0.5`): Replaced with smoothed corner heights via `GetCornerValue` — these are fractional (e.g., `0.4375` for fluid level 7).
- **Bottom vertices** (`y ≤ 0.5`): Either `0.0` (normal pool edge) or the neighbor's smoothed surface height (waterfall curtain when `useSmoothBottom = true`).

This means side face vertices have **sub-block Y positions**, exactly like custom mesh half-slabs. The existing bilinear interpolation infrastructure handles this directly:

```csharp
// For each of the 4 side vertices, after Y override:
cornerLights.GetFace(faceIndex, out Color32 l0, out Color32 l1, out Color32 l2, out Color32 l3);

// blockLocal = vertex position relative to block origin (e.g., (0, 0.4375, 0) for a side vertex)
GetCornerUV(faceIndex, blockLocal, out float u, out float v);
lightData.Add(BilinearLerpLight(l0, l1, l2, l3, u, v));
```

For side faces, `GetCornerUV` maps `v = blockLocalPos.y`. A vertex at `y=0.4375` gets `v=0.4375`, blending 56% toward the bottom corner lights and 44% toward the top — proportional to the vertex's actual height in the face plane.

**Waterfall curtain case** (`useSmoothBottom = true`): Bottom vertices sit at the neighbor's surface height (e.g., `y=0.3`). `GetCornerUV` handles this naturally — `v=0.3` gives an appropriately weighted blend. No special-case code is needed.

**Full-height side face** (`y` bottom = `0`, top = `1.0`): For submerged side faces where both top and bottom vertices sit exactly at block corners (`y=0.0` and `y=1.0`), `GetCornerUV` produces `v=0` and `v=1`, and `BilinearLerpLight` degenerates to pure corner values — matching standard cube behavior exactly.

**Anisotropy fix:** Compare the 4 interpolated light values at the actual vertex positions. Since side face vertices may not sit at corners, the comparison uses the bilinearly interpolated values rather than the raw corner lights.

##### 2.5.3.5 Flat Fallback (Smooth Lighting Disabled)

When `SmoothLighting` is disabled, `GenerateFluidMeshData` ignores the `FluidCornerLights` struct and uses `BuildFlatLight` with the direct neighbor's separated sun/block channels — identical to the current behavior but with proper channel separation:

```csharp
// Top face: sample from block above
Color32 fluidLight = above.HasValue
    ? BuildFlatLight(above.State.Sunlight, above.State.Blocklight)
    : new Color32(255, 255, 255, 0);

// Side faces: sample from side neighbor
Color32 sideFlatLight = sideNeighbor.HasValue
    ? BuildFlatLight(sideNeighbor.State.Sunlight, sideNeighbor.State.Blocklight)
    : new Color32(255, 255, 255, 0);

// Bottom face: sample from block below
Color32 bottomFlatLight = below.HasValue
    ? BuildFlatLight(below.State.Sunlight, below.State.Blocklight)
    : new Color32(255, 255, 255, 0);
```

This replaces the current merged-scalar `LightFloatToUNorm8` path, giving fluids proper sun/block channel separation even without smooth lighting.

#### 2.5.4 Legacy Rotated Blocks

Blocks using `GenerateStandardCubeWithLegacyOrientation` (the pre-Axis3 rotation path with `Quaternion.Euler`) — including `HorizontalOnly` (stone, dirt, and most terrain blocks) and `Legacy` schema blocks — use **smooth lighting** via `CalculateCornerLights` when the setting is enabled. Corner averaging is performed on the world face `p` (correct neighbor sampling), and the results are permuted to match the rotated vertex positions via `PermuteCornerLightsForYRotation` before emission. Shared vertices between adjacent blocks produce identical light
values regardless of per-block rotation.

Side faces do not need permutation because `GetTranslatedFaceIndex` remaps to a face whose vertex ordering, after rotation, naturally aligns with the world corner positions. Top and bottom faces (`translatedP == p`) do require permutation: `PermuteCornerLightsForYRotation` swaps `(l0, l1, l2, l3)` based on the Y rotation step count (0°/90°/180°/270°) so the anisotropy fix compares correct diagonal pairs. See [Bug 06 (fixed)](../Bugs/LIGHTING_BUGS.md#bug-06-diagonal-shadow-artifacts-on-smooth-lit-legacy-rotated-blocks).

#### 2.5.5 Phase 1 Mesh Type Coverage

| Mesh Type       | Smooth Lighting | Separate Sun/Block | Notes                                                                                            |
|-----------------|-----------------|--------------------|--------------------------------------------------------------------------------------------------|
| Standard cubes  | Yes             | Yes                | Full corner averaging + anisotropy fix                                                           |
| Axis3 / Facing6 | Yes             | Yes                | Via `EmitStandardCubeFaceIfVisible`                                                              |
| Legacy rotated  | Yes             | Yes                | Corner permutation via `PermuteCornerLightsForYRotation`                                         |
| Cross meshes    | Yes             | Yes                | Precomputed `CrossMeshCornerLights`; direct corner assignment, vertical gradient at High quality |
| Custom meshes   | Yes             | Yes                | Bilinear interpolation via `GetCornerUV` + `BilinearLerpLight`                                   |
| Fluids          | Yes             | Yes                | Precomputed `FluidCornerLights`; top/bottom direct, sides bilinear interp.                       |

### 2.6 Mesh Pipeline Changes

#### 2.6.1 `MeshDataJobOutput` (JobData.cs)

Two new buffers added to `MeshDataJobOutput`:

```csharp
public NativeList<Color32> LightData;              // TexCoord1: UNorm8 (sun, sun, sun, block) in Phase 1
public NativeList<NormalLightVertex> InterleavedStream3;  // Normals + LightData interleaved for GPU upload
```

`LightData` stores per-vertex light values using `Color32` (4 bytes, maps to UNorm8x4). `InterleavedStream3` is populated by `PostProcessMeshJob` in `Chunk.cs` (see Section 2.6.5) to combine normals and light data into a single GPU-uploadable buffer.

A supporting struct packs both attributes into a single 16-byte element for stream 3:

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct NormalLightVertex
{
    public Vector3 Normal;     // 12 bytes
    public Color32 LightData;  //  4 bytes
}
```

#### 2.6.2 `SectionRenderer` Vertex Layout

Normal and `TexCoord1` are **interleaved in stream 3** to stay within Unity's 4-stream limit (streams 0-3). An initial attempt to use stream 4 failed with `ArgumentException: Invalid vertex attribute stream value (4)`.

```csharp
private static readonly VertexAttributeDescriptor[] s_layout =
{
    new(VertexAttribute.Position,  VertexAttributeFormat.Float32, 3, stream: 0),  // 12 bytes
    new(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4, stream: 1),  // 16 bytes
    new(VertexAttribute.Color,     VertexAttributeFormat.Float32, 4, stream: 2),  // 16 bytes
    new(VertexAttribute.Normal,    VertexAttributeFormat.Float32, 3, stream: 3),  // 12 bytes ─┐ interleaved
    new(VertexAttribute.TexCoord1, VertexAttributeFormat.UNorm8,  4, stream: 3),  //  4 bytes ─┘ = 16 bytes
};
// Total: 60 bytes/vertex (up from 56, +7.1%)
// Stream 3 stride: 16 bytes (matches sizeof(NormalLightVertex))
```

`UpdateMeshNative` takes a single `NativeArray<NormalLightVertex> stream3` parameter and uploads it with one `SetVertexBufferData` call to stream 3, replacing the previous separate normals upload.

#### 2.6.3 `MeshGenerationJob` Corner Sampling

The smooth lighting functions are private methods on `MeshGenerationJob` (not `VoxelMeshHelper`) because they need direct access to the job's neighbor maps and `BlockTypes` array for voxel lookups:

```csharp
/// Computes per-vertex corner-averaged light for all 4 corners of a face.
/// Accepts the pre-fetched direct neighbor to avoid redundant lookup.
private void CalculateCornerLights(int faceIndex, Vector3Int blockPos,
    VoxelState? directNeighbor,
    out Color32 l0, out Color32 l1, out Color32 l2, out Color32 l3)

/// Samples the 3 LUT neighbors for one corner, applies diagonal occlusion,
/// averages with the direct neighbor, and encodes to UNorm8.
/// Phase 2: averages all 4 channels (sun, blockR, blockG, blockB) independently.
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private Color32 SampleCorner(int faceIndex, int cornerIndex, Vector3Int blockPos,
    byte directSun, byte directR, byte directG, byte directB)

/// Reads a neighbor's sun/block light values and opacity.
/// Phase 2: reads RGB blocklight from ushort light array via GetLightDataFromLocalPos.
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private void SampleNeighborLight(Vector3Int pos,
    out byte sun, out byte blockR, out byte blockG, out byte blockB, out bool isOpaque)
```

The encoding uses rounded integer arithmetic for Burst efficiency: `(byte)((sunSum * 17 + 2) / 4)` — the `+ 2` provides correct rounding for the divide-by-4 average.

#### 2.6.4 `MeshGenerationJob` — Standard Cube Path

`EmitStandardCubeFaceIfVisible` branches on the `SmoothLighting` flag:

- **Enabled:** Calls `CalculateCornerLights` to produce 4 distinct `Color32` values, then passes them to `GenerateStandardCubeFace`'s per-vertex overload (which includes the anisotropy fix).
- **Disabled:** Calls `BuildFlatLightData(neighborVoxel)` to produce a single `Color32` with separated sun/block channels, duplicated to all 4 vertices.

`BuildFlatLightData` reads the direct neighbor's `Sunlight` and `Blocklight` properties independently (not the merged `LightAsFloat`), encoding as `Color32(sun*17, sun*17, sun*17, block*17)`. This ensures correct day/night modulation even with smooth lighting disabled.

`GenerateStandardCubeWithLegacyOrientation` (used by `HorizontalOnly` and `Legacy` schema blocks) follows the same `SmoothLighting` branching pattern, calling `CalculateCornerLights` on the world face `p` for correct neighbor sampling, then `PermuteCornerLightsForYRotation` to align corner lights with the rotated vertex positions on top/bottom faces.

Custom meshes use smooth lighting with bilinear interpolation (see Section 2.5.2). Fluids use smooth lighting via a precomputed `FluidCornerLights` struct with direct corner assignment for top/bottom faces and bilinear interpolation for side faces (see Section 2.5.3). Cross meshes use flat lighting in Phase 1 (see Section 2.5.5).

#### 2.6.5 `Chunk.cs` — PostProcessMeshJob

A Burst-compiled `PostProcessMeshJob : IJob` in `Chunk.cs` runs on the main thread (via `Schedule().Complete()`) after the mesh generation job completes. It performs two tasks:

1. **Interleaves Normal + LightData** into `InterleavedStream3` for the GPU stream 3 upload:
   ```csharp
   InterleavedStream3.ResizeUninitialized(totalVerts);
   for (int v = 0; v < totalVerts; v++)
   {
       InterleavedStream3[v] = new NormalLightVertex
       {
           Normal = Normals[v],
           LightData = LightData[v],
       };
   }
   ```
2. **Adjusts vertex positions and triangle indices** from chunk-space to section-space (existing logic, unchanged).

This interleaving was moved from the main thread into a Burst job for performance — the tight copy loop benefits significantly from Burst's SIMD optimizations.

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

The liquid shader has its own vertex structures (`LiquidAppdata`, `LiquidV2F`) separate from `VoxelCommon.hlsl`. Phase 1 adds `half4 lightData : TEXCOORD1` to `LiquidAppdata` and uses a **scalar fallback** in the vertex shader:

```hlsl
o.lightLevel = max(v.lightData.r, v.lightData.a);
```

This takes the per-channel max of sunlight (R) and blocklight (A) to produce a single scalar that feeds into the existing `CalculateVoxelShade` path. The liquid fragment shader is unchanged — it continues to use the scalar `i.lightLevel` for its custom deep/shallow water color blending.

**Rationale:** The liquid shader applies shade in a non-standard way (manual blending between deep and shallow water colors based on depth), so it cannot directly use `ApplyVoxelLightingRGB`. A proper RGB-aware liquid lighting path is deferred to Phase 2, where the fragment shader would read separate sun/block channels and apply the shade curve independently.

`Color.a` is set to `0.0` for fluid vertices (previously carried light). `Color.rgba` is now `(liquidType, shoreMask, shadowMultiplier, 0)`.

#### 2.7.6 Editor Preview Shaders

Editor preview shaders read `TexCoord1` directly — all editor mesh generators populate the attribute with valid light data:

- **`BlockPreviewShader.shader`:** The vertex shader passes `lightData` through via `VoxelVert` unchanged. The fragment shader uses `ApplyVoxelLightingRGB` with hardcoded daylight globals. `EditorMeshGenerator` populates `TexCoord1` with full brightness `(1,1,1,1)` for block editor icons; `ChunkPreview3DWindow` provides actual per-vertex smooth lighting data via `MeshPostProcessJob`.

- **`FluidPreviewShader.shader`:** Same as `BlockPreviewShader` — reads `lightData` from `TexCoord1` directly. Fluid meshes from `EditorMeshGenerator` receive full brightness; meshes from `ChunkPreview3DWindow` receive smooth-lit values.

- **`ChunkPreviewShader.shader`:** Does **not** read `TexCoord1`. Uses a hardcoded `lightLevel = 1.0` as a minimal fallback shader. Only used when `BlockDatabase` is unavailable (the `ChunkPreview3DWindow` normally uses `BlockPreviewShader` and `FluidPreviewShader` via `EditorPreviewMaterialUtility`).

- **`DebugVoxelShader.shader`:** Not updated — continues to use `Color` only for its debug visualization. Does not read `TexCoord1`.

### 2.8 Settings Quality Level

A **Smooth Lighting** quality dropdown in the Graphics settings tab controls per-vertex light averaging via `SmoothLightingQuality`:

```csharp
[SettingField(SettingsTab.Graphics, Label = "Smooth Lighting")]
public SmoothLightingQuality smoothLighting = SmoothLightingQuality.High;
```

| Level        | Behavior                                                                                                        |
|--------------|-----------------------------------------------------------------------------------------------------------------|
| **Off**      | Flat per-face lighting (classic blocky look). Mesh job skips corner averaging entirely.                         |
| **Standard** | Corner-averaged AO with horizontal gradients for all mesh types. Cross meshes sample 1 Y-level (top face only). |
| **High**     | Standard plus vertical gradients on cross meshes (flora). Samples 2 Y-levels — head height and ground level.    |

The shader, vertex layout, and data format are unchanged across all levels — the setting only controls how much CPU-side averaging the mesh job performs. This provides:

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

With `UNorm8 x4` for `TexCoord1` interleaved into stream 3: **4 bytes/vertex** additional.

- Previous: 56 bytes/vertex (Position 12 + TexCoord0 16 + Color 16 + Normal 12)
- New: 60 bytes/vertex (Position 12 + TexCoord0 16 + Color 16 + Normal+TexCoord1 16) (+7.1%)
- Stream 3 grew from 12 bytes/vertex (Normal only) to 16 bytes/vertex (Normal + LightData interleaved)
- For a typical section with ~3000 vertices: +12 KB per section
- At 2000 loaded sections: +24 MB total GPU memory

This is modest. Using `Float32 x4` instead would be +16 bytes/vertex (+28.6%), adding ~96 MB — nearly 4x worse for zero quality benefit.

#### 2.9.3 Underwater Gradient Quality

With water opacity = 2, the BFS formula (`sourceLight - max(1, opacity)`) attenuates light by 2 per step: 15 → 13 → 11 → 9 → 7 → 5 → 3 → 1 → 0. Adjacent blocks differ by 2 light levels. After corner averaging with 4 neighbors, the gradient smooths to approximately 1-level differences between adjacent vertices — producing a gentle, natural-looking underwater falloff.

> **Prerequisite completed:** The BFS attenuation formula was aligned with the Starlight/Moonrise `max(1, opacity)` formula (see `LIGHTING_SYSTEM_OVERVIEW.md` Section 4.2). All three attenuation sites (`PropagateLight`, `RecalculateSunlightForColumn`, `CheckEdgeVoxel`) now use the consistent formula, eliminating the previous 1-level shadow line artifact at chunk borders underwater.

### 2.10 Scope

**Files modified:**

| File                                                | Change                                                                                                                                                                                                                                                                                                                                                                                                                                   |
|-----------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Data/Enums/SmoothLightingQuality.cs`               | Add `SmoothLightingQuality` enum (`Off`, `Standard`, `High`) — byte-backed for Burst compatibility                                                                                                                                                                                                                                                                                                                                       |
| `Data/JobData.cs`                                   | Add `NormalLightVertex` struct, `LightData` + `InterleavedStream3` to `MeshDataJobOutput`; add `FluidCornerLights` (96 bytes), `CrossMeshCornerLights` (32 bytes, 2 levels × 4 corners)                                                                                                                                                                                                                                                  |
| `Jobs/BurstData/BurstVoxelData.cs`                  | Add `CornerOffsets` SharedStatic LUT (72 × `int3`) with `BuildCornerOffsetLUT`                                                                                                                                                                                                                                                                                                                                                           |
| `Data/JobData.cs`                                   | (see above — `FluidCornerLights` + `CrossMeshCornerLights` structs)                                                                                                                                                                                                                                                                                                                                                                      |
| `Helpers/VoxelMeshHelper.cs`                        | Add `ref lightData` param to all vertex-emitting methods, `LightFloatToUNorm8` helper, shared `EmitQuadTriangles` for anisotropy-aware winding (used by standard cubes, all 3 fluid face types, and cross meshes); fluid smooth lighting via `FluidCornerLights` + `GetCornerUV`/`BilinearLerpLight` for side faces; cross mesh smooth lighting via `CrossMeshCornerLights` with direct corner assignment and per-vertex Y-level mapping |
| `Jobs/MeshGenerationJob.cs`                         | Add `CalculateCornerLights`/`SampleCorner`/`SampleNeighborLight`/`BuildFlatLightData`; `SmoothLightingQuality` enum field (replaces `bool`); precompute `FluidCornerLights` for 6 faces before fluid dispatch; precompute `CrossMeshCornerLights` (1 or 2 `CalculateCornerLights` calls depending on quality level)                                                                                                                      |
| `SectionRenderer.cs`                                | Interleave Normal + TexCoord1 (UNorm8x4) in stream 3, accept `NativeArray<NormalLightVertex>`                                                                                                                                                                                                                                                                                                                                            |
| `Chunk.cs`                                          | Add `PostProcessMeshJob` (Burst) for Normal/LightData interleaving, thread `InterleavedStream3` through `ApplyMeshData`                                                                                                                                                                                                                                                                                                                  |
| `WorldJobManager.cs`                                | Pass `settings.smoothLighting` to `MeshGenerationJob.SmoothLighting`                                                                                                                                                                                                                                                                                                                                                                     |
| `SettingsManager.cs`                                | `smoothLighting` field changed from `bool` to `SmoothLightingQuality` enum (Off/Standard/High) under Graphics → Lighting subheader                                                                                                                                                                                                                                                                                                       |
| `Shaders/Includes/VoxelCommon.hlsl`                 | Add `lightData` to `VoxelAppdata` + `VoxelV2F`, pass through in `VoxelVert`                                                                                                                                                                                                                                                                                                                                                              |
| `Shaders/Includes/VoxelLighting.hlsl`               | Add `ApplyVoxelLightingRGB` + `VoxelLightToShadow` (existing `ApplyVoxelLighting` preserved for compatibility)                                                                                                                                                                                                                                                                                                                           |
| `Shaders/StandardBlockShader.shader`                | Fragment uses `ApplyVoxelLightingRGB(col.rgb, i.lightData.rgb, i.lightData.a, ...)`                                                                                                                                                                                                                                                                                                                                                      |
| `Shaders/TransparentBlockShader.shader`             | Same as StandardBlockShader                                                                                                                                                                                                                                                                                                                                                                                                              |
| `Shaders/Includes/LiquidCore.hlsl`                  | Add `lightData` to `LiquidAppdata`, scalar fallback: `o.lightLevel = max(v.lightData.r, v.lightData.a)`                                                                                                                                                                                                                                                                                                                                  |
| `Shaders/Editor/BlockPreviewShader.shader`          | Override `o.lightData = half4(1,1,1,1)`, use `ApplyVoxelLightingRGB` with hardcoded daylight                                                                                                                                                                                                                                                                                                                                             |
| `Shaders/Editor/ChunkPreviewShader.shader`          | Hardcode `lightLevel = 1.0`, does not read `TexCoord1`                                                                                                                                                                                                                                                                                                                                                                                   |
| `Editor/BlockEditor/Helpers/EditorMeshGenerator.cs` | Thread `NativeList<Color32> nativeLightData` through all `VoxelMeshHelper` calls                                                                                                                                                                                                                                                                                                                                                         |

**Files NOT modified:** `NeighborhoodLightingJob.cs` (only the BFS attenuation fix, which is a prerequisite), `BurstVoxelDataBitMapping.cs`, `ChunkData.cs` — the lighting BFS propagation logic and voxel data format are unchanged in Phase 1.

---

## 3. Phase 2: Full RGB Light Engine

### 3.1 Overview

Phase 2 makes **blocklight** fully RGB-aware in the lighting engine and adds **shader-only sky tinting** for sunlight. This means:

- **Sunlight:** Remains a monochrome scalar (0-15) in the BFS, identical to Phase 1. Time-of-day color (blue moonlight, red blood moon, warm dawn) is applied as a shader uniform (`SkyLightColor`) that tints the scalar value at render time. This achieves the same visual result as full BFS RGB sunlight with zero lighting engine cost, because the sky is a uniform color — there is no per-region sky gradient to model.
- **Blocklight RGB:** Each light-emitting block defines an emission color via a color picker with synced RGB sliders (each channel 0-15). The BFS propagates three independent color channels using per-channel max at each destination voxel.

The smooth-lighting vertex averaging from Phase 1 already produces `Color32` light data per vertex. Phase 2 replaces the duplicated sunlight channels (R=G=B) and scalar blocklight (A) with `(sunLuminance, blockR, blockG, blockB)`. **No vertex format, mesh upload, or stream layout changes are needed** — only the encoding in `SampleCorner`/`BuildFlatLightData` and the shader's `ApplyVoxelLightingRGB` function update.

### 3.1.1 Design Rationale: Per-Channel Propagation vs Single Emission + Tint

An alternative approach was considered: propagate a single scalar emission through the BFS and store a separate color "tint" per voxel. This was rejected because it cannot handle multi-source color mixing correctly.

**The fundamental problem:** When two differently-colored light sources illuminate the same voxel (e.g., a red torch and a blue lantern both contributing intensity 12), the tint approach must pick or blend a single color. Picking one loses the other source entirely; blending produces an average `(0.5, 0, 0.5)` rather than the physically correct additive result `(1, 0, 1)`. The BFS processes neighbors in non-deterministic order, so the "winner" would be unstable.

**Per-channel propagation solves this naturally:** Each channel propagates and attenuates independently. At overlap zones, per-channel `max()` produces the correct additive color mixing — both sources contribute at full strength. No provenance tracking, no blending ambiguity, no order dependence.

The sky tinting approach works for sunlight specifically because all sunlight comes from one source (the sky), so there is never a color mixing problem — every sun-lit voxel has the same color, just different scalar intensity.

### 3.2 Voxel Data Changes

#### 3.2.1 The Storage Problem

The current 32-bit `uint` allocates 4 bits each for sunlight and blocklight (8 bits total). RGB blocklight needs 12 additional bits:

- Sunlight: 1 channel × 4 bits = 4 bits (monochrome, tinted in shader)
- Blocklight: 3 channels × 4 bits = 12 bits
- Total: 16 bits (up from 8), requiring 8 additional bits

```
Current:  [ID: 16][Sun: 4][Block: 4][Meta: 8] = 32 bits — fully used
RGB need: [Sun: 4][BlockR: 4][BlockG: 4][BlockB: 4] = 16 bits — separate array
```

#### 3.2.2 Chosen Solution: Separate `NativeArray<ushort>` Light Storage

> **Decision (2026-06-06):** Option A (separate light array) selected over Option B (64-bit voxel) without benchmarking. Option A is architecturally superior — it avoids the enormous blast radius of widening the primary voxel `uint` (serialization migration, generation, fluid logic, all bit-mapping helpers), enables future optimizations (lazy allocation for empty sections, section-level light culling), and provides better cache behavior for the BFS (smaller, contiguous light-only working set). Option B's only advantage (single source of truth) does not
> justify doubling the voxel data memory footprint and touching every system in the pipeline.

Keep the existing `uint` voxel data for ID + metadata. Add a parallel `ushort` per voxel for all light channels:

```
Existing uint:  [ID: 16][Sun(legacy): 4][Block(legacy): 4][Meta: 8] = 32 bits
New ushort:     [Sun: 4][BlockR: 4][BlockG: 4][BlockB: 4] = 16 bits
```

**Why `ushort` (16-bit) instead of `uint` (32-bit):** Exactly 16 bits of light data are needed (4 channels × 4 bits). Using `ushort` halves the memory cost compared to `uint` (64 KB vs 128 KB per chunk, or 32,768 × 2 bytes) and improves cache density for the BFS inner loop. The tradeoff — `ushort` bit manipulation in Burst requires casting — is trivial to wrap in helper methods.

**Memory impact:**

- Per section (16×16×16): 4,096 × 2 bytes = 8 KB
- Per chunk (16 sections): 65,536 × 2 bytes = 128 KB *(but only 32,768 voxels per chunk with current height — sections can be allocated lazily)*
- At 2,000 loaded chunks: ~128 MB worst-case *(significantly less with lazy allocation for empty/air-only sections)*

**No change to the existing `uint` layout:** ID, metadata, serialization, fluid logic, and all non-lighting systems are untouched. Clean separation of concerns: one array for block identity, one for light state. The BFS can operate on a contiguous light-only buffer without polluting the cache with ID/metadata bits.

#### 3.2.3 `ushort` Light Array Bit Packing

```
Bit layout (16 bits total):
  [Sun: 4][BlockR: 4][BlockG: 4][BlockB: 4]

  Bits 0-3:   Sunlight scalar    (0-15)
  Bits 4-7:   Blocklight Red     (0-15)
  Bits 8-11:  Blocklight Green   (0-15)
  Bits 12-15: Blocklight Blue    (0-15)
```

Helper methods (in a separate `LightBitMapping` static class at `Jobs/BurstData/LightBitMapping.cs`):

```csharp
private const int SUN_SHIFT = 0;
private const int BLOCK_R_SHIFT = 4;
private const int BLOCK_G_SHIFT = 8;
private const int BLOCK_B_SHIFT = 12;
private const int CHANNEL_MASK = 0xF;

[MethodImpl(MethodImplOptions.AggressiveInlining)]
public static byte GetSunLight(ushort lightData) => (byte)((lightData >> SUN_SHIFT) & CHANNEL_MASK);

[MethodImpl(MethodImplOptions.AggressiveInlining)]
public static byte GetBlocklightR(ushort lightData) => (byte)((lightData >> BLOCK_R_SHIFT) & CHANNEL_MASK);

[MethodImpl(MethodImplOptions.AggressiveInlining)]
public static byte GetBlocklightG(ushort lightData) => (byte)((lightData >> BLOCK_G_SHIFT) & CHANNEL_MASK);

[MethodImpl(MethodImplOptions.AggressiveInlining)]
public static byte GetBlocklightB(ushort lightData) => (byte)((lightData >> BLOCK_B_SHIFT) & CHANNEL_MASK);

[MethodImpl(MethodImplOptions.AggressiveInlining)]
public static ushort PackLightData(byte sun, byte blockR, byte blockG, byte blockB)
    => (ushort)((sun & CHANNEL_MASK) | ((blockR & CHANNEL_MASK) << BLOCK_R_SHIFT)
        | ((blockG & CHANNEL_MASK) << BLOCK_G_SHIFT) | ((blockB & CHANNEL_MASK) << BLOCK_B_SHIFT));
```

#### 3.2.4 Legacy Scalar Light Bits (Dual-Write Strategy)

The sunlight (bits 16-19) and blocklight (bits 20-23) in the existing `uint` become **derived values** during Phase 2 implementation. After the BFS writes to the `ushort` light array, it also writes `max(R,G,B)` to the legacy blocklight bits and the sunlight scalar to the legacy sunlight bits. This maintains compatibility with all existing scalar light readers:

- `ChunkData.ModifyVoxel` — reads `GetSunLight()`/`GetBlockLight()` for queue seeding
- `MeshGenerationJob.SampleNeighborLight` — reads scalar light for smooth lighting
- Mob spawning checks — uses `max(sun, block) >= threshold`
- Face culling heuristics

**Post-cleanup step (after Phase 2 is stable):** Migrate all scalar light readers to the new `ushort` array, stop dual-writing, and free the 8 bits in the `uint` for future metadata expansion (biome tint, damage state, block variant, etc.). This is a separate refactor, not part of the Phase 2 implementation.

### 3.3 Sunlight: Shader-Only Sky Tinting

> **Decision (2026-06-06):** Shader-only tinting confirmed. The sunlight BFS remains monochrome (scalar 0-15, unchanged from Phase 1). The sky color is applied as a per-frame shader uniform. This delivers the visual goal (blue moonlight, red blood moon, warm dawn) with zero lighting engine cost. Full BFS RGB sunlight could be added later if per-region sky coloring becomes a requirement, but is not planned.

#### 3.3.1 Sky Color Uniform

`World.cs` already sets `GlobalLightLevel` (0-1) as a day/night cycle uniform. Phase 2 adds a `SkyLightColor` uniform (RGB) that varies with time of day:

```csharp
// World.cs
[SerializeField] private Gradient _skyLightGradient;  // Configurable in inspector
private Color _skyLightColor = Color.white;            // Updated per frame from gradient

// Example gradient stops:
// Dawn  (0.25): (1.0, 0.85, 0.6)   — warm orange
// Noon  (0.50): (1.0, 1.0, 1.0)    — pure white
// Dusk  (0.75): (1.0, 0.7, 0.4)    — deep orange
// Night (0.00): (0.6, 0.7, 1.0)    — cool blue (moonlight)
// Blood moon:   (1.0, 0.2, 0.15)   — deep red (event-driven override)
```

The shader multiplies the monochrome sunlight scalar by this color:

```hlsl
half3 sunContrib = baseColor * sunShadow * SkyLightColor;
```

#### 3.3.2 Sunlight BFS — Unchanged

The sunlight BFS continues to propagate a single scalar value (0-15), identical to Phase 1. The `ushort` light array stores the sunlight scalar in bits 0-3. No changes to `RecalculateSunlightForColumn`, `PropagateLight` (sun channel), or `PropagateDarkness` (sun channel) are needed.

#### 3.3.3 Sun/Block Interaction at Boundaries

The shader-only approach produces correct sun/block blending because the final compositing uses per-channel `max()` after both contributions are fully computed:

```hlsl
// Sunlight: scalar × shade curve × sky color → RGB contribution
half3 sunContrib = baseColor * sunShadow * SkyLightColor;

// Blocklight: RGB channels × shade curve (always full intensity) → RGB contribution
half3 blockContrib = baseColor * half3(blockR_shadow, blockG_shadow, blockB_shadow);

// Per-channel max: brighter source wins per RGB channel
return max(sunContrib, blockContrib);
```

At a doorway at dusk: outside has high sun luminance tinted warm orange, inside has high blocklight RGB from torches. The smooth lighting vertex averaging produces a gradient of sun luminance (high→low) and blocklight RGB (low→high). The per-channel `max()` picks the dominant source at each point, creating a natural crossfade between warm outdoor sunset light and warm indoor torch light.

### 3.4 Blocklight RGB Changes

#### 3.4.1 Block Type Changes

Add an emission color to `BlockType` / `BlockTypeJobData`:

```csharp
// In BlockType (ScriptableObject / inspector-editable)
[ColorUsage(false)]  // No alpha needed
public Color lightEmissionColor = Color.white;  // RGB emission tint

[Range(0, 15)]
public byte lightEmission;  // Peak emission intensity (existing field)
// Inspector UI: color picker sets the ratio, intensity slider sets the peak channel value.
// A torch defined as color #FF9944 at intensity 15 maps to (15, 9, 4).
// Synced RGB sliders (0-15 per channel) provide a fallback for precise control.

// In BlockTypeJobData (Burst-safe job copy)
public readonly byte EmissionR;  // 0-15
public readonly byte EmissionG;  // 0-15
public readonly byte EmissionB;  // 0-15
```

**Emission derivation:** The inspector color picker and intensity slider are combined to produce the 3 emission channels:

```csharp
// In BlockTypeJobData constructor:
float maxComponent = Mathf.Max(color.r, color.g, color.b);
float scale = maxComponent > 0 ? lightEmission / maxComponent : 0;
EmissionR = (byte)Mathf.Clamp(Mathf.RoundToInt(color.r * scale), 0, 15);
EmissionG = (byte)Mathf.Clamp(Mathf.RoundToInt(color.g * scale), 0, 15);
EmissionB = (byte)Mathf.Clamp(Mathf.RoundToInt(color.b * scale), 0, 15);
```

The existing `lightEmission` scalar is derived as `max(EmissionR, EmissionG, EmissionB)` and kept for backwards compatibility with systems that only need scalar luminance (mob spawning, legacy dual-write to `uint` blocklight bits). Existing blocks with `lightEmission > 0` and no `lightEmissionColor` set default to white `(1, 1, 1)`, producing `(15, 15, 15)` — identical to the current scalar behavior.

#### 3.4.2 BFS Propagation Changes

The blocklight BFS propagates three channels independently. Each channel attenuates via the block's opacity:

```
targetR = sourceR - max(1, opacity)
targetG = sourceG - max(1, opacity)
targetB = sourceB - max(1, opacity)
```

A neighbor is enqueued if **any** channel increased. At the destination voxel, each channel is stored as `max(existing, incoming)` — the brightest source wins per-channel, enabling correct additive color mixing.

#### 3.4.3 Queue Entry Format

The current BFS queue entries use `LightQueueNode { Vector3Int Position; byte OldLightLevel }` (13 bytes) for the managed-side queue and `LightRemovalNode { Vector3Int Pos; byte LightLevel }` (13 bytes) inside the job. With RGB blocklight, the blocklight queue entries must carry 3 channel levels.

**Managed-side queue (`ChunkData.cs`):**

```csharp
public struct LightQueueNode : IEquatable<LightQueueNode>
{
    public Vector3Int Position;
    public byte OldLightLevel;   // Sunlight queue: scalar (unchanged)
    public byte OldBlockR;       // Blocklight queue: RGB channels
    public byte OldBlockG;
    public byte OldBlockB;
}
```

The sunlight queue continues to use `OldLightLevel` only. The blocklight queue uses `OldBlockR/G/B`. Adding 3 bytes to the struct is acceptable — queue sizes are bounded (typically <1000 entries per chunk) and the struct is not in a hot loop.

**Job-side queue (`NeighborhoodLightingJob.cs`):**

```csharp
public struct LightRemovalNode
{
    public Vector3Int Pos;
    public byte LightR;   // Old light values for per-channel darkness removal
    public byte LightG;
    public byte LightB;
}
```

For the darkness removal BFS, entries carry the **old** RGB values so the removal phase can correctly identify which channels were dependent on the removed source.

**Serialization:** The light queue is serialized to disk (pending light work survives save/load). The queue entry format change requires an AOT migration step that expands each serialized entry from `(Vector3Int + byte)` to `(Vector3Int + byte + byte + byte + byte)`. See Section 3.7.

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

`LightModification` expands to carry RGB blocklight values:

```csharp
public struct LightModification
{
    public Vector3Int GlobalPosition;
    public byte LightLevel;    // Sunlight: scalar (unchanged)
    public byte BlockR;        // Blocklight: RGB channels
    public byte BlockG;
    public byte BlockB;
    public LightChannel Channel;
}
```

When `Channel == LightChannel.Block`, the RGB fields are used. When `Channel == LightChannel.Sun`, `LightLevel` is used (scalar, unchanged).

**Write-through cache:** The current `NativeHashMap<long, uint>` caches the full packed `uint` voxel data for neighbor positions modified during the job. With the separate `ushort` light array, the cache must also track light modifications. Options:

- **Expand value to `ulong`:** Pack both the voxel `uint` and the light `ushort` into a single `ulong` cache entry: `(uint voxelData << 16) | lightData`. Simple, no extra hash map.
- **Separate cache:** Add a second `NativeHashMap<long, ushort>` for light data. Cleaner separation, two lookups per read.

The `ulong` packing approach is preferred for cache coherence — a single hash map lookup returns both voxel and light data.

### 3.5 Mesh Job Changes (Minimal)

The `CalculateCornerLights` function from Phase 1 already returns `Color32`. In Phase 1, it reads scalar sunlight and blocklight and encodes them as `Color32(sun, sun, sun, block)`. In Phase 2, `SampleNeighborLight` reads from both the voxel `uint` (for sunlight scalar and block type opacity) and the `ushort` light array (for RGB blocklight):

```csharp
// Sunlight is still scalar from the uint (or from ushort bits 0-3)
byte sun = LightBitMapping.GetSunLight(lightData);

// Blocklight is now RGB from the ushort light array
byte blockR = LightBitMapping.GetBlocklightR(lightData);
byte blockG = LightBitMapping.GetBlocklightG(lightData);
byte blockB = LightBitMapping.GetBlocklightB(lightData);

// Encode into Color32 for TexCoord1
Color32 result = new Color32(
    (byte)(sun * 17),      // R: sun luminance (shader tints via SkyLightColor)
    (byte)(blockR * 17),   // G: block red
    (byte)(blockG * 17),   // B: block green
    (byte)(blockB * 17)    // A: block blue
);
```

The `SampleCorner` function averages each of the 4 channels independently across the 4 corner neighbors, using the same rounded integer arithmetic as Phase 1: `(byte)((channelSum * 17 + 2) / 4)`.

`BuildFlatLightData` (smooth lighting disabled) follows the same encoding: `Color32(sun*17, blockR*17, blockG*17, blockB*17)`.

#### 3.5.1 TexCoord1 Layout (Phase 2)

| Component | Phase 1 (current)                      | Phase 2                                     |
|-----------|----------------------------------------|---------------------------------------------|
| `R`       | Sun luminance × 17 (0-255)             | Sun luminance × 17 (0-255, shader tints it) |
| `G`       | Sun luminance × 17 (duplicate, wasted) | Blocklight Red × 17 (0-255)                 |
| `B`       | Sun luminance × 17 (duplicate, wasted) | Blocklight Green × 17 (0-255)               |
| `A`       | Block luminance × 17 (scalar)          | Blocklight Blue × 17 (0-255)                |

This fits cleanly into the existing `UNorm8 x4` format with **no vertex layout changes**. The G and B channels, currently wasted as duplicates of R, become blocklight color channels. No changes to `SectionRenderer.cs`, `NormalLightVertex`, or stream layout are needed.

### 3.6 Shader Changes (Phase 2)

#### 3.6.1 New Uniform

```hlsl
half3 SkyLightColor;  // Set by World.cs per frame from the sky light gradient
```

Added to `StandardBlockShader.shader`, `TransparentBlockShader.shader`, and `UberLiquidShader.shader` properties blocks.

#### 3.6.2 Updated `ApplyVoxelLightingRGB`

The Phase 1 function signature changes to accept the sky color tint and read blocklight as RGB:

```hlsl
half3 ApplyVoxelLightingRGB(half3 color,
                            float sunLuminance, half3 blockRGB,
                            half3 skyColor,
                            float globalLight, float minLight, float maxLight)
{
    // Sunlight: scalar luminance × shade curve × sky color tint
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

**Call sites change from:**

```hlsl
col.rgb = ApplyVoxelLightingRGB(col.rgb, i.lightData.rgb, i.lightData.a, ...);
```

**To:**

```hlsl
col.rgb = ApplyVoxelLightingRGB(col.rgb, i.lightData.r, i.lightData.gba, SkyLightColor, ...);
```

#### 3.6.3 Liquid Shader Scalar Fallback

`LiquidCore.hlsl` updates its scalar fallback to take the per-channel max across all 4 light components:

```hlsl
o.lightLevel = max(v.lightData.r, max(v.lightData.g, max(v.lightData.b, v.lightData.a)));
```

A proper RGB-aware liquid lighting path is deferred — the liquid fragment shader uses a custom deep/shallow water color blending that requires separate design work for RGB integration.

#### 3.6.4 Notes on Shade Curve at Zero

Each blocklight channel goes through `VoxelLightToShadow` independently. A channel at 0.0 produces shadow ~= 0.006 (near-zero). A channel at 1.0 produces shadow = 1.0 (full brightness). Channels at intermediate values follow the same gamma curve as sunlight — ensuring a red torch `(0.8, 0.1, 0.0)` has the same perceived brightness falloff as sunlight at the same numeric level.

When all 3 blocklight channels are 0.0, the blocklight contribution is uniformly ~0.006 across all RGB channels — effectively invisible and always losing the `max()` against any non-trivial sunlight contribution. No light is "invented" where none exists.

### 3.7 Serialization Impact

#### 3.7.1 Light Array — Runtime-Only

The `ushort` light array is **runtime-only** and does **not** need to be saved to disk. On chunk load, the light array is initialized from the `uint` packed voxel data via `ChunkSerializer.InitLightDataFromPacked`: sunlight is copied directly, and scalar blocklight is mapped to white RGB `(L, L, L)`. This provides a correct baseline for chunks that skip the lighting pipeline (already fully lit, no pending changes). Chunks that do go through the lighting pipeline (initial lighting, pending queue entries, edge checks) will overwrite these values with
proper BFS-computed RGB data. This means:

- No changes to the section serialization format for voxel data.
- The `ushort` light array is allocated during chunk generation and freed on unload.
- The existing scalar sunlight/blocklight bits in the `uint` continue to be written to disk as part of the voxel data (they are derived values during Phase 2, deprecated in the post-cleanup step).
- On every load, `InitLightDataFromPacked` derives the ushort from the uint, ensuring the mesh job always has valid light data even without a BFS pass.

#### 3.7.2 Light Queue Serialization — Upgraded to RGB

> **Decision (2026-06-06):** The light queue serialization is upgraded to carry RGB blocklight values (not reconstructed at runtime). This matches the existing architecture where pending light work survives save/load cycles, and avoids edge cases where partially-propagated light values persist in voxel data but the queue that would finish them is lost.

The current `WriteLightQueue`/`ReadLightQueue` in `ChunkSerializer.cs` serializes `LightQueueNode` entries as `(Vector3Int + byte)` = 13 bytes per entry. Phase 2 expands this to `(Vector3Int + byte + byte + byte + byte)` = 16 bytes per entry (sunlight scalar + 3 blocklight RGB channels).

**Migration step required:** This is a chunk format change. A new AOT migration step (`Migration_v{N}_to_v{N+1}_RGBLightQueues.cs`) must:

1. Bump `SaveSystem.CURRENT_VERSION`.
2. Read old-format queue entries `(pos, oldLightLevel)`.
3. Write new-format entries `(pos, oldLightLevel, oldBlockR=oldLightLevel, oldBlockG=oldLightLevel, oldBlockB=oldLightLevel)` — mapping the old scalar blocklight to white `(L, L, L)` preserves the original behavior for in-flight light operations.

**Backwards compatibility:** Old worlds with pending scalar blocklight operations will have their queue entries expanded to white RGB. This is correct — all existing light sources emit white light (scalar 0-15 maps to `(L, L, L)`) until block definitions are updated with emission colors.

### 3.8 Scope

**Files modified (beyond Phase 1):**

| File                                                             | Change                                                                                                                                                                                                                             |
|------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Data/BlockType.cs`                                              | Add `lightEmissionColor` field (Color, inspector color picker)                                                                                                                                                                     |
| `Data/JobData.cs` (`BlockTypeJobData`)                           | Add `EmissionR`, `EmissionG`, `EmissionB` bytes; derive from color + intensity                                                                                                                                                     |
| `Data/JobData.cs` (`LightQueueNode`)                             | Add `OldBlockR`, `OldBlockG`, `OldBlockB` bytes for RGB queue entries                                                                                                                                                              |
| `Data/ChunkSection.cs`                                           | Add `ushort[]` light array (managed side); pool reset via `Array.Clear`                                                                                                                                                            |
| `Data/ChunkData.cs`                                              | RGB-aware `ModifyVoxel` light queuing; `AddToBlockLightQueue` carries RGB values                                                                                                                                                   |
| `Jobs/BurstData/BurstVoxelDataBitMapping.cs`                     | Add `LightBitMapping` helper (or extend existing) for `ushort` light pack/unpack                                                                                                                                                   |
| `Jobs/NeighborhoodLightingJob.cs`                                | Triple-channel blocklight BFS; per-channel darkness removal (`ProcessDarknessChannel` helper); `SetSunlight`/`SetBlocklightRGB`/`GetLightData`; expanded `LightModification` and `LightRemovalNode`; write-through cache expansion |
| `Jobs/Data/LightingJobData.cs`                                   | Pass `NativeArray<ushort>` light arrays (center + 8 neighbors) into job                                                                                                                                                            |
| `WorldJobManager.cs`                                             | Pass `ushort` light arrays to/from lighting and meshing jobs; handle RGB `LightModification` cross-chunk mods                                                                                                                      |
| `World.cs`                                                       | Add `SkyLightColor` shader uniform + `Gradient` for time-of-day color                                                                                                                                                              |
| `Jobs/MeshGenerationJob.cs`                                      | `SampleNeighborLight` reads RGB from `ushort` array; `SampleCorner`/`BuildFlatLightData` encode `(sun, blockR, blockG, blockB)`                                                                                                    |
| `Chunk.cs`                                                       | Thread `ushort` light arrays through `ApplyMeshData` pipeline                                                                                                                                                                      |
| `Shaders/Includes/VoxelLighting.hlsl`                            | Update `ApplyVoxelLightingRGB` signature: `(sun scalar, block RGB, sky color, ...)`                                                                                                                                                |
| `Shaders/StandardBlockShader.shader`                             | Add `SkyLightColor` uniform; update frag call to `ApplyVoxelLightingRGB(col, lightData.r, lightData.gba, ...)`                                                                                                                     |
| `Shaders/TransparentBlockShader.shader`                          | Same as StandardBlockShader                                                                                                                                                                                                        |
| `Shaders/Includes/LiquidCore.hlsl`                               | Update scalar fallback: `max(r, max(g, max(b, a)))`                                                                                                                                                                                |
| `Shaders/UberLiquidShader.shader`                                | Add `SkyLightColor` uniform                                                                                                                                                                                                        |
| `Serialization/ChunkSerializer.cs`                               | Expand `WriteLightQueue`/`ReadLightQueue` for RGB entries; add `InitLightDataFromPacked` to reconstruct ushort light array from uint packed data on every chunk load                                                               |
| `Serialization/Migration/Steps/MigrationV7ToV8RGBLightQueues.cs` | AOT migration step (world v7→v8, chunk format v4→v5) for light queue format change                                                                                                                                                 |
| `Benchmarks/LightingJobBenchmark.cs`                             | Add RGB-specific scenarios; fix existing broken scenarios                                                                                                                                                                          |
| `Editor/BlockEditor/` (inspector)                                | Add emission color picker + synced RGB sliders to block editor UI                                                                                                                                                                  |
| `Editor/WorldTools/Libraries/EditorChunkPipelineRunner.cs`       | Thread `ushort` light arrays through `ScheduleLighting` and `ScheduleMeshing` for the 3D chunk preview pipeline                                                                                                                    |
| `Editor/WorldTools/ChunkPreview3DWindow.Pipeline.cs`             | Store/pass `_chunkLightMaps` dictionary; cross-chunk mod MAX guard; full-bright stamp for disabled lighting                                                                                                                        |

**Files NOT changed from Phase 1 state:** `SectionRenderer.cs` (vertex layout unchanged), `VoxelCommon.hlsl` (vertex structs unchanged), `Helpers/VoxelMeshHelper.cs` (already works with `Color32`) — the rendering pipeline is fully prepared by Phase 1.

### 3.8.1 Post-Cleanup Step (After Phase 2 Stabilizes)

A separate refactor after Phase 2 is stable and verified:

1. Migrate all scalar light readers (`GetSunLight()`, `GetBlockLight()` on the `uint`) to read from the `ushort` light array.
2. Remove the dual-write from the BFS (stop writing to legacy `uint` light bits).
3. Free the 8 bits (sunlight 4 + blocklight 4) in the `uint` for future metadata expansion.
4. Update `PackVoxelData` to no longer accept sunlight/blocklight parameters.

This is tracked as a separate work item, not part of the Phase 2 implementation.

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

| Step         | Change                                                                   | Risk                                                            | Rollback                                              |
|--------------|--------------------------------------------------------------------------|-----------------------------------------------------------------|-------------------------------------------------------|
| **Phase 1a** | Interleave `TexCoord1` (UNorm8x4) with Normal in stream 3 + all shaders  | Low — additive change, revert stream 3 to Normal-only to undo   | Revert stream 3 layout + remove `NormalLightVertex`   |
| **Phase 1b** | Implement corner averaging + diagonal occlusion + LUT in mesh job        | Medium — affects every visible face, ~24 extra reads per face   | Set `smoothLighting = false` (flat per-face fallback) |
| **Phase 1c** | Add anisotropy fix (quad diagonal flip)                                  | Low — only changes triangle winding order when needed           | Revert to default winding                             |
| **Phase 1d** | Add smooth lighting settings toggle                                      | Low — UI-only                                                   | Remove setting field                                  |
| **Phase 1e** | Fluid smooth lighting via precomputed `FluidCornerLights` struct         | Low — isolated to fluid path, flat fallback via settings toggle | Set `smoothLighting = false` (flat per-face fallback) |
| **Phase 2a** | Baseline benchmarks + fix broken benchmark scenarios                     | Low — no code changes, establishes performance reference        | N/A                                                   |
| **Phase 2b** | Add `ushort` light array + `LightBitMapping` helpers + pool reset        | Medium — new allocation per section, pool reset rules apply     | Remove array, fall back to scalar-only path           |
| **Phase 2c** | Block emission color (BlockType + BlockTypeJobData + editor UI)          | Low — additive data fields, existing blocks default to white    | Remove color fields, fall back to scalar emission     |
| **Phase 2d** | Triple-channel blocklight BFS + per-channel darkness removal             | **High** — core lighting engine change                          | Feature flag to use scalar BFS                        |
| **Phase 2e** | Light queue serialization upgrade + AOT migration step                   | Medium — serialization format change                            | Revert migration, fall back to scalar queue format    |
| **Phase 2f** | Mesh job: `SampleCorner`/`BuildFlatLightData` encode `(sun, bR, bG, bB)` | Low — just reading new data from the `ushort` array             | Write Phase 1 encoding (sun, sun, sun, block)         |
| **Phase 2g** | Shaders: `SkyLightColor` uniform + `ApplyVoxelLightingRGB` update        | Low — shader-only, no BFS change                                | Set `SkyLightColor = white`, revert to Phase 1 call   |
| **Post**     | Deprecate legacy `uint` light bits (separate refactor)                   | Low — mechanical migration of readers                           | Re-enable dual-write                                  |

### 4.2 Key Risks

- **Phase 1 performance:** ~24 memory accesses per face (12 voxel reads + 12 block type lookups) in Burst. Should be profiled against the baseline. The precomputed LUT and diagonal occlusion early-out reduce constant factors. The settings toggle provides a user-facing fallback if performance impact is unacceptable on target hardware.

- **Phase 2 BFS complexity:** Three-channel blocklight propagation roughly triples the comparison work per neighbor in the BFS inner loop (3 channel comparisons instead of 1). However, the memory access pattern is similar — the main cost is the voxel read, not the comparison. The `ushort` light array (16 bits, cache-friendly) mitigates the additional read cost.

- **Per-channel darkness removal correctness:** Removing a red source `(12, 0, 0)` must clear only the red channel, not blue light `(0, 0, 15)` at the same voxel from a different source. The removal BFS tracks per-channel old values in `LightRemovalNode`. Edge cases to test: 3+ overlapping colored sources, removal at chunk boundary, rapid place/break cycling of colored sources.

- **Write-through cache with two arrays:** The current `NativeHashMap<long, uint>` must expand to cache both voxel and light data. The `ulong` packing approach `(uint voxelData << 16) | lightData` keeps this to a single hash map. Incorrect cache handling would cause the BFS to read stale light values from ReadOnly neighbor arrays, producing ghost light after source removal.

- **Phase 2 cross-chunk mods:** The `LightModification` struct grows by 3 bytes (RGB channels). Memory impact is proportional to the number of cross-chunk border voxels with blocklight — typically small but should be monitored.

- **Pool reset for light array:** The `ushort[]` light array in `ChunkSection` must be cleared in `ChunkSection.Reset()` per pool-reset-safety rules. A recycled section with stale light data from its previous lifecycle would produce visible light artifacts until the BFS overwrites it.

- **Serialization migration:** The light queue format change requires an AOT migration step. Old worlds with pending scalar blocklight operations have their queue entries expanded to white `(L, L, L)`. This is safe because all existing light sources emit white light.

- **UNorm8 quantization:** 256 levels per channel for 16 discrete light values is more than sufficient. The corner averaging with rounded integer arithmetic — `(byte)((channelSum * 17 + 2) / 4)` — introduces at most 0.2% error, which is imperceptible.

### 4.3 Benchmarking Plan

#### 4.3.1 Pre-Phase 2 Baseline (Step 2a)

Establish baseline performance metrics using the existing `LightingJobBenchmark.cs` tool (after fixing broken scenarios):

1. Profile `NeighborhoodLightingJob` duration across all existing scenarios (`SunlightVerticalFlat`, `SunlightComplexCaves`, `BlocklightSimple`, `BlocklightStressTest`, `SunlightRemovalCovered`, `BlocklightRemovalSingle`).
2. Profile `MeshGenerationJob` duration with smooth lighting enabled vs disabled.
3. Record GPU frame time and vertex buffer memory usage.

#### 4.3.2 Phase 2 RGB BFS Benchmarks

After implementing the triple-channel blocklight BFS (Step 2d):

4. Re-run all existing lighting benchmark scenarios with the RGB BFS. Compare against baseline.
5. **New scenario: `BlocklightRGBOverlap`** — Multiple colored light sources (red, green, blue, white) in a confined space with overlapping propagation zones. Measures per-channel max and 3× comparison overhead.
6. **New scenario: `BlocklightRGBRemoval`** — Place and remove colored light sources in sequence. Measures per-channel darkness removal correctness and performance.
7. Target: < 30% increase in `NeighborhoodLightingJob` duration for blocklight scenarios (the 3× comparison work is offset by the `ushort` array's better cache density vs the current interleaved `uint`).
8. Measure `ushort` light array memory overhead (should be ~64 KB per chunk with lazy section allocation).

### 4.4 Testing Strategy

#### 4.4.1 Phase 1 Tests (Completed)

- **Visual regression:** Capture screenshots of known scenes (cave with torches, surface day/night, underwater, chunk borders) before and after Phase 1. Smooth lighting should only improve gradients — never change the average brightness of a face.
- **Boundary correctness:** Place torches at section boundaries and chunk boundaries. Verify no light seams or discontinuities at the 16-block section edges and the chunk edges (all 8 diagonal neighbors are sampled correctly).
- **Diagonal occlusion:** Build an L-shaped wall with a torch behind it. Verify that the inner corner is dark (AO effect) and no light leaks through the diagonal.
- **Anisotropy:** Build a 2×2 checkerboard of torch/dark blocks. Verify no visual "pinwheel" artifacts — the quad diagonal flip should produce a symmetric diamond pattern.
- **Settings toggle:** Toggle smooth lighting off and verify the rendering matches the pre-Phase-1 flat lighting exactly.

#### 4.4.2 Phase 2 Tests

- **White light backwards compatibility:** Existing torch blocks (scalar emission 15, no color set → white default) must produce identical visual output to Phase 1. Emission `(15, 15, 15)` through the RGB BFS should match the scalar BFS result exactly.
- **Color mixing:** Place red `(15, 0, 0)` and blue `(0, 0, 15)` light sources 5 blocks apart. Verify: red-dominant near red source, magenta overlap zone (per-channel max), blue-dominant near blue source. No order-dependent flickering.
- **Per-channel darkness removal:** Place a red torch and blue lantern near the same block. Remove the red torch. Verify: red light disappears, blue light remains at full intensity. The voxel should show only blue, not purple or dark.
- **3+ source overlap:** Place red, green, and blue sources around a single voxel. Verify the center receives near-white `(~15, ~15, ~15)` light. Remove one source and verify only that channel dims.
- **Chunk boundary colored light:** Place a colored light source at a chunk boundary. Verify the color propagates correctly across the boundary via `LightModification` RGB fields. No color desync at chunk edges.
- **Sky tinting:** Set time to night, verify blue moonlight tint outdoors. Enter a torch-lit room, verify warm indoor light is unaffected by sky color. Stand in a doorway, verify smooth gradient between blue outdoor and warm indoor.
- **Blood moon event:** Override `SkyLightColor = (1.0, 0.2, 0.15)`. Verify all outdoor surfaces take on a deep red hue. Indoor torch-lit areas remain unaffected.
- **Smooth lighting + RGB:** Verify that corner averaging works correctly with colored light. A smooth lighting gradient from a red torch should produce smooth red falloff, not banded color shifts.
- **enableLighting = false:** Verify the engine functions correctly with lighting disabled. The `ushort` light array should be zeroed or max-filled as appropriate. No crashes from null/unallocated arrays.
- **Save/load round-trip:** Place colored light sources, save the world, reload. Verify pending RGB light queue entries are correctly deserialized and the BFS completes propagation after load.
- **World migration:** Load a pre-Phase-2 world save. Verify the AOT migration correctly expands scalar queue entries to white RGB. No light artifacts or corruption.
- **Performance:** Compare profiler snapshots against baseline. Document results in this section after benchmarking.
