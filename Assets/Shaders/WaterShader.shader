Shader "Minecraft/Water Shader"
{
    Properties
    {
        _MainTex("First Texture", 2D) = "white" {}
        _SecondaryTex("Secondary Texture", 2D) = "white" {}
        _Transparency("Transparency", Range(0.0, 1.0)) = 0.5
        _MoveSpeed("Move Speed", Range(0.0, 1.0)) = 0.5
        _AnimationChange("Animation Change Speed", Range(0.0, 1.0)) = 0.5
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent" "RenderType"="Transparent"
        }
        LOD 100
        Lighting Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

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
            sampler2D _SecondaryTex;
            float _Transparency;
            float _MoveSpeed;
            float _AnimationChange;
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
                i.uv.x += (_SinTime.x * _MoveSpeed);
                
                fixed4 tex1 = tex2D(_MainTex, i.uv);
                fixed4 tex2 = tex2D(_SecondaryTex, i.uv);

                fixed4 col = lerp(tex1, tex2, 0.5 + (_SinTime.w * _AnimationChange));


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
                clip(col.a - _Transparency);

                // Darken block based on block light level.
                col = lerp(col, col * .10, shade);

                col.a = _Transparency;

                return col;
            }
            ENDCG
        }
    }
}