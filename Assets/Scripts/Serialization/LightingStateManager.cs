using System;
using System.Collections.Generic;
using System.IO;
using Data;
using UnityEngine;
using UnityEngine.Pool;

namespace Serialization
{
    /// <summary>
    /// Manages the persistence of pending lighting work for unloaded chunks: sunlight column
    /// recalculations (<c>pending_lighting.bin</c>) and cross-chunk blocklight modifications
    /// (<c>pending_blocklight.bin</c>) awaiting their target chunk.
    /// </summary>
    public class LightingStateManager
    {
        /// <summary>
        /// A persisted cross-chunk blocklight modification awaiting an unloaded target chunk.
        /// Mirrors the RGB payload of a job-emitted <c>LightModification</c>; replayed through
        /// <c>CrossChunkLightModApplier</c> when the chunk is loaded from disk.
        /// </summary>
        public struct PendingBlocklightMod
        {
            /// <summary>The red blocklight channel the modification wants to set (0-15).</summary>
            public byte R;

            /// <summary>The green blocklight channel the modification wants to set (0-15).</summary>
            public byte G;

            /// <summary>The blue blocklight channel the modification wants to set (0-15).</summary>
            public byte B;

            /// <summary>True when emitted by a darkness/removal pass (zero channels mean "remove";
            /// false means zero channels are merely "no contribution" and may never lower light).</summary>
            public bool IsRemoval;
        }

        // pending_blocklight.bin format version. The file is self-describing (unlike
        // pending_lighting.bin) so future layout changes can migrate it in isolation.
        private const byte BLOCKLIGHT_FILE_VERSION = 1;

        private readonly string _filePath;
        private readonly string _blocklightFilePath;

        // Pending sunlight recalculations for chunks that aren't loaded yet (or were saved while waiting).
        // Key: Chunk Coordinate
        // Value: List of LOCAL column coordinates (0-15, 0-15) stored as Vector2Int
        private readonly Dictionary<ChunkCoord, HashSet<Vector2Int>> _pendingRecalcs = new Dictionary<ChunkCoord, HashSet<Vector2Int>>();

        // Pending cross-chunk blocklight modifications for chunks that aren't loaded/populated yet.
        // Key: Chunk Coordinate; inner key: LOCAL voxel position. Last write per voxel wins —
        // every mod carries absolute target channel values, so a newer mod fully supersedes an
        // older one for the same voxel.
        private readonly Dictionary<ChunkCoord, Dictionary<Vector3Int, PendingBlocklightMod>> _pendingBlocklightMods = new Dictionary<ChunkCoord, Dictionary<Vector3Int, PendingBlocklightMod>>();

