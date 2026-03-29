Shader "Hidden/UI/KawaseBlur"
{
    Properties
    {
        _BlitTexture("Source", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        LOD 100
        ZTest Always ZWrite Off Cull Off

        Pass
        {
            Name "KawaseBlur"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            half _BlurOffset;

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float2 texelSize = _BlitTexture_TexelSize.xy;
                half offset = _BlurOffset;

                // Kawase blur: sample 4 corners at increasing offsets
                // This is significantly more efficient than a full Gaussian kernel
                half4 col = 0;
                col += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2(-offset - 0.5, -offset - 0.5) * texelSize);
                col += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2(-offset - 0.5,  offset + 0.5) * texelSize);
                col += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2( offset + 0.5, -offset - 0.5) * texelSize);
                col += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2( offset + 0.5,  offset + 0.5) * texelSize);
                col *= 0.25;

                return col;
            }
            ENDHLSL
        }
    }
}
