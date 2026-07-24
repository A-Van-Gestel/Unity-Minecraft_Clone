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
        // order matches GatherPaddedFluidVoxelsBand' (w,e,s,n,sw,nw,se,ne) parameters and s_neighborOffsets.
        private NativeArray<uint>[] _neighborBuffers;
        private NativeArray<uint>[] _jobNeighbors;
        private bool _neighborsAllocated;
        private NativeList<int> _interior;
        private NativeList<VoxelMod> _mods;
        private NativeList<int> _modsPerSource;
        private NativeList<int> _inactive;
        private bool _allocated;

        // TG-4 Y-band: the active-fluid Y-band the FluidTickJob gathers + reads (a prefix of the full-height padded
        // volume) — the tight [minActiveY−reach, maxActiveY+reach] computed by the Prepare pass, making the per-tick
        // copy independent of world height.
        private int _bandMinY;
        private int _bandHeight;

        // Neighbor voxel-origin offsets in compass order (w,e,s,n,sw,nw,se,ne) — matches GatherPaddedFluidVoxelsBand and
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

        /// <summary>Voxel mods emitted by the most recent <see cref="RunFluids"/> (valid until the next call).</summary>
        public NativeList<VoxelMod> Mods => _mods;

        /// <summary>
        /// Per-source mod run lengths from the most recent run, one entry per active fluid voxel in
        /// <see cref="InteriorIndices"/> order. Consume <see cref="Mods"/> in these runs to replay the job's emission
        /// in bucket order (valid until the next call).
        /// </summary>
        public NativeList<int> ModsPerSource => _modsPerSource;

        /// <summary>Flat indices of fluid voxels that became inactive in the most recent run (valid until the next call).</summary>
        public NativeList<int> InactiveInterior => _inactive;

        /// <summary>
        /// The flat chunk indices of the fluid voxels processed by the most recent run, in the same order as
        /// <see cref="ModsPerSource"/> (i.e. the order they were enumerated from the bucket). The i-th entry owns
        /// the i-th <see cref="ModsPerSource"/> run — lets a caller map the job's per-source output back to a voxel
        /// position without re-deriving the partition (valid until the next call).
        /// </summary>
        public NativeList<int> InteriorIndices => _interior;

        /// <summary>
        /// The serial (<c>.Run()</c>) fluid tick: ticks EVERY active fluid (interior AND border) of <paramref name="cd"/>
        /// through <see cref="FluidTickJob"/>, border voxels resolving cross-chunk reads from the gathered neighbor
        /// halo (neighbors looked up in <paramref name="worldData"/>). Reads a pre-tick snapshot and does NOT modify
        /// the chunk; the caller drains <see cref="Mods"/>/<see cref="ModsPerSource"/>/<see cref="InactiveInterior"/>
        /// afterward. Used by the validation harness as the serial determinism baseline; production schedules via
        /// <see cref="ScheduleFluids"/>.
        /// </summary>
        /// <param name="cd">The chunk whose fluids to tick.</param>
        /// <param name="tickCounter">The current tick salt (<c>World.TickCounter</c>) for the viscosity RNG.</param>
        /// <param name="blockTypes">The global block-type job blob.</param>
        /// <param name="worldData">The chunk store used to resolve the 8 neighbor snapshots (missing → sentinel/void).</param>
        /// <remarks>
        /// The gather + reads are sized to the tight active-fluid Y-band (<see cref="ChunkMath.FLUID_VERTICAL_REACH"/>-padded)
        /// rather than full chunk height — byte-identical by the reach invariant, but the per-tick copy is independent
        /// of world height.
        /// </remarks>
        public void RunFluids(ChunkData cd, int tickCounter, NativeArray<BlockTypeJobData> blockTypes, WorldData worldData)
        {
            if (PrepareFluidJob(cd, worldData))
                BuildJob(cd, tickCounter, blockTypes).Run();
        }

        /// <summary>
        /// The parallel counterpart of <see cref="RunFluids"/>: prepares all-fluid indices + the neighbor halo, then
        /// schedules the job on a worker and returns its <see cref="JobHandle"/>. The caller batches handles across
        /// chunks, completes them, then drains the outputs in deterministic chunk order. <b>Each concurrently-scheduled
        /// chunk needs its own ticker instance</b> (the snapshot + neighbor scratch is per-ticker, not shareable in flight).
        /// </summary>
        /// <param name="cd">The chunk whose fluids to tick.</param>
        /// <param name="tickCounter">The current tick salt (<c>World.TickCounter</c>) for the viscosity RNG.</param>
        /// <param name="blockTypes">The global block-type job blob.</param>
        /// <param name="worldData">The chunk store used to resolve the 8 neighbor snapshots (missing → sentinel/void).</param>
        /// <returns>The scheduled job's handle, or <c>default</c> when the chunk has no active fluids.</returns>
        public JobHandle ScheduleFluids(ChunkData cd, int tickCounter, NativeArray<BlockTypeJobData> blockTypes, WorldData worldData)
        {
            if (!PrepareFluidJob(cd, worldData))
                return default;

            return BuildJob(cd, tickCounter, blockTypes).Schedule();
        }

        /// <summary>
        /// Clears the per-run outputs and partitions <see cref="ChunkData.ActiveFluidsBucket"/> for the tick: collects
        /// <b>every</b> active fluid index for the job, sizes the active-fluid Y-band, then gathers the 8 neighbor
        /// snapshots and the center. Shared by the serial (<see cref="RunFluids"/>) and parallel
        /// (<see cref="ScheduleFluids"/>) paths.
        /// </summary>
        /// <param name="cd">The chunk whose fluids to prepare.</param>
        /// <param name="worldData">The chunk store used to resolve the neighbor snapshots.</param>
        /// <returns>True if there is any active fluid (a job should run); false if the bucket is empty.</returns>
        private bool PrepareFluidJob(ChunkData cd, WorldData worldData)
        {
            EnsureAllocated();
            _interior.Clear();
            _mods.Clear();
            _modsPerSource.Clear();
            _inactive.Clear();

            NativeHashSet<int> bucket = cd.ActiveFluidsBucket;
            if (!bucket.IsCreated || bucket.Count == 0)
                return false;

            // Every active fluid is job-computed (the halo lets border voxels read across seams). Collect the indices
            // and track their Y-extent in the same pass to size the gather window.
            int minY = int.MaxValue;
            int maxY = int.MinValue;
            foreach (int index in bucket)
            {
                _interior.Add(index);
                ChunkMath.GetLocalPositionFromFlattenedIndex(index, out _, out int y, out _);
                minY = math.min(minY, y);
                maxY = math.max(maxY, y);
            }

            // Size the Y-band: [minActiveY−reach, maxActiveY+reach] clamped to the chunk — a tight superset of every
            // read (FLUID_VERTICAL_REACH invariant), making the per-tick copy independent of world height.
            _bandMinY = math.max(0, minY - ChunkMath.FLUID_VERTICAL_REACH);
            int bandMaxYExclusive = math.min(ChunkMath.CHUNK_HEIGHT, maxY + ChunkMath.FLUID_VERTICAL_REACH + 1);
            _bandHeight = bandMaxYExclusive - _bandMinY;

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
                if (worldData != null && worldData.TryGetChunk(origin, out ChunkData neighbor) && neighbor != null)
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

        /// <summary>Builds the fluid job over the prepared snapshot + neighbor halo + indices and this ticker's outputs.</summary>
        private FluidTickJob BuildJob(ChunkData cd, int tickCounter, NativeArray<BlockTypeJobData> blockTypes) =>
            new FluidTickJob
            {
                CenterVoxels = _snapshot,
                // Neighbor selection set by PrepareNeighbors: a real snapshot per loaded neighbor, else the empty
                // (sentinel) neighbor for a missing one. Compass order: 0=W,1=E,2=S,3=N,4=SW,5=NW,6=SE,7=NE.
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
            // The selection array starts all-empty; PrepareNeighbors swaps in the real buffers per loaded neighbor.
            // The full-chunk neighbor buffers themselves are allocated lazily on first neighbor gather.
            _jobNeighbors = new NativeArray<uint>[s_neighborOffsets.Length];
            for (int i = 0; i < _jobNeighbors.Length; i++)
                _jobNeighbors[i] = _emptyNeighbor;
            _interior = new NativeList<int>(256, Allocator.Persistent);
            _mods = new NativeList<VoxelMod>(256, Allocator.Persistent);
            _modsPerSource = new NativeList<int>(256, Allocator.Persistent);
            _inactive = new NativeList<int>(64, Allocator.Persistent);
            _allocated = true;
        }

        /// <summary>Lazily allocates the 8 full-chunk neighbor snapshot buffers on first neighbor gather.</summary>
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
            if (_mods.IsCreated) _mods.Dispose();
            if (_modsPerSource.IsCreated) _modsPerSource.Dispose();
            if (_inactive.IsCreated) _inactive.Dispose();
            _allocated = false;
        }
    }
}
