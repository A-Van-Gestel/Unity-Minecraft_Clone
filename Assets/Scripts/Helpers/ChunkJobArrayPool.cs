using System;
using System.Collections.Generic;
using Jobs.Data;
using Unity.Collections;

namespace Helpers
{
    /// <summary>
    /// Pools the fixed-size full-volume voxel/light map buffers used as chunk job inputs,
    /// avoiding per-job Persistent allocate/dispose churn.
    /// <para><b>Contract:</b> rented arrays are NOT cleared — renters must write every element
    /// before the array is read (see <c>WorldData.FillChunkMapForJob</c> / <c>FillChunkLightMapForJob</c>).</para>
    /// <para><b>Threading:</b> main-thread only. Return buffers only after <c>JobHandle.Complete()</c>.</para>
    /// </summary>
    public class ChunkJobArrayPool : IDisposable
    {
        /// <summary>Number of elements in one full-chunk buffer (16 × 128 × 16).</summary>
        public const int BufferLength = VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth;

        /// <summary>Number of elements in one halo-padded lighting buffer (20 × 128 × 20 = 51,200).</summary>
        public const int PaddedBufferLength = ChunkMath.PADDED_LIGHTING_VOLUME;

        /// <summary>
        /// Maximum buffers retained per element type; returns beyond this cap dispose instead,
        /// so a backlog spike does not permanently pin its peak native memory.
        /// Sized above peak steady-state in-flight demand (≈468 buffers per type at max job
        /// settings: (32 lighting + 20 mesh jobs) × 9 buffers per type) so renting never degrades
        /// into an alloc/free storm. Worst-case retention ≈ 96 MB, but the pool only ever holds
        /// the peak that was actually rented concurrently.
        /// </summary>
        private const int MAX_RETAINED_PER_TYPE = 512;

        private readonly Stack<NativeArray<uint>> _voxelMaps = new Stack<NativeArray<uint>>();
        private readonly Stack<NativeArray<ushort>> _lightMaps = new Stack<NativeArray<ushort>>();

        // LI-1: separate retained stacks for the halo-padded lighting buffers (length PaddedBufferLength).
        // Kept distinct from the full-chunk maps because their length differs — a Return validates length
        // against the matching cap before retaining, so the two pools can never cross-contaminate.
        private readonly Stack<NativeArray<uint>> _paddedVoxels = new Stack<NativeArray<uint>>();
        private readonly Stack<NativeArray<ushort>> _paddedLight = new Stack<NativeArray<ushort>>();

        private bool _isDisposed;

