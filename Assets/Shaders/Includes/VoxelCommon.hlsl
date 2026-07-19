#ifndef VOXEL_COMMON_INCLUDED
#define VOXEL_COMMON_INCLUDED

// =============================================================================
// VoxelCommon.hlsl — Shared vertex structures and transform logic for all
// standard/transparent block shaders (game + editor preview).
// =============================================================================

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

// --- Texture Declarations ---
TEXTURE2D(_MainTex);
SAMPLER(sampler_MainTex);

// --- Vertex Input ---
struct VoxelAppdata
{
    float4 vertex : POSITION;
    float4 uv : TEXCOORD0; // xy = atlas UV; zw = foliage sway weight/phase (FL-1; zero on non-flora verts)
    half4 color : COLOR;
    half4 lightData : TEXCOORD1; // UNorm8: (skyLight, blocklightR, blocklightG, blocklightB)
};

// --- Fragment Input ---
struct VoxelV2F
{
    float4 vertex : SV_POSITION;
    float2 uv : TEXCOORD0;
    half4 color : COLOR;
    half4 lightData : TEXCOORD1;
};

// --- Foliage sway globals (FL-1) ---
// Set per frame by FoliageSway.cs; the zero defaults freeze all foliage (edit mode, sway disabled).
float2 FoliageWindVector; // XZ wind direction, pre-scaled by wind strength (unitless multiplier)
float4 FoliageSwayParams; // x = amplitude (blocks), y = frequency (rad/s), z = gust fraction, w = gust frequency (rad/s)

// --- Foliage Sway (FL-1) ---
/// Displaces a vertex in object space by the global wind. swayData.x is the mesh-baked sway
/// weight (0 = rigid — roots and every non-flora vert), swayData.y the per-voxel phase in [0, 1]
/// that de-synchronizes neighboring tufts. Chunk transforms are translation-only, so the
/// object-space XZ offset equals a render-space offset.
float3 ApplyFoliageSway(float3 positionOS, float2 swayData)
{
    float phase = swayData.y * TWO_PI;
    float wave = sin(FoliageSwayParams.y * _Time.y + phase);
    float gust = sin(FoliageSwayParams.w * _Time.y + phase * 1.7) * FoliageSwayParams.z;
    positionOS.xz += FoliageWindVector * ((wave + gust) * FoliageSwayParams.x * swayData.x);
    return positionOS;
}

// --- Vertex Function ---
VoxelV2F VoxelVert(VoxelAppdata v)
{
    VoxelV2F o;
    o.vertex = TransformObjectToHClip(v.vertex.xyz);
    o.uv = v.uv.xy;
    o.color = v.color;
    o.lightData = v.lightData;
    return o;
}

// --- Texture Sampling ---
/// Samples the block texture atlas at the given UV coordinates.
half4 SampleBlockTexture(float2 uv)
{
    return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
}

#endif // VOXEL_COMMON_INCLUDED