        public LightingStateManager(string worldName, bool useVolatilePath)
        {
            string folder = SaveSystem.GetSavePath(worldName, useVolatilePath);
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "pending_lighting.bin");
            _blocklightFilePath = Path.Combine(folder, "pending_blocklight.bin");
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
                    Debug.LogError($"[LightingStateManager] Invalid local column {col.ToString()} for chunk {chunkCoord.ToString()}. Must be 0-15!");
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
        /// Records a cross-chunk blocklight modification targeting an unloaded/unpopulated chunk so
        /// it can be replayed when the chunk is loaded from disk. Sunlight column recalculations
        /// cannot restore RGB data — without this store, blocklight removals (broken lamps) and
        /// uplifts that crossed into an unloaded chunk would be permanently lost (Bug 08, path 1).
        /// </summary>
        /// <param name="chunkCoord">The target chunk coordinate.</param>
        /// <param name="localPos">The LOCAL voxel position inside the target chunk.</param>
        /// <param name="r">The red blocklight channel the modification wants to set (0-15).</param>
        /// <param name="g">The green blocklight channel the modification wants to set (0-15).</param>
        /// <param name="b">The blue blocklight channel the modification wants to set (0-15).</param>
        /// <param name="isRemoval">True when emitted by a darkness/removal pass (zero channels mean "remove").</param>
        public void AddPendingBlocklight(ChunkCoord chunkCoord, Vector3Int localPos, byte r, byte g, byte b, bool isRemoval)
        {
            // VALIDATION: Ensure the position is truly local.
            if (localPos.x < 0 || localPos.x >= VoxelData.ChunkWidth ||
                localPos.y < 0 || localPos.y >= VoxelData.ChunkHeight ||
                localPos.z < 0 || localPos.z >= VoxelData.ChunkWidth)
            {
                Debug.LogError($"[LightingStateManager] Invalid local position {localPos.ToString()} for pending blocklight in chunk {chunkCoord.ToString()}.");
                return;
            }

            if (!_pendingBlocklightMods.TryGetValue(chunkCoord, out Dictionary<Vector3Int, PendingBlocklightMod> mods))
            {
                mods = DictionaryPool<Vector3Int, PendingBlocklightMod>.Get(); // POOLING
                _pendingBlocklightMods[chunkCoord] = mods;
            }

            // Guard against a placement mod overwriting a prior removal mod for the same voxel:
            // the removal's darkness wave must still run to clear the old lamp's propagated light;
            // the placement's uplift will be recomputed by SyncEmissionToLightArray on load since
            // the block's emission is baked into the packed uint data. A removal may always overwrite
            // a placement (the block is gone), and same-type overwrites keep the latest state.
            if (!isRemoval && mods.TryGetValue(localPos, out PendingBlocklightMod existing) && existing.IsRemoval)
                return;

            mods[localPos] = new PendingBlocklightMod { R = r, G = g, B = b, IsRemoval = isRemoval };
        }

        /// <summary>
        /// Attempts to retrieve and remove the pending cross-chunk blocklight modifications for a
        /// chunk. The caller takes ownership of the returned dictionary and must release it via
        /// <c>DictionaryPool&lt;Vector3Int, PendingBlocklightMod&gt;</c> after replaying.
        /// </summary>
        /// <param name="chunkCoord">The chunk coordinate to query.</param>
        /// <param name="mods">The pending modifications keyed by LOCAL voxel position, if any.</param>
        /// <returns>True if pending modifications were found; otherwise, false.</returns>
        public bool TryGetAndRemovePendingBlocklight(ChunkCoord chunkCoord, out Dictionary<Vector3Int, PendingBlocklightMod> mods)
        {
            return _pendingBlocklightMods.Remove(chunkCoord, out mods);
        }

        /// <summary>
        /// Discards any pending cross-chunk blocklight modifications for a chunk. Used when the
        /// chunk is freshly GENERATED (not loaded from disk): initial lighting recomputes all light
        /// from current neighbor data, so mods recorded while the chunk was absent are obsolete.
        /// </summary>
        /// <param name="chunkCoord">The chunk coordinate whose pending modifications are discarded.</param>
        public void DiscardPendingBlocklight(ChunkCoord chunkCoord)
        {
            if (_pendingBlocklightMods.Remove(chunkCoord, out Dictionary<Vector3Int, PendingBlocklightMod> mods))
            {
                DictionaryPool<Vector3Int, PendingBlocklightMod>.Release(mods);
            }
        }

        /// <summary>
        /// Saves the pending sunlight recalculation queues and pending blocklight modifications to disk.
        /// </summary>
        public void Save()
        {
            using (FileStream stream = new FileStream(_filePath, FileMode.Create))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
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

            SavePendingBlocklight();
        }

