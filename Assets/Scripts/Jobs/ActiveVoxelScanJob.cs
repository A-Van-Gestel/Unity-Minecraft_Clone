using Data;
using Helpers;
using Jobs.BurstData;
using Jobs.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace Jobs
{
    /// <summary>
    /// Single-threaded Burst pass that scans a fully-generated chunk voxel map and emits the
    /// flat chunk indices of every voxel whose block type has active behavior (e.g. grass, water) and that can act on
    /// its next tick — the ES-31 registration screen (<see cref="ActiveVoxelScreen"/>) drops the rest.
    /// </summary>
    /// <remarks>
    /// Scheduled as the final generation pass (after cave carving / isolation filtering) so it
    /// reads the finalized voxel map. Replaces the main-thread managed
    /// <c>World.Instance.BlockTypes[id].isActive</c> dereference that previously ran up to
    /// <see cref="VoxelData.ChunkWidth"/>² × <see cref="VoxelData.ChunkHeight"/> times per chunk.
    /// The emitted indices use the <see cref="ChunkMath.GetFlattenedIndexInChunk"/> convention and
    /// are unpacked back to local positions on the main thread by
    /// <see cref="Data.ChunkData.RegisterActiveVoxelsFromJob"/>.
    /// <para><b>Parity invariant:</b> this Burst job (fresh-gen path) and the managed
    /// <see cref="Data.ChunkData.RescanActiveVoxels"/> bitmask scan (load-from-save / visual re-attach path) are two
    /// implementations of the same "which voxels are active" decision and MUST agree on both (1) the active
    /// criterion and (2) the flat-index/section convention. The criterion is drift-proof by construction —
    /// this job's <see cref="BlockTypeJobData.IsActive"/> and the rescan's <see cref="World.IsActiveById"/>
    /// are built in one loop from <c>BlockType.isActive</c> in <c>World</c> init, and both apply the one
    /// <see cref="ActiveVoxelScreen.MustRegister{TSource}"/>. If you change either the criterion or the index
    /// convention, change both paths.</para>
    /// </remarks>
    [BurstCompile]
    public struct ActiveVoxelScanJob : IJob
    {
        /// <summary>The finalized packed voxel map for the chunk (post cave-carve).</summary>
        [ReadOnly]
        public NativeArray<uint> VoxelMap;

        /// <summary>Global block-type lookup; <see cref="BlockTypeJobData.IsActive"/> drives emission.</summary>
        [ReadOnly]
        public NativeArray<BlockTypeJobData> BlockTypes;

        /// <summary>Output list of packed local indices (0..ChunkVolume-1) for the active voxels the screen keeps.</summary>
        public NativeList<int> ActiveVoxels;

        /// <summary>Accumulates this job's execute time for the performance monitor; untimed by default.</summary>
        public JobBusyTimer Timer;

        /// <inheritdoc />
        public void Execute()
        {
            long busyStart = Timer.Begin();
            FlatMapScreenSource screen = new FlatMapScreenSource { VoxelMap = VoxelMap, BlockTypes = BlockTypes };
            for (int i = 0; i < VoxelMap.Length; i++)
            {
                uint packed = VoxelMap[i];
                ushort id = BurstVoxelDataBitMapping.GetId(packed);

                // Guard against a stale/migrated voxel map carrying an id outside the current palette: BlockTypes
                // is sized to the registered block count, so an out-of-range id would be an opaque out-of-bounds
                // Burst read (garbage or a crash in a release build with safety checks off). An unknown id is inert.
                if (id < BlockTypes.Length && BlockTypes[id].IsActive)
                {
                    ChunkMath.GetLocalPositionFromFlattenedIndex(i, out int x, out int y, out int z);
                    if (ActiveVoxelScreen.MustRegister(ref screen, x, y, z, packed))
                        ActiveVoxels.Add(i);
                }
            }

            Timer.End(busyStart);
        }
    }
}
