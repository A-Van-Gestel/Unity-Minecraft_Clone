#ifndef LIQUID_CORE_INCLUDED
#define LIQUID_CORE_INCLUDED

// =============================================================================
// LiquidCore.hlsl — Shared liquid shader logic for both game and editor preview.
//
// Contains: structs, vertex function, noise functions, shore data calculation,
// EvaluateLava, EvaluateWater, and flow-mapping time utilities.
//
// Does NOT contain: SampleSceneColor (game-only), global light uniforms.
// =============================================================================

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

// =============================================================================
// Structs
// =============================================================================

struct LiquidAppdata
{
    float4 vertex : POSITION;
    float3 normal : NORMAL;
    float4 uv : TEXCOORD0; // xy = localFlowVector, zw = shorePush (normalized direction)
    half4 color : COLOR; // r=LiquidType, g=PackedShoreMask (8-bit wall flags), b=ShadowMultiplier, a=LightLevel
};

struct LiquidV2F
{
    float4 vertex : SV_POSITION;
    float3 worldPos : TEXCOORD0;
    float4 screenPos : TEXCOORD1;
    float3 worldNormal : TEXCOORD2;
    float liquidType : TEXCOORD3;
    float lightLevel : TEXCOORD4;
    float shadowMultiplier : TEXCOORD5;
    float2 localFlowVector : TEXCOORD6; // Physical flow XY vector from mesher
    float2 shorePush : TEXCOORD7; // Normalized push direction from C# mesher
    float packedShoreMask : TEXCOORD8; // Bit-packed 8-bit wall neighbor flags (constant across quad)
};

// =============================================================================
// Material Property Declarations
// =============================================================================

CBUFFER_START(UnityPerMaterial)
    // Editor preview type selector (shared between game and preview shaders)
    float _EditorPreviewType;

    // --- Global Shoreline Controls ---
    float _ShorePushSpeed;

    // --- Lava Properties ---
    half4 _BrightColor, _MidColor, _DarkColor, _CrustColor;
    float _LavaFlowMultiplier, _NoiseScale, _CellDensity, _Speed, _CrackBrightness, _PulseSpeed, _HeatDistortionAmount;
    float _LavaShoreWidth, _LavaShoreCrust, _FlowHighlight;

    // --- Water Properties ---
    half4 _DeepColor, _ShallowColor, _FoamColor;
    float _WaterFlowMultiplier, _WaveScale, _WaveSpeed, _RippleScale, _RippleSpeed, _FoamThreshold, _DistortionAmount;
    float _WaterShoreWidth, _WaterShoreFoam, _StreamEffect;
CBUFFER_END

// =============================================================================
// Vertex Function
// =============================================================================

LiquidV2F LiquidVert(LiquidAppdata v)
{
    LiquidV2F o;
    o.vertex = TransformObjectToHClip(v.vertex.xyz);
    o.worldPos = TransformObjectToWorld(v.vertex.xyz);
    o.screenPos = ComputeScreenPos(o.vertex);
    o.worldNormal = TransformObjectToWorldNormal(v.normal);
    o.liquidType = v.color.r;
    o.lightLevel = v.color.a;
    o.shadowMultiplier = v.color.b;
    o.localFlowVector = v.uv.xy; // flow XZ
    o.shorePush = v.uv.zw; // shore push direction (normalized)
    o.packedShoreMask = v.color.g; // packed 8-bit wall neighbor flags
    return o;
}

// =============================================================================
// Noise Functions
// =============================================================================

