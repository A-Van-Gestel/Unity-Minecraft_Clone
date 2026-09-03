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
            #pragma target 4.5

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

            // Sun extinction (SN-1). Air scatters SHORT wavelengths out of the sight line hardest, and
            // that — not a dimming — is why a setting sun reddens: by the time the light has crossed a
            // horizon-length column its blue is gone and its red is largely still there. A single scalar
            // haze cannot express that at all; it can only wash the disc toward one colour.
            //
            // Ratios rather than physical Rayleigh coefficients, because the sky these have to agree
            // with is AUTHORED rather than simulated (design doc §3.3). Picked against a render.
            static const float3 SUN_EXTINCTION_BETA = float3(0.55, 1.30, 2.40);

            // Scales the whole optical depth. Separated from the ratios above so the reddening can be
            // strengthened without re-balancing the channels against each other.
            static const float SUN_EXTINCTION_DEPTH = 1.6;

            // How the sun's own optical depth falls off with its elevation. Steeper than
            // HORIZON_HAZE_FALLOFF, and deliberately NOT that constant, because the two model different
            // things: the shared one is calibrated for VEILING — how much air hides a body, which is
            // also what the moon and the disc haze use — while this stands in for AIRMASS, which barely
            // doubles between the zenith and 30 degrees and only climbs steeply in the last few.
            // Reusing the veiling curve put the sun at 18% of full optical depth at 30 degrees, about
            // three times too much, and it rendered as an orange ball against a blue sky where a real
            // sun is still near-white. At the horizon both curves reach the same place, so sunrise and
            // sunset are unaffected.
            static const float SUN_PATH_FALLOFF = 5.0;

            // Sun aureole (SN-0) — the forward-scattered halo of sunlight around the disc. Its
            // absence is why a correct disc still read as a sticker: the gradient above is a
            // function of view ELEVATION alone, so the air beside the sun rendered identically to
            // the air 180 degrees away from it.
            //
            // TWO cosine-power lobes, not one. A single lobe cannot be both tight enough to hug a
            // 1.5-degree disc and broad enough to be the tens-of-degrees sky brightening a real
            // aureole is; whichever width is picked, the other half of the effect goes missing.
            static const float AUREOLE_CORE_EXPONENT = 900.0;
            static const float AUREOLE_CORE_STRENGTH = 0.35;
            static const float AUREOLE_HALO_EXPONENT = 8.0;
            static const float AUREOLE_HALO_STRENGTH = 0.12;

            // A third and tightest lobe: the GLARE that makes the sun read as a light source rather
            // than a bright patch. This is where the sun's glow is produced, and deliberately so — an
            // HDR disc driving URP's post-process bloom was built and refuted, because that bloom is a
            // single global instance whose radius is sized for the block emitters and cannot also suit
            // a 3-degree disc (design doc §7.3). Produced here, the falloff is angular, costs no HDR
            // headroom, and shares no tuning with anything else in the world.
            //
            // Broader than the core lobe and much tighter than the halo, so the three together read as
            // one falloff rather than three rings: roughly 0.73 of the blend at the disc's rim, 0.45 a
            // degree and a half beyond it, 0.16 at six degrees.
            static const float AUREOLE_GLARE_EXPONENT = 400.0;
            static const float AUREOLE_GLARE_STRENGTH = 0.40;

            // Aerosol broadens the aureole, so only the WIDE lobe takes the haze boost. Applying it
            // to the core as well just brightens the few degrees the sun disc draws over anyway.
            static const float AUREOLE_HAZE_BOOST = 1.4;

            // The aureole IS sunlight, so it warms as the sun reddens: two bright warm tints, picked
            // by sun elevation. Both are near the top of the LDR range on purpose — the glow is
            // BLENDED toward rather than added (see the frag), and a blend can only pull the sky
            // toward its target, so a dim target would darken the sky near the sun instead of
            // lighting it. Blue sits well below red in both, which is what makes the sky whiten
            // toward the sun the way a real aureole does.
            // The aureole IS sunlight, scattered — so its colour is the SUN's OWN colour after the same
            // extinction SN-1 applies to the disc, renormalized back to full brightness. That makes glow
            // and disc redden together by construction, with no second palette to keep in step.
            //
            // Two earlier attempts are worth not repeating. A fixed pale tint (1.00, 0.82, 0.62), R:B
            // 1.61, sat against a dusk sky at R:B 4.60 and — because the blend peaks at the disc centre —
            // washed the sun's reddening from R:B 4.36 back to 2.09, spending SN-1's whole effect on a
            // constant. Deriving it from the authored `_HorizonColor` instead fixed dusk but broke
            // mid-morning: at 10 degrees elevation the horizon global has already turned pale blue while
            // the sun is still visibly warm, so the disc rendered BLUER than neutral (R:B 0.95).
            // Transmitted sunlight is warm at exactly the times the sun is, which neither of those was.
            //
            // Renormalized rather than used raw: extinction makes the tint dark as well as red, and a
            // blend toward a dark target would dim the glow at the hour it should be strongest. Dividing
            // by the peak channel keeps the hue and discards the dimming, which the disc's own
            // extinction has already accounted for.
            static const float AUREOLE_TINT_EPSILON = 1e-4;

            // Pulls the tint back toward white so the glow stays a glow rather than becoming a colour
            // wash. At 0 the aureole takes the full transmitted hue, which at dusk is nearly monochrome
            // red; the sky around a real setting sun is warm but not that saturated.
            //
            // The value trades colour against SHAPE, measured across a full authored day. Lowering it
            // deepens the disc (dusk red:blue 3.04 here, 4.18 at 0.20, 5.57 at 0.10) but also flattens
            // it, because the blend peaks at the disc centre and drags the centre toward the rim: at
            // 0.15 the horizon limb gradient falls to 1.7% of the disc's luminance against 4.7% here,
            // giving up the limb detail the disc gained in the RF-2 polish arc. This value keeps the
            // shaded ball, and the disc is still more saturated than the sky beside it (3.04 vs 2.73).
            static const float AUREOLE_TINT_DESATURATE = 0.35;

            // Sine of sun elevation over which the aureole dies below the horizon. Non-zero because
            // this IS the twilight afterglow — cutting at exactly y = 0 removes the glow that
            // rightly outlives the disc. Some fade is mandatory: saturate(dot()) alone would light
            // the sky around the ANTI-sun point at midnight.
            static const float AUREOLE_TWILIGHT_FADE = 0.25;

            // pow(0, n) is exp(n * log(0)) and not every compiler resolves that to zero rather than
            // a NaN. Half the sky sits at exactly zero here, so the guard is not hypothetical.
            static const float AUREOLE_POW_EPSILON = 1e-4;

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

                // Haze veiling the MOON, strongest at the horizon, plus the aerosol boost on the
                // aureole's widest lobe. Gated on fog being on, so disabling fog leaves those crisp.
                //
                // The sun deliberately does NOT take this gate — it runs on `sunPathHaze` below, which
                // is ungated. Distance Fog is a view-distance setting; the sun's colour is a property
                // of the atmosphere, and tying the two would make a sunset sun render near-white
                // against the authored orange horizon whenever a player turned fog off. The moon keeps
                // the gate because its own model is pinned by B6/B7 and RF-2's locked decisions.
                // B11 asserts the sun's independence, so re-gating it reds a test rather than quietly
                // changing the look.
                float hazeAmount = 0.0;
                if (_VoxelFogRange.y > _VoxelFogRange.x)
                    hazeAmount = pow(saturate(1.0 - viewDir.y), HORIZON_HAZE_FALLOFF) * HORIZON_HAZE_STRENGTH;

                // --- Sun aureole ---------------------------------------------------------------
                // Added HERE, before the airlight is captured, and that placement is load-bearing:
                // the moon reads skyAirlight to settle into the sky behind it, so a moon near the
                // sun must see the glow too. Adding the aureole after the discs instead would punch
                // the moon out of it as a dark hole.
                float sunward = max(saturate(dot(viewDir, _SunDirection)), AUREOLE_POW_EPSILON);
                float aureole = AUREOLE_GLARE_STRENGTH * pow(sunward, AUREOLE_GLARE_EXPONENT)
                    + AUREOLE_CORE_STRENGTH * pow(sunward, AUREOLE_CORE_EXPONENT)
                    + AUREOLE_HALO_STRENGTH * pow(sunward, AUREOLE_HALO_EXPONENT)
                    * (1.0 + hazeAmount * AUREOLE_HAZE_BOOST);
                aureole *= saturate(1.0 + _SunDirection.y / AUREOLE_TWILIGHT_FADE);

                // Warmth keys on the SUN's own path, not the view's: the glow belongs to the sun, so a
                // low sun reddens the whole halo rather than only the half nearer the horizon.
                //
                // Deliberately UNGATED by fog, unlike `hazeAmount` above — see the note there. This one
                // value feeds both the aureole's tint and the disc's extinction, so the two redden
                // together whatever the Distance Fog setting is.
                float sunPathHaze = pow(saturate(1.0 - _SunDirection.y), SUN_PATH_FALLOFF)
                                    * HORIZON_HAZE_STRENGTH;
                float3 sunTransmittance = exp(-sunPathHaze * SUN_EXTINCTION_DEPTH * SUN_EXTINCTION_BETA);
                float3 transmittedSun = float3(SUN_CORE_COLOR) * sunTransmittance;
                float tintPeak = max(max(transmittedSun.r, transmittedSun.g), transmittedSun.b);
                float3 aureoleTint = transmittedSun / max(tintPeak, AUREOLE_TINT_EPSILON);
                half3 aureoleColor = half3(lerp(aureoleTint, float3(1.0, 1.0, 1.0), AUREOLE_TINT_DESATURATE));
                float aureoleBlend = saturate(aureole);

                // BLENDED, not added. There is almost no headroom to add into: colour grading is LDR
                // with no tonemapper, so anything past 1 clips flat, and the authored sky beside the
                // sun already sits at 0.78-0.88. An additive glow pushed both the sky AND the disc
                // past 1, which read as a white wash around a sun whose limb detail had been clipped
                // away. A blend between two values that are each <= 1 cannot exceed 1 by construction.
                color = lerp(color, aureoleColor, aureoleBlend);

                // The gradient and aureole, kept before the stars are added: this is the airlight the
                // celestial discs are seen THROUGH. It must exclude the stars — adding a star-bearing
                // colour behind the moon would let stars shine out of the disc, which is the
                // transparency bug again by another route.
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

                    // Extinction of the disc's own light, then the airlight that same air scatters
                    // back in — the two-part model §4's moon already uses, replacing a single scalar
                    // blend toward the fog colour.
                    //
                    // Written as a PER-CHANNEL lerp because `own * T + fog * (1 - T)` IS extinction
                    // plus airlight, and in that form both endpoints are <= 1 so the result cannot
                    // clip. The literal "multiply, then add airlight" spelling overflows immediately
                    // here: colour grading is LDR with no tonemapper, and the authored sky beside the
                    // sun already occupies most of the range (design doc §7.1).
                    // Reuses `sunTransmittance` rather than recomputing it: that is the SUN's own path
                    // depth, not the view's. For disc pixels the two are nearly the same direction
                    // anyway, but they must not use different falloff curves or the disc reddens on a
                    // different schedule from the glow around it — sharing the one value makes that
                    // structural instead of a convention two call sites have to keep.
                    sunColor = half3(float3(sunColor) * sunTransmittance
                                     + float3(_VoxelFogColor) * (1.0 - sunTransmittance));

                    // The aureole is air in FRONT of the disc, so the disc is veiled by exactly the
                    // same blend the sky beside it received. Applying it to only one of the two is
                    // what put a hole in the sun: near the horizon the haze above paints the disc
                    // almost pure fog colour, and a sky that then got the glow on top ended up
                    // BRIGHTER than the sun sitting in it. One veil over both preserves whatever
                    // ordering the disc and sky already had, at every elevation and every hour.
                    sunColor = lerp(sunColor, aureoleColor, aureoleBlend);

                    color = lerp(color, sunColor, sunMask);
                }

                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
