Shader "Minecraft/UberLiquidShader"
{
    Properties
    {
        // This now correctly controls the preview in the editor
        [KeywordEnum(Water, Lava)] _EditorPreviewType("Editor Preview Type", Float) = 0

        // --- Global Shoreline Controls ---
        [Header(Shoreline Effects)]
        _ShorePushSpeed("Shore Push Speed", Range(0.0, 3.0)) = 0.8

        // --- Lava Properties ---
        [Header(Lava)]
        _BrightColor("Bright Color (Cracks)", Color) = (1, 0.9, 0.6, 1)
        _MidColor("Mid Color", Color) = (1, 0.5, 0, 1)
        _DarkColor("Dark Color (Crust)", Color) = (0.6, 0.1, 0, 1)
        _CrustColor("Cooled Crust Color (Shore)", Color) = (0.2, 0.05, 0.0, 1)
        _LavaFlowMultiplier("Lava Flow Multiplier", Range(0.0, 5.0)) = 0.35
        _NoiseScale("Lava Scale", Range(0.1, 10)) = 2.0
        _CellDensity("Cell Density", Range(1, 4)) = 2.5
        _Speed("Flow Speed", Range(0, 2)) = 0.3
        _CrackBrightness("Crack Brightness", Range(0, 3)) = 1.5
        _PulseSpeed("Pulse Speed", Range(0, 5)) = 1.5
        _HeatDistortionAmount("Heat Distortion", Range(0, 0.1)) = 0.015

        [Header(Lava Shores and Flow)]
        _LavaShoreWidth("Shore Width", Range(0.01, 1.0)) = 0.4
        _LavaShoreCrust("Shore Crust Amount", Range(0.0, 2.0)) = 1.0
        _FlowHighlight("Flow Sparks", Range(0, 2)) = 0.5

        // --- Water Properties ---
        [Header(Water)]
        _DeepColor("Deep Color (Low Light)", Color) = (0.1, 0.2, 0.5, 0.85)
        _ShallowColor("Shallow Color (High Light)", Color) = (0.3, 0.5, 0.9, 0.7)
        _FoamColor("Foam Color", Color) = (0.9, 0.9, 0.9, 1)
        _WaterFlowMultiplier("Water Flow Multiplier", Range(0.0, 5.0)) = 2.5
        _WaveScale("Wave Scale", Range(0.1, 10)) = 5.0
        _WaveSpeed("Wave Speed", Range(0, 2)) = 0.4
        _RippleScale("Ripple Scale", Range(1, 20)) = 15.0
        _RippleSpeed("Ripple Speed", Range(0, 5)) = 1.2
        _FoamThreshold("Wave Foam Threshold", Range(0.5, 1.0)) = 0.8
        _DistortionAmount("Refraction Distortion", Range(0, 0.1)) = 0.02

        [Header(Water Shores and Flow)]
        _WaterShoreWidth("Shore Width", Range(0.01, 1.0)) = 0.15
        _WaterShoreFoam("Shore Foam Amount", Range(0.0, 3.0)) = 0.65
        _StreamEffect("Stream Foam Amount", Range(0.0, 3.0)) = 1.0
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent" "RenderType"="Transparent"
        }

        GrabPass
        {
            "_GrabTexture"
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vertFunction
            #pragma fragment fragFunction
            #pragma target 3.0

            #include "UnityCG.cginc"
            #include "UnityShaderVariables.cginc" // For unity_IsEditorPlaying

            // Structs
            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR; // r:LiquidType, g:ShorelineFlag, b:ShadowMultiplier, a:LightLevel
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float4 screenPos : TEXCOORD1;
                float3 worldNormal : TEXCOORD2;
                float liquidType : TEXCOORD3;
                float shorelineFlag : TEXCOORD4;
                float lightLevel : TEXCOORD5;
                float shadowMultiplier : TEXCOORD6;
                float2 localFlowVector : TEXCOORD7; // Physical flow XY vector from mesher
            };

            // Global Properties
            float _EditorPreviewType;
            float _ShorePushSpeed;
            sampler2D _GrabTexture;
            float GlobalLightLevel, minGlobalLightLevel, maxGlobalLightLevel;

            // Lava Properties
            fixed4 _BrightColor, _MidColor, _DarkColor, _CrustColor;
            float _LavaFlowMultiplier, _NoiseScale, _CellDensity, _Speed, _CrackBrightness, _PulseSpeed, _HeatDistortionAmount;
            float _LavaShoreWidth, _LavaShoreCrust, _FlowHighlight;

            // Water Properties
            fixed4 _DeepColor, _ShallowColor, _FoamColor;
            float _WaterFlowMultiplier, _WaveScale, _WaveSpeed, _RippleScale, _RippleSpeed, _FoamThreshold, _DistortionAmount;
            float _WaterShoreWidth, _WaterShoreFoam, _StreamEffect;

            // Noise Functions
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

            v2f vertFunction(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.screenPos = ComputeGrabScreenPos(o.vertex);
                o.worldNormal = UnityObjectToWorldNormal(v.normal); // Pass normal to fragment
                o.liquidType = v.color.r;
                o.shorelineFlag = v.color.g;
                o.lightLevel = v.color.a;
                o.shadowMultiplier = v.color.b;
                o.localFlowVector = v.uv; // Added physical flow XY vector from mesher
                return o;
            }

            // --- REUSABLE SHORELINE LOGIC ---
            // Decodes the 8-bit mask and returns BOTH the gradient intensity and a continuous 2D push vector
            void GetShoreData(float shorelineFlag, float3 worldPos, float shoreWidth, out float shore_gradient, out float2 shore_push)
            {
                int mask = round(shorelineFlag * 255.0);
                float minDist = 1.0;
                shore_push = float2(0, 0);

                if (mask > 0)
                {
                    // Derive block-local UV from world position (0.0 to 1.0 across the face)
                    // Clamp prevents frac() from wrapping exactly at the 1.0 boundary
                    float2 localUV = clamp(frac(worldPos.xz), 0.001, 0.999);

                    float dN = 1.0 - localUV.y; // North (+Z)
                    float dE = 1.0 - localUV.x; // East (+X)
                    float dS = localUV.y; // South (-Z)
                    float dW = localUV.x; // West (-X)

                    // 1. Cardinal pushes.
                    // The weight (1.0 - d) guarantees the push fades exactly to 0.0 at the opposite edge of the block.
                    if ((mask & 1) != 0)
                    {
                        minDist = min(minDist, dN);
                        shore_push += float2(0, 1) * (1.0 - dN);
                    }
                    if ((mask & 2) != 0)
                    {
                        minDist = min(minDist, dE);
                        shore_push += float2(1, 0) * (1.0 - dE);
                    }
                    if ((mask & 4) != 0)
                    {
                        minDist = min(minDist, dS);
                        shore_push += float2(0, -1) * (1.0 - dS);
                    }
                    if ((mask & 8) != 0)
                    {
                        minDist = min(minDist, dW);
                        shore_push += float2(-1, 0) * (1.0 - dW);
                    }

                    // 2. Outer corner pushes.
                    // We ONLY apply these if the adjacent cardinals are empty (e.g. if N is solid, the N push handles it).
                    // We use max(0.0, 1.0 - d) to prevent the influence from going negative across block boundaries!
                    if ((mask & 16) != 0 && (mask & 3) == 0)
                    {
                        // NE is solid, N(1) & E(2) are empty
                        float d = length(float2(dE, dN));
                        minDist = min(minDist, d);
                        shore_push += normalize(float2(1, 1)) * max(0.0, 1.0 - d);
                    }
                    if ((mask & 32) != 0 && (mask & 6) == 0)
                    {
                        // SE is solid, E(2) & S(4) are empty
                        float d = length(float2(dE, dS));
                        minDist = min(minDist, d);
                        shore_push += normalize(float2(1, -1)) * max(0.0, 1.0 - d);
                    }
                    if ((mask & 64) != 0 && (mask & 12) == 0)
                    {
                        // SW is solid, S(4) & W(8) are empty
                        float d = length(float2(dW, dS));
                        minDist = min(minDist, d);
                        shore_push += normalize(float2(-1, -1)) * max(0.0, 1.0 - d);
                    }
                    if ((mask & 128) != 0 && (mask & 9) == 0)
                    {
                        // NW is solid, W(8) & N(1) are empty
                        float d = length(float2(dW, dN));
                        minDist = min(minDist, d);
                        shore_push += normalize(float2(-1, 1)) * max(0.0, 1.0 - d);
                    }
                }

                // Sub-voxel shore gradient: 1.0 at the wall, fading out exactly at shoreWidth
                shore_gradient = saturate(1.0 - (minDist / max(0.001, shoreWidth)));
                shore_gradient = smoothstep(0.0, 1.0, shore_gradient);

                // Normalize push direction and scale it so it is strongest exactly at the wall
                if (length(shore_push) > 0.001)
                {
                    shore_push = normalize(shore_push) * shore_gradient;
                }
            }

            void EvaluateLava(v2f i, float phaseTime, out float3 lavaCol, out float2 heatNormal)
            {
                float t_boil = _Time.y * _Speed;

                float shore_gradient;
                float2 shore_push;
                GetShoreData(i.shorelineFlag, i.worldPos, _LavaShoreWidth, shore_gradient, shore_push);

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
                float3 flow3D;
                float3 absNorm = abs(i.worldNormal);

                if (absNorm.y > 0.5)
                {
                    // Top/Bottom Face: UV translates to (X, Z) axes
                    flow3D = float3(flow.x, 0, flow.y);
                }
                else if (absNorm.x > 0.5)
                {
                    // East/West Face: UV translates to (Z, Y) axes
                    flow3D = float3(0, flow.y, flow.x);
                }
                else
                {
                    // North/South Face: UV translates to (X, Y) axes
                    flow3D = float3(flow.x, flow.y, 0);
                }

                // Apply the routed 3D flow to the noise coordinates
                float3 p1 = i.worldPos * _NoiseScale + flow3D + float3(0, t_boil, 0);
                float3 p2 = i.worldPos * _NoiseScale - (flow3D * 0.8) + float3(0, -t_boil * 0.8, 0);

                float base_fbm = fbm(p1, 5);
                float base_noise = (base_fbm + 1.0) * 0.5;

                float noise1 = fbm(p1 * _CellDensity, 5);
                float noise2 = fbm(p2 * _CellDensity, 5);
                float crack_pattern = pow(abs(noise1 - noise2), 2.0) * _CrackBrightness;

                fixed3 col = lerp(_DarkColor.rgb, _MidColor.rgb, smoothstep(0.3, 0.7, base_noise));
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

            void EvaluateWater(v2f i, float phaseTime, out float3 waterCol, out float foamAmt, out float2 waterNormal)
            {
                float shore_gradient;
                float2 shore_push;
                GetShoreData(i.shorelineFlag, i.worldPos, _WaterShoreWidth, shore_gradient, shore_push);

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
                float3 flow3D;
                float3 absNorm = abs(i.worldNormal);

                if (absNorm.y > 0.5)
                {
                    flow3D = float3(flow.x, 0, flow.y);
                }
                else if (absNorm.x > 0.5)
                {
                    flow3D = float3(0, flow.y, flow.x);
                }
                else
                {
                    flow3D = float3(flow.x, flow.y, 0);
                }

                // Apply the routed 3D flow to the noise coordinates
                float3 wave_p = i.worldPos * _WaveScale + flow3D + float3(0, _Time.y * _WaveSpeed, 0);
                float3 ripple_p = i.worldPos * _RippleScale - flow3D + float3(0, _Time.y * _RippleSpeed, 0);

                float wave_fbm = fbm(wave_p, 4);
                float ripple_noise = fbm(ripple_p, 4);

                fixed4 water_base_color = lerp(_DeepColor, _ShallowColor, i.lightLevel);

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

            fixed4 fragFunction(v2f i) : SV_Target
            {
                float finalLiquidType = i.liquidType;

                #if defined(UNITY_EDITOR)
                if (!unity_IsEditorPlaying) finalLiquidType = _EditorPreviewType;
                #endif

                float shade = (maxGlobalLightLevel - minGlobalLightLevel) * GlobalLightLevel + minGlobalLightLevel;
                shade *= i.lightLevel;
                shade = clamp(1.0 - shade, minGlobalLightLevel, maxGlobalLightLevel);

                // --- FLOW MAPPING TIME SETUP ---
                // Lowered from 3.0 to 1.5. This halves the maximum distance the texture can stretch
                // before the dual-phase crossfade resets it, hiding distortions much better.
                float cycleDuration = 1.5;
                float phase0 = frac(_Time.y / cycleDuration);
                float phase1 = frac((_Time.y + cycleDuration * 0.5) / cycleDuration);

                float weight0 = 1.0 - abs(2.0 * phase0 - 1.0);
                float weight1 = 1.0 - abs(2.0 * phase1 - 1.0);

                float time0 = phase0 * cycleDuration;
                float time1 = phase1 * cycleDuration;

                if (finalLiquidType > 0.5) // Lava
                {
                    float3 col0, col1;
                    float2 norm0, norm1;

                    EvaluateLava(i, time0, col0, norm0);
                    EvaluateLava(i, time1, col1, norm1);

                    fixed3 lava_col = col0 * weight0 + col1 * weight1;
                    float2 final_normal = norm0 * weight0 + norm1 * weight1;

                    float2 distortedUV = (i.screenPos.xy / i.screenPos.w) + final_normal;
                    fixed4 background = tex2D(_GrabTexture, distortedUV);

                    float pulse = (sin(_Time.y * _PulseSpeed) * 0.5 + 0.5) * 0.2 + 0.9;
                    lava_col *= pulse;
                    lava_col = lerp(lava_col, lava_col * 0.1, shade);

                    lava_col *= i.shadowMultiplier;
                    return lerp(background, fixed4(lava_col, 1.0), 0.95);
                }
                else // Water
                {
                    float3 col0, col1;
                    float foam0, foam1;
                    float2 norm0, norm1;

                    EvaluateWater(i, time0, col0, foam0, norm0);
                    EvaluateWater(i, time1, col1, foam1, norm1);

                    fixed3 water_surface_color = col0 * weight0 + col1 * weight1;

                    // Because foam0 and foam1 are phase-blended here, the shore effect
                    // seamlessly fades and loops along with the flow!
                    float total_foam = foam0 * weight0 + foam1 * weight1;

                    float2 final_normal = norm0 * weight0 + norm1 * weight1;

                    float2 distortedUV = (i.screenPos.xy / i.screenPos.w) + final_normal;
                    fixed4 background = tex2D(_GrabTexture, distortedUV);

                    fixed3 final_color = lerp(water_surface_color, _FoamColor.rgb, total_foam);
                    final_color = lerp(final_color, final_color * 0.1, shade);
                    final_color *= i.shadowMultiplier;

                    fixed4 water_base_color = lerp(_DeepColor, _ShallowColor, i.lightLevel);
                    return lerp(background, fixed4(final_color, 1.0), water_base_color.a);
                }
            }
            ENDCG
        }
    }
    FallBack "Transparent/VertexLit"
}
