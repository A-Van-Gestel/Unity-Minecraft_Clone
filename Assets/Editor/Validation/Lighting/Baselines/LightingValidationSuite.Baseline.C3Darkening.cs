using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;
using UnityEngine;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Baseline regression scenarios for finding <b>C3</b> — cross-chunk <b>sunlight darkening</b>, the
    /// quadrant of the dynamic cross-chunk matrix that had no deterministic baseline (see
    /// Documentation/Architecture/Testing Framework/LIGHTING_VALIDATION_HARNESS_FIDELITY.md). The matrix's
    /// other quadrants were already covered: <b>B7</b> (blocklight removal, neighbor in flight), <b>B13</b>
    /// (sunlight <i>uplift</i>, neighbor in flight), <b>B12</b> (blocklight cross-border re-spread after a
    /// removal). The steady-state half of darkening is covered by the Bug-12 family (<b>B51</b> asymmetric
    /// two-shaft, <b>B53</b> mutually-lit seam loop); these two add the still-missing pieces explicitly:
    /// <list type="bullet">
    /// <item><b>B54</b> — the <b>race</b> quadrant: a sunlight removal mod deferred into an <b>in-flight</b>
    /// neighbor (the sunlight-removal twin of B7/B13). Guards the defer/drain for the
    /// <c>LightChannel.Sun</c>, <c>LightLevel == 0</c> route, which no scenario exercised.</item>
    /// <item><b>B55</b> — the canonical <b>steady-state</b> single-shaft case: sealing one offset sky shaft
    /// in chunk (1,1) re-darkens the spill that crossed into chunk (2,1), to the borderless oracle. A simpler,
    /// explicit representative of the darkening-across-a-border path than the Bug-12 loop geometries.</item>
    /// </list>
    /// Self-registered via the <see cref="AddCrossChunkDarkeningBaselineScenarios"/> hook called from
    /// <c>AddBaselineScenarios</c> (the <c>Baselines/</c> group-partial pattern). Both reuse the existing
    /// flight/convergence primitives — no new harness capability was needed. This is also the same
    /// neighborhood Bug 11 lived in (<c>CrossChunkLightModApplier.ComputeSunlight</c> removal path), so a
    /// regression there flips these red as well.
    /// </summary>
    public static partial class LightingValidationSuite
    {
        // --- Shared geometry for the C3 cross-chunk darkening family (B54 race + B55 steady-state). A solid
        // rock slab with a 1-wide E-W air corridor at y63,z24 running across the (1,1)/(2,1) chunk seam
        // (x31|x32), lit by a SINGLE vertical sky shaft offset from the seam at x28 (in chunk (1,1)). Sky
        // enters only at the shaft and spreads horizontally with -1/voxel decay, crossing the seam into
        // chunk (2,1) (x32:11, x33:10, ...). Capping the shaft (one PlaceBlock just above the corridor)
        // removes the only source, so the borderless oracle for the sealed corridor is fully dark — the
        // darkening must propagate across the seam into (2,1). Centralized so the race and steady-state
        // scenarios can never drift apart on geometry (mirrors the Bug-12 BuildSeamLoopWorld pattern). ---
        private const int C3_DARK_SLAB_MIN_Y = 58; // solid rock slab, low bound (corridor floor below)
        private const int C3_DARK_SLAB_MAX_Y = 68; // solid rock slab, high bound (corridor roof above)
        private const int C3_DARK_CORRIDOR_Y = 63; // the carved 1-wide corridor (inside the slab)
        private const int C3_DARK_CORRIDOR_Z = 24; // corridor runs along x at this z (chunk row cz=1)
        private const int C3_DARK_CORRIDOR_MIN_X = 26; // west end (chunk (1,1)); capped by slab at x25
        private const int C3_DARK_CORRIDOR_MAX_X = 37; // east end (chunk (2,1)); capped by slab at x38
        private const int C3_DARK_SHAFT_X = 28; // the single sky shaft, in chunk (1,1), 3 west of the x31|32 seam
        private const int C3_DARK_OBSERVE_X = 33; // observation voxel in chunk (2,1): spill-lit pre-seal, dark post-seal

        private static readonly Vector2Int s_c3DarkChunkA = new Vector2Int(1, 1); // owns the shaft + west corridor
        private static readonly Vector2Int s_c3DarkChunkB = new Vector2Int(2, 1); // receives the cross-seam spill

        /// <summary>Registers the C3 cross-chunk darkening baseline scenarios (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddCrossChunkDarkeningBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("B54: A cross-chunk sunlight REMOVAL deferred into an in-flight neighbor darkens the spill to the oracle — the sunlight-removal twin of B7/B13 (finding C3, race quadrant)", Baseline_CrossChunkSunlightDarkeningInFlightRace));
            scenarios.Add(new Scenario("B55: Sealing an offset sky shaft re-darkens the spill that crossed into the neighbor chunk, to the borderless oracle (finding C3, steady-state)", Baseline_CrossChunkSunlightDarkeningSteadyState));
        }

        /// <summary>
        /// Builds the shared C3 darkening world (used by B54 and B55): a solid rock slab with a 1-wide E-W
        /// corridor at y63,z24 spanning the (1,1)/(2,1) seam, lit by a single vertical sky shaft at x28 (in
        /// chunk (1,1)). The shaft is the corridor's only light source; the corridor is otherwise fully
        /// enclosed in stone. Heightmaps are recalculated; the world is returned un-lit — the caller runs the
        /// initial lighting pass, applies the seal, and disposes the world.
        /// </summary>
        /// <returns>A freshly-built, un-lit gridSize-3 test world.</returns>
        private static LightingTestWorld BuildCrossChunkDarkeningWorld()
        {
            LightingTestWorld world = new LightingTestWorld(3);
            int width = world.GridSize * VoxelData.ChunkWidth;

            // Solid slab everywhere across the grid; the corridor/shaft are carved out of it below.
            world.FillBox(new Vector3Int(0, C3_DARK_SLAB_MIN_Y, 0), new Vector3Int(width - 1, C3_DARK_SLAB_MAX_Y, width - 1),
                TestBlockPalette.Stone);

            // 1-wide E-W corridor at y63,z24, crossing the seam (x26..x37) — a sealed tube inside the slab.
            for (int x = C3_DARK_CORRIDOR_MIN_X; x <= C3_DARK_CORRIDOR_MAX_X; x++)
                world.SetBlock(new Vector3Int(x, C3_DARK_CORRIDOR_Y, C3_DARK_CORRIDOR_Z), TestBlockPalette.Air);

            // Single vertical sky shaft at x28: open the column from the corridor up through the roof to the
            // sky, so this one corridor voxel receives vertical sky (15) and feeds the horizontal spill.
            for (int y = C3_DARK_CORRIDOR_Y; y < VoxelData.ChunkHeight; y++)
                world.SetBlock(new Vector3Int(C3_DARK_SHAFT_X, y, C3_DARK_CORRIDOR_Z), TestBlockPalette.Air);

            world.RecalculateHeightmaps();
            return world;
        }

        /// <summary>
        /// Seals the single sky shaft by placing one stone block just above the corridor at the shaft column,
        /// via the player-edit path (<see cref="LightingTestWorld.PlaceBlock"/>) so the opacity change queues
        /// a sunlight column recalc in chunk (1,1) exactly as production. After this the corridor has no sky
        /// source, so the borderless oracle is fully dark throughout the tube.
        /// </summary>
        /// <param name="world">The darkening world to seal.</param>
        private static void SealCrossChunkDarkeningShaft(LightingTestWorld world)
        {
            world.PlaceBlock(new Vector3Int(C3_DARK_SHAFT_X, C3_DARK_CORRIDOR_Y + 1, C3_DARK_CORRIDOR_Z), TestBlockPalette.Stone);
        }

        /// <summary>
        /// B54 (finding C3, race quadrant): the sunlight-removal twin of B7 (blocklight removal in flight) and
        /// B13 (sunlight uplift in flight). After the spill is established, chunk (2,1) has a lighting job IN
        /// FLIGHT (inputs already snapshotted at the bright pre-seal state) when the shaft is sealed in chunk
        /// (1,1). (1,1)'s job runs the darkness wave to the seam and emits a cross-chunk sunlight <b>removal</b>
        /// mod (<c>LightChannel.Sun</c>, <c>LightLevel == 0</c>) toward (2,1) — which, because (2,1) is in
        /// flight, must be <b>deferred</b> and drained right after (2,1)'s merge, not applied to live data and
        /// then silently reverted by the stale full-LightMap overwrite. Without the defer/drain the removal
        /// would be lost and (2,1) would stay permanently brighter than the oracle (the Bug-08-class failure,
        /// here on the previously-untested sunlight-removal route).
        /// <para>
        /// Asserts the removal mod was actually deferred (<c>ModsDeferred &gt; 0</c> — the race really exercised
        /// the defer path, not an incidental direct apply), that a voxel lit by the spill in (2,1)
        /// (<see cref="C3_DARK_OBSERVE_X"/>) re-darkens to 0, and that the whole field matches the borderless
        /// oracle after wave-parallel reconciliation.
        /// </para>
        /// </summary>
        private static bool Baseline_CrossChunkSunlightDarkeningInFlightRace()
        {
            using LightingTestWorld world = BuildCrossChunkDarkeningWorld();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B54: initial lighting converges");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B54: initial spill field matches the borderless oracle");

            // Precondition: the shaft's sky spill has crossed the seam and lit the observation voxel in (2,1).
            Vector3Int observe = new Vector3Int(C3_DARK_OBSERVE_X, C3_DARK_CORRIDOR_Y, C3_DARK_CORRIDOR_Z);
            byte litBefore = world.GetSkyLight(observe);
            passed &= LightingAssert.IsTrue(litBefore > 0,
                "B54: the sky spill crosses the seam and lights the observation voxel in chunk (2,1) before the seal",
                $"Expected sky > 0 at x{C3_DARK_OBSERVE_X} in (2,1), got {litBefore}");

            // Put chunk (2,1)'s job IN FLIGHT (snapshots the bright pre-seal state), THEN seal the shaft in
            // (1,1) and run (1,1)'s job — its cross-chunk sunlight removal mod targets (2,1) mid-flight.
            LightingTestWorld.LightingJobFlight inFlight = world.BeginLightingJob(s_c3DarkChunkB);

            SealCrossChunkDarkeningShaft(world);
            LightingTestWorld.LightingRunResult aResult = world.RunLightingJob(s_c3DarkChunkA);
            passed &= LightingAssert.IsTrue(aResult.ModsDeferred > 0,
                "B54: (1,1)'s cross-seam sunlight removal mod is deferred while (2,1) is in flight",
                $"Expected ModsDeferred > 0 (the Sun/removal defer route), got {aResult.ModsDeferred}");

            // The merge that would have overwritten a live-applied removal; the deferred removal drains right
            // after it, then the darkness wave reconciles wave-parallel (production's concurrent-job model).
            world.CompleteLightingJob(inFlight);

            passed &= LightingAssert.Converged(world.RunWaveToConvergence(), "B54: post-race reconciliation converges");
            passed &= LightingAssert.IsTrue(world.GetSkyLight(observe) == 0,
                "B54: the previously-lit (2,1) voxel re-darkens to 0 — the deferred removal crossed the seam",
                $"Expected sky 0 at x{C3_DARK_OBSERVE_X} after the race (was {litBefore}), got {world.GetSkyLight(observe)}");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B54: corridor darkens to the borderless oracle after the in-flight removal race");

            return passed;
        }

        /// <summary>
        /// B55 (finding C3, steady-state): the canonical single-shaft cross-border darkening. The shaft's sky
        /// spill crosses the (1,1)/(2,1) seam into chunk (2,1); sealing the shaft (one edit in (1,1)) drives
        /// the darkness wave across the seam under sequential convergence (<see cref="LightingTestWorld.RunToConvergence"/>),
        /// and the corridor — including the (2,1) side — must re-darken to the borderless oracle. A simpler,
        /// explicit representative of the darkening-across-a-border path than the Bug-12 loop geometries
        /// (B51/B53), which target the mutually-lit / asymmetric two-shaft seam-loop cases specifically.
        /// </summary>
        private static bool Baseline_CrossChunkSunlightDarkeningSteadyState()
        {
            using LightingTestWorld world = BuildCrossChunkDarkeningWorld();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B55: initial lighting converges");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B55: initial spill field matches the borderless oracle");

            Vector3Int observe = new Vector3Int(C3_DARK_OBSERVE_X, C3_DARK_CORRIDOR_Y, C3_DARK_CORRIDOR_Z);
            byte litBefore = world.GetSkyLight(observe);
            passed &= LightingAssert.IsTrue(litBefore > 0,
                "B55: the sky spill crosses the seam and lights the observation voxel in chunk (2,1) before the seal",
                $"Expected sky > 0 at x{C3_DARK_OBSERVE_X} in (2,1), got {litBefore}");

            SealCrossChunkDarkeningShaft(world);

            passed &= LightingAssert.Converged(world.RunToConvergence(), "B55: post-seal reconciliation converges");
            passed &= LightingAssert.IsTrue(world.GetSkyLight(observe) == 0,
                "B55: the previously-lit (2,1) voxel re-darkens to 0 — the darkness crossed the seam",
                $"Expected sky 0 at x{C3_DARK_OBSERVE_X} after the seal (was {litBefore}), got {world.GetSkyLight(observe)}");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B55: corridor darkens to the borderless oracle after the shaft is sealed");

            return passed;
        }
    }
}
