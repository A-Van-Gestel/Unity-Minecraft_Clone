using Data;
using Editor.Validation.Framework;
using Editor.Validation.PhysicsSolver.Framework;
using Helpers;
using Rendering;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Id = Editor.Validation.PhysicsSolver.Framework.TestPhysicsBlockPalette.Id;

namespace Editor.Validation.UnderwaterRender
{
    /// <summary>
    /// UW-4's baselines: the overlay pass's fog arithmetic, the packing that feeds it, and the renderer-asset
    /// wiring that decides whether any of it reaches the screen.
    /// </summary>
    /// <remarks>
    /// B10 is the positive control and must be read before B11–B15, which all conclude from a measured
    /// composite. A harness that draws nothing reads as "no fog", which is indistinguishable from the
    /// pass-through the density-zero and strength-zero scenarios are asserting.
    /// <para>
    /// B16 and B17 need no graphics device on purpose. The render scenarios go INCONCLUSIVE under
    /// <c>-nographics</c>, so if the wiring check were device-gated too, a headless CI run would assert
    /// nothing at all about UW-4.
    /// </para>
    /// </remarks>
    public static partial class UnderwaterRenderValidationSuite
    {
        /// <summary>Camera near plane the overlay scenarios build their depth values against.</summary>
        private const float OVERLAY_NEAR = 0.3f;

        /// <summary>Camera far plane the overlay scenarios build their depth values against.</summary>
        private const float OVERLAY_FAR = 1000f;

        /// <summary>Vertical field of view, in degrees, the scenarios' view-ray basis is built from.</summary>
        private const float OVERLAY_FOV = 60f;

        /// <summary>Aspect ratio the scenarios' view-ray basis is built from.</summary>
        private const float OVERLAY_ASPECT = 16f / 9f;

        /// <summary>A water-like extinction, low enough to leave the fog measurably below saturation.</summary>
        private const float WATER_DENSITY = 0.14f;

        /// <summary>
        /// An eye depth deep enough that every ray in these scenarios is submerged over its whole length.
        /// </summary>
        /// <remarks>
        /// Lets the fog scenarios measure the <i>full</i> ray distance without also exercising the
        /// surface-crossing solve, which <c>B19</c> owns. The steepest ray here rises at about 0.37 per
        /// unit traveled, so a hundred blocks of depth puts the crossing far past the sampled geometry.
        /// </remarks>
        private const float DEEP_EYE_DEPTH = 100f;

        /// <summary>
        /// An eye a hair <b>below</b> the surface — the half-submerged case a waterline splits on.
        /// </summary>
        /// <remarks>
        /// Deliberately tiny rather than zero. Zero is not submerged (<c>IsSubmerged</c> wants a positive
        /// depth), and it has to stay small enough that a rising ray's short submerged segment fogs by less
        /// than <see cref="COLOR_EPSILON"/>, so "the upper half is clear" stays a real assertion.
        /// </remarks>
        private const float AT_SURFACE_DEPTH = 0.002f;

        /// <summary>An eye standing clear of the surface, as when wading in a shallow pool.</summary>
        private const float ABOVE_SURFACE_DEPTH = -0.3f;

        /// <summary>Path of the renderer asset whose feature order B17 reads back.</summary>
        private const string RENDERER_ASSET_PATH = "Assets/settings/Rendering/VoxelEngine-URP-Renderer.asset";

        /// <summary>
        /// Backdrop for the scenarios that <b>measure</b> fog.
        /// </summary>
        /// <remarks>
        /// Black against a white tint makes the composite <c>lerp(0, 1, a)</c>, so a read channel <i>is</i>
        /// the alpha the shader produced — no inversion, and no error introduced by one.
        /// </remarks>
        private static readonly Color s_measureBackdrop = Color.black;

        /// <summary>
        /// Backdrop for the scenarios that assert the overlay left the screen <b>alone</b>.
        /// </summary>
        /// <remarks>Distinct in all three channels, so "unchanged" is a real claim rather than "still zero".</remarks>
        private static readonly Color s_passThroughBackdrop = new Color(0.25f, 0.5f, 0.75f, 1f);

        /// <summary>The view-ray basis matching <see cref="OVERLAY_FOV"/> and <see cref="OVERLAY_ASPECT"/>.</summary>
        private static Vector4 OverlayRayParams
        {
            get
            {
                float tanVertical = Mathf.Tan(0.5f * OVERLAY_FOV * Mathf.Deg2Rad);
                return new Vector4(tanVertical * OVERLAY_ASPECT, tanVertical, 0f, 0f);
            }
        }

        /// <summary>A <b>level</b> camera: world up is screen up, and forward is horizontal.</summary>
        private static readonly Quaternion s_levelBasis = Quaternion.identity;

        /// <summary>A camera pitched straight <b>down</b>.</summary>
        private static readonly Quaternion s_pitchedDownBasis = Quaternion.Euler(90f, 0f, 0f);

        /// <summary>A camera pitched straight <b>up</b>.</summary>
        private static readonly Quaternion s_pitchedUpBasis = Quaternion.Euler(-90f, 0f, 0f);

        /// <summary>
        /// A fluid body with no reachable horizontal edge, so a scenario measures the surface plane alone.
        /// </summary>
        private static readonly Vector4 s_unboundedBody = new Vector4(
            World.UnboundedFluidExtent, World.UnboundedFluidExtent,
            World.UnboundedFluidExtent, World.UnboundedFluidExtent);

        /// <summary>
        /// Fog params for a wholly submerged view: a density and an eye far below the surface.
        /// </summary>
        /// <param name="density">Extinction per block.</param>
        /// <returns>The <c>_SubmersionParams</c> to publish.</returns>
        private static Vector4 FogParams(float density) => FogParams(density, DEEP_EYE_DEPTH);

        /// <summary>Fog params with an explicit eye depth, reserved slots left at zero.</summary>
        /// <param name="density">Extinction per block.</param>
        /// <param name="eyeDepth">Signed depth of the eye below the drawn surface.</param>
        /// <returns>The <c>_SubmersionParams</c> to publish.</returns>
        private static Vector4 FogParams(float density, float eyeDepth) =>
            new Vector4(density, eyeDepth, 0f, 0f);

