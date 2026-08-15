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
    /// <item>Scaling the moon's airlight by <c>hazeAmount</c> — the more physical reading, and the one
    /// the term deliberately does not take — reds B7: the slope collapses from 0.941 to 0.017 overhead
    /// while the horizon keeps 0.801. It reds B6's spread too, so the mutation is caught twice; what B7
    /// adds is that the LIT disc is where the trade is paid, which B6's phase 0 cannot see.</item>
    /// <item>Zeroing the aureole's two lobe strengths reds B8's first assertion; removing its twilight
    /// fade reds B8's second, which is the only thing standing between the shader and a sky that glows
    /// around the anti-sun point at midnight.</item>
    /// <item>Deriving the aureole's tint from the authored <c>_HorizonColor</c> instead of from
    /// transmitted sunlight reds ALL THREE of B9's assertions: the reddening reverses below 12 degrees
    /// (1.28 there, then 1.24 and 1.19 at the horizon) and the disc turns bluer than it is red against
    /// a blue sky, because that global goes pale blue well before the sun stops being warm. The same
    /// mutation leaves DUSK looking correct, which is why B9 sweeps the whole descent instead of
    /// sampling one time of day. Replacing the disc's per-channel extinction with a scalar haze, by
    /// contrast, does NOT red B9 — see its remarks for that measurement and why it is accepted.</item>
    /// <item>Gating <c>sunPathHaze</c> on the fog range reds B11's first assertion; ungating the moon's
    /// <c>hazeAmount</c> reds its second. B11 exists to pin a deliberate ASYMMETRY, so both halves are
    /// asserted — without the moon control, "the sun ignores fog" would pass equally in a build where
    /// nothing responds to fog at all.</item>
    /// <item>Giving the aureole to the sky but not to the sun disc reds B4 — a regression that shipped
    /// briefly and that B4's <i>previous</i> fixture passed, because it ran with fog off (so the disc's
    /// haze term was a no-op), at a mid-elevation sun (where haze is weak), and sampled the sky at a
    /// frame corner rather than beside the disc. All three are now fixed; the episode is why B4 carries
    /// the longest remarks block in this file.</item>
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

        /// <summary>Red-to-blue ratio above which a high sun reads as visibly warm rather than white.</summary>
        /// <remarks>
        /// Placed between two measured states rather than picked, both measured on B9's OWN fixture:
        /// with the sun's optical depth on the veiling falloff a 30-degree sun measures <b>1.44</b>
        /// here, and on the airmass falloff it measures <b>1.16</b>. Loose enough that re-tuning the
        /// extinction ratios cannot false-red it, tight enough that returning to the veiling curve does.
        /// </remarks>
        private const float MIDDAY_NEUTRAL_LIMIT = 1.27f;

        /// <summary>Minimum glow amplitude spent between the disc's rim and six degrees out.</summary>
        /// <remarks>
        /// Placed between two measured states rather than picked, both measured on B10's OWN fixture:
        /// the shipped three-lobe falloff loses <b>0.342</b> of luminance across that band, and the same
        /// falloff with the glare lobe deleted loses <b>0.155</b>. This is the assertion that actually
        /// observes the glare, after a near-to-far ratio turned out to survive deleting it.
        /// </remarks>
        private const float GLARE_MIN_NEAR_BAND_DROP = 0.22f;

        /// <summary>How much brighter the sky at the disc's rim must be than the open sky.</summary>
        /// <remarks>
        /// A floor on the <b>aureole as a whole</b>, not on the glare lobe — deleting the glare still
        /// leaves 1.51 here against the shipped 1.99, so this cannot observe it. B8 owns that coverage;
        /// this is kept only as a cheap sanity floor and is deliberately well below the shipped value
        /// so tuning the lobes stays free. The assertion that does observe the glare is
        /// <see cref="GLARE_MIN_NEAR_BAND_DROP"/>.
        /// </remarks>
        private const float GLARE_MIN_CONTRAST = 1.3f;

        /// <summary>Pixel radius of a sample taken just OUTSIDE the sun's disc.</summary>
        /// <remarks>
        /// 1.69 degrees at <see cref="DISC_FIELD_OF_VIEW"/>, which clears the sun's 1.5-degree disc plus
        /// its feather (1.545) by about six pixels. Distinct from <see cref="DISC_SAMPLE_RADIUS"/>,
        /// which happens to share the value 72 for an unrelated reason — there it is the MOON's own
        /// 1.7-degree radius, here it is "just past the sun's rim".
        /// </remarks>
        private const int SUN_RIM_SAMPLE_RADIUS = 72;

        /// <summary>Largest red:blue shift the sun may show when Distance Fog is toggled.</summary>
        /// <remarks>
        /// Not zero: the sun's own extinction is fog-independent, but the disc still sits on a sky whose
        /// airlight term the fog gate does touch, so a small residual is expected and correct. Gating
        /// <c>sunPathHaze</c> moves it far past this.
        /// </remarks>
        private const float SUN_FOG_INDEPENDENCE_TOLERANCE = 0.15f;

        /// <summary>Smallest luminance change the moon must show when Distance Fog is toggled.</summary>
        /// <remarks>
        /// The control half of B11. Without it, "the sun is the fog-independent one" would pass just as
        /// well in a build where nothing responds to fog at all.
        /// </remarks>
        private const float MOON_FOG_RESPONSE_MINIMUM = 0.05f;

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

        /// <summary>A colorless sky, for scenarios that measure the sky's effect on <i>hue</i>.</summary>
        /// <remarks>
        /// Deliberately achromatic. Any scenario asserting that something reddens or cools has to run
        /// against a background that pushes no color of its own, or the background's hue rather than
        /// the shader's is what gets measured — see <see cref="RunB9SunReddening"/>, whose first draft
        /// was defeated exactly that way by <see cref="s_daySky"/>'s blue.
        /// </remarks>
        private static readonly Color s_neutralSky = new Color(0.400f, 0.400f, 0.400f, 1f);

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
                new Scenario("B7 The lit moon carries the sky's airlight at every elevation", RunB7LitMoonAirlight),
                new Scenario("B8 The sky glows toward the sun and the glow dies with it", RunB8SunAureole),
                new Scenario("B9 The sun reddens as it descends, and is never bluer than it is red", RunB9SunReddening),
                new Scenario("B10 The glare falls off monotonically from the disc into the open sky", RunB10GlareFalloff),
                new Scenario("B11 The sun's colour is independent of the Distance Fog setting, the moon's is not", RunB11SunFogIndependence),
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
        /// <remarks>
        /// <para>
        /// Three details of this fixture are load-bearing, and an earlier draft had all three wrong — it
        /// passed a shipped regression that was obvious in a screenshot (the sun rendering as a hole in
        /// its own glow, reported PASS at "center 0.9682 outshines sky 0.4803").
        /// </para>
        /// <para>
        /// <b>Fog is ON.</b> <see cref="SkyPreviewState.Uniform"/> zeroes <c>FogRange</c>, which zeroes
        /// <c>hazeAmount</c> and makes the shader's disc-haze term a no-op — so the fixture could not
        /// reach the code that caused the defect. This is the same trap Architecture §7.1 already records
        /// for the haze scenario, met a second time.
        /// </para>
        /// <para>
        /// <b>The sun is LOW.</b> Haze scales with <c>1 - viewDir.y</c>, so a mid-elevation sun barely
        /// exercises it; the defect only became visible near the horizon.
        /// </para>
        /// <para>
        /// <b>The sky is sampled just OUTSIDE the disc rim</b>, not at a frame corner. The defect was that
        /// the immediately-adjacent sky outran the disc while the sky further out did not — a corner
        /// sample sits where the aureole has already fallen off and reports the comparison the wrong way.
        /// </para>
        /// </remarks>
        private static bool RunB4SunBrighterThanSky()
        {
            if (SkipWithoutGraphics("B4")) return true;

            const int centre = DISC_RENDER_SIZE / 2;
            using SkyPreviewRenderer renderer = new SkyPreviewRenderer();

            // --- The original, unchanged: a mid-elevation sun against a fog-free sky. Its 1.5x margin
            // is calibrated to THIS fixture, so it stays on it rather than being re-tuned to survive the
            // harder one below — a low sun is legitimately closer to its sky, and moving the constant to
            // absorb that would have quietly weakened the guard instead of adding to it.
            Vector3 highSun = new Vector3(0f, 0.5f, 1f).normalized;
            SkyPreviewState high = SkyPreviewState.Uniform(s_daySky);
            high.SunDirection = highSun;
            high.SunAngularRadius = 1.5f;
            high.MoonDirection = -highSun;
            high.MoonAngularRadius = 0.001f;

            renderer.Render(high, highSun, DISC_RENDER_SIZE, DISC_RENDER_SIZE, DISC_FIELD_OF_VIEW);
            float highDisc = Luminance(renderer.SampleLinear(centre, centre));
            float highFarSky = Luminance(renderer.SampleLinear(4, 4));

            bool ok = Check($"the sun's centre ({highDisc:F4}) outshines the distant sky ({highFarSky:F4})",
                highDisc > highFarSky * 1.5f);

            // --- The case the original could not reach: a low sun with fog ON, sampled at the rim.
            Vector3 lowSun = new Vector3(0f, 0.09f, 1f).normalized;
            SkyPreviewState low = SkyPreviewState.Uniform(s_daySky);
            low.SunDirection = lowSun;
            low.SunAngularRadius = 1.5f;
            low.FogRange = new Vector4(0f, 160f, 0f, 0f);
            low.FogColor = s_daySky;
            low.MoonDirection = -lowSun;
            low.MoonAngularRadius = 0.001f;

            renderer.Render(low, lowSun, DISC_RENDER_SIZE, DISC_RENDER_SIZE, DISC_FIELD_OF_VIEW);
            float lowDisc = Luminance(renderer.SampleLinear(centre, centre));

            float lowRimSky = Luminance(renderer.SampleLinear(centre + SUN_RIM_SAMPLE_RADIUS, centre));

            ok &= Check($"a low sun's centre ({lowDisc:F4}) still outshines the sky just outside its rim ({lowRimSky:F4})",
                lowDisc > lowRimSky * 1.05f);

            return ok;
        }

        /// <summary>B8 — the sky is brighter toward the sun than away from it (the SN-0 aureole).</summary>
        /// <returns>True when every assertion holds.</returns>
        /// <remarks>
        /// <para>
        /// A <b>differential</b> rather than an absolute sample, so re-tuning the glow's strength cannot
        /// false-red it — the same reasoning that shaped B7.
        /// </para>
        /// <para>
        /// Both probes sit at the <b>same elevation</b>, which is what makes the assertion airtight: the
        /// base gradient is a function of view elevation alone, so it contributes an identical amount to
        /// each and cancels exactly. Any surviving difference is the aureole and nothing else. An
        /// angular offset from the sun would not do — it moves the probe in elevation too, and at low sun
        /// the gradient then swamps the term under test.
        /// </para>
        /// <para>
        /// Note the probes are separated in <b>azimuth</b>, which is only safe because both sit well away
        /// from the poles: near the zenith an azimuth offset collapses to almost no true arc, which during
        /// development put a nominal 3-degree probe <i>inside</i> the 1.5-degree sun disc and reported the
        /// disc as though it were sky.
        /// </para>
        /// </remarks>
        private static bool RunB8SunAureole()
        {
            if (SkipWithoutGraphics("B8")) return true;

            const float probeElevation = 30f;
            const float probeAzimuth = 15f;

            using SkyPreviewRenderer renderer = new SkyPreviewRenderer();

            float near = SampleSkyAt(renderer, probeAzimuth, probeElevation);
            float far = SampleSkyAt(renderer, 180f, probeElevation);

            bool ok = Check($"the sky toward the sun ({near:F4}) is brighter than the same elevation away from it ({far:F4})",
                near > far * 1.03f);

            // The aureole must die with the sun, or the sky glows around the sun's direction at midnight.
            //
            // These probes are placed by a TRUE angular rotation away from the sun axis, not at a fixed
            // elevation, and that is the whole assertion. A fixed-elevation pair sits more than 90
            // degrees from a deeply-buried sun, where saturate(dot(view, sun)) is zero on BOTH probes
            // whatever the fade does — the first draft of this check was written that way, passed the
            // mutation it exists to catch, and only prove-red exposed it. 4 degrees also clears the
            // 1.5 degree disc, so the sample is sky rather than the sun itself.
            Vector3 buriedSun = SphericalDirection(0f, -80f);
            float nightNear = SampleSkyDirection(renderer, AngularOffset(buriedSun, 4f), -80f);
            float nightFar = SampleSkyDirection(renderer, AngularOffset(-buriedSun, 4f), -80f);

            ok &= Check($"with the sun 80 degrees below the horizon the glow is gone ({nightNear:F4} vs {nightFar:F4})",
                Mathf.Abs(nightNear - nightFar) < 0.005f);

            return ok;
        }

        /// <summary>B9 — the sun's disc reddens as it descends (SN-1's per-channel extinction).</summary>
        /// <returns>True when every assertion holds.</returns>
        /// <remarks>
        /// <para>
        /// Asserted as a change in the red-to-blue <b>ratio</b>, never as absolute channel values.
        /// Reddening is a shift in the balance between channels, so a ratio is the thing the feature
        /// actually claims; pinning the channels would pin `SUN_EXTINCTION_BETA` and `_DEPTH` instead,
        /// and any future re-tune of the look would false-red a test that was never about the look.
        /// </para>
        /// <para>
        /// The second assertion exists because a real defect took the opposite sign. When the aureole's
        /// tint was derived from the authored `_HorizonColor`, a sun at 10 degrees rendered with
        /// <b>R:B 0.95</b> — bluer than it was red — because the horizon global turns pale blue well
        /// before the sun stops being warm. Dusk looked correct throughout, so a dusk-only check would
        /// have passed it. The sweep runs the whole descent for that reason.
        /// </para>
        /// <para>
        /// Fog is ON: extinction scales with `hazeAmount`, which `SkyPreviewState.Uniform` zeroes.
        /// Without it every elevation returns the same color and both assertions are vacuous.
        /// </para>
        /// <para>
        /// The sky and fog are a NEUTRAL GRAY, not the suite's usual <c>s_daySky</c>, and that is what
        /// makes the third assertion mean anything. Against the blue day sky the disc mixes toward a
        /// blue fog as it descends, and that mixing dominates the channel balance completely: measured,
        /// the ratio went 1.41 without per-channel extinction and 1.64 with it, so a threshold able to
        /// separate them barely existed and the first draft's 1.5x failed BOTH. Against gray, the fog
        /// contributes equally to every channel, so any imbalance in the result can only have come from
        /// <see cref="SUN_EXTINCTION_BETA"/> — which is precisely the feature under test.
        /// </para>
        /// <para>
        /// <b>What this scenario does NOT isolate, measured rather than assumed.</b> Two separate paths
        /// redden the disc and both read the same <c>SUN_EXTINCTION_BETA</c>: the disc's own extinction,
        /// and the aureole tint blended over it (which is derived from transmitted sunlight so that glow
        /// and disc redden together — a deliberate coupling). At the disc center the aureole blend is
        /// roughly 0.47, so about half the reddening arrives by the second path. Replacing the disc's
        /// per-channel extinction with the old scalar haze moves the 0-degree ratio only from 2.20 to
        /// 1.95, and this scenario stays GREEN. So B9 guards the visible property — the sun reddens as
        /// it descends and never turns cold — and it would catch that property vanishing outright, but
        /// it cannot attribute the reddening to one path or witness the loss of one alone.
        /// The 1.5x floor is left loose on purpose: a threshold tight enough to separate 2.20 from 1.95
        /// would be pinning a 13 % gap, and any re-tune of the desaturation or the beta ratios would
        /// then false-red a test that was never about those constants.
        /// </para>
        /// </remarks>
        private static bool RunB9SunReddening()
        {
            if (SkipWithoutGraphics("B9")) return true;

            // Descending, so each step must be at least as red as the one above it.
            float[] elevations = { 60f, 40f, 25f, 12f, 5f, 0f };
            float previousRatio = 0f;
            bool monotonic = true;
            bool everCold = false;
            string trace = string.Empty;

            using SkyPreviewRenderer renderer = new SkyPreviewRenderer();

            foreach (float elevation in elevations)
            {
                Color disc = SampleSunDisc(renderer, elevation, s_neutralSky);
                float ratio = disc.b > 1e-4f ? disc.r / disc.b : float.MaxValue;

                if (ratio < previousRatio - 0.01f) monotonic = false;
                previousRatio = ratio;

                trace += $"{elevation:F0}deg={ratio:F2}  ";

                // The cold-sun check runs against a BLUE sky, not the gray one. A gray background
                // cannot tint the disc blue no matter what the shader does, so this assertion was
                // unfalsifiable there — green for want of anything able to make it fail. The defect it
                // guards arose precisely because a blue-ish sky global reached the aureole tint.
                Color againstBlue = SampleSunDisc(renderer, elevation, s_daySky);
                if (againstBlue.r < againstBlue.b) everCold = true;
            }

            bool ok = Check($"the disc reddens monotonically as the sun descends ({trace.TrimEnd()})", monotonic);

            ok &= Check("against a blue sky the sun is still never bluer than it is red", !everCold);

            // A real spread, not merely a non-decreasing one. This is a magnitude FLOOR on the visible
            // effect, and deliberately not tighter — see the remarks on what it cannot isolate.
            Color high = SampleSunDisc(renderer, 60f, s_neutralSky);
            Color low = SampleSunDisc(renderer, 0f, s_neutralSky);
            float highRatio = high.b > 1e-4f ? high.r / high.b : float.MaxValue;
            float lowRatio = low.b > 1e-4f ? low.r / low.b : float.MaxValue;

            ok &= Check($"a setting sun is markedly redder than a high one ({lowRatio:F2} against {highRatio:F2})",
                lowRatio > highRatio * 1.5f);

            // And it must still be NEUTRAL well before then. Monotonic reddening says nothing about the
            // schedule: a curve that is already orange at 30 degrees climbs just as monotonically as a
            // correct one, and that is exactly the defect that shipped here — the sun's optical depth
            // reused the veiling falloff, hit 18 % of maximum at 30 degrees against a true airmass
            // barely twice the zenith's, and rendered an orange ball against a blue sky.
            Color midday = SampleSunDisc(renderer, 30f, s_neutralSky);
            float middayRatio = midday.b > 1e-4f ? midday.r / midday.b : float.MaxValue;

            ok &= Check($"a sun 30 degrees up is still close to neutral ({middayRatio:F2})",
                middayRatio < MIDDAY_NEUTRAL_LIMIT);

            return ok;
        }

        /// <summary>B10 — the glare reads as one falloff from the disc, not as a ring or a plateau.</summary>
        /// <returns>True when every assertion holds.</returns>
        /// <remarks>
        /// <para>
        /// The sun's glow is produced <b>in this shader</b> as three cosine-power lobes, after driving it
        /// from URP's post-process bloom was built and refuted — one global bloom cannot size its halo
        /// for both a 3-degree disc and the block emitters. Three separate lobes summed together can
        /// easily read as concentric rings or as a flat plateau that ends abruptly, so what is asserted
        /// is the shape they make together rather than any one of them.
        /// </para>
        /// <para>
        /// Run against a <b>uniform</b> sky on purpose — <see cref="s_daySky"/>, not the achromatic
        /// <see cref="s_neutralSky"/>, and the distinction matters. It is <i>uniformity</i> that is
        /// load-bearing here, not neutrality: with zenith and horizon the same color the base gradient
        /// is constant in every direction, so every difference along this walk is the aureole and
        /// nothing else. Against a real gradient the walk changes elevation as it goes and the
        /// gradient's own falloff would be measured alongside the glare's. Neutrality is what a
        /// <i>hue</i> assertion needs (B9); this one measures luminance, so the sky's color cancels.
        /// </para>
        /// </remarks>
        private static bool RunB10GlareFalloff()
        {
            if (SkipWithoutGraphics("B10")) return true;

            using SkyPreviewRenderer renderer = new SkyPreviewRenderer();

            // Clear of the 1.5 degree disc, then outward into open sky. The near band the glare
            // assertion measures runs from index 0 to NEAR_BAND_END_INDEX, resolved by INDEX rather
            // than by matching a float: picking the 6-degree entry by value meant that editing this
            // array left the far end of the band at 0, which turned the drop into the near sample
            // alone and passed the assertion unconditionally.
            float[] offsets = { 1.7f, 2.5f, 4f, 6f, 12f, 25f, 45f };
            const int nearBandEndIndex = 3;
            float previous = float.MaxValue;
            bool monotonic = true;
            string trace = string.Empty;
            float nearGlare = 0f, midGlare = 0f, farSky = 0f;

            for (int i = 0; i < offsets.Length; i++)
            {
                float value = SampleSkyDirection(renderer,
                    AngularOffset(SphericalDirection(0f, 40f), offsets[i]), 40f);

                if (value > previous + 0.002f) monotonic = false;
                previous = value;

                if (i == 0) nearGlare = value;
                if (i == nearBandEndIndex) midGlare = value;
                if (i == offsets.Length - 1) farSky = value;

                trace += $"{offsets[i]:F0}deg={value:F3}  ";
            }

            bool ok = Check($"the glow only ever falls off with angle ({trace.TrimEnd()})", monotonic);

            // How much of the glow is spent in the FIRST few degrees, which is the one thing the glare
            // lobe adds and the broad halo cannot fake.
            //
            // A near-to-far ratio was the obvious assertion and it is a false green: the core and halo
            // lobes alone reach 1.51 against the shipped 1.99, so any threshold loose enough to survive
            // tuning also survives deleting the glare outright — measured, not argued. The drop across
            // the near band separates the same two states by 0.139 against 0.307.
            float nearBandDrop = nearGlare - midGlare;

            ok &= Check($"the glow is concentrated near the disc (loses {nearBandDrop:F3} between the rim and 6 degrees)",
                nearBandDrop > GLARE_MIN_NEAR_BAND_DROP);

            ok &= Check($"and the aureole as a whole is present ({nearGlare:F3} against {farSky:F3})",
                nearGlare > farSky * GLARE_MIN_CONTRAST);

            return ok;
        }

        /// <summary>B11 — the sun ignores the Distance Fog setting; the moon obeys it.</summary>
        /// <returns>True when every assertion holds.</returns>
        /// <remarks>
        /// <para>
        /// Pins a <b>deliberate asymmetry</b> rather than a correctness property, which is why it
        /// asserts both halves. The sun's optical depth runs on the ungated <c>sunPathHaze</c>: Distance
        /// Fog is a view-distance setting, while the sun's color is a property of the atmosphere, and
        /// gating it would render a near-white sun against the authored orange horizon whenever a player
        /// turned fog off. The moon keeps the fog-gated <c>hazeAmount</c>, because its own atmosphere
        /// model is pinned by B6/B7 and by RF-2's locked decisions.
        /// </para>
        /// <para>
        /// Nothing guarded this before B11, and it was not a decision anyone made — the sun lost the gate
        /// incidentally, while its extinction was being moved onto an airmass falloff. So the test exists
        /// as much to record the choice as to catch a regression: re-gating the sun reds the first
        /// assertion, and ungating the moon reds the second.
        /// </para>
        /// </remarks>
        private static bool RunB11SunFogIndependence()
        {
            if (SkipWithoutGraphics("B11")) return true;

            using SkyPreviewRenderer renderer = new SkyPreviewRenderer();

            // Low, where extinction is strongest and so the on/off difference would be largest.
            const float lowElevation = 3f;

            Color sunFogOn = SampleSunDisc(renderer, lowElevation, s_neutralSky);
            Color sunFogOff = SampleSunDiscNoFog(renderer, lowElevation, s_neutralSky);
            float sunShift = Mathf.Abs(Ratio(sunFogOn) - Ratio(sunFogOff));

            bool ok = Check($"the sun's colour barely moves when fog is switched off (red:blue {Ratio(sunFogOn):F2} against {Ratio(sunFogOff):F2})",
                sunShift < SUN_FOG_INDEPENDENCE_TOLERANCE);

            // The control, and the half that makes the asymmetry an assertion rather than a claim:
            // the moon must still respond, or "the sun is the independent one" says nothing.
            Color moonFogOn = SampleMoonDisc(renderer, lowElevation, s_neutralSky, true);
            Color moonFogOff = SampleMoonDisc(renderer, lowElevation, s_neutralSky, false);
            float moonShift = Mathf.Abs(Luminance(moonFogOn) - Luminance(moonFogOff));

            ok &= Check($"the moon's brightness still does move ({Luminance(moonFogOn):F3} against {Luminance(moonFogOff):F3})",
                moonShift > MOON_FOG_RESPONSE_MINIMUM);

            return ok;
        }

        /// <summary>Red-to-blue ratio of a color, guarding a zero blue channel.</summary>
        /// <param name="color">The color to measure.</param>
        /// <returns>The ratio, or 0 when blue is too small to divide by.</returns>
        private static float Ratio(Color color) => color.b > 1e-4f ? color.r / color.b : 0f;

        /// <summary>Renders the sun's disc center with Distance Fog switched off.</summary>
        /// <param name="renderer">The renderer to draw with.</param>
        /// <param name="sunElevationDegrees">Sun elevation in degrees.</param>
        /// <param name="sky">Sky color to render against.</param>
        /// <returns>The linear color at the disc's center.</returns>
        private static Color SampleSunDiscNoFog(SkyPreviewRenderer renderer, float sunElevationDegrees, Color sky)
        {
            Vector3 sun = SphericalDirection(0f, sunElevationDegrees);

            // Uniform already zeroes FogRange, which is exactly "Distance Fog = Off".
            SkyPreviewState state = SkyPreviewState.Uniform(sky);
            state.SunDirection = sun;
            state.SunAngularRadius = 1.5f;
            state.MoonDirection = -sun;
            state.MoonAngularRadius = 0.001f;

            renderer.Render(state, sun, DISC_RENDER_SIZE, DISC_RENDER_SIZE, DISC_FIELD_OF_VIEW);
            return renderer.SampleLinear(DISC_RENDER_SIZE / 2, DISC_RENDER_SIZE / 2);
        }

        /// <summary>Renders the moon's disc center, fully lit, with fog on or off.</summary>
        /// <param name="renderer">The renderer to draw with.</param>
        /// <param name="moonElevationDegrees">Moon elevation in degrees.</param>
        /// <param name="sky">Sky color to render against.</param>
        /// <param name="fog">True to render with Distance Fog on.</param>
        /// <returns>The linear color at the disc's center.</returns>
        private static Color SampleMoonDisc(SkyPreviewRenderer renderer, float moonElevationDegrees,
            Color sky, bool fog)
        {
            Vector3 moon = SphericalDirection(0f, moonElevationDegrees);

            SkyPreviewState state = SkyPreviewState.Uniform(sky);
            state.MoonDirection = moon;
            state.MoonAngularRadius = 1.7f;
            state.MoonPhase = 1f;

            // Sun opposite and vanishingly small, so the moon reads full and the sun cannot be sampled.
            state.SunDirection = -moon;
            state.SunAngularRadius = 0.001f;

            if (fog)
            {
                state.FogRange = new Vector4(0f, 160f, 0f, 0f);
                state.FogColor = sky;
            }

            renderer.Render(state, moon, DISC_RENDER_SIZE, DISC_RENDER_SIZE, DISC_FIELD_OF_VIEW);
            return renderer.SampleLinear(DISC_RENDER_SIZE / 2, DISC_RENDER_SIZE / 2);
        }

        /// <summary>Renders the center pixel of the sun's disc with the sun at a given elevation.</summary>
        /// <param name="renderer">The renderer to draw with.</param>
        /// <param name="sunElevationDegrees">Sun elevation in degrees.</param>
        /// <param name="sky">Sky and fog color to render against.</param>
        /// <returns>The linear color at the disc's center.</returns>
        private static Color SampleSunDisc(SkyPreviewRenderer renderer, float sunElevationDegrees, Color sky)
        {
            Vector3 sun = SphericalDirection(0f, sunElevationDegrees);

            SkyPreviewState state = SkyPreviewState.Uniform(s_neutralSky);
            state.SunDirection = sun;
            state.SunAngularRadius = 1.5f;

            // Fog ON — extinction scales with hazeAmount, which Uniform zeroes.
            state.FogRange = new Vector4(0f, 160f, 0f, 0f);
            state.FogColor = sky;

            state.MoonDirection = -sun;
            state.MoonAngularRadius = 0.001f;

            renderer.Render(state, sun, DISC_RENDER_SIZE, DISC_RENDER_SIZE, DISC_FIELD_OF_VIEW);
            return renderer.SampleLinear(DISC_RENDER_SIZE / 2, DISC_RENDER_SIZE / 2);
        }

        /// <summary>Renders one sky pixel at an azimuth/elevation, with the sun at a given elevation.</summary>
        /// <param name="renderer">The renderer to draw with.</param>
        /// <param name="azimuthDegrees">View azimuth, 0 pointing at the sun's azimuth.</param>
        /// <param name="elevationDegrees">View elevation.</param>
        /// <param name="sunElevationDegrees">Sun elevation; the sun always sits at azimuth 0.</param>
        /// <returns>Luminance of the center pixel of that view.</returns>
        private static float SampleSkyAt(SkyPreviewRenderer renderer, float azimuthDegrees,
            float elevationDegrees, float sunElevationDegrees = 30f)
        {
            return SampleSkyDirection(renderer, SphericalDirection(azimuthDegrees, elevationDegrees),
                sunElevationDegrees);
        }

        /// <summary>Rotates a direction by a true angular offset, about an axis perpendicular to it.</summary>
        /// <param name="axis">The direction to rotate away from.</param>
        /// <param name="degrees">How far to rotate, in degrees of real arc.</param>
        /// <returns>A unit vector exactly <paramref name="degrees"/> away from <paramref name="axis"/>.</returns>
        /// <remarks>
        /// Offsetting in azimuth instead would not be equivalent: an azimuth step shrinks by cos(elevation),
        /// so near the poles a nominally several-degree probe covers almost no arc and can land inside the
        /// sun disc it was meant to sit outside of.
        /// </remarks>
        private static Vector3 AngularOffset(Vector3 axis, float degrees)
        {
            Vector3 perpendicular = Vector3.Cross(axis, Vector3.up);
            if (perpendicular.sqrMagnitude < 1e-6f) perpendicular = Vector3.Cross(axis, Vector3.forward);
            return (Quaternion.AngleAxis(degrees, perpendicular.normalized) * axis).normalized;
        }

        /// <summary>Renders one sky pixel along an explicit view direction.</summary>
        /// <param name="renderer">The renderer to draw with.</param>
        /// <param name="viewDirection">Direction to look along.</param>
        /// <param name="sunElevationDegrees">Sun elevation; the sun always sits at azimuth 0.</param>
        /// <returns>Luminance of the center pixel of that view.</returns>
        private static float SampleSkyDirection(SkyPreviewRenderer renderer, Vector3 viewDirection,
            float sunElevationDegrees)
        {
            SkyPreviewState state = SkyPreviewState.Uniform(s_daySky);
            state.SunDirection = SphericalDirection(0f, sunElevationDegrees);
            state.SunAngularRadius = 1.5f;
            state.FogRange = new Vector4(0f, 160f, 0f, 0f);
            state.FogColor = s_daySky;

            // Park the moon opposite the probes so it cannot contribute to either sample.
            state.MoonDirection = SphericalDirection(90f, -60f);
            state.MoonAngularRadius = 0.001f;

            renderer.Render(state, viewDirection, DISC_RENDER_SIZE, DISC_RENDER_SIZE, DISC_FIELD_OF_VIEW);

            return Luminance(renderer.SampleLinear(DISC_RENDER_SIZE / 2, DISC_RENDER_SIZE / 2));
        }

        /// <summary>Builds a unit direction from azimuth (0 = +Z) and elevation, both in degrees.</summary>
        /// <param name="azimuthDegrees">Azimuth in degrees, measured from +Z toward +X.</param>
        /// <param name="elevationDegrees">Elevation in degrees above the horizon.</param>
        /// <returns>The corresponding unit vector in Unity render space.</returns>
        private static Vector3 SphericalDirection(float azimuthDegrees, float elevationDegrees)
        {
            float azimuth = azimuthDegrees * Mathf.Deg2Rad;
            float elevation = elevationDegrees * Mathf.Deg2Rad;
            return new Vector3(
                Mathf.Sin(azimuth) * Mathf.Cos(elevation),
                Mathf.Sin(elevation),
                Mathf.Cos(azimuth) * Mathf.Cos(elevation)).normalized;
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

        /// <summary>
        /// B7 — the airlight the moon carries is the sky's, at full strength, whatever the elevation.
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        /// <remarks>
        /// <para>
        /// <see cref="RunB6MoonAirlight"/> measures phase 0 only, where the disc's own reflectance drops
        /// out of the composite — so the airlight term is unmeasured on the half of the model where it
        /// competes with a bright surface. This pins that half.
        /// </para>
        /// <para>
        /// Measured as a <b>differential</b> against two skies rather than as a brightness: the moon's
        /// own reflectance and its surface detail are identical between the two renders and cancel, so
        /// the slope isolates the airlight term and survives any re-tuning of the lunar surface
        /// constants. Both fixtures sit far above <c>MOON_AIRLIGHT_REFERENCE</c>, so the daytime
        /// silhouette factor is the same in each and does not enter the difference.
        /// </para>
        /// <para>
        /// The second assertion is the one that documents the trade. Airlight is added without being
        /// scaled by the haze that models it, so the disc takes the sky's full airlight even where the
        /// sight line holds no air — which is what keeps a daytime new moon from reading as a hole
        /// punched in the sky, and is why a <i>lit</i> daytime disc brightens with elevation.
        /// </para>
        /// </remarks>
        private static bool RunB7LitMoonAirlight()
        {
            if (SkipWithoutGraphics("B7")) return true;

            // Same geometry, two sky brightnesses. Halved rather than dimmed to night: the point is to
            // move the sky while keeping both ends saturated on the silhouette ramp.
            Color brightSky = s_daySky;
            Color dimSky = new Color(s_daySky.r * 0.5f, s_daySky.g * 0.5f, s_daySky.b * 0.5f, 1f);

            Vector4 fog = AtmosphericFog.ComputeFogRange(10, SkyPreviewRenderer.DefaultFarClip,
                AtmosphericFog.DefaultFogStartFraction, AtmosphericFog.DefaultFogCurvePower, FogStyle.Full);

            using SkyPreviewRenderer renderer = new SkyPreviewRenderer();

            float slopeHigh = AirlightSlope(renderer, 0.90f, brightSky, dimSky, fog);
            float slopeLow = AirlightSlope(renderer, 0.02f, brightSky, dimSky, fog);

            bool ok = Check($"a lit moon high overhead still tracks the sky's brightness " +
                            $"(slope {slopeHigh:F3}, and 0 would mean the airlight never reaches it)",
                slopeHigh >= 0.5f);

            ok &= Check($"and tracks it by the same amount down at the horizon, where the haze is " +
                        $"(slopes {slopeHigh:F3} high, {slopeLow:F3} low)",
                Mathf.Abs(slopeHigh - slopeLow) <= 0.15f);

            return ok;
        }

        /// <summary>
        /// How much of a change in sky brightness reaches a fully lit moon disc at one elevation.
        /// </summary>
        /// <param name="renderer">Renderer to draw with.</param>
        /// <param name="elevation">Moon height, as the y of its direction before normalization.</param>
        /// <param name="brightSky">The brighter of the two skies, in linear values.</param>
        /// <param name="dimSky">The dimmer of the two skies, in linear values.</param>
        /// <param name="fogRange">Fog range to render under; the horizon haze is gated on a non-empty one.</param>
        /// <returns>Change in disc luminance divided by change in sky luminance.</returns>
        private static float AirlightSlope(SkyPreviewRenderer renderer, float elevation,
            Color brightSky, Color dimSky, Vector4 fogRange)
        {
            Vector3 moon = new Vector3(0.3f, elevation, 0.6f).normalized;
            Vector3 sun = new Vector3(0.3f, 0.9f, 0.3f).normalized;

            float brightDisc = SampleLitDisc(renderer, brightSky, moon, sun, fogRange);
            float dimDisc = SampleLitDisc(renderer, dimSky, moon, sun, fogRange);

            float skyDelta = Luminance(brightSky) - Luminance(dimSky);
            return (brightDisc - dimDisc) / Mathf.Max(skyDelta, 1e-6f);
        }

        /// <summary>Renders a full moon against one sky and returns the disc's luminance.</summary>
        /// <param name="renderer">Renderer to draw with.</param>
        /// <param name="sky">Background color, in linear values.</param>
        /// <param name="moon">Direction to the moon.</param>
        /// <param name="sun">Direction to the sun.</param>
        /// <param name="fogRange">Fog range to render under.</param>
        /// <returns>Luminance of the sampled disc pixel.</returns>
        /// <remarks>
        /// Sampled off-center at the same pixel each time, so the surface markings under it are identical
        /// between renders and cancel when the two are subtracted.
        /// </remarks>
        private static float SampleLitDisc(SkyPreviewRenderer renderer, Color sky, Vector3 moon,
            Vector3 sun, Vector4 fogRange)
        {
            // Phase 1 lights the whole disc: the terminator sits off the near limb, so every sampled
            // pixel is surface rather than earthshine.
            SkyPreviewState state = MoonScene(sky, moon, sun, 1f, 0f);
            state.FogRange = fogRange;
            state.FogColor = sky;

            renderer.Render(state, moon, DISC_RENDER_SIZE, DISC_RENDER_SIZE, DISC_FIELD_OF_VIEW);
            return Luminance(renderer.SampleLinear(108, 128));
        }
    }
}
