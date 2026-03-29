Shader "Minecraft/Transparent Blocks"
{
    Properties
    {
        _MainTex("Block Texture Atlas", 2D) = "white" {}
        _AlphaCutout("Apha Cutout", Range(0.0, 1.0)) = 0.5
    }

    SubShader
    {
        Tags
        {
            "Queue"="AlphaTest" "IgnoreProjector"="True" "RenderType"="TransparentCutout" "RenderPipeline"="UniversalPipeline"
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

            #include "Includes/VoxelCommon.hlsl"
            #include "Includes/VoxelLighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _AlphaCutout;
            CBUFFER_END

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

                // Remove pixels from the alpha channel below a certain threshold.
                clip(col.a - _AlphaCutout);

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