        /// <summary>
        /// Beer-Lambert extinction at one pixel, computed on the CPU from the same basis the shader is given.
        /// </summary>
        /// <param name="density">Extinction per block.</param>
        /// <param name="viewZ">Distance along the camera's forward axis.</param>
        /// <param name="pixelX">Sampled pixel X.</param>
        /// <param name="pixelY">Sampled pixel Y.</param>
        /// <returns>The expected fog factor, 0-1.</returns>
        /// <remarks>
        /// The ray length is what makes an off-center pixel differ from a central one at the same depth. Its
        /// sign cannot matter — only the magnitude is used — so this expectation is immune to the platform UV
        /// flip that <c>GetFullScreenTriangleTexCoord</c> applies on some graphics APIs.
        /// <para>
        /// Assumes the ray is submerged over its <b>whole</b> length, which is what
        /// <see cref="DEEP_EYE_DEPTH"/> guarantees for the scenarios that use this. The partly submerged
        /// case has no closed-form expectation to share and is asserted structurally by <c>B19</c>.
        /// </para>
        /// </remarks>
        private static float ExpectedFog(float density, float viewZ, int pixelX, int pixelY)
        {
            Vector4 rayParams = OverlayRayParams;
            float ndcX = OverlayFragmentRenderer.UvAtPixelCenter(pixelX) * 2f - 1f;
            float ndcY = OverlayFragmentRenderer.UvAtPixelCenter(pixelY) * 2f - 1f;

            Vector3 ray = new Vector3(ndcX * rayParams.x, ndcY * rayParams.y, 1f);

            return 1f - Mathf.Exp(-density * viewZ * ray.magnitude);
        }

        /// <summary>Whether a channel matches an expected value within <see cref="COLOR_EPSILON"/>.</summary>
        /// <param name="actual">Measured value.</param>
        /// <param name="expected">Expected value.</param>
        /// <returns>True when they agree.</returns>
        private static bool Near(float actual, float expected) => Mathf.Abs(actual - expected) <= COLOR_EPSILON;

        /// <summary>Whether a sampled color matches an expected color in all three channels.</summary>
        /// <param name="actual">Measured color.</param>
        /// <param name="expected">Expected color.</param>
        /// <returns>True when every channel agrees.</returns>
        private static bool NearRgb(Color actual, Color expected)
        {
            return Near(actual.r, expected.r) && Near(actual.g, expected.g) && Near(actual.b, expected.b);
        }

        /// <summary>Reports whether the overlay harness itself is usable, as an assertion.</summary>
        /// <param name="renderer">The renderer to check.</param>
        /// <returns>True when the shader was found and compiles here.</returns>
        /// <remarks>
        /// A missing or unsupported shader is a FAIL, never an inconclusive: the shader is a checked-in
        /// project asset, and only the absence of a <i>device</i> justifies declining to measure.
        /// </remarks>
        private static bool OverlayShaderUsable(OverlayFragmentRenderer renderer)
        {
            return Check("the shader Hidden/Voxel/UnderwaterOverlay was found and is supported",
                renderer.ShaderUsable);
        }

        /// <summary>
        /// B10 — the positive control, and it must be read before B11 through B15.
        /// </summary>
        /// <remarks>
        /// Those five all conclude from a measured composite, and "the overlay drew nothing" produces exactly
        /// the reading that the pass-through scenarios call success. This one drives the density hard enough
        /// to saturate, so the whole screen must arrive at the authored tint — which also pins that the
        /// <c>rgb</c> comes from <c>_SubmersionColor</c> rather than from anywhere else.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB10OverlayPositiveControl()
        {
            if (SkipWithoutGraphics("B10")) return true;

            using OverlayFragmentRenderer renderer = new OverlayFragmentRenderer();
            if (!OverlayShaderUsable(renderer)) return false;

            Color tint = new Color(0.2f, 0.6f, 0.9f, 1f);
            float raw = OverlayFragmentRenderer.RawDepthForViewZ(10f, OVERLAY_NEAR, OVERLAY_FAR);

            // Density 4 over 10 blocks is exp(-40): saturated to within float precision.
            if (!Check("the overlay material submitted its draw",
                    renderer.Render(s_measureBackdrop, tint, FogParams(4f), OverlayRayParams, s_levelBasis, s_unboundedBody, raw,
                        OVERLAY_NEAR, OVERLAY_FAR)))
                return false;

            Color center = renderer.Sample(32, 32);

            bool ok = Check($"something drew over the backdrop (center = {center})",
                center.r + center.g + center.b > PRESENCE_EPSILON);

            ok &= Check($"a saturated medium reaches the authored tint (got {center}, want {tint})",
                NearRgb(center, tint));

            return ok;
        }

        /// <summary>
        /// B11 — the shader's depth decode agrees with the view distance the harness asked for.
        /// </summary>
        /// <remarks>
        /// The harness inverts <c>LinearEyeDepth</c> to pick a raw depth; the shader decodes it back. Two
        /// independent implementations of the same relation, compared through the GPU — so a wrong
        /// <c>_ZBufferParams</c> convention or a mishandled reversed-Z buffer fails here instead of quietly
        /// shifting every fog expectation that follows.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB11DepthDecode()
        {
            if (SkipWithoutGraphics("B11")) return true;

            using OverlayFragmentRenderer renderer = new OverlayFragmentRenderer();
            if (!OverlayShaderUsable(renderer)) return false;

            bool ok = true;
            float[] distances = { 5f, 20f };
            float previousFog = 0f;

            for (int i = 0; i < distances.Length; i++)
            {
                float viewZ = distances[i];
                float raw = OverlayFragmentRenderer.RawDepthForViewZ(viewZ, OVERLAY_NEAR, OVERLAY_FAR);

                if (!Check($"the overlay submitted its draw at viewZ {viewZ}",
                        renderer.Render(s_measureBackdrop, Color.white, FogParams(WATER_DENSITY),
                            OverlayRayParams, s_levelBasis, s_unboundedBody, raw, OVERLAY_NEAR, OVERLAY_FAR)))
                    return false;

                float measured = renderer.Sample(32, 32).r;
                float expected = ExpectedFog(WATER_DENSITY, viewZ, 32, 32);

                ok &= Check($"fog at viewZ {viewZ} is {expected:F5} (measured {measured:F5})",
                    Near(measured, expected));

                if (i > 0)
                    ok &= Check($"the farther sample fogs more ({measured:F5} > {previousFog:F5})",
                        measured > previousFog);

                previousFog = measured;
            }

            return ok;
        }

