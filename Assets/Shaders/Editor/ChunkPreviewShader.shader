Shader "Hidden/Editor/ChunkPreview"
{
    // Minimal vertex-color shader for the 3D chunk preview window.
    // Renders section meshes using per-vertex lighting colors baked by MeshGenerationJob.
    Properties
    {
        _Color ("Tint", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline"="UniversalPipeline"
            "Queue"="Geometry"
        }

        Pass
        {
            Name "ChunkPreview"

            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            half4 _Color;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float4 color      : COLOR;
                float3 normal     : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4  color      : COLOR;
                half   lighting   : TEXCOORD0;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS);
                o.color = v.color;

                // Simple directional light from upper-right for depth cues
                float3 worldNormal = TransformObjectToWorldNormal(v.normal);
                o.lighting = saturate(dot(worldNormal, normalize(float3(0.3, 0.8, -0.5)))) * 0.4 + 0.6;

                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                // Vertex color alpha encodes voxel light level (sunlight/blocklight)
                // RGB channels carry ambient occlusion or tint
                half lightLevel = saturate(i.color.a * 1.2);
                half3 col = lightLevel * i.lighting * _Color.rgb;
                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }

    FallBack "Sprites/Default"
}
