using System;
using System.Collections.Generic;
using Data;
using Unity.Collections;

namespace Helpers
{
    /// <summary>
    /// One startup lighting sweep's shared snapshots: each chunk's voxel and light map, filled on first request and
    /// handed to every job of the sweep that reads it, instead of one copy per reader. Sound only while nothing writes
    /// voxels or light between the first fill and the sweep's last schedule. Freed once the sweep is closed and every
    /// job holding a reference has been released. Main thread only.
    /// </summary>
    public sealed class StartupSweepSnapshots : INeighborMapSource, IDisposable
    {
        private readonly Dictionary<ChunkCoord, NativeArray<uint>> _voxelMaps = new Dictionary<ChunkCoord, NativeArray<uint>>();
        private readonly Dictionary<ChunkCoord, NativeArray<ushort>> _lightMaps = new Dictionary<ChunkCoord, NativeArray<ushort>>();
        private readonly WorldData _worldData;
        private readonly Allocator _allocator;
        private int _holders;
        private bool _closed;

        /// <summary>Creates an empty, open set.</summary>
        /// <param name="worldData">The chunks the snapshots are filled from.</param>
        /// <param name="allocator">The allocator for every snapshot.</param>
        public StartupSweepSnapshots(WorldData worldData, Allocator allocator)
        {
            _worldData = worldData;
            _allocator = allocator;
        }

        /// <summary>Whether the snapshots have been freed.</summary>
        public bool IsDisposed { get; private set; }

        /// <summary>Distinct voxel plus light maps filled so far.</summary>
        public int SnapshotCount => _voxelMaps.Count + _lightMaps.Count;

        /// <summary>Counts one more job reading these snapshots; it must call <see cref="Release"/> once.</summary>
        public void Retain() => _holders++;

        /// <summary>Drops one job's reference; frees the snapshots when the set is closed and no job holds them.</summary>
        public void Release()
        {
            _holders--;
            if (_closed && _holders <= 0) Dispose();
        }

        /// <summary>Ends the sweep: no new reader may take a map, and the snapshots go with the last holder.</summary>
        public void Close()
        {
            _closed = true;
            if (_holders <= 0) Dispose();
        }

        /// <inheritdoc />
        /// <remarks>Shared: the caller must not dispose the returned map. <paramref name="pooled"/> and
        /// <paramref name="allocator"/> are ignored.</remarks>
        public NativeArray<uint> AcquireVoxelMap(ChunkCoord coord, bool pooled, Allocator allocator)
        {
            ThrowIfUnavailable();
            if (_voxelMaps.TryGetValue(coord, out NativeArray<uint> map)) return map;

            map = _worldData.GetChunkMapForJob(coord.ToVoxelOrigin(), _allocator);
            _voxelMaps.Add(coord, map);
            return map;
        }

        /// <inheritdoc />
        /// <remarks>Shared: the caller must not dispose the returned map. <paramref name="pooled"/> and
        /// <paramref name="allocator"/> are ignored.</remarks>
        public NativeArray<ushort> AcquireLightMap(ChunkCoord coord, bool pooled, Allocator allocator)
        {
            ThrowIfUnavailable();
            if (_lightMaps.TryGetValue(coord, out NativeArray<ushort> map)) return map;

            map = _worldData.GetChunkLightMapForJob(coord.ToVoxelOrigin(), _allocator);
            _lightMaps.Add(coord, map);
            return map;
        }

        /// <summary>
        /// Refills every snapshot from its live chunk and counts the maps that differ: non-zero means something wrote
        /// voxels or light after a job took its snapshot. Diagnostic only; allocates two temporary maps.
        /// </summary>
        /// <returns>The voxel plus light maps that no longer match their chunk.</returns>
        public int CountStaleSnapshots()
        {
            int stale = 0;
            const int volume = ChunkMath.CHUNK_VOLUME;
            NativeArray<uint> voxels = new NativeArray<uint>(volume, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            NativeArray<ushort> light = new NativeArray<ushort>(volume, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            try
            {
                foreach (KeyValuePair<ChunkCoord, NativeArray<uint>> entry in _voxelMaps)
                {
                    _worldData.FillChunkMapForJob(entry.Key.ToVoxelOrigin(), voxels);
                    if (!entry.Value.ArraysEqual(voxels)) stale++;
                }

                foreach (KeyValuePair<ChunkCoord, NativeArray<ushort>> entry in _lightMaps)
                {
                    _worldData.FillChunkLightMapForJob(entry.Key.ToVoxelOrigin(), light);
                    if (!entry.Value.ArraysEqual(light)) stale++;
                }
            }
            finally
            {
                voxels.Dispose();
                light.Dispose();
            }

            return stale;
        }

        /// <summary>Frees every snapshot. Only safe once no scheduled job still reads them.</summary>
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;

            foreach (NativeArray<uint> map in _voxelMaps.Values) map.Dispose();
            foreach (NativeArray<ushort> map in _lightMaps.Values) map.Dispose();
            _voxelMaps.Clear();
            _lightMaps.Clear();
        }

        private void ThrowIfUnavailable()
        {
            if (_closed || IsDisposed)
                throw new InvalidOperationException("Startup sweep snapshots taken after the sweep closed.");
        }
    }
}
