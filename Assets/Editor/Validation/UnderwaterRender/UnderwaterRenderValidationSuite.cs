using System.Collections.Generic;
using Data;
using Editor.Dev;
using Editor.Validation.Framework;
using Helpers;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.UnderwaterRender
{
    /// <summary>
    /// The submersion arc's baselines (<c>FLUID_BUGS</c> #02, design <c>UW-*</c>): that a fluid body renders
    /// from inside it, and that the shared eye query reports the surface the mesher actually drew.
    /// <para>
    /// B1–B3 render <c>UberLiquidShader</c> in edit mode and compare against computed values rather than
    /// reference images, like <c>UIBlurRenderValidationSuite</c> and <c>SkyRenderValidationSuite</c> — GPU
    /// output is not bit-reproducible across drivers, so checked-in goldens would fail for reasons unrelated
    /// to the shader. Under <c>-nographics</c> they report <b>INCONCLUSIVE</b> and pass rather than failing a
    /// headless run.
    /// </para>
    /// <para>
    /// B4–B9 need no device. Since UW-2 the mesher and the eye query derive the surface from one shared
    /// <see cref="FluidSurfaceResolver"/>, so their <i>values</i> cannot drift apart — that is what the shared
    /// path buys, and asserting it would be asserting a helper against itself. What these pin is what sharing
    /// does not: the mapping between resolver corners and emitted vertices, the axis order of
    /// <c>SampleSurfaceAt</c>'s two fractions, and the two-cell search and soft-failure of the surrounding query
    /// . Expected values are read off the vertices the <b>real</b>
    /// <c>VoxelMeshHelper.GenerateFluidMeshData</c> emitted, never re-typed from the smoothing expression.
    /// </para>
    /// </summary>
    public static partial class UnderwaterRenderValidationSuite
    {
        /// <summary>Tolerance for a linear color comparison, allowing half-float quantization.</summary>
        private const float COLOR_EPSILON = 0.004f;

        /// <summary>Tolerance for a surface height in block-local units.</summary>
        private const float HEIGHT_EPSILON = 1e-5f;

        /// <summary>
        /// How far a sample must sit from the backdrop, per channel, to count as "something drew here".
        /// </summary>
        /// <remarks>
        /// Well above <see cref="COLOR_EPSILON"/>: this decides presence, not equality, and the backdrop is
        /// chosen to be unlike water in every channel so a real draw clears it by a wide margin.
        /// </remarks>
        private const float PRESENCE_EPSILON = 0.05f;

        /// <summary>
        /// The color both targets are cleared to, standing in for whatever is already on screen.
        /// </summary>
        /// <remarks>
        /// Deliberately unlike the water branch's output in every channel — a backdrop near the fluid color
        /// would let a culled (undrawn) quad still land inside <see cref="PRESENCE_EPSILON"/>.
        /// </remarks>
        private static readonly Color s_backdrop = new Color(0.95f, 0.15f, 0.6f, 1f);

        /// <summary>Runs every scenario and prints a categorized summary via the shared runner.</summary>
        [MenuItem("Minecraft Clone/Dev/Validate Underwater Render", priority = DevMenuPriority.Validation)]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the underwater scenarios, returning the categorized result (the headless/CI entry point).
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result.</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar.</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>
            {
                new Scenario("B1 The liquid material renders at least one winding at all", RunB1PositiveControl),
                new Scenario("B2 Both windings of a liquid quad are drawn, neither culled", RunB2BackfacesDrawn),
                new Scenario("B3 A negated normal shades identically", RunB3NormalAgnostic),
                new Scenario("B4 The resolver's corners are the vertices the mesher emitted", RunB4CornerOracle),
                new Scenario("B5 The interior sample is the mean of the emitted corners at the cell center",
                    RunB5InteriorSample),
                new Scenario("B6 Fluid directly above forces every drawn corner flat", RunB6SubmergedCellIsFlat),
                new Scenario("B7 A vanishing fluid level still clears the minimum surface height",
                    RunB7MinimumHeight),
                new Scenario("B8 The eye query searches its own cell, then the one below", RunB8TwoCellSearch),
                new Scenario("B9 A disposed world reports no fluid rather than throwing", RunB9DisposedWorld),
                new Scenario("B10 The overlay draws, and a saturated medium reaches the authored tint",
                    RunB10OverlayPositiveControl),
                new Scenario("B11 The overlay decodes depth to the view distance it was given",
                    RunB11DepthDecode),
                new Scenario("B12 The fog follows the true view ray, not the camera's forward axis",
                    RunB12ViewRayScale),
                new Scenario("B13 A zero density leaves the screen untouched", RunB13ZeroDensityPassThrough),
                new Scenario("B14 Far-plane depth takes full density, so sky reads as submerged",
                    RunB14FarPlaneSaturates),
                new Scenario("B15 A zero strength is the not-submerged fail-safe",
                    RunB15ZeroStrengthPassThrough),
                new Scenario("B16 The eye query packs onto the channels the overlay reads, and ramps",
                    RunB16PackedGlobals),
                new Scenario("B17 The overlay is wired in, shader assigned, ahead of the UI blur",
                    RunB17RendererWiring),
                new Scenario("B18 A sinking eye reports one continuous depth, not one per cell",
                    RunB18ContinuousDepthWhileSinking),
                new Scenario("B19 The fog follows each ray's own submerged length, so a partial view splits",
                    RunB19PerPixelSubmergedLength),
                new Scenario("B20 The fogged half is the half the camera looks down at",
                    RunB20FoggedHalfIsTheLowerHalf),
                new Scenario("B21 An eye above the surface fogs nothing, however deep the geometry",
                    RunB21AboveSurfaceFogsNothing),
                new Scenario("B22 A ray leaving the body sideways is charged only for the water it crossed",
                    RunB22HorizontalBodyBound),
                new Scenario("B23 The body extents ease across cell boundaries but snap on entering water",
                    RunB23ExtentsEaseButSnapOnEntry),
                new Scenario("B24 A block standing in the water does not shrink the body around it",
                    RunB24ObstructionDoesNotShrinkTheBody),
            };

            return ValidationSuiteRunner.Execute("Underwater Render", scenarios, KnownBugChannel.Bug,
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
            if (LiquidFaceRenderer.IsSupported) return false;

            Debug.LogWarning($"  [INCONCLUSIVE] {scenario}: no graphics device (running with -nographics?) — " +
                             "rendered pixels cannot be observed here.");
            return true;
        }

        /// <summary>Whether a sampled color differs from the backdrop by more than <see cref="PRESENCE_EPSILON"/>.</summary>
        /// <param name="sample">The sampled color.</param>
        /// <returns>True when something was drawn over the clear.</returns>
        private static bool Drew(Color sample)
        {
            return Mathf.Abs(sample.r - s_backdrop.r) > PRESENCE_EPSILON ||
                   Mathf.Abs(sample.g - s_backdrop.g) > PRESENCE_EPSILON ||
                   Mathf.Abs(sample.b - s_backdrop.b) > PRESENCE_EPSILON;
        }

        /// <summary>Renders both windings, reporting whether the harness itself is usable.</summary>
        /// <param name="renderer">The renderer to drive.</param>
        /// <returns>True when the shader was found and the draw submitted.</returns>
        private static bool TryRender(LiquidFaceRenderer renderer)
        {
            if (!Check("the shader Minecraft/UberLiquidShader was found and is supported", renderer.ShaderUsable))
                return false;

            return Check("the liquid material bound its pass", renderer.Render(s_backdrop));
        }

        /// <summary>
        /// B1 — the positive control, and it must be read before B2 and B3.
        /// </summary>
        /// <remarks>
        /// Both of those conclude from a pixel that differs from the backdrop. A harness that renders nothing
        /// at all — a missing shader global, an unbound pass, a projection that puts the quad off-screen —
        /// makes B2 fail for a reason that has nothing to do with culling, and would make a future "both are
        /// backdrop, therefore equal" reading of B3 pass vacuously.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB1PositiveControl()
        {
            if (SkipWithoutGraphics("B1")) return true;

            using LiquidFaceRenderer renderer = new LiquidFaceRenderer();
            if (!TryRender(renderer)) return false;

            // Which winding the device calls front-facing is a platform convention, so the control asserts
            // over the pair: with Cull Back at least one of them must land, with Cull Off both do.
            return Check("at least one winding drew liquid over the backdrop",
                Drew(renderer.PositiveNormalSample) || Drew(renderer.NegativeNormalSample));
        }

        /// <summary>
        /// B2 — a fluid body must be visible from inside it, which is goal 1 of the design and the root cause
        /// of <c>FLUID_BUGS</c> #02's remaining bullet.
        /// </summary>
        /// <remarks>
        /// A submerged camera sits inside a shell whose faces all point away from it. The geometry is emitted;
        /// back-face culling is what hid it. Red until <c>UberLiquidShader</c>'s <c>LiquidForward</c> pass
        /// declares <c>Cull Off</c>.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB2BackfacesDrawn()
        {
            if (SkipWithoutGraphics("B2")) return true;

            using LiquidFaceRenderer renderer = new LiquidFaceRenderer();
            if (!TryRender(renderer)) return false;

            bool ok = Check("the +Z-normal winding drew liquid", Drew(renderer.PositiveNormalSample));
            ok &= Check("the reversed, -Z-normal winding drew liquid rather than being culled",
                Drew(renderer.NegativeNormalSample));

            return ok;
        }

        /// <summary>
        /// B3 — the property that makes an unconditional <c>Cull Off</c> safe: the fragment reads
        /// <c>worldNormal</c> only through <c>abs()</c>, so a negated normal changes nothing downstream.
        /// </summary>
        /// <remarks>
        /// This is the scenario that guards the decision rather than the symptom. Adding any normal-dependent
        /// term to the liquid fragment — a diffuse wrap, a Fresnel rim, a specular lobe — reds it, which is
        /// the moment to revisit whether unconditional back-face rendering is still free.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB3NormalAgnostic()
        {
            if (SkipWithoutGraphics("B3")) return true;

            using LiquidFaceRenderer renderer = new LiquidFaceRenderer();
            if (!TryRender(renderer)) return false;

            Color positive = renderer.PositiveNormalSample;
            Color negated = renderer.NegativeNormalSample;

            // Guarded on the control: two identical backdrops would otherwise satisfy the comparison below.
            if (!Check("both windings drew, so the comparison is not between two backdrops",
                    Drew(positive) && Drew(negated)))
                return false;

            bool match = Mathf.Abs(positive.r - negated.r) <= COLOR_EPSILON &&
                         Mathf.Abs(positive.g - negated.g) <= COLOR_EPSILON &&
                         Mathf.Abs(positive.b - negated.b) <= COLOR_EPSILON;

            return Check("a negated normal shades identically — " +
                         $"+Z ({positive.r:F3}, {positive.g:F3}, {positive.b:F3}), " +
                         $"-Z ({negated.r:F3}, {negated.g:F3}, {negated.b:F3})", match);
        }

        /// <summary>
        /// B4 — the drift gate. The resolver's corner heights must be the exact Y values the mesher wrote
        /// into its top-face vertices, sampled through the same corner assignment.
        /// </summary>
        /// <remarks>
        /// The heights themselves agree by construction — one shared function computes both. The claim under
        /// test is the <b>mapping</b>: sampling at the four corner fractions pins
        /// <c>BL=(0,0) BR=(1,0) TL=(0,1) TR=(1,1)</c> against the order the mesher writes its vertices in, and
        /// a transposed assignment or a swapped fraction pair leaves every averaged quantity identical while
        /// putting the tint boundary on the wrong slope. That is why the fixture insists its four corners
        /// differ before any of this is read.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB4CornerOracle()
        {
            const string scenario = "B4";

            using FluidSurfaceFixture fixture = FluidSurfaceFixture.Sloped();
            if (!Check($"{scenario}: the mesher emitted a top face", fixture.HasTopFace)) return false;

            // A neighborhood that smooths to four DIFFERENT corners: with all four equal, a transposed or
            // averaged implementation would satisfy every assertion here.
            if (!Check($"{scenario}: the emitted corners are not all equal, so corner order is observable",
                    fixture.CornersAreDistinct))
                return false;

            FluidCornerHeights resolved = fixture.ResolvedSurface;

            bool ok = Check($"{scenario}: (0,0) matches the emitted back-left vertex",
                Mathf.Abs(FluidSurfaceResolver.SampleSurfaceAt(in resolved, 0f, 0f) - fixture.MeshBL) <= HEIGHT_EPSILON);
            ok &= Check($"{scenario}: (1,0) matches the emitted back-right vertex",
                Mathf.Abs(FluidSurfaceResolver.SampleSurfaceAt(in resolved, 1f, 0f) - fixture.MeshBR) <= HEIGHT_EPSILON);
            ok &= Check($"{scenario}: (0,1) matches the emitted front-left vertex",
                Mathf.Abs(FluidSurfaceResolver.SampleSurfaceAt(in resolved, 0f, 1f) - fixture.MeshTL) <= HEIGHT_EPSILON);
            ok &= Check($"{scenario}: (1,1) matches the emitted front-right vertex",
                Mathf.Abs(FluidSurfaceResolver.SampleSurfaceAt(in resolved, 1f, 1f) - fixture.MeshTR) <= HEIGHT_EPSILON);

            return ok;
        }

        /// <summary>
        /// B5 — the interior of the cell, where the tint boundary actually sits.
        /// </summary>
        /// <remarks>
        /// Characterized as the mean of the four emitted vertices rather than by re-evaluating the bilinear
        /// expression: at the cell center the two are equal, but the mean is an independent statement about
        /// the surface, so a resolver that dropped a corner or weighted the axes wrongly still reds.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB5InteriorSample()
        {
            const string scenario = "B5";

            using FluidSurfaceFixture fixture = FluidSurfaceFixture.Sloped();
            if (!Check($"{scenario}: the mesher emitted a top face", fixture.HasTopFace)) return false;

            FluidCornerHeights resolved = fixture.ResolvedSurface;
            float center = FluidSurfaceResolver.SampleSurfaceAt(in resolved, 0.5f, 0.5f);
            float mean = (fixture.MeshBL + fixture.MeshBR + fixture.MeshTL + fixture.MeshTR) * 0.25f;

            bool ok = Check($"{scenario}: the cell center is the mean of the four emitted vertices",
                Mathf.Abs(center - mean) <= HEIGHT_EPSILON);

            // A surface that stepped rather than interpolated would still hit the center exactly.
            float quarter = FluidSurfaceResolver.SampleSurfaceAt(in resolved, 0.25f, 0f);
            float expectedQuarter = Mathf.Lerp(fixture.MeshBL, fixture.MeshBR, 0.25f);
            ok &= Check($"{scenario}: a quarter of the way along the back edge interpolates between its ends",
                Mathf.Abs(quarter - expectedQuarter) <= HEIGHT_EPSILON);

            return ok;
        }

        /// <summary>
        /// B6 — a cell with the same fluid above draws a flat, full-height top, so the body has no internal
        /// lip. Asserted against mesh output, so the override cannot drift out of the shared path.
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB6SubmergedCellIsFlat()
        {
            const string scenario = "B6";

            using FluidSurfaceFixture fixture = FluidSurfaceFixture.SlopedWithFluidAbove();
            if (!Check($"{scenario}: the mesher emitted no top face for a submerged cell", !fixture.HasTopFace))
                return false;

            // The face is interior and therefore not drawn, but the eye query still has to answer for this
            // cell — a camera inside the body is exactly where it is asked.
            FluidCornerHeights resolved = fixture.ResolvedSurface;
            bool ok = Check($"{scenario}: the resolved surface is full height at every corner",
                Mathf.Abs(resolved.BL - 1f) <= HEIGHT_EPSILON &&
                Mathf.Abs(resolved.BR - 1f) <= HEIGHT_EPSILON &&
                Mathf.Abs(resolved.TL - 1f) <= HEIGHT_EPSILON &&
                Mathf.Abs(resolved.TR - 1f) <= HEIGHT_EPSILON);

            // The smoothed stage must NOT be flattened — the side faces still rise to it.
            ok &= Check($"{scenario}: the un-forced smoothed corners are still sloped",
                fixture.SmoothedAreDistinct);

            return ok;
        }

        /// <summary>
        /// B7 — the z-fighting floor. A cell whose fluid has all but drained still reports a surface above
        /// its own floor, and the resolver's constant is the one the mesher uses.
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB7MinimumHeight()
        {
            const string scenario = "B7";

            using FluidSurfaceFixture fixture = FluidSurfaceFixture.NearlyEmpty();
            if (!Check($"{scenario}: the mesher emitted a top face", fixture.HasTopFace)) return false;

            FluidCornerHeights resolved = fixture.ResolvedSurface;
            float lowest = Mathf.Min(Mathf.Min(resolved.BL, resolved.BR), Mathf.Min(resolved.TL, resolved.TR));

            bool ok = Check($"{scenario}: no corner sits below the minimum surface height",
                lowest >= FluidSurfaceResolver.MinSurfaceHeight - HEIGHT_EPSILON);
            ok &= Check($"{scenario}: the emitted vertices agree with that floor",
                Mathf.Abs(Mathf.Min(Mathf.Min(fixture.MeshBL, fixture.MeshBR),
                    Mathf.Min(fixture.MeshTL, fixture.MeshTR)) - lowest) <= HEIGHT_EPSILON);

            return ok;
        }

        /// <summary>
        /// B8 — the eye query's two-cell search, over a real <c>World</c>.
        /// </summary>
        /// <remarks>
        /// The cell below cannot submerge the eye, but it has to supply a surface anyway: the waterline needs
        /// a plane to track while the eye sits just above water, and reports a negative depth there. Both
        /// halves are asserted, because a query that simply returned <c>default</c> over air would satisfy
        /// "not submerged" while leaving nothing to split the screen on.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB8TwoCellSearch()
        {
            const string scenario = "B8";

            using EyeSubmersionFixture fixture = new EyeSubmersionFixture();

            // Deep inside the column: the eye's own cell owns the answer.
            EyeSubmersion deep = fixture.Sample(fixture.EyeXz.x, EyeSubmersionFixture.FluidTopY + 0.5f, fixture.EyeXz.y);
            bool ok = Check($"{scenario}: an eye inside the fluid reads submerged", deep.IsSubmerged);
            ok &= Check($"{scenario}: it reports the fluid it is in", deep.Type == FluidType.WaterLike);
            ok &= Check($"{scenario}: its depth below the surface is positive", deep.EyeDepth > 0f);

            // Just above the column's top cell: the cell below supplies the surface, depth goes negative.
            EyeSubmersion above = fixture.Sample(fixture.EyeXz.x, EyeSubmersionFixture.FluidTopY + 1.5f, fixture.EyeXz.y);
            ok &= Check($"{scenario}: an eye above the surface does not read submerged", !above.IsSubmerged);
            ok &= Check($"{scenario}: the cell below still supplied a fluid type",
                above.Type == FluidType.WaterLike);
            ok &= Check($"{scenario}: the surface it reports sits below the eye", above.EyeDepth < 0f);

            // Well clear of the water: nothing within two cells.
            EyeSubmersion dry = fixture.Sample(fixture.EyeXz.x, EyeSubmersionFixture.FluidTopY + 6f, fixture.EyeXz.y);
            ok &= Check($"{scenario}: an eye clear of the fluid reports none at all", dry.Type == FluidType.None);

            return ok;
        }

        /// <summary>
        /// B9 — the query runs from the per-frame global publish, which can outlive a world unload by a
        /// frame. It must fail soft, exactly as <c>GatherFluidContact</c> does.
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB9DisposedWorld()
        {
            const string scenario = "B9";

            EyeSubmersionFixture fixture = new EyeSubmersionFixture();
            Vector2 eyeXz = fixture.EyeXz;

            // Proves the query answers before disposal, so the assertion after it is about the guard and not
            // about a fixture that never held fluid in the first place.
            EyeSubmersion before = fixture.Sample(eyeXz.x, EyeSubmersionFixture.FluidTopY + 0.5f, eyeXz.y);
            if (!Check($"{scenario}: the fixture reports fluid before disposal", before.IsSubmerged)) return false;

            World world = World.Instance;
            fixture.DisposeJobData();

            bool threw = false;
            EyeSubmersion after = default;
            try
            {
                world.GatherEyeSubmersion(
                    new Vector3(eyeXz.x, EyeSubmersionFixture.FluidTopY + 0.5f, eyeXz.y), out after);
            }
            catch (System.Exception e)
            {
                threw = true;
                Debug.LogWarning($"  [INFO] {scenario}: the query threw {e.GetType().Name}.");
            }
            finally
            {
                fixture.Dispose();
            }

            bool ok = Check($"{scenario}: a disposed world did not throw", !threw);
            ok &= Check($"{scenario}: it reported no fluid", after.Type == FluidType.None);

            return ok;
        }
    }
}
