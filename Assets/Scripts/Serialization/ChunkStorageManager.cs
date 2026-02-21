using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Data;
using Serialization.Migration;
using UnityEngine;

namespace Serialization
{
    public class ChunkStorageManager
    {
        private readonly string _saveFolderPath;

        // Concurrent Dictionary with Lazy to ensure thread-safe, single initialization of RegionFiles
        private readonly ConcurrentDictionary<Vector2Int, Lazy<RegionFile>> _regions = new ConcurrentDictionary<Vector2Int, Lazy<RegionFile>>();

        public ChunkStorageManager(string worldName, bool useVolatilePath)
        {
            // Determine Save Path
            string basePath = useVolatilePath
                ? Path.Combine(Application.persistentDataPath, "Editor_Temp_Saves")
                : Path.Combine(Application.persistentDataPath, "Saves");

            _saveFolderPath = Path.Combine(basePath, worldName, "Region");

            if (!Directory.Exists(_saveFolderPath)) Directory.CreateDirectory(_saveFolderPath);
        }

        // Returns null if chunk not found on disk
        public async Task<ChunkData> LoadChunkAsync(Vector2Int chunkCoord)
        {
            // Run I/O on background thread
            return await Task.Run(() =>
            {
                // Accessing .Value triggers the file open if not already open
                RegionFile region = GetRegion(GetRegionCoord(chunkCoord));

                // Local coordinates inside the region (0-31)
                int lx = chunkCoord.x % 32;
                int lz = chunkCoord.y % 32;
                if (lx < 0) lx += 32;
                if (lz < 0) lz += 32;

                // Now receiving tuple containing raw bytes and algorithm used
                var (data, algorithm) = region.LoadChunkData(lx, lz);
                if (data == null)
                {
                    Debug.Log($"[LoadChunkAsync] Chunk {chunkCoord} not on disk -> Will be generated");
                    return null;
                }

                // Deserialize (Expensive CPU, kept on background thread)
                var chunk = ChunkSerializer.Deserialize(data, algorithm, chunkCoord);

                if (data == null)
                {
                    Debug.LogWarning($"[LoadChunkAsync] Chunk {chunkCoord} deserialization failed -> Will be (re-)generated");

                    return null;
                }

                return chunk;
            });
        }

        /// <summary>
        /// Synchronous version of SaveChunk. 
        /// </summary>
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
                    Debug.LogWarning($"[SaveChunk] Chunk {data.position} serialization returned 0 bytes");
                    return;
                }

                // Write to Region
                Vector2Int coord = data.position;
                RegionFile region = GetRegion(GetRegionCoord(coord));

                int lx = coord.x % 32;
                int lz = coord.y % 32;
                if (lx < 0) lx += 32;
                if (lz < 0) lz += 32;

                region.SaveChunkData(lx, lz, buffer, length, algorithm);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveChunk] Failed to save chunk {data.position}: {e.Message}");
            }
            finally
            {
                SerializationBufferPool.Return(buffer);
            }
        }

        /// <summary>
        /// Saves a chunk asynchronously.
        /// Includes CancellationToken support to safely abort if the game quits mid-operation.
        /// Returns a Task so the caller can await completion or catch errors if needed.
        /// </summary>
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
                    Vector2Int coord = snapshot.position;
                    RegionFile region = GetRegion(GetRegionCoord(coord));

                    int lx = coord.x % 32;
                    int lz = coord.y % 32;
                    if (lx < 0) lx += 32;
                    if (lz < 0) lz += 32;

                    region.SaveChunkData(lx, lz, buffer, length, algorithm);
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Expected during quit - safe to ignore
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveChunkAsync] Failed to save chunk {data.position}: {e.Message}");
            }
            finally
            {
                // Always return the buffer to the pool
                SerializationBufferPool.Return(buffer);
                // Return snapshot components back to the pool
                World.Instance.ChunkPool.ReturnChunkData(snapshot);
            }
        }

        private ChunkData CreateSerializationSnapshot(ChunkData source)
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
                foreach (var item in source.SunlightBfsQueue) snapshot.SunlightBfsQueue.Enqueue(item);
            }

            lock (source.BlocklightBfsQueue)
            {
                foreach (var item in source.BlocklightBfsQueue) snapshot.BlocklightBfsQueue.Enqueue(item);
            }

            return snapshot;
        }

        public void RunMigration(WorldMigration migration)
        {
            Debug.Log($"[RunMigration] Starting Migration: {migration.Description} (v{migration.SourceVersion} -> v{migration.TargetVersion})");

            // 4. Migrate Region Files (Chunks)
            string[] regionFiles = Directory.GetFiles(_saveFolderPath, "r.*.*.bin");

            foreach (string regionPath in regionFiles)
            {
                try
                {
                    // We open the region file manually here to process it
                    using var region = new RegionFile(regionPath);

                    // Gather all chunks first to avoid modifying the collection while iterating
                    foreach (Vector2Int localCoord in region.GetAllChunkCoords())
                    {
                        // Migration note: We currently only support migrating the raw GZip bytes in the base class.
                        // Future migrations might need to become Compression-aware.
                        // For now, LoadChunkData returns (byte[], algo), we discard algo for the generic migration
                        var (oldData, _) = region.LoadChunkData(localCoord.x, localCoord.y);
                        if (oldData == null) continue;

                        try
                        {
                            // Execute the migration logic on the raw bytes
                            byte[] newData = migration.MigrateChunk(oldData);

                            // Only write back if data actually changed
                            if (newData != oldData)
                            {
                                region.RawWriteChunk(localCoord.x, localCoord.y, newData);
                            }
                        }
                        catch (Exception e)
                        {
                            Debug.LogError($"[RunMigration] Failed to migrate chunk inside {Path.GetFileName(regionPath)} at {localCoord}: {e.Message}");
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[RunMigration] Failed to process region file {regionPath} for migration: {e.Message}");
                }
            }

            Debug.Log($"[RunMigration] Migration {migration.SourceVersion} -> {migration.TargetVersion} complete.");
        }

        private RegionFile GetRegion(Vector2Int regionCoord)
        {
            // Use Lazy to guarantee that the RegionFile constructor (which opens the file) 
            // runs exactly once per region coordinate, even if called concurrently.
            return _regions.GetOrAdd(regionCoord, coord => new Lazy<RegionFile>(() =>
            {
                string path = Path.Combine(_saveFolderPath, $"r.{coord.x}.{coord.y}.bin");
                return new RegionFile(path);
            })).Value;
        }

        private Vector2Int GetRegionCoord(Vector2Int chunkCoord)
        {
            return new Vector2Int(
                Mathf.FloorToInt(chunkCoord.x / 32f),
                Mathf.FloorToInt(chunkCoord.y / 32f)
            );
        }

        public void Dispose()
        {
            Debug.Log($"[ChunkStorageManager] Disposing {_regions.Count} region files...");

            foreach (var lazyRegion in _regions.Values)
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
    }
}
