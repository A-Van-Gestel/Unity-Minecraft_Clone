using System.Collections.Generic;
using Editor.Validation.Framework;
using Editor.WorldTools.Libraries;
using Sky;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.Celestial
{
    /// <summary>
    /// Renders the skybox shader in edit mode and asserts invariants on the resulting pixels — the first
    /// coverage this project has of the sky's <b>shader</b> half, which <see cref="SkyValidationSuite"/>
    /// explicitly cannot reach.
    /// <para>
    /// It deliberately does <b>not</b> compare against reference images. GPU output is not bit-reproducible
    /// across drivers, machines or engine versions, so checked-in goldens would fail for reasons that have
    /// nothing to do with the sky. What is asserted instead are properties that must hold on any correct
    /// renderer: the disc is opaque, no configuration produces a NaN, the sun outshines the sky, the
    /// gradient is the right way up, and the moon's daytime silhouette is independent of its elevation.
    /// Every one of these corresponds to a defect that actually occurred.
    /// </para>
    /// <para>
    /// <b>Prove-red</b> — each mutation below was applied, run, and reverted; these are observed results,
    /// not predictions:
    /// <list type="bullet">
    /// <item>Rendering the measurement path to an 8-bit sRGB target instead of half-float linear reds B1,
    /// reporting an authored 0.075 as 0.302 — the exact four-times-brighter error the authoring notes
    /// warn about, reproduced inside the measuring tool.</item>
    /// <item>Compositing the moon by <c>moonMask * lit</c> instead of the mask alone reds B2: the unlit
    /// side turns transparent and the star field shows through the disc.</item>
    /// <item>Removing the moon's zenith frame guard reds B3 — not by producing a NaN, but by collapsing
    /// the surface frame so the disc renders as featureless gray, which is why B3 asserts detail survives
    /// rather than only that the pixels are finite.</item>
    /// <item>Swapping the zenith and horizon colors in the gradient lerp reds B5.</item>
    /// <item>Restoring the original haze-then-airlight order reds B6: the unlit moon's ratio to the sky
    /// drifts from 0.94 to 1.42 as it descends, because that order pays for the same atmosphere twice.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Under <c>-nographics</c> there is no device to render with, so every scenario reports
    /// <b>INCONCLUSIVE</b> and passes rather than failing a headless run — the same convention the meshing
    /// suite's zero-allocation scenario uses when its runtime cannot measure.
    /// </para>
    /// </summary>
    public static class SkyRenderValidationSuite
    {
        /// <summary>Render size for scenarios that inspect a disc filling the frame.</summary>
        private const int DISC_RENDER_SIZE = 256;

        /// <summary>Narrow field of view that makes a ~1.7 degree disc fill the frame.</summary>
        private const float DISC_FIELD_OF_VIEW = 6f;

        /// <summary>Radius in pixels around the frame center excluded from disc statistics.</summary>
        /// <remarks>
        /// At a new moon the sun is collinear with the moon by definition, and its feathered mask covers a
        /// few pixels at the exact center even at a sub-pixel angular radius. A summary statistic taken
        /// over the whole disc measures those pixels and reports the <i>sun</i> — which once produced a
        /// confidently-filed defect that did not exist.
        /// </remarks>
        private const float CENTRE_EXCLUSION_RADIUS = 8f;

        /// <summary>
        /// Outer radius in pixels of the disc sample, comfortably inside the rendered disc's edge.
        /// </summary>
        /// <remarks>
        /// At <see cref="DISC_FIELD_OF_VIEW"/> a 1.7 degree disc has a radius of about 72 px in a
        /// <see cref="DISC_RENDER_SIZE"/> frame; sampling to 55 stays clear of the feathered rim and of
        /// the sky beyond it.
        /// </remarks>
        private const float DISC_SAMPLE_RADIUS = 55f;

        /// <summary>Tolerance for a linear color round trip, allowing half-float quantization.</summary>
        private const float COLOR_EPSILON = 0.002f;

        /// <summary>The engine's authored night zenith, in linear values.</summary>
        /// <remarks>
        /// Taken from the shipped gradient rather than invented. The value matters: earthshine on the
        /// moon's unlit side fades against sky brightness, so a fixture merely "dark blue" — an earlier
        /// draft used luminance 0.0136 against the real 0.0062 — sits partway up that ramp and measures a
        /// half-faded moon while claiming to measure a night one.
        /// </remarks>
        private static readonly Color s_nightSky = new Color(0.004f, 0.005f, 0.024f, 1f);

        /// <summary>A daylight sky, in linear values.</summary>
        private static readonly Color s_daySky = new Color(0.180f, 0.340f, 0.700f, 1f);

        /// <summary>Runs every scenario and prints a categorized summary via the shared runner.</summary>
        [MenuItem("Minecraft Clone/Dev/Validate Sky Render")]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the rendered-sky scenarios, returning the categorized result (the headless/CI entry point).
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result.</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar.</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>
            {
                new Scenario("B1 A linear color survives the render round trip unchanged", RunB1ColorRoundTrip),
                new Scenario("B2 The moon disc is opaque - it occludes the star field", RunB2DiscOpacity),
                new Scenario("B3 No configuration renders a NaN, and the zenith moon keeps its detail", RunB3Degenerate),
                new Scenario("B4 The sun disc is brighter than the sky around it", RunB4SunBrighterThanSky),
                new Scenario("B5 The gradient is the right way up - zenith overhead, horizon at the horizon", RunB5GradientOrientation),
                new Scenario("B6 The unlit moon is a constant silhouette by day and visible at night", RunB6MoonAirlight),
            };

            return ValidationSuiteRunner.Execute("Sky Render", scenarios, KnownBugChannel.Unimplemented,
                logToConsole, showProgress);
        }

        /// <summary>Logs a single assertion as PASS/FAIL and returns its result for AND-chaining.</summary>
        /// <param name="label">Human-readable assertion description.</param>
        /// <param name="condition">The asserted condition.</param>
        /// <returns><paramref name="condition"/>.</returns>
        private static bool Check(string label, bool condition)
        {
            if (condition) Debug.Log($"  [PASS] {label}");
            else Debug.LogError($"  [FAIL] {label}");
            return condition;
        }

        /// <summary>
        /// True when this session cannot render, after logging the skip.
        /// </summary>
        /// <param name="scenario">Scenario name for the log line.</param>
        /// <returns>True when the caller should report a vacuous pass.</returns>
        private static bool SkipWithoutGraphics(string scenario)
        {
            if (SkyPreviewRenderer.IsSupported) return false;

            Debug.LogWarning($"  [INCONCLUSIVE] {scenario}: no graphics device (running with -nographics?) — " +
                             "rendered pixels cannot be observed here.");
            return true;
        }

        /// <summary>Relative luminance of a linear color.</summary>
        /// <param name="c">The color.</param>
        /// <returns>Its luminance.</returns>
        private static float Luminance(Color c) => c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f;

        /// <summary>
        /// A sky with the given background, a moon of the standard size, and nothing else contributing.
        /// </summary>
        /// <param name="sky">Background color, in linear values.</param>
        /// <param name="moonDirection">Direction to the moon.</param>
        /// <param name="sunDirection">Direction to the sun.</param>
        /// <param name="phase">Moon lit fraction.</param>
        /// <param name="starBrightness">Star field brightness.</param>
        /// <returns>The state.</returns>
        private static SkyPreviewState MoonScene(Color sky, Vector3 moonDirection, Vector3 sunDirection,
            float phase, float starBrightness)
        {
            SkyPreviewState state = SkyPreviewState.Uniform(sky);
            state.MoonDirection = moonDirection.normalized;
            state.SunDirection = sunDirection.normalized;
            state.MoonPhase = phase;
            state.MoonAngularRadius = 1.7f;

            // A pinpoint sun keeps the eclipse rule from painting the moon at collinear configurations.
            state.SunAngularRadius = 0.001f;
            state.StarBrightness = starBrightness;
            state.SkyRotation = Quaternion.identity;
            return state;
        }

        /// <summary>
        /// Summarizes the disc pixels of the last render, excluding the center.
        /// </summary>
        /// <param name="renderer">The renderer holding the last result.</param>
        /// <param name="minimum">Receives the lowest luminance found.</param>
        /// <param name="maximum">Receives the highest luminance found.</param>
        /// <returns>True when every sampled pixel was finite.</returns>
        private static bool SummarizeDisc(SkyPreviewRenderer renderer, out float minimum, out float maximum)
        {
            const int inset = 56;
            const float centre = DISC_RENDER_SIZE * 0.5f;
            bool finite = true;
            minimum = float.PositiveInfinity;
            maximum = float.NegativeInfinity;

            for (int y = inset; y < DISC_RENDER_SIZE - inset; y++)
            for (int x = inset; x < DISC_RENDER_SIZE - inset; x++)
            {
                float dx = x - centre;
                float dy = y - centre;
                float distanceSquared = dx * dx + dy * dy;

                // Strictly inside the disc. A square box of the disc's own radius reaches 1.41x that at
                // its corners and takes in sky, which then dominates min/max and makes any statement
                // about the SURFACE insensitive — measured, it let a collapsed surface frame pass.
                if (distanceSquared > DISC_SAMPLE_RADIUS * DISC_SAMPLE_RADIUS) continue;
                if (distanceSquared <= CENTRE_EXCLUSION_RADIUS * CENTRE_EXCLUSION_RADIUS) continue;

                Color c = renderer.SampleLinear(x, y);
                if (float.IsNaN(c.r) || float.IsNaN(c.g) || float.IsNaN(c.b) ||
                    float.IsInfinity(c.r) || float.IsInfinity(c.g) || float.IsInfinity(c.b))
                {
                    finite = false;
                    continue;
                }

                float lum = Luminance(c);
                if (lum < minimum) minimum = lum;
                if (lum > maximum) maximum = lum;
            }

            return finite;
        }

        /// <summary>Sums the luminance of a rectangular region of the last render.</summary>
        /// <param name="renderer">The renderer holding the last result.</param>
        /// <param name="x0">Left bound, inclusive.</param>
        /// <param name="x1">Right bound, exclusive.</param>
        /// <param name="y0">Bottom bound, inclusive.</param>
        /// <param name="y1">Top bound, exclusive.</param>
        /// <returns>The summed luminance.</returns>
        private static float SumRegion(SkyPreviewRenderer renderer, int x0, int x1, int y0, int y1)
        {
            float sum = 0f;
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                sum += Luminance(renderer.SampleLinear(x, y));

            return sum;
        }

        /// <summary>B1 — the render path does not silently re-encode the colors it is given.</summary>
        /// <returns>True when every assertion holds.</returns>
        /// <remarks>
        /// The known-answer test for the whole pipeline. A sky of one flat color with no stars, no fog and
        /// both discs below the horizon has exactly one correct answer, so any deviation is the round trip
        /// rather than the sky. Values span daylight down to the night sky's authored 0.004.
        /// </remarks>
        private static bool RunB1ColorRoundTrip()
        {
            if (SkipWithoutGraphics("B1")) return true;

            float[] probes = { 0.5f, 0.25f, 0.075f, 0.004f };
            bool ok = true;

            using SkyPreviewRenderer renderer = new SkyPreviewRenderer();
            foreach (float value in probes)
            {
                renderer.Render(SkyPreviewState.Uniform(new Color(value, value, value, 1f)),
                    Vector3.forward, 64, 64);
                Color got = renderer.SampleLinear(32, 32);

                ok &= Check($"linear {value:F4} renders as {got.r:F5} (sRGB encoding would give " +
                            $"{(value <= 0.0031308f ? 12.92f * value : 1.055f * Mathf.Pow(value, 1f / 2.4f) - 0.055f):F5})",
                    Mathf.Abs(got.r - value) <= COLOR_EPSILON);
            }

            return ok;
        }

        /// <summary>
        /// B2 — the disc composites by its mask alone, so the night side hides the stars behind it.
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        /// <remarks>
        /// The control is load-bearing and was added after the first version of this test passed
        /// vacuously: with the sun on the horizon <c>starFade</c> is zero, so neither render had stars and
        /// "the disc did not change" proved nothing. Stars are also sparse sub-cell points, so only a
        /// summed region detects them — a handful of sampled pixels hits none.
        /// </remarks>
        private static bool RunB2DiscOpacity()
        {
            if (SkipWithoutGraphics("B2")) return true;

            Vector3 moon = new Vector3(0f, 0.4f, 1f).normalized;

            // Perpendicular to the moon AND below the horizon, so the star field is actually lit.
            Vector3 sun = new Vector3(0f, -moon.z, moon.y).normalized;

            // A HUGE disc, so the region sampled behind it certainly contains stars. At the shipped 1.7
            // degrees the disc covers so little sky that a sampled block can contain none, and "unchanged"
            // then means "nothing was there" rather than "the moon hid it" — measured, a mutation that
            // made the unlit side fully transparent still passed this scenario at the real size.
            const float largeRadius = 25f;
            const float wideFov = 60f;
            const int discInset = 78;
            const int skyEdge = 26;

            using SkyPreviewRenderer renderer = new SkyPreviewRenderer();
            SkyPreviewState starless = MoonScene(s_nightSky, moon, sun, 0.5f, 0f);
            starless.MoonAngularRadius = largeRadius;
            SkyPreviewState starry = starless;
            starry.StarBrightness = 3f;

            renderer.Render(starless, moon, DISC_RENDER_SIZE, DISC_RENDER_SIZE, wideFov);
            float discOff = SumRegion(renderer, discInset, DISC_RENDER_SIZE - discInset,
                discInset, DISC_RENDER_SIZE - discInset);
            float skyOff = SumRegion(renderer, 0, DISC_RENDER_SIZE, 0, skyEdge);

            renderer.Render(starry, moon, DISC_RENDER_SIZE, DISC_RENDER_SIZE, wideFov);
            float discOn = SumRegion(renderer, discInset, DISC_RENDER_SIZE - discInset,
                discInset, DISC_RENDER_SIZE - discInset);
            float skyOn = SumRegion(renderer, 0, DISC_RENDER_SIZE, 0, skyEdge);

            float skyDelta = skyOn - skyOff;
            bool ok = Check($"CONTROL: stars change the open sky beside the disc by {skyDelta:F3}",
                skyDelta > 0.5f);

            ok &= Check($"the disc interior is unchanged by the star field (delta {discOn - discOff:F5})",
                Mathf.Abs(discOn - discOff) <= 1e-4f);
            return ok;
        }

        /// <summary>
        /// B3 — the three degenerate configurations render finite pixels, and the zenith one keeps its
        /// surface detail.
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        /// <remarks>
        /// Finiteness alone is not enough: removing the moon's zenith frame guard does not produce a NaN,
        /// it produces a <i>featureless</i> disc. Detail is therefore asserted as a spread across the
        /// face, measured against the same disc at an ordinary elevation.
        /// </remarks>
        private static bool RunB3Degenerate()
        {
            if (SkipWithoutGraphics("B3")) return true;

            using SkyPreviewRenderer renderer = new SkyPreviewRenderer();
            // New moon: the sun is exactly collinear with the moon, the sunward-vector degeneracy.
            Vector3 moon = new Vector3(0f, 0.4f, 1f).normalized;
            renderer.Render(MoonScene(s_nightSky, moon, moon, 0f, 0f), moon,
                DISC_RENDER_SIZE, DISC_RENDER_SIZE, DISC_FIELD_OF_VIEW);
            bool ok = Check("a new moon, with the sun exactly collinear, renders finite pixels",
                SummarizeDisc(renderer, out _, out _));

            // Full moon: collinear in the other direction.
            renderer.Render(MoonScene(s_nightSky, moon, -moon, 1f, 0f), moon,
                DISC_RENDER_SIZE, DISC_RENDER_SIZE, DISC_FIELD_OF_VIEW);
            ok &= Check("a full moon, with the sun exactly opposite, renders finite pixels",
                SummarizeDisc(renderer, out float referenceMin, out float referenceMax));
            float referenceSpread = referenceMax - referenceMin;

            // Zenith: world up is collinear with the moon, the surface-frame degeneracy.
            renderer.Render(MoonScene(s_nightSky, Vector3.up, Vector3.down, 1f, 0f), Vector3.up,
                DISC_RENDER_SIZE, DISC_RENDER_SIZE, DISC_FIELD_OF_VIEW);
            ok &= Check("a moon at the exact zenith renders finite pixels",
                SummarizeDisc(renderer, out float zenithMin, out float zenithMax));

            float zenithSpread = zenithMax - zenithMin;
            ok &= Check($"and keeps its surface detail there — spread {zenithSpread:F4} against " +
                        $"{referenceSpread:F4} at an ordinary elevation",
                zenithSpread >= referenceSpread * 0.5f);

            return ok;
        }

        /// <summary>B4 — the sun reads as a light source rather than as a disc lost in the sky.</summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB4SunBrighterThanSky()
        {
            if (SkipWithoutGraphics("B4")) return true;

            Vector3 sun = new Vector3(0f, 0.5f, 1f).normalized;

            using SkyPreviewRenderer renderer = new SkyPreviewRenderer();
            SkyPreviewState state = SkyPreviewState.Uniform(s_daySky);
            state.SunDirection = sun;
            state.SunAngularRadius = 1.5f;

            // Keep the moon out of frame so this measures the sun alone.
            state.MoonDirection = -sun;
            state.MoonAngularRadius = 0.001f;

            renderer.Render(state, sun, DISC_RENDER_SIZE, DISC_RENDER_SIZE, DISC_FIELD_OF_VIEW);

            float disc = Luminance(renderer.SampleLinear(DISC_RENDER_SIZE / 2, DISC_RENDER_SIZE / 2));
            float sky = Luminance(renderer.SampleLinear(4, 4));

            return Check($"the sun's centre ({disc:F4}) outshines the sky beside it ({sky:F4})",
                disc > sky * 1.5f);
        }

        /// <summary>B5 — the gradient's ends are not swapped.</summary>
        /// <returns>True when every assertion holds.</returns>
        /// <remarks>
        /// Uses colors distinguishable by hue rather than by brightness, so the assertion cannot be
        /// satisfied by a render that merely darkens with elevation.
        /// </remarks>
        private static bool RunB5GradientOrientation()
        {
            if (SkipWithoutGraphics("B5")) return true;

            Color zenith = new Color(0.05f, 0.05f, 0.80f, 1f);
            Color horizon = new Color(0.80f, 0.05f, 0.05f, 1f);

            using SkyPreviewRenderer renderer = new SkyPreviewRenderer();
            SkyPreviewState state = SkyPreviewState.Uniform(zenith);
            state.HorizonColor = horizon;
            state.FogColor = horizon;

            renderer.Render(state, Vector3.up, 64, 64, 20f);
            Color overhead = renderer.SampleLinear(32, 32);

            renderer.Render(state, Vector3.forward, 64, 64, 20f);
            Color atHorizon = renderer.SampleLinear(32, 32);

            bool ok = Check($"looking up is blue, not red (r {overhead.r:F3}, b {overhead.b:F3})",
                overhead.b > overhead.r);
            ok &= Check($"looking at the horizon is red, not blue (r {atHorizon.r:F3}, b {atHorizon.b:F3})",
                atHorizon.r > atHorizon.b);
            return ok;
        }

        /// <summary>
        /// B6 — the atmosphere in front of the moon is one model, not two.
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        /// <remarks>
        /// The elevation-independence assertion is the valuable half. Haze and airlight both describe the
        /// air between viewer and disc, and applying them in the wrong order double-counts it — which does
        /// not show up as a wrong number at any single elevation, only as a ratio that <i>drifts</i> as the
        /// moon descends. Measured before the fix: 0.94 high, 1.42 near the horizon.
        /// </remarks>
        private static bool RunB6MoonAirlight()
        {
            if (SkipWithoutGraphics("B6")) return true;

            float[] elevations = { 0.02f, 0.15f, 0.40f, 0.90f };
            Vector3 sunUp = new Vector3(0.3f, 0.9f, 0.3f).normalized;
            Vector3 sunDown = new Vector3(0.3f, -0.9f, 0.3f).normalized;

            // Fog MUST be on. The horizon haze is gated on a non-empty fog range, and a state with fog
            // off has no haze term at all — under which the correct ordering and the double-counting one
            // are literally the same expression. Measured: with fog off this scenario passed the very
            // mutation it exists to catch.
            Vector4 fog = AtmosphericFog.ComputeFogRange(10, SkyPreviewRenderer.DefaultFarClip,
                AtmosphericFog.DefaultFogStartFraction, AtmosphericFog.DefaultFogCurvePower, FogStyle.Full);

            using SkyPreviewRenderer renderer = new SkyPreviewRenderer();
            float lowest = float.PositiveInfinity;
            float highest = float.NegativeInfinity;
            bool allBelowSky = true;

            foreach (float elevation in elevations)
            {
                Vector3 moon = new Vector3(0.3f, elevation, 0.6f).normalized;
                SkyPreviewState day = MoonScene(s_daySky, moon, sunUp, 0f, 0f);
                day.FogRange = fog;
                day.FogColor = s_daySky;

                renderer.Render(day, moon, DISC_RENDER_SIZE, DISC_RENDER_SIZE, DISC_FIELD_OF_VIEW);

                float disc = Luminance(renderer.SampleLinear(108, 128));
                float sky = Luminance(renderer.SampleLinear(DISC_RENDER_SIZE - 6, 128));
                float ratio = disc / Mathf.Max(sky, 1e-6f);

                if (ratio >= 1f) allBelowSky = false;
                lowest = Mathf.Min(lowest, ratio);
                highest = Mathf.Max(highest, ratio);
            }

            bool ok = Check($"by day the unlit moon sits below the sky at every elevation " +
                            $"(ratios {lowest:F3}..{highest:F3})", allBelowSky);

            ok &= Check($"and by the same amount regardless of elevation (spread {highest - lowest:F4})",
                highest - lowest <= 0.02f);

            // At night the same disc must remain visible, or the moon disappears after dark.
            Vector3 nightMoon = new Vector3(0.2f, 0.9f, 0.35f).normalized;
            SkyPreviewState night = MoonScene(s_nightSky, nightMoon, sunDown, 0f, 0f);
            night.FogRange = fog;
            night.FogColor = s_nightSky;

            renderer.Render(night, nightMoon, DISC_RENDER_SIZE, DISC_RENDER_SIZE, DISC_FIELD_OF_VIEW);

            float nightDisc = Luminance(renderer.SampleLinear(DISC_RENDER_SIZE / 2, DISC_RENDER_SIZE / 2));
            float nightSky = Luminance(renderer.SampleLinear(4, 4));
            ok &= Check($"at night the unlit moon is still visible against the sky " +
                        $"({nightDisc:F4} against {nightSky:F4})", nightDisc > nightSky * 2f);

            return ok;
        }
    }
}
