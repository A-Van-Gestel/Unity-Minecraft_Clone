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
            #pragma target 3.5

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

            // Below this length the sunward (or across-disc) vector carries no usable direction and
            // normalizing it would produce a NaN. Guards the disc centre and the collinear sun.
            static const float SUNWARD_DEGENERATE_EPSILON = 1e-5;

            // Star point size and placement within its cell, in cell-relative units.
            static const float STAR_JITTER = 0.55;
            static const float STAR_RADIUS_MIN = 0.16;
            static const float STAR_RADIUS_VARIANCE = 0.13;

            // Moon surface. The night side stays opaque and nearly black — it must still occlude the
            // stars behind it, or the disc reads as a hole rather than a body.
            static const float3 MOON_NIGHT_SIDE = float3(0.018, 0.020, 0.030);

            // MOON_NIGHT_SIDE stands in for earthshine, exaggerated roughly 200x over the real thing so
            // the unlit side reads against a night sky. It fades out as the sky brightens, because by day
            // it is the only reason the unlit disc is visible and it can only ever push the disc BRIGHTER
            // than the sky — a glowing spot that takes the eye, the opposite of the quiet detail a
            // daytime moon should be.
            //
            // Keyed to the SKY's own brightness rather than to sun elevation, because sky brightness is
            // what earthshine actually competes with — the reason earthshine is a night-only phenomenon
            // in reality. Sun elevation is a poor proxy at exactly the wrong moment: at sunrise the sun
            // sits near 0 while the sky has already reached 0.5 luminance, so an elevation-keyed fade
            // still had earthshine at 65% against a sky eighty times brighter than night.
            // Low because earthshine (0.021) is the same order as TWILIGHT sky values (0.02-0.08): any
            // partial earthshine left in that band still reads as a glow. Full strength survives only
            // where the sky is genuinely night-dark.
            static const float MOON_AIRLIGHT_REFERENCE = 0.03;

            // What remains by day is a faint SILHOUETTE: the disc carries slightly less of the sky's own
            // airlight than the sky beside it. Physically the unlit moon is exactly sky-coloured, so this
            // is the smallest readable departure from correct — and darker-than-sky is noticed calmly,
            // where brighter-than-sky demands attention.
            static const float MOON_DAY_SILHOUETTE = 0.06;
            static const float3 MOON_MARIA = float3(0.72, 0.73, 0.70);
            static const float3 MOON_HIGHLAND = float3(0.97, 0.97, 0.93);
            static const float MOON_LIMB_DARKENING = 0.22;

            // Above this |y| the moon is treated as vertical, and its surface frame swings to a
            // different reference axis. Deliberately loose: the failure is exact collinearity only, so
            // the threshold just has to catch it without ever sitting near a value the sky produces.
            static const float MOON_FRAME_VERTICAL_LIMIT = 0.999;

            // Fine mottling over the maria patches: breaks up the flat fill without inventing structure
            // the eye can resolve at this angular size.
            static const float MOON_NOISE_FREQUENCY = 4.5;
            static const float MOON_MOTTLE_STRENGTH = 0.42;

            // Craters, as one hashed disc per cell of a grid across the moon's face. Threshold leaves
            // most cells empty, so the field reads as scattered rather than tiled.
            static const float MOON_CRATER_DENSITY = 7.0;
            static const float MOON_CRATER_THRESHOLD = 0.55;
            static const float MOON_CRATER_JITTER = 0.7;
            static const float MOON_CRATER_RADIUS_MIN = 0.18;
            static const float MOON_CRATER_RADIUS_VARIANCE = 0.30;
            static const float MOON_CRATER_DEPTH = 0.30;
            static const float MOON_CRATER_RIM = 0.22;
            static const float MOON_CRATER_FLOOR_EDGE = 0.62;

            // Sun disc. Limb darkening is real and reddens toward the edge, because the effect is
            // stronger at short wavelengths — so the rim gets its own, warmer color rather than a
            // uniformly scaled one.
            static const float3 SUN_CORE_COLOR = float3(1.0, 0.97, 0.86);
            static const float3 SUN_LIMB_COLOR = float3(1.0, 0.86, 0.66);
            static const float SUN_LIMB_DARKENING = 0.18;

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

            // Smoothly interpolated 2D value noise, built on the same hash as the stars so the shader
            // still needs no texture of any kind.
            float ValueNoise2D(float2 p)
            {
                float2 cell = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);

                float a = Hash31(float3(cell, 0.0));
                float b = Hash31(float3(cell + float2(1.0, 0.0), 0.0));
                float c = Hash31(float3(cell + float2(0.0, 1.0), 0.0));
                float d = Hash31(float3(cell + float2(1.0, 1.0), 0.0));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            // Three octaves of mottling across the moon's face, normalized back to roughly [0, 1].
            float MoonMottling(float2 face)
            {
                float value = ValueNoise2D(face * MOON_NOISE_FREQUENCY);
                value += 0.5 * ValueNoise2D(face * (MOON_NOISE_FREQUENCY * 2.03) + 17.0);
                value += 0.25 * ValueNoise2D(face * (MOON_NOISE_FREQUENCY * 4.07) + 41.0);
                return value / 1.75;
            }

            // Crater shading as a signed multiplier offset: a darkened floor with a brighter raised rim.
            // A plain dark disc reads as a stain; it is the rim that makes it read as a crater.
            float MoonCraters(float2 face)
            {
                float2 scaled = face * MOON_CRATER_DENSITY;
                float2 baseCell = floor(scaled);
                float shading = 0.0;

                // The 3x3 neighborhood, so a crater straddling a cell boundary is not sliced in half.
                for (int y = -1; y <= 1; y++)
                {
                    for (int x = -1; x <= 1; x++)
                    {
                        float2 cell = baseCell + float2(x, y);
                        float3 seed = float3(cell, 0.0);
                        if (Hash31(seed + 5.0) < MOON_CRATER_THRESHOLD) continue;

                        float2 jitter = float2(Hash31(seed + 13.0), Hash31(seed + 29.0)) - 0.5;
                        float2 center = cell + 0.5 + jitter * MOON_CRATER_JITTER;
                        float radius = MOON_CRATER_RADIUS_MIN + MOON_CRATER_RADIUS_VARIANCE * Hash31(seed + 47.0);

                        float distance = length(scaled - center) / max(radius, 1e-4);
                        if (distance > 1.0) continue;

                        float floorTerm = -MOON_CRATER_DEPTH * (1.0 - smoothstep(0.0, MOON_CRATER_FLOOR_EDGE, distance));
                        float rimTerm = MOON_CRATER_RIM
                                        * smoothstep(MOON_CRATER_FLOOR_EDGE, 0.9, distance)
                                        * (1.0 - smoothstep(0.9, 1.0, distance));
                        shading += floorTerm + rimTerm;
                    }
                }

                return shading;
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

                // The gradient alone, kept before the stars are added: this is the airlight the celestial
                // discs are seen THROUGH. It must exclude the stars — adding a star-bearing colour behind
                // the moon would let stars shine out of the disc, which is the transparency bug again by
                // another route.
                half3 skyAirlight = color;

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
                    float alongSun = (sunwardLen > SUNWARD_DEGENERATE_EPSILON && acrossLen > SUNWARD_DEGENERATE_EPSILON)
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
                    //
                    // The third degeneracy on this disc, and the one with no guard until now: world up
                    // is collinear with the moon at the zenith, where the cross product is exactly zero
                    // and the frame collapses — measured, the disc loses every marking and renders as
                    // flat grey. Only EXACT collinearity does it; a ten-thousandth of a degree away is
                    // indistinguishable from the 45-degree case, so this is a measure-zero case a
                    // continuous clock is unlikely to land on, guarded because it costs one compare.
                    float3 upReference = abs(_MoonDirection.y) > MOON_FRAME_VERTICAL_LIMIT
                        ? float3(0.0, 0.0, 1.0)
                        : float3(0.0, 1.0, 0.0);
                    float3 moonRight = normalize(cross(upReference, _MoonDirection));
                    float3 moonUp = cross(_MoonDirection, moonRight);
                    float2 face = float2(dot(acrossRaw, moonRight), dot(acrossRaw, moonUp)) / radians(_MoonAngularRadius);

                    // Four soft dark patches read as maria at a glance and cost four distance tests —
                    // cheaper and more deliberate-looking than noise at this angular size.
                    float maria = 1.0;
                    maria -= 0.30 * smoothstep(0.42, 0.0, length(face - float2(-0.22, 0.18)));
                    maria -= 0.24 * smoothstep(0.34, 0.0, length(face - float2(0.26, 0.31)));
                    maria -= 0.18 * smoothstep(0.30, 0.0, length(face - float2(0.11, -0.33)));
                    maria -= 0.14 * smoothstep(0.24, 0.0, length(face - float2(-0.35, -0.27)));

                    // Mottling perturbs which terrain type a point reads as, rather than being painted
                    // over the top — so the patch edges break up instead of staying as clean circles.
                    maria -= MOON_MOTTLE_STRENGTH * (MoonMottling(face) - 0.5);

                    float3 litSurface = lerp(MOON_MARIA, MOON_HIGHLAND, saturate(maria));
                    litSurface *= saturate(1.0 + MoonCraters(face));
                    litSurface *= 1.0 - MOON_LIMB_DARKENING * radial * radial;

                    // Detail multiplies into the LIT surface only, and the composite below still uses the
                    // mask alone, so the disc's opacity is preserved by construction rather than by
                    // retesting it.
                    float daylight = saturate(dot(float3(skyAirlight), float3(0.2126, 0.7152, 0.0722))
                                              / MOON_AIRLIGHT_REFERENCE);
                    float3 nightSide = MOON_NIGHT_SIDE * (1.0 - daylight);
                    float3 surface = lerp(nightSide, litSurface, lit);

                    // Atmosphere in front of the disc, as ONE model in two halves rather than two models
                    // of the same air. Getting this wrong is what made a low moon glow at sunrise: haze
                    // blended the disc toward the fog colour and airlight was then added on top, so the
                    // air was paid for twice and the disc read 1.24 against a 0.60 sky.
                    //
                    // Extinction — near the horizon the line of sight is long and the disc's OWN light is
                    // scattered out of it.
                    surface *= 1.0 - hazeAmount;

                    // Airlight — what that same air scatters back IN. It is the whole reason a daylight
                    // new moon disappears (the unlit side ends up sky coloured, as in reality) and a
                    // daytime gibbous reads as a pale disc rather than a hole punched in the sky. Untuned:
                    // a sum has no strength to pick. The one deliberate departure is carrying a fraction
                    // less of it than the open sky does, which is the quiet silhouette.
                    //
                    // Together they make a fully hazed disc resolve to exactly the sky beside it, so the
                    // moon settles into the horizon rather than standing out against it. Added and never
                    // blended toward, so the disc still writes over the sky at full mask and the opacity
                    // that occludes stars is untouched.
                    //
                    // NOT scaled by hazeAmount, which is the deliberate half of this. Scaling it would be
                    // the more physical reading — no air in the sight line, no airlight — but it is what
                    // makes the unlit disc go black overhead, the hole in the sky described above. The
                    // cost is carried by the LIT side: a daytime full moon brightens with elevation
                    // (~3x between horizon and zenith) because it keeps its own reflectance AND takes the
                    // full sky airlight on top. Accepted; B7 in the Sky Render suite pins it, so a future
                    // haze-scaling of this term reds a test rather than silently changing the look.
                    surface += float3(skyAirlight) * (1.0 - daylight * MOON_DAY_SILHOUETTE);

                    // Composited by the disc mask ALONE, so the whole disc is opaque. Folding `lit` into
                    // the mask made the unlit side transparent, letting the sky and stars show through.
                    color = lerp(color, half3(surface), moonMask);
                }

                // --- Sun -----------------------------------------------------------------------
                // Drawn last so it wins wherever it overlaps the moon (an eclipse reads as the sun).
                float sunMask = DiscMask(viewDir, _SunDirection, _SunAngularRadius);
                if (sunMask > 0.0)
                {
                    // Radial position on the disc, 0 at the centre and 1 at the limb — the moon's
                    // measure, applied to the sun.
                    float sunRadial = acos(clamp(dot(viewDir, _SunDirection), -1.0, 1.0))
                                      / radians(_SunAngularRadius);

                    // Squared so the fall-off stays flat across most of the disc and steepens near the
                    // edge, which is what limb darkening actually looks like; a linear ramp reads as a
                    // gradient smeared over the whole face.
                    float limb = saturate(sunRadial * sunRadial);
                    half3 sunColor = lerp(half3(SUN_CORE_COLOR), half3(SUN_LIMB_COLOR), limb);
                    sunColor *= 1.0 - SUN_LIMB_DARKENING * limb;

                    sunColor = lerp(sunColor, half3(_VoxelFogColor), hazeAmount);
                    color = lerp(color, sunColor, sunMask);
                }

                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
