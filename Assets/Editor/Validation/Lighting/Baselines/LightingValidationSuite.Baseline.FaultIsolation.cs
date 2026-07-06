using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;
using Jobs.BurstData;
using UnityEngine;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Baseline regression scenario for the completion-pass fault isolation extracted into the shared
    /// <c>LightingCompletionPass</c> skeleton (HF-4 #2; fidelity finding <b>B7</b>, full closure). The
    /// production pass isolates a per-job merge fault so one throwing merge cannot abort the whole pass or
    /// strand the other already-released jobs with disposed containers (the <c>ObjectDisposedException</c>
    /// cascade). Before HF-4 #2 that control flow lived only in <c>WorldJobManager.ProcessLightingJobs</c>
    /// and the harness could not replay it; now the simulator drives the same skeleton, so
    /// <see cref="LightingFrameSimulator.SetMergeFaultInjector"/> can inject a merge fault into one job of a
    /// multi-job pass and assert the invariant mechanically.
    /// <para>
    /// <b>B65</b> schedules four independent single-chunk lamp jobs into one completion pass, faults exactly
    /// one job's merge, and asserts: the fault is isolated and counted (the other three still merge — the
    /// pass is not aborted), the faulted job is removed rather than stranded (so it can reschedule), and the
    /// field recovers to the borderless oracle once the faulted chunk's work is resubmitted (an aborted merge
    /// faithfully discards the job's drained BFS work, so recovery models a later edit re-triggering it). A
    /// regression that removes the skeleton's <c>try/catch/finally</c> lets the injected exception escape
    /// <c>RunFrame</c>, which the suite runner reports as a scenario throw — this scenario is its own prove-red.
    /// </para>
    /// Self-registered via the <see cref="AddFaultIsolationBaselineScenarios"/> hook called from
    /// <c>AddBaselineScenarios</c> (the <c>Baselines/</c> group-partial pattern).
    /// </summary>
    public static partial class LightingValidationSuite
    {
        /// <summary>Registers the completion-pass fault-isolation baseline (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddFaultIsolationBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B65: A merge fault in one job of a multi-job completion pass is isolated — the other jobs still complete, the faulted job is removed (not stranded), and the field recovers to the oracle (HF-4 #2, finding B7)",
                Baseline_CompletionPassFaultIsolation));
        }

        /// <summary>The four corner chunks (pairwise ≥2 chunks apart) each carry one independent lamp, so
        /// each flags only its own chunk before any job runs — a genuine multi-job completion pass.</summary>
        private static readonly Vector2Int[] s_faultIsoChunks =
        {
            new Vector2Int(0, 0), new Vector2Int(2, 0), new Vector2Int(0, 2), new Vector2Int(2, 2),
        };

        /// <summary>Voxel position of the lamp seeded into the interior of the given corner chunk.</summary>
        /// <param name="chunkCoord">A corner chunk grid coordinate.</param>
        /// <returns>The lamp's world voxel position (chunk interior, above the floor).</returns>
        private static Vector3Int FaultIsoLampPos(Vector2Int chunkCoord) =>
            new Vector3Int(chunkCoord.x * VoxelData.ChunkWidth + 8, 11, chunkCoord.y * VoxelData.ChunkWidth + 8);

        /// <summary>
        /// B65: proves the shared completion-pass skeleton isolates a per-job merge fault. Builds four
        /// independent lamp jobs, schedules them into one pass, faults one job's merge, and asserts the pass
        /// completes the other three, counts exactly one isolated fault, and recovers to the oracle once the
        /// injector clears (the faulted job must have been removed rather than stranded in flight).
        /// </summary>
        /// <returns>True when the fault is isolated, the pass is not aborted, and the field recovers.</returns>
        private static bool Baseline_CompletionPassFaultIsolation()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();
            world.RunInitialLighting();

            LightingFrameSimulator sim = new LightingFrameSimulator(world);

            foreach (Vector2Int chunkCoord in s_faultIsoChunks)
                world.PlaceBlock(FaultIsoLampPos(chunkCoord), TestBlockPalette.LampWhite);

            // Frame A: nothing is in flight, so Phase 1 is empty and Phase 2 schedules all four independent
            // lamp jobs together — the multi-job pass the fault isolation needs.
            sim.RunFrame();
            bool ok = LightingAssert.IsTrue(sim.InFlightCount == s_faultIsoChunks.Length,
                "B65: four independent lamp jobs scheduled into one pass",
                $"expected {s_faultIsoChunks.Length} in-flight jobs, got {sim.InFlightCount}");

            // Frame B: complete the pass with a merge fault injected on the first corner. The shared skeleton
            // must isolate it: the other three merge, the faulted job is released + removed, and the faulted
            // chunk is re-flagged for a corrective pass.
            Vector2Int faulted = s_faultIsoChunks[0];
            sim.SetMergeFaultInjector(coord => coord == faulted);
            sim.RunFrame();

            ok &= LightingAssert.IsTrue(sim.LastFaultedMergeJobs == 1,
                "B65: exactly one merge fault was isolated",
                $"expected 1 isolated merge fault, got {sim.LastFaultedMergeJobs}");

            // The three non-faulted lamps completed despite the fault — the pass was not aborted.
            for (int i = 1; i < s_faultIsoChunks.Length; i++)
            {
                Vector3Int lampPos = FaultIsoLampPos(s_faultIsoChunks[i]);
                ok &= LightingAssert.IsTrue(LightBitMapping.GetBlocklightR(world.GetLightData(lampPos)) > 0,
                    $"B65: non-faulted lamp {s_faultIsoChunks[i]} completed and holds its emission despite the isolated fault");
            }

            // Recovery: a merge fault discards the job's drained BFS work (faithful to production — the job's
            // native queues are released on a stage-2 fault), so re-flagging alone cannot rebuild the faulted
            // chunk's lost sky/block waves. A later edit resubmits that work; model it by re-stamping the
            // faulted lamp, then converge. If the faulted job had instead been stranded in flight (its
            // in-flight marker not cleared), the chunk could never reschedule and this would hang / mismatch —
            // so this still guards AbortLightingJob's in-flight removal.
            sim.SetMergeFaultInjector(null);
            Vector3Int faultedLamp = FaultIsoLampPos(faulted);
            world.BreakBlock(faultedLamp);
            world.PlaceBlock(faultedLamp, TestBlockPalette.LampWhite);
            int frames = sim.RunToConvergence();
            ok &= LightingAssert.IsTrue(frames >= 0, "B65: converges after the injected fault clears (no stranded in-flight job)");
            ok &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B65: field recovers to the oracle once the faulted chunk's lost work is resubmitted");

            return ok;
        }
    }
}
