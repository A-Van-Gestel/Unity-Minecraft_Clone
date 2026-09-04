Shader "Hidden/Voxel/UnderwaterOverlay"
{
    SubShader
    {
        Tags
        {
            "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline"
        }
        LOD 100

        // SrcAlpha blending IS lerp(scene, tint, alpha), so the pass never reads the color target and
        // needs no copy of it. Color and alpha both stay per-fragment.
        ZTest Always ZWrite Off Cull Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "UnderwaterOverlay"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            // Stands in for "this direction never leaves the body", so open water is not clamped by the
            // eye query's scan reach. Matches World.UnboundedFluidExtent.
            #define SUBMERSION_UNBOUNDED 1e6

            // Published every frame by World.PublishSubmersionGlobals. A zero _SubmersionColor.a means
            // "the eye is not under a surface", which is what uninitialized globals give — the same
            // fail-safe convention VoxelFog.hlsl uses for its zero-width range.
            half4 _SubmersionColor;
            float4 _SubmersionParams;
            float4 _SubmersionRayParams;
            float4 _SubmersionRayBasisX;
            float4 _SubmersionRayBasisY;
            float4 _SubmersionRayBasisZ;
            float4 _SubmersionBounds;

            /// How far a ray runs before it leaves the fluid body sideways.
            ///
            /// @param direction  One world-space axis component of the ray direction.
            /// @param negative   Distance to the body's edge in that axis' negative direction, in blocks.
            /// @param positive   Distance to the body's edge in that axis' positive direction, in blocks.
            /// @return           Distance along the ray to the exit, or a large value if it never exits.
            float SlabExitDistance(float direction, float negative, float positive)
            {
                // A ray with no travel on this axis can never cross either of its faces.
                if (abs(direction) < 1e-6) return SUBMERSION_UNBOUNDED;

                return (direction > 0.0 ? positive : negative) / abs(direction);
            }

            /// How much of a view ray travels inside the fluid body.
            ///
            /// @param eyeDepth     Signed depth of the eye below the drawn surface; positive submerged.
            /// @param worldRayDir  The ray's world-space direction, normalized.
            /// @param rayDistance  Total length of the ray, to the sampled geometry.
            /// @return             Length of the submerged segment, in blocks.
            float SubmergedRayLength(float eyeDepth, float3 worldRayDir, float rayDistance)
            {
                float rayUpwardness = worldRayDir.y;
                // An eye above the surface fogs nothing, and that is exact, not a simplification: the
                // surface is a plane but the fluid is a body, and a ray that does reach water ENDS at it —
                // the liquid mesh is a closed shell that writes depth.
                if (eyeDepth <= 0.0) return 0.0;

                // Submerged from here on. A level or descending ray never leaves the water through the
                // surface within its own length; a rising one exits where it meets the surface.
                float submerged = rayUpwardness <= 0.0
                                      ? rayDistance
                                      : min(eyeDepth / rayUpwardness, rayDistance);

                // The surface is only the LID, so bound the ray by the four sides too. Depth cannot do it:
                // the boundary face nearest the eye is often inside the near clip plane and never
                // rasterized at all.
                float exitX = SlabExitDistance(worldRayDir.x, _SubmersionBounds.x, _SubmersionBounds.y);
                float exitZ = SlabExitDistance(worldRayDir.z, _SubmersionBounds.z, _SubmersionBounds.w);

                return min(submerged, min(exitX, exitZ));
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // Uniform across the draw, so this branch is free and it skips the depth fetch entirely
                // when there is no fluid at the eye at all.
                half strength = _SubmersionColor.a;
                if (strength <= 0.0h) return half4(0.0h, 0.0h, 0.0h, 0.0h);

                float2 uv = input.texcoord;

                // Screen-space NDC with +Y up. Do NOT compensate for UNITY_UV_STARTS_AT_TOP here —
                // Blit.hlsl's GetFullScreenTriangleTexCoord already flips V on those platforms, and a
                // second flip would fog the sky instead of the water. Baseline B20 measures the sign.
                float2 ndc = uv * 2.0 - 1.0;

                // Distance along the actual view ray, NOT along the camera's forward axis: this is a
                // medium, so a pixel at the screen edge looks through more of it than one at the center.
                float3 viewRay = float3(ndc * _SubmersionRayParams.xy, 1.0);
                float rayLength = length(viewRay);

                float viewZ = LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
                float rayDistance = viewZ * rayLength;

                // Fog the submerged part of THIS ray, not the whole of it. A partly submerged view has
                // rays that leave the water or never enter it, and charging those the full distance is
                // what made the medium switch off wholesale the moment the eye broke the surface.
                float3 cameraDir = viewRay / rayLength;
                float3 worldRayDir = float3(dot(cameraDir, _SubmersionRayBasisX.xyz),
                                            dot(cameraDir, _SubmersionRayBasisY.xyz),
                                            dot(cameraDir, _SubmersionRayBasisZ.xyz));

                float submergedLength =
                    SubmergedRayLength(_SubmersionParams.y, worldRayDir, rayDistance);

                // Beer-Lambert extinction from zero distance, deliberately not VoxelFog.hlsl's XZ-radial
                // pow band: that one conceals the loaded-chunk boundary, this one is the water column.
                // Sky pixels sit at the far plane and saturate here without needing a special case.
                half fog = (half)saturate(1.0 - exp(-_SubmersionParams.x * submergedLength));

                return half4(_SubmersionColor.rgb, fog * strength);
            }
            ENDHLSL
        }
    }
}
