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
            "Queue"="Transparent" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline"
        }

        Pass
        {
            Name "LiquidForward"
            Tags
            {
                "LightMode"="UniversalForward"
            }

            HLSLPROGRAM
            #pragma vertex vertFunction
            #pragma fragment fragFunction
            #pragma target 3.0

            // Shared liquid logic (structs, vertex, noise, shore, evaluate)
            #include "Includes/LiquidCore.hlsl"
            #include "Includes/VoxelLighting.hlsl"

            // Game-only: scene refraction via URP Opaque Texture
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            // Game-only: global light uniforms from World.cs
            float GlobalLightLevel, minGlobalLightLevel, maxGlobalLightLevel;

            LiquidV2F vertFunction(LiquidAppdata v)
            {
                return LiquidVert(v);
            }

            half4 fragFunction(LiquidV2F i) : SV_Target
            {
                float finalLiquidType = i.liquidType;

                #if defined(UNITY_EDITOR)
                if (!unity_IsEditorPlaying) finalLiquidType = _EditorPreviewType;
                #endif

                float shade = CalculateVoxelShade(i.lightLevel,
                                                  GlobalLightLevel, minGlobalLightLevel, maxGlobalLightLevel);

                // --- FLOW MAPPING TIME SETUP ---
                float time0, time1, weight0, weight1;
                CalculateFlowPhases(time0, time1, weight0, weight1);

                if (finalLiquidType > 0.5) // Lava
                {
                    float3 col0, col1;
                    float2 norm0, norm1;

                    EvaluateLava(i, time0, col0, norm0);
                    EvaluateLava(i, time1, col1, norm1);

                    half3 lava_col = col0 * weight0 + col1 * weight1;
                    float2 final_normal = norm0 * weight0 + norm1 * weight1;

                    float2 distortedUV = (i.screenPos.xy / i.screenPos.w) + final_normal;
                    half4 background = half4(SampleSceneColor(distortedUV), 1.0);

                    float pulse = (sin(_Time.y * _PulseSpeed) * 0.5 + 0.5) * 0.2 + 0.9;
                    lava_col *= pulse;
                    lava_col = lerp(lava_col, lava_col * 0.1, shade);

                    lava_col *= i.shadowMultiplier;
                    return lerp(background, half4(lava_col, 1.0), 0.95);
                }
                else // Water
                {
                    float3 col0, col1;
                    float foam0, foam1;
                    float2 norm0, norm1;

                    EvaluateWater(i, time0, col0, foam0, norm0);
                    EvaluateWater(i, time1, col1, foam1, norm1);

                    half3 water_surface_color = col0 * weight0 + col1 * weight1;

                    // Because foam0 and foam1 are phase-blended here, the shore effect
                    // seamlessly fades and loops along with the flow!
                    float total_foam = foam0 * weight0 + foam1 * weight1;

                    float2 final_normal = norm0 * weight0 + norm1 * weight1;

                    float2 distortedUV = (i.screenPos.xy / i.screenPos.w) + final_normal;
                    half4 background = half4(SampleSceneColor(distortedUV), 1.0);

                    half3 final_color = lerp(water_surface_color, _FoamColor.rgb, total_foam);
                    final_color = lerp(final_color, final_color * 0.1, shade);
                    final_color *= i.shadowMultiplier;

                    half4 water_base_color = lerp(_DeepColor, _ShallowColor, i.lightLevel);
                    return lerp(background, half4(final_color, 1.0), water_base_color.a);
                }
            }
            ENDHLSL
        }
    }
    FallBack "Universal Render Pipeline/Unlit"
}
