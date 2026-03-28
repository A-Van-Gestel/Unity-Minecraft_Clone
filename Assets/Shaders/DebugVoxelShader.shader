Shader "Minecraft/Debug/VoxelVisualizer"
{
    Properties
    {
        // No properties needed, we will use vertex colors.
    }
    SubShader
    {
        Tags
        {
            "Queue"="Transparent+100" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline"
        } // Increased queue for good measure
        LOD 100

        Pass
        {
            Name "DebugOverlay"
            Blend SrcAlpha OneMinusSrcAlpha // Standard transparency
            ZWrite Off // Don't write to the depth buffer
            ZTest Always // Always draw this, ignoring what's behind it.
            Cull Off // Render both front and back faces

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct appdata
            {
                float4 vertex : POSITION;
                half4 color : COLOR; // Input color from the mesh
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                half4 color : COLOR; // Pass color to the fragment shader
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = TransformObjectToHClip(v.vertex.xyz);
                o.color = v.color;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                // The final color is just the vertex color from the mesh.
                return i.color;
            }
            ENDHLSL
        }
    }
}
