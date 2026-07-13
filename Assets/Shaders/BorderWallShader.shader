Shader "Minecraft/BorderWall"
{
    Properties
    {
        _Color ("Tint", Color) = (1, 0.78, 0.16, 0.18)
        _MainTex ("Overlay (optional)", 2D) = "white" {}
        _BandScale ("Band Scale", Float) = 0.06
        _ScrollSpeed ("Scroll Speed", Float) = 0.15
        _FadeStart ("Fade Start (m)", Float) = 40
        _FadeRange ("Fade Range (m)", Float) = 60
    }
    SubShader
    {
        Tags
        {
            "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline"
        }

        // Vertical fence seen from both sides; translucent, no depth write.
        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "BorderWallPass"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float4 _MainTex_ST;
                float _BandScale;
                float _ScrollSpeed;
                float _FadeStart;
                float _FadeRange;
            CBUFFER_END

            struct appdata
            {
                float4 vertex : POSITION;
                // uv.x = world distance along the face, uv.y = world height (set by BorderWallRenderer).
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float3 worldPos = TransformObjectToWorld(v.vertex.xyz);
                o.worldPos = worldPos;
                o.vertex = TransformWorldToHClip(worldPos);
                o.uv = v.uv;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                // World-anchored so the pattern doesn't swim as the quad slides with the player.
                float bars = 0.5 + 0.5 * sin(i.uv.x * _BandScale * 6.28318530718);
                float scroll = frac(i.uv.y * _BandScale - _Time.y * _ScrollSpeed);
                float wave = smoothstep(0.0, 0.15, scroll) * smoothstep(1.0, 0.85, scroll);
                float pattern = saturate(bars * 0.4 + wave * 0.8 + 0.2);

                // Distance fade mimics the terrain draw distance so the far edge dissolves.
                float dist = distance(i.worldPos, _WorldSpaceCameraPos);
                float distFade = saturate(1.0 - (dist - _FadeStart) / max(_FadeRange, 0.0001));

                half4 overlay = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, TRANSFORM_TEX(i.uv, _MainTex));

                half4 col = _Color;
                col.rgb *= overlay.rgb;
                col.a *= pattern * distFade * overlay.a;
                return col;
            }
            ENDHLSL
        }
    }
}
