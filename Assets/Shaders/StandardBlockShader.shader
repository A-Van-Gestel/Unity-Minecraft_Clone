Shader "Minecraft/Blocks"
{
    Properties
    {
        _MainTex("Block Texture Atlas", 2D) = "white" {}
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline"
        }
        LOD 100

        Pass
        {
            Name "ForwardLit"
            Tags
            {
                "LightMode"="UniversalForward"
            }

            HLSLPROGRAM
            #pragma vertex vertFunction
            #pragma fragment fragFunction
            #pragma target 2.0
            #pragma multi_compile _ DEBUG_LIGHTDATA

            #include "Includes/VoxelCommon.hlsl"
            #include "Includes/VoxelLighting.hlsl"

            // Global properties set by World.cs — must be outside CBUFFER
            float GlobalLightLevel;
            float minGlobalLightLevel;
            float maxGlobalLightLevel;
            half3 SkyLightColor;

            VoxelV2F vertFunction(VoxelAppdata v)
            {
                return VoxelVert(v);
            }

            half4 fragFunction(VoxelV2F i) : SV_Target
            {
                #ifdef DEBUG_LIGHTDATA
                // False-color visualization: R = sunlight (from lightData.r),
                // G = blocklight (from lightData.a), B = 0.
                // Smooth lighting produces per-vertex gradients; flat lighting is uniform per face.
                return half4(i.lightData.r, i.lightData.a, 0.0, 1.0);
                #endif

                half4 col = SampleBlockTexture(i.uv);

                // Apply voxel lighting: sun (scalar, tinted by sky color) + block (RGB)
                col.rgb = ApplyVoxelLightingRGB(col.rgb, i.lightData.r, i.lightData.gba,
                                                SkyLightColor,
                                                GlobalLightLevel, minGlobalLightLevel, maxGlobalLightLevel);

                // Multiply by vertex RGB to support BlockIconGenerator shadows and tinting
                col.rgb *= i.color.rgb;

                return col;
            }
            ENDHLSL
        }
    }
}
