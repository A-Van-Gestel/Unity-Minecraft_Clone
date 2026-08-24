using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;
using Helpers;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Baseline for the §7 <b>weak-gate edge-check fallback</b> (LP-5, finding F4): border edge work rides
    /// ANY successful lighting schedule, including one taken through the regular arm under the relaxed
    /// <c>AreNeighborsDataReady</c> gate rather than the strict <c>AreNeighborsReadyAndLit</c> one. Until
    /// LP-5 that behavior had no dedicated baseline at all — it was documented in the pipeline doc and
    /// implied by convergence assertions elsewhere, but nothing named it.
    /// <para>
    /// <b>What this witnesses, precisely.</b> The scan's routing (<see cref="LightingScanDecision"/> sending
    /// this flag/gate combination to the regular arm) and the <i>harness's</i> consumption of the flag in
    /// <c>LightingTestWorld.BeginLightingJob</c>, which derives it through the same shared
    /// <see cref="ScheduledEdgeCheckDecision"/> production uses.
    /// </para>
    /// <para>
    /// <b>What it does NOT witness.</b> Production's own consumption. <c>WorldJobManager.ScheduleLightingUpdate</c>
    /// has three callers, all in <c>World.cs</c>, and no suite reaches it — so the two lines that feed the
    /// derived value into the real job (<c>PerformEdgeCheck</c> and the LI-2 band argument) are unobserved
    /// here. This is measured, not assumed: replacing production's <c>PerformEdgeCheck = performEdgeCheck</c>
    /// with <c>false</c> — border reconciliation switched off entirely — left the universal gate at
    /// 573/573 across 25 suites. Closing that needs a harness that drives the production scheduler
    /// (design doc LP-8); this baseline deliberately claims the narrower thing.
    /// </para>
    /// <para>
    /// <b>Prove-red, measured.</b> Dropping the flag term from the harness's derivation (deriving from
    /// <c>false</c> instead of <c>NeedsEdgeCheck</c>) reds <b>3 of 112</b>: B120, plus B32 and B64, which
    /// depend on the fallback for convergence without naming it. B120 is the one that says what broke.
    /// </para>
    /// Self-registered via the <see cref="AddEdgeCheckFallbackBaselineScenarios"/> hook.
    /// </summary>
    public static partial class LightingValidationSuite
    {
        /// <summary>Registers the LP-5 weak-gate fallback baseline (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddEdgeCheckFallbackBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B120: an armed edge check rides the REGULAR arm's schedule when neighbors are data-ready but not lit — the §7 weak-gate fallback (LP-5, finding F4)",
                Baseline_EdgeCheckRidesWeakGateSchedule));
        }

        /// <summary>
        /// B120: stages the exact state the fallback exists for — a chunk carrying <c>EdgeCheck</c> +
        /// <c>LightChanges</c> whose neighbors have terrain data but are NOT settled — then asserts the scan
        /// routes it to the regular arm and the resulting job still edge-checks, with both flags consumed.
        /// <para>
        /// The gate-split assertion is load-bearing and runs FIRST: if both neighbor gates happened to pass,
        /// the scenario would be exercising the dedicated edge arm and every later assertion would hold for
        /// the wrong reason — a vacuous pass. It is asserted rather than assumed.
        /// </para>
        /// </summary>
        /// <returns>True when all assertions pass.</returns>
        private static bool Baseline_EdgeCheckRidesWeakGateSchedule()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B120: initial lighting converges");

            Vector2Int center = new Vector2Int(1, 1);
            Vector2Int neighbor = new Vector2Int(0, 1);

            // The chunk under test carries a real, combined edge-check arm (production's cascade transition).
            world.ArmEdgeCheck(center);

            // One cardinal neighbor still has unsettled light: that is what splits the two gates apart —
            // terrain data exists (DataReady passes) but the border is still moving (ReadyAndLit blocks).
            world.FlagLightWork(neighbor);

            bool dataReady = world.AreNeighborsDataReady(center);
            bool readyAndLit = world.AreNeighborsReadyAndLit(center);

            passed &= LightingAssert.IsTrue(dataReady && !readyAndLit,
                "B120: the two neighbor gates disagree — the precondition the fallback exists for",
                $"Expected DataReady=true, ReadyAndLit=false; got DataReady={dataReady}, ReadyAndLit={readyAndLit}");

            // The scan must send this to the REGULAR arm — the edge arm is gated shut by ReadyAndLit.
            LightingScanDecision.ScanAction action = LightingScanDecision.EvaluateReadyChunk(
                world.IsChunkInFlight(center),
                world.ChunkNeedsInitialLighting(center),
                world.ChunkNeedsEdgeCheck(center),
                world.ChunkHasLightWork(center),
                dataReady,
                readyAndLit);

            passed &= LightingAssert.IsTrue(action == LightingScanDecision.ScanAction.ScheduleRegular,
                "B120: the scan routes the armed chunk to the regular arm, not the edge arm",
                $"Expected ScheduleRegular, got {action}");

            // Schedule with NO explicit edge request — exactly what the regular arm does. The edge check
            // must ride along anyway, off the chunk's own flag.
            LightingTestWorld.LightingJobFlight flight = world.BeginLightingJob(center);

            passed &= LightingAssert.IsTrue(flight.Job.PerformEdgeCheck,
                "B120: the regular-arm job still performs the border edge check (the fallback)",
                "Expected PerformEdgeCheck=true on a job scheduled without an explicit edge request");

            passed &= LightingAssert.IsTrue(!world.ChunkNeedsEdgeCheck(center),
                "B120: the edge-check flag is consumed by the schedule",
                "Expected NeedsEdgeCheck=false after BeginLightingJob");

            passed &= LightingAssert.IsTrue(!world.ChunkHasLightWork(center),
                "B120: the light-changes flag is consumed by the same schedule",
                "Expected HasLightChangesToProcess=false after BeginLightingJob");

            // Complete the flight so its native containers are released (a leaked flight fails the suite's
            // allocator checks, not this scenario's assertions).
            world.CompleteLightingJob(flight);

            return passed;
        }
    }
}
