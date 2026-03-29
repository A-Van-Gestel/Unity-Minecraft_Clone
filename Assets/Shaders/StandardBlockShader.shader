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
                "LightMode"="SRPDefaultUnlit"
            }

            HLSLPROGRAM
            #pragma vertex vertFunction
            #pragma fragment fragFunction
            #pragma target 2.0

            #include "Includes/VoxelCommon.hlsl"
            #include "Includes/VoxelLighting.hlsl"

            // Global properties set by World.cs via Shader.SetGlobalFloat — must be outside CBUFFER
            float GlobalLightLevel;
            float minGlobalLightLevel;
            float maxGlobalLightLevel;

            VoxelV2F vertFunction(VoxelAppdata v)
            {
                return VoxelVert(v);
            }

            half4 fragFunction(VoxelV2F i) : SV_Target
            {
                half4 col = SampleBlockTexture(i.uv);

                // Apply voxel lighting using runtime globals from World.cs
                col.rgb = ApplyVoxelLighting(col.rgb, i.color.a,
                                             GlobalLightLevel, minGlobalLightLevel, maxGlobalLightLevel);

                // Multiply by vertex RGB to support BlockIconGenerator shadows and tinting
                col.rgb *= i.color.rgb;

                return col;
            }
            ENDHLSL
        }
    }
}
