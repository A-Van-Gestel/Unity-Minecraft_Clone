Shader "Minecraft/CloudShader"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags
        {
            "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline"
        }

        // Overlap strategy (vanilla-Minecraft parity): ZWrite ON despite alpha blending. A farther cloud
        // face drawn after a nearer one Z-fails, and a nearer face drawn after a farther one dominates the
        // blend at the material's high alpha — so per-face shading stays coherent where faces overlap.
        // The alternatives both fail here: a stencil first-fragment-wins guard picks a draw-order-arbitrary
        // face (visibly wrong once faces shade differently), and a depth-prepass + ZTest Equal pair relies
        // on multi-pass execution URP does not guarantee for this shader (clouds vanished entirely).
        ZWrite On
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "CloudPass"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Includes/VoxelLighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
            CBUFFER_END

            // Shader globals (set from C#, outside UnityPerMaterial like the block shaders):
            // SkyLightColor — time-of-day sky tint (hue only — brightness lives in the shade curve).
            // GlobalLightLevel / min / max — day/night cycle inputs to the shared shade curve.
            // _CloudFaceShading — 1 = per-face weights (Fancy), 0 = flat (Fast, all bottom faces).
            // _CloudFadeParams — x = fade start distance (blocks), y = 1 / fade range (Clouds.UpdateClouds).
            half3 SkyLightColor;
            float GlobalLightLevel;
            float minGlobalLightLevel;
            float maxGlobalLightLevel;
            half _CloudFaceShading;
            float4 _CloudFadeParams;

            // Minecraft-style face shading weights, matching the terrain's block-face language.
            static const half FACE_SHADE_TOP = 1.0;
            static const half FACE_SHADE_BOTTOM = 0.7;
            static const half FACE_SHADE_SIDE_X = 0.9;
            static const half FACE_SHADE_SIDE_Z = 0.8;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = TransformObjectToHClip(v.vertex.xyz);
                o.normalWS = TransformObjectToWorldNormal(v.normal);
                o.positionWS = TransformObjectToWorld(v.vertex.xyz);
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                // Cloud faces are axis-aligned, so the dominant normal axis selects the weight
                // (no normalization needed for a sign/threshold test).
                half3 n = i.normalWS;
                half shade =
                    n.y > 0.5 ? FACE_SHADE_TOP : n.y < -0.5 ? FACE_SHADE_BOTTOM : abs(n.x) > 0.5 ? FACE_SHADE_SIDE_X : FACE_SHADE_SIDE_Z;
                shade = lerp(1.0, shade, _CloudFaceShading);

                // Day/night brightness: clouds are fully sky-exposed, so run sunLuminance = 1 through
                // the terrain's shared shade curve, NORMALIZED to its noon value — noon clouds keep the
                // authored _Color exactly, and night darkens by the same relative factor as sky-lit
                // terrain. SkyLightColor alone can't do this: it carries the hue, not the brightness.
                float sunShadow = VoxelLightToShadow(1.0, GlobalLightLevel, minGlobalLightLevel, maxGlobalLightLevel);
                float noonShadow = VoxelLightToShadow(1.0, 1.0, minGlobalLightLevel, maxGlobalLightLevel);
                half dayNight = sunShadow / noonShadow;

                half4 col = _Color;
                col.rgb *= shade * dayNight * SkyLightColor;

                // Fade the cloudscape's outer edge instead of ending in a hard line at the coverage radius.
                float dist = length(i.positionWS.xz - _WorldSpaceCameraPos.xz);
                col.a *= saturate(1.0 - (dist - _CloudFadeParams.x) * _CloudFadeParams.y);

                return col;
            }
            ENDHLSL
        }
    }
}
