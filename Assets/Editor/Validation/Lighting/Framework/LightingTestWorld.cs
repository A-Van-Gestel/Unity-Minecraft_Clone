using System;
using System.Collections.Generic;
using Data;
using Helpers;
using Jobs;
using Serialization;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Pool;

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

        // The real production pending-light store, run in its disk-free in-memory mode. Cross-chunk mods
        // targeting an in-world-but-unloaded chunk are persisted here (production's PersistUndeliverable
        // route) and replayed when the chunk is marked loaded again — exercising the genuine persist/
        // replay logic the fixed grid otherwise can't reach (LIGHTING_VALIDATION_HARNESS_FIDELITY.md, B1).
        // Typed as IPendingLightStore so Save()/Load() (disk I/O) are unreachable from the harness.
        private readonly IPendingLightStore _pendingStore = LightingStateManager.CreateInMemory();

        // The harness gates pipeline work on the REAL production flag ChunkData.HasLightChangesToProcess
        // (see TestChunk.HasLightWork), whose setter fires the static ChunkData.OnLightWorkFlagged. No
        // live World subscribes in the editor suite, but a stale subscriber from a prior play session
        // (no domain reload) could throw — so neutralize it for the lifetime of the world and restore on
        // Dispose. (Setting a flag to false never fires the callback; only true does.)
        private readonly Action<Vector2Int> _savedLightWorkCallback;

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

            _savedLightWorkCallback = ChunkData.OnLightWorkFlagged;
            ChunkData.OnLightWorkFlagged = null;

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

            // Release any pending mods still held by the in-memory store (it pools its inner
            // HashSets/Dictionaries) so a finished test never leaks them back into the editor pools.
            _pendingStore.Clear();

            ChunkData.OnLightWorkFlagged = _savedLightWorkCallback;
        }

        // --- Per-chunk state ---

        /// <summary>
        /// Harness-side stand-in for the lighting-relevant state of <c>ChunkData</c>: flattened voxel and
        /// light buffers, the per-chunk heightmap, and the main-thread BFS wake-up queues. The
        /// "has pending light work" gate (<see cref="HasLightWork"/>) is backed by the REAL
        /// <c>ChunkData.HasLightChangesToProcess</c> flag, not a separate mirror.
        /// </summary>
        private sealed class TestChunk
        {
            public readonly Vector2Int Coord;
            public readonly Vector2Int VoxelOrigin;

            /// <summary>
            /// Real production storage: voxels and ushort light held in <see cref="ChunkSection"/>s with
            /// uniform-sky compaction. The harness reads, writes, snapshots, and merges light through the
            /// same <see cref="ChunkData"/> code production uses (<c>GetLightData</c>/<c>SetLightData</c>,
            /// <c>FillJobLightMap</c>, <c>ApplyJobLightMap</c>) — closing the section-merge fidelity gap
            /// (see Documentation/Architecture/LIGHTING_VALIDATION_HARNESS_FIDELITY.md, finding A1).
            /// </summary>
            public readonly ChunkData Data;

            public readonly Queue<LightQueueNode> SunQueue = new Queue<LightQueueNode>();
            public readonly Queue<LightQueueNode> BlockQueue = new Queue<LightQueueNode>();
            public readonly Queue<Vector2Int> SunColumnRecalcQueue = new Queue<Vector2Int>();

            /// <summary>
            /// Pending-light-work gate, backed by the REAL production flag
            /// <see cref="ChunkData.HasLightChangesToProcess"/> rather than a separate harness mirror — so
            /// the set/clear-site pairing runs against production state and a missed reset of it on pool
            /// recycle is observable (see <c>RecycleChunkData</c> and B33/B34, finding B4). The setter
            /// fires <c>ChunkData.OnLightWorkFlagged</c> (neutralized for the harness's lifetime; only a
            /// `true` write fires it).
            /// </summary>
            public bool HasLightWork
            {
                get => Data.HasLightChangesToProcess;
                set => Data.HasLightChangesToProcess = value;
            }

            /// <summary>
            /// Whether this chunk is currently loaded/populated. Mirror of production's
            /// "a non-null, populated RequestChunk result". Set false via
            /// <see cref="MarkChunkUnloaded"/> to model an in-world-but-unloaded chunk: cross-chunk mods
            /// toward it are then persisted (production's PersistUndeliverable route) rather than applied,
            /// and a job completing for it discards its result and degrades inbound deferred mods.
            /// Cleared back to true by <see cref="MarkChunkLoaded"/>.
            /// </summary>
            public bool IsLoaded = true;

            /// <summary>
            /// Whether THIS chunk's neighbors all have populated terrain data — the harness analog of
            /// production's <c>World.AreNeighborsDataReady</c>. When false, a scheduling attempt for this
            /// chunk resolves to <see cref="LightingScheduleDecision.Result.NeighborsNotReady"/>: the chunk
            /// keeps its pending light work (<see cref="HasLightWork"/> stays set) and is NOT scheduled,
            /// to be retried on a later frame once neighbors load — mirroring
            /// <c>WorldJobManager.ScheduleLightingUpdate</c>'s deferral arm. Distinct from
            /// <see cref="IsLoaded"/>: that models the chunk being absent for inbound mod delivery, this
            /// models the chunk's own re-lighting being blocked on still-generating neighbor terrain.
            /// Toggled via <see cref="MarkNeighborsNotReady"/> / <see cref="MarkNeighborsReady"/>
            /// (default true). Closes finding B2 in
            /// Documentation/Architecture/LIGHTING_VALIDATION_HARNESS_FIDELITY.md.
            /// </summary>
            public bool NeighborsReady = true;

            public TestChunk(Vector2Int coord)
            {
                Coord = coord;
                VoxelOrigin = coord * VoxelData.ChunkWidth;
                Data = new ChunkData(VoxelOrigin);
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

            /// <summary>The chunk coordinate this flight targets. Read-only accessor for test predicates.</summary>
            public Vector2Int ChunkCoord => Coord;
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

            /// <summary>Mods persisted to the pending-light store because their target chunk is in-world
            /// but currently unloaded (production's PersistUndeliverable route). Replayed when the target
            /// is marked loaded again.</summary>
            public int ModsPersisted;

            /// <summary>Mods that had been deferred FOR this chunk but were degraded to the pending-light
            /// store because this chunk's own job completed while it was unloaded — its result (and thus
            /// the drain that would have consumed them) was discarded (production's
            /// DegradeDeferredCrossChunkMods).</summary>
            public int ModsDegraded;
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
        /// Returns true when the given chunk's neighbor terrain data is ready, so a lighting job for it
        /// may be scheduled. Harness analog of production's <c>World.AreNeighborsDataReady</c>, consumed by
        /// <c>LightingFrameSimulator.RunFrame</c> as the second argument to
        /// <see cref="LightingScheduleDecision.Evaluate"/>. Defaults to true; set false via
        /// <see cref="MarkNeighborsNotReady"/> to exercise the <c>NeighborsNotReady</c> deferral arm.
        /// </summary>
        /// <param name="chunkCoord">The grid coordinate of the chunk being considered for scheduling.</param>
        public bool AreNeighborsDataReady(Vector2Int chunkCoord) => GetChunk(chunkCoord).NeighborsReady;

        /// <summary>
        /// Marks a chunk's neighbor terrain data as NOT ready — production's <c>AreNeighborsDataReady</c>
        /// returning false because a neighbor is still generating. While not ready, the simulator defers
        /// this chunk's lighting (the <c>NeighborsNotReady</c> arm): it keeps its pending light work and is
        /// never scheduled. Reverse with <see cref="MarkNeighborsReady"/>.
        /// </summary>
        /// <param name="chunkCoord">The grid coordinate of the chunk whose neighbors are not ready.</param>
        public void MarkNeighborsNotReady(Vector2Int chunkCoord) => GetChunk(chunkCoord).NeighborsReady = false;

        /// <summary>
        /// Marks a chunk's neighbor terrain data as ready again (clear site for
        /// <see cref="MarkNeighborsNotReady"/>) so the simulator may schedule its retained light work on
        /// the next frame.
        /// </summary>
        /// <param name="chunkCoord">The grid coordinate of the chunk whose neighbors are now ready.</param>
        public void MarkNeighborsReady(Vector2Int chunkCoord) => GetChunk(chunkCoord).NeighborsReady = true;

        /// <summary>
        /// How a chunk's persisted pending light work is treated when it is marked loaded again, mirroring
        /// the two distinct production load paths.
        /// </summary>
        public enum ChunkLoadMode
        {
            /// <summary>Loaded from disk (the chunk kept its saved light): persisted sunlight column
            /// recalcs AND pending cross-chunk blocklight mods are replayed into the live chunk
            /// (mirror of <c>World.LoadOrGenerateChunk</c>'s restore + replay).</summary>
            LoadFromDisk,

            /// <summary>Freshly generated (light recomputed from scratch): persisted sunlight column
            /// recalcs are replayed, but pending blocklight mods are DISCARDED — initial lighting
            /// recomputes all blocklight, so mods recorded while the chunk was absent are obsolete
            /// (mirror of <c>WorldJobManager</c>'s generated-chunk <c>DiscardPendingBlocklight</c>).</summary>
            FreshlyGenerated,
        }

        /// <summary>
        /// Marks a chunk as in-world but unloaded/unpopulated — production's "RequestChunk returned null
        /// or an unpopulated chunk". While unloaded: cross-chunk mods toward it are persisted to the
        /// pending store instead of applied (the PersistUndeliverable route), and if it has a lighting
        /// job in flight, completing that job discards the result and degrades inbound deferred mods.
        /// Reverse with <see cref="MarkChunkLoaded"/>.
        /// </summary>
        /// <param name="chunkCoord">The grid coordinate of the chunk to unload.</param>
        public void MarkChunkUnloaded(Vector2Int chunkCoord) => GetChunk(chunkCoord).IsLoaded = false;

        /// <summary>
        /// Marks a previously-unloaded chunk as loaded again (clear site for the unload flag set by
        /// <see cref="MarkChunkUnloaded"/>) and replays the pending light work the store accumulated while
        /// it was unloaded, mirroring production's replay-on-load. The seeded BFS wake-up nodes and
        /// sunlight column recalcs take effect on the next lighting pass the test runs for this chunk.
        /// </summary>
        /// <param name="chunkCoord">The grid coordinate of the chunk to mark loaded.</param>
        /// <param name="mode">Whether the chunk is loaded from disk (replay blocklight) or freshly
        /// generated (discard blocklight). Defaults to <see cref="ChunkLoadMode.LoadFromDisk"/>.</param>
        public void MarkChunkLoaded(Vector2Int chunkCoord, ChunkLoadMode mode = ChunkLoadMode.LoadFromDisk)
        {
            TestChunk chunk = GetChunk(chunkCoord);
            chunk.IsLoaded = true;
            ReplayPendingOnLoad(chunkCoord, chunk, mode);
        }

        /// <summary>
        /// Recycles every chunk's <see cref="ChunkData"/> through the REAL production <c>Reset()</c> — the
        /// same call <c>ChunkPoolManager.ReturnChunkData</c> (Reset to zero) and <c>GetChunkData</c> (Reset
        /// to the live position) make on pool return/acquire. Models a full pool recycle: all transient
        /// state (light, heightmap, sections, the lighting flags, and the <see cref="ChunkData.RemainingEdgeCheckRounds"/>
        /// counter) must be cleared/restored, or the next lifecycle inherits stale state. After this call
        /// the chunks are blank (all-air, unlit) — re-author terrain and re-light as for a fresh world.
        /// A defect where <c>Reset()</c> fails to clear a flag/counter surfaces as a corrupted re-light
        /// (this scenario) or a stale field (the reflection invariant <c>AssertResetClearsTransientState</c>).
        /// See LIGHTING_VALIDATION_HARNESS_FIDELITY.md, finding B4.
        /// </summary>
        public void RecycleAllChunks()
        {
            foreach (TestChunk chunk in _chunks.Values)
            {
                // Mirror production's return-then-acquire double reset (ReturnChunkData resets to zero,
                // GetChunkData resets to the new position). World.Instance is null in the editor, so
                // Reset() takes its Array.Clear(sections) fallback rather than pooling sections.
                chunk.Data.Reset(Vector2Int.zero);
                chunk.Data.Reset(chunk.VoxelOrigin);

                // The harness's managed BFS wake-up queues mirror ChunkData's; clear them too so a
                // recycled TestChunk carries no stale wake-ups (they are normally drained on schedule,
                // so this only matters if a test recycles mid-lifecycle).
                chunk.SunQueue.Clear();
                chunk.BlockQueue.Clear();
                chunk.SunColumnRecalcQueue.Clear();
            }
        }

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

            // Edge check runs when explicitly requested OR when the chunk's REAL NeedsEdgeCheck flag is
            // set, then the flag is cleared on schedule (mirror of production reading + clearing
            // ChunkData.NeedsEdgeCheck in ScheduleLightingUpdate). Driving off the real flag means a
            // missed reset of it on pool recycle would alter scheduling — observable (B4).
            bool edgeCheck = performEdgeCheck || chunk.Data.NeedsEdgeCheck;
            chunk.Data.NeedsEdgeCheck = false;

            LightingJobFlight flight = new LightingJobFlight { Coord = chunkCoord };

            // Center chunk: writable copies (the job owns them for the duration of the flight).
            // Reconstructed from the section store via the SAME production fill code the live pipeline
            // uses (ChunkData.FillJob*Map) — every element is written, so UninitializedMemory is safe.
            NativeArray<uint> map = NewOwned(flight, new NativeArray<uint>(ChunkBufferLength, Allocator.Persistent, NativeArrayOptions.UninitializedMemory));
            chunk.Data.FillJobVoxelMap(map);
            NativeArray<ushort> lightMap = NewOwned(flight, new NativeArray<ushort>(ChunkBufferLength, Allocator.Persistent, NativeArrayOptions.UninitializedMemory));
            chunk.Data.FillJobLightMap(lightMap);
            NativeArray<ushort> heightmap = NewOwned(flight, new NativeArray<ushort>(chunk.Data.heightMap, Allocator.Persistent));

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
                PerformEdgeCheck = edgeCheck,
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

            // Mirror production's main-thread-processing guard (WorldJobManager.ProcessLightingJobs sets
            // this true before applying the result and clears it after). Set/clear pairing on the real
            // flag so its reset-completeness on recycle is verified (B4); cleared before returning below.
            chunk.Data.IsAwaitingMainThreadProcess = true;

            LightingRunResult result = new LightingRunResult
            {
                JobReportedStable = flight.IsStable[0],
                ModsEmitted = flight.Mods.Length,
            };

            bool hasRealCrossChunkMods = false;

            // Mirror of WorldJobManager.ProcessLightingJobs' "chunkData != null && IsPopulated" gate:
            // a job whose chunk is still loaded merges + routes its mods; a job whose chunk was unloaded
            // mid-flight discards its result and degrades inbound deferred mods (the else branch).
            if (chunk.IsLoaded)
            {
                // Merge through the SAME production per-section + uniform-sky compaction code the live
                // pipeline runs (ChunkData.ApplyJobLightMap), so section/compaction defects are visible to
                // the suite instead of being bypassed by a flat-array copy. Block types are null: the
                // harness does not mesh, and section counts are meshing-only (light-irrelevant).
                // Mods applied to live data during the flight would be overwritten here — which is why mods
                // targeting in-flight chunks are DEFERRED and drained right after this merge (Bug 08 path-2).
                chunk.Data.ApplyJobLightMap(job.Map, job.LightMap, null);

                // Drain mods deferred for this chunk while its job was in flight
                // (mirror of WorldJobManager.DrainDeferredCrossChunkMods).
                if (_deferredMods.Remove(flight.Coord, out List<LightModification> deferred))
                {
                    foreach (LightModification mod in deferred)
                        ApplyModToChunk(chunk, in mod);
                }

                // Apply cross-chunk mods through the SAME shared routing decision as
                // WorldJobManager.ProcessLightingJobs, so the harness and production can never disagree
                // on drop/persist/defer/apply or the stability override.
                foreach (LightModification mod in flight.Mods)
                {
                    Vector2Int targetCoord = WorldToChunkCoord(new Vector2Int(mod.GlobalPosition.x, mod.GlobalPosition.z));
                    bool targetInWorld = _chunks.TryGetValue(targetCoord, out TestChunk target);

                    // targetLoaded now reflects real per-chunk load state (set via MarkChunkUnloaded),
                    // so the PersistUndeliverable route — target in-world but unloaded — is reachable.
                    LightingJobProcessor.CrossChunkModRoute route = LightingJobProcessor.RouteCrossChunkMod(
                        targetInWorld,
                        targetLoaded: targetInWorld && target.IsLoaded,
                        targetJobInFlightThisPass: targetInWorld && _inFlightCoords.Contains(targetCoord));

                    hasRealCrossChunkMods |= LightingJobProcessor.CountsAsRealCrossChunkMod(route);

                    switch (route)
                    {
                        case LightingJobProcessor.CrossChunkModRoute.DropOutOfWorld:
                            // Target outside the grid (out-of-world): dropped without affecting stability.
                            result.ModsDroppedOutOfWorld++;
                            continue;

                        case LightingJobProcessor.CrossChunkModRoute.PersistUndeliverable:
                            // Target in-world but unloaded: persist for replay on load (mirror of
                            // WorldJobManager.PersistUndeliverableLightMod), the Bug 08 path-1 store.
                            PersistMod(targetCoord, in mod);
                            result.ModsPersisted++;
                            continue;

                        case LightingJobProcessor.CrossChunkModRoute.Defer:
                            // Applying now would be overwritten by the target's merge — defer; drained
                            // right after the target's merge (the Bug 08 path-2 defer/drain).
                            if (!_deferredMods.TryGetValue(targetCoord, out List<LightModification> deferredList))
                            {
                                deferredList = new List<LightModification>();
                                _deferredMods[targetCoord] = deferredList;
                            }

                            deferredList.Add(mod);
                            result.ModsDeferred++;
                            continue;

                        case LightingJobProcessor.CrossChunkModRoute.ApplyDirect:
                            if (ApplyModToChunk(target, in mod))
                                result.ModsApplied++;
                            continue;
                    }
                }
            }
            else
            {
                // The emitting chunk was unloaded mid-flight: its job result is discarded (no merge) and
                // its own emitted mods are dropped. Mods OTHER chunks deferred FOR it can never be drained
                // now, so degrade them to the pending store (mirror of WorldJobManager's else branch +
                // DegradeDeferredCrossChunkMods). hasRealCrossChunkMods stays false -> effectively stable.
                result.ModsDegraded += DegradeDeferredMods(flight.Coord);
            }

            // Production stability override: not-stable solely due to out-of-world mods counts as stable.
            result.IsStable = LightingJobProcessor.IsEffectivelyStable(result.JobReportedStable, hasRealCrossChunkMods);

            if (!result.IsStable)
                chunk.HasLightWork = true;

            // Main-thread processing complete (mirror of production clearing IsAwaitingMainThreadProcess
            // at the end of the per-job pass).
            chunk.Data.IsAwaitingMainThreadProcess = false;

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
        /// converged, and then the edge-check rounds (driven by each chunk's real
        /// <c>ChunkData.RemainingEdgeCheckRounds</c>) reconcile borders, each followed by convergence.
        /// </summary>
        /// <param name="maxRounds">The round budget for each convergence stage.</param>
        /// <returns>The total number of convergence rounds taken, or -1 if any stage failed to converge.</returns>
        public int RunInitialLighting(int maxRounds = DefaultMaxRounds)
        {
            foreach (TestChunk chunk in _chunks.Values)
                QueueFullSunlightRecalc(chunk.Coord);

            int totalRounds = RunToConvergence(maxRounds);
            if (totalRounds < 0) return -1;

            // Edge-check rounds, driven by each chunk's REAL RemainingEdgeCheckRounds counter (production
            // decrements it per stabilized round). Consuming the real field — not a fixed local count —
            // makes a missed Reset() of it on pool recycle observable (B4): a recycled chunk that kept a
            // stale 0 would silently run zero edge-check rounds and fail to reconcile its borders.
            while (DecrementEdgeCheckRound())
            {
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
        /// the edge-check rounds (driven by each chunk's real <c>ChunkData.RemainingEdgeCheckRounds</c>)
        /// also run as waves — the faithful mirror of initial world generation, where neighboring chunks
        /// light simultaneously from stale snapshots (the convergence condition behind Bug 05).
        /// </summary>
        /// <param name="maxRounds">The round budget for each convergence stage.</param>
        /// <returns>The total number of waves taken, or -1 if any stage failed to converge.</returns>
        public int RunInitialLightingParallel(int maxRounds = DefaultMaxRounds)
        {
            foreach (TestChunk chunk in _chunks.Values)
                QueueFullSunlightRecalc(chunk.Coord);

            int totalRounds = RunWaveToConvergence(maxRounds);
            if (totalRounds < 0) return -1;

            // Edge-check rounds driven by the real RemainingEdgeCheckRounds counter (see RunInitialLighting);
            // here each round's flagged chunks run as one concurrent wave via RunWaveToConvergence.
            while (DecrementEdgeCheckRound())
            {
                int rounds = RunWaveToConvergence(maxRounds);
                if (rounds < 0) return -1;
                totalRounds += rounds;
            }

            return totalRounds;
        }

        /// <summary>
        /// Diagnostic variant of <see cref="RunInitialLightingParallel"/> that runs a FIXED number of
        /// wave-parallel edge-check rounds instead of the production-faithful count driven by each chunk's
        /// real <see cref="ChunkData.RemainingEdgeCheckRounds"/> (= 2). Used by the Bug-05 dense-canopy
        /// fuzz to distinguish a round-budget shortfall — the field DOES converge to the oracle given
        /// enough rounds, which is exactly the Bug-05 mechanism (cardinal-only <c>CheckEdges</c> needing
        /// more than 2 hops for a multi-chunk diagonal pocket) — from a genuinely unreachable pocket (dark
        /// in the oracle too, i.e. not Bug 05). This is NOT a production code path: production caps edge
        /// checks at <c>RemainingEdgeCheckRounds = 2</c>.
        /// </summary>
        /// <param name="forcedEdgeRounds">The exact number of edge-check rounds to run after the initial wave.</param>
        /// <param name="maxRounds">The convergence round budget for each wave stage.</param>
        /// <returns>The total number of convergence waves taken, or -1 if any stage failed to converge.</returns>
        public int RunInitialLightingParallelForcedEdgeRounds(int forcedEdgeRounds, int maxRounds = DefaultMaxRounds)
        {
            foreach (TestChunk chunk in _chunks.Values)
                QueueFullSunlightRecalc(chunk.Coord);

            int totalRounds = RunWaveToConvergence(maxRounds);
            if (totalRounds < 0) return -1;

            // Force exactly forcedEdgeRounds edge-check waves, bypassing the real RemainingEdgeCheckRounds
            // cap. Flagging NeedsEdgeCheck + HasLightWork makes BeginLightingJob run the border CheckEdges
            // pass (it consumes NeedsEdgeCheck into the job's PerformEdgeCheck), same as a real edge round.
            for (int r = 0; r < forcedEdgeRounds; r++)
            {
                foreach (TestChunk chunk in _chunks.Values)
                {
                    chunk.Data.NeedsEdgeCheck = true;
                    chunk.HasLightWork = true;
                }

                int rounds = RunWaveToConvergence(maxRounds);
                if (rounds < 0) return -1;
                totalRounds += rounds;
            }

            return totalRounds;
        }

        /// <summary>
        /// One edge-check round step shared by <see cref="RunInitialLighting"/> and
        /// <see cref="RunInitialLightingParallel"/>: for every chunk that still has edge-check rounds left
        /// on its real <see cref="ChunkData.RemainingEdgeCheckRounds"/> counter, decrement it, flag
        /// <see cref="ChunkData.NeedsEdgeCheck"/> (consumed by <see cref="BeginLightingJob"/>), and mark it
        /// as having pending work. Mirrors production's per-stabilized-round decrement in
        /// <c>WorldJobManager.ProcessLightingJobs</c>.
        /// </summary>
        /// <returns>True if at least one chunk had a remaining edge-check round (a round should run);
        /// false when every chunk's counter is exhausted.</returns>
        private bool DecrementEdgeCheckRound()
        {
            bool anyRound = false;
            foreach (TestChunk chunk in _chunks.Values)
            {
                if (chunk.Data.RemainingEdgeCheckRounds <= 0) continue;

                chunk.Data.RemainingEdgeCheckRounds--;
                chunk.Data.NeedsEdgeCheck = true;
                chunk.HasLightWork = true;
                anyRound = true;
            }

            return anyRound;
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

            ushort currentLight = target.Data.GetLightData(localPos.x, localPos.y, localPos.z);

            // Mirror WorldJobManager: only sunlight removals (LightLevel == 0) consult in-chunk support.
            byte inChunkSunSupport = mod.Channel == LightChannel.Sun && mod.LightLevel == 0
                ? CrossChunkLightModApplier.InChunkSunlightSupport(target.Data, localPos)
                : (byte)0;
            CrossChunkLightModApplier.ApplyDecision decision = CrossChunkLightModApplier.Compute(currentLight, in mod, inChunkSunSupport);
            if (!decision.ShouldApply) return false;

            target.Data.SetLightData(localPos.x, localPos.y, localPos.z, decision.NewLight);

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

        /// <summary>
        /// Persists a single cross-chunk light modification whose target chunk is in-world but unloaded,
        /// into the in-memory pending store for replay on load. Harness mirror of
        /// <c>WorldJobManager.PersistUndeliverableLightMod</c>: sun mods become pending column recalcs,
        /// blocklight mods become pending RGB modifications. Shares the local-column math + in-footprint
        /// bounds guard with production via <see cref="LightingModPersister.TryComputeLocalColumn"/>.
        /// </summary>
        /// <param name="targetCoord">The grid coordinate of the (unloaded) target chunk.</param>
        /// <param name="mod">The undeliverable cross-chunk modification.</param>
        private void PersistMod(Vector2Int targetCoord, in LightModification mod)
        {
            ChunkCoord targetChunkCoord = new ChunkCoord(targetCoord.x, targetCoord.y);

            if (!LightingModPersister.TryComputeLocalColumn(targetChunkCoord, in mod, out int localX, out int localZ))
            {
                Debug.LogError($"[LightingTestWorld] PersistMod: mod at {mod.GlobalPosition.ToString()} falls outside target chunk {targetCoord.ToString()}.");
                return;
            }

            if (mod.Channel == LightChannel.Sun)
            {
                // Production batches columns via _droppedLightUpdates before AddPending; the harness adds
                // the single column directly through a pooled scratch set (AddPending copies its contents
                // into its own pooled set and takes no ownership, so the scratch is released right after).
                HashSet<Vector2Int> scratch = HashSetPool<Vector2Int>.Get();
                scratch.Add(new Vector2Int(localX, localZ));
                _pendingStore.AddPending(targetChunkCoord, scratch);
                HashSetPool<Vector2Int>.Release(scratch);
            }
            else
            {
                // A sunlight column recalc cannot restore RGB data — persist the actual blocklight
                // modification for replay when the chunk is loaded (Bug 08, path 1).
                _pendingStore.AddPendingBlocklight(targetChunkCoord,
                    new Vector3Int(localX, mod.GlobalPosition.y, localZ),
                    mod.BlockR, mod.BlockG, mod.BlockB, mod.IsRemoval);
            }
        }

        /// <summary>
        /// Degrades the cross-chunk mods that were deferred FOR a chunk whose own job just completed while
        /// it was unloaded: that chunk's merge (and the drain that would have consumed them) was discarded,
        /// so the mods are persisted to the pending store instead. Harness mirror of
        /// <c>WorldJobManager.DegradeDeferredCrossChunkMods</c>.
        /// </summary>
        /// <param name="chunkCoord">The (now-unloaded) chunk whose inbound deferred mods are degraded.</param>
        /// <returns>The number of deferred mods degraded.</returns>
        private int DegradeDeferredMods(Vector2Int chunkCoord)
        {
            if (!_deferredMods.Remove(chunkCoord, out List<LightModification> deferred))
                return 0;

            foreach (LightModification mod in deferred)
                PersistMod(chunkCoord, in mod);

            return deferred.Count;
        }

        /// <summary>
        /// Replays the pending light work the store accumulated for a chunk while it was unloaded, when
        /// that chunk is marked loaded again. Harness mirror of production's replay-on-load
        /// (<c>World.LoadOrGenerateChunk</c> for disk loads; <c>WorldJobManager</c>'s generated-chunk path
        /// for fresh generation): drains the persisted sunlight column recalcs into the chunk's recalc
        /// queue (both modes), then either replays the pending cross-chunk blocklight mods through the
        /// shared <see cref="CrossChunkLightModApplier.ComputeBlocklight"/> decision (disk load) or
        /// discards them (fresh generation). Pooled store containers are released after draining.
        /// </summary>
        /// <param name="chunkCoord">The grid coordinate of the chunk being loaded.</param>
        /// <param name="chunk">The chunk being loaded.</param>
        /// <param name="mode">Disk-load (replay blocklight) vs. fresh-generation (discard blocklight).</param>
        private void ReplayPendingOnLoad(Vector2Int chunkCoord, TestChunk chunk, ChunkLoadMode mode)
        {
            ChunkCoord storeKey = new ChunkCoord(chunkCoord.x, chunkCoord.y);

            // Sunlight column recalcs (BOTH load modes). The store holds LOCAL columns (0-15); the harness
            // per-chunk recalc queue is also in local space (see QueueFullSunlightRecalc / BeginLightingJob),
            // so they enqueue directly. Production round-trips local->global->local only because it routes
            // through the world-level SunlightRecalculationQueue — semantically identical.
            if (_pendingStore.TryGetAndRemove(storeKey, out HashSet<Vector2Int> localCols))
            {
                foreach (Vector2Int col in localCols)
                    chunk.SunColumnRecalcQueue.Enqueue(col);

                chunk.HasLightWork = true;
                HashSetPool<Vector2Int>.Release(localCols); // TryGetAndRemove transfers ownership to us
            }

            if (mode == ChunkLoadMode.FreshlyGenerated)
            {
                // Initial lighting recomputes all blocklight from current data, so mods recorded while the
                // chunk was absent are obsolete (mirror of the generated-chunk DiscardPendingBlocklight).
                _pendingStore.DiscardPendingBlocklight(storeKey);

                // A freshly-generated chunk is recycled through ChunkData.Reset(), which restores
                // RemainingEdgeCheckRounds = 2 — so its post-generation edge-check rounds (which re-derive
                // cross-chunk spill from loaded neighbors) fire. Refresh the real counter to model that.
                chunk.Data.RemainingEdgeCheckRounds = 2;
                return;
            }

            // LoadFromDisk: replay each pending blocklight mod through the SAME shared decision logic as
            // the live cross-chunk apply path, then seed the BFS wake-up node so propagation re-runs
            // (mirror of World.LoadOrGenerateChunk's replay — Bug 08, path 1).
            if (_pendingStore.TryGetAndRemovePendingBlocklight(storeKey,
                    out Dictionary<Vector3Int, LightingStateManager.PendingBlocklightMod> pendingBlocklight))
            {
                foreach (KeyValuePair<Vector3Int, LightingStateManager.PendingBlocklightMod> entry in pendingBlocklight)
                {
                    Vector3Int localPos = entry.Key;
                    ushort currentLight = chunk.Data.GetLightData(localPos.x, localPos.y, localPos.z);

                    CrossChunkLightModApplier.ApplyDecision decision = CrossChunkLightModApplier.ComputeBlocklight(
                        currentLight, entry.Value.R, entry.Value.G, entry.Value.B, entry.Value.IsRemoval);

                    if (!decision.ShouldApply) continue;

                    chunk.Data.SetLightData(localPos.x, localPos.y, localPos.z, decision.NewLight);
                    chunk.BlockQueue.Enqueue(new LightQueueNode
                    {
                        Position = localPos, OldLightLevel = decision.OldLevel,
                        OldBlockR = decision.OldR, OldBlockG = decision.OldG, OldBlockB = decision.OldB,
                    });
                    chunk.HasLightWork = true;
                }

                DictionaryPool<Vector3Int, LightingStateManager.PendingBlocklightMod>.Release(pendingBlocklight);
            }
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
            if (!_chunks.TryGetValue(chunkCoord + new Vector2Int(dx, dz), out TestChunk neighbor))
                return NewOwned(flight, new NativeArray<uint>(0, Allocator.Persistent));

            NativeArray<uint> arr = new NativeArray<uint>(ChunkBufferLength, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            neighbor.Data.FillJobVoxelMap(arr);
            return NewOwned(flight, arr);
        }

        private NativeArray<ushort> SnapshotNeighborLight(LightingJobFlight flight, Vector2Int chunkCoord, int dx, int dz)
        {
            if (!_chunks.TryGetValue(chunkCoord + new Vector2Int(dx, dz), out TestChunk neighbor))
                return NewOwned(flight, new NativeArray<ushort>(0, Allocator.Persistent));

            NativeArray<ushort> arr = new NativeArray<ushort>(ChunkBufferLength, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            neighbor.Data.FillJobLightMap(arr);
            return NewOwned(flight, arr);
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