        /// <summary>
        /// B12 — the fog follows the true view ray, not the camera's forward axis.
        /// </summary>
        /// <remarks>
        /// The single most likely thing to be subtly wrong in this shader, and invisible to every other
        /// scenario: at one uniform depth, a center pixel and a corner pixel look through measurably
        /// different amounts of medium. Dropping the ray-length scale leaves the center correct and both
        /// off-center samples short by far more than <see cref="COLOR_EPSILON"/>, so this is the assertion
        /// that catches it. Depth is uniform across the screen by construction, so the only thing varying
        /// between these three samples is the ray.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB12ViewRayScale()
        {
            if (SkipWithoutGraphics("B12")) return true;

            using OverlayFragmentRenderer renderer = new OverlayFragmentRenderer();
            if (!OverlayShaderUsable(renderer)) return false;

            const float viewZ = 10f;
            float raw = OverlayFragmentRenderer.RawDepthForViewZ(viewZ, OVERLAY_NEAR, OVERLAY_FAR);

            if (!Check("the overlay submitted its draw",
                    renderer.Render(s_measureBackdrop, Color.white, FogParams(WATER_DENSITY),
                        OverlayRayParams, s_levelBasis, s_unboundedBody, raw, OVERLAY_NEAR, OVERLAY_FAR)))
                return false;

            const int edge = OverlayFragmentRenderer.RenderSize - 1;
            bool ok = true;

            foreach ((int x, int y, string label) in new[]
                     {
                         (32, 32, "screen center"),
                         (edge, 32, "horizontal edge"),
                         (edge, edge, "corner"),
                     })
            {
                float measured = renderer.Sample(x, y).r;
                float expected = ExpectedFog(WATER_DENSITY, viewZ, x, y);

                ok &= Check($"fog at the {label} is {expected:F5} (measured {measured:F5})",
                    Near(measured, expected));
            }

            // Guards the guard: if these three expectations were not actually distinct, the loop above would
            // pass with the ray scale missing entirely.
            float centerExpected = ExpectedFog(WATER_DENSITY, viewZ, 32, 32);
            float cornerExpected = ExpectedFog(WATER_DENSITY, viewZ, edge, edge);

            ok &= Check("the corner and center expectations differ enough to discriminate " +
                        $"({cornerExpected:F5} vs {centerExpected:F5})",
                cornerExpected - centerExpected > 10f * COLOR_EPSILON);

            return ok;
        }

        /// <summary>
        /// B13 — a zero density leaves the screen exactly as it was.
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB13ZeroDensityPassThrough()
        {
            if (SkipWithoutGraphics("B13")) return true;

            using OverlayFragmentRenderer renderer = new OverlayFragmentRenderer();
            if (!OverlayShaderUsable(renderer)) return false;

            float raw = OverlayFragmentRenderer.RawDepthForViewZ(10f, OVERLAY_NEAR, OVERLAY_FAR);

            if (!Check("the overlay submitted its draw",
                    renderer.Render(s_passThroughBackdrop, Color.white, FogParams(0f), OverlayRayParams,
                        s_levelBasis, s_unboundedBody, raw, OVERLAY_NEAR, OVERLAY_FAR)))
                return false;

            Color center = renderer.Sample(32, 32);

            return Check($"a zero density leaves the backdrop untouched (got {center}, want {s_passThroughBackdrop})",
                NearRgb(center, s_passThroughBackdrop));
        }

        /// <summary>
        /// B14 — depth at the far plane takes full density, so sky reads as submerged.
        /// </summary>
        /// <remarks>
        /// No special case in the shader: the far plane linearizes to the far clip distance, which saturates
        /// the same exponential every other pixel uses. This pins that it stays true.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB14FarPlaneSaturates()
        {
            if (SkipWithoutGraphics("B14")) return true;

            using OverlayFragmentRenderer renderer = new OverlayFragmentRenderer();
            if (!OverlayShaderUsable(renderer)) return false;

            if (!Check("the overlay submitted its draw",
                    renderer.Render(s_measureBackdrop, Color.white, FogParams(WATER_DENSITY),
                        OverlayRayParams, s_levelBasis, s_unboundedBody, OverlayFragmentRenderer.RawDepthAtFarPlane(),
                        OVERLAY_NEAR, OVERLAY_FAR)))
                return false;

            float measured = renderer.Sample(32, 32).r;

            return Check($"far-plane depth saturates the medium (measured {measured:F5})", Near(measured, 1f));
        }

        /// <summary>
        /// B15 — a zero strength is the "not submerged" fail-safe, whatever else is published.
        /// </summary>
        /// <remarks>
        /// The convention uninitialized globals give, matching <c>VoxelFog.hlsl</c>'s zero-width range. A
        /// scene rendered before <c>World</c> ever publishes must come out untinted, so the density here is
        /// deliberately one that would otherwise saturate.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB15ZeroStrengthPassThrough()
        {
            if (SkipWithoutGraphics("B15")) return true;

            using OverlayFragmentRenderer renderer = new OverlayFragmentRenderer();
            if (!OverlayShaderUsable(renderer)) return false;

            Color dry = new Color(1f, 1f, 1f, 0f);
            float raw = OverlayFragmentRenderer.RawDepthForViewZ(10f, OVERLAY_NEAR, OVERLAY_FAR);

            if (!Check("the overlay submitted its draw",
                    renderer.Render(s_passThroughBackdrop, dry, FogParams(4f), OverlayRayParams, s_levelBasis, s_unboundedBody, raw,
                        OVERLAY_NEAR, OVERLAY_FAR)))
                return false;

            Color center = renderer.Sample(32, 32);

            return Check($"a zero strength leaves the backdrop untouched (got {center}, " +
                         $"want {s_passThroughBackdrop})",
                NearRgb(center, s_passThroughBackdrop));
        }

