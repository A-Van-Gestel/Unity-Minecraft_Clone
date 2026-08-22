using System.Collections.Generic;
using Data;
using Helpers;
using UnityEngine;

namespace Editor.Validation.ChunkPipeline.Framework
{
    /// <summary>
    /// Frame pump replaying <c>World.Update()</c>'s step order over the
    /// <see cref="ChunkPipelineFixture"/>'s stub world (CHUNK_LIFECYCLE_PIPELINE.md §4). Every scheduling
    /// <b>decision</b> is production code — the three real readiness gates plus
    /// <see cref="LightingScanDecision"/>, <see cref="MeshingScheduleDecision"/> and
    /// <see cref="ChunkUnloadDecision"/>; only stage <b>execution</b> (running a Burst job) is modeled as an
    /// event this pump raises after a configurable latency.
    /// <para>That split is deliberate. Job output is owned by the Lighting and Meshing suites; what has never
    /// had a guard is the state machine that decides <i>when</i> those jobs may run, which is where all three
    /// historical deadlocks lived. The consequence is that this suite cannot see a bug <i>inside</i> a job.</para>
    /// <para><b>Fidelity gate.</b> A pump that models production badly would let every scenario converge and
    /// prove nothing. The suite's first scenario therefore neuters the §9.6 strand guard and requires the pump
    /// to <i>deadlock</i>; if it converges anyway, this class is wrong, not the engine.</para>
    /// </summary>
    public sealed class ChunkPipelineSimulator
    {
        /// <summary>How the unloader gathers its facts — the §9.6 lever.</summary>
        public enum UnloadFactGathering : byte
        {
            /// <summary>Current production: the strand fact inspects the 8 neighbors.</summary>
            WithStrandGuard,

            /// <summary>The pre-§9.6-fix shape: only the chunk being unloaded is inspected, so
            /// <c>WouldStrandInRangeNeighbor</c> is never reported and a needed neighbor is stranded.</summary>
            SelfOnly,
        }

        /// <summary>Per-frame counters — the non-vacuity evidence that a scenario's adversarial order bit.</summary>
        public struct FrameResult
        {
            /// <summary>Generation jobs completed this frame.</summary>
            public int GenerationCompleted;

            /// <summary>Lighting jobs scheduled this frame.</summary>
            public int LightingScheduled;

            /// <summary>Chunks parked by the ready-set scan (a readiness gate failed, or a job is in flight).</summary>
            public int LightingParked;

            /// <summary>Mesh jobs scheduled this frame.</summary>
            public int MeshScheduled;

            /// <summary>Mesh scheduling attempts declined by a gate — the chunk stays queued.</summary>
            public int MeshDeclined;

            /// <summary>Chunks actually unloaded this frame.</summary>
            public int Unloaded;

            /// <summary>Unload candidates deferred by the strand guard.</summary>
            public int UnloadDeferredStrand;
        }

        private struct Flight
        {
            public ChunkCoord Coord;
            public int CompletesOnFrame;
        }

        private readonly ChunkPipelineFixture _fixture;
        private readonly List<Flight> _generationFlights = new List<Flight>();
        private readonly List<Flight> _lightingFlights = new List<Flight>();
        private readonly List<Flight> _meshFlights = new List<Flight>();
        private readonly Queue<ChunkCoord> _generationRequests = new Queue<ChunkCoord>();
        private readonly List<ChunkCoord> _meshQueue = new List<ChunkCoord>();
        private readonly HashSet<ChunkCoord> _meshed = new HashSet<ChunkCoord>();
        private readonly HashSet<ChunkCoord> _outOfRange = new HashSet<ChunkCoord>();
        private readonly HashSet<ChunkCoord> _initialLightingDone = new HashSet<ChunkCoord>();
        private readonly List<ChunkCoord> _scanScratch = new List<ChunkCoord>();
        private readonly List<Flight> _completedScratch = new List<Flight>();

