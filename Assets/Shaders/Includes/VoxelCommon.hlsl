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
    float2 uv : TEXCOORD0;
    half4 color : COLOR;
};

// --- Fragment Input ---
struct VoxelV2F
{
    float4 vertex : SV_POSITION;
    float2 uv : TEXCOORD0;
    half4 color : COLOR;
};

// --- Vertex Function ---
VoxelV2F VoxelVert(VoxelAppdata v)
{
    VoxelV2F o;
    o.vertex = TransformObjectToHClip(v.vertex.xyz);
    o.uv = v.uv;
    o.color = v.color;
    return o;
}

// --- Texture Sampling ---
/// Samples the block texture atlas at the given UV coordinates.
half4 SampleBlockTexture(float2 uv)
{
    return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
}

#endif // VOXEL_COMMON_INCLUDED
