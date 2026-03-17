Shader "Minecraft/UberLiquidShader"
{
    Properties
    {
        // This now correctly controls the preview in the editor
        [KeywordEnum(Water, Lava)] _EditorPreviewType("Editor Preview Type", Float) = 0

        // --- Global Shoreline Controls ---
        [Header(Shoreline Effects)]
        [Toggle(USE_SHORE_EFFECTS)] _UseShoreEffects ("Enable Shore Effects", Float) = 0
        _ShoreSize("Shore Effect Size", Range(0.0, 1.0)) = 0.6

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
        _FlowHighlight("Flow Highlight", Range(0, 2)) = 0.5

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
        _StreamEffect("Stream Foam Effect", Range(0.0, 3.0)) = 1.0
        _DistortionAmount("Refraction Distortion", Range(0, 0.1)) = 0.02
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

            // Keyword for the shoreline toggle
            #pragma shader_feature USE_SHORE_EFFECTS

            #include "UnityCG.cginc"
            #include "UnityShaderVariables.cginc" // For unity_IsEditorPlaying

            // Structs
            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL; // Normal is required for gradient
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR; // r:LiquidType, g:Shoreline, b:Unused, a:LightLevel
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float4 screenPos : TEXCOORD1;
                float3 worldNormal : TEXCOORD2; // Pass normal to fragment
                float liquidType : TEXCOORD3;
                float shorelineFlag : TEXCOORD4;
                float lightLevel : TEXCOORD5;
                float shadowMultiplier : TEXCOORD6;
                float2 localFlowVector : TEXCOORD7; // Added physical flow XY vector from mesher
            };

            // Global Properties
            float _EditorPreviewType;
            float _ShoreSize;
            sampler2D _GrabTexture;
            float GlobalLightLevel, minGlobalLightLevel, maxGlobalLightLevel;

            // Lava Properties
            fixed4 _BrightColor, _MidColor, _DarkColor, _CrustColor;
            float _LavaFlowMultiplier, _NoiseScale, _CellDensity, _Speed, _CrackBrightness, _PulseSpeed, _HeatDistortionAmount, _FlowHighlight;

            // Water Properties
            fixed4 _DeepColor, _ShallowColor, _FoamColor;
            float _WaterFlowMultiplier, _WaveScale, _WaveSpeed, _RippleScale, _RippleSpeed, _FoamThreshold, _StreamEffect, _DistortionAmount;

            // Noise Functions
            float3 mod289(float3 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
            float4 mod289(float4 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
            float4 permute(float4 x) { return mod289(((x * 34.0) + 1.0) * x); }
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
                o.worldNormal = UnityObjectToWorldNormal(v.normal); // ransform normal to world space
                o.liquidType = v.color.r;
                o.shorelineFlag = v.color.g;
                o.lightLevel = v.color.a;
                o.shadowMultiplier = v.color.b;
                o.localFlowVector = v.uv; // Pass the flow direction vector
                return o;
            }

            // Helper function to calculate the shoreline gradient
            float get_shore_factor(float3 worldPos, float3 worldNormal, float shoreSize)
            {
                // Determine which plane the quad is on based on the normal
                float3 absNormal = abs(worldNormal);
                float2 quadUV;
                if (absNormal.y > absNormal.x && absNormal.y > absNormal.z)
                    quadUV = frac(worldPos.xz); // Horizontal plane
                else if (absNormal.x > absNormal.y && absNormal.x > absNormal.z)
                    quadUV = frac(worldPos.yz); // X-facing vertical plane
                else
                    quadUV = frac(worldPos.xy); // Z-facing vertical plane

                // Calculate distance from the center of the quad (0-0.5)
                float2 distFromCenter = abs(quadUV - 0.5);
                float maxDist = max(distFromCenter.x, distFromCenter.y);

                // Create a smooth gradient that starts from the edge and moves inwards
                // The 'shoreSize' property now controls the width of this gradient
                return 1.0 - smoothstep(0.5 - (shoreSize * 0.5), 0.5, maxDist);
            }

            void EvaluateLava(v2f i, float phaseTime, out float3 lavaCol, out float2 heatNormal)
            {
                float t_boil = _Time.y * _Speed;
                float2 flow = i.localFlowVector * phaseTime * _LavaFlowMultiplier;

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

                float2 offset = float2(0.01, 0.0);
                float normal_dx = fbm(p1 + offset.xyy, 4) - base_fbm;
                float normal_dz = fbm(p1 + offset.yxy, 4) - base_fbm;
                float3 normal = normalize(float3(normal_dx, 0.1, normal_dz));
                heatNormal = normal.xz * _HeatDistortionAmount;

                float noise1 = fbm(p1 * _CellDensity, 5);
                float noise2 = fbm(p2 * _CellDensity, 5);
                float crack_pattern = pow(abs(noise1 - noise2), 2.0) * _CrackBrightness;

                fixed3 col = lerp(_DarkColor.rgb, _MidColor.rgb, smoothstep(0.3, 0.7, base_noise));
                lavaCol = lerp(col, _BrightColor.rgb, smoothstep(0.1, 0.35, crack_pattern));

                // Flow Highlight
                float3 flow_mask_p = i.worldPos * _NoiseScale * 2.5 + (flow3D * 2.0);

                float flow_mask = (fbm(flow_mask_p, 4) + 1.0) * 0.5;
                lavaCol += _BrightColor.rgb * smoothstep(0.5, 0.7, flow_mask) * _FlowHighlight;
            }

            void EvaluateWater(v2f i, float phaseTime, out float3 waterCol, out float foamAmt, out float2 waterNormal)
            {
                float2 flow = i.localFlowVector * phaseTime * _WaterFlowMultiplier * _Speed;

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

                float2 offset = float2(0.01, 0.0);
                float normal_dx = fbm(wave_p + offset.xyy, 3) - wave_fbm;
                float normal_dz = fbm(wave_p + offset.yxy, 3) - wave_fbm;
                float3 normal = normalize(float3(normal_dx, 0.1, normal_dz));
                waterNormal = normal.xz * _DistortionAmount;

                fixed4 water_base_color = lerp(_DeepColor, _ShallowColor, i.lightLevel);

                float combined_noise = (wave_fbm + ripple_noise) * 0.5;
                combined_noise = (combined_noise + 1.0) * 0.5;

                waterCol = lerp(water_base_color.rgb, _ShallowColor.rgb, combined_noise);
                foamAmt = smoothstep(_FoamThreshold - 0.1, _FoamThreshold + 0.1, combined_noise);

                // --- Streamy Flow Highlights ---
                // 1. Get the raw speed of the fluid independent of time (length of the mesher's XZ vector)
                float rawSpeed = length(i.localFlowVector);

                // 2. Variable stream intensity:
                // rawSpeed is ~0.35 for gentle rivers, up to 1.0 for waterfalls.
                // We map this so flat rivers have gentle sparks (~20%), and waterfalls roar (100%).
                float isFlowing = smoothstep(0.1, 0.9, rawSpeed);

                // 3. Sample a higher-frequency noise that moves significantly faster along the flow vector
                float3 stream_p = i.worldPos * _WaveScale * 2.0 + (flow3D * 3.0);
                float stream_noise = (fbm(stream_p, 3) + 1.0) * 0.5;

                // 4. Threshold it sharply to create isolated "sparks" and streaks, multiplying by our flow mask
                float stream_foam = smoothstep(0.55, 0.75, stream_noise) * isFlowing * _StreamEffect;

                // 5. Add the stream foam to the base foam, saturating to keep it between 0.0 and 1.0
                foamAmt = saturate(foamAmt + stream_foam);
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
                float cycleDuration = 3.0; // Seconds before flow phase resets
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

                    #if defined(USE_SHORE_EFFECTS)
                    float crust_noise = (snoise(i.worldPos.xzy * 0.7) + 1.0) * 0.5;
                    float shore_factor = get_shore_factor(i.worldPos, i.worldNormal, _ShoreSize);
                    float crust_amount = i.shorelineFlag * shore_factor * crust_noise;
                    lava_col = lerp(lava_col, _CrustColor.rgb, crust_amount);
                    #endif

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
                    float wave_foam = foam0 * weight0 + foam1 * weight1;
                    float2 final_normal = norm0 * weight0 + norm1 * weight1;

                    float2 distortedUV = (i.screenPos.xy / i.screenPos.w) + final_normal;
                    fixed4 background = tex2D(_GrabTexture, distortedUV);

                    float total_foam = wave_foam;

                    #if defined(USE_SHORE_EFFECTS)
                    float shore_foam_noise = (snoise(i.worldPos.xzy * 0.5) + 1.0) * 0.5;
                    float shore_factor = get_shore_factor(i.worldPos, i.worldNormal, _ShoreSize);
                    float shore_foam = i.shorelineFlag * shore_factor * shore_foam_noise;
                    total_foam = saturate(total_foam + shore_foam);
                    #endif

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
