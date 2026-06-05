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

/// Calculates the raw shade value from lighting parameters without applying it.
/// Use this when the shader applies shade in a custom way (e.g., liquid shaders).
///
/// @param lightLevel   Per-vertex light level (vertex color alpha, 0..1).
/// @param globalLight  The world's global light level (day/night cycle, 0..1).
/// @param minLight     Minimum allowed light level (VoxelData.MinLightLevel = 0.15).
/// @param maxLight     Maximum allowed light level (VoxelData.MaxLightLevel = 1.0).
/// @return             The shade factor (0 = fully lit, 1 = fully dark).
float CalculateVoxelShade(float lightLevel,
                          float globalLight, float minLight, float maxLight)
{
    float shade = (maxLight - minLight) * globalLight + minLight;
    shade *= lightLevel;
    return clamp(1.0 - shade, minLight, maxLight);
}

// --- Lighting Constants ---
static const float MAX_SHADOW_DARKNESS = 0.10;
static const float GAMMA_CORRECTION_CURVE = 2.2;

/// Emulates the legacy Gamma-space block shadow falloff in Linear color space.
/// Use this to multiply against your final color instead of a raw lerp.
///
/// @param shade    The voxel shade value (0 = fully lit, 1 = fully dark).
/// @return         A linear-space multiplier that maps correctly back to monitor gamma.
float CalculateLinearVoxelShadow(float shade)
{
    float shadowMultiplier = lerp(1.0, MAX_SHADOW_DARKNESS, shade);
    // max(0.0, ...) is used strictly to silence the DirectX HLSL static analysis compiler warning
    // about pow(f, e) not working for fractional exponents on negative bases.
    return pow(max(0.0, shadowMultiplier), GAMMA_CORRECTION_CURVE);
}

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
    float shade = CalculateVoxelShade(lightLevel, globalLight, minLight, maxLight);
    return color * CalculateLinearVoxelShadow(shade);
}

/// Applies the shared shade curve to a single light channel, returning a
/// linear-space brightness multiplier (0 = dark, 1 = full brightness).
///
/// @param lightLevel   Per-vertex light level (0..1).
/// @param globalLight  Day/night cycle (0..1) — pass 1.0 for blocklight.
/// @param minLight     Minimum ambient (VoxelData.MinLightLevel = 0.15).
/// @param maxLight     Maximum light (VoxelData.MaxLightLevel = 1.0).
float VoxelLightToShadow(float lightLevel,
                         float globalLight, float minLight, float maxLight)
{
    float shade = CalculateVoxelShade(lightLevel, globalLight, minLight, maxLight);
    return CalculateLinearVoxelShadow(shade);
}

/// Applies the voxel lighting model with separate sunlight and blocklight channels.
/// Both channels share the same gamma-corrected shade curve. Sunlight is modulated
/// by the day/night cycle; blocklight is always at full intensity.
///
/// @param color            Base texture color (RGB).
/// @param sunRGB           Per-vertex sunlight as RGB (Phase 1: r=luminance, gb=0).
/// @param blockLuminance   Per-vertex blocklight luminance (0..1).
/// @param globalLight      Day/night cycle (0..1) — modulates sunlight only.
/// @param minLight         Minimum ambient (0.15).
/// @param maxLight         Maximum light (1.0).
half3 ApplyVoxelLightingRGB(half3 color,
                            half3 sunRGB, float blockLuminance,
                            float globalLight, float minLight, float maxLight)
{
    // Sunlight: modulated by day/night cycle
    float sunLuminance = max(sunRGB.r, max(sunRGB.g, sunRGB.b));
    float sunShadow = VoxelLightToShadow(sunLuminance, globalLight, minLight, maxLight);
    half3 sunTint = sunLuminance > 0 ? sunRGB / sunLuminance : half3(1, 1, 1);
    half3 sunContrib = color * sunShadow * sunTint;

    // Blocklight: NOT modulated by day/night (globalLight = 1.0)
    float blockShadow = VoxelLightToShadow(blockLuminance, 1.0, minLight, maxLight);
    half3 blockContrib = color * blockShadow;

    return max(sunContrib, blockContrib);
}

#endif // VOXEL_LIGHTING_INCLUDED
