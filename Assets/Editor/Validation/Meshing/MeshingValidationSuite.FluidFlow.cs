using System.Collections.Generic;
using Data;
using Editor.Validation.Meshing.Framework;
using Jobs.BurstData;
using Unity.Mathematics;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Meshing
{
    /// <summary>
    /// Fluid flow-vector baselines — the first coverage of
    /// <see cref="Jobs.BurstData.BurstFluidFlowUtility.CalculateSymmetricCornerFlow"/>'s <i>output</i>.
    /// <para>
    /// <b>The gap these close.</b> <c>B38</c>'s docstring names the flow function, but its assertion sums
    /// emitted vertex <i>Y</i> — which comes from <c>GetSmoothedCornerHeight</c>, not from the flow
    /// derivative. Flow reaches the GPU through the UV stream (<c>uv.xy</c>) and nothing read it, so the
    /// four corner arguments could be transposed, or the whole vector negated, with every meshing baseline
    /// still green. That mattered the moment the flow core was extracted for the physics side to share.
    /// </para>
    /// <para>
    /// <b>Property assertions, not transcribed formulas.</b> Nothing here recomputes the derivative or the
    /// smoothstep speed curve — a test that re-expresses the function it guards agrees with it by
    /// construction, including when both are wrong. These pin behavior the formula must exhibit whatever
    /// its constants: the vector lies on the gradient axis, points <b>uphill</b>, reverses under a mirrored
    /// fixture, vanishes on a flat field, and grows with a steeper slope.
    /// </para>
    /// <para>
    /// <b>Uphill is not a typo.</b> <c>uv.xy</c> is a UV <i>scroll offset</i>, and <c>LiquidCore.hlsl</c>
    /// adds it to the noise sample position — advancing the sample toward +X makes the pattern appear to
    /// travel toward −X. The stored vector is therefore the negation of the visible current, which is why
    /// <c>Physics.FluidContactResolver</c> negates it to obtain a push direction.
    /// </para>
    /// Self-registers via the <see cref="AddFluidFlowBaselineScenarios"/> hook called from
    /// <c>AddBaselineScenarios</c>.
    /// </summary>
    public static partial class MeshingValidationSuite
    {
        /// <summary>Cell Y the flow patches are built on — matched to the other fluid fixtures.</summary>
        private const int FLOW_Y = 8;

        /// <summary>
        /// Cells the patch spans <b>along</b> its gradient axis. Kept short enough that the steepest step
        /// below still lands under <see cref="BurstVoxelDataBitMapping.FLUID_FALLING_FLAG"/> — level 8 is
        /// not a deeper horizontal level, it is the falling flag, which re-reads the nibble as a full-height
        /// column and inverts the slope at the far edge. <see cref="AssertGradientStaysHorizontal"/> pins it.
        /// </summary>
        private const int FLOW_SPAN_ALONG = 4;

        /// <summary>
        /// Cells the patch spans <b>across</b> its gradient axis, where every cell carries the same level.
        /// This is the span whose mirror symmetry forces the cross-axis flow component to cancel.
        /// </summary>
        private const int FLOW_SPAN_ACROSS = 5;

        /// <summary>Chunk-local X of the patch's low edge, leaving the patch clear of the chunk borders.</summary>
        private const int FLOW_PATCH_X0 = 6;

        /// <summary>Chunk-local Z of the patch's low edge, leaving the patch clear of the chunk borders.</summary>
        private const int FLOW_PATCH_Z0 = 6;

        /// <summary>Fluid-level step per cell for the gentle gradient (one level per cell).</summary>
        private const byte FLOW_GENTLE_STEP = 1;

        /// <summary>Fluid-level step per cell for the steep gradient — a larger drop over the same distance.</summary>
        private const byte FLOW_STEEP_STEP = 2;

        /// <summary>
        /// Sum tolerance for a component that symmetry forces to zero. Well above the <c>Float16</c> UV
        /// rounding floor (<see cref="MeshAssert.UvHalfEpsilon"/>) accumulated over the patch's vertices,
        /// and far below any real single-corner contribution.
        /// </summary>
        private const float FLOW_ZERO_SUM_TOLERANCE = 0.05f;

        /// <summary>
        /// Minimum summed magnitude a genuinely sloped patch must produce. A flow function wired to
        /// constants, or never called at all, lands under this.
        /// </summary>
        private const float FLOW_PRESENT_MINIMUM = 0.5f;

        /// <summary>Which axis a fixture's fluid-level gradient runs along.</summary>
        private enum FlowAxis
        {
            X,
            Z,
        }

        /// <summary>Registers the fluid flow-vector baselines.</summary>
        /// <param name="scenarios">The suite's scenario list.</param>
        static partial void AddFluidFlowBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("B64: fluid flow lies on the gradient axis, points uphill, and mirrors",
                B64_FlowIsAxisAlignedAndAntisymmetric));
            scenarios.Add(new Scenario("B65: fluid flow vanishes on a flat field and grows with slope",
                B65_FlowRespondsToSlope));
        }

        /// <summary>
        /// B64 — the flow vector's <b>wiring</b>: which axis it lands on, which way it points, and that a
        /// mirrored world produces the mirrored answer.
        /// <para>
        /// Each fixture is a fluid patch whose levels rise along one axis and are constant across the other,
        /// so the cross-axis component is forced to zero by the fixture's own mirror symmetry rather than by
        /// anything the flow function chooses. Transposing the four corner arguments moves the response to
        /// the wrong axis; negating the derivative flips the sign; and the X↔Z legs together catch a swap
        /// that a single-axis test would read as correct.
        /// </para>
        /// </summary>
        /// <returns>True when every leg holds.</returns>
        private static bool B64_FlowIsAxisAlignedAndAntisymmetric()
        {
            Vector2 risingX = SumTopFaceFlow(FlowAxis.X, FLOW_GENTLE_STEP, ascending: true);
            Vector2 fallingX = SumTopFaceFlow(FlowAxis.X, FLOW_GENTLE_STEP, ascending: false);
            Vector2 risingZ = SumTopFaceFlow(FlowAxis.Z, FLOW_GENTLE_STEP, ascending: true);

            // Levels rise with +X, and a higher level is a SHORTER column, so the surface falls toward +X.
            // The stored vector is the UV scroll offset, which points at the high side — i.e. toward -X.
            bool ok = MeshAssert.IsTrue("B64 +X slope puts flow on the X axis",
                risingX.x < -FLOW_PRESENT_MINIMUM,
                $"summed uv.x = {risingX.x:F4} (expected clearly negative — the offset points uphill, toward -X)");

            ok &= MeshAssert.IsTrue("B64 +X slope leaves Z untouched",
                Mathf.Abs(risingX.y) < FLOW_ZERO_SUM_TOLERANCE,
                $"summed uv.y = {risingX.y:F4} (the patch is mirror-symmetric in Z, so this must cancel to 0)");

            ok &= MeshAssert.IsTrue("B64 mirroring the slope mirrors the flow",
                Mathf.Abs(fallingX.x + risingX.x) < FLOW_ZERO_SUM_TOLERANCE,
                $"summed uv.x = {risingX.x:F4} rising vs {fallingX.x:F4} mirrored (expected exact negation — " +
                "an asymmetry here means the corner quad is not evaluated symmetrically)");

            ok &= MeshAssert.IsTrue("B64 +Z slope puts flow on the Z axis",
                risingZ.y < -FLOW_PRESENT_MINIMUM,
                $"summed uv.y = {risingZ.y:F4} (expected clearly negative)");

            ok &= MeshAssert.IsTrue("B64 +Z slope leaves X untouched",
                Mathf.Abs(risingZ.x) < FLOW_ZERO_SUM_TOLERANCE,
                $"summed uv.x = {risingZ.x:F4} (the patch is mirror-symmetric in X, so this must cancel to 0) — " +
                "a non-zero value here with B64's X legs passing means the two axes are transposed");

            return ok;
        }

        /// <summary>
        /// B65 — the flow vector's <b>response</b>: a level field with no slope must produce no flow, and a
        /// steeper slope must produce more of it.
        /// <para>
        /// The flat leg is the one that cannot be satisfied by a function returning a constant, and the
        /// monotonicity leg pins that the derivative actually reads the neighbor levels rather than merely
        /// their arrangement. Neither restates the speed curve, so retuning that curve leaves both green
        /// while a broken derivative reddens them.
        /// </para>
        /// </summary>
        /// <returns>True when every leg holds.</returns>
        private static bool B65_FlowRespondsToSlope()
        {
            bool ok = AssertGradientStaysHorizontal(FLOW_GENTLE_STEP);
            ok &= AssertGradientStaysHorizontal(FLOW_STEEP_STEP);
            if (!ok) return false;

            Vector2 flat = SumTopFaceFlow(FlowAxis.X, step: 0, ascending: true);
            Vector2 gentle = SumTopFaceFlow(FlowAxis.X, FLOW_GENTLE_STEP, ascending: true);
            Vector2 steep = SumTopFaceFlow(FlowAxis.X, FLOW_STEEP_STEP, ascending: true);

            ok = MeshAssert.IsTrue("B65 a level fluid field carries no flow",
                flat.magnitude < FLOW_ZERO_SUM_TOLERANCE,
                $"summed flow = {flat} on a uniform-level patch (expected ~0 — a non-zero reading means the " +
                "vector does not come from the level field at all)");

            ok &= MeshAssert.IsTrue("B65 a steeper slope flows harder",
                Mathf.Abs(steep.x) > Mathf.Abs(gentle.x) + FLOW_ZERO_SUM_TOLERANCE,
                $"summed |uv.x| = {Mathf.Abs(steep.x):F4} at {FLOW_STEEP_STEP} levels/cell vs " +
                $"{Mathf.Abs(gentle.x):F4} at {FLOW_GENTLE_STEP} (expected strictly greater)");

            return ok;
        }

        /// <summary>
        /// Fixture integrity: the steepest cell in a gradient must still be a <b>horizontal</b> fluid level.
        /// </summary>
        /// <remarks>
        /// Level 8 is <see cref="BurstVoxelDataBitMapping.FLUID_FALLING_FLAG"/>, not "one level lower than
        /// 7": it marks a vertically falling column, whose template height jumps back to a full 1.0 and
        /// whose lower three bits mean something else entirely. A gradient that ran into it would reverse
        /// its own slope at the far edge and quietly measure the opposite of what the scenario claims.
        /// Asserted rather than merely commented so a later retune of the spans or steps reddens here
        /// instead of silently changing what B65 tests.
        /// </remarks>
        /// <param name="step">Fluid levels added per cell along the gradient axis.</param>
        /// <returns>True when the whole gradient stays under the falling flag.</returns>
        private static bool AssertGradientStaysHorizontal(byte step)
        {
            int maxLevel = (FLOW_SPAN_ALONG - 1) * step;
            return MeshAssert.IsTrue($"B65 fixture: a {step}-level/cell gradient stays horizontal",
                maxLevel < BurstVoxelDataBitMapping.FLUID_FALLING_FLAG,
                $"the far edge reaches fluid level {maxLevel}; the falling flag sits at " +
                $"{BurstVoxelDataBitMapping.FLUID_FALLING_FLAG} and the gradient must stay below it " +
                "(shorten FLOW_SPAN_ALONG or the step)");
        }

        /// <summary>
        /// Builds a fluid patch whose levels step along one axis and stay constant across the other, and
        /// returns the sum of <c>uv.xy</c> over every emitted <b>top-face</b> vertex.
        /// <para>
        /// Summing the whole patch rather than isolating one cell is deliberate: the patch is mirror-
        /// symmetric across the gradient axis, so the cross-axis components of the edge cells cancel in the
        /// sum exactly as the interior's do, and no vertex has to be attributed back to the cell that
        /// emitted it (top-face vertices sit on shared cell corners, so that attribution is ambiguous).
        /// </para>
        /// </summary>
        /// <param name="axis">Which axis the level gradient runs along.</param>
        /// <param name="step">Fluid levels added per cell along that axis; 0 builds a flat field.</param>
        /// <param name="ascending">False mirrors the patch, reversing the level order along the axis.</param>
        /// <returns>The summed flow vector over the patch's top faces.</returns>
        private static Vector2 SumTopFaceFlow(FlowAxis axis, byte step, bool ascending)
        {
            using MeshingTestWorld world = new MeshingTestWorld();

            int spanX = axis == FlowAxis.X ? FLOW_SPAN_ALONG : FLOW_SPAN_ACROSS;
            int spanZ = axis == FlowAxis.X ? FLOW_SPAN_ACROSS : FLOW_SPAN_ALONG;

            for (int ix = 0; ix < spanX; ix++)
            for (int iz = 0; iz < spanZ; iz++)
            {
                int alongAxis = axis == FlowAxis.X ? ix : iz;
                if (!ascending) alongAxis = FLOW_SPAN_ALONG - 1 - alongAxis;

                byte level = (byte)(alongAxis * step);
                world.SetBlock(FLOW_PATCH_X0 + ix, FLOW_Y, FLOW_PATCH_Z0 + iz,
                    TestMeshBlockPalette.WaterSource, level);
            }

            MeshDataJobOutput o = world.Run();

            Vector2 sum = Vector2.zero;
            for (int i = 0; i < o.Normals.Length; i++)
            {
                // Top faces only: the side faces carry a projected flow on a different basis, which would
                // mix two encodings into one sum.
                if (o.Normals[i].y < 0.99f) continue;

                float4 uv = o.Uvs[i];
                sum.x += uv.x;
                sum.y += uv.y;
            }

            return sum;
        }
    }
}
