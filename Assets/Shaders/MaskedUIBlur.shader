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

        // Written by Mask / RectMask2D through the UI's material machinery, never authored by hand.
        // Without them a blurred graphic ignores every mask it sits under.
        [HideInInspector] _StencilComp ("Stencil Comparison", Float) = 8
        [HideInInspector] _Stencil ("Stencil ID", Float) = 0
        [HideInInspector] _StencilOp ("Stencil Operation", Float) = 0
        [HideInInspector] _StencilWriteMask ("Stencil Write Mask", Float) = 255
        [HideInInspector] _StencilReadMask ("Stencil Read Mask", Float) = 255
        [HideInInspector] _ColorMask ("Color Mask", Float) = 15
        [HideInInspector] _ClipRect ("Clip Rect", Vector) = (-32767, -32767, 32767, 32767)
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "UIBlurSample"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            // Enabled by RectMask2D and by Mask with "Show Mask Graphic" off, respectively.
            #pragma multi_compile_local __ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local __ UNITY_UI_ALPHACLIP

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct appdata_t
            {
                float4 vertex : POSITION;
                float2 texcoord : TEXCOORD0;
                float4 color : COLOR;
            };

            // 3 interpolators, or 4 with clipping enabled — COLOR occupies a slot like a TEXCOORD
            // (SHADER_CONVENTIONS.md 1.3). Well inside the 15-interpolator budget.
            struct v2f
            {
                float4 vertex : SV_POSITION;
                float4 screenPos : TEXCOORD0;
                float2 uvmain : TEXCOORD1;
                float4 color : COLOR;
                #if UNITY_UI_CLIP_RECT
                float4 localPosition : TEXCOORD2;
                #endif
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
                float4 _ClipRect;
                float _Size;
            CBUFFER_END

            // Declared locally rather than pulled from UnityUI.cginc: that include belongs to the
            // Built-in pipeline and mixing it into a URP HLSL program is not worth three lines.
            half Get2DClipping(float2 position, float4 clipRect)
            {
                half2 inside = step(clipRect.xy, position.xy) * step(position.xy, clipRect.zw);
                return inside.x * inside.y;
            }

            v2f vert(appdata_t v)
            {
                v2f o;
                o.vertex = TransformObjectToHClip(v.vertex.xyz);
                o.screenPos = ComputeScreenPos(o.vertex);
                o.uvmain = TRANSFORM_TEX(v.texcoord, _MainTex);
                o.color = v.color;
                #if UNITY_UI_CLIP_RECT
                // Untransformed on purpose: _ClipRect is authored in the canvas's own space, which is
                // what the UI feeds in as the vertex position.
                o.localPosition = v.vertex;
                #endif
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                float2 screenUV = i.screenPos.xy / i.screenPos.w;

                // Sample the pre-blurred screen texture from UIBlurRendererFeature
                half4 blurred = SAMPLE_TEXTURE2D(_UIBlurTexture, sampler_UIBlurTexture, screenUV);

                // Material tints first, then the UI vertex color scales the whole panel — so fading a
                // panel out fades its additive term too, rather than leaving a glow behind.
                half4 result = half4(
                    blurred.r * _MultiplyColor.r + _AdditiveColor.r,
                    blurred.g * _MultiplyColor.g + _AdditiveColor.g,
                    blurred.b * _MultiplyColor.b + _AdditiveColor.b,
                    SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uvmain).a
                );

                result *= i.color;

                #if UNITY_UI_CLIP_RECT
                result.a *= Get2DClipping(i.localPosition.xy, _ClipRect);
                #endif

                #if UNITY_UI_ALPHACLIP
                clip(result.a - 0.001);
                #endif

                return result;
            }
            ENDHLSL
        }
    }
}
