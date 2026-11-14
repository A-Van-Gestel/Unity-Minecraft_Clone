using System.Collections.Generic;
using System.Text;
using Data;
using Editor.Validation.ChunkPipeline.Framework;
using Helpers;
using Jobs.BurstData;
using LightingJobData = global::Jobs.Data.LightingJobData;
using LightingJobInputData = global::Jobs.Data.LightingJobInputData;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.ChunkPipeline
{
    /// <summary>
    /// Baseline scenarios for the chunk-pipeline state machine. All must stay green; a failure is a
    /// regression in the gate composition, the scheduling arms, or the unload policy.
    /// <para><b>Prove-red map — OBSERVED, not predicted (swept 2026-08-23; the two rows that carry B5's and
    /// B6's prove-red re-measured the same day, after LP-3 retired the flag whose row this map lost).</b>
    /// Every mutation below was applied in isolation, the suite run, and the red-set recorded; each was then
    /// reverted. B1–B6 have each been observed failing at least once here; B7 carries its own prove-red in
    /// its docstring.</para>
    /// <list type="table">
    /// <item><term>Pump's mesh gate → <c>AreNeighborsReadyAndLit</c></term><description>reds B3, B4, B6 — B6's prove-red (re-measured post-LP-3)</description></item>
    /// <item><term>Budget break drops the work instead of leaving it ready</term><description>reds B4 alone</description></item>
    /// <item><term><c>World.AreNeighborsDataReady</c> always true</term><description>reds B1, B5 — B5's prove-red (re-measured post-LP-3)</description></item>
    /// <item><term>Generation admission cap ignored (no staggering)</term><description>reds B2, B3</description></item>
    /// <item><term>Drop the <c>WouldStrandInRangeNeighbor</c> arm from <see cref="Helpers.ChunkUnloadDecision.Evaluate"/></term><description>reds B5 alone — B1 stays green, the intended asymmetry</description></item>
    /// <item><term>Scan clears lighting flags in its <c>Park</c> branch (work dropped, not deferred)</term><description>reds B1, B2, B5, B6 — B3/B4 stay green; inside B6 only the clear/schedule balance fires, the end-state sweep does not</description></item>
    /// </list>
    /// <para><b>One prediction was wrong and is worth keeping.</b> B2's docstring used to claim that forcing
    /// <c>AreNeighborsDataReady</c> true would red it. It does not: target parks drop to zero, but B2's
    /// non-vacuity floor is still satisfied by its mesh declines, and it converges either way. B2's real
    /// prove-red is the admission-cap mutation. This is why the map above records measurements rather than
    /// expectations.</para>
    /// </summary>
    public static partial class ChunkPipelineValidationSuite
    {
        private const int FRAME_BUDGET = 64;
        private const byte B8_BLOCKLIGHT = 5;
        private const byte B8_BLOCKLIGHT_CHANGED = 6;
        private const int B9_HEIGHTMAP_LENGTH = 256;

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
            scenarios.Add(new Scenario(
                "B7 NeighborReadinessDecision census — all 3 gates × 2⁶ fact combinations match the gate contract (LP-2)", B7NeighborReadinessCensus));
            scenarios.Add(new Scenario(
                "B8 Startup sweep snapshots: one shared map per chunk, a live write is caught as stale, freed only after close and the last holder, no reuse after close", B8StartupSweepSnapshots));
            scenarios.Add(new Scenario(
                "B9 Startup sweep registry: one open set, every job's reference released before it is freed, a teardown frees what is left; a shared job's Dispose frees only what it owns", B9StartupSweepRegistry));
        }

        /// <summary>
        /// B9 — the job-side bookkeeping of the shared startup snapshots, world-free: the
        /// <see cref="StartupSweepSnapshotRegistry"/> the lighting scheduler takes sets from and releases them to, and
        /// <see cref="LightingJobData.Dispose"/> on a job that holds shared maps.
        /// </summary>
        private static bool B9StartupSweepRegistry()
        {
            StringBuilder log = new StringBuilder();
            bool ok = true;

            void Check(string label, bool condition)
            {
                log.AppendLine($"  [{(condition ? "PASS" : "FAIL")}] {label}");
                ok &= condition;
            }

            WorldData worldData = new WorldData("B9StartupSweepRegistry", 0);
            StartupSweepSnapshotRegistry registry = new StartupSweepSnapshotRegistry();
            Unity.Collections.NativeArray<ushort> ownedHeightmap = default;
            Unity.Collections.NativeArray<ushort> ownedLight = default;
            try
            {
                Check("Nothing to take before a sweep opens",
                    registry.TakeForJob(out int noId) == null && noId == 0);
                Check("One set opens; a second open is refused",
                    registry.Begin(worldData, Unity.Collections.Allocator.TempJob)
                    && !registry.Begin(worldData, Unity.Collections.Allocator.TempJob));

                StartupSweepSnapshots first = registry.TakeForJob(out int firstId);
                StartupSweepSnapshots second = registry.TakeForJob(out int secondId);
                Check("Two jobs take the same open set under one non-zero id",
                    first != null && ReferenceEquals(first, second) && firstId != 0 && firstId == secondId);

                // A job holding shared maps: its Dispose frees what it owns and leaves the shared maps to the set.
                ChunkCoord center = new ChunkCoord(0, 0);
                ownedHeightmap = new Unity.Collections.NativeArray<ushort>(B9_HEIGHTMAP_LENGTH, Unity.Collections.Allocator.TempJob);
                ownedLight = new Unity.Collections.NativeArray<ushort>(ChunkMath.CHUNK_VOLUME, Unity.Collections.Allocator.TempJob);
                LightingJobData job = new LightingJobData
                {
                    SharedSnapshotSet = firstId,
                    Input = new LightingJobInputData
                    {
                        Heightmap = ownedHeightmap,
                        Neighbors = NeighborMapAssembler.Build(center, first, false, Unity.Collections.Allocator.TempJob),
                    },
                    Map = first.AcquireVoxelMap(center, false, Unity.Collections.Allocator.TempJob),
                    LightMap = ownedLight,
                };
                Unity.Collections.NativeArray<uint> sharedNeighbor = job.Input.Neighbors.NeighborN;
                Unity.Collections.NativeArray<uint> sharedCenter = job.Map;
                job.Dispose();
                Check("A shared job's Dispose leaves the shared neighbor and center maps alive",
                    IsReadable(sharedNeighbor) && IsReadable(sharedCenter));
                Check("It frees the heightmap and light map it owns",
                    !IsReadable(ownedHeightmap) && !IsReadable(ownedLight));

                registry.Close();
                Check("A closed set lives while jobs hold it, and is not handed out",
                    registry.LiveSetCount == 1 && registry.TakeForJob(out int closedId) == null && closedId == 0);
                registry.Release(firstId);
                Check("It survives all but the last job's release", registry.LiveSetCount == 1 && !first.IsDisposed);
                registry.Release(secondId);
                Check("The last release frees it", registry.LiveSetCount == 0 && first.IsDisposed);
                registry.Release(firstId);
                Check("A release for a freed set is ignored", registry.LiveSetCount == 0);

                registry.Begin(worldData, Unity.Collections.Allocator.TempJob);
                StartupSweepSnapshots stranded = registry.TakeForJob(out int strandedId);
                Check("A later sweep gets a new id", strandedId != 0 && strandedId != firstId);
                registry.Dispose();
                Check("Teardown frees an open set still held by a job",
                    registry.LiveSetCount == 0 && stranded.IsDisposed);
            }
            finally
            {
                registry.Dispose();
                if (IsReadable(ownedHeightmap)) ownedHeightmap.Dispose();
                if (IsReadable(ownedLight)) ownedLight.Dispose();
            }

            Debug.Log(log.ToString().TrimEnd());
            return ok;
        }

        /// <summary>Whether a native array is still allocated: reading a freed one throws under the safety checks.</summary>
        private static bool IsReadable<T>(Unity.Collections.NativeArray<T> array) where T : struct
        {
            if (!array.IsCreated) return false;

            try
            {
                _ = array[0];
                return true;
            }
            catch (System.ObjectDisposedException)
            {
                return false;
            }
            catch (System.InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// B8 — <see cref="StartupSweepSnapshots"/> on a scratch <see cref="WorldData"/>: every reader of a chunk gets the
        /// same buffer filled from the live chunk, the stale check sees a write made after the fill, and the set outlives
        /// its close and every holder but the last. Writes blocklight, which a lighting-off fill's skylight stamp keeps.
        /// </summary>
        private static bool B8StartupSweepSnapshots()
        {
            StringBuilder log = new StringBuilder();
            bool ok = true;

            void Check(string label, bool condition)
            {
                log.AppendLine($"  [{(condition ? "PASS" : "FAIL")}] {label}");
                ok &= condition;
            }

            ChunkCoord lit = new ChunkCoord(0, 0);
            ChunkCoord missing = new ChunkCoord(5, 5);
            WorldData worldData = new WorldData("B8StartupSweepSnapshots", 0);
            ChunkData chunk = new ChunkData(lit.ToVoxelOrigin());
            chunk.SetLightData(1, 10, 1, LightBitMapping.SetBlocklightR(0, B8_BLOCKLIGHT));
            worldData.SetChunk(lit.ToVoxelOrigin(), chunk);
            int litIndex = ChunkMath.GetFlattenedIndexInChunk(1, 10, 1);

            StartupSweepSnapshots set = new StartupSweepSnapshots(worldData, Unity.Collections.Allocator.TempJob);
            StartupSweepSnapshots unheld = new StartupSweepSnapshots(worldData, Unity.Collections.Allocator.TempJob);
            try
            {
                Unity.Collections.NativeArray<ushort> first = set.AcquireLightMap(lit, false, Unity.Collections.Allocator.TempJob);
                Unity.Collections.NativeArray<ushort> second = set.AcquireLightMap(lit, true, Unity.Collections.Allocator.Persistent);
                set.AcquireVoxelMap(missing, false, Unity.Collections.Allocator.TempJob);
                Check("Two readers of one chunk share one light buffer", first.Equals(second));
                Check("The shared map holds the live light, an absent chunk an empty map",
                    LightBitMapping.GetBlocklightR(first[litIndex]) == B8_BLOCKLIGHT && set.SnapshotCount == 2);
                Check("An untouched set has no stale map", set.CountStaleSnapshots() == 0);

                chunk.SetLightData(1, 10, 1, LightBitMapping.SetBlocklightR(0, B8_BLOCKLIGHT_CHANGED));
                Check("A light write after the fill is caught as one stale map", set.CountStaleSnapshots() == 1);

                set.Retain();
                set.Retain();
                set.Close();
                Check("A closed set stays alive while jobs hold it", !set.IsDisposed);
                bool threw = false;
                try
                {
                    set.AcquireVoxelMap(lit, false, Unity.Collections.Allocator.TempJob);
                }
                catch (System.InvalidOperationException)
                {
                    threw = true;
                }

                Check("No map is handed out after the close", threw);
                set.Release();
                Check("It survives all but the last holder", !set.IsDisposed);
                set.Release();
                Check("The last holder frees it", set.IsDisposed);

                unheld.Close();
                Check("A set closed with no holder is freed at once", unheld.IsDisposed);
            }
            finally
            {
                set.Dispose();
                unheld.Dispose();
            }

            Debug.Log(log.ToString().TrimEnd());
            return ok;
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
        /// <para><b>Prove-red (observed):</b> ignore the pump's generation admission cap so every chunk
        /// arrives on frame 0 — the staggering this scenario exists to exercise disappears, the floor finds
        /// no target blocking, and B2 reds (alongside B3). Note the mutation that does <i>not</i> work:
        /// forcing <c>AreNeighborsDataReady</c> true leaves B2 green, because its mesh declines still satisfy
        /// the floor and it converges regardless.</para>
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
        /// B3 — §9.3 wave-front starvation. A pre-seeded neighborhood cannot reproduce this: with every
        /// chunk populated from frame 0, <c>AreNeighborsDataReady</c> passes immediately and no interior
        /// chunk is ever gated, so the scenario converges without exercising anything. The wave-front only
        /// forms when chunks <b>arrive over time</b> — so B3 drives generation one admission per frame while
        /// each completed lighting pass flags its cardinal neighbors, which is the ping-pong that once
        /// blocked interior chunks forever.
        /// <para>It converges today only because meshing gates on the relaxed <c>AreNeighborsMeshReady</c>
        /// rather than <c>AreNeighborsReadyAndLit</c>.</para>
        /// <para><b>Prove-red (observed):</b> point the pump's mesh gate at <c>AreNeighborsReadyAndLit</c> —
        /// the §9.3 deadlock returns and B3 reds (alongside B4 and B6) while B2 stays green. Ignoring the
        /// generation admission cap also reds it. The non-vacuity floor is scoped to the target set, so a B3
        /// that stopped gating its own targets reds rather than coasting on frontier parks — which is exactly
        /// what the pre-redesign version of this scenario did.</para>
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
                    GenerationAdmissionsPerFrame = 1,
                };

                // Chunks arrive over time — the leading edge keeps re-flagging interior chunks whose own
                // neighbors are still generating. Corners last, so the center waits on the slowest arrivals.
                List<ChunkCoord> targets = new List<ChunkCoord>();
                for (int ring = 0; ring <= 1; ring++)
                for (int x = -1; x <= 1; x++)
                for (int z = -1; z <= 1; z++)
                {
                    if (Mathf.Max(Mathf.Abs(x), Mathf.Abs(z)) != ring) continue;
                    ChunkCoord coord = new ChunkCoord(x, z);
                    targets.Add(coord);
                    sim.RequestGeneration(coord);
                }

                SeedRing(fixture, radius: 2);

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
        /// <para><b>Prove-red (observed):</b> drop the un-served remainder's flags on a budget break instead
        /// of leaving it ready — the work is never retried and B4 reds alone, the cleanest isolation in the
        /// suite.</para>
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

                List<ChunkCoord> targets = SeedNeighborhood(fixture, radius: 1);
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
        /// <para><b>Prove-red (observed):</b> drop the <c>WouldStrandInRangeNeighbor</c> arm from
        /// <see cref="Helpers.ChunkUnloadDecision.Evaluate"/> — B5 reds alone and B1 stays green, confirming
        /// the asymmetry this pair was built to express.</para>
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
        /// B6 — flag pairing, asserted from both ends.
        /// <list type="number">
        /// <item><b>End state:</b> after a wave-front run settles, no populated chunk may still hold
        /// <c>NeedsInitialLighting</c> or <c>HasLightChangesToProcess</c> — both have a clear site that must
        /// have been reachable. Carries a non-vacuity floor (flags must actually have been exercised) so it
        /// cannot pass on an idle world.</item>
        /// <item><b>Clear/schedule balance:</b> across the whole run, the number of chunks the ready-set scan
        /// clears flags on must equal the number of lighting jobs it schedules. The clear count is derived
        /// from <c>ChunkData</c> state either side of the scan
        /// (<see cref="ChunkPipelineSimulator.FrameResult.LightingFlagsCleared"/>), never from the code path
        /// that clears — so this is a witness, not a restatement.</item>
        /// </list>
        /// <para>Half 2 exists because half 1 alone gives B6 no signal that B3/B4 do not already carry: every
        /// flag-stranding mutation also breaks convergence. A scan that clears flags <i>without</i> scheduling
        /// silently drops work and makes the pipeline converge <i>better</i>, so only the balance check sees
        /// it.</para>
        /// <para><b>Prove-red (observed):</b> half 1 — point the pump's mesh gate at
        /// <c>AreNeighborsReadyAndLit</c>: the stricter gate starves the wave front, chunks end holding
        /// <c>HasLightChangesToProcess</c>, and B6 names them (B3 and B4 red too). Half 2 — clear the flags
        /// in the scan's <c>Park</c> branch: measured 30 clears against 18 schedules, and half 1 stayed
        /// <b>green</b> on the same run, which is the whole point of adding it. That mutation reds B1/B2/B5/B6 but
        /// leaves <b>B3 and B4 green</b> — the overlap half 1 could not escape.</para>
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

                List<ChunkCoord> targets = SeedNeighborhood(fixture, radius: 1);
                bool converged = sim.RunUntilConverged(FRAME_BUDGET, targets, out ChunkPipelineSimulator.FrameResult totals);
                ok = PipelineAssert.FlagsPaired("settled pipeline holds no stranded flags", fixture, targets,
                    totals.LightingScheduled, log);

                // Half 2 — the clear/schedule balance. Derived from chunk state either side of the scan, so
                // a scan that clears flags without scheduling (work dropped silently, pipeline converges
                // BETTER) fails here and nowhere else in this suite.
                if (totals.LightingFlagsCleared != totals.LightingScheduled)
                {
                    log.AppendLine($"  [FAIL] clear/schedule balance — the scan cleared lighting flags on " +
                                   $"{totals.LightingFlagsCleared} chunk(s) but scheduled " +
                                   $"{totals.LightingScheduled} job(s); every clear must buy a scheduled job");
                    ok = false;
                }
                else
                {
                    log.AppendLine($"  [PASS] clear/schedule balance — {totals.LightingFlagsCleared} flag " +
                                   "clear(s), each paired with a scheduled lighting job");
                }

                // The docstring claims a *settled* run; without this, B6 would green on a run that never
                // converged so long as the target flags happened to be clear.
                if (!converged)
                {
                    log.AppendLine($"  [FAIL] run settled — did not converge in {sim.Frame} frames, so the " +
                                   "flag sweep describes an unfinished pipeline");
                    ok = false;
                }
                else
                {
                    log.AppendLine($"  [PASS] run settled — converged in {sim.Frame} frames");
                }
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

            fixture.GetChunk(0, 0).FlagLightWork();
            sim.MarkOutOfRange(new ChunkCoord(1, 0));
        }

        /// <summary>Populates the (1,1) gate-block, releasing the center's readiness gate.</summary>
        /// <param name="fixture">The fixture owning the stub world.</param>
        private static void PopulateBlocker(ChunkPipelineFixture fixture)
        {
            ChunkData blocker = fixture.GetChunk(1, 1);
            blocker.IsPopulated = true;
            blocker.FlagInitialLighting();
        }

        /// <summary>
        /// Seeds a populated, unlit square of the given radius plus the ring beyond it (so the outermost
        /// targets have the neighbor data their gates read), and returns the target coords.
        /// </summary>
        private static List<ChunkCoord> SeedNeighborhood(ChunkPipelineFixture fixture, int radius)
        {
            List<ChunkCoord> targets = new List<ChunkCoord>();
            for (int x = -radius; x <= radius; x++)
            for (int z = -radius; z <= radius; z++)
            {
                fixture.AddChunk(x, z, populated: true, needsInitialLighting: true);
                targets.Add(new ChunkCoord(x, z));
            }

            SeedRing(fixture, radius + 1);
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

        /// <summary>
        /// B7 — sweeps all three <see cref="NeighborReadinessDecision.Gate"/> values against all 2⁶
        /// <see cref="NeighborReadinessDecision.NeighborFacts"/> combinations and asserts each result equals
        /// <see cref="ExpectedBlockReason"/>, an independent restatement of the gate contract.
        /// <para>B1–B6 exercise the gates through the pump, where a term swap can still converge and go
        /// unnoticed. This asserts the term matrix directly — specifically the three places the gates
        /// deliberately disagree: an unpopulated neighbor blocks <c>DataReady</c> and <c>MeshReady</c> but is
        /// skipped by <c>ReadyAndLit</c>; lighting in flight blocks only <c>ReadyAndLit</c>; and
        /// <c>MeshReady</c>'s initial-lighting term is bypassed when lighting is disabled.</para>
        /// <para><b>Prove-red:</b> drop the <c>lightingEnabled &amp;&amp;</c> guard from the
        /// <c>MeshReady</c> arm of <c>Evaluate</c> → the lighting-disabled rows diverge and B7 reds.</para>
        /// </summary>
        private static bool B7NeighborReadinessCensus()
        {
            StringBuilder log = new StringBuilder();
            StringBuilder mismatches = new StringBuilder();
            int checkedCount = 0;

            NeighborReadinessDecision.Gate[] gates =
            {
                NeighborReadinessDecision.Gate.DataReady,
                NeighborReadinessDecision.Gate.ReadyAndLit,
                NeighborReadinessDecision.Gate.MeshReady,
            };

            foreach (NeighborReadinessDecision.Gate gate in gates)
            {
                for (int mask = 0; mask < 64; mask++)
                {
                    bool generationInFlight = (mask & 1) != 0;
                    bool lightingInFlight = (mask & 2) != 0;
                    bool existsAndPopulated = (mask & 4) != 0;
                    bool needsInitialLighting = (mask & 8) != 0;
                    bool hasLightChanges = (mask & 16) != 0;
                    bool lightingEnabled = (mask & 32) != 0;

                    NeighborReadinessDecision.NeighborFacts facts = new NeighborReadinessDecision.NeighborFacts(
                        generationInFlight, lightingInFlight, existsAndPopulated, needsInitialLighting,
                        hasLightChanges, lightingEnabled);

                    NeighborReadinessDecision.BlockReason expected = ExpectedBlockReason(gate, facts);
                    NeighborReadinessDecision.BlockReason actual = NeighborReadinessDecision.Evaluate(gate, facts);
                    checkedCount++;

                    if (actual == expected) continue;

                    mismatches.AppendLine(
                        $"    {gate}: gen={generationInFlight}, light={lightingInFlight}, pop={existsAndPopulated}, " +
                        $"init={needsInitialLighting}, changes={hasLightChanges}, " +
                        $"lightingEnabled={lightingEnabled}: expected {expected}, got {actual}");
                }
            }

            bool ok = mismatches.Length == 0;
            log.AppendLine(ok
                ? $"  [PASS] NeighborReadinessDecision census — all {checkedCount} gate × fact combinations " +
                  "match the contract oracle (per-gate unpopulated / lighting-in-flight / lighting-disabled asymmetries intact)"
                : $"  [FAIL] NeighborReadinessDecision census — {checkedCount} combinations checked; " +
                  $"divergences from the contract:\n{mismatches.ToString().TrimEnd()}");

            Debug.Log(log.ToString().TrimEnd());
            return ok;
        }

        /// <summary>
        /// Independent restatement of the three gates' CONTRACT — B7's oracle. A separate copy of the spec,
        /// NOT a call into <see cref="NeighborReadinessDecision.Evaluate"/>, so a mutation to the production
        /// predicate diverges from it (the prove-red mechanism). Transcribed from the three original
        /// <c>World</c> loops, not from the extracted code.
        /// <para><b>What this independence does not cover:</b> retiring a term edits the predicate and this
        /// oracle in the same change, so B7 stays green through the removal by construction. It witnesses a
        /// term that <i>misbehaves</i>, never a term that <i>disappears</i> — LP-3 removed
        /// <c>AwaitingMainThread</c> under exactly that blind spot.</para>
        /// </summary>
        /// <param name="gate">The gate whose rules to apply.</param>
        /// <param name="facts">The neighbor facts to judge.</param>
        /// <returns>The reason the neighbor blocks, or <c>None</c>.</returns>
        private static NeighborReadinessDecision.BlockReason ExpectedBlockReason(
            NeighborReadinessDecision.Gate gate, in NeighborReadinessDecision.NeighborFacts facts)
        {
            if (facts.GenerationInFlight) return NeighborReadinessDecision.BlockReason.GenerationInFlight;

            if (gate == NeighborReadinessDecision.Gate.DataReady)
                return facts.ExistsAndPopulated
                    ? NeighborReadinessDecision.BlockReason.None
                    : NeighborReadinessDecision.BlockReason.NotPopulated;

            if (gate == NeighborReadinessDecision.Gate.MeshReady)
            {
                if (!facts.ExistsAndPopulated) return NeighborReadinessDecision.BlockReason.NotPopulated;

                return facts.LightingEnabled && facts.NeedsInitialLighting
                    ? NeighborReadinessDecision.BlockReason.NeedsInitialLighting
                    : NeighborReadinessDecision.BlockReason.None;
            }

            // ReadyAndLit: the in-flight lighting check precedes the populated guard, so an unpopulated
            // neighbor with a lighting job still blocks — matching the original loop's ordering.
            if (facts.LightingInFlight) return NeighborReadinessDecision.BlockReason.LightingInFlight;
            if (!facts.ExistsAndPopulated) return NeighborReadinessDecision.BlockReason.None;

            if (facts.HasLightChanges) return NeighborReadinessDecision.BlockReason.PendingLightWork;
            if (facts.NeedsInitialLighting) return NeighborReadinessDecision.BlockReason.NeedsInitialLighting;

            return NeighborReadinessDecision.BlockReason.None;
        }
    }
}
