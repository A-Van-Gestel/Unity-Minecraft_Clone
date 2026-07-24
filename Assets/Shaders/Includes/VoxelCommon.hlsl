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

// --- Foliage sway globals (FL-1/FL-2) ---
// Set per frame by FoliageSway.cs; the zero defaults freeze all foliage (edit mode, sway disabled).
float2 FoliageWindVector; // XZ wind direction, pre-scaled by wind strength (unitless multiplier)
float4 FoliageSwayParams; // x = amplitude (blocks), y = frequency (rad/s), z = gust fraction, w = gust frequency (rad/s)
float4 FoliageSwayParams2; // x = spatial frequency (rad/block along wind), y = per-voxel phase jitter fraction, z = vertical bob fraction, w = gust spatial multiplier

// Unity→voxel-space shift, re-pushed on every floating-origin re-anchor (same global LiquidCore's
// LiquidNoisePos uses). Zero (identity) in edit mode and before World initializes.
float3 _WorldOriginOffset;

// --- Foliage Sway (FL-1/FL-2) ---
/// Displaces a vertex in object space by the global wind. swayData.x is the mesh-baked sway
/// weight (0 = rigid — roots and every non-flora vert; FL-2 cubes carry their authored strength),
/// swayData.y the baked per-voxel value used as a SMALL phase jitter. The dominant phase term is
/// spatial: a wave traveling along the wind through voxel-space XZ, so neighboring foliage moves
/// coherently and gusts visibly ripple across canopies and meadows instead of each voxel
/// oscillating independently. Voxel-space position (worldPos + _WorldOriginOffset) keeps the wave
/// pattern invariant across floating-origin re-anchors (WS-3). Chunk transforms are
/// translation-only, so the object-space offset equals a render-space offset.
float3 ApplyFoliageSway(float3 positionOS, float2 swayData)
{
    float weight = swayData.x;
    float3 positionWS = TransformObjectToWorld(positionOS);
    float2 voxelXZ = positionWS.xz + _WorldOriginOffset.xz;

    // Distance along the wind direction; FoliageWindVector is ~unit-length at reference wind
    // strength, so FoliageSwayParams2.x is effectively rad/block. Zero wind → zero spatial term
    // AND zero displacement below, so no normalize (and no NaN risk) is needed.
    float alongWind = dot(voxelXZ, FoliageWindVector) * FoliageSwayParams2.x;
    float jitter = swayData.y * TWO_PI * FoliageSwayParams2.y;

    // Primary traveling wave + a broader, slower gust wave riding the same wind line.
    float wave = sin(FoliageSwayParams.y * _Time.y - alongWind + jitter);
    float gust = sin(FoliageSwayParams.w * _Time.y - alongWind * FoliageSwayParams2.w + jitter) * FoliageSwayParams.z;
    float sway = (wave + gust) * FoliageSwayParams.x * weight;

    positionOS.xz += FoliageWindVector * sway;
    // Slight downward settle at the sway extremes — reads as bending, not sliding.
    positionOS.y -= wave * wave * FoliageSwayParams2.z * FoliageSwayParams.x * weight;
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
