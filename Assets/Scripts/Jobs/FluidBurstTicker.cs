using System;
using Data;
using Helpers;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Jobs
{
    /// <summary>
    /// TG-4 Phase 3 — runs the <b>Tier-1 interior</b> fluid tick for a single chunk through the Burst
    /// <see cref="FluidTickJob"/>. Owns reusable native scratch (the voxel snapshot + the index/output lists) so
    /// the per-tick work allocates nothing after warm-up; <b>one instance is reused across every chunk each tick</b>
    /// (the behavior tick is serial, so the scratch can be shared). This keeps the per-chunk main-thread cost to a
    /// snapshot-fill + a bucket partition + the drain — the heavy flow compute runs in the job.
    /// <para>
    /// <b>Not wired into the tick path yet</b> (C4 does the integration: merging the job's mods with the managed
    /// border path through the canonical drain, applying the inactive drops, and the feature flag). This type is
    /// the self-contained snapshot → partition → schedule unit. It reads a <i>pre-tick</i> snapshot and never
    /// mutates the chunk; the caller drains <see cref="Mods"/> + <see cref="InactiveInterior"/> afterward.
    /// </para>
    /// </summary>
    public sealed class FluidBurstTicker : IDisposable
    {
        private NativeArray<uint> _snapshot;

        // TG-4 Phase 4b: the gather destination (halo-padded, full height) the FluidTickJob reads, plus a created
        // zero-length array passed for every neighbor on the interior-only path — the gather sentinel-fills it, so
        // border reads resolve to void exactly as the old out-of-chunk read did (Phase 4b C4 swaps in real neighbors).
        private NativeArray<uint> _paddedVoxels;

        private NativeArray<uint> _emptyNeighbor;

        // TG-4 Phase 4b halo mode: 8 owned full-chunk neighbor snapshot buffers (lazy) + the per-build selection
        // (a real filled buffer when the neighbor is loaded, else _emptyNeighbor → sentinel halo → void). Compass
        // order matches GatherPaddedFluidVoxels' (w,e,s,n,sw,nw,se,ne) parameters and s_neighborOffsets.
        private NativeArray<uint>[] _neighborBuffers;
        private NativeArray<uint>[] _jobNeighbors;
        private bool _neighborsAllocated;
        private NativeList<int> _interior;
        private NativeList<int2> _replay;
        private NativeList<VoxelMod> _mods;
        private NativeList<int> _modsPerSource;
        private NativeList<int> _inactive;
        private bool _allocated;

        // TG-4 Phase 4b Y-band: the active-fluid Y-band the FluidTickJob gathers + reads (a prefix of the full-height
        // padded volume). Set by the Prepare pass: [0, CHUNK_HEIGHT) on the interior + full-height halo paths
        // (byte-identical to the pre-band gather), or the tight [minActiveY−reach, maxActiveY+reach] on the band path.
        private int _bandMinY;
        private int _bandHeight;

        // Neighbor voxel-origin offsets in compass order (w,e,s,n,sw,nw,se,ne) — matches GatherPaddedFluidVoxels and
        // the lighting AcquireNeighborMaps compass (N=+Z, S=−Z, E=+X, W=−X).
        private static readonly Vector2Int[] s_neighborOffsets =
        {
            new Vector2Int(-ChunkMath.CHUNK_WIDTH, 0), // W
            new Vector2Int(ChunkMath.CHUNK_WIDTH, 0), // E
            new Vector2Int(0, -ChunkMath.CHUNK_WIDTH), // S
            new Vector2Int(0, ChunkMath.CHUNK_WIDTH), // N
            new Vector2Int(-ChunkMath.CHUNK_WIDTH, -ChunkMath.CHUNK_WIDTH), // SW
            new Vector2Int(-ChunkMath.CHUNK_WIDTH, ChunkMath.CHUNK_WIDTH), // NW
            new Vector2Int(ChunkMath.CHUNK_WIDTH, -ChunkMath.CHUNK_WIDTH), // SE
            new Vector2Int(ChunkMath.CHUNK_WIDTH, ChunkMath.CHUNK_WIDTH), // NE
        };

        /// <summary>Voxel mods emitted by the most recent <see cref="RunInteriorFluids"/> (valid until the next call).</summary>
        public NativeList<VoxelMod> Mods => _mods;

        /// <summary>
        /// Per-source mod run lengths from the most recent run, one entry per interior voxel in
        /// <see cref="InteriorIndices"/> order. Consume <see cref="Mods"/> in these runs (walking
        /// <see cref="ReplayOrder"/>) to replay the job's emission interleaved with the managed border path
        /// (valid until the next call).
        /// </summary>
        public NativeList<int> ModsPerSource => _modsPerSource;

        /// <summary>
        /// The bucket's full enumeration order captured during the partition, one entry per active fluid voxel:
        /// <c>x</c> = flat chunk index, <c>y</c> = 1 if the voxel is Tier-1 interior (its mods were computed by the
        /// job, replay via the <see cref="ModsPerSource"/> cursor) or 0 if it is a border voxel (replay via the
        /// managed path). Lets the caller replay the tick in a <b>single</b> ordered walk — interleaving interior
        /// and border emission exactly as the legacy single loop would — without re-enumerating or re-classifying
        /// the bucket (valid until the next call).
        /// </summary>
        public NativeList<int2> ReplayOrder => _replay;

        /// <summary>Flat indices of interior voxels that became inactive in the most recent run (valid until the next call).</summary>
        public NativeList<int> InactiveInterior => _inactive;

        /// <summary>
        /// The flat chunk indices of the interior voxels processed by the most recent run, in the same order as
        /// <see cref="ModsPerSource"/> (i.e. the order they were enumerated from the bucket). The i-th entry owns
        /// the i-th <see cref="ModsPerSource"/> run — lets a caller map the job's per-source output back to a voxel
        /// position without re-deriving the partition (valid until the next call).
        /// </summary>
        public NativeList<int> InteriorIndices => _interior;

        /// <summary>
        /// Ticks the interior fluids of <paramref name="cd"/> via <see cref="FluidTickJob"/>. Reads a pre-tick
        /// snapshot of the chunk and does NOT modify it. The bucket's enumeration order (interior/border tagged) is
        /// captured in <see cref="ReplayOrder"/> for the caller to replay in a single ordered walk; border (Tier-2)
        /// voxels carry no precomputed output and stay on the managed path.
        /// </summary>
        /// <param name="cd">The chunk whose interior fluids to tick.</param>
        /// <param name="tickCounter">The current tick salt (<c>World.TickCounter</c>) for the viscosity RNG.</param>
        /// <param name="blockTypes">The global block-type job blob (<c>World.JobDataManager.BlockTypesJobData</c>).</param>
        public void RunInteriorFluids(ChunkData cd, int tickCounter, NativeArray<BlockTypeJobData> blockTypes)
        {
            // Serial path (TG-4 Phase 3): prepare + run the job synchronously on the calling thread.
            if (PrepareInteriorJob(cd))
                BuildJob(cd, tickCounter, blockTypes).Run();
        }

        /// <summary>
        /// TG-4 Phase 4a — the parallel counterpart of <see cref="RunInteriorFluids"/>: prepares the same snapshot +
        /// partition on the calling thread, then <b>schedules</b> the interior fluid job on a worker and returns its
        /// <see cref="JobHandle"/>. The caller batches handles across chunks, completes them, then drains the outputs
        /// (<see cref="Mods"/>/<see cref="ModsPerSource"/>/<see cref="ReplayOrder"/>/<see cref="InactiveInterior"/>)
        /// in deterministic chunk order. <b>Each concurrently-scheduled chunk must use its own ticker instance</b>
        /// (the scratch is per-ticker, not shareable across in-flight jobs).
        /// </summary>
        /// <param name="cd">The chunk whose interior fluids to tick.</param>
        /// <param name="tickCounter">The current tick salt (<c>World.TickCounter</c>) for the viscosity RNG.</param>
        /// <param name="blockTypes">The global block-type job blob (<c>World.JobDataManager.BlockTypesJobData</c>).</param>
        /// <returns>The scheduled job's handle, or <c>default</c> (an already-complete handle) when the chunk has no interior fluids.</returns>
        public JobHandle ScheduleInteriorFluids(ChunkData cd, int tickCounter, NativeArray<BlockTypeJobData> blockTypes)
        {
            if (!PrepareInteriorJob(cd))
                return default;

            return BuildJob(cd, tickCounter, blockTypes).Schedule();
        }

        /// <summary>
        /// TG-4 Phase 4b — the full halo path's serial counterpart of <see cref="RunInteriorFluids"/>: ticks EVERY
        /// active fluid (interior AND border) through the job, the border voxels resolving cross-chunk reads from the
        /// gathered neighbor halo (neighbors looked up in <paramref name="worldData"/>). No managed border path; the
        /// drain (<see cref="ReplayOrder"/> all-job) emits every source's mods from the job in bucket order.
        /// </summary>
        /// <param name="cd">The chunk whose fluids to tick.</param>
        /// <param name="tickCounter">The current tick salt (<c>World.TickCounter</c>) for the viscosity RNG.</param>
        /// <param name="blockTypes">The global block-type job blob.</param>
        /// <param name="worldData">The chunk store used to resolve the 8 neighbor snapshots (missing → sentinel/void).</param>
        /// <param name="useBand">
        /// TG-4 Phase 4b Y-band: when true, the gather + reads are restricted to the tight active-fluid Y-band
        /// (<see cref="ChunkMath.FLUID_VERTICAL_REACH"/>-padded) instead of the full chunk height — byte-identical by
        /// the reach invariant, but the per-tick copy is independent of world height. Defaults false (full height).
        /// </param>
        public void RunFluids(ChunkData cd, int tickCounter, NativeArray<BlockTypeJobData> blockTypes, WorldData worldData, bool useBand = false)
        {
            if (PrepareFluidJob(cd, worldData, useBand))
                BuildJob(cd, tickCounter, blockTypes).Run();
        }

        /// <summary>
        /// TG-4 Phase 4b — the full halo path's parallel counterpart of <see cref="ScheduleInteriorFluids"/>: prepares
        /// all-fluid indices + the neighbor halo, then schedules the job. <b>Each concurrently-scheduled chunk needs
        /// its own ticker instance</b> (the snapshot + neighbor scratch is per-ticker, not shareable in flight).
        /// </summary>
        /// <param name="cd">The chunk whose fluids to tick.</param>
        /// <param name="tickCounter">The current tick salt (<c>World.TickCounter</c>) for the viscosity RNG.</param>
        /// <param name="blockTypes">The global block-type job blob.</param>
        /// <param name="worldData">The chunk store used to resolve the 8 neighbor snapshots (missing → sentinel/void).</param>
        /// <param name="useBand">TG-4 Phase 4b Y-band: restrict the gather + reads to the tight active-fluid Y-band (see <see cref="RunFluids"/>). Defaults false.</param>
        /// <returns>The scheduled job's handle, or <c>default</c> when the chunk has no active fluids.</returns>
        public JobHandle ScheduleFluids(ChunkData cd, int tickCounter, NativeArray<BlockTypeJobData> blockTypes, WorldData worldData, bool useBand = false)
        {
            if (!PrepareFluidJob(cd, worldData, useBand))
                return default;

            return BuildJob(cd, tickCounter, blockTypes).Schedule();
        }

        /// <summary>
        /// Clears the per-run outputs and runs the single partition pass over <see cref="ChunkData.ActiveFluidsBucket"/>:
        /// captures the bucket's enumeration order (interior/border tagged) in <see cref="ReplayOrder"/> and collects
        /// the interior indices for the job, then snapshots the chunk's pre-tick voxels. Shared by the serial
        /// (<see cref="RunInteriorFluids"/>) and parallel (<see cref="ScheduleInteriorFluids"/>) paths.
        /// </summary>
        /// <param name="cd">The chunk whose interior fluids to prepare.</param>
        /// <returns>True if there is interior work (a job should run); false if the bucket or interior set is empty.</returns>
        private bool PrepareInteriorJob(ChunkData cd)
        {
            EnsureAllocated();
            _interior.Clear();
            _replay.Clear();
            _mods.Clear();
            _modsPerSource.Clear();
            _inactive.Clear();

            NativeHashSet<int> bucket = cd.ActiveFluidsBucket;
            if (!bucket.IsCreated || bucket.Count == 0)
                return false;

            // Single partition pass over the active-fluids bucket: capture the enumeration order (interior/border
            // tagged) in _replay so the caller replays in one walk, and collect interior indices for the Burst job.
            foreach (int index in bucket)
            {
                ChunkMath.GetLocalPositionFromFlattenedIndex(index, out int x, out int y, out int z);
                bool interior = FluidTierClassifier.IsTier1Interior(x, y, z);
                _replay.Add(new int2(index, interior ? 1 : 0));
                if (interior)
                    _interior.Add(index);
            }

            if (_interior.Length == 0)
                return false;

            // Interior-only path: no neighbor halo needed (interior reads stay in-chunk). Select the empty neighbor
            // for every direction → the gather sentinel-fills the halo.
            SelectEmptyNeighbors();

            // Full-height band: the job's GetStateLocal then behaves exactly as the pre-band gather (py == y).
            _bandMinY = 0;
            _bandHeight = ChunkMath.CHUNK_HEIGHT;

            // Snapshot the chunk's pre-tick voxels (section-contiguous) for the job to read.
            cd.FillJobVoxelMap(_snapshot);
            return true;
        }

        /// <summary>
        /// TG-4 Phase 4b — clears the outputs and partitions <see cref="ChunkData.ActiveFluidsBucket"/> for the full
        /// halo path: collects <b>every</b> active fluid index for the job (no Tier-1/Tier-2 split) and tags every
        /// <see cref="ReplayOrder"/> entry as job-computed, then gathers the 8 neighbor snapshots and the center.
        /// </summary>
        /// <param name="cd">The chunk whose fluids to prepare.</param>
        /// <param name="worldData">The chunk store used to resolve the neighbor snapshots.</param>
        /// <param name="useBand">When true, size the gather/read window to the tight active-fluid Y-band instead of full height (see <see cref="RunFluids"/>).</param>
        /// <returns>True if there is any active fluid (a job should run); false if the bucket is empty.</returns>
        private bool PrepareFluidJob(ChunkData cd, WorldData worldData, bool useBand)
        {
            EnsureAllocated();
            _interior.Clear();
            _replay.Clear();
            _mods.Clear();
            _modsPerSource.Clear();
            _inactive.Clear();

            NativeHashSet<int> bucket = cd.ActiveFluidsBucket;
            if (!bucket.IsCreated || bucket.Count == 0)
                return false;

            // Every active fluid is job-computed (the halo lets border voxels read across seams). ReplayOrder tags
            // them all interior=1, so the unchanged drain emits every source's mods from the job in bucket order.
            // On the band path, track the active fluids' Y-extent in the same pass to size the gather window.
            int minY = int.MaxValue;
            int maxY = int.MinValue;
            foreach (int index in bucket)
            {
                _replay.Add(new int2(index, 1));
                _interior.Add(index);

                if (useBand)
                {
                    ChunkMath.GetLocalPositionFromFlattenedIndex(index, out _, out int y, out _);
                    minY = math.min(minY, y);
                    maxY = math.max(maxY, y);
                }
            }

            // Size the Y-band: [minActiveY−reach, maxActiveY+reach] clamped to the chunk — a tight superset of every
            // read (FLUID_VERTICAL_REACH invariant), so band reads are byte-identical to full height. The full path
            // keeps [0, CHUNK_HEIGHT) so the job's GetStateLocal degenerates to py == y.
            if (useBand)
            {
                _bandMinY = math.max(0, minY - ChunkMath.FLUID_VERTICAL_REACH);
                int bandMaxYExclusive = math.min(ChunkMath.CHUNK_HEIGHT, maxY + ChunkMath.FLUID_VERTICAL_REACH + 1);
                _bandHeight = bandMaxYExclusive - _bandMinY;
            }
            else
            {
                _bandMinY = 0;
                _bandHeight = ChunkMath.CHUNK_HEIGHT;
            }

            // Gather the 8 neighbor snapshots (for the border voxels) + the center snapshot, all pre-tick.
            PrepareNeighbors(cd, worldData);
            cd.FillJobVoxelMap(_snapshot);
            return true;
        }

        /// <summary>
        /// Fills the owned neighbor buffers from the 8 loaded neighbor chunks (pre-tick snapshots) and points
        /// <see cref="_jobNeighbors"/> at them; a missing/unloaded neighbor points at <see cref="_emptyNeighbor"/>
        /// (zero-length → the gather sentinel-fills it → border reads resolve to void, matching managed GetVoxelState).
        /// </summary>
        private void PrepareNeighbors(ChunkData cd, WorldData worldData)
        {
            EnsureNeighborsAllocated();
            for (int i = 0; i < s_neighborOffsets.Length; i++)
            {
                Vector2Int origin = cd.Position + s_neighborOffsets[i];
                if (worldData != null && worldData.Chunks.TryGetValue(origin, out ChunkData neighbor) && neighbor != null)
                {
                    neighbor.FillJobVoxelMap(_neighborBuffers[i]);
                    _jobNeighbors[i] = _neighborBuffers[i];
                }
                else
                {
                    _jobNeighbors[i] = _emptyNeighbor;
                }
            }
        }

        /// <summary>Points every <see cref="_jobNeighbors"/> slot at the empty (sentinel) neighbor — the interior path.</summary>
        private void SelectEmptyNeighbors()
        {
            for (int i = 0; i < _jobNeighbors.Length; i++)
                _jobNeighbors[i] = _emptyNeighbor;
        }

        /// <summary>Builds the fluid job over the prepared snapshot + neighbor halo + indices and this ticker's outputs.</summary>
        private FluidTickJob BuildJob(ChunkData cd, int tickCounter, NativeArray<BlockTypeJobData> blockTypes) =>
            new FluidTickJob
            {
                CenterVoxels = _snapshot,
                // Neighbor selection set by the Prepare pass: empty (sentinel halo) for the interior path, real
                // snapshots for the Phase-4b halo path. Compass order: 0=W,1=E,2=S,3=N,4=SW,5=NW,6=SE,7=NE.
                VoxelW = _jobNeighbors[0], VoxelE = _jobNeighbors[1], VoxelS = _jobNeighbors[2], VoxelN = _jobNeighbors[3],
                VoxelSW = _jobNeighbors[4], VoxelNW = _jobNeighbors[5], VoxelSE = _jobNeighbors[6], VoxelNE = _jobNeighbors[7],
                PaddedVoxels = _paddedVoxels,
                BlockTypes = blockTypes,
                InteriorFluidIndices = _interior.AsArray(),
                BandMinY = _bandMinY,
                BandHeight = _bandHeight,
                TickCounter = tickCounter,
                ChunkOrigin = new int2(cd.Position.x, cd.Position.y),
                Mods = _mods,
                NowInactive = _inactive,
                ModsPerSource = _modsPerSource,
            };

        /// <summary>Lazily allocates the reusable persistent scratch on first use.</summary>
        private void EnsureAllocated()
        {
            if (_allocated)
                return;

            _snapshot = new NativeArray<uint>(ChunkMath.CHUNK_VOLUME, Allocator.Persistent);
            _paddedVoxels = new NativeArray<uint>(ChunkMath.PADDED_FLUID_VOLUME, Allocator.Persistent);
            _emptyNeighbor = new NativeArray<uint>(0, Allocator.Persistent); // created, zero-length → gather sentinel
            // The selection array starts all-empty (the interior path); the halo Prepare swaps in real buffers. The
            // full-chunk neighbor buffers themselves are allocated lazily only when the halo path is first used.
            _jobNeighbors = new NativeArray<uint>[s_neighborOffsets.Length];
            for (int i = 0; i < _jobNeighbors.Length; i++)
                _jobNeighbors[i] = _emptyNeighbor;
            _interior = new NativeList<int>(256, Allocator.Persistent);
            _replay = new NativeList<int2>(256, Allocator.Persistent);
            _mods = new NativeList<VoxelMod>(256, Allocator.Persistent);
            _modsPerSource = new NativeList<int>(256, Allocator.Persistent);
            _inactive = new NativeList<int>(64, Allocator.Persistent);
            _allocated = true;
        }

        /// <summary>Lazily allocates the 8 full-chunk neighbor snapshot buffers on first use of the Phase-4b halo path.</summary>
        private void EnsureNeighborsAllocated()
        {
            if (_neighborsAllocated)
                return;

            _neighborBuffers = new NativeArray<uint>[s_neighborOffsets.Length];
            for (int i = 0; i < _neighborBuffers.Length; i++)
                _neighborBuffers[i] = new NativeArray<uint>(ChunkMath.CHUNK_VOLUME, Allocator.Persistent);
            _neighborsAllocated = true;
        }

        /// <summary>Disposes the reusable native scratch. Call once when the owner (World) tears down.</summary>
        public void Dispose()
        {
            if (!_allocated)
                return;

            if (_snapshot.IsCreated) _snapshot.Dispose();
            if (_paddedVoxels.IsCreated) _paddedVoxels.Dispose();
            if (_emptyNeighbor.IsCreated) _emptyNeighbor.Dispose();
            if (_neighborsAllocated)
            {
                for (int i = 0; i < _neighborBuffers.Length; i++)
                    if (_neighborBuffers[i].IsCreated)
                        _neighborBuffers[i].Dispose();
                _neighborsAllocated = false;
            }

            if (_interior.IsCreated) _interior.Dispose();
            if (_replay.IsCreated) _replay.Dispose();
            if (_mods.IsCreated) _mods.Dispose();
            if (_modsPerSource.IsCreated) _modsPerSource.Dispose();
            if (_inactive.IsCreated) _inactive.Dispose();
            _allocated = false;
        }
    }
}
