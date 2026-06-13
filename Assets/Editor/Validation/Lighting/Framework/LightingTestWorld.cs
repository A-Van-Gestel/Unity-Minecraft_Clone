using System;
using System.Collections.Generic;
using Data;
using Helpers;
using Jobs;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Editor.Validation.Lighting.Framework
{
    /// <summary>
    /// Self-contained, deterministic multi-chunk lighting harness for editor validation tests.
    /// Owns an N×N grid of chunk-shaped voxel/light buffers and replays the production lighting
    /// pipeline synchronously: it runs the real <see cref="NeighborhoodLightingJob"/> per chunk and
    /// applies cross-chunk <see cref="LightModification"/>s through the shared production decision
    /// logic (<see cref="CrossChunkLightModApplier"/>), mirroring <c>WorldJobManager.ProcessLightingJobs</c>.
    /// <para>
    /// The grid boundary behaves like the world boundary: chunks outside the grid do not exist,
    /// mods targeting them are dropped (production's out-of-world skip), and the job receives
    /// uncreated neighbor arrays for them.
    /// </para>
    /// <para>
    /// <b>Race injection:</b> <see cref="BeginLightingJob"/> snapshots the job inputs without running
    /// the job, allowing a test to mutate live chunk data (e.g. by completing another chunk's job)
    /// "during the flight" before <see cref="CompleteLightingJob"/> merges the result — turning the
    /// timing-dependent in-flight races of Bug 08 into deterministic scenarios.
    /// </para>
    /// </summary>
    public sealed partial class LightingTestWorld : IDisposable
    {
        /// <summary>Number of voxels in one full chunk buffer (16 × 128 × 16).</summary>
        public const int ChunkBufferLength = VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth;

        /// <summary>
        /// Default maximum convergence rounds. Production initial lighting stabilizes a 3x3
        /// neighborhood in a handful of rounds; anything beyond this indicates ping-pong divergence.
        /// </summary>
        public const int DefaultMaxRounds = 16;

        private readonly Dictionary<Vector2Int, TestChunk> _chunks = new Dictionary<Vector2Int, TestChunk>();
        private readonly BlockTypeJobData[] _blockTypes;
        private NativeArray<BlockTypeJobData> _blockTypesNative;
        private bool _isDisposed;

        // Chunks with a lighting job currently in flight (mirror of production's LightingJobs keys,
        // minus the already-processed _completedLightJobs entries).
        private readonly HashSet<Vector2Int> _inFlightCoords = new HashSet<Vector2Int>();

        // Cross-chunk mods deferred because their target chunk had its own job in flight — applying
        // immediately would be overwritten by that job's merge. Drained right after the target's
        // merge (mirror of WorldJobManager._deferredCrossChunkMods, the Bug 08 path-2 fix).
        private readonly Dictionary<Vector2Int, List<LightModification>> _deferredMods = new Dictionary<Vector2Int, List<LightModification>>();

        /// <summary>The number of chunks along each horizontal axis of the grid.</summary>
        public int GridSize { get; }

        /// <summary>The managed block type palette used by this world (index = block ID).</summary>
        public BlockTypeJobData[] BlockTypes => _blockTypes;

        /// <summary>
        /// Initializes a test world with a square grid of empty (all-air) chunks.
        /// </summary>
        /// <param name="gridSize">Chunks per horizontal axis (3 covers a single 3×3 neighborhood; 5 enables diagonal-stale scenarios).</param>
        /// <param name="blockTypes">The block palette (defaults to <see cref="TestBlockPalette.CreateJobDataArray"/> when null).</param>
        public LightingTestWorld(int gridSize = 3, BlockTypeJobData[] blockTypes = null)
        {
            if (gridSize < 1) throw new ArgumentOutOfRangeException(nameof(gridSize));

            GridSize = gridSize;
            _blockTypes = blockTypes ?? TestBlockPalette.CreateJobDataArray();
            _blockTypesNative = new NativeArray<BlockTypeJobData>(_blockTypes, Allocator.Persistent);

            for (int cx = 0; cx < gridSize; cx++)
            {
                for (int cz = 0; cz < gridSize; cz++)
                {
                    Vector2Int coord = new Vector2Int(cx, cz);
                    _chunks[coord] = new TestChunk(coord);
                }
            }
        }

        /// <summary>Disposes the native block type array. Call when the test is finished (or use a using-statement).</summary>
        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            if (_blockTypesNative.IsCreated) _blockTypesNative.Dispose();
        }

        // --- Per-chunk state ---

        /// <summary>
        /// Harness-side stand-in for the lighting-relevant state of <c>ChunkData</c>: flattened voxel and
        /// light buffers, the per-chunk heightmap, the main-thread BFS wake-up queues, and the
        /// "has pending light work" flag (mirror of <c>HasLightChangesToProcess</c>).
        /// </summary>
        private sealed class TestChunk
        {
            public readonly Vector2Int Coord;
            public readonly Vector2Int VoxelOrigin;
            public readonly uint[] Voxels = new uint[ChunkBufferLength];
            public readonly ushort[] Light = new ushort[ChunkBufferLength];
            public readonly ushort[] HeightMap = new ushort[VoxelData.ChunkWidth * VoxelData.ChunkWidth];
            public readonly Queue<LightQueueNode> SunQueue = new Queue<LightQueueNode>();
            public readonly Queue<LightQueueNode> BlockQueue = new Queue<LightQueueNode>();
            public readonly Queue<Vector2Int> SunColumnRecalcQueue = new Queue<Vector2Int>();
            public bool HasLightWork;

            public TestChunk(Vector2Int coord)
            {
                Coord = coord;
                VoxelOrigin = coord * VoxelData.ChunkWidth;
            }
        }

        // --- Job flight (snapshot → run → merge) ---

        /// <summary>
        /// An in-flight lighting job: inputs snapshotted, BFS queues drained, job not yet run.
        /// Created by <see cref="BeginLightingJob"/>; consumed exactly once by <see cref="CompleteLightingJob"/>.
        /// </summary>
        public sealed class LightingJobFlight
        {
            internal Vector2Int Coord;
            internal NeighborhoodLightingJob Job;
            internal NativeArray<bool> IsStable;
            internal NativeList<LightModification> Mods;
            internal readonly List<IDisposable> OwnedContainers = new List<IDisposable>();
            internal bool Completed;
        }

        /// <summary>The outcome of one completed lighting job, after cross-chunk mod application.</summary>
        public struct LightingRunResult
        {
            /// <summary>Stability as reported by the Burst job (false whenever any cross-chunk mods were emitted).</summary>
            public bool JobReportedStable;

            /// <summary>Effective stability after the production override (out-of-world-only mods count as stable).</summary>
            public bool IsStable;

            /// <summary>Total cross-chunk mods the job emitted.</summary>
            public int ModsEmitted;

            /// <summary>Mods whose guard decision resulted in an actual write + wake-up node.</summary>
            public int ModsApplied;

            /// <summary>Mods dropped because they targeted chunks outside the grid (out-of-world).</summary>
            public int ModsDroppedOutOfWorld;

            /// <summary>Mods deferred because their target chunk had its own job in flight; they are
            /// applied right after that chunk's merge (the Bug 08 path-2 defer/drain).</summary>
            public int ModsDeferred;
        }

        /// <summary>
        /// Returns true when a lighting job is currently in flight for the given chunk coordinate
        /// (i.e., <see cref="BeginLightingJob"/> was called but <see cref="CompleteLightingJob"/>
        /// has not yet been called for the returned flight). Mirrors the production
        /// <c>LightingJobs.ContainsKey</c> guard in <c>WorldJobManager.ScheduleLightingUpdate</c>.
        /// </summary>
        public bool IsChunkInFlight(Vector2Int chunkCoord) => _inFlightCoords.Contains(chunkCoord);

        /// <summary>
        /// Returns true when the specified chunk has pending BFS work (managed queue nodes waiting
        /// to be drained into a lighting job). Per-chunk mirror of the <see cref="HasPendingLightWork"/>
        /// property (which checks <b>any</b> chunk in the grid).
        /// </summary>
        public bool ChunkHasLightWork(Vector2Int chunkCoord) => GetChunk(chunkCoord).HasLightWork;

        /// <summary>
        /// Snapshots the 3×3 neighborhood and drains the chunk's BFS queues into a ready-to-run job,
        /// without executing it. Mirrors the scheduling half of the production pipeline. Mutations to
        /// live chunk data made between this call and <see cref="CompleteLightingJob"/> are invisible
        /// to the job — exactly like a real in-flight background job.
        /// </summary>
        /// <param name="chunkCoord">The grid coordinate of the chunk to light.</param>
        /// <param name="performEdgeCheck">Enables the job's border consistency pass (production sets this only during initial-generation convergence rounds).</param>
        /// <returns>The flight handle to pass to <see cref="CompleteLightingJob"/>.</returns>
        public LightingJobFlight BeginLightingJob(Vector2Int chunkCoord, bool performEdgeCheck = false)
        {
            TestChunk chunk = GetChunk(chunkCoord);

            // Production never schedules a second lighting job for a chunk that already has one in
            // flight (the LightingJobs.ContainsKey guard) — a test doing so is a setup error.
            if (!_inFlightCoords.Add(chunkCoord))
                throw new InvalidOperationException($"Chunk {chunkCoord} already has a lighting job in flight.");

            LightingJobFlight flight = new LightingJobFlight { Coord = chunkCoord };

            // Center chunk: writable copies (the job owns them for the duration of the flight).
            NativeArray<uint> map = NewOwned(flight, new NativeArray<uint>(chunk.Voxels, Allocator.Persistent));
            NativeArray<ushort> lightMap = NewOwned(flight, new NativeArray<ushort>(chunk.Light, Allocator.Persistent));
            NativeArray<ushort> heightmap = NewOwned(flight, new NativeArray<ushort>(chunk.HeightMap, Allocator.Persistent));

            // Seed queues: drain the chunk's managed queues into native ones (production flushes
            // ChunkData's queues into the job the same way; the chunk-side flag clears on schedule).
            NativeQueue<LightQueueNode> sunQueue = NewOwned(flight, new NativeQueue<LightQueueNode>(Allocator.Persistent));
            while (chunk.SunQueue.Count > 0) sunQueue.Enqueue(chunk.SunQueue.Dequeue());

            NativeQueue<LightQueueNode> blockQueue = NewOwned(flight, new NativeQueue<LightQueueNode>(Allocator.Persistent));
            while (chunk.BlockQueue.Count > 0) blockQueue.Enqueue(chunk.BlockQueue.Dequeue());

            NativeQueue<Vector2Int> sunColumnQueue = NewOwned(flight, new NativeQueue<Vector2Int>(Allocator.Persistent));
            while (chunk.SunColumnRecalcQueue.Count > 0) sunColumnQueue.Enqueue(chunk.SunColumnRecalcQueue.Dequeue());

            chunk.HasLightWork = false;

            flight.IsStable = NewOwned(flight, new NativeArray<bool>(1, Allocator.Persistent));
            flight.Mods = NewOwned(flight, new NativeList<LightModification>(Allocator.Persistent));

            flight.Job = new NeighborhoodLightingJob
            {
                Map = map,
                LightMap = lightMap,
                ChunkPosition = chunk.VoxelOrigin,
                SunlightBfsQueue = sunQueue,
                BlocklightBfsQueue = blockQueue,
                SunlightColumnRecalcQueue = sunColumnQueue,
                Heightmap = heightmap,
                NeighborN = SnapshotNeighborVoxels(flight, chunkCoord, 0, 1),
                NeighborE = SnapshotNeighborVoxels(flight, chunkCoord, 1, 0),
                NeighborS = SnapshotNeighborVoxels(flight, chunkCoord, 0, -1),
                NeighborW = SnapshotNeighborVoxels(flight, chunkCoord, -1, 0),
                NeighborNE = SnapshotNeighborVoxels(flight, chunkCoord, 1, 1),
                NeighborSE = SnapshotNeighborVoxels(flight, chunkCoord, 1, -1),
                NeighborSW = SnapshotNeighborVoxels(flight, chunkCoord, -1, -1),
                NeighborNW = SnapshotNeighborVoxels(flight, chunkCoord, -1, 1),
                LightN = SnapshotNeighborLight(flight, chunkCoord, 0, 1),
                LightE = SnapshotNeighborLight(flight, chunkCoord, 1, 0),
                LightS = SnapshotNeighborLight(flight, chunkCoord, 0, -1),
                LightW = SnapshotNeighborLight(flight, chunkCoord, -1, 0),
                LightNE = SnapshotNeighborLight(flight, chunkCoord, 1, 1),
                LightSE = SnapshotNeighborLight(flight, chunkCoord, 1, -1),
                LightSW = SnapshotNeighborLight(flight, chunkCoord, -1, -1),
                LightNW = SnapshotNeighborLight(flight, chunkCoord, -1, 1),
                BlockTypes = _blockTypesNative,
                CrossChunkLightMods = flight.Mods,
                IsStable = flight.IsStable,
                PerformEdgeCheck = performEdgeCheck,
            };

            return flight;
        }

        /// <summary>
        /// Runs the in-flight job synchronously, merges its light output into the live chunk (full
        /// overwrite — mirroring <c>WorldJobManager.ApplyLightingJobResult</c>), drains mods other
        /// jobs deferred for this chunk during the flight, and applies the emitted cross-chunk mods
        /// to neighbor chunks via <see cref="CrossChunkLightModApplier"/> — deferring those whose
        /// target has its own job in flight (the Bug 08 path-2 defer/drain). Disposes all flight
        /// containers.
        /// </summary>
        /// <param name="flight">The flight created by <see cref="BeginLightingJob"/>.</param>
        /// <returns>The run result, including effective stability.</returns>
        public LightingRunResult CompleteLightingJob(LightingJobFlight flight)
        {
            if (flight.Completed) throw new InvalidOperationException("Flight already completed.");
            flight.Completed = true;

            // The chunk now counts as processed: mods emitted by jobs completed after this point
            // apply to its live data directly (production's _completedLightJobs semantics).
            _inFlightCoords.Remove(flight.Coord);

            TestChunk chunk = GetChunk(flight.Coord);
            NeighborhoodLightingJob job = flight.Job;
            job.Run();

            // Merge: overwrite the live light buffer with the job's computed values.
            // Production merges per section but the effective light result is identical.
            // Mods applied to live data during the flight would be overwritten here — which is
            // why mods targeting in-flight chunks are DEFERRED and drained right after this
            // merge instead (the Bug 08 path-2 fix).
            job.LightMap.CopyTo(chunk.Light);

            // Drain mods deferred for this chunk while its job was in flight
            // (mirror of WorldJobManager.DrainDeferredCrossChunkMods).
            if (_deferredMods.Remove(flight.Coord, out List<LightModification> deferred))
            {
                foreach (LightModification mod in deferred)
                    ApplyModToChunk(chunk, in mod);
            }

            LightingRunResult result = new LightingRunResult
            {
                JobReportedStable = flight.IsStable[0],
                ModsEmitted = flight.Mods.Length,
            };

            // Apply cross-chunk mods — mirror of WorldJobManager.ProcessLightingJobs.
            bool hasRealCrossChunkMods = false;
            foreach (LightModification mod in flight.Mods)
            {
                Vector2Int targetCoord = WorldToChunkCoord(new Vector2Int(mod.GlobalPosition.x, mod.GlobalPosition.z));

                // Mods targeting chunks outside the grid can never be consumed —
                // production skips them without affecting stability (out-of-world skip).
                if (!_chunks.TryGetValue(targetCoord, out TestChunk target))
                {
                    result.ModsDroppedOutOfWorld++;
                    continue;
                }

                hasRealCrossChunkMods = true;

                // The target has its own job in flight, snapshotted before this mod existed —
                // applying now would be overwritten by that job's merge. Defer; drained right
                // after the target's merge (mirror of the production defer in
                // WorldJobManager.ProcessLightingJobs).
                if (_inFlightCoords.Contains(targetCoord))
                {
                    if (!_deferredMods.TryGetValue(targetCoord, out List<LightModification> deferredList))
                    {
                        deferredList = new List<LightModification>();
                        _deferredMods[targetCoord] = deferredList;
                    }

                    deferredList.Add(mod);
                    result.ModsDeferred++;
                    continue;
                }

                if (ApplyModToChunk(target, in mod))
                    result.ModsApplied++;
            }

            // Production stability override: not-stable solely due to out-of-world mods counts as stable.
            result.IsStable = result.JobReportedStable || !hasRealCrossChunkMods;

            if (!result.IsStable)
                chunk.HasLightWork = true;

            foreach (IDisposable container in flight.OwnedContainers)
                container.Dispose();
            flight.OwnedContainers.Clear();

            return result;
        }

        /// <summary>
        /// Convenience wrapper: snapshot, run, and merge one chunk's lighting job synchronously.
        /// </summary>
        /// <param name="chunkCoord">The grid coordinate of the chunk to light.</param>
        /// <param name="performEdgeCheck">Enables the job's border consistency pass.</param>
        /// <returns>The run result, including effective stability.</returns>
        public LightingRunResult RunLightingJob(Vector2Int chunkCoord, bool performEdgeCheck = false)
        {
            return CompleteLightingJob(BeginLightingJob(chunkCoord, performEdgeCheck));
        }

        /// <summary>
        /// Repeatedly runs lighting jobs for every chunk with pending light work (in deterministic
        /// coordinate order) until the whole grid is quiet or <paramref name="maxRounds"/> is exceeded.
        /// One round = one pass over all currently-pending chunks. Non-convergence (the Bug 07
        /// flicker/ping-pong symptom) is reported, not thrown, so tests can assert on it.
        /// </summary>
        /// <param name="maxRounds">The round budget before giving up.</param>
        /// <returns>The number of rounds taken, or -1 when work was still pending after <paramref name="maxRounds"/>.</returns>
        public int RunToConvergence(int maxRounds = DefaultMaxRounds)
        {
            List<Vector2Int> pending = new List<Vector2Int>();

            for (int round = 1; round <= maxRounds; round++)
            {
                pending.Clear();
                foreach (KeyValuePair<Vector2Int, TestChunk> entry in _chunks)
                {
                    if (entry.Value.HasLightWork)
                        pending.Add(entry.Key);
                }

                if (pending.Count == 0)
                    return round - 1;

                // Deterministic processing order: row-major over the grid.
                pending.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));

                foreach (Vector2Int coord in pending)
                    RunLightingJob(coord);
            }

            foreach (TestChunk chunk in _chunks.Values)
            {
                if (chunk.HasLightWork)
                    return -1;
            }

            return maxRounds;
        }

        /// <summary>
        /// Runs the full initial-lighting sequence the production pipeline performs for freshly
        /// generated chunks: every chunk gets a full 256-column sunlight recalculation, the grid is
        /// converged, and then two edge-check rounds (mirror of <c>RemainingEdgeCheckRounds = 2</c>)
        /// reconcile borders, each followed by convergence.
        /// </summary>
        /// <param name="maxRounds">The round budget for each convergence stage.</param>
        /// <returns>The total number of convergence rounds taken, or -1 if any stage failed to converge.</returns>
        public int RunInitialLighting(int maxRounds = DefaultMaxRounds)
        {
            foreach (TestChunk chunk in _chunks.Values)
                QueueFullSunlightRecalc(chunk.Coord);

            int totalRounds = RunToConvergence(maxRounds);
            if (totalRounds < 0) return -1;

            const int edgeCheckRounds = 2; // ChunkData.RemainingEdgeCheckRounds initial value
            for (int round = 0; round < edgeCheckRounds; round++)
            {
                foreach (Vector2Int coord in AllChunkCoords())
                    RunLightingJob(coord, performEdgeCheck: true);

                int rounds = RunToConvergence(maxRounds);
                if (rounds < 0) return -1;
                totalRounds += rounds;
            }

            return totalRounds;
        }

        /// <summary>
        /// Wave-parallel convergence: each round BEGINS the jobs of every pending chunk first (all
        /// snapshot the same pre-round state) and only then COMPLETES them in order. This mirrors
        /// production under load, where many lighting jobs are in flight in the same frame and
        /// cross-chunk mods are applied to chunks whose own job already snapshotted — the in-flight
        /// loss window that <see cref="RunToConvergence"/> (strictly sequential) can never produce.
        /// </summary>
        /// <param name="maxRounds">The round budget before giving up.</param>
        /// <returns>The number of waves taken, or -1 when work was still pending after <paramref name="maxRounds"/>.</returns>
        public int RunWaveToConvergence(int maxRounds = DefaultMaxRounds)
        {
            List<Vector2Int> pending = new List<Vector2Int>();
            List<LightingJobFlight> flights = new List<LightingJobFlight>();

            for (int round = 1; round <= maxRounds; round++)
            {
                pending.Clear();
                foreach (KeyValuePair<Vector2Int, TestChunk> entry in _chunks)
                {
                    if (entry.Value.HasLightWork)
                        pending.Add(entry.Key);
                }

                if (pending.Count == 0)
                    return round - 1;

                pending.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));

                flights.Clear();
                foreach (Vector2Int coord in pending)
                    flights.Add(BeginLightingJob(coord));

                foreach (LightingJobFlight flight in flights)
                    CompleteLightingJob(flight);
            }

            foreach (TestChunk chunk in _chunks.Values)
            {
                if (chunk.HasLightWork)
                    return -1;
            }

            return maxRounds;
        }

        /// <summary>
        /// Wave-parallel variant of <see cref="RunInitialLighting"/>: the initial 256-column recalcs
        /// of ALL chunks run as one concurrent wave (every job sees its neighbors fully unlit), and
        /// the two edge-check rounds also run as waves — the faithful mirror of initial world
        /// generation, where neighboring chunks light simultaneously from stale snapshots
        /// (the convergence condition behind Bug 05).
        /// </summary>
        /// <param name="maxRounds">The round budget for each convergence stage.</param>
        /// <returns>The total number of waves taken, or -1 if any stage failed to converge.</returns>
        public int RunInitialLightingParallel(int maxRounds = DefaultMaxRounds)
        {
            foreach (TestChunk chunk in _chunks.Values)
                QueueFullSunlightRecalc(chunk.Coord);

            int totalRounds = RunWaveToConvergence(maxRounds);
            if (totalRounds < 0) return -1;

            const int edgeCheckRounds = 2; // ChunkData.RemainingEdgeCheckRounds initial value
            List<LightingJobFlight> flights = new List<LightingJobFlight>();

            for (int round = 0; round < edgeCheckRounds; round++)
            {
                flights.Clear();
                foreach (Vector2Int coord in AllChunkCoords())
                    flights.Add(BeginLightingJob(coord, performEdgeCheck: true));

                foreach (LightingJobFlight flight in flights)
                    CompleteLightingJob(flight);

                int rounds = RunWaveToConvergence(maxRounds);
                if (rounds < 0) return -1;
                totalRounds += rounds;
            }

            return totalRounds;
        }

        // --- Private helpers ---

        /// <summary>
        /// Applies one cross-chunk mod to a live chunk through the shared production decision logic
        /// and enqueues the BFS wake-up node (mirror of <c>WorldJobManager.ApplyCrossChunkLightMod</c>).
        /// </summary>
        /// <param name="target">The chunk the modification targets.</param>
        /// <param name="mod">The cross-chunk modification emitted by a neighbor's lighting job.</param>
        /// <returns>True when the decision resulted in an actual write + wake-up node.</returns>
        private static bool ApplyModToChunk(TestChunk target, in LightModification mod)
        {
            Vector3Int localPos = new Vector3Int(
                mod.GlobalPosition.x - target.VoxelOrigin.x,
                mod.GlobalPosition.y,
                mod.GlobalPosition.z - target.VoxelOrigin.y);

            int index = ChunkMath.GetFlattenedIndexInChunk(localPos.x, localPos.y, localPos.z);
            ushort currentLight = target.Light[index];

            CrossChunkLightModApplier.ApplyDecision decision = CrossChunkLightModApplier.Compute(currentLight, in mod);
            if (!decision.ShouldApply) return false;

            target.Light[index] = decision.NewLight;

            if (mod.Channel == LightChannel.Sun)
            {
                target.SunQueue.Enqueue(new LightQueueNode { Position = localPos, OldLightLevel = decision.OldLevel });
            }
            else
            {
                target.BlockQueue.Enqueue(new LightQueueNode
                {
                    Position = localPos, OldLightLevel = decision.OldLevel,
                    OldBlockR = decision.OldR, OldBlockG = decision.OldG, OldBlockB = decision.OldB,
                });
            }

            target.HasLightWork = true;
            return true;
        }

        private static T NewOwned<T>(LightingJobFlight flight, T container) where T : struct, IDisposable
        {
            flight.OwnedContainers.Add(container);
            return container;
        }

        private NativeArray<uint> SnapshotNeighborVoxels(LightingJobFlight flight, Vector2Int chunkCoord, int dx, int dz)
        {
            // Outside the grid = outside the world: hand the job a zero-length array — the job
            // treats Length == 0 as void space, and the scheduler requires constructed containers.
            return _chunks.TryGetValue(chunkCoord + new Vector2Int(dx, dz), out TestChunk neighbor)
                ? NewOwned(flight, new NativeArray<uint>(neighbor.Voxels, Allocator.Persistent))
                : NewOwned(flight, new NativeArray<uint>(0, Allocator.Persistent));
        }

        private NativeArray<ushort> SnapshotNeighborLight(LightingJobFlight flight, Vector2Int chunkCoord, int dx, int dz)
        {
            return _chunks.TryGetValue(chunkCoord + new Vector2Int(dx, dz), out TestChunk neighbor)
                ? NewOwned(flight, new NativeArray<ushort>(neighbor.Light, Allocator.Persistent))
                : NewOwned(flight, new NativeArray<ushort>(0, Allocator.Persistent));
        }

        private TestChunk GetChunk(Vector2Int chunkCoord)
        {
            if (!_chunks.TryGetValue(chunkCoord, out TestChunk chunk))
                throw new ArgumentOutOfRangeException(nameof(chunkCoord), $"Chunk {chunkCoord} is outside the {GridSize}x{GridSize} test grid.");
            return chunk;
        }

        private static Vector2Int WorldToChunkCoord(Vector2Int worldXZ)
        {
            return new Vector2Int(
                Mathf.FloorToInt(worldXZ.x / (float)VoxelData.ChunkWidth),
                Mathf.FloorToInt(worldXZ.y / (float)VoxelData.ChunkWidth));
        }
    }
}
