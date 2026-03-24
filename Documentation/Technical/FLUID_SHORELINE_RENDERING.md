# Fluid Shoreline Rendering

This document describes the algorithm and techniques used to render the foam shoreline effect where fluid surfaces meet solid walls. The system spans the C# mesher (`VoxelMeshHelper.cs`) and the HLSL shader (`UberLiquidShader.shader`).

---

## Overview

The shoreline effect has two visual components:

1. **Shore Gradient** — a foam/crust band that appears along wall edges, controlled by the `Shore Width` material property.
2. **Shore Push** — a slow displacement animation that pushes the flow pattern away from walls, controlled by the `Shore Push Speed` property.

These are encoded into separate vertex channels and combined per-pixel in the fragment shader.

---

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│ C# Mesher (VoxelMeshHelper.GenerateFluidMeshData)           │
│                                                             │
│  ┌────────────────────┐  ┌──────────────────────────────┐   │
│  │ 8-Neighbor Wall    │  │ Symmetric Corner Shore Push  │   │
│  │ Mask (per-voxel)   │  │ Directions (per-vertex)      │   │
│  │ → color.g          │  │ → uv.zw                      │   │
│  └────────────────────┘  └──────────────────────────────┘   │
└──────────────┬──────────────────────────┬───────────────────┘
               │                          │
               ▼                          ▼
┌─────────────────────────────────────────────────────────────┐
│ HLSL Fragment Shader (GetShoreData)                         │
│                                                             │
│  Decode wall flags    GPU-interpolated push direction       │
│       │                          │                          │
│       ▼                          ▼                          │
│  Per-pixel min-dist   Normalize + scale by gradient         │
│  via frac(worldPos)              │                          │
│       │                          │                          │
│       ▼                          ▼                          │
│  shore_gradient ──────────► shore_push                      │
│       │                          │                          │
│       ▼                          ▼                          │
│  Foam color blend        Flow UV displacement               │
└─────────────────────────────────────────────────────────────┘
```

---

## Channel Layout (Top Face)

| Channel   | Data                          | Scope               | GPU Interpolation           |
|-----------|-------------------------------|---------------------|-----------------------------|
| `color.r` | Liquid type (0=lava, 1=water) | Per-vertex          | Interpolated                |
| `color.g` | Packed 8-bit wall mask        | Per-quad (constant) | No (all vertices identical) |
| `color.b` | Shadow multiplier             | Per-vertex          | Interpolated                |
| `color.a` | Light level                   | Per-vertex          | Interpolated                |
| `uv.xy`   | Local flow vector             | Per-vertex          | Interpolated                |
| `uv.zw`   | Shore push direction          | Per-vertex          | Interpolated                |

Side and bottom faces set `color.g = 0.0` (no shore effect).

---

## Part 1: 8-Neighbor Wall Mask (Shore Gradient)

### Problem: Why Not Per-Vertex Scalar?

The obvious approach — store a scalar shore distance (0 or 1) at each vertex and let the GPU interpolate — fails for two reasons:

1. **Triangle-split artifact**: The GPU splits each quad into 2 triangles. Values at corners off the shared diagonal exist in only one triangle, producing visible straight-line seams instead of smooth gradients.
2. **Cardinal vs corner asymmetry**: For L-shaped walls (e.g., south + west), 3 of 4 corners are hot. Bilinear interpolation produces hyperbolic iso-contours `(d = 1 - u·v)` instead of constant-width bands.

### Solution: Per-Voxel Wall Flags + Per-Pixel Distance

Instead of interpolating a scalar, we:

1. **Detect walls per-voxel** (not per-corner):
    - 4 cardinal neighbors (N, S, E, W) are checked for solidity
    - 4 diagonal neighbors (NE, NW, SE, SW) are checked only if neither adjacent cardinal wall is present (to avoid redundancy)

2. **Bit-pack into `color.g`**:
   ```
   mask = wallN*1 + wallS*2 + wallE*4 + wallW*8
        + diagNE*16 + diagNW*32 + diagSE*64 + diagSW*128
   color.g = mask / 255.0
   ```
   This value is **identical at all 4 vertices** of the quad, so the GPU passes it through unchanged.

3. **Decode and compute distance per-pixel** in the fragment shader:
   ```hlsl
   float packed = round(packedMask * 255.0);
   // Decode each bit...
   float wallN = fmod(packed, 2.0);
   // etc.

   float2 t = frac(worldPos.xz);  // Sub-voxel position
   float minDist = 1.0;
   if (wallN > 0.5) minDist = min(minDist, 1.0 - t.y);
   if (wallS > 0.5) minDist = min(minDist, t.y);
   if (wallE > 0.5) minDist = min(minDist, 1.0 - t.x);
   if (wallW > 0.5) minDist = min(minDist, t.x);
   // Diagonal corners use L∞ distance for sharp shapes:
   if (diagNE > 0.5) minDist = min(minDist, max(1.0 - t.x, 1.0 - t.y));
   // etc.
   ```

4. **Convert to gradient**:
   ```hlsl
   shore_gradient = smoothstep(0, 1, saturate(1.0 - minDist / shoreWidth));
   ```

### Why This Is Seam-Free

At the boundary between two adjacent fluid voxels (e.g., at `worldX = 6.0`):

- Voxel A's right edge: `frac(6.0) → 1.0`, east wall distance = `1.0 - 1.0 = 0.0`
- Voxel B's left edge: `frac(6.0) → 0.0`, west wall distance = `0.0`

If A has an east wall, then B necessarily has a west wall (same solid block). Both pixels compute `minDist = 0.0` → identical gradient. No seam.

### Why Diagonal Corners Use L∞

Using Euclidean distance (`sqrt(dx² + dy²)`) for diagonal corners produces a circular falloff — the shore band would be wider at 45° than at 0°/90°. L∞ distance (`max(dx, dy)`) produces a square falloff that matches the sharp L-shaped corners of the voxel grid and maintains
constant `shoreWidth` in all directions.

---

## Part 2: Shore Push Direction

### Computation

Push direction is computed per-vertex in C# via `CalculateSymmetricCornerShorePush`. This uses the same 4-block symmetric neighborhood as flow vectors:

```
b01 ─── b11       Each corner of the fluid quad is the shared
 │       │         vertex of a 2×2 neighborhood of blocks.
 │       │
