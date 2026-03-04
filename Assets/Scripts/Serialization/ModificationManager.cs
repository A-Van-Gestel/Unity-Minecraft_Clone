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

        /// <summary>
        /// Registers a modification for a chunk that is not yet loaded.
        /// These mods will be applied immediately when the target chunk is loaded/generated.
        /// </summary>
        /// <param name="targetChunk">The coordinate of the chunk that will receive the modification.</param>
        /// <param name="mod">The voxel modification to queue.</param>
        public void AddPendingMod(ChunkCoord targetChunk, VoxelMod mod)
        {
            // Ensure dictionary key exists
            if (!_pendingMods.ContainsKey(targetChunk))
            {
                _pendingMods[targetChunk] = new List<VoxelMod>();
            }

            _pendingMods[targetChunk].Add(mod);
        }

        /// <summary>
        /// Attempts to retrieve and remove all pending voxel modifications for a specific chunk.
        /// </summary>
        /// <param name="coord">The chunk coordinate to query.</param>
        /// <param name="mods">The list of pending modifications, if any.</param>
        /// <returns>True if modifications were found; otherwise, false.</returns>
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

        /// <summary>
        /// Saves all pending modifications to disk.
        /// </summary>
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
        }

        /// <summary>
        /// Loads all pending modifications from disk.
        /// </summary>
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
        }
    }
}
