using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Data;
using Helpers;
using UnityEngine;

namespace Serialization
{
    public class ChunkStorageManager
    {
        private readonly string _saveFolderPath;
        private readonly IRegionAddressCodec _codec;

        // Concurrent Dictionary with Lazy to ensure thread-safe, single initialization of RegionFiles
        private readonly ConcurrentDictionary<Vector2Int, Lazy<RegionFile>> _regions = new ConcurrentDictionary<Vector2Int, Lazy<RegionFile>>();

        /// <summary>
        /// Initializes a new instance of the ChunkStorageManager, setting up region file I/O for the specified world.
        /// </summary>
        /// <param name="worldName">Name of the world being loaded or created.</param>
        /// <param name="useVolatilePath">True in Editor mode to use a temp save directory.</param>
        /// <param name="saveVersion">
        /// The version field read from <c>level.dat</c>. Determines which
        /// <see cref="IRegionAddressCodec"/> is used for all region address arithmetic.
        /// Pass <see cref="SaveSystem.CURRENT_VERSION"/> for new worlds.
        /// </param>
        public ChunkStorageManager(string worldName, bool useVolatilePath, int saveVersion)
        {
            // Determine Save Path
            string basePath = useVolatilePath
                ? Path.Combine(Application.persistentDataPath, "Editor_Temp_Saves")
                : Path.Combine(Application.persistentDataPath, "Saves");

            _saveFolderPath = Path.Combine(basePath, worldName, "Region");
            _codec = RegionAddressCodec.ForVersion(saveVersion);

            if (!Directory.Exists(_saveFolderPath)) Directory.CreateDirectory(_saveFolderPath);
        }

        // -------------------------------------------------------------------------
        // Public API
        // -------------------------------------------------------------------------

        /// <summary>
        /// Asynchronously loads and deserializes a chunk from its corresponding region file into memory.
        /// I/O operations and decompression are kept on background threads to prevent frame drops.
        /// </summary>
        /// <param name="chunkVoxelPos">The voxel-space world origin of the chunk.</param>
        /// <returns>The deserialized <see cref="ChunkData"/>, or null if the chunk does not exist on disk.</returns>
        public async Task<ChunkData> LoadChunkAsync(Vector2Int chunkVoxelPos)
        {
            // Run I/O on background thread
            return await Task.Run(() =>
            {
                (Vector2Int regionCoord, int lx, int lz) = _codec.ChunkVoxelPosToRegionAddress(chunkVoxelPos);
                RegionFile region = GetRegion(regionCoord);

                (byte[] data, CompressionAlgorithm algorithm) = region.LoadChunkData(lx, lz);
                if (data == null)
                {
                    Debug.Log($"[LoadChunkAsync] Chunk at voxelPos {chunkVoxelPos} not on disk -> Will be generated");
                    return null;
                }

                // Deserialize (Expensive CPU, kept on background thread)
                ChunkData chunk = ChunkSerializer.Deserialize(data, algorithm, chunkVoxelPos);

                if (chunk == null)
                {
                    Debug.LogWarning($"[LoadChunkAsync] Chunk at voxelPos {chunkVoxelPos} deserialization failed -> Will be (re-)generated");
                    return null;
                }

                return chunk;
            });
        }

        /// <summary>
        /// Synchronously serializes, compresses, and saves a chunk to its region file.
        /// This directly blocks the calling thread, rendering it suitable primarily for application shutdown logic.
        /// </summary>
        /// <param name="data">The chunk data object to persist.</param>
        public void SaveChunk(ChunkData data)
        {
            CompressionAlgorithm algorithm = World.Instance.settings.saveCompression;

            // Get buffer from pool to avoid GC allocation on main thread
            byte[] buffer = SerializationBufferPool.Get();
            try
            {
                // Serialize
                int length = ChunkSerializer.Serialize(data, buffer, algorithm);
                if (length <= 0)
                {
                    Debug.LogWarning($"[SaveChunk] Chunk at voxelPos {data.position} serialization returned 0 bytes");
                    return;
                }

                // Write to Region
                (Vector2Int regionCoord, int lx, int lz) = _codec.ChunkVoxelPosToRegionAddress(data.position);
                RegionFile region = GetRegion(regionCoord);

                region.SaveChunkData(lx, lz, buffer, length, algorithm);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveChunk] Failed to save chunk at voxelPos {data.position}: {e.Message}");
            }
            finally
            {
                SerializationBufferPool.Return(buffer);
            }
        }

