Shader "Minecraft/Blocks"
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
            "Queue"="AlphaTest" "IgnoreProjector"="True" "RenderType"="TransparentCutout"
        }
        LOD 100
        Lighting Off

        Pass
        {

            CGPROGRAM
            #pragma vertex vertFunction
            #pragma fragment fragFunction
            #pragma target 2.0

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            sampler2D _MainTex;
            float _AlphaCutout;
            float GlobalLightLevel;

            v2f vertFunction(appdata v)
            {
                v2f o;

                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color;

                return o;
            }

            fixed4 fragFunction(v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv);
                const float localLightLevel = clamp(GlobalLightLevel + i.color.a, 0, 1);

                // Remove pixels from the alpha channel below a certain threshold.
                clip(col.a - _AlphaCutout);

                // Darken block based on block light level.
                col = lerp(col, col * .25, localLightLevel);

                return col;
            }
            ENDCG
        }
    }
}