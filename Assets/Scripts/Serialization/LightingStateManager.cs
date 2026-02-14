using System.Collections.Generic;
using System.IO;
using UnityEngine;

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
        /// <param name="coord">The chunk coordinate.</param>
        /// <param name="localColumns">A set of Vector2Ints where x/y are local 0-15 coordinates.</param>
        public void AddPending(ChunkCoord coord, HashSet<Vector2Int> localColumns)
        {
            if (localColumns == null || localColumns.Count == 0) return;

            // VALIDATION: Ensure coordinates are truly local (0-15)
            foreach (var col in localColumns)
            {
                if (col.x < 0 || col.x >= VoxelData.ChunkWidth ||
                    col.y < 0 || col.y >= VoxelData.ChunkWidth)
                {
                    Debug.LogError($"[LightingStateManager] Invalid local column {col} for chunk {coord}. Must be 0-15!");
                }
            }

            if (!_pendingRecalcs.ContainsKey(coord))
            {
                _pendingRecalcs[coord] = new HashSet<Vector2Int>();
            }

            foreach (var col in localColumns)
            {
                _pendingRecalcs[coord].Add(col);
            }
        }

        public bool TryGetAndRemove(ChunkCoord coord, out HashSet<Vector2Int> localColumns)
        {
            if (_pendingRecalcs.Remove(coord, out localColumns))
            {
                return true;
            }

            return false;
        }

        public void Save()
        {
            using var stream = new FileStream(_filePath, FileMode.Create);
            using var writer = new BinaryWriter(stream);

            writer.Write(_pendingRecalcs.Count);
            foreach (var kvp in _pendingRecalcs)
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

        public void Load()
        {
            if (!File.Exists(_filePath)) return;

            using var stream = new FileStream(_filePath, FileMode.Open);
            using var reader = new BinaryReader(stream);

            int chunkCount = reader.ReadInt32();
            for (int i = 0; i < chunkCount; i++)
            {
                int x = reader.ReadInt32();
                int z = reader.ReadInt32();
                ChunkCoord coord = new ChunkCoord(x, z);

                int colCount = reader.ReadInt32();
                HashSet<Vector2Int> cols = new HashSet<Vector2Int>();

                for (int c = 0; c < colCount; c++)
                {
                    byte lx = reader.ReadByte();
                    byte lz = reader.ReadByte(); // Local Z
                    cols.Add(new Vector2Int(lx, lz));
                }

                _pendingRecalcs[coord] = cols;
            }
        }
    }
}