        private int _frame;

        /// <summary>Creates a pump over a fixture's stub world.</summary>
        /// <param name="fixture">The fixture owning the stub world and job dictionaries.</param>
        public ChunkPipelineSimulator(ChunkPipelineFixture fixture) => _fixture = fixture;

        /// <summary>Frames a scheduled job takes to complete (1 = completes on the next frame's process step).</summary>
        public int JobLatencyFrames { get; set; } = 1;

        /// <summary>Per-frame ceiling on generation admissions (P-4 §3.1 in-flight cap).</summary>
        public int GenerationAdmissionsPerFrame { get; set; } = int.MaxValue;

        /// <summary>Per-frame ceiling on lighting schedules (the ready-set scan's budget throttle).</summary>
        public int LightingSchedulesPerFrame { get; set; } = int.MaxValue;

        /// <summary>Per-frame ceiling on mesh schedules (the drain's quota).</summary>
        public int MeshSchedulesPerFrame { get; set; } = int.MaxValue;

        /// <summary>
        /// When true, a chunk's first completed lighting pass flags its 4 cardinal neighbors with
        /// <c>HasLightChangesToProcess</c> — the cross-chunk mod emission that drives §9.2 ping-pong and
        /// §9.3 wave-front starvation. Off by default so a scenario opts into the pressure it wants.
        /// </summary>
        public bool EmitCrossChunkModsOnLightingComplete { get; set; }

        /// <summary>How the unload pass gathers its facts (the §9.6 lever).</summary>
        public UnloadFactGathering UnloadFacts { get; set; } = UnloadFactGathering.WithStrandGuard;

        /// <summary>The frame index the pump is about to run.</summary>
        public int Frame => _frame;

        /// <summary>Chunks that have had a mesh applied — the convergence target.</summary>
        public IReadOnlyCollection<ChunkCoord> Meshed => _meshed;

        /// <summary>Requests terrain generation for a coordinate (production: <c>CheckViewDistance</c> enqueue).</summary>
        /// <param name="coord">The chunk to generate.</param>
        public void RequestGeneration(ChunkCoord coord) => _generationRequests.Enqueue(coord);

        /// <summary>Requests a mesh rebuild (production: <c>RequestChunkMeshRebuild</c>).</summary>
        /// <param name="coord">The chunk to re-mesh.</param>
        public void RequestMesh(ChunkCoord coord)
        {
            if (!_meshQueue.Contains(coord)) _meshQueue.Add(coord);
        }

        /// <summary>Marks a chunk as beyond the unload distance, making it an unload candidate.</summary>
        /// <param name="coord">The chunk to move out of range.</param>
        public void MarkOutOfRange(ChunkCoord coord) => _outOfRange.Add(coord);

