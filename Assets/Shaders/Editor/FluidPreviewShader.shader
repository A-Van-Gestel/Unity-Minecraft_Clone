Shader "Hidden/Editor/FluidPreview"
{
    // Editor preview shader for fluid block types (Water, Lava).
    // Uses the same LiquidCore.hlsl include as the game shader for all animation,
    // noise, shore, and flow logic. Substitutes a solid base color instead of
    // SampleSceneColor (which requires a rendered scene that doesn't exist in
    // PreviewRenderUtility).
    Properties
    {
        [KeywordEnum(Water, Lava)] _EditorPreviewType("Editor Preview Type", Float) = 0

        // --- Global Shoreline Controls ---
        [Header(Shoreline Effects)]
        _ShorePushSpeed("Shore Push Speed", Range(0.0, 3.0)) = 0.8

        // --- Lava Properties ---
        [Header(Lava)]
        _BrightColor("Bright Color (Cracks)", Color) = (1.0, 0.941, 0.588, 1.0)
        _MidColor("Mid Color", Color) = (1.0, 0.434, 0.0, 1.0)
        _DarkColor("Dark Color (Crust)", Color) = (0.51, 0.02, 0.0, 1.0)
        _CrustColor("Cooled Crust Color (Shore)", Color) = (0.118, 0.039, 0.02, 1.0)
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
        _DeepColor("Deep Color (Low Light)", Color) = (0.098, 0.232, 0.502, 0.85)
        _ShallowColor("Shallow Color (High Light)", Color) = (0.165, 0.463, 0.945, 0.75)
        _FoamColor("Foam Color", Color) = (0.941, 0.961, 1.0, 1.0)
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
            "RenderPipeline"="UniversalPipeline"
            "Queue"="Transparent" "RenderType"="Transparent"
        }

        Pass
        {
            Name "FluidEditorPreview"
            // No LightMode tag — defaults to SRPDefaultUnlit, works in preview cameras

            Cull Back
            ZWrite On
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vertFunction
            #pragma fragment fragFunction
            #pragma target 3.0

            // Shared liquid logic (identical to the game shader)
            #include "../Includes/LiquidCore.hlsl"
            #include "../Includes/VoxelLighting.hlsl"

            LiquidV2F vertFunction(LiquidAppdata v)
            {
                return LiquidVert(v);
            }

            half4 fragFunction(LiquidV2F i) : SV_Target
            {
                float finalLiquidType = i.liquidType;

                // In editor (not playing), use the material's preview type selector
                #if defined(UNITY_EDITOR)
                if (!unity_IsEditorPlaying) finalLiquidType = _EditorPreviewType;
                #endif

                // Calculate shade using shared lighting function with editor daylight defaults
                float shade = CalculateVoxelShade(i.lightLevel, 1.0, 0.15, 1.0);

                // --- FLOW MAPPING TIME SETUP ---
                float time0, time1, weight0, weight1;
                CalculateFlowPhases(time0, time1, weight0, weight1);

                if (finalLiquidType > 0.5) // Lava
                {
                    float3 col0, col1;
                    float2 norm0, norm1;

                    EvaluateLava(i, time0, col0, norm0);
                    EvaluateLava(i, time1, col1, norm1);

                    // Actually use the refraction normal variables slightly to silence compiler warnings
                    // without relying on C-style (void) casts which break some Unity HLSL backends
                    col0.r += norm0.x * 0.000001;
                    col1.r += norm1.x * 0.000001;

                    half3 lava_col = col0 * weight0 + col1 * weight1;

                    float pulse = (sin(_Time.y * _PulseSpeed) * 0.5 + 0.5) * 0.2 + 0.9;
                    lava_col *= pulse;

                    // Apply gamma shadow in linear space using shared helper
                    lava_col *= CalculateLinearVoxelShadow(shade);

                    lava_col *= i.shadowMultiplier;

                    // Preview: solid background instead of SampleSceneColor
                    half3 background = half3(0.15, 0.08, 0.04); // Dark warm background for lava
                    return half4(lerp(background, lava_col, 0.95), 1.0);
                }
                else // Water
                {
                    float3 col0, col1;
                    float foam0, foam1;
                    float2 norm0, norm1;

                    EvaluateWater(i, time0, col0, foam0, norm0);
                    EvaluateWater(i, time1, col1, foam1, norm1);

                    // Actually use the refraction normal variables slightly to silence compiler warnings
                    col0.r += norm0.x * 0.000001;
                    col1.r += norm1.x * 0.000001;

                    half3 water_surface_color = col0 * weight0 + col1 * weight1;
                    float total_foam = foam0 * weight0 + foam1 * weight1;

                    half3 final_color = lerp(water_surface_color, _FoamColor.rgb, total_foam);

                    // Apply gamma shadow in linear space using shared helper
                    final_color *= CalculateLinearVoxelShadow(shade);

                    final_color *= i.shadowMultiplier;

                    // Preview: solid background instead of SampleSceneColor
                    half4 water_base_color = lerp(_DeepColor, _ShallowColor, i.lightLevel);
                    half3 background = half3(0.05, 0.1, 0.15); // Dark cool background for water
                    return half4(lerp(background, final_color, water_base_color.a), water_base_color.a);
                }
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
