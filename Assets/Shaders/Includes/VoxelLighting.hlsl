#ifndef VOXEL_LIGHTING_INCLUDED
#define VOXEL_LIGHTING_INCLUDED

// =============================================================================
// VoxelLighting.hlsl — Shared voxel lighting calculation used by all block
// shaders (game + editor preview).
//
// The function takes light parameters explicitly (no global state dependency)
// so game shaders can pass in runtime globals from World.cs while editor
// preview shaders pass in hardcoded daylight defaults.
// =============================================================================

/// Applies the engine's voxel lighting model to a base color.
///
/// @param color        The base texture color (RGB).
/// @param lightLevel   Per-vertex light level (vertex color alpha, 0..1).
/// @param globalLight  The world's global light level (day/night cycle, 0..1).
/// @param minLight     Minimum allowed light level (VoxelData.MinLightLevel = 0.15).
/// @param maxLight     Maximum allowed light level (VoxelData.MaxLightLevel = 1.0).
/// @return             The lit color.
half3 ApplyVoxelLighting(half3 color, float lightLevel,
                         float globalLight, float minLight, float maxLight)
{
    // Calculate block shade level
    // (maxLight - minLight) = total range available
    // * globalLight = use a percentage of that range
    // + minLight = re-add the minimum to calculate final shade
    float shade = (maxLight - minLight) * globalLight + minLight;

    // Apply per-vertex block light level onto shade
    shade *= lightLevel;

    // 1 = absolute darkest, so reverse so 1 = absolute lightest
    shade = clamp(1.0 - shade, minLight, maxLight);

    // Darken block based on calculated shade
    return lerp(color, color * 0.10, shade);
}

#endif // VOXEL_LIGHTING_INCLUDED