        /// <summary>
        /// B16 — the eye query packs onto the channels the shader reads, and gates rather than fades.
        /// </summary>
        /// <remarks>
        /// Needs no device. The strength is a <b>gate</b>, not a ramp — it never takes an intermediate value
        /// — and it opens exactly when <c>IsSubmerged</c> does, which is also the ambience filter's boundary.
        /// A strength that <i>faded</i> with eye depth is what let a player at the waterline switch the whole
        /// medium off; keeping the gate open from <i>outside</i> the water is the opposite error, and painted
        /// the medium over dry caves. Both were reported in game 2026-09-04; the split at the waterline comes
        /// from the per-pixel solve (<c>B19</c>, <c>B20</c>), never from this scalar.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB16PackedGlobals()
        {
            EyeSubmersion deep = new EyeSubmersion
            {
                Type = FluidType.WaterLike,
                SurfaceY = 63.25f,
                EyeDepth = 2.5f,
                SubmersionColor = new Color(0.08f, 0.24f, 0.5f, 1f),
                SubmersionDensity = WATER_DENSITY,
            };

            Quaternion level = Quaternion.identity;
            SubmersionGlobals packed = SubmersionOverlay.Pack(in deep, OVERLAY_FOV, OVERLAY_ASPECT, level);

            bool ok = Check($"a submerged eye opens the gate (a = {packed.Color.a})",
                ExactValue.Equal(packed.Color.a, 1f));

            ok &= Check("the tint rgb is the authored color",
                NearRgb(packed.Color, deep.SubmersionColor));

            ok &= Check($"x carries the authored density ({packed.FogParams.x})",
                ExactValue.Equal(packed.FogParams.x, WATER_DENSITY));

            ok &= Check("y carries the eye's signed depth, not the surface's absolute Y " +
                        $"({packed.FogParams.y})",
                ExactValue.Equal(packed.FogParams.y, deep.EyeDepth));

            ok &= Check("z and w stay reserved at zero (UW-5 meniscus, v2 distortion)",
                ExactValue.IsZero(packed.FogParams.z) && ExactValue.IsZero(packed.FogParams.w));

            float tanVertical = Mathf.Tan(0.5f * OVERLAY_FOV * Mathf.Deg2Rad);
            ok &= Check($"the ray spread is the frustum's half-extents ({packed.RayParams.x:F5}, " +
                        $"{packed.RayParams.y:F5})",
                Near(packed.RayParams.x, tanVertical * OVERLAY_ASPECT) &&
                Near(packed.RayParams.y, tanVertical));

            ok &= Check("a level camera packs world up as the vertical basis",
                Near(packed.RayBasisY.x, 0f) && Near(packed.RayBasisY.y, 1f) &&
                Near(packed.RayBasisY.z, 0f));

            // Pitch and roll must reach the basis, or the waterline cannot track the camera.
            SubmersionGlobals pitchedDown = SubmersionOverlay.Pack(in deep, OVERLAY_FOV, OVERLAY_ASPECT,
                Quaternion.Euler(90f, 0f, 0f));
            ok &= Check("a camera pitched down packs a downward forward axis " +
                        $"({pitchedDown.RayBasisY.z:F3})",
                Near(pitchedDown.RayBasisY.z, -1f));

            SubmersionGlobals rolled = SubmersionOverlay.Pack(in deep, OVERLAY_FOV, OVERLAY_ASPECT,
                Quaternion.Euler(0f, 0f, 90f));
            ok &= Check("a rolled camera moves the vertical into the right axis " +
                        $"({rolled.RayBasisY.x:F3}, {rolled.RayBasisY.y:F3})",
                Near(Mathf.Abs(rolled.RayBasisY.x), 1f) && Near(rolled.RayBasisY.y, 0f));

            // The gate, across the surface: it tracks IsSubmerged exactly, which is also where the
            // ambience low-pass switches, asserting §3.3's "one boundary" rather than assuming it. A
            // half-submerged view stays fogged through the per-pixel solve (B19/B20), not through B21.
            EyeSubmersion barelyUnder = deep;
            barelyUnder.EyeDepth = AT_SURFACE_DEPTH;
            ok &= Check("an eye a hair under the surface opens the gate, so its lower half stays fogged",
                ExactValue.Equal(SubmersionOverlay.Pack(in barelyUnder, OVERLAY_FOV, OVERLAY_ASPECT, level).Color.a, 1f) &&
                barelyUnder.IsSubmerged);

            EyeSubmersion atSurface = deep;
            atSurface.EyeDepth = 0f;
            ok &= Check("an eye exactly at the surface closes it, agreeing with IsSubmerged",
                ExactValue.IsZero(SubmersionOverlay.Pack(in atSurface, OVERLAY_FOV, OVERLAY_ASPECT, level).Color.a) &&
                !atSurface.IsSubmerged);

            EyeSubmersion above = deep;
            above.EyeDepth = ABOVE_SURFACE_DEPTH;
            SubmersionGlobals abovePacked = SubmersionOverlay.Pack(in above, OVERLAY_FOV, OVERLAY_ASPECT, level);

            ok &= Check("an eye above the surface closes it — from outside there is nothing to fog",
                ExactValue.IsZero(abovePacked.Color.a));

            ok &= Check("the signed depth is still published negative there, for a waterline to track",
                abovePacked.FogParams.y < 0f);

            EyeSubmersion dry = default;
            ok &= Check("a dry eye closes the gate — the not-submerged fail-safe",
                ExactValue.IsZero(SubmersionOverlay.Pack(in dry, OVERLAY_FOV, OVERLAY_ASPECT, level).Color.a));

            return ok;
        }

        /// <summary>
        /// B19 — the fog follows each ray's own submerged length, so a partly submerged view splits.
        /// </summary>
        /// <remarks>
        /// The item-4 fix (reported in game 2026-09-04): a screen-wide strength let a player at the
        /// waterline switch the medium off over a fully submerged lower half. Fog is now charged per pixel
        /// for the part of <i>that</i> ray below the surface.
        /// <para>
        /// This one asserts the split's <b>structure</b> without reference to screen orientation: a camera
        /// pitched straight down has every ray descending and one pitched straight up has every ray rising,
        /// both regardless of pixel position, so those pin the forward axis absolutely; a level camera must
        /// then produce a split, and rolling 180° must move the fog to the other half.
        /// <para>
        /// It does <b>not</b> pin which half is which — that is <c>B20</c>'s job, and leaving it out here
        /// is what let an inverted vertical sign ship. Structure and orientation are separate failures and
        /// need separate baselines: every assertion below still passes with the sign backwards.
        /// </para>
        /// </para>
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB19PerPixelSubmergedLength()
        {
            if (SkipWithoutGraphics("B19")) return true;

            using OverlayFragmentRenderer renderer = new OverlayFragmentRenderer();
            if (!OverlayShaderUsable(renderer)) return false;

            // The eye exactly at the surface: the sign of each ray alone decides whether it is in water.
            Vector4 atSurface = FogParams(WATER_DENSITY, AT_SURFACE_DEPTH);
            float raw = OverlayFragmentRenderer.RawDepthForViewZ(40f, OVERLAY_NEAR, OVERLAY_FAR);
            const int lower = 8;
            const int upper = OverlayFragmentRenderer.RenderSize - 9;

            if (!Check("the overlay submitted its pitched-down draw",
                    renderer.Render(s_measureBackdrop, Color.white, atSurface, OverlayRayParams,
                        s_pitchedDownBasis, s_unboundedBody, raw, OVERLAY_NEAR, OVERLAY_FAR)))
                return false;

            bool ok = Check("pitched straight down at the surface, every ray is submerged and fogged",
                renderer.Sample(32, lower).r > 0.9f && renderer.Sample(32, upper).r > 0.9f);

            if (!Check("the overlay submitted its pitched-up draw",
                    renderer.Render(s_measureBackdrop, Color.white, atSurface, OverlayRayParams,
                        s_pitchedUpBasis, s_unboundedBody, raw, OVERLAY_NEAR, OVERLAY_FAR)))
                return false;

            ok &= Check("pitched straight up at the surface, no ray enters the water and nothing is fogged",
                renderer.Sample(32, lower).r < COLOR_EPSILON && renderer.Sample(32, upper).r < COLOR_EPSILON);

            // Level: one half of the screen must be submerged and the other clear.
            if (!Check("the overlay submitted its level draw",
                    renderer.Render(s_measureBackdrop, Color.white, atSurface, OverlayRayParams,
                        s_levelBasis, s_unboundedBody, raw, OVERLAY_NEAR, OVERLAY_FAR)))
                return false;

            float levelLow = renderer.Sample(32, lower).r;
            float levelHigh = renderer.Sample(32, upper).r;

            ok &= Check($"level at the surface, the screen splits ({levelLow:F3} vs {levelHigh:F3})",
                Mathf.Abs(levelLow - levelHigh) > 0.9f);

            // Rolled 180°: world up now points the other way down the screen, so the fog must change sides.
            Quaternion rolledBasis = Quaternion.Euler(0f, 0f, 180f);
            if (!Check("the overlay submitted its rolled draw",
                    renderer.Render(s_measureBackdrop, Color.white, atSurface, OverlayRayParams,
                        rolledBasis, s_unboundedBody, raw, OVERLAY_NEAR, OVERLAY_FAR)))
                return false;

            float rolledLow = renderer.Sample(32, lower).r;
            float rolledHigh = renderer.Sample(32, upper).r;

            ok &= Check("rolling 180 degrees swaps which half is fogged " +
                        $"({levelLow:F3}/{levelHigh:F3} -> {rolledLow:F3}/{rolledHigh:F3})",
                Mathf.Abs(rolledLow - levelHigh) < 0.1f && Mathf.Abs(rolledHigh - levelLow) < 0.1f);

            // A deep eye must NOT split: the whole view is medium again.
            if (!Check("the overlay submitted its deep draw",
                    renderer.Render(s_measureBackdrop, Color.white, FogParams(WATER_DENSITY),
                        OverlayRayParams, s_levelBasis, s_unboundedBody, raw, OVERLAY_NEAR, OVERLAY_FAR)))
                return false;

            ok &= Check("a deep eye fogs both halves, so the split is the surface and not the horizon",
                renderer.Sample(32, lower).r > 0.9f && renderer.Sample(32, upper).r > 0.9f);

            return ok;
        }