        /// <summary>
        /// Rents a full-chunk <c>uint</c> voxel map buffer. Contents are undefined —
        /// the caller must write every element.
        /// </summary>
        /// <returns>A Persistent-allocated NativeArray of length <see cref="BufferLength"/>.</returns>
        public NativeArray<uint> RentVoxelMap()
        {
            return _voxelMaps.Count > 0
                ? _voxelMaps.Pop()
                : new NativeArray<uint>(BufferLength, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        /// <summary>
        /// Rents a full-chunk <c>ushort</c> light map buffer. Contents are undefined —
        /// the caller must write every element.
        /// </summary>
        /// <returns>A Persistent-allocated NativeArray of length <see cref="BufferLength"/>.</returns>
        public NativeArray<ushort> RentLightMap()
        {
            return _lightMaps.Count > 0
                ? _lightMaps.Pop()
                : new NativeArray<ushort>(BufferLength, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        /// <summary>
        /// Rents a halo-padded <c>uint</c> voxel volume buffer (length <see cref="PaddedBufferLength"/>),
        /// used by <c>NeighborhoodLightingJob</c> (LI-1). Contents are undefined — the caller (the gather
        /// fill) writes every element.
        /// </summary>
        /// <returns>A Persistent-allocated NativeArray of length <see cref="PaddedBufferLength"/>.</returns>
        public NativeArray<uint> RentPaddedVoxels()
        {
            return _paddedVoxels.Count > 0
                ? _paddedVoxels.Pop()
                : new NativeArray<uint>(PaddedBufferLength, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        /// <summary>
        /// Rents a halo-padded <c>ushort</c> light volume buffer (length <see cref="PaddedBufferLength"/>),
        /// used by <c>NeighborhoodLightingJob</c> (LI-1). Contents are undefined — the caller (the gather
        /// fill) writes every element.
        /// </summary>
        /// <returns>A Persistent-allocated NativeArray of length <see cref="PaddedBufferLength"/>.</returns>
        public NativeArray<ushort> RentPaddedLight()
        {
            return _paddedLight.Count > 0
                ? _paddedLight.Pop()
                : new NativeArray<ushort>(PaddedBufferLength, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        /// <summary>
        /// Returns a voxel map buffer to the pool. The buffer must no longer be referenced by any
        /// scheduled job. Disposes the buffer instead if the retention cap is reached or the pool
        /// has been disposed.
        /// </summary>
        /// <param name="buffer">The buffer to return. Safe to pass a default/uncreated array (no-op).</param>
        public void Return(NativeArray<uint> buffer)
        {
            if (!buffer.IsCreated) return;

            if (_isDisposed || _voxelMaps.Count >= MAX_RETAINED_PER_TYPE || buffer.Length != BufferLength)
            {
                buffer.Dispose();
                return;
            }

            _voxelMaps.Push(buffer);
        }

        /// <summary>
        /// Returns a light map buffer to the pool. The buffer must no longer be referenced by any
        /// scheduled job. Disposes the buffer instead if the retention cap is reached or the pool
        /// has been disposed.
        /// </summary>
        /// <param name="buffer">The buffer to return. Safe to pass a default/uncreated array (no-op).</param>
        public void Return(NativeArray<ushort> buffer)
        {
            if (!buffer.IsCreated) return;

            if (_isDisposed || _lightMaps.Count >= MAX_RETAINED_PER_TYPE || buffer.Length != BufferLength)
            {
                buffer.Dispose();
                return;
            }

            _lightMaps.Push(buffer);
        }

        /// <summary>
        /// Returns a halo-padded voxel volume buffer to the pool. The buffer must no longer be referenced
        /// by any scheduled job. Disposes the buffer instead if the retention cap is reached, the pool has
        /// been disposed, or the length does not match <see cref="PaddedBufferLength"/>.
        /// </summary>
        /// <param name="buffer">The buffer to return. Safe to pass a default/uncreated array (no-op).</param>
        public void ReturnPaddedVoxels(NativeArray<uint> buffer)
        {
            if (!buffer.IsCreated) return;

            if (_isDisposed || _paddedVoxels.Count >= MAX_RETAINED_PER_TYPE || buffer.Length != PaddedBufferLength)
            {
                buffer.Dispose();
                return;
            }

            _paddedVoxels.Push(buffer);
        }

        /// <summary>
        /// Returns a halo-padded light volume buffer to the pool. The buffer must no longer be referenced
        /// by any scheduled job. Disposes the buffer instead if the retention cap is reached, the pool has
        /// been disposed, or the length does not match <see cref="PaddedBufferLength"/>.
        /// </summary>
        /// <param name="buffer">The buffer to return. Safe to pass a default/uncreated array (no-op).</param>
        public void ReturnPaddedLight(NativeArray<ushort> buffer)
        {
            if (!buffer.IsCreated) return;

            if (_isDisposed || _paddedLight.Count >= MAX_RETAINED_PER_TYPE || buffer.Length != PaddedBufferLength)
            {
                buffer.Dispose();
                return;
            }

            _paddedLight.Push(buffer);
        }

        /// <summary>
        /// Returns every buffer of a neighbor map set to the pool. The buffers must no longer be
        /// referenced by any scheduled job. Safe to call with a partially-created set —
        /// uncreated entries are skipped.
        /// </summary>
        /// <param name="maps">The neighbor map set whose buffers are returned.</param>
        public void Return(in NeighborMapSet maps)
        {
            Return(maps.NeighborN);
            Return(maps.NeighborE);
            Return(maps.NeighborS);
            Return(maps.NeighborW);
            Return(maps.NeighborNE);
            Return(maps.NeighborSE);
            Return(maps.NeighborSW);
            Return(maps.NeighborNW);
            Return(maps.LightN);
            Return(maps.LightE);
            Return(maps.LightS);
            Return(maps.LightW);
            Return(maps.LightNE);
            Return(maps.LightSE);
            Return(maps.LightSW);
            Return(maps.LightNW);
        }

        /// <summary>
        /// Disposes all retained buffers. Buffers still rented out at this point must be
        /// disposed by their owners (subsequent <c>Return</c> calls dispose instead of pooling).
        /// </summary>
        public void Dispose()
        {
            _isDisposed = true;

            while (_voxelMaps.Count > 0)
                _voxelMaps.Pop().Dispose();

            while (_lightMaps.Count > 0)
                _lightMaps.Pop().Dispose();

            while (_paddedVoxels.Count > 0)
                _paddedVoxels.Pop().Dispose();

            while (_paddedLight.Count > 0)
                _paddedLight.Pop().Dispose();
        }
    }
}
