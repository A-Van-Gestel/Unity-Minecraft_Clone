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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                // (No per-material properties for this shader)
            CBUFFER_END

            // Global properties set by World.cs via Shader.SetGlobalFloat — must be outside CBUFFER
            float GlobalLightLevel;
            float minGlobalLightLevel;
            float maxGlobalLightLevel;

            v2f vertFunction(appdata v)
            {
                v2f o;

                o.vertex = TransformObjectToHClip(v.vertex.xyz);
                o.uv = v.uv;
                o.color = v.color;

                return o;
            }

            half4 fragFunction(v2f i) : SV_Target
            {
                half4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);


                // Calculate block shade level
                // (0.75 - 0.25) = 0.5  // Total range available
                // 0.5 * 0.4 = 0.2  // Use certain percent of range available
                // 0.2 + 0.25 = 0.45  // Re-add the minimum light value to calculate final shade
                float shade = (maxGlobalLightLevel - minGlobalLightLevel) * GlobalLightLevel + minGlobalLightLevel;
                // Apply block light level onto block shade level
                shade *= i.color.a;
                // 1 = Absulute darkest, so reverse shade so that 1 equels absulute lightest --> 1 - 0.95 = 0.05
                shade = clamp(1 - shade, minGlobalLightLevel, maxGlobalLightLevel);

                // const float localLightLevel = clamp(GlobalLightLevel + i.color.a, 0, 1);

                // Darken block based on block light level.
                col = lerp(col, col * .10, shade);

                // Multiply by vertex RGB to support BlockIconGenerator shadows and tinting
                col.rgb *= i.color.rgb;

                return col;
            }
            ENDHLSL
        }
    }
}