        /// <summary>
        /// B18 — an eye sinking through a fluid body reports one continuous depth, not one per cell.
        /// </summary>
        /// <remarks>
        /// The drawn surface of a fluid body is a <b>single plane</b>, so every eye position in the column
        /// below it must report the same <c>SurfaceY</c> and a monotonically deepening <c>EyeDepth</c>.
        /// Reading the surface off the eye's own cell instead puts it at that cell's ceiling, which snaps
        /// down at every boundary and restarts the ramp — the tint visibly re-fades once per cell while
        /// sinking (reported in game 2026-09-04).
        /// <para>
        /// B8 could not catch this: it only asserts the <i>sign</i> of the depth, and the sign stays
        /// correct throughout. Nothing pinned <c>SurfaceY</c>'s value for a submerged eye, which is also
        /// the plane UW-5 will split the screen on.
        /// </para>
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB18ContinuousDepthWhileSinking()
        {
            using EyeSubmersionFixture fixture = new EyeSubmersionFixture();

            // Straddles two cell boundaries inside the body, closely on either side of each.
            float[] eyeHeights = { 6.5f, 6.2f, 6.01f, 5.99f, 5.5f, 5.01f, 4.99f, 4.5f };

            float surfaceAtTop = 0f;
            float previousDepth = float.NegativeInfinity;
            bool ok = true;

            for (int i = 0; i < eyeHeights.Length; i++)
            {
                EyeSubmersion sample = fixture.Sample(fixture.EyeXz.x, eyeHeights[i], fixture.EyeXz.y);
                if (i == 0) surfaceAtTop = sample.SurfaceY;

                ok &= Check($"B18: at eyeY {eyeHeights[i]:F2} the surface is still {surfaceAtTop:F4} " +
                            $"(got {sample.SurfaceY:F4})",
                    Mathf.Abs(sample.SurfaceY - surfaceAtTop) <= HEIGHT_EPSILON);

                ok &= Check($"B18: at eyeY {eyeHeights[i]:F2} the depth deepened " +
                            $"({sample.EyeDepth:F4} > {previousDepth:F4})",
                    sample.EyeDepth > previousDepth);

                // The user-visible symptom, asserted directly: the depth the shader is handed must deepen
                // with the eye, not reset. The gate staying open is asserted alongside it, since a closed
                // gate would hide a wrong depth entirely.
                SubmersionGlobals packed = SubmersionOverlay.Pack(in sample, OVERLAY_FOV, OVERLAY_ASPECT,
                    Quaternion.identity);

                ok &= Check($"B18: at eyeY {eyeHeights[i]:F2} the published depth is the reported one " +
                            $"({packed.FogParams.y:F4})",
                    Mathf.Abs(packed.FogParams.y - sample.EyeDepth) <= HEIGHT_EPSILON);

                ok &= Check($"B18: at eyeY {eyeHeights[i]:F2} the medium gate stays open " +
                            $"(a = {packed.Color.a:F3})",
                    ExactValue.Equal(packed.Color.a, 1f));

                previousDepth = sample.EyeDepth;
            }

            return ok;
        }

        /// <summary>
        /// B20 — the fogged half is the half the camera looks <b>down</b> at, not the half above it.
        /// </summary>
        /// <remarks>
        /// The screen-orientation assertion `B19` deliberately left out, and the defect that slipped
        /// through because of it: an inverted vertical sign fogged the <b>sky</b> and left the water clear
        /// (reported in game 2026-09-04). The sign became load-bearing when the fog went per-pixel, because
        /// `Blit.hlsl` already flips its texcoord on platforms whose textures start at the top — compensating
        /// a second time inverts the result.
        /// <para>
        /// Measured rather than reasoned. A band is drawn across the <b>bottom half of clip space</b>, where
        /// <c>y = -1</c> is the bottom of the view by definition, and the rows it occupies in the readback
        /// are recorded. The overlay is then rendered with a level camera at the surface, which must fog
        /// exactly the downward rays. Asserting that the two sets of rows agree needs no assumption about
        /// this platform's texture origin or <c>ReadPixels</c> orientation, because a flip anywhere in that
        /// chain moves the marker and the fog together.
        /// </para>
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB20FoggedHalfIsTheLowerHalf()
        {
            if (SkipWithoutGraphics("B20")) return true;

            using OverlayFragmentRenderer renderer = new OverlayFragmentRenderer();
            if (!OverlayShaderUsable(renderer)) return false;

            // Where does clip-space "down" land in the readback?
            Color marker = new Color(1f, 0f, 1f, 1f);
            if (!Check("the clip-space marker material was available and drew",
                    renderer.RenderClipSpaceBottomMarker(Color.black, marker)))
                return false;

            bool lowRowIsClipBottom = renderer.Sample(32, 8).r > 0.5f;
            bool highRowIsClipBottom = renderer.Sample(32, OverlayFragmentRenderer.RenderSize - 9).r > 0.5f;

            // Exactly one half must carry the marker, or the marker itself is not measuring anything.
            if (!Check("the marker covers exactly one half of the readback " +
                       $"(low={lowRowIsClipBottom}, high={highRowIsClipBottom})",
                    lowRowIsClipBottom != highRowIsClipBottom))
                return false;

            int clipBottomRow = lowRowIsClipBottom ? 8 : OverlayFragmentRenderer.RenderSize - 9;
            int clipTopRow = lowRowIsClipBottom ? OverlayFragmentRenderer.RenderSize - 9 : 8;

            // A level camera with its eye exactly at the surface: every downward ray is in the water for
            // its whole length, every upward ray leaves immediately.
            float raw = OverlayFragmentRenderer.RawDepthForViewZ(40f, OVERLAY_NEAR, OVERLAY_FAR);
            if (!Check("the overlay submitted its level draw",
                    renderer.Render(s_measureBackdrop, Color.white, FogParams(WATER_DENSITY, AT_SURFACE_DEPTH),
                        OverlayRayParams, s_levelBasis, s_unboundedBody, raw, OVERLAY_NEAR, OVERLAY_FAR)))
                return false;

            float fogAtClipBottom = renderer.Sample(32, clipBottomRow).r;
            float fogAtClipTop = renderer.Sample(32, clipTopRow).r;

            bool ok = Check($"the half the camera looks down at is fogged ({fogAtClipBottom:F3})",
                fogAtClipBottom > 0.9f);

            ok &= Check($"the half above the surface is left clear ({fogAtClipTop:F3}) — a fogged sky over " +
                        "clear water is the inverted-sign symptom",
                fogAtClipTop < COLOR_EPSILON);

            return ok;
        }