        /// <summary>
        /// Runs one frame in <c>World.Update()</c>'s order (§4): process generation → drain admissions →
        /// apply modifications → process lighting → ready-set scan → process mesh → schedule mesh, with the
        /// unload pass last.
        /// </summary>
        /// <returns>This frame's counters.</returns>
        public FrameResult RunFrame()
        {
            FrameResult result = default;

            // Step 2 — ProcessGenerationJobs (jobs scheduled on a previous frame).
            CollectCompleted(_generationFlights);
            foreach (Flight flight in _completedScratch)
            {
                _fixture.Jobs.GenerationJobs.Remove(flight.Coord);
                ChunkData chunk = Resolve(flight.Coord);
                if (chunk == null) continue; // Discarded out-of-range mid-flight (pipeline §3.2).
                chunk.IsPopulated = true;
                chunk.NeedsInitialLighting = true;
                result.GenerationCompleted++;
            }

            // Step 1b — DrainGenerationRequests: admit under the in-flight cap.
            int admitted = 0;
            while (_generationRequests.Count > 0 && admitted < GenerationAdmissionsPerFrame)
            {
                ChunkCoord coord = _generationRequests.Dequeue();
                if (Resolve(coord) == null) _fixture.AddChunk(coord.X, coord.Z, populated: false);
                _fixture.Jobs.GenerationJobs[coord] = default;
                _generationFlights.Add(new Flight { Coord = coord, CompletesOnFrame = _frame + JobLatencyFrames });
                admitted++;
            }

            // Step 3 — ApplyModifications: no voxel edits are scripted in this slice.

            // Step 4 — ProcessLightingJobs (jobs scheduled on a previous frame).
            CollectCompleted(_lightingFlights);
            foreach (Flight flight in _completedScratch)
            {
                _fixture.Jobs.LightingJobs.Remove(flight.Coord);
                ChunkData chunk = Resolve(flight.Coord);
                if (chunk == null) continue;

                // The merge window: production sets this at merge start and clears it in a per-job finally.
                chunk.IsAwaitingMainThreadProcess = true;
                try
                {
                    bool wasInitial = _initialLightingDone.Add(flight.Coord);
                    if (wasInitial && EmitCrossChunkModsOnLightingComplete) EmitCrossChunkMods(flight.Coord);

                    // A settled pass requests the mesh (production: IsStable -> RequestChunkMeshRebuild).
                    if (!chunk.HasLightChangesToProcess) RequestMesh(flight.Coord);
                }
                finally
                {
                    chunk.IsAwaitingMainThreadProcess = false;
                }
            }

            // Step 5 — the lighting ready-set scan, through the real gates and the real arm decision.
            int lightingScheduled = 0;
            _scanScratch.Clear();
            foreach (KeyValuePair<Vector2Int, ChunkData> entry in Chunks()) _scanScratch.Add(ChunkCoord.FromVoxelOrigin(entry.Key));
            _scanScratch.Sort(CompareCoords); // Deterministic visit order; production's is a HashSet's.

            foreach (ChunkCoord coord in _scanScratch)
            {
                ChunkData chunk = Resolve(coord);
                if (chunk == null || !chunk.IsPopulated) continue;
                if (!chunk.NeedsInitialLighting && !chunk.NeedsEdgeCheck && !chunk.HasLightChangesToProcess) continue;

                LightingScanDecision.ScanAction action = LightingScanDecision.EvaluateReadyChunk(
                    jobInFlight: _fixture.Jobs.LightingJobs.ContainsKey(coord),
                    needsInitialLighting: chunk.NeedsInitialLighting,
                    needsEdgeCheck: chunk.NeedsEdgeCheck,
                    hasLightChanges: chunk.HasLightChangesToProcess,
                    neighborsDataReady: _fixture.World.AreNeighborsDataReady(coord),
                    neighborsReadyAndLit: _fixture.World.AreNeighborsReadyAndLit(coord));

                if (action == LightingScanDecision.ScanAction.Park)
                {
                    result.LightingParked++;
                    continue;
                }
                if (action == LightingScanDecision.ScanAction.Remove) continue;
                if (lightingScheduled >= LightingSchedulesPerFrame) continue; // Budget break: stays ready.

                // A successful schedule clears every lighting flag (the caller contract on EvaluateReadyChunk).
                chunk.NeedsInitialLighting = false;
                chunk.NeedsEdgeCheck = false;
                chunk.HasLightChangesToProcess = false;
                _fixture.Jobs.LightingJobs[coord] = default;
                _lightingFlights.Add(new Flight { Coord = coord, CompletesOnFrame = _frame + JobLatencyFrames });
                lightingScheduled++;
            }
            result.LightingScheduled = lightingScheduled;

            // Step 6 — ProcessMeshJobs.
            CollectCompleted(_meshFlights);
            foreach (Flight flight in _completedScratch)
            {
                _fixture.Jobs.MeshJobs.Remove(flight.Coord);
                if (Resolve(flight.Coord) != null) _meshed.Add(flight.Coord);
            }

            // Step 7 — schedule new mesh jobs from the queue, through the real decision.
            int meshScheduled = 0;
            for (int i = 0; i < _meshQueue.Count && meshScheduled < MeshSchedulesPerFrame; i++)
            {
                ChunkCoord coord = _meshQueue[i];
                ChunkData chunk = Resolve(coord);
                if (chunk == null)
                {
                    _meshQueue.RemoveAt(i--);
                    continue;
                }

                MeshingScheduleDecision.Result decision = MeshingScheduleDecision.Evaluate(
                    jobInFlight: _fixture.Jobs.MeshJobs.ContainsKey(coord),
                    lightingEnabled: _fixture.World.settings.enableLighting,
                    centerHasLightWork: chunk.HasLightChangesToProcess,
                    centerNeedsInitialLighting: chunk.NeedsInitialLighting,
                    neighborsMeshReady: _fixture.World.AreNeighborsMeshReady(coord));

                if (!MeshingScheduleDecision.DequeuesChunk(decision))
                {
                    result.MeshDeclined++;
                    continue; // MP-3: left queued.
                }

                _meshQueue.RemoveAt(i--);
                _fixture.Jobs.MeshJobs[coord] = default;
                _meshFlights.Add(new Flight { Coord = coord, CompletesOnFrame = _frame + JobLatencyFrames });
                meshScheduled++;
            }
            result.MeshScheduled = meshScheduled;

            RunUnloadPass(ref result);

            _frame++;
            return result;
        }

