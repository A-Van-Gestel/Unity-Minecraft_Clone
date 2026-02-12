using System.Collections.Generic;
using System.IO;
using Data;
using UnityEngine;

namespace Serialization
{
    public class ModificationManager
    {
        private readonly string _filePath;

        // Modifications targeting chunks that aren't loaded yet.
        // Key: Chunk Coordinate
        // Value: List of mods for that chunk
        private readonly Dictionary<ChunkCoord, List<VoxelMod>> _pendingMods = new Dictionary<ChunkCoord, List<VoxelMod>>();

        // Pending sunlight recalculations for chunks that aren't loaded yet (or were saved while waiting).
        // Key: Chunk Coordinate
        // Value: List of LOCAL column coordinates (0-15, 0-15) stored as Vector2Int
        private readonly Dictionary<ChunkCoord, HashSet<Vector2Int>> _pendingLightUpdates = new Dictionary<ChunkCoord, HashSet<Vector2Int>>();

        public ModificationManager(string worldName, bool useVolatilePath)
        {
            // Determine Save Path
            string basePath = useVolatilePath
                ? Path.Combine(Application.persistentDataPath, "Editor_Temp_Saves")
                : Path.Combine(Application.persistentDataPath, "Saves");

            string folder = Path.Combine(basePath, worldName);
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "pending_mods.bin");
        }

        #region Voxel Mods

        // TODO: Currently unused and should be implemented correctly
        public void AddPendingMod(ChunkCoord targetChunk, VoxelMod mod)
        {
            if (!_pendingMods.ContainsKey(targetChunk))
            {
                _pendingMods[targetChunk] = new List<VoxelMod>();
            }

            _pendingMods[targetChunk].Add(mod);
        }

        public bool TryGetModsForChunk(ChunkCoord coord, out List<VoxelMod> mods)
        {
            if (_pendingMods.Remove(coord, out mods))
            {
                // We found mods. We should remove them from the pending list 
                // because they are about to be applied to the chunk.
                return true;
            }

            return false;
        }

        #endregion

        #region Lighting Updates

        /// <summary>
        /// Adds a set of local column coordinates that need sunlight recalculation to the pending store.
        /// </summary>
        /// <param name="targetChunk">The chunk coordinate.</param>
        /// <param name="localColumns">A set of Vector2Ints where x/y are local 0-15 coordinates.</param>
        /// TODO: Currently unused and should be implemented correctly
        public void AddPendingLightUpdates(ChunkCoord targetChunk, HashSet<Vector2Int> localColumns)
        {
            if (localColumns == null || localColumns.Count == 0) return;

            if (!_pendingLightUpdates.ContainsKey(targetChunk))
            {
                _pendingLightUpdates[targetChunk] = new HashSet<Vector2Int>();
            }

            foreach (var col in localColumns)
            {
                _pendingLightUpdates[targetChunk].Add(col);
            }
        }

        public bool TryGetLightUpdatesForChunk(ChunkCoord coord, out HashSet<Vector2Int> localColumns)
        {
            if (_pendingLightUpdates.Remove(coord, out localColumns))
            {
                return true;
            }

            return false;
        }

        #endregion

        public void Save()
        {
            using var stream = new FileStream(_filePath, FileMode.Create);
            using var writer = new BinaryWriter(stream);

            // --- 1. Save Voxel Mods ---
            writer.Write(_pendingMods.Count);
            foreach (var kvp in _pendingMods)
            {
                writer.Write(kvp.Key.X);
                writer.Write(kvp.Key.Z);
                writer.Write(kvp.Value.Count);

                foreach (var mod in kvp.Value)
                {
                    // Serialize VoxelMod
                    writer.Write(mod.GlobalPosition.x);
                    writer.Write(mod.GlobalPosition.y);
                    writer.Write(mod.GlobalPosition.z);
                    writer.Write(mod.ID);
                    writer.Write(mod.Orientation);
                    writer.Write(mod.FluidLevel);
                    // We don't save "ImmediateUpdate" flag as it's a runtime priority thing
                }
            }

            // --- 2. Save Lighting Updates ---
            writer.Write(_pendingLightUpdates.Count);
            foreach (var kvp in _pendingLightUpdates)
            {
                writer.Write(kvp.Key.X);
                writer.Write(kvp.Key.Z);
                writer.Write(kvp.Value.Count);

                foreach (Vector2Int col in kvp.Value)
                {
                    // Local columns are 0-15, so we can safely use bytes to save space
                    writer.Write((byte)col.x);
                    writer.Write((byte)col.y);
                }
            }
        }

        public void Load()
        {
            if (!File.Exists(_filePath)) return;

            using var stream = new FileStream(_filePath, FileMode.Open);
            using var reader = new BinaryReader(stream);

            // --- 1. Load Voxel Mods ---
            int chunkCount = reader.ReadInt32();
            for (int i = 0; i < chunkCount; i++)
            {
                int x = reader.ReadInt32();
                int z = reader.ReadInt32();
                ChunkCoord coord = new ChunkCoord(x, z);

                int modCount = reader.ReadInt32();
                List<VoxelMod> mods = new List<VoxelMod>(modCount);

                for (int m = 0; m < modCount; m++)
                {
                    Vector3Int pos = new Vector3Int(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                    ushort id = reader.ReadUInt16();
                    byte orient = reader.ReadByte();
                    byte fluid = reader.ReadByte();

                    mods.Add(new VoxelMod(pos, id)
                    {
                        Orientation = orient,
                        FluidLevel = fluid
                    });
                }

                _pendingMods[coord] = mods;
            }

            // --- 2. Load Lighting Updates ---
            // Check if stream still has data (for backward compatibility if we update format later)
            if (stream.Position < stream.Length)
            {
                int lightChunkCount = reader.ReadInt32();
                for (int i = 0; i < lightChunkCount; i++)
                {
                    int x = reader.ReadInt32();
                    int z = reader.ReadInt32();
                    ChunkCoord coord = new ChunkCoord(x, z);

                    int colCount = reader.ReadInt32();
                    HashSet<Vector2Int> cols = new HashSet<Vector2Int>();

                    for (int c = 0; c < colCount; c++)
                    {
                        byte lx = reader.ReadByte();
                        byte ly = reader.ReadByte(); // Local Z
                        cols.Add(new Vector2Int(lx, ly));
                    }

                    _pendingLightUpdates[coord] = cols;
                }
            }
        }
    }
}