        /// <summary>
        /// Saves the pending cross-chunk blocklight modifications to <c>pending_blocklight.bin</c>.
        /// </summary>
        private void SavePendingBlocklight()
        {
            using FileStream stream = new FileStream(_blocklightFilePath, FileMode.Create);
            using BinaryWriter writer = new BinaryWriter(stream);

            writer.Write(BLOCKLIGHT_FILE_VERSION);
            writer.Write(_pendingBlocklightMods.Count);
            foreach (KeyValuePair<ChunkCoord, Dictionary<Vector3Int, PendingBlocklightMod>> kvp in _pendingBlocklightMods)
            {
                writer.Write(kvp.Key.X);
                writer.Write(kvp.Key.Z);
                writer.Write(kvp.Value.Count);

                foreach (KeyValuePair<Vector3Int, PendingBlocklightMod> mod in kvp.Value)
                {
                    // Local positions fit in bytes (x/z: 0-15, y: 0-127); channels are nibbles.
                    writer.Write((byte)mod.Key.x);
                    writer.Write((byte)mod.Key.y);
                    writer.Write((byte)mod.Key.z);
                    writer.Write(mod.Value.R);
                    writer.Write(mod.Value.G);
                    writer.Write(mod.Value.B);
                    writer.Write(mod.Value.IsRemoval);
                }
            }
        }

        /// <summary>
        /// Loads the pending sunlight recalculation queues and pending blocklight modifications from disk.
        /// </summary>
        public void Load()
        {
            LoadPendingColumns();
            LoadPendingBlocklight();
        }

        /// <summary>
        /// Loads the pending sunlight recalculation queues from <c>pending_lighting.bin</c>.
        /// </summary>
        private void LoadPendingColumns()
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
                Debug.LogWarning("[LightingStateManager] pending_lighting.bin was truncated. Some pending lighting updates may be lost, but the pool remains safe.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LightingStateManager] Error loading pending lighting: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads the pending cross-chunk blocklight modifications from <c>pending_blocklight.bin</c>.
        /// The file's absence is normal (worlds saved before the store existed, or nothing pending).
        /// </summary>
        private void LoadPendingBlocklight()
        {
            if (!File.Exists(_blocklightFilePath)) return;

            try
            {
                using FileStream stream = new FileStream(_blocklightFilePath, FileMode.Open);
                using BinaryReader reader = new BinaryReader(stream);

                byte version = reader.ReadByte();
                if (version != BLOCKLIGHT_FILE_VERSION)
                {
                    Debug.LogError($"[LightingStateManager] Unknown pending_blocklight.bin version {version.ToString()} (expected {BLOCKLIGHT_FILE_VERSION.ToString()}). Skipping load.");
                    return;
                }

                int chunkCount = reader.ReadInt32();
                for (int i = 0; i < chunkCount; i++)
                {
                    int x = reader.ReadInt32();
                    int z = reader.ReadInt32();
                    ChunkCoord chunkCoord = new ChunkCoord(x, z);

                    int modCount = reader.ReadInt32();
                    for (int m = 0; m < modCount; m++)
                    {
                        byte lx = reader.ReadByte();
                        byte ly = reader.ReadByte();
                        byte lz = reader.ReadByte();
                        byte r = reader.ReadByte();
                        byte g = reader.ReadByte();
                        byte b = reader.ReadByte();
                        bool isRemoval = reader.ReadBoolean();

                        // AddPendingBlocklight validates bounds (rejecting corrupted disk data),
                        // handles pooled inner dictionaries, and merges duplicate chunk entries.
                        AddPendingBlocklight(chunkCoord, new Vector3Int(lx, ly, lz), r, g, b, isRemoval);
                    }
                }
            }
            catch (EndOfStreamException)
            {
                Debug.LogWarning("[LightingStateManager] pending_blocklight.bin was truncated. Some pending blocklight modifications may be lost, but the pool remains safe.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LightingStateManager] Error loading pending blocklight: {ex.Message}");
            }
        }

        /// <summary>
        /// Safely releases all remaining pooled HashSets and Dictionaries.
        /// Call this when destroying the world to prevent pool leaks.
        /// </summary>
        public void Clear()
        {
            foreach (HashSet<Vector2Int> set in _pendingRecalcs.Values)
            {
                HashSetPool<Vector2Int>.Release(set);
            }

            _pendingRecalcs.Clear();

            foreach (Dictionary<Vector3Int, PendingBlocklightMod> mods in _pendingBlocklightMods.Values)
            {
                DictionaryPool<Vector3Int, PendingBlocklightMod>.Release(mods);
            }

            _pendingBlocklightMods.Clear();
        }
    }
}