        /// <summary>
        /// B21 — an eye above the surface fogs nothing, however far below it the geometry sits.
        /// </summary>
        /// <remarks>
        /// Reported in game 2026-09-04: standing in a shallow pool with the head clear of the water painted
        /// the medium over a dry cave and its stone, because everything below the waterline was being
        /// charged for traveling through water.
        /// <para>
        /// The surface is a <b>plane</b> and the fluid is a <b>body</b>. While the eye is under the surface
        /// the two agree closely enough — the liquid mesh is a closed shell that writes depth, so a ray
        /// leaving the water terminates at the surface or at a side face and the depth buffer bounds the
        /// medium for free. Above the surface they diverge without limit: the plane runs to the horizon
        /// while the pool is three blocks wide, and any ray that misses the water is charged for the whole
        /// distance to whatever it does hit.
        /// </para>
        /// <para>
        /// So there is nothing to fog from above, and that is exact rather than a simplification: a ray that
        /// does reach water <i>ends</i> at the water, and what the pixel shows is the surface itself as the
        /// liquid shader drew it — never a column of water seen through.
        /// </para>
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB21AboveSurfaceFogsNothing()
        {
            // The packing half needs no device, so it is asserted before the graphics gate.
            EyeSubmersion wading = new EyeSubmersion
            {
                Type = FluidType.WaterLike,
                SurfaceY = 63f,
                EyeDepth = ABOVE_SURFACE_DEPTH,
                SubmersionColor = new Color(0.08f, 0.24f, 0.5f, 1f),
                SubmersionDensity = WATER_DENSITY,
            };

            SubmersionGlobals packed =
                SubmersionOverlay.Pack(in wading, OVERLAY_FOV, OVERLAY_ASPECT, Quaternion.identity);

            bool ok = Check("an eye above the surface closes the gate, even with fluid in the search",
                ExactValue.IsZero(packed.Color.a));

            ok &= Check("the negative depth still reaches the shader, for a waterline to track",
                packed.FogParams.y < 0f);

            if (SkipWithoutGraphics("B21")) return ok;

            using OverlayFragmentRenderer renderer = new OverlayFragmentRenderer();
            if (!OverlayShaderUsable(renderer)) return false;

            // The shader is asserted independently of the gate: a strength of 1 is forced, so this measures
            // the fragment's own handling of an above-surface eye rather than C# declining to draw.
            float raw = OverlayFragmentRenderer.RawDepthForViewZ(40f, OVERLAY_NEAR, OVERLAY_FAR);
            if (!Check("the overlay submitted its draw",
                    renderer.Render(s_passThroughBackdrop, Color.white,
                        FogParams(WATER_DENSITY, ABOVE_SURFACE_DEPTH), OverlayRayParams, s_levelBasis,
                        s_unboundedBody, raw, OVERLAY_NEAR, OVERLAY_FAR)))
                return false;

            const int lower = 8;
            const int upper = OverlayFragmentRenderer.RenderSize - 9;

            ok &= Check($"the half below the waterline is left alone ({renderer.Sample(32, lower)})",
                NearRgb(renderer.Sample(32, lower), s_passThroughBackdrop));

            ok &= Check($"the half above it is left alone too ({renderer.Sample(32, upper)})",
                NearRgb(renderer.Sample(32, upper), s_passThroughBackdrop));

            return ok;
        }

        /// <summary>
        /// B22 — a ray that leaves the fluid body sideways is charged only for the water it crossed.
        /// </summary>
        /// <remarks>
        /// The shoreline case, measured live in game 2026-09-04: an eye 2.4 cm under the surface at the
        /// body's western edge, where a ray to the west passed through <b>zero</b> water and was charged
        /// 3.9 blocks — 42 % fog on dry cave — while rays east through 3.9 blocks of real water were
        /// correct to within 3 %.
        /// <para>
        /// The plane is only the body's <b>lid</b>. It was believed the depth buffer bounded the sides for
        /// free, since the liquid mesh writes depth; the measurement disproved it. At a shoreline the
        /// nearest boundary face is a few centimetres from the eye — inside the near clip plane — so it is
        /// never rasterized, and the depth buffer reports the terrain beyond instead.
        /// </para>
        /// <para>
        /// The four extents close that. This scenario reproduces the measured frame's shape: a body that
        /// ends immediately to the west and runs unbounded east.
        /// </para>
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB22HorizontalBodyBound()
        {
            if (SkipWithoutGraphics("B22")) return true;

            using OverlayFragmentRenderer renderer = new OverlayFragmentRenderer();
            if (!OverlayShaderUsable(renderer)) return false;

            // West edge 2 cm away, east unbounded — the live frame's geometry.
            const float westExtent = 0.02f;
            Vector4 shoreline = new Vector4(westExtent, World.UnboundedFluidExtent,
                World.UnboundedFluidExtent, World.UnboundedFluidExtent);

            // Level camera facing +Z, so screen-left is −X (west) and screen-right is +X (east).
            float raw = OverlayFragmentRenderer.RawDepthForViewZ(40f, OVERLAY_NEAR, OVERLAY_FAR);
            if (!Check("the overlay submitted its shoreline draw",
                    renderer.Render(s_measureBackdrop, Color.white, FogParams(WATER_DENSITY),
                        OverlayRayParams, s_levelBasis, shoreline, raw, OVERLAY_NEAR, OVERLAY_FAR)))
                return false;

            const int left = 6;
            const int right = OverlayFragmentRenderer.RenderSize - 7;

            float fogWest = renderer.Sample(left, 32).r;
            float fogEast = renderer.Sample(right, 32).r;

            bool ok = Check("a ray leaving the body immediately to the west is barely fogged " +
                            $"({fogWest:F3})",
                fogWest < 0.05f);

            ok &= Check($"a ray into unbounded water to the east is still fully fogged ({fogEast:F3})",
                fogEast > 0.9f);

            // The same view with the body unbounded both ways must fog BOTH sides — otherwise the west
            // sample above could be reading something other than the horizontal bound.
            if (!Check("the overlay submitted its open-water control",
                    renderer.Render(s_measureBackdrop, Color.white, FogParams(WATER_DENSITY),
                        OverlayRayParams, s_levelBasis, s_unboundedBody, raw, OVERLAY_NEAR, OVERLAY_FAR)))
                return false;

            ok &= Check("the control fogs the west side too, so the bound is what changed " +
                        $"({renderer.Sample(left, 32).r:F3})",
                renderer.Sample(left, 32).r > 0.9f);

            return ok;
        }