float3 mod289(float3 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
float4 mod289(float4 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
float4 permute(float4 x) { return mod289((x * 34.0 + 1.0) * x); }
float4 taylorInvSqrt(float4 r) { return 1.79284291400159 - 0.85373472095314 * r; }

float snoise(float3 v)
{
    const float2 C = float2(1.0 / 6.0, 1.0 / 3.0);
    const float4 D = float4(0.0, 0.5, 1.0, 2.0);
    float3 i = floor(v + dot(v, C.yyy));
    float3 x0 = v - i + dot(i, C.xxx);
    float3 g = step(x0.yzx, x0.xyz);
    float3 l = 1.0 - g;
    float3 i1 = min(g.xyz, l.zxy);
    float3 i2 = max(g.xyz, l.zxy);
    float3 x1 = x0 - i1 + C.xxx;
    float3 x2 = x0 - i2 + C.yyy;
    float3 x3 = x0 - D.yyy;
    i = mod289(i);
    float4 p = permute(permute(permute(i.z + float4(0.0, i1.z, i2.z, 1.0)) + i.y + float4(0.0, i1.y, i2.y, 1.0)) + i.x + float4(0.0, i1.x, i2.x, 1.0));
    float n_ = 0.142857142857;
    float3 ns = n_ * D.wyz - D.xzx;
    float4 j = p - 49.0 * floor(p * ns.z * ns.z);
    float4 x_ = floor(j * ns.z);
    float4 y_ = floor(j - 7.0 * x_);
    float4 x = x_ * ns.x + ns.yyyy;
    float4 y = y_ * ns.x + ns.yyyy;
    float4 h = 1.0 - abs(x) - abs(y);
    float4 b0 = float4(x.xy, y.xy);
    float4 b1 = float4(x.zw, y.zw);
    float4 s0 = floor(b0) * 2.0 + 1.0;
    float4 s1 = floor(b1) * 2.0 + 1.0;
    float4 sh = -step(h, float4(0.0, 0.0, 0.0, 0.0));
    float4 a0 = b0.xzyw + s0.xzyw * sh.xxyy;
    float4 a1 = b1.xzyw + s1.xzyw * sh.zzww;
    float3 p0 = float3(a0.xy, h.x);
    float3 p1 = float3(a0.zw, h.y);
    float3 p2 = float3(a1.xy, h.z);
    float3 p3 = float3(a1.zw, h.w);
    float4 norm = taylorInvSqrt(float4(dot(p0, p0), dot(p1, p1), dot(p2, p2), dot(p3, p3)));
    p0 *= norm.x;
    p1 *= norm.y;
    p2 *= norm.z;
    p3 *= norm.w;
    float4 m = max(0.6 - float4(dot(x0, x0), dot(x1, x1), dot(x2, x2), dot(x3, x3)), 0.0);
    m = m * m;
    return 42.0 * dot(m * m, float4(dot(p0, x0), dot(p1, x1), dot(p2, x2), dot(p3, x3)));
}

float fbm(float3 p, int octaves)
{
    float v = 0.0;
    float a = 0.5;
    float f = 1.0;
    for (int i = 0; i < octaves; i++)
    {
        v += a * snoise(p * f);
        a *= 0.5;
        f *= 2.0;
    }
    return v;
}

// =============================================================================
// Shore Data Calculation
// =============================================================================

/// Decodes 8-bit wall neighbor flags from color.g and computes per-pixel
/// minimum distance to the nearest wall edge. Cardinal walls use perpendicular
/// distance; diagonal corners use L-infinity (max of both axes) for sharp shapes.
void GetShoreData(float packedMask, float3 worldPos, float3 worldNormal,
                  float2 shorePush_in, float shoreWidth,
                  out float shore_gradient, out float2 shore_push)
{
    // Decode the 8 wall flags from the packed bitmask.
    // Encoding: (wallN*1 + wallS*2 + wallE*4 + wallW*8 +
    //            diagNE*16 + diagNW*32 + diagSE*64 + diagSW*128) / 255.0
    float packed = round(packedMask * 255.0);
    float wallN = fmod(packed, 2.0);
    float wallS = fmod(floor(packed / 2.0), 2.0);
    float wallE = fmod(floor(packed / 4.0), 2.0);
    float wallW = fmod(floor(packed / 8.0), 2.0);
    float diagNE = fmod(floor(packed / 16.0), 2.0);
    float diagNW = fmod(floor(packed / 32.0), 2.0);
    float diagSE = fmod(floor(packed / 64.0), 2.0);
    float diagSW = fmod(floor(packed / 128.0), 2.0);

    // Get sub-voxel fractional position.
    // Route axes based on surface normal (same convention as flow routing).
    float3 absNorm = abs(worldNormal);
    float2 t;
    if (absNorm.y > 0.5)
        t = frac(worldPos.xz); // Top/Bottom face
    else if (absNorm.x > 0.5)
        t = frac(worldPos.zy); // East/West face
    else
        t = frac(worldPos.xy); // North/South face

    // Compute minimum distance to the nearest wall.
    // Cardinal walls: perpendicular distance = frac coordinate on the relevant axis.
    // Diagonal corners: L-infinity distance = max(dx, dy) for a sharp square falloff.
    float minDist = 1.0;
    if (wallN > 0.5) minDist = min(minDist, 1.0 - t.y); // North wall at z=1
    if (wallS > 0.5) minDist = min(minDist, t.y); // South wall at z=0
    if (wallE > 0.5) minDist = min(minDist, 1.0 - t.x); // East wall at x=1
    if (wallW > 0.5) minDist = min(minDist, t.x); // West wall at x=0
    if (diagNE > 0.5) minDist = min(minDist, max(1.0 - t.x, 1.0 - t.y));
    if (diagNW > 0.5) minDist = min(minDist, max(t.x, 1.0 - t.y));
    if (diagSE > 0.5) minDist = min(minDist, max(1.0 - t.x, t.y));
    if (diagSW > 0.5) minDist = min(minDist, max(t.x, t.y));

    // Convert distance to shore gradient: 1.0 at the wall, 0.0 at shoreWidth distance.
    shore_gradient = saturate(1.0 - minDist / max(0.001, shoreWidth));
    shore_gradient = smoothstep(0.0, 1.0, shore_gradient);

    // Push direction: normalize the interpolated push vector for displacement.
    float pushLen = length(shorePush_in);
    float2 push_dir = pushLen > 0.001 ? (shorePush_in / pushLen) : float2(0, 0);
    shore_push = push_dir * shore_gradient;
}

// =============================================================================
// Flow Routing Helper
// =============================================================================

/// Routes a 2D flow vector to 3D based on surface normal orientation.
float3 RouteFlowTo3D(float2 flow, float3 worldNormal)
{
    float3 absNorm = abs(worldNormal);

    if (absNorm.y > 0.5)
    {
        // Top/Bottom Face: UV translates to (X, Z) axes
        return float3(flow.x, 0, flow.y);
    }
    else if (absNorm.x > 0.5)
    {
        // East/West Face: UV translates to (Z, Y) axes
        return float3(0, flow.y, flow.x);
    }
    else
    {
        // North/South Face: UV translates to (X, Y) axes
        return float3(flow.x, flow.y, 0);
    }
}

// =============================================================================
// Lava Evaluation
// =============================================================================

void EvaluateLava(LiquidV2F i, float phaseTime, out float3 lavaCol, out float2 heatNormal)
{
    float t_boil = _Time.y * _Speed;

    float shore_gradient;
    float2 shore_push;
    GetShoreData(i.packedShoreMask, i.worldPos, i.worldNormal,
                 i.shorePush, _LavaShoreWidth, shore_gradient, shore_push);

    float rawMacroSpeed = length(i.localFlowVector);

    // The user's setting acts as the guaranteed minimum baseline for idle pools.
    // We add the raw macro speed on top of it so that fast-moving rivers
    // push back proportionally harder to fix diagonal artifacts.
    float dynamicPush = _ShorePushSpeed + rawMacroSpeed;

    // Combine C# macro flow with Shader micro shore repulsion
    float2 totalFlow = i.localFlowVector + shore_push * dynamicPush;

    float rawSpeed = length(totalFlow);

    // Turbulence is 1.0 near shores/waterfalls and drops to 0.0 in still lava
    float turbulence = smoothstep(0.1, 0.8, rawSpeed);
    // Idle is 1.0 when perfectly still, 0.0 when rushing
    float idle = 1.0 - turbulence;

    float2 flow = totalFlow * phaseTime * _LavaFlowMultiplier;

    // Route 2D flow to 3D based on surface normal
    float3 flow3D = RouteFlowTo3D(flow, i.worldNormal);

    // Apply the routed 3D flow to the noise coordinates
    float3 p1 = i.worldPos * _NoiseScale + flow3D + float3(0, t_boil, 0);
    float3 p2 = i.worldPos * _NoiseScale - (flow3D * 0.8) + float3(0, -t_boil * 0.8, 0);

    float base_fbm = fbm(p1, 5);
    float base_noise = (base_fbm + 1.0) * 0.5;

    float noise1 = fbm(p1 * _CellDensity, 5);
    float noise2 = fbm(p2 * _CellDensity, 5);
    float crack_pattern = pow(abs(noise1 - noise2), 2.0) * _CrackBrightness;

    half3 col = lerp(_DarkColor.rgb, _MidColor.rgb, smoothstep(0.3, 0.7, base_noise));
    lavaCol = lerp(col, _BrightColor.rgb, smoothstep(0.1, 0.35, crack_pattern));

    // --- FLOW & SHORE EFFECTS ---

    // 1. Cooling Crust (Idle Shorelines)
    float3 crust_p = i.worldPos * _NoiseScale * 2.0 - flow3D + float3(0, t_boil * 0.5, 0);
    float crust_noise = (fbm(crust_p, 3) + 1.0) * 0.5;

    // Multiply by 'idle' so fast-flowing lava rivers don't crust over, even at the shores!
    float crust_mask = smoothstep(0.4, 0.8, crust_noise) * shore_gradient * idle;
    lavaCol = lerp(lavaCol, _CrustColor.rgb, saturate(crust_mask * _LavaShoreCrust));

    // 2. Bright Sparks where flow is strong (Turbulence)
    float3 flow_mask_p = i.worldPos * _NoiseScale * 2.5 + (flow3D * 2.0);

    float flow_mask = (fbm(flow_mask_p, 4) + 1.0) * 0.5;
    lavaCol += _BrightColor.rgb * smoothstep(0.5, 0.7, flow_mask) * turbulence * _FlowHighlight;

    // Distortion Normal
    float2 offset = float2(0.01, 0.0);
    float normal_dx = fbm(p1 + offset.xyy, 4) - base_fbm;
    float normal_dz = fbm(p1 + offset.yxy, 4) - base_fbm;
    float3 normal = normalize(float3(normal_dx, 0.1, normal_dz));
    heatNormal = normal.xz * _HeatDistortionAmount;
}

// =============================================================================
// Water Evaluation
// =============================================================================

void EvaluateWater(LiquidV2F i, float phaseTime, out float3 waterCol, out float foamAmt, out float2 waterNormal)
{
    float shore_gradient;
    float2 shore_push;
    GetShoreData(i.packedShoreMask, i.worldPos, i.worldNormal,
                 i.shorePush, _WaterShoreWidth, shore_gradient, shore_push);

    float rawMacroSpeed = length(i.localFlowVector);

    // The user's setting acts as the guaranteed minimum baseline for idle pools.
    // We add the raw macro speed on top of it so that fast-moving rivers
    // push back proportionally harder to fix diagonal artifacts.
    float dynamicPush = _ShorePushSpeed + rawMacroSpeed;

    // Combine C# macro flow with Shader micro shore repulsion
    float2 totalFlow = i.localFlowVector + shore_push * dynamicPush;

    float rawSpeed = length(totalFlow);
    float turbulence = smoothstep(0.1, 0.8, rawSpeed);

    float2 flow = totalFlow * phaseTime * _WaterFlowMultiplier * _Speed;

    // Route 2D flow to 3D based on surface normal
    float3 flow3D = RouteFlowTo3D(flow, i.worldNormal);

    // Apply the routed 3D flow to the noise coordinates
    float3 wave_p = i.worldPos * _WaveScale + flow3D + float3(0, _Time.y * _WaveSpeed, 0);
    float3 ripple_p = i.worldPos * _RippleScale - flow3D + float3(0, _Time.y * _RippleSpeed, 0);

    float wave_fbm = fbm(wave_p, 4);
    float ripple_noise = fbm(ripple_p, 4);

    half4 water_base_color = lerp(_DeepColor, _ShallowColor, i.lightLevel);

    float combined_noise = (wave_fbm + ripple_noise) * 0.5;
    combined_noise = (combined_noise + 1.0) * 0.5;

    waterCol = lerp(water_base_color.rgb, _ShallowColor.rgb, combined_noise);
    foamAmt = smoothstep(_FoamThreshold - 0.1, _FoamThreshold + 0.1, combined_noise);

    // --- SHORE & STREAM EFFECTS ---

    float3 stream_p = i.worldPos * _WaveScale * 2.0 + (flow3D * 3.0);
    float stream_noise = (fbm(stream_p, 3) + 1.0) * 0.5;

    // 1. Stream Foam (Turbulence-based)
    // Create isolated streaks that only appear where turbulence is high (at drops/fast flow)
    float stream_foam = smoothstep(0.55, 0.75, stream_noise) * turbulence * _StreamEffect;

    // 2. Shore Foam (Mask Distance-based)
    float shore_foam = smoothstep(0.4, 0.75, stream_noise) * shore_gradient * _WaterShoreFoam;

    foamAmt = saturate(foamAmt + stream_foam + shore_foam);

    // Distortion Normal
    float2 offset = float2(0.01, 0.0);
    float normal_dx = fbm(wave_p + offset.xyy, 3) - wave_fbm;
    float normal_dz = fbm(wave_p + offset.yxy, 3) - wave_fbm;
    float3 normal = normalize(float3(normal_dx, 0.1, normal_dz));
    waterNormal = normal.xz * _DistortionAmount;
}

// =============================================================================
// Flow-Mapping Time Utilities
// =============================================================================

/// Calculates dual-phase flow mapping time values for seamless looping.
/// Call this in the fragment shader, then pass time0/time1 to EvaluateLava/EvaluateWater
/// and blend the results using weight0/weight1.
void CalculateFlowPhases(out float time0, out float time1,
                         out float weight0, out float weight1)
{
    // Lowered from 3.0 to 1.5. This halves the maximum distance the texture can stretch
    // before the dual-phase crossfade resets it, hiding distortions much better.
    float cycleDuration = 1.5;
    float phase0 = frac(_Time.y / cycleDuration);
    float phase1 = frac((_Time.y + cycleDuration * 0.5) / cycleDuration);

    weight0 = 1.0 - abs(2.0 * phase0 - 1.0);
    weight1 = 1.0 - abs(2.0 * phase1 - 1.0);

    time0 = phase0 * cycleDuration;
    time1 = phase1 * cycleDuration;
}

#endif // LIQUID_CORE_INCLUDED
