using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;
using UnityEngine;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Known-bug reproduction scenarios — the test-first encodings of the open bugs in
    /// <c>Documentation/Bugs/LIGHTING_BUGS.md</c>. Each scenario asserts the CORRECT behavior, so it
    /// is EXPECTED TO FAIL until its bug is fixed; the suite runner reports these as warnings, not
    /// regressions. When one starts passing, fix-confirm in-game and archive the bug entry.
    /// </summary>
    public static partial class LightingValidationSuite
    {
        // NOTE on Bug 05: a minimal repro was attempted (5×5 grid, full slab with a single diagonal
        // sky well, wave-parallel initial lighting via RunInitialLightingParallel) but the engine
        // converges to the oracle field — it now lives as baseline B8. Bug 05 likely requires
        // denser multi-pocket canopy patterns or mod-loss timing the minimal case doesn't hit;
        // a faithful repro remains TODO before that bug's fix can be test-driven.
        static partial void AddKnownBugScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("K06: Generated emissive block illuminates surroundings on initial lighting", KnownBug06_GeneratedEmissiveSeedsBfs, "Bug 06"));
            scenarios.Add(new Scenario("K07a: Two emissive sources blend across the chunk border", KnownBug07A_TwoSourceBorderBlend, "Bug 07"));
            scenarios.Add(new Scenario("K07b: Adjacent opposing lamps at the border converge without flicker", KnownBug07B_AdjacentLampsNoFlicker, "Bug 07"));
            scenarios.Add(new Scenario("K07c: Broken source's area is re-lit by the cross-border independent source", KnownBug07C_CrossBorderRespread, "Bug 07"));
            scenarios.Add(new Scenario("K08a: Sunlight uplift mods survive an in-flight neighbor job (race)", KnownBug08A_InFlightSunlightUpliftLoss, "Bug 08"));
        }

        /// <summary>
        /// K06 (Bug 06): A lamp written by GENERATION (raw voxel write, no queue seeding — the
        /// <c>SetBlock</c> path). The initial lighting pass stamps the lamp's own emission via
        /// <c>SyncEmissionToLightArray</c> but never enqueues it for propagation, so the
        /// surroundings stay dark until something else touches the area.
        /// </summary>
        private static bool KnownBug06_GeneratedEmissiveSeedsBfs()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.SetBlock(new Vector3Int(24, 11, 24), TestBlockPalette.LampWhite);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "K06: initial lighting converges");

            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(25, 11, 24)) == (14, 14, 14),
                "K06: generated lamp illuminates its neighbor voxel",
                $"Expected (14,14,14) next to the lamp, got {world.GetBlocklightRGB(new Vector3Int(25, 11, 24))} — emission was stamped but never propagated");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "K06: field matches oracle");
            return passed;
        }

        /// <summary>
        /// K07a (Bug 07): Chunk (2,1) holds a blue lamp whose light bleeds across the border into
        /// chunk (1,1); then a red lamp is placed in (1,1) near that border. The correct result is
        /// the per-channel blend of both fields across the border. The documented defect: the red
        /// lamp's uplift mods are re-interpreted as removals in (2,1) (force-clear on
        /// <c>OldBlock &gt; 0</c> wake-ups) and the zero-channel pass-through wipes the blue channel —
        /// a hard cut-off at the border instead of a blend.
        /// </summary>
        private static bool KnownBug07A_TwoSourceBorderBlend()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "K07a: initial lighting converges");

            // First source: blue lamp in chunk (2,1), one voxel east of the border.
            world.PlaceBlock(new Vector3Int(33, 11, 24), TestBlockPalette.LampBlue);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "K07a: first source converges");

            // Second source: red lamp in chunk (1,1), against the border.
            world.PlaceBlock(new Vector3Int(30, 11, 24), TestBlockPalette.LampRed);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "K07a: second source converges");

            // Probe on the blue side: red must have crossed the border. The straight line
            // 30→34 passes THROUGH the opaque blue lamp at x=33, so the red light detours
            // over it: 30(15) → 31(14) → 32(13) → (32,12)(12) → (33,12)(11) → (34,12)(10)
            // → (34,11)(9). Oracle-verified.
            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(34, 11, 24)).R == 9,
                "K07a: red light crosses into the blue lamp's chunk",
                $"Expected R=9 at x=34 (detour around the opaque blue lamp), got {world.GetBlocklightRGB(new Vector3Int(34, 11, 24))} — hard cut-off at the border");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "K07a: blended field matches oracle");
            return passed;
        }

        /// <summary>
        /// K07b (Bug 07, flicker form): Red and blue lamps DIRECTLY adjacent across the border
        /// (x=31 / x=32) — the maximal mutual-interference configuration. The documented ping-pong
        /// (each side's uplift destructively re-interpreted by the other) manifests here as either
        /// non-convergence within the round budget (flicker) or a stable-but-wrong field (cut-off).
        /// </summary>
        private static bool KnownBug07B_AdjacentLampsNoFlicker()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "K07b: initial lighting converges");

            world.PlaceBlock(new Vector3Int(31, 11, 24), TestBlockPalette.LampRed);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "K07b: first lamp converges");

            world.PlaceBlock(new Vector3Int(32, 11, 24), TestBlockPalette.LampBlue);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "K07b: adjacent lamps converge without ping-pong");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "K07b: field matches oracle");
            return passed;
        }

        /// <summary>
        /// K07c (Bug 07, defect 2): Two white torches with overlapping fields on opposite sides of
        /// the border. Breaking the first must restore its area from the surviving cross-border
        /// source and return the field bit-identically to the single-source baseline. The documented
        /// defect: re-spread seeds for out-of-center voxels are dropped
        /// (<c>IsInCenterChunk</c> guard in <c>PropagateDarknessRGB</c>), so light removed on one
        /// side is never restored from the neighbor's contribution.
        /// </summary>
        private static bool KnownBug07C_CrossBorderRespread()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "K07c: initial lighting converges");

            // The surviving source: torch in chunk (2,1).
            world.PlaceBlock(new Vector3Int(36, 11, 24), TestBlockPalette.Torch);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "K07c: surviving source converges");

            Dictionary<Vector2Int, ushort[]> baseline = world.SnapshotLightField();

            // The doomed source: torch in chunk (1,1), fields overlapping across the border.
            world.PlaceBlock(new Vector3Int(28, 11, 24), TestBlockPalette.Torch);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "K07c: both sources converge");

            world.BreakBlock(new Vector3Int(28, 11, 24));
            passed &= LightingAssert.Converged(world.RunToConvergence(), "K07c: post-break convergence");

            passed &= LightingAssert.FieldsEqual(baseline, world, "K07c: field returns to the single-source baseline");
            return passed;
        }

        /// <summary>
        /// K08a (Bug 08, path 2): The in-flight overwrite race, made deterministic with the flight
        /// API. A roof spans the border; chunk (2,1) has a lighting job IN FLIGHT (inputs already
        /// snapshotted) when a roof block is broken in chunk (1,1) and that chunk's job applies
        /// sunlight uplift mods into (2,1)'s live data. Completing the in-flight job overwrites the
        /// uplift; the surviving wake-up node becomes a no-op (<c>currentLight == OldLightLevel</c>),
        /// and because edge checks never run after edits, the loss is permanent — (2,1) stays darker
        /// than the oracle under the roof.
        /// </summary>
        private static bool KnownBug08A_InFlightSunlightUpliftLoss()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);

            // Roof at y=30 spanning the (1,1)/(2,1) border.
            world.FillBox(new Vector3Int(26, 30, 18), new Vector3Int(38, 30, 30), TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "K08a: initial lighting converges");

            // Put chunk (2,1)'s job in flight, THEN break a roof block in (1,1) near the border and
            // run (1,1)'s job — its uplift mods land in (2,1)'s live data during the flight.
            LightingTestWorld.LightingJobFlight inFlight = world.BeginLightingJob(new Vector2Int(2, 1));

            world.BreakBlock(new Vector3Int(30, 30, 24));
            world.RunLightingJob(new Vector2Int(1, 1));

            // The stale merge: overwrites (2,1)'s light with pre-break values, losing the uplift.
            world.CompleteLightingJob(inFlight);

            passed &= LightingAssert.Converged(world.RunToConvergence(), "K08a: post-race convergence");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "K08a: field matches oracle after the race");
            return passed;
        }
    }
}