        /// <summary>Runs frames until every requested chunk is meshed, or the budget is spent.</summary>
        /// <param name="maxFrames">Frame ceiling — exhausting it is the deterministic form of a deadlock.</param>
        /// <param name="targets">The chunks that must all reach the meshed state.</param>
        /// <param name="totals">The summed per-frame counters, for non-vacuity assertions.</param>
        /// <returns>True when every target meshed within the budget.</returns>
        public bool RunUntilConverged(int maxFrames, IReadOnlyList<ChunkCoord> targets, out FrameResult totals)
        {
            totals = default;
            for (int i = 0; i < maxFrames; i++)
            {
                FrameResult frame = RunFrame();
                totals.GenerationCompleted += frame.GenerationCompleted;
                totals.LightingScheduled += frame.LightingScheduled;
                totals.LightingParked += frame.LightingParked;
                totals.MeshScheduled += frame.MeshScheduled;
                totals.MeshDeclined += frame.MeshDeclined;
                totals.Unloaded += frame.Unloaded;
                totals.UnloadDeferredStrand += frame.UnloadDeferredStrand;

                if (AllMeshed(targets)) return true;
            }
            return AllMeshed(targets);
        }

        /// <summary>Runs a fixed number of frames, returning the summed counters.</summary>
        /// <param name="count">How many frames to run.</param>
        /// <returns>The summed per-frame counters.</returns>
        public FrameResult RunFrames(int count)
        {
            FrameResult totals = default;
            for (int i = 0; i < count; i++)
            {
                FrameResult frame = RunFrame();
                totals.GenerationCompleted += frame.GenerationCompleted;
                totals.LightingScheduled += frame.LightingScheduled;
                totals.LightingParked += frame.LightingParked;
                totals.MeshScheduled += frame.MeshScheduled;
                totals.MeshDeclined += frame.MeshDeclined;
                totals.Unloaded += frame.Unloaded;
                totals.UnloadDeferredStrand += frame.UnloadDeferredStrand;
            }
            return totals;
        }

        /// <summary>True when every target chunk has had a mesh applied.</summary>
        /// <param name="targets">The chunks to test.</param>
        /// <returns>True when all are meshed.</returns>
        public bool AllMeshed(IReadOnlyList<ChunkCoord> targets)
        {
            foreach (ChunkCoord target in targets)
                if (!_meshed.Contains(target)) return false;

            return true;
        }

