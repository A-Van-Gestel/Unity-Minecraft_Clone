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
            float minGlobalLightLevel;
            float maxGlobalLightLevel;

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

                // Remove pixels from the alpha channel below a certain threshold.
                clip(col.a - _AlphaCutout);

                // Darken block based on block light level.
                col = lerp(col, col * .10, shade);

                return col;
            }
            ENDCG
        }
    }
}