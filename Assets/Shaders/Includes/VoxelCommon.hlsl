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
    half4 lightData : TEXCOORD1; // UNorm8: (skylight, blocklightR, blocklightG, blocklightB)
};

// --- Fragment Input ---
// MSAA shades an edge pixel at the pixel center, which can lie outside the covered primitive; plain
// interpolation then extrapolates, walking `uv` off its atlas tile into the neighbor's texels. Centroid
// samples inside the primitive and costs no interpolators. `fogDistance` is exempt — a smooth ramp.
struct VoxelV2F
{
    float4 vertex : SV_POSITION;
    centroid float2 uv : TEXCOORD0;
    centroid half4 color : COLOR;
    centroid half4 lightData : TEXCOORD1;
    // Horizontal distance to the camera, for RF-2 §4 fog. Interpolated per-vertex rather than derived
    // in the fragment because the block shaders keep no world position; consumers that do not fog
    // simply ignore it.
    float fogDistance : TEXCOORD2;
};

// --- Foliage sway globals (FL-1/FL-2) ---
// Set per frame by FoliageSway.cs; the zero defaults freeze all foliage (edit mode, sway disabled).
float2 FoliageWindVector; // XZ wind direction, pre-scaled by wind strength (unitless multiplier)
float4 FoliageSwayParams; // x = amplitude (blocks), z = gust fraction; y/w are the wave frequencies, applied CPU-side and not read here
float4 FoliageSwayParams2; // x = spatial frequency (rad/block along wind), y = per-voxel phase jitter fraction, z = vertical bob fraction, w = gust spatial multiplier
float4 FoliageWavePhase; // x = primary wave's phase (rad), y = gust's; time + origin, both already reduced mod 2pi

// --- Foliage Sway (FL-1/FL-2) ---
/// Displaces a vertex in object space by the global wind. swayData.x is the mesh-baked sway
/// weight (0 = rigid — roots and every non-flora vert; FL-2 cubes carry their authored strength),
/// swayData.y the baked per-voxel value used as a SMALL phase jitter. The dominant phase term is
/// spatial: a wave traveling along the wind through voxel XZ, so neighboring foliage moves
/// coherently and gusts visibly ripple across canopies and meadows instead of each voxel
/// oscillating independently. The wave is anchored to voxel space — and so survives a
/// floating-origin re-anchor (WS-3) — via FoliageWavePhase rather than an absolute coordinate.
/// FoliageSway.cs folds both unbounded terms, elapsed time and the origin's contribution, into that
/// one constant and reduces it modulo a cycle in double precision; the reduction is exact for a sine,
/// so all this stage adds is a short render-space distance and the wave animates identically at any
/// distance and any session length. Chunk transforms are translation-only, so the object-space offset
/// equals a render-space offset.
float3 ApplyFoliageSway(float3 positionOS, float2 swayData)
{
    float weight = swayData.x;
    float3 positionWS = TransformObjectToWorld(positionOS);

    // Distance along the wind direction, render-space only (time and the origin arrive pre-reduced).
    // FoliageWindVector is ~unit-length at reference wind strength, so FoliageSwayParams2.x is
    // effectively rad/block. Zero wind → zero spatial term AND zero displacement below, so no
    // normalize (and no NaN risk) is needed.
    float alongWind = dot(positionWS.xz, FoliageWindVector) * FoliageSwayParams2.x;
    float jitter = swayData.y * TWO_PI * FoliageSwayParams2.y;

    // Primary traveling wave + a broader, slower gust wave riding the same wind line. The gust carries
    // its own phase — scaling the primary's reduced value would not survive the reduction.
    float wave = sin(FoliageWavePhase.x - alongWind + jitter);
    float gust = sin(FoliageWavePhase.y - alongWind * FoliageSwayParams2.w + jitter) * FoliageSwayParams.z;
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
    o.fogDistance = distance(TransformObjectToWorld(v.vertex.xyz).xz, _WorldSpaceCameraPos.xz);
    return o;
}

// --- Texture Sampling ---
/// Samples the block texture atlas at the given UV coordinates.
half4 SampleBlockTexture(float2 uv)
{
    return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
}

#endif // VOXEL_COMMON_INCLUDED
