using System;
using Data;
using Helpers;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

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
        private NativeList<int> _interior;
        private NativeList<int2> _replay;
        private NativeList<VoxelMod> _mods;
        private NativeList<int> _modsPerSource;
        private NativeList<int> _inactive;
        private bool _allocated;

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

            // Snapshot the chunk's pre-tick voxels (section-contiguous) for the job to read.
            cd.FillJobVoxelMap(_snapshot);
            return true;
        }

        /// <summary>Builds the interior fluid job over the prepared snapshot + interior indices and this ticker's outputs.</summary>
        private FluidTickJob BuildJob(ChunkData cd, int tickCounter, NativeArray<BlockTypeJobData> blockTypes) =>
            new FluidTickJob
            {
                CenterVoxels = _snapshot,
                // Interior-only path: empty neighbors → the gather sentinel-fills the halo (border reads as void).
                // Phase 4b C4 supplies real neighbor snapshots here.
                VoxelW = _emptyNeighbor, VoxelE = _emptyNeighbor, VoxelS = _emptyNeighbor, VoxelN = _emptyNeighbor,
                VoxelSW = _emptyNeighbor, VoxelNW = _emptyNeighbor, VoxelSE = _emptyNeighbor, VoxelNE = _emptyNeighbor,
                PaddedVoxels = _paddedVoxels,
                BlockTypes = blockTypes,
                InteriorFluidIndices = _interior.AsArray(),
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
            _interior = new NativeList<int>(256, Allocator.Persistent);
            _replay = new NativeList<int2>(256, Allocator.Persistent);
            _mods = new NativeList<VoxelMod>(256, Allocator.Persistent);
            _modsPerSource = new NativeList<int>(256, Allocator.Persistent);
            _inactive = new NativeList<int>(64, Allocator.Persistent);
            _allocated = true;
        }

        /// <summary>Disposes the reusable native scratch. Call once when the owner (World) tears down.</summary>
        public void Dispose()
        {
            if (!_allocated)
                return;

            if (_snapshot.IsCreated) _snapshot.Dispose();
            if (_paddedVoxels.IsCreated) _paddedVoxels.Dispose();
            if (_emptyNeighbor.IsCreated) _emptyNeighbor.Dispose();
            if (_interior.IsCreated) _interior.Dispose();
            if (_replay.IsCreated) _replay.Dispose();
            if (_mods.IsCreated) _mods.Dispose();
            if (_modsPerSource.IsCreated) _modsPerSource.Dispose();
            if (_inactive.IsCreated) _inactive.Dispose();
            _allocated = false;
        }
    }
}