        /// <summary>
        /// B23 — the published body extents ease toward what was measured, but snap on entering water.
        /// </summary>
        /// <remarks>
        /// Reported in game 2026-09-04: the medium shifts as the player swims, and jumps hardest crossing a
        /// cell boundary <i>vertically</i>. Measured on a terraced pool — <c>EyeDepth</c> stayed continuous
        /// (0.855 → 0.895) while all four extents stepped 2.50 → 6.50 at once, because they are re-measured
        /// from whichever cell the eye occupies.
        /// <para>
        /// Needs no device: the easing is a pure function so that the two things most likely to be wrong
        /// are both reachable — the <b>snap</b> on entering water, which prevents the fog sweeping in from
        /// the last body swum in, and the <b>space</b> the easing happens in. A linear interpolation of raw
        /// distances would spend seconds descending from <c>World.UnboundedFluidExtent</c> to a two-block
        /// channel, bounding nothing the whole way — which is the over-fogging the extents exist to stop.
        /// </para>
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB23ExtentsEaseButSnapOnEntry()
        {
            Vector4 wide = new Vector4(6.5f, 6.5f, 6.5f, 6.5f);
            Vector4 narrow = new Vector4(2.5f, 2.5f, 2.5f, 2.5f);
            const float frame = 1f / 60f;

            // Entering water must take the measurement whole, however stale the previous body.
            bool ok = Check("the first publish after entering water snaps to the measured extents",
                SubmersionOverlay.StepExtents(wide, narrow, frame, false) == narrow);

            ok &= Check("a zero-length frame changes nothing",
                SubmersionOverlay.StepExtents(wide, narrow, 0f, true) == wide);

            // One frame moves toward the target without reaching or passing it.
            Vector4 stepped = SubmersionOverlay.StepExtents(wide, narrow, frame, true);
            ok &= Check($"one frame eases toward the target without overshooting ({stepped.x:F4})",
                stepped.x < wide.x && stepped.x > narrow.x);

            // Repeated frames converge, and never past the target.
            Vector4 running = wide;
            for (int i = 0; i < 240; i++) running = SubmersionOverlay.StepExtents(running, narrow, frame, true);

            ok &= Check($"four seconds of frames converge on the measured extents ({running.x:F4})",
                Mathf.Abs(running.x - narrow.x) < 0.01f && running.x >= narrow.x - 0.01f);

            // The interesting direction: leaving open water for a narrow channel must bound quickly. A
            // linear lerp would still read ~630000 blocks here, which clamps nothing.
            Vector4 fromOpenWater = new Vector4(World.UnboundedFluidExtent, World.UnboundedFluidExtent,
                World.UnboundedFluidExtent, World.UnboundedFluidExtent);
            Vector4 afterOneTimeConstant =
                SubmersionOverlay.StepExtents(fromOpenWater, narrow, SubmersionOverlay.ExtentDampTime, true);

            ok &= Check("one time constant out of open water reaches a bound that actually clamps " +
                        $"({afterOneTimeConstant.x:F3} blocks)",
                afterOneTimeConstant.x < 10f);

            ok &= Check("...without undershooting past the measured extent",
                afterOneTimeConstant.x > narrow.x);

            // And the reverse: a body that opens out must not be clamped tight for long.
            Vector4 opening = SubmersionOverlay.StepExtents(narrow, fromOpenWater,
                SubmersionOverlay.ExtentDampTime, true);
            ok &= Check($"one time constant into open water releases the bound ({opening.x:F1} blocks)",
                opening.x > 3f * narrow.x);

            return ok;
        }

        /// <summary>
        /// B24 — something standing in the water does not shrink the surrounding body.
        /// </summary>
        /// <remarks>
        /// The extents ask where the fluid <b>body</b> ends, not where the first non-water cell is. Measured
        /// in game 2026-09-04: a single block six cells out cut that side of the box from 23 cells to 6, and
        /// swimming past such blocks was what made the medium look unstable — the body appeared to breathe
        /// as obstructions moved in and out of the four axis probes.
        /// <para>
        /// Reading past a gap is correct, not lenient: a solid block inside the body is an <b>occluder</b>,
        /// and the depth buffer already stops each ray at it, so a ray aimed that way is charged for the
        /// water in front of it whatever the extent says. What the extent must not do is under-report the
        /// water available to every <i>other</i> ray on that side.
        /// </para>
        /// <para>
        /// Drives the real <c>World.GatherEyeSubmersion</c> over a pool with a pillar in it, so it pins the
        /// scan rather than a restatement of it.
        /// </para>
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB24ObstructionDoesNotShrinkTheBody()
        {
            const int groundY = 2;
            const int fluidY = 5;
            const int poolX = 8;
            const int poolZ = 8;
            const int poolRadius = 7;

            using PhysicsTestWorld world = new PhysicsTestWorld(TestPhysicsBlockPalette.Create());
            world.FillLayer(groundY, Id.Ground);

            for (int y = groundY + 1; y <= fluidY; y++)
            for (int dx = -poolRadius; dx <= poolRadius; dx++)
            for (int dz = -poolRadius; dz <= poolRadius; dz++)
                world.SetBlock(poolX + dx, y, poolZ + dz, Id.Fluid, 0);

            Vector3 eye = new Vector3(poolX + 0.5f, fluidY + 0.5f, poolZ + 0.5f);

            World.Instance.GatherEyeSubmersion(eye, out EyeSubmersion open);
            bool ok = Check($"the open pool reaches its far edge to the east ({open.HorizontalExtent.y:F2})",
                open.IsSubmerged && Near(open.HorizontalExtent.y, poolRadius + 0.5f));

            // One block, four cells east, at the eye's own height — the shape measured in game.
            world.SetBlock(poolX + 4, fluidY, poolZ, Id.Ground);

            World.Instance.GatherEyeSubmersion(eye, out EyeSubmersion obstructed);

            ok &= Check("a single block in the way leaves the body's reach intact " +
                        $"({obstructed.HorizontalExtent.y:F2}, was {open.HorizontalExtent.y:F2})",
                ExactValue.Equal(obstructed.HorizontalExtent.y, open.HorizontalExtent.y));

            ok &= Check("the untouched sides are unchanged",
                ExactValue.Equal(obstructed.HorizontalExtent.x, open.HorizontalExtent.x) &&
                ExactValue.Equal(obstructed.HorizontalExtent.z, open.HorizontalExtent.z) &&
                ExactValue.Equal(obstructed.HorizontalExtent.w, open.HorizontalExtent.w));

            // A real edge must still be found: fill the far half of the eastern arm with stone so the water
            // genuinely ends, and the extent must come in.
            for (int dx = 3; dx <= poolRadius; dx++)
                world.SetBlock(poolX + dx, fluidY, poolZ, Id.Ground);

            World.Instance.GatherEyeSubmersion(eye, out EyeSubmersion walled);

            ok &= Check("where the water truly ends, the body still ends with it " +
                        $"({walled.HorizontalExtent.y:F2})",
                Near(walled.HorizontalExtent.y, 2.5f));

            return ok;
        }

