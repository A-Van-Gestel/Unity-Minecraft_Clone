using System;
using System.Collections.Generic;
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
        private NativeList<int> _interior;
        private NativeList<VoxelMod> _mods;
        private NativeList<int> _modsPerSource;
        private NativeList<int> _inactive;
        private bool _allocated;

        /// <summary>Voxel mods emitted by the most recent <see cref="RunInteriorFluids"/> (valid until the next call).</summary>
        public NativeList<VoxelMod> Mods => _mods;

        /// <summary>
        /// Per-source mod run lengths from the most recent run, in the same order interior voxels were enumerated
        /// from the bucket. Walk the bucket in that order again and consume <see cref="Mods"/> in these runs to
        /// replay the job's emission interleaved with the managed border path (valid until the next call).
        /// </summary>
        public NativeList<int> ModsPerSource => _modsPerSource;

        /// <summary>Flat indices of interior voxels that became inactive in the most recent run (valid until the next call).</summary>
        public NativeList<int> InactiveInterior => _inactive;

        /// <summary>
        /// Ticks the interior fluids of <paramref name="cd"/> via <see cref="FluidTickJob"/>. Reads a pre-tick
        /// snapshot of the chunk and does NOT modify it. Border (Tier-2) fluid indices are appended to
        /// <paramref name="borderFluidsOut"/> for the managed path (the list is not cleared here — the caller owns it).
        /// </summary>
        /// <param name="cd">The chunk whose interior fluids to tick.</param>
        /// <param name="tickCounter">The current tick salt (<c>World.TickCounter</c>) for the viscosity RNG.</param>
        /// <param name="blockTypes">The global block-type job blob (<c>World.JobDataManager.BlockTypesJobData</c>).</param>
        /// <param name="borderFluidsOut">Receives the flat indices that stay on the managed (Tier-2) path.</param>
        public void RunInteriorFluids(ChunkData cd, int tickCounter,
            NativeArray<BlockTypeJobData> blockTypes, List<int> borderFluidsOut)
        {
            EnsureAllocated();
            _interior.Clear();
            _mods.Clear();
            _modsPerSource.Clear();
            _inactive.Clear();

            NativeHashSet<int> bucket = cd.ActiveFluidsBucket;
            if (!bucket.IsCreated || bucket.Count == 0)
                return;

            // Partition the active-fluids bucket: interior → Burst job, border → managed remainder.
            foreach (int index in bucket)
            {
                ChunkMath.GetLocalPositionFromFlattenedIndex(index, out int x, out int y, out int z);
                if (FluidTierClassifier.IsTier1Interior(x, y, z))
                    _interior.Add(index);
                else
                    borderFluidsOut.Add(index);
            }

            if (_interior.Length == 0)
                return;

            // Snapshot the chunk's pre-tick voxels (section-contiguous), then run the interior fluid job serially.
            // .Run() (not Schedule) keeps Phase 3 single-threaded — Phase 4 parallelizes across chunks.
            cd.FillJobVoxelMap(_snapshot);

            new FluidTickJob
            {
                VoxelMap = _snapshot,
                BlockTypes = blockTypes,
                InteriorFluidIndices = _interior.AsArray(),
                TickCounter = tickCounter,
                ChunkOrigin = new int2(cd.Position.x, cd.Position.y),
                Mods = _mods,
                NowInactive = _inactive,
                ModsPerSource = _modsPerSource,
            }.Run();
        }

        /// <summary>Lazily allocates the reusable persistent scratch on first use.</summary>
        private void EnsureAllocated()
        {
            if (_allocated)
                return;

            _snapshot = new NativeArray<uint>(ChunkMath.CHUNK_VOLUME, Allocator.Persistent);
            _interior = new NativeList<int>(256, Allocator.Persistent);
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
            if (_interior.IsCreated) _interior.Dispose();
            if (_mods.IsCreated) _mods.Dispose();
            if (_modsPerSource.IsCreated) _modsPerSource.Dispose();
            if (_inactive.IsCreated) _inactive.Dispose();
            _allocated = false;
        }
    }
}
