using System.Collections.Generic;
using System.Text;
using Data;
using Editor.Validation.ChunkPipeline.Framework;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.ChunkPipeline
{
    /// <summary>
    /// Baseline scenarios for the chunk-pipeline state machine. All must stay green; a failure is a
    /// regression in the gate composition, the scheduling arms, or the unload policy.
    /// <para><b>Prove-red map</b> (each scenario's docstring names its own mutation; these are the
    /// cross-cutting ones): forcing <c>AreNeighborsMeshReady</c> to return true unconditionally reds B3 and
    /// B4; forcing <c>AreNeighborsDataReady</c> to return true reds B2; dropping the
    /// <c>WouldStrandInRangeNeighbor</c> arm from <see cref="Helpers.ChunkUnloadDecision.Evaluate"/> reds
    /// B5 while B1 — which asserts the deadlock — stays green, which is the intended asymmetry.</para>
    /// </summary>
    public static partial class ChunkPipelineValidationSuite
    {
        private const int FRAME_BUDGET = 64;

        /// <summary>Registers the baseline scenarios (called from <c>Execute</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B1 Harness fidelity: with the §9.6 strand guard neutered, the pipeline DEADLOCKS", B1UnguardedUnloadDeadlocks));
            scenarios.Add(new Scenario(
                "B2 Out-of-order generation completion still converges (no ordering assumption in the gates)", B2OutOfOrderGeneration));
            scenarios.Add(new Scenario(
                "B3 Wave-front cross-chunk mods converge — §9.3 starvation stays fixed by the relaxed mesh gate", B3WaveFrontConverges));
            scenarios.Add(new Scenario(
                "B4 Budget exhaustion mid-stage delays but never strands (un-served remainder stays schedulable)", B4BudgetExhaustion));
            scenarios.Add(new Scenario(
                "B5 §9.6 unload stranding: the strand guard defers, then releases once the chunk is no longer needed", B5StrandGuardDefers));
            scenarios.Add(new Scenario(
                "B6 Flag pairing: a converged neighbourhood leaves no chunk holding an unclearable lighting flag", B6FlagsPaired));
        }

        /// <summary>
        /// B1 — the harness's own prove-red, and the scenario every other convergence assertion depends on.
        /// Builds the §9.6 shape exactly: the center is light-pending and <b>gate-blocked</b> (its diagonal
        /// neighbor is an unpopulated placeholder, so <c>AreNeighborsDataReady</c> fails and the scan parks
        /// it with its flag still set), while its east neighbor leaves range. With fact gathering set to
        /// <c>SelfOnly</c> — the pre-fix code, which inspected only the chunk being unloaded — the east
        /// neighbor unloads. The blocking placeholder is then populated, which in a healthy pipeline would
        /// release the center; here it cannot, because the unloaded neighbor is now permanently missing from
        /// <c>AreNeighborsDataReady</c>.
        /// <para><b>The assertion is the center's flag, not its mesh.</b> Any chunk adjacent to an unloaded
        /// neighbor fails the mesh gate on the missing neighbor alone, so a mesh-based assertion here would
        /// be red whether or not stranding was fixed — a false green waiting to happen. Being permanently
        /// unable to clear <c>HasLightChangesToProcess</c> is what §9.6 actually describes.</para>
        /// <para>A PASS means the pump reproduces a real historical deadlock. A FAIL means the pump is
        /// modeling production badly and B2–B6's green is meaningless — fix
        /// <see cref="ChunkPipelineSimulator"/>, not the engine.</para>
        /// </summary>
        private static bool B1UnguardedUnloadDeadlocks()
        {
            StringBuilder log = new StringBuilder();
            bool ok = true;

            using (ChunkPipelineFixture fixture = new ChunkPipelineFixture())
            {
                ChunkPipelineSimulator sim = new ChunkPipelineSimulator(fixture)
                {
                    UnloadFacts = ChunkPipelineSimulator.UnloadFactGathering.SelfOnly,
                };

                SeedStrandShape(fixture, sim);
                sim.RunFrames(8);

                if (fixture.GetChunk(1, 0) != null)
                {
                    log.AppendLine("  [FAIL] unguarded unload proceeded — the needed neighbor was NOT " +
                                   "unloaded, so this scenario never sets up the strand");
                    ok = false;
                }

                // Release the gate-block. Only the missing unloaded neighbor still stands in the way.
                PopulateBlocker(fixture);
                sim.RunFrames(FRAME_BUDGET);

                ok &= PipelineAssert.StuckLightPending("unguarded unload strands the center",
                    fixture.GetChunk(0, 0), log);
            }

            Debug.Log(log.ToString().TrimEnd());
            return ok;
        }

        /// <summary>
        /// B2 — generation jobs completing out of order must not strand anyone. The corner chunks are
        /// requested last but admitted one per frame, so the center spends several frames failing
        /// <c>AreNeighborsDataReady</c> before its neighborhood completes.
        /// <para>Prove-red: make <c>AreNeighborsDataReady</c> return true unconditionally — the center
        /// schedules lighting against unpopulated neighbors and the parked count drops to 0, tripping the
        /// vacuity floor.</para>
        /// </summary>
        private static bool B2OutOfOrderGeneration()
        {
            StringBuilder log = new StringBuilder();
            bool ok;

            using (ChunkPipelineFixture fixture = new ChunkPipelineFixture())
            {
                ChunkPipelineSimulator sim = new ChunkPipelineSimulator(fixture)
                {
                    GenerationAdmissionsPerFrame = 1,
                };

                // Center first, corners last — the reverse of the order the center's gate needs.
                List<ChunkCoord> targets = new List<ChunkCoord> { new ChunkCoord(0, 0) };
                sim.RequestGeneration(new ChunkCoord(0, 0));
                for (int x = -1; x <= 1; x++)
                for (int z = -1; z <= 1; z++)
                {
                    if (x == 0 && z == 0) continue;
                    ChunkCoord coord = new ChunkCoord(x, z);
                    targets.Add(coord);
                    sim.RequestGeneration(coord);
                }

                SeedRing(fixture, radius: 2);

                bool converged = sim.RunUntilConverged(FRAME_BUDGET, targets, out ChunkPipelineSimulator.FrameResult totals);
                ok = PipelineAssert.Converged("staggered generation converges", converged, sim, targets, totals,
                    requireBlocking: true, log);
            }

            Debug.Log(log.ToString().TrimEnd());
            return ok;
        }

        /// <summary>
        /// B3 — §9.3 wave-front starvation. Every chunk's first lighting pass flags its cardinal neighbors
        /// with <c>HasLightChangesToProcess</c>, the exact ping-pong that once blocked interior chunks
        /// forever. It converges today only because meshing gates on the relaxed
        /// <c>AreNeighborsMeshReady</c> rather than <c>AreNeighborsReadyAndLit</c>.
        /// <para>Prove-red: point the pump's mesh gate at <c>AreNeighborsReadyAndLit</c> — the §9.3 deadlock
        /// returns and B3 reds while B2 stays green.</para>
        /// </summary>
        private static bool B3WaveFrontConverges()
        {
            StringBuilder log = new StringBuilder();
            bool ok;

            using (ChunkPipelineFixture fixture = new ChunkPipelineFixture())
            {
                ChunkPipelineSimulator sim = new ChunkPipelineSimulator(fixture)
                {
                    EmitCrossChunkModsOnLightingComplete = true,
                };

                List<ChunkCoord> targets = SeedNeighborhood(fixture, sim, radius: 1);
                bool converged = sim.RunUntilConverged(FRAME_BUDGET, targets, out ChunkPipelineSimulator.FrameResult totals);
                ok = PipelineAssert.Converged("cross-chunk mod wave converges", converged, sim, targets, totals,
                    requireBlocking: true, log);
            }

            Debug.Log(log.ToString().TrimEnd());
            return ok;
        }

        /// <summary>
        /// B4 — a per-frame budget of one lighting schedule and one mesh schedule must delay convergence,
        /// never prevent it: the un-served remainder stays in the ready set rather than being parked
        /// (pipeline §9.1's break semantics, which P-4's rate quota deliberately preserved).
        /// <para>Prove-red: park the un-served remainder on a budget break instead of leaving it ready — the
        /// chunks never re-enter the scan without a promotion event and B4 stops converging.</para>
        /// </summary>
        private static bool B4BudgetExhaustion()
        {
            StringBuilder log = new StringBuilder();
            bool ok;

            using (ChunkPipelineFixture fixture = new ChunkPipelineFixture())
            {
                ChunkPipelineSimulator sim = new ChunkPipelineSimulator(fixture)
                {
                    LightingSchedulesPerFrame = 1,
                    MeshSchedulesPerFrame = 1,
                    EmitCrossChunkModsOnLightingComplete = true,
                };

                List<ChunkCoord> targets = SeedNeighborhood(fixture, sim, radius: 1);
                bool converged = sim.RunUntilConverged(FRAME_BUDGET, targets, out ChunkPipelineSimulator.FrameResult totals);
                ok = PipelineAssert.Converged("starved budget still converges", converged, sim, targets, totals,
                    requireBlocking: true, log);
            }

            Debug.Log(log.ToString().TrimEnd());
            return ok;
        }

        /// <summary>
        /// B5 — the same §9.6 setup as B1, but with production's real fact gathering. The strand guard must
        /// defer the unload at least once (asserted, so the scenario cannot pass by simply never becoming an
        /// unload candidate); once the block lifts, the center must clear the flag B1 shows it can never clear when stranded, and the deferred chunk must finally be reclaimed.
        /// <para>Prove-red: drop the <c>WouldStrandInRangeNeighbor</c> arm from
        /// <see cref="Helpers.ChunkUnloadDecision.Evaluate"/> — B5 reds while B1 stays green.</para>
        /// </summary>
        private static bool B5StrandGuardDefers()
        {
            StringBuilder log = new StringBuilder();
            bool ok = true;

            using (ChunkPipelineFixture fixture = new ChunkPipelineFixture())
            {
                ChunkPipelineSimulator sim = new ChunkPipelineSimulator(fixture);
                SeedStrandShape(fixture, sim);

                // Phase 1 — while the center is light-pending and gate-blocked, the guard must hold the
                // unload back. Asserting the deferral (not merely the absence of harm) is what stops this
                // scenario from passing with the guard deleted.
                ChunkPipelineSimulator.FrameResult held = sim.RunFrames(8);
                if (held.UnloadDeferredStrand == 0)
                {
                    log.AppendLine("  [FAIL] strand guard engaged — the unload was never deferred, so this " +
                                   "scenario would pass with the guard removed entirely");
                    ok = false;
                }
                else if (fixture.GetChunk(1, 0) == null)
                {
                    log.AppendLine("  [FAIL] strand guard engaged — the needed neighbor was unloaded anyway");
                    ok = false;
                }
                else
                {
                    log.AppendLine($"  [PASS] strand guard held the unload back — deferred {held.UnloadDeferredStrand} time(s)");
                }

                // Phase 2 — release the gate-block. The center must now clear its lighting flag (the exact
                // thing B1 shows it can never do once stranded), and the deferred neighbor must finally be
                // reclaimed: a guard that defers forever is its own stall.
                PopulateBlocker(fixture);
                ChunkPipelineSimulator.FrameResult released = sim.RunFrames(FRAME_BUDGET);
                List<ChunkCoord> center = new List<ChunkCoord> { new ChunkCoord(0, 0) };
                ok &= PipelineAssert.FlagsPaired("center clears its lighting flag once the block lifts",
                    fixture, center, released.LightingScheduled, log);

                if (fixture.GetChunk(1, 0) != null)
                {
                    log.AppendLine("  [FAIL] deferred unload released — the neighbor is still pinned after the " +
                                   "center settled, so the guard defers forever");
                    ok = false;
                }
                else
                {
                    log.AppendLine("  [PASS] deferred unload released once the center no longer needed it");
                }
            }

            Debug.Log(log.ToString().TrimEnd());
            return ok;
        }

        /// <summary>
        /// B6 — flag pairing. After a wave-front run settles, no populated chunk may still hold
        /// <c>NeedsInitialLighting</c>, <c>HasLightChangesToProcess</c> or <c>IsAwaitingMainThreadProcess</c>:
        /// every one of those has a clear site that must have been reachable. The assertion carries a
        /// non-vacuity floor (flags must actually have been exercised) so it cannot pass on an idle world.
        /// <para>Prove-red: skip the <c>IsAwaitingMainThreadProcess</c> clear in the pump's lighting
        /// completion <c>finally</c> — the flag survives and B6 names the chunks holding it.</para>
        /// </summary>
        private static bool B6FlagsPaired()
        {
            StringBuilder log = new StringBuilder();
            bool ok;

            using (ChunkPipelineFixture fixture = new ChunkPipelineFixture())
            {
                ChunkPipelineSimulator sim = new ChunkPipelineSimulator(fixture)
                {
                    EmitCrossChunkModsOnLightingComplete = true,
                };

                List<ChunkCoord> targets = SeedNeighborhood(fixture, sim, radius: 1);
                sim.RunUntilConverged(FRAME_BUDGET, targets, out ChunkPipelineSimulator.FrameResult totals);
                ok = PipelineAssert.FlagsPaired("settled pipeline holds no stranded flags", fixture, targets,
                    totals.LightingScheduled, log);
            }

            Debug.Log(log.ToString().TrimEnd());
            return ok;
        }

        /// <summary>
        /// Seeds the §9.6 stranding shape shared by B1 and B5: a settled 3×3 neighborhood in which the
        /// center is light-pending and permanently gate-blocked by an <b>unpopulated</b> diagonal placeholder
        /// at (1,1), with the east neighbor (1,0) marked out of range. The block keeps the center's
        /// <c>HasLightChangesToProcess</c> from clearing — without it the ready-set scan clears the flag in
        /// step 5 before the unload pass runs, and no strand is ever reported.
        /// </summary>
        /// <param name="fixture">The fixture owning the stub world.</param>
        /// <param name="sim">The pump, for the out-of-range marking.</param>
        private static void SeedStrandShape(ChunkPipelineFixture fixture, ChunkPipelineSimulator sim)
        {
            for (int x = -1; x <= 1; x++)
            for (int z = -1; z <= 1; z++)
                fixture.AddChunk(x, z, populated: true, needsInitialLighting: false);

            SeedRing(fixture, radius: 2);

            // The gate-block: a registered but unpopulated diagonal neighbor of the center.
            fixture.GetChunk(1, 1).IsPopulated = false;

            fixture.GetChunk(0, 0).HasLightChangesToProcess = true;
            sim.MarkOutOfRange(new ChunkCoord(1, 0));
        }

        /// <summary>Populates the (1,1) gate-block, releasing the center's readiness gate.</summary>
        /// <param name="fixture">The fixture owning the stub world.</param>
        private static void PopulateBlocker(ChunkPipelineFixture fixture)
        {
            ChunkData blocker = fixture.GetChunk(1, 1);
            blocker.IsPopulated = true;
            blocker.NeedsInitialLighting = true;
        }

        /// <summary>
        /// Seeds a populated, unlit square of the given radius plus the ring beyond it (so the outermost
        /// targets have the neighbor data their gates read), and returns the target coords.
        /// </summary>
        private static List<ChunkCoord> SeedNeighborhood(ChunkPipelineFixture fixture, ChunkPipelineSimulator sim, int radius)
        {
            List<ChunkCoord> targets = new List<ChunkCoord>();
            for (int x = -radius; x <= radius; x++)
            for (int z = -radius; z <= radius; z++)
            {
                fixture.AddChunk(x, z, populated: true, needsInitialLighting: true);
                targets.Add(new ChunkCoord(x, z));
            }

            SeedRing(fixture, radius + 1);
            _ = sim; // The pump reads the world the fixture owns; nothing to register per-chunk.
            return targets;
        }

        /// <summary>
        /// Seeds the already-settled ring at the given radius: populated, initial lighting done, no pending
        /// flags. Support data the outermost targets' gates read — never itself a convergence target.
        /// </summary>
        /// <param name="fixture">The fixture owning the stub world.</param>
        /// <param name="radius">The Chebyshev radius of the ring to seed.</param>
        private static void SeedRing(ChunkPipelineFixture fixture, int radius)
        {
            for (int x = -radius; x <= radius; x++)
            for (int z = -radius; z <= radius; z++)
            {
                if (Mathf.Max(Mathf.Abs(x), Mathf.Abs(z)) != radius) continue;
                fixture.AddChunk(x, z, populated: true, needsInitialLighting: false);
            }
        }
    }
}