        /// <summary>
        /// B25 — a dry gap ends the body, where a solid block inside it does not.
        /// </summary>
        /// <remarks>
        /// The complement of <c>B24</c>, and the case its reasoning did not cover. Passing over a
        /// <b>solid</b> block is right: the depth buffer already stops every ray at it, so the water beyond
        /// still belongs to the rays that miss it. Air is the opposite — nothing stops a ray crossing a dry
        /// floor, so counting a second pool past one charges the whole side for water it never enters, and
        /// the overlay fogs the dry gap.
        /// <para>
        /// Two pools at one height with a dry gap between them, which is what a cave with two puddles
        /// looks like. Drives the real <c>World.GatherEyeSubmersion</c>, so it pins the scan itself.
        /// </para>
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB25DryGapEndsTheBody()
        {
            // The harness world is one 16-wide chunk, so every x here must stay inside [0, 16):
            // near pool 1..5, dry gap 6..7, far pool 8..12.
            const int groundY = 2;
            const int fluidY = 5;
            const int poolX = 3;
            const int poolZ = 8;
            const int nearPoolReach = 2;
            const int gapCells = 2;
            const int farPoolEdge = 12;

            using PhysicsTestWorld world = new PhysicsTestWorld(TestPhysicsBlockPalette.Create());
            world.FillLayer(groundY, Id.Ground);

            // Near pool: the eye's own body, ending two cells east of it.
            for (int y = groundY + 1; y <= fluidY; y++)
            for (int dx = -nearPoolReach; dx <= nearPoolReach; dx++)
            for (int dz = -nearPoolReach; dz <= nearPoolReach; dz++)
                world.SetBlock(poolX + dx, y, poolZ + dz, Id.Fluid, 0);

            // Far pool: same height, past a dry gap. Left as air between them — no wall.
            for (int x = poolX + nearPoolReach + gapCells + 1; x <= farPoolEdge; x++)
                world.SetBlock(x, fluidY, poolZ, Id.Fluid, 0);

            Vector3 eye = new Vector3(poolX + 0.5f, fluidY + 0.5f, poolZ + 0.5f);

            World.Instance.GatherEyeSubmersion(eye, out EyeSubmersion gapped);

            bool ok = Check("the eye is submerged in the near pool", gapped.IsSubmerged);

            ok &= Check("the body ends at the near pool's edge, not at the far pool past the dry gap " +
                        $"({gapped.HorizontalExtent.y:F2}, the far pool would read " +
                        $"{farPoolEdge - poolX + 0.5f:F2})",
                Near(gapped.HorizontalExtent.y, nearPoolReach + 0.5f));

            // The same gap filled with stone must NOT shorten it: that is B24's rule, and this fix must not
            // have traded one for the other.
            for (int x = poolX + nearPoolReach + 1; x <= poolX + nearPoolReach + gapCells; x++)
                world.SetBlock(x, fluidY, poolZ, Id.Ground);

            World.Instance.GatherEyeSubmersion(eye, out EyeSubmersion bridged);

            ok &= Check("filling that same gap with solid restores the far pool's reach " +
                        $"({bridged.HorizontalExtent.y:F2})",
                Near(bridged.HorizontalExtent.y, farPoolEdge - poolX + 0.5f));

            return ok;
        }

        /// <summary>
        /// B17 — the overlay is wired into the renderer asset, shader assigned, ahead of the UI blur.
        /// </summary>
        /// <remarks>
        /// The render scenarios above all pass on the shader alone and cannot observe an unwired pipeline.
        /// Three separate silent failures live here, and each needs its own assertion: the feature missing
        /// entirely; the feature present but ordered <i>after</i> <c>UIBlurRendererFeature</c>, which leaves
        /// every blurred HUD panel showing an untinted world; and the feature present with a null shader,
        /// which makes <c>Create</c> log a warning and disable itself while this suite's own material — built
        /// from <c>Shader.Find</c> — keeps passing. A membership-only check catches none of the last two.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB17RendererWiring()
        {
            UniversalRendererData data =
                AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RENDERER_ASSET_PATH);

            if (!Check($"the renderer asset loaded from {RENDERER_ASSET_PATH}", data != null)) return false;

            int overlayIndex = -1;
            int blurIndex = -1;
            UnderwaterOverlayRendererFeature overlay = null;

            for (int i = 0; i < data.rendererFeatures.Count; i++)
            {
                ScriptableRendererFeature feature = data.rendererFeatures[i];

                if (feature is UnderwaterOverlayRendererFeature typed)
                {
                    overlayIndex = i;
                    overlay = typed;
                }
                else if (feature is UIBlurRendererFeature)
                {
                    blurIndex = i;
                }
            }

            bool ok = Check($"UnderwaterOverlayRendererFeature is listed (index {overlayIndex})",
                overlayIndex >= 0);

            ok &= Check($"UIBlurRendererFeature is listed (index {blurIndex})", blurIndex >= 0);

            if (overlayIndex < 0 || blurIndex < 0) return false;

            ok &= Check($"the overlay records before the UI blur ({overlayIndex} < {blurIndex}), so the " +
                        "blur samples an already-tinted screen",
                overlayIndex < blurIndex);

            SerializedObject featureObject = new SerializedObject(overlay);
            SerializedProperty shaderProperty = featureObject.FindProperty("_settings.overlayShader");

            ok &= Check("the feature's overlay shader field exists", shaderProperty != null);

            if (shaderProperty != null)
                ok &= Check($"the overlay shader is assigned ({shaderProperty.objectReferenceValue})",
                    shaderProperty.objectReferenceValue != null);

            return ok;
        }
    }
}
