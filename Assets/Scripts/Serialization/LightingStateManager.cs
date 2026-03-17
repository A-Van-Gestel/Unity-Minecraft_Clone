using System;
using System.Collections.Generic;
using System.IO;
using Data;
using UnityEngine;
using UnityEngine.Pool;

namespace Serialization
{
    /// <summary>
    /// Manages the persistence of pending sunlight recalculations for unloaded chunks.
    /// </summary>
    public class LightingStateManager
    {
        private readonly string _filePath;

        // Pending sunlight recalculations for chunks that aren't loaded yet (or were saved while waiting).
        // Key: Chunk Coordinate
        // Value: List of LOCAL column coordinates (0-15, 0-15) stored as Vector2Int
        private readonly Dictionary<ChunkCoord, HashSet<Vector2Int>> _pendingRecalcs = new Dictionary<ChunkCoord, HashSet<Vector2Int>>();

        public LightingStateManager(string worldName, bool useVolatilePath)
        {
            string basePath = useVolatilePath
                ? Path.Combine(Application.persistentDataPath, "Editor_Temp_Saves")
                : Path.Combine(Application.persistentDataPath, "Saves");

            string folder = Path.Combine(basePath, worldName);
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "lighting_pending.bin");
        }

        /// <summary>
        /// Adds a set of local column coordinates that need sunlight recalculation to the pending store.
        /// </summary>
        /// <param name="chunkCoord">The chunk coordinate.</param>
        /// <param name="localColumns">A set of Vector2Ints where x/y are local 0-15 coordinates.</param>
        public void AddPending(ChunkCoord chunkCoord, HashSet<Vector2Int> localColumns)
        {
            if (localColumns == null || localColumns.Count == 0) return;

            // VALIDATION: Ensure coordinates are truly local (0-15)
            foreach (Vector2Int col in localColumns)
            {
                if (col.x < 0 || col.x >= VoxelData.ChunkWidth ||
                    col.y < 0 || col.y >= VoxelData.ChunkWidth)
                {
                    Debug.LogError($"[LightingStateManager] Invalid local column {col} for chunk {chunkCoord}. Must be 0-15!");
                }
            }

            if (!_pendingRecalcs.TryGetValue(chunkCoord, out HashSet<Vector2Int> existingSet))
            {
                existingSet = HashSetPool<Vector2Int>.Get(); // POOLING
                _pendingRecalcs[chunkCoord] = existingSet;
            }

            foreach (Vector2Int col in localColumns)
            {
                existingSet.Add(col);
            }
        }

        /// <summary>
        /// Attempts to retrieve and remove the pending local columns for sunlight recalculation in a specific chunk.
        /// </summary>
        /// <param name="chunkCoord">The chunk coordinate to query.</param>
        /// <param name="localColumns">A set of local 0-15 coordinates needing recalculation, if any.</param>
        /// <returns>True if pending columns were found; otherwise, false.</returns>
        public bool TryGetAndRemove(ChunkCoord chunkCoord, out HashSet<Vector2Int> localColumns)
        {
            if (_pendingRecalcs.Remove(chunkCoord, out localColumns))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Saves the pending sunlight recalculation queues to disk.
        /// </summary>
        public void Save()
        {
            using FileStream stream = new FileStream(_filePath, FileMode.Create);
            using BinaryWriter writer = new BinaryWriter(stream);

            writer.Write(_pendingRecalcs.Count);
            foreach (KeyValuePair<ChunkCoord, HashSet<Vector2Int>> kvp in _pendingRecalcs)
            {
                writer.Write(kvp.Key.X);
                writer.Write(kvp.Key.Z);
                writer.Write(kvp.Value.Count);

                foreach (Vector2Int col in kvp.Value)
                {
                    // Local columns are always 0-15, so byte is sufficient (optimizes disk space)
                    writer.Write((byte)col.x);
                    writer.Write((byte)col.y);
                }
            }
        }

        /// <summary>
        /// Loads the pending sunlight recalculation queues from disk.
        /// </summary>
        public void Load()
        {
            if (!File.Exists(_filePath)) return;

            try
            {
                using FileStream stream = new FileStream(_filePath, FileMode.Open);
                using BinaryReader reader = new BinaryReader(stream);

                int chunkCount = reader.ReadInt32();
                for (int i = 0; i < chunkCount; i++)
                {
                    int x = reader.ReadInt32();
                    int z = reader.ReadInt32();
                    ChunkCoord chunkCoord = new ChunkCoord(x, z);

                    int colCount = reader.ReadInt32();
                    HashSet<Vector2Int> cols = HashSetPool<Vector2Int>.Get(); // POOLED

                    // Track if the dictionary has assumed responsibility for releasing this memory.
                    bool ownershipTransferred = false;

                    try
                    {
                        for (int c = 0; c < colCount; c++)
                        {
                            byte lx = reader.ReadByte();
                            byte lz = reader.ReadByte(); // Local Z

                            // Inline validation to reject corrupted disk data
                            // (Note: byte is always >= 0, so only upper bounds are checked)
                            if (lx >= VoxelData.ChunkWidth || lz >= VoxelData.ChunkWidth)
                            {
                                Debug.LogError($"[LightingStateManager] Invalid local column ({lx}, {lz}) loaded for chunk {chunkCoord}. Skipping.");
                                continue;
                            }

                            cols.Add(new Vector2Int(lx, lz));
                        }

                        // Do not store empty sets if all loaded columns failed validation.
                        if (cols.Count > 0)
                        {
                            // Prevent orphaned HashSets if a corrupted file contains duplicate chunk coordinates.
                            if (_pendingRecalcs.TryGetValue(chunkCoord, out HashSet<Vector2Int> existing))
                            {
                                existing.UnionWith(cols);
                                // We leave ownershipTransferred as false so `cols` gets released in the finally block.
                            }
                            else
                            {
                                _pendingRecalcs[chunkCoord] = cols; // Transfer ownership to the dictionary
                                ownershipTransferred = true;
                            }
                        }
                    }
                    finally
                    {
                        // EXCEPTION SAFETY: If the read fails mid-loop, or if the set was empty/merged,
                        // ownership was never transferred. Return the temporary set safely to the pool.
                        if (!ownershipTransferred)
                        {
                            HashSetPool<Vector2Int>.Release(cols);
                        }
                    }
                }
            }
            catch (EndOfStreamException)
            {
                Debug.LogWarning("[LightingStateManager] lighting_pending.bin was truncated. Some pending lighting updates may be lost, but the pool remains safe.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LightingStateManager] Error loading pending lighting: {ex.Message}");
            }
        }

        /// <summary>
        /// Safely releases all remaining pooled HashSets.
        /// Call this when destroying the world to prevent pool leaks.
        /// </summary>
        public void Clear()
        {
            foreach (HashSet<Vector2Int> set in _pendingRecalcs.Values)
            {
                HashSetPool<Vector2Int>.Release(set);
            }

            _pendingRecalcs.Clear();
        }
    }
}
