Shader "Minecraft/Lava Shader (Advanced)"
{
    Properties
    {
        _BrightColor("Bright Color (Cracks)", Color) = (1, 0.9, 0.6, 1) // White-Yellow
        _MidColor("Mid Color", Color) = (1, 0.5, 0, 1) // Orange
        _DarkColor("Dark Color (Crust)", Color) = (0.6, 0.1, 0, 1) // Deep Red
        _NoiseScale("Overall Scale", Range(0.1, 10)) = 2.0
        _CellDensity("Cell Density", Range(1, 4)) = 2.5
        _Speed("Flow Speed", Range(0, 2)) = 0.3
        _CrackBrightness("Crack Brightness", Range(0, 3)) = 1.5
        _PulseSpeed("Pulse Speed", Range(0, 5)) = 1.5
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
            #pragma target 3.0 // Upped target for better performance with loops

            #include "UnityCG.cginc"

            // Structs
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
                float3 worldPos : TEXCOORD1;
            };

            // Properties
            fixed4 _BrightColor;
            fixed4 _MidColor;
            fixed4 _DarkColor;
            float _NoiseScale;
            float _CellDensity;
            float _Speed;
            float _CrackBrightness;
            float _PulseSpeed;

            // Global Lighting (from original shader)
            float GlobalLightLevel;
            float minGlobalLightLevel;
            float maxGlobalLightLevel;

            // Simplex Noise (unchanged)
            float3 mod289(float3 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
            float4 mod289(float4 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
            float4 permute(float4 x) { return mod289(((x * 34.0) + 1.0) * x); }
            float4 taylorInvSqrt(float4 r) { return 1.79284291400159 - 0.85373472095314 * r; }

            float snoise(float3 v)
            {
                const float2 C = float2(1.0 / 6.0, 1.0 / 3.0);
                const float4 D = float4(0.0, 0.5, 1.0, 2.0);
                float3 i = floor(v + dot(v, C.yyy));
                float3 x0 = v - i + dot(i, C.xxx);
                float3 g = step(x0.yzx, x0.xyz);
                float3 l = 1.0 - g;
                float3 i1 = min(g.xyz, l.zxy);
                float3 i2 = max(g.xyz, l.zxy);
                float3 x1 = x0 - i1 + C.xxx;
                float3 x2 = x0 - i2 + C.yyy;
                float3 x3 = x0 - D.yyy;
                i = mod289(i);
                float4 p = permute(permute(permute(i.z + float4(0.0, i1.z, i2.z, 1.0)) + i.y + float4(0.0, i1.y, i2.y, 1.0)) + i.x + float4(0.0, i1.x, i2.x, 1.0));
                float n_ = 0.142857142857;
                float3 ns = n_ * D.wyz - D.xzx;
                float4 j = p - 49.0 * floor(p * ns.z * ns.z);
                float4 x_ = floor(j * ns.z);
                float4 y_ = floor(j - 7.0 * x_);
                float4 x = x_ * ns.x + ns.yyyy;
                float4 y = y_ * ns.x + ns.yyyy;
                float4 h = 1.0 - abs(x) - abs(y);
                float4 b0 = float4(x.xy, y.xy);
                float4 b1 = float4(x.zw, y.zw);
                float4 s0 = floor(b0) * 2.0 + 1.0;
                float4 s1 = floor(b1) * 2.0 + 1.0;
                float4 sh = -step(h, float4(0.0, 0.0, 0.0, 0.0));
                float4 a0 = b0.xzyw + s0.xzyw * sh.xxyy;
                float4 a1 = b1.xzyw + s1.xzyw * sh.zzww;
                float3 p0 = float3(a0.xy, h.x);
                float3 p1 = float3(a0.zw, h.y);
                float3 p2 = float3(a1.xy, h.z);
                float3 p3 = float3(a1.zw, h.w);
                float4 norm = taylorInvSqrt(float4(dot(p0, p0), dot(p1, p1), dot(p2, p2), dot(p3, p3)));
                p0 *= norm.x;
                p1 *= norm.y;
                p2 *= norm.z;
                p3 *= norm.w;
                float4 m = max(0.6 - float4(dot(x0, x0), dot(x1, x1), dot(x2, x2), dot(x3, x3)), 0.0);
                m = m * m;
                return 42.0 * dot(m * m, float4(dot(p0, x0), dot(p1, x1), dot(p2, x2), dot(p3, x3)));
            }

            // FBM (Fractional Brownian Motion) function
            float fbm(float3 p)
            {
                float value = 0.0;
                float amplitude = 0.5;
                float frequency = 1.0;
                for (int i = 0; i < 6; i++)
                {
                    value += amplitude * snoise(p * frequency);
                    amplitude *= 0.5;
                    frequency *= 2.0;
                }
                return value;
            }

            // Vertex Shader
            v2f vertFunction(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color;
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            // Fragment Shader
            fixed4 fragFunction(v2f i) : SV_Target
            {
                float t = _Time.y * _Speed;

                // Two noise patterns moving at different speeds to create the crack effect
                float3 p1 = i.worldPos * _NoiseScale + float3(0.0, t, 0.0);
                float3 p2 = i.worldPos * _NoiseScale + float3(0.0, -t * 0.8, 0.0); // Move in opposite direction

                // Calculate FBM for both positions
                float noise1 = fbm(p1 * _CellDensity);
                float noise2 = fbm(p2 * _CellDensity);

                // The difference between the two noise patterns creates the cracks
                // abs() makes sharp ridges, pow() enhances the effect
                float crack_pattern = pow(abs(noise1 - noise2), 2.0) * _CrackBrightness;

                // Base noise for the main body of the lava
                float base_noise = (fbm(p1) + 1.0) * 0.5; // Map to 0-1 range

                // Build the color gradient
                // Start with the dark crust color
                fixed3 col = _DarkColor.rgb;
                // Add the mid-tone orange based on the base noise
                col = lerp(col, _MidColor.rgb, smoothstep(0.3, 0.7, base_noise));
                // Layer the bright cracks on top
                col = lerp(col, _BrightColor.rgb, smoothstep(0.1, 0.35, crack_pattern));

                // Add a subtle pulsing glow to the whole thing
                float pulse = (sin(_Time.y * _PulseSpeed) * 0.5 + 0.5) * 0.2 + 0.9;
                col *= pulse;

                // Apply block lighting (from original shader)
                float shade = (maxGlobalLightLevel - minGlobalLightLevel) * GlobalLightLevel + minGlobalLightLevel;
                shade *= i.color.a;
                shade = clamp(1.0 - shade, minGlobalLightLevel, maxGlobalLightLevel);
                col = lerp(col, col * 0.1, shade);

                return fixed4(col, 1.0);
            }
            ENDCG
        }
    }
}