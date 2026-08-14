using System.Collections.Generic;
using Editor.Validation.Framework;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.UIBlur
{
    /// <summary>
    /// Renders the <c>Custom/MaskedUIBlur</c> shader in edit mode and asserts its UI compositing contract:
    /// what it does with the blurred screen it samples, with the material tints, with the UI vertex color,
    /// and with a clip rect.
    /// <para>
    /// Like <c>SkyRenderValidationSuite</c> it compares against computed values rather than reference
    /// images — GPU output is not bit-reproducible across drivers, so checked-in goldens would fail for
    /// reasons unrelated to the shader. Every assertion here is arithmetic the shader must satisfy on any
    /// correct renderer.
    /// </para>
    /// <para>
    /// B4 and B5 exist because of UI_BUGS #06: the shader declared no <c>COLOR</c> semantic and no clip
    /// support, so every panel rendered fully opaque and unclipped — blur panels could not stack, and a
    /// full-screen one hid whatever UI was beneath it.
    /// </para>
    /// <para>
    /// Under <c>-nographics</c> there is no device to render with, so every scenario reports
    /// <b>INCONCLUSIVE</b> and passes rather than failing a headless run — the convention the meshing and
    /// sky-render suites already use when their measurement cannot be taken.
    /// </para>
    /// </summary>
    public static class UIBlurRenderValidationSuite
    {
        /// <summary>Tolerance for a linear color comparison, allowing half-float quantization.</summary>
        private const float COLOR_EPSILON = 0.004f;

        /// <summary>Column sampled well inside the quad.</summary>
        private const int INSIDE_X = 12;

        /// <summary>Column sampled in the backdrop strip to the right of the quad.</summary>
        private const int BACKDROP_X = 56;

        /// <summary>Column sampled inside the quad but outside the clip rect used by B5.</summary>
        private const int CLIPPED_X = 36;

        /// <summary>Right edge of B5's clip rect, splitting the quad.</summary>
        private const float CLIP_RECT_RIGHT = 24f;

        /// <summary>Row sampled by every scenario.</summary>
        private const int SAMPLE_Y = 32;

        /// <summary>The blurred screen color every scenario feeds in, in linear values.</summary>
        private static readonly Color s_blurSource = new Color(0.25f, 0.5f, 0.75f, 1f);

        /// <summary>The color the target is cleared to, standing in for whatever is already on screen.</summary>
        /// <remarks>
        /// Deliberately unlike <see cref="s_blurSource"/> in every channel: a backdrop close to the blur
        /// would let a panel that ignores its alpha still land inside tolerance on B4.
        /// </remarks>
        private static readonly Color s_backdrop = new Color(0.9f, 0.1f, 0.05f, 1f);

        /// <summary>Runs every scenario and prints a categorized summary via the shared runner.</summary>
        [MenuItem("Minecraft Clone/Dev/Validate UI Blur Render")]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the UI blur render scenarios, returning the categorized result (the headless/CI entry point).
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result.</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar.</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>
            {
                new Scenario("B1 The sampled blur reaches the panel unchanged, and the backdrop around it survives",
                    RunB1RoundTrip),
                new Scenario("B2 _MultiplyColor scales the sampled blur", RunB2MultiplyTint),
                new Scenario("B3 _AdditiveColor offsets the sampled blur", RunB3AdditiveTint),
                new Scenario("B4 The UI vertex color tints the panel and fades it over what is behind",
                    RunB4VertexColor),
                new Scenario("B5 A clip rect excludes the pixels outside it", RunB5ClipRect),
            };

            return ValidationSuiteRunner.Execute("UI Blur Render", scenarios, KnownBugChannel.Bug,
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

        /// <summary>True when this session cannot render, after logging the skip.</summary>
        /// <param name="scenario">Scenario name for the log line.</param>
        /// <returns>True when the caller should report a vacuous pass.</returns>
        private static bool SkipWithoutGraphics(string scenario)
        {
            if (UIBlurQuadRenderer.IsSupported) return false;

            Debug.LogWarning($"  [INCONCLUSIVE] {scenario}: no graphics device (running with -nographics?) — " +
                             "rendered pixels cannot be observed here.");
            return true;
        }

        /// <summary>Asserts that a sampled pixel matches an expected linear color within tolerance.</summary>
        /// <param name="label">Human-readable assertion description.</param>
        /// <param name="actual">The sampled color.</param>
        /// <param name="expected">The color the shader's arithmetic requires.</param>
        /// <returns>True when every channel is within <see cref="COLOR_EPSILON"/>.</returns>
        private static bool CheckColor(string label, Color actual, Color expected)
        {
            bool match = Mathf.Abs(actual.r - expected.r) <= COLOR_EPSILON &&
                         Mathf.Abs(actual.g - expected.g) <= COLOR_EPSILON &&
                         Mathf.Abs(actual.b - expected.b) <= COLOR_EPSILON;

            return Check($"{label} — expected ({expected.r:F3}, {expected.g:F3}, {expected.b:F3}), " +
                         $"got ({actual.r:F3}, {actual.g:F3}, {actual.b:F3})", match);
        }

        /// <summary>Confirms the material bound a pass, so a shader-compile failure cannot read as a blank pass.</summary>
        /// <param name="rendered">Return value of the render call.</param>
        /// <returns><paramref name="rendered"/>.</returns>
        private static bool CheckBound(bool rendered) =>
            Check("the blur material bound its pass", rendered);

        /// <summary>
        /// The linear value a material <c>Color</c> property actually reaches the shader with.
        /// </summary>
        /// <remarks>
        /// In a linear color-space project Unity treats material color properties as sRGB and converts
        /// them on upload, so a tint authored 0.5 arrives as 0.214. An expectation built from the authored
        /// number instead reds a correct shader — measured here before the fix, and the same class of trap
        /// <c>SkyRenderValidationSuite</c> documents for 8-bit render targets. White and black are fixed
        /// points of the conversion, which is why only the tinted scenarios can observe it.
        /// </remarks>
        /// <param name="authored">The color as written into the material.</param>
        /// <returns>Its linear equivalent.</returns>
        private static Color AsShaderSees(Color authored) => authored.linear;

        /// <summary>B1 — the harness renders, and neither the blur sample nor the backdrop is re-encoded.</summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB1RoundTrip()
        {
            if (SkipWithoutGraphics("B1")) return true;

            using UIBlurQuadRenderer renderer = new UIBlurQuadRenderer();
            if (!Check("the shader Custom/MaskedUIBlur was found", renderer.ShaderFound)) return false;

            bool ok = CheckBound(renderer.Render(s_blurSource, s_backdrop, Color.white, Color.white, Color.clear));

            ok &= CheckColor("the panel shows the blur it sampled",
                renderer.SampleLinear(INSIDE_X, SAMPLE_Y), s_blurSource);

            // Reads the clear, not the shader: proves the target, blend and readback path are trustworthy
            // before any scenario draws a conclusion from a composited pixel.
            ok &= CheckColor("the backdrop beside the panel is untouched",
                renderer.SampleLinear(BACKDROP_X, SAMPLE_Y), s_backdrop);

            return ok;
        }

        /// <summary>B2 — the multiply tint scales the sampled blur channel-wise.</summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB2MultiplyTint()
        {
            if (SkipWithoutGraphics("B2")) return true;

            using UIBlurQuadRenderer renderer = new UIBlurQuadRenderer();
            if (!Check("the shader Custom/MaskedUIBlur was found", renderer.ShaderFound)) return false;

            Color multiply = new Color(0.5f, 0.25f, 0.125f, 1f);
            bool ok = CheckBound(renderer.Render(s_blurSource, s_backdrop, Color.white, multiply, Color.clear));

            Color tint = AsShaderSees(multiply);
            ok &= CheckColor("the panel shows the blur scaled by _MultiplyColor",
                renderer.SampleLinear(INSIDE_X, SAMPLE_Y),
                new Color(s_blurSource.r * tint.r, s_blurSource.g * tint.g, s_blurSource.b * tint.b));

            return ok;
        }

        /// <summary>B3 — the additive tint offsets the sampled blur.</summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB3AdditiveTint()
        {
            if (SkipWithoutGraphics("B3")) return true;

            using UIBlurQuadRenderer renderer = new UIBlurQuadRenderer();
            if (!Check("the shader Custom/MaskedUIBlur was found", renderer.ShaderFound)) return false;

            Color additive = new Color(0.1f, 0.05f, 0.2f, 0f);
            bool ok = CheckBound(renderer.Render(s_blurSource, s_backdrop, Color.white, Color.white, additive));

            Color offset = AsShaderSees(additive);
            ok &= CheckColor("the panel shows the blur offset by _AdditiveColor",
                renderer.SampleLinear(INSIDE_X, SAMPLE_Y),
                new Color(s_blurSource.r + offset.r, s_blurSource.g + offset.g, s_blurSource.b + offset.b));

            return ok;
        }

        /// <summary>
        /// B4 — the UI vertex color is honored on both rgb and alpha (UI_BUGS #06).
        /// </summary>
        /// <remarks>
        /// The alpha half is the stacking guard: an opaque panel replaces the pixels behind it instead of
        /// compositing, which is what let the benchmark HUD show un-dimmed world over the pause menu.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB4VertexColor()
        {
            if (SkipWithoutGraphics("B4")) return true;

            using UIBlurQuadRenderer renderer = new UIBlurQuadRenderer();
            if (!Check("the shader Custom/MaskedUIBlur was found", renderer.ShaderFound)) return false;

            Color tint = new Color(0.5f, 0.5f, 0.5f, 1f);
            bool ok = CheckBound(renderer.Render(s_blurSource, s_backdrop, tint, Color.white, Color.clear));

            ok &= CheckColor("an opaque vertex color tints the panel",
                renderer.SampleLinear(INSIDE_X, SAMPLE_Y),
                new Color(s_blurSource.r * tint.r, s_blurSource.g * tint.g, s_blurSource.b * tint.b));

            const float alpha = 0.5f;
            ok &= CheckBound(renderer.Render(s_blurSource, s_backdrop, new Color(1f, 1f, 1f, alpha),
                Color.white, Color.clear));

            ok &= CheckColor($"a vertex alpha of {alpha} blends the panel with the backdrop",
                renderer.SampleLinear(INSIDE_X, SAMPLE_Y),
                Color.Lerp(s_backdrop, s_blurSource, alpha));

            return ok;
        }

        /// <summary>
        /// B5 — a clip rect excludes the pixels outside it, so a blurred graphic under a
        /// <c>RectMask2D</c> clips like every other UI graphic (UI_BUGS #06).
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB5ClipRect()
        {
            if (SkipWithoutGraphics("B5")) return true;

            using UIBlurQuadRenderer renderer = new UIBlurQuadRenderer();
            if (!Check("the shader Custom/MaskedUIBlur was found", renderer.ShaderFound)) return false;

            Rect clip = Rect.MinMaxRect(0f, 0f, CLIP_RECT_RIGHT, UIBlurQuadRenderer.RenderSize);
            bool ok = CheckBound(renderer.Render(s_blurSource, s_backdrop, Color.white, Color.white,
                Color.clear, clip));

            ok &= CheckColor("the panel still draws inside the clip rect",
                renderer.SampleLinear(INSIDE_X, SAMPLE_Y), s_blurSource);

            ok &= CheckColor("the panel is clipped away outside the clip rect",
                renderer.SampleLinear(CLIPPED_X, SAMPLE_Y), s_backdrop);

            return ok;
        }
    }
}