        private void RunUnloadPass(ref FrameResult result)
        {
            if (_outOfRange.Count == 0) return;

            _scanScratch.Clear();
            foreach (ChunkCoord coord in _outOfRange) _scanScratch.Add(coord);
            _scanScratch.Sort(CompareCoords);

            foreach (ChunkCoord coord in _scanScratch)
            {
                ChunkData chunk = Resolve(coord);
                if (chunk == null) continue;

                bool jobRunning = _fixture.Jobs.GenerationJobs.ContainsKey(coord)
                                  || _fixture.Jobs.MeshJobs.ContainsKey(coord)
                                  || _fixture.Jobs.LightingJobs.ContainsKey(coord);

                ChunkUnloadDecision.ChunkUnloadFacts facts = new ChunkUnloadDecision.ChunkUnloadFacts(
                    beyondUnloadDistance: true,
                    jobRunning: jobRunning,
                    processingLight: chunk.IsAwaitingMainThreadProcess || chunk.HasLightChangesToProcess,
                    wouldStrandInRangeNeighbor: UnloadFacts == UnloadFactGathering.WithStrandGuard
                                                && WouldStrandInRangeNeighbor(coord));

                switch (ChunkUnloadDecision.Evaluate(facts))
                {
                    case ChunkUnloadDecision.Result.DeferWouldStrand:
                        result.UnloadDeferredStrand++;
                        break;
                    case ChunkUnloadDecision.Result.Unload:
                    case ChunkUnloadDecision.Result.UnloadPersistLightPending:
                        _fixture.RemoveChunk(coord.X, coord.Z);
                        _outOfRange.Remove(coord);
                        _meshed.Remove(coord);
                        result.Unloaded++;
                        break;
                }
            }
        }

        /// <summary>
        /// Production's §9.6 strand fact: a populated neighbor that is itself <b>in range</b> and still needs
        /// this chunk's data for lighting. Out-of-range neighbors are excluded (P-4 rec 3).
        /// </summary>
        private bool WouldStrandInRangeNeighbor(ChunkCoord coord)
        {
            foreach (Vector3Int offset in VoxelData.AllNeighborOffsets)
            {
                ChunkCoord neighborCoord = coord.Neighbor(offset.x, offset.z);
                if (_outOfRange.Contains(neighborCoord)) continue;

                ChunkData neighbor = Resolve(neighborCoord);
                if (neighbor is not { IsPopulated: true }) continue;
                if (neighbor.HasLightChangesToProcess || neighbor.NeedsInitialLighting) return true;
            }
            return false;
        }

        private void EmitCrossChunkMods(ChunkCoord source)
        {
            foreach (int faceIndex in VoxelData.HorizontalFaceChecksIndices)
            {
                Vector3Int offset = VoxelData.FaceChecks[faceIndex];
                ChunkData neighbor = Resolve(source.Neighbor(offset.x, offset.z));
                if (neighbor is { IsPopulated: true }) neighbor.HasLightChangesToProcess = true;
            }
        }

        private void CollectCompleted(List<Flight> flights)
        {
            _completedScratch.Clear();
            for (int i = flights.Count - 1; i >= 0; i--)
            {
                if (flights[i].CompletesOnFrame > _frame) continue;
                _completedScratch.Add(flights[i]);
                flights.RemoveAt(i);
            }
        }

        private ChunkData Resolve(ChunkCoord coord) =>
            _fixture.World.worldData.TryGetChunk(coord.ToVoxelOrigin(), out ChunkData chunk) ? chunk : null;

        private IEnumerable<KeyValuePair<Vector2Int, ChunkData>> Chunks() => _fixture.World.worldData.Chunks;

        private static int CompareCoords(ChunkCoord a, ChunkCoord b) =>
            a.X != b.X ? a.X.CompareTo(b.X) : a.Z.CompareTo(b.Z);
    }
}
