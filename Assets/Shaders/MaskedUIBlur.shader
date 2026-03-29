// URP replacement for the legacy GrabPass-based MaskedUIBlur.
// Samples the pre-blurred _UIBlurTexture provided by UIBlurRendererFeature.

Shader "Custom/MaskedUIBlur"
{
    Properties
    {
        _Size ("Blur", Range(0, 30)) = 1
        [HideInInspector] _MainTex ("Masking Texture", 2D) = "white" {}
        _AdditiveColor ("Additive Tint color", Color) = (0, 0, 0, 0)
        _MultiplyColor ("Multiply Tint color", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline"
        }

        Cull Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "UIBlurSample"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct appdata_t
            {
                float4 vertex : POSITION;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float4 screenPos : TEXCOORD0;
                float2 uvmain : TEXCOORD1;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float4 _MainTex_ST;

            // The pre-blurred screen texture provided by UIBlurRendererFeature
            TEXTURE2D(_UIBlurTexture);
            SAMPLER(sampler_UIBlurTexture);

            CBUFFER_START(UnityPerMaterial)
                half4 _AdditiveColor;
                half4 _MultiplyColor;
                float _Size;
            CBUFFER_END

            v2f vert(appdata_t v)
            {
                v2f o;
                o.vertex = TransformObjectToHClip(v.vertex.xyz);
                o.screenPos = ComputeScreenPos(o.vertex);
                o.uvmain = TRANSFORM_TEX(v.texcoord, _MainTex);
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                float2 screenUV = i.screenPos.xy / i.screenPos.w;

                // Sample the pre-blurred screen texture from UIBlurRendererFeature
                half4 blurred = SAMPLE_TEXTURE2D(_UIBlurTexture, sampler_UIBlurTexture, screenUV);

                // Apply tinting (same as original shader)
                half4 result = half4(
                    blurred.r * _MultiplyColor.r + _AdditiveColor.r,
                    blurred.g * _MultiplyColor.g + _AdditiveColor.g,
                    blurred.b * _MultiplyColor.b + _AdditiveColor.b,
                    SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uvmain).a
                );

                return result;
            }
            ENDHLSL
        }
    }
}