        /// <summary>
        /// Async save. Snapshots chunk data on the calling thread, then serializes and writes on a ThreadPool thread.
        /// Includes CancellationToken support to safely abort on game quit.
        /// </summary>
        /// <param name="data">The chunk data to save.</param>
        /// <param name="cancellationToken">An optional cancellation token to abort the task.</param>
        /// <returns>A task representing the asynchronous save operation.</returns>
        public async Task SaveChunkAsync(ChunkData data, CancellationToken cancellationToken = default)
        {
            // 1. Get Preferred Algorithm from Global Settings
            CompressionAlgorithm algorithm = World.Instance.settings.saveCompression;

            // 2. Get Buffer
            byte[] buffer = SerializationBufferPool.Get();

            // 3. Create a thread-safe snapshot on the Main Thread (Zero GC via Pooling)
            ChunkData snapshot = CreateSerializationSnapshot(data);

            try
            {
                // Check token before expensive work
                if (cancellationToken.IsCancellationRequested) return;

                // 4. Offload serialization of the isolated snapshot to Thread Pool
                await Task.Run(() =>
                {
                    // Serialize
                    int length = ChunkSerializer.Serialize(snapshot, buffer, algorithm);
                    // Check token again before disk write to prevent writing partial/cancelled state
                    if (length <= 0 || cancellationToken.IsCancellationRequested) return;

                    // Write
                    (Vector2Int regionCoord, int lx, int lz) = _codec.ChunkVoxelPosToRegionAddress(snapshot.position);
                    RegionFile region = GetRegion(regionCoord);

                    region.SaveChunkData(lx, lz, buffer, length, algorithm);
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Expected during quit - safe to ignore
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveChunkAsync] Failed to save chunk at voxelPos {data.position}: {e.Message}");
            }
            finally
            {
                // Always return the buffer to the pool
                SerializationBufferPool.Return(buffer);
                // Return snapshot components back to the pool
                World.Instance.ChunkPool.ReturnChunkData(snapshot);
            }
        }

        /// <summary>
        /// Safely disposes all open region files, forcing their physical file streams to flush pending I/O bytes to the drive.
        /// Call this only when tearing down the storage manager.
        /// </summary>
        public void Dispose()
        {
            Debug.Log($"[ChunkStorageManager] Disposing {_regions.Count} region files...");
            foreach (Lazy<RegionFile> lazyRegion in _regions.Values)
            {
                // Only dispose if the file was actually opened
                if (lazyRegion.IsValueCreated)
                {
                    lazyRegion.Value.Dispose(); // This calls RegionFile.Dispose()
                }
            }

            _regions.Clear();
            Debug.Log("[ChunkStorageManager] All regions disposed.");
        }

        // -------------------------------------------------------------------------
        // Private helpers
        // -------------------------------------------------------------------------

        private RegionFile GetRegion(Vector2Int regionCoord)
        {
            return _regions.GetOrAdd(regionCoord, coord => new Lazy<RegionFile>(() =>
            {
                string path = Path.Combine(_saveFolderPath, $"r.{coord.x}.{coord.y}.bin");
                return new RegionFile(path);
            })).Value;
        }

        private static ChunkData CreateSerializationSnapshot(ChunkData source)
        {
            ChunkData snapshot = World.Instance.ChunkPool.GetChunkData(source.position);
            snapshot.NeedsInitialLighting = source.NeedsInitialLighting;

            // Copy Heightmap
            if (source.heightMap != null && snapshot.heightMap != null)
                Array.Copy(source.heightMap, snapshot.heightMap, source.heightMap.Length);

            // Correctly iterate the Section Array
            for (int i = 0; i < source.sections.Length; i++)
            {
                // Check specific section in source array
                if (source.sections[i] != null && !source.sections[i].IsEmpty)
                {
                    ChunkSection snapSec = World.Instance.ChunkPool.GetChunkSection();
                    snapSec.nonAirCount = source.sections[i].nonAirCount;

                    // Copy voxels
                    Array.Copy(source.sections[i].voxels, snapSec.voxels, 4096);

                    // Assign to snapshot array
                    snapshot.sections[i] = snapSec;
                }
            }

            // Queue copying (Locking is correct)
            lock (source.SunlightBfsQueue)
            {
                foreach (LightQueueNode item in source.SunlightBfsQueue) snapshot.SunlightBfsQueue.Enqueue(item);
            }

            lock (source.BlocklightBfsQueue)
            {
                foreach (LightQueueNode item in source.BlocklightBfsQueue) snapshot.BlocklightBfsQueue.Enqueue(item);
            }

            return snapshot;
        }
    }
}
