Shader "Hidden/Editor/BlockPreview"
{
    // Editor preview shader for standard and transparent block types.
    // Uses the same shared includes as the game shaders (VoxelCommon + VoxelLighting)
    // but with hardcoded daylight defaults instead of runtime global uniforms.
    Properties
    {
        _MainTex ("Texture Atlas", 2D) = "white" {}
        [HideInInspector] _ForceOpaque("Force Opaque", Float) = 0.0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline"="UniversalPipeline"
            "Queue"="Geometry"
        }

        Pass
        {
            Name "EditorPreview"
            // No LightMode tag — defaults to SRPDefaultUnlit, guaranteed to work in preview cameras

            Cull Back
            ZWrite On
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vertFunction
            #pragma fragment fragFunction

            #include "../Includes/VoxelCommon.hlsl"
            #include "../Includes/VoxelLighting.hlsl"

            float _ForceOpaque;

            VoxelV2F vertFunction(VoxelAppdata v)
            {
                return VoxelVert(v);
            }

            half4 fragFunction(VoxelV2F i) : SV_Target
            {
                half4 col = SampleBlockTexture(i.uv);

                // Discard fully transparent pixels (e.g., leaf cutouts)
                clip(col.a - 0.1);

                // Apply voxel lighting with hardcoded editor daylight defaults
                // (VoxelData.MinLightLevel = 0.15, MaxLightLevel = 1.0, full daylight = 1.0)
                col.rgb = ApplyVoxelLighting(col.rgb, i.color.a,
                                             1.0, // globalLight  — full daylight
                                             0.15, // minLight     — VoxelData.MinLightLevel
                                             1.0); // maxLight     — VoxelData.MaxLightLevel

                // Multiply by vertex RGB (supports BlockIconGenerator isometric shadows)
                col.rgb *= i.color.rgb;

                // Dynamically force alpha to 1.0 when _ForceOpaque is enabled (e.g. for UI icons)
                col.a = lerp(col.a, 1.0, _ForceOpaque);

                return col;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
