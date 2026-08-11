// Procedural sky for RF-2: a zenith/horizon gradient, sun and moon discs at the directions the
// C# celestial model publishes, and a star field that rides the same celestial sphere.
//
// Every time-varying input arrives as a shader GLOBAL set once per frame from World.PublishSkyGlobals
// (not as material properties), so the one Sky material stays stateless and the model stays testable
// in C#. Defaults below are what an un-driven material renders — a plain daytime sky.
Shader "Minecraft/SkyboxShader"
{
    Properties
    {
        _StarDensity ("Star Density", Range(4, 512)) = 190
        _StarThreshold ("Star Threshold", Range(0.9, 0.9999)) = 0.982
    }

    SubShader
    {
        Tags
        {
            "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" "RenderPipeline"="UniversalPipeline"
        }

        Cull Off
        ZWrite Off
        ZTest LEqual

        Pass
        {
            Name "SkyboxPass"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // For _VoxelFogRange / _VoxelFogColor: the sky hazes only when the world's fog is on.
            #include "Includes/VoxelFog.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _StarDensity;
                float _StarThreshold;
            CBUFFER_END

            // Globals published each frame by World.PublishSkyGlobals (RF-2).
            float3 _SunDirection;
            float3 _MoonDirection;
            float _MoonPhase;
            float4x4 _SkyRotation;
            half4 _ZenithColor;
            half4 _HorizonColor;
            float _SunAngularRadius;
            float _MoonAngularRadius;
            float _StarBrightness;

            // How sharply the horizon color gives way to the zenith color. Higher concentrates the
            // warm band nearer the horizon, which is what makes sunrise read as a band rather than a wash.
            //
            // Applied as 1 - (1 - |y|)^FALLOFF rather than |y|^(1/FALLOFF). Both concentrate color near
            // the horizon, but the latter has INFINITE slope at y = 0 — it packs an eighth of the whole
            // gradient into the first half-degree, which renders as a hard bright line along the horizon.
            static const float HORIZON_FALLOFF = 3.5;

            // How strongly the sun and moon discs are veiled by haze near the horizon. Without this they
            // read as sitting in front of the fog, since the sky is drawn behind everything else.
            static const float HORIZON_HAZE_STRENGTH = 0.92;
            static const float HORIZON_HAZE_FALLOFF = 2.5;

            // Disc edges are feathered by a FRACTION of their own radius, not a fixed angle: a fixed
            // 0.35 degrees is a fifth of the moon's radius, which reads as an out-of-focus blob.
            static const float DISC_EDGE_FEATHER = 0.03;
            static const float DISC_EDGE_FEATHER_MIN = 0.015;

            // Sun elevation (as a sine) over which the stars fade out. Stars are gone by the time the
            // sun reaches the horizon, matching the light curve's twilight ramp.
            static const float STAR_FADE_RANGE = 0.18;

            // Keeps the moon's lit side facing the sun without a second direction global: the terminator
            // is a plane whose normal is the component of the sun direction perpendicular to the moon.
            static const float MOON_TERMINATOR_SOFTNESS = 0.06;

            // Star point size and placement within its cell, in cell-relative units.
            static const float STAR_JITTER = 0.55;
            static const float STAR_RADIUS_MIN = 0.16;
            static const float STAR_RADIUS_VARIANCE = 0.13;

            // Moon surface. The night side stays opaque and nearly black — it must still occlude the
            // stars behind it, or the disc reads as a hole rather than a body.
            static const float3 MOON_NIGHT_SIDE = float3(0.018, 0.020, 0.030);
            static const float3 MOON_MARIA = float3(0.72, 0.73, 0.70);
            static const float3 MOON_HIGHLAND = float3(0.97, 0.97, 0.93);
            static const float MOON_LIMB_DARKENING = 0.22;

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 viewDirWS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                // The skybox mesh is a unit cube around the camera, so object space IS the view ray.
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.viewDirWS = input.positionOS.xyz;
                return output;
            }

            // Cheap 3D value hash — same family as the CL-3 cloud noise, no texture needed.
            float Hash31(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            // Angular mask for a disc of the given radius, softened just enough to anti-alias the rim.
            float DiscMask(float3 viewDir, float3 discDir, float radiusDegrees)
            {
                float angle = degrees(acos(clamp(dot(viewDir, discDir), -1.0, 1.0)));
                float feather = max(radiusDegrees * DISC_EDGE_FEATHER, DISC_EDGE_FEATHER_MIN);
                return 1.0 - smoothstep(radiusDegrees - feather, radiusDegrees + feather, angle);
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 viewDir = normalize(input.viewDirWS);

                // --- Base gradient -------------------------------------------------------------
                // Symmetric about the horizon: below it the same gradient continues, so a player
                // looking down past the world edge sees sky rather than a hard seam.
                float heightFactor = 1.0 - pow(1.0 - saturate(abs(viewDir.y)), HORIZON_FALLOFF);
                half3 color = lerp(_HorizonColor.rgb, _ZenithColor.rgb, heightFactor);

                // Haze veiling the celestial discs, strongest at the horizon. Gated on fog being on, so
                // disabling fog leaves the sky crisp too rather than the two disagreeing.
                float hazeAmount = 0.0;
                if (_VoxelFogRange.y > _VoxelFogRange.x)
                    hazeAmount = pow(saturate(1.0 - viewDir.y), HORIZON_HAZE_FALLOFF) * HORIZON_HAZE_STRENGTH;

                // --- Stars ---------------------------------------------------------------------
                // Sampled in CELESTIAL space, so the field turns with the sky instead of being pinned
                // to the world. Fades out as the sun approaches the horizon.
                float starFade = saturate(-_SunDirection.y / STAR_FADE_RANGE) * _StarBrightness;
                if (starFade > 0.0)
                {
                    float3 celestialDir = mul((float3x3)_SkyRotation, viewDir);
                    float3 scaled = celestialDir * _StarDensity;
                    float3 cell = floor(scaled);

                    // A star is a POINT inside its cell, not the whole cell. Lighting the cell itself
                    // paints axis-aligned squares — which is what made the field read as chunky blocks
                    // and what put a hard-edged grey notch across the moon.
                    float amplitude = smoothstep(_StarThreshold, 1.0, Hash31(cell));
                    if (amplitude > 0.0)
                    {
                        float3 jitter = float3(Hash31(cell + 11.0), Hash31(cell + 23.0), Hash31(cell + 37.0)) - 0.5;
                        float distanceToStar = length((frac(scaled) - 0.5) - jitter * STAR_JITTER);
                        float radius = STAR_RADIUS_MIN + STAR_RADIUS_VARIANCE * Hash31(cell + 53.0);
                        float star = smoothstep(radius, 0.0, distanceToStar) * amplitude;
                        // Vary brightness per star so the field does not read as a uniform dot grid.
                        star *= 0.35 + 0.65 * Hash31(cell + 17.0);
                        color += star * starFade;
                    }
                }

                // --- Moon ----------------------------------------------------------------------
                float moonMask = DiscMask(viewDir, _MoonDirection, _MoonAngularRadius);
                if (moonMask > 0.0)
                {
                    float cosToMoon = clamp(dot(viewDir, _MoonDirection), -1.0, 1.0);

                    // Disc coordinates: radial distance from the centre, 0 at the middle and 1 at the limb.
                    float radial = acos(cosToMoon) / radians(_MoonAngularRadius);

                    // Both of these collapse to a zero-length vector on axis — at the exact centre of the
                    // disc, and (for the sunward one) whenever the sun and moon are collinear, which is
                    // every new moon. Normalizing blindly there yields NaN, so fall back to 0 and let the
                    // phase term alone decide.
                    float3 sunwardRaw = _SunDirection - _MoonDirection * dot(_SunDirection, _MoonDirection);
                    float3 acrossRaw = viewDir - _MoonDirection * cosToMoon;
                    float sunwardLen = length(sunwardRaw);
                    float acrossLen = length(acrossRaw);
                    float alongSun = (sunwardLen > 1e-5 && acrossLen > 1e-5)
                        ? dot(acrossRaw / acrossLen, sunwardRaw / sunwardLen)
                        : 0.0;

                    // The terminator on a lit sphere projects to an ELLIPSE, not a straight line: at
                    // radial offset y from the sun axis it sits at (1 − 2·phase)·sqrt(1 − y²). That
                    // curvature is what makes a quarter moon read as a crescent rather than a half-disc.
                    float alongAxis = radial * alongSun;
                    float perpSquared = saturate(radial * radial - alongAxis * alongAxis);
                    float terminator = (1.0 - 2.0 * _MoonPhase) * sqrt(saturate(1.0 - perpSquared));
                    float lit = smoothstep(-MOON_TERMINATOR_SOFTNESS, MOON_TERMINATOR_SOFTNESS,
                                           alongAxis - terminator);

                    // Disc coordinates in a moon-fixed frame, so the surface markings stay put on the
                    // face instead of sliding across it as the moon crosses the sky.
                    float3 moonRight = normalize(cross(float3(0.0, 1.0, 0.0), _MoonDirection));
                    float3 moonUp = cross(_MoonDirection, moonRight);
                    float2 face = float2(dot(acrossRaw, moonRight), dot(acrossRaw, moonUp)) / radians(_MoonAngularRadius);

                    // Four soft dark patches read as maria at a glance and cost four distance tests —
                    // cheaper and more deliberate-looking than noise at this angular size.
                    float maria = 1.0;
                    maria -= 0.30 * smoothstep(0.42, 0.0, length(face - float2(-0.22, 0.18)));
                    maria -= 0.24 * smoothstep(0.34, 0.0, length(face - float2(0.26, 0.31)));
                    maria -= 0.18 * smoothstep(0.30, 0.0, length(face - float2(0.11, -0.33)));
                    maria -= 0.14 * smoothstep(0.24, 0.0, length(face - float2(-0.35, -0.27)));

                    float3 litSurface = lerp(MOON_MARIA, MOON_HIGHLAND, saturate(maria));
                    litSurface *= 1.0 - MOON_LIMB_DARKENING * radial * radial;
                    float3 surface = lerp(MOON_NIGHT_SIDE, litSurface, lit);

                    // Composited by the disc mask ALONE, so the whole disc is opaque. Folding `lit` into
                    // the mask made the unlit side transparent, letting the sky and stars show through.
                    surface = lerp(surface, float3(_VoxelFogColor), hazeAmount);
                    color = lerp(color, half3(surface), moonMask);
                }

                // --- Sun -----------------------------------------------------------------------
                // Drawn last so it wins wherever it overlaps the moon (an eclipse reads as the sun).
                float sunMask = DiscMask(viewDir, _SunDirection, _SunAngularRadius);
                half3 sunColor = lerp(half3(1.0, 0.97, 0.86), half3(_VoxelFogColor), hazeAmount);
                color = lerp(color, sunColor, sunMask);

                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
