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
/// NOTE: this scalar entry point still MULTIPLIES by globalLight — the pre-RF-1 model. It has no
/// callers; the shipped path is ApplyVoxelLightingRGB, which subtracts (see ApplySkyDarken). Any new
/// surface using this would darken out of step with the surrounding terrain.
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

/// Subtracts the day/night darkening from a stored sky-exposure value (RF-1 §10).
///
/// The stored channel is time-invariant sky EXPOSURE, so time of day is applied here, at read time.
/// Subtracting rather than multiplying is what keeps the render honest: a voxel that looks like
/// level 4 IS effective level 4, the same number `LightBitMapping.GetEffectiveLight` hands gameplay.
/// A multiply would scale every level toward zero and agree with that query at only two points.
///
/// @param skyExposure  Per-vertex stored sky light, normalized (0..1 = level 0..15).
/// @param globalLight  Normalized brightness of fully-exposed sky (WorldTimeManager.GlobalLightLevel,
///                     = 1 - skyDarken/15). Its complement is the darkening to subtract.
/// @return             The sky light actually reaching this vertex, normalized.
float ApplySkyDarken(float skyExposure, float globalLight)
{
    return max(skyExposure - (1.0 - globalLight), 0.0);
}

/// Applies the voxel lighting model with separate sunlight and RGB blocklight channels.
/// Sunlight is a scalar tinted by SkyLightColor (time-of-day gradient).
/// Blocklight is per-channel RGB, each going through the same shade curve independently.
///
/// @param color            Base texture color (RGB).
/// @param sunLuminance     Per-vertex sunlight scalar (0..1) — stored sky EXPOSURE, not brightness.
/// @param blockRGB         Per-vertex blocklight RGB (0..1 per channel).
/// @param skyColor         Sky light tint color (from World.cs gradient, white at noon).
/// @param globalLight      Day/night cycle (0..1) — subtracted from the sky channel only.
/// @param minLight         Minimum ambient (0.15).
/// @param maxLight         Maximum light (1.0).
half3 ApplyVoxelLightingRGB(half3 color,
                            float sunLuminance, half3 blockRGB,
                            half3 skyColor,
                            float globalLight, float minLight, float maxLight)
{
    // Sunlight: exposure minus the time-of-day darkening, then the shade curve at full intensity
    // (the curve's own globalLight term stays 1.0 — the day/night term has already been applied).
    float litSky = ApplySkyDarken(sunLuminance, globalLight);
    float sunShadow = VoxelLightToShadow(litSky, 1.0, minLight, maxLight);
    half3 sunContrib = color * sunShadow * skyColor;

    // Blocklight: RGB channels × same shade curve, always full intensity
    float blockR_shadow = VoxelLightToShadow(blockRGB.r, 1.0, minLight, maxLight);
    float blockG_shadow = VoxelLightToShadow(blockRGB.g, 1.0, minLight, maxLight);
    float blockB_shadow = VoxelLightToShadow(blockRGB.b, 1.0, minLight, maxLight);
    half3 blockContrib = color * half3(blockR_shadow, blockG_shadow, blockB_shadow);

    return max(sunContrib, blockContrib);
}

// --- RF-3: HDR emissive ---

/// Multiplier turning a vertex's stored emissive strength into HDR headroom above 1.0, so the bloom
/// threshold has something to catch. Set per frame by GraphicsSettingsController; the 0 default keeps
/// every shader that includes this file bit-identical until the post stack is actually enabled (and
/// keeps editor previews, which never set it, unchanged).
float _EmissiveBoost;

/// Adds a block's own emission on top of its lit color.
///
/// Emitters are lit by their own blocklight and so already sit near 1.0; this pushes them past it,
/// which is the entire point — below 1.0 nothing blooms. Deliberately independent of the day/night
/// cycle: a lamp emits the same at noon as at midnight, and the bloom threshold decides what reads
/// as glowing.
///
/// @param litColor  The surface color after voxel lighting has been applied.
/// @param strength  Per-vertex emissive strength (vertex color alpha, 0..1 = emission level 0..15).
/// @return          The lit color plus its emissive contribution, potentially above 1.0.
half3 ApplyVoxelEmissive(half3 litColor, half strength)
{
    return litColor + litColor * strength * _EmissiveBoost;
}

#endif // VOXEL_LIGHTING_INCLUDED
