using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Data;
using Serialization.Migration;
using UnityEngine;

namespace Serialization
{
    public class ChunkStorageManager
    {
        // ... existing code ...
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

                if (data == null) return null;

                // Deserialize (Expensive CPU, kept on background thread)
                return ChunkSerializer.Deserialize(data, algorithm);
            });
        }

        /// <summary>
        /// Saves a chunk asynchronously.
        /// Returns a Task so the caller can await completion or catch errors if needed.
        /// </summary>
        public async Task SaveChunkAsync(ChunkData data)
        {
            // 1. Get Preferred Algorithm from Global Settings
            CompressionAlgorithm algorithm = World.Instance.settings.saveCompression;

            // 2. Serialize using that algorithm
            byte[] buffer = SerializationBufferPool.Get();
            int length = ChunkSerializer.Serialize(data, buffer, algorithm);

            Vector2Int coord = data.position;

            // 3. Offload Write to Disk
            await Task.Run(() =>
            {
                try
                {
                    RegionFile region = GetRegion(GetRegionCoord(coord));

                    int lx = coord.x % 32;
                    int lz = coord.y % 32;
                    if (lx < 0) lx += 32;
                    if (lz < 0) lz += 32;

                    region.SaveChunkData(lx, lz, buffer, length, algorithm);
                }
                catch (Exception e)
                {
                    Debug.LogError($"Failed to save chunk {coord}: {e.Message}");
                }
                finally
                {
                    // Return buffer to pool
                    SerializationBufferPool.Return(buffer);
                }
            });
        }

        public void RunMigration(WorldMigration migration)
        {
            Debug.Log($"Starting Migration: {migration.Description} (v{migration.SourceVersion} -> v{migration.TargetVersion})");

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
                            Debug.LogError($"Failed to migrate chunk inside {Path.GetFileName(regionPath)} at {localCoord}: {e.Message}");
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"Failed to process region file {regionPath} for migration: {e.Message}");
                }
            }

            Debug.Log($"Migration {migration.SourceVersion} -> {migration.TargetVersion} complete.");
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
            foreach (var lazyRegion in _regions.Values)
            {
                // Only dispose if the file was actually opened
                if (lazyRegion.IsValueCreated)
                {
                    lazyRegion.Value.Dispose();
                }
            }

            _regions.Clear();
        }
    }
}