b00 ─── b10
```

The push vector points away from solid walls:

- **Cardinal walls** (two adjacent blocks solid): push perpendicular to the wall.
- **Diagonal corners** (single isolated solid): push diagonally away.
- The vector is **normalized** — it only encodes direction, not magnitude.

### Why Symmetric?

Two adjacent fluid quads share corner vertices. If each quad computed push independently, the values at shared vertices would differ → visible seam. The symmetric 4-block neighborhood guarantees identical values at shared vertices because both quads reference the same blocks.

### Shader Usage

The GPU bilinearly interpolates the per-vertex push vectors. The fragment shader normalizes the interpolated result and scales it by `shore_gradient`:

```hlsl
float2 push_dir = normalize(shorePush_interpolated);
shore_push = push_dir * shore_gradient;
```

This is added to the flow UV offset to create the push-away animation:

```hlsl
float dynamicPush = _ShorePushSpeed + rawMacroSpeed;
float2 totalFlow = localFlowVector + shore_push * dynamicPush;
```

The push speed is proportional to flow velocity: calm water pulsates slowly, rapid flow pushes faster — mimicking real fluid behavior.

---

## Part 3: Approaches Tried and Why They Failed

| #     | Approach                                                       | Issue                                                                                                                                                                       |
|-------|----------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| 1     | 8-bit shore mask in `color.g`, per-pixel gradient via `frac()` | Discontinuous mask → hard seams at voxel boundaries                                                                                                                         |
| 2     | Binary `shoreIntensity` + bilinear GPU interpolation           | Diagonal wedge artifacts from triangle split                                                                                                                                |
| 3     | Normalized push + per-pixel gradient                           | Reintroduced seams from approach 1                                                                                                                                          |
| 4     | Unnormalized push + `length(interpolated push)`                | Vector cancellation at corners → invisible shore for diagonal walls                                                                                                         |
| 5     | Per-vertex scalar `shoreDistance` + GPU interpolation          | Triangle-split artifact: straight-line seams for off-diagonal corners                                                                                                       |
| 6     | Adaptive triangle diagonal flip                                | Only fixed isolated corners; L-shaped walls (3-hot, 1-clear) still broken                                                                                                   |
| 7     | 4-corner bitmask + per-pixel bilinear reconstruction           | Correct curves but: (a) side/bottom faces had `color.g=1.0` → decoded as "all walls"; (b) narrow channels had gradient=1.0 everywhere; (c) rounded instead of sharp corners |
| **8** | **8-neighbor wall flags + per-pixel min-distance**             | **✓ Final solution — uniform width, sharp corners, no seams, no bleeding**                                                                                                  |

---

## Debug Tools

`World.DebugLogFluidSurfaceMath` (triggered via raycast debug key) outputs:

- **Wall Mask**: hex value + decoded N/S/E/W/diagonal flags + packed `color.g` value
- **Per-corner push directions**: the 4 normalized push vectors at BL/TL/BR/TR
- **Seam check**: compares the NE push of this voxel with the NW push of the east neighbor

---

## Key Files

| File                                        | Responsibility                                                           |
|---------------------------------------------|--------------------------------------------------------------------------|
| `Assets/Scripts/Helpers/VoxelMeshHelper.cs` | `GenerateFluidMeshData` — wall mask encoding, push direction computation |
| `Assets/Shaders/UberLiquidShader.shader`    | `GetShoreData` — per-pixel wall distance, gradient, and push             |
| `Assets/Scripts/World.cs`                   | `DebugLogFluidSurfaceMath` — diagnostic logging                          |

---

## Material Properties

| Property                               | Type  | Effect                                                      |
|----------------------------------------|-------|-------------------------------------------------------------|
| `_WaterShoreWidth` / `_LavaShoreWidth` | Float | Width of the foam band (in voxel units, 0–1)                |
| `_WaterShoreFoam` / `_LavaShoreCrust`  | Float | Intensity of the foam/crust overlay                         |
| `_ShorePushSpeed`                      | Float | Baseline speed of the push-away animation                   |
| `_StreamEffect`                        | Float | Intensity of flow-driven stream foam (independent of shore) |
