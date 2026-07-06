using System;
using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;
using UnityEngine;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// AS-2 Phase-3 scheduler-mode baselines — the regression guards that close fidelity finding <b>B6</b>
    /// (the MT-2 <c>LightWorkScheduler</c> park/promote layer was unmodeled in the frame simulator). Phase 1/2
    /// added scheduler mode to <see cref="LightingFrameSimulator"/> (a real <c>LightWorkScheduler</c> ready/waiting
    /// split driven off the shared <see cref="LightingScanDecision"/>, with completion / neighbor-ready
    /// promotion hooks); these scenarios exercise it with the <c>PromoteAll</c> fail-safe <b>off</b>, which is
    /// the load-bearing AS-2 assertion: <i>a scenario that only converges with the fail-safe on has a missing
    /// promotion hook</i>. Registered via the <see cref="AddSchedulerModeBaselineScenarios"/> hook.
    /// <para>
    /// The Bug-09 fleet (B15/B16/B22/B26–B29/B40/B41) stays in legacy mode, byte-identical; these are the
    /// distinct scheduler-mode second pass, so a regression in either scheduling path is a named failure.
    /// </para>
    /// </summary>
    public static partial class LightingValidationSuite
    {
        /// <summary>Registers the AS-2 scheduler-mode baselines (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddSchedulerModeBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B66: Cross-chunk lamp break→re-place converges to the oracle in BOTH legacy and scheduler mode, fail-safe off (AS-2 Phase 3, finding B6)",
                Baseline_SchedulerCrossChunkBothModes));
            scenarios.Add(new Scenario(
                "B67: A chunk parked on not-ready neighbors re-enters and converges only via the neighbor-ready promotion (scheduler mode, fail-safe off, finding B6)",
                Baseline_SchedulerNeighborsNotReadyPromotion));
            scenarios.Add(new Scenario(
                "B68: All Bug-09 geometry-fuzz seeds converge in scheduler mode with the fail-safe off — no missed-promotion stall (AS-2 Phase 3, finding B6)",
                Baseline_SchedulerModeBug09Fuzz));
            scenarios.Add(new Scenario(
                "B69: Suppressing the completion promotion stalls a chunk re-flagged mid-flight (fail-safe off) and only the fail-safe recovers it — prove-red for the completion promotion hook (AS-2 Phase 3, finding B6)",
                Baseline_SchedulerCompletionPromotionProveRed));
            scenarios.Add(new Scenario(
                "B70: Border-heightmap fuzz (generation wave + border edits, incl. Bug 05's re-granted edge round) settles on the oracle in scheduler mode with the fail-safe off (AS-2 Phase 3 reconcile, finding B6)",
                Baseline_SchedulerModeBorderHeightFuzz));
        }

        /// <summary>
        /// Runs <paramref name="body"/> against a fresh world twice — once in legacy mode, once in AS-2
        /// scheduler mode with the <c>PromoteAll</c> fail-safe off — and passes only if both converge (the body
        /// returns true for a convergence-to-oracle). The two runs are independent worlds so scheduler-mode
        /// state cannot leak into the legacy pass. Emits one <c>[PASS]</c>/<c>[FAIL]</c> line.
        /// </summary>
        /// <param name="worldFactory">Builds a fully set-up, converged world for each run.</param>
        /// <param name="body">The scenario; returns true on convergence-to-oracle. Must be mode-agnostic —
        /// use <c>sim.MarkNeighborsReady</c>/<c>sim.MarkChunkLoaded</c> (the promoting wrappers) for any
        /// generation/load events so the scheduler-mode run gets its promotions.</param>
        /// <param name="name">The scenario name for the combined log line.</param>
        /// <returns>True when both modes converge.</returns>
        private static bool RunScenarioBothModes(
            Func<LightingTestWorld> worldFactory,
            Func<LightingTestWorld, LightingFrameSimulator, bool> body,
            string name)
        {
            bool legacy;
            using (LightingTestWorld world = worldFactory())
                legacy = body(world, new LightingFrameSimulator(world));

            bool scheduler;
            using (LightingTestWorld world = worldFactory())
                scheduler = body(world, new LightingFrameSimulator(world, seed: null, schedulerMode: true));

            return LightingAssert.IsTrue(legacy && scheduler, name,
                $"legacy-mode pass={legacy}, scheduler-mode pass={scheduler} (both must converge to the oracle with the fail-safe off)");
        }

        /// <summary>Builds a superflat gridSize-3 world with a converged lamp on the (1,1)/(2,1) border.</summary>
        /// <returns>The set-up world (lamp placed and lit).</returns>
        private static LightingTestWorld BuildSchedulerCrossChunkWorld()
        {
            LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();
            world.RunInitialLighting();
            world.PlaceBlock(new Vector3Int(31, 11, 24), TestBlockPalette.LampWhite);
            world.RunToConvergence();
            return world;
        }

        /// <summary>
        /// B66: the everyday cross-chunk path through the scheduler — break the border lamp (its removal wave
        /// crosses the seam) then re-place it (re-spread), and require convergence to the borderless oracle in
        /// both modes. In scheduler mode the neighbor re-lights via the flag sink + completion promotion; a
        /// broken hook shows up as a scheduler-mode-only non-convergence.
        /// </summary>
        /// <returns>True when both modes converge to the oracle.</returns>
        private static bool Baseline_SchedulerCrossChunkBothModes()
        {
            Vector3Int lampPos = new Vector3Int(31, 11, 24);
            return RunScenarioBothModes(
                BuildSchedulerCrossChunkWorld,
                (world, sim) =>
                {
                    world.BreakBlock(lampPos);
                    sim.RunFrame();
                    world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
                    if (sim.RunToConvergence() < 0) return false;
                    return LightingAssert.MatchesOracleQuiet(world, LightingOracle.Solve(world), out _);
                },
                "B66: cross-chunk lamp break→re-place converges to the oracle in legacy AND scheduler mode (fail-safe off)");
        }

        /// <summary>
        /// B67: exercises the neighbor-ready promotion hook. A chunk whose neighbor terrain is still generating
        /// parks in scheduler mode (the ready scan can't schedule it); with the fail-safe off nothing re-adds it
        /// until <c>sim.MarkNeighborsReady</c> fires <c>PromoteNeighborhood</c>. Proves the parked chunk both
        /// stays parked (no spurious scheduling) and then converges once — and only once — the promotion runs.
        /// </summary>
        /// <returns>True when the chunk parks then converges via the neighbor-ready promotion.</returns>
        private static bool Baseline_SchedulerNeighborsNotReadyPromotion()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B67: initial lighting converges");

            LightingFrameSimulator sim = new LightingFrameSimulator(world, seed: null, schedulerMode: true);
            Vector2Int litChunk = new Vector2Int(1, 1);
            Vector3Int lampPos = new Vector3Int(31, 11, 24);

            // (1,1)'s neighbor terrain is still generating → the edit flags it, the ready scan parks it.
            world.MarkNeighborsNotReady(litChunk);
            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);

            for (int i = 0; i < 5; i++) sim.RunFrame();

            passed &= LightingAssert.IsTrue(sim.InFlightCount == 0 && world.ChunkHasLightWork(litChunk),
                "B67: chunk stays parked with pending work while neighbors are not ready (fail-safe off)",
                $"Expected InFlightCount 0 + pending work, got InFlight={sim.InFlightCount}, hasWork={world.ChunkHasLightWork(litChunk)}");
            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(lampPos) == (0, 0, 0),
                "B67: no blocklight propagates while parked",
                $"Expected (0,0,0) while parked, got {world.GetBlocklightRGB(lampPos)}");

            // The neighbor-ready promotion (sim wrapper → PromoteNeighborhood) un-parks it → converges.
            sim.MarkNeighborsReady(litChunk);
            passed &= LightingAssert.Converged(sim.RunToConvergence(), "B67: converges after the neighbor-ready promotion");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B67: field matches the oracle after the promotion resolves the park");

            return passed;
        }

        /// <summary>
        /// B68: the broad B6 guard — re-runs the 50-seed Bug-09 geometry fuzz (randomized border/source/held
        /// chunk/budget) through scheduler mode with the fail-safe off. Every seed's cross-chunk break/place
        /// churn must reach the oracle driven only by the real park/promote layer; a missed-promotion stall on
        /// any seed shows up as non-convergence. The legacy pass of the same fuzz is B40.
        /// </summary>
        /// <returns>True when every fuzz seed converges in scheduler mode.</returns>
        private static bool Baseline_SchedulerModeBug09Fuzz()
        {
            int? failingSeed = SweepBug09Fuzz(BUG09_FUZZ_BASELINE_ITERATIONS, startSeed: 0, schedulerMode: true);
            return LightingAssert.IsTrue(!failingSeed.HasValue,
                $"B68: all {BUG09_FUZZ_BASELINE_ITERATIONS} Bug-09 geometry-fuzz seeds converge in scheduler mode (fail-safe off)",
                failingSeed.HasValue
                    ? $"REGRESSION: scheduler-mode seed {failingSeed.Value} fails to converge — {Bug09FuzzCase.FromSeed(failingSeed.Value).Describe()}"
                    : "");
        }

        /// <summary>
        /// B69: the prove-red for the completion promotion hook. A chunk whose lighting job is in flight is
        /// re-flagged by a fresh edit — the ready scan parks it (a job is already in flight), and the ONLY
        /// thing that un-parks it is its own job's completion promotion (<c>PromoteNeighborhood</c> in the
        /// driver's <c>RemoveAndPromote</c>). With <see cref="LightingFrameSimulator.SuppressCompletionPromotion"/>
        /// on and the fail-safe off it therefore stalls (non-convergence); the ~1 s <c>PromoteAll</c> fail-safe
        /// then recovers it, proving the parked work was valid and only the promotion was missing. Baking the
        /// stall assertion green here means a regression that drops the completion promotion flips B69 red.
        /// </summary>
        /// <returns>True when suppression stalls the chunk and the fail-safe recovers it to the oracle.</returns>
        private static bool Baseline_SchedulerCompletionPromotionProveRed()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B69: initial lighting converges");

            Vector3Int lampPos = new Vector3Int(24, 11, 24); // interior of chunk (1,1) — single-chunk churn
            Vector2Int chunkA = new Vector2Int(1, 1);
            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B69: initial lamp converges");

            LightingFrameSimulator sim = new LightingFrameSimulator(world, seed: null, schedulerMode: true);

            // Break the lamp → schedule A's removal job, then HOLD it in flight.
            world.BreakBlock(lampPos);
            sim.RunFrame(completionPredicate: LightingFrameSimulator.ExceptChunks(chunkA));
            // Re-place the lamp WHILE A's job is in flight → A re-flags; the ready scan parks it (job in flight).
            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            sim.RunFrame(completionPredicate: LightingFrameSimulator.ExceptChunks(chunkA));

            // Prove-red: with the completion promotion suppressed, completing A's held job leaves its re-flagged
            // work parked → it never reschedules → stalls with the fail-safe off.
            sim.SuppressCompletionPromotion = true;
            int stalled = sim.RunToConvergence(maxFrames: 30);
            passed &= LightingAssert.IsTrue(stalled < 0,
                "B69: suppressing the completion promotion stalls the re-flagged chunk (fail-safe off)",
                $"Expected a stall (non-convergence) with the completion promotion suppressed, but converged in {stalled} frame(s) — the hook is not load-bearing here");

            // Recover: the ~1 s PromoteAll fail-safe is the backstop — it re-seeds the parked work → converges.
            sim.SuppressCompletionPromotion = false;
            sim.FailSafePromoteAll();
            passed &= LightingAssert.Converged(sim.RunToConvergence(maxFrames: 30),
                "B69: the fail-safe backstop recovers the stalled chunk");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B69: field matches the oracle after the fail-safe recovery");

            return passed;
        }

        /// <summary>
        /// B70: the AS-2 Phase-3 reconcile for the <c>RunReGrantedEdgeCheckRound</c> quiescence hook. Re-runs the
        /// border-heightmap fuzz (varied seam heights + seam overhangs + seeded border edits — the same geometry
        /// as B64, which found Bug 05's post-edit edge-round exhaustion) in scheduler mode with the fail-safe
        /// off. Scheduler mode has the real <c>AreNeighborsReadyAndLit</c> edge gate in its scan, so a re-granted
        /// border edge check settles through the normal park/promote path (backstopped by the shared
        /// quiescence hook, which is retained for legacy mode). Every seed must settle on the oracle.
        /// </summary>
        /// <returns>True when every border-heightmap seed settles on the oracle in scheduler mode.</returns>
        private static bool Baseline_SchedulerModeBorderHeightFuzz()
        {
            int? failingSeed = SweepBorderHeightFuzz(BORDER_FUZZ_BASELINE_ITERATIONS, startSeed: 0, schedulerMode: true);
            return LightingAssert.IsTrue(!failingSeed.HasValue,
                $"B70: all {BORDER_FUZZ_BASELINE_ITERATIONS} border-heightmap seeds settle on the oracle in scheduler mode (fail-safe off)",
                failingSeed.HasValue
                    ? $"REGRESSION: scheduler-mode border-heightmap seed {failingSeed.Value} does not settle — {BorderHeightFuzzCase.FromSeed(failingSeed.Value).Describe()}"
                    : "");
        }
    }
}
