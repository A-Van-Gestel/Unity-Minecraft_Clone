using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using MyBox;
using UnityEngine;

namespace Data
{
    [Serializable]
    public class WorldData
    {
        [ReadOnly] public string worldName;
        [ReadOnly] public int seed;

        [NonSerialized]
        public Dictionary<Vector2Int, ChunkData> chunks = new Dictionary<Vector2Int, ChunkData>();

        [NonSerialized]
        public HashSet<ChunkData> modifiedChunks = new HashSet<ChunkData>();

        public WorldData(string worldName, int seed)
        {
            this.worldName = worldName;
            this.seed = seed;
        }

        public WorldData(WorldData wD)
        {
            worldName = wD.worldName;
            seed = wD.seed;
        }

        public ChunkData RequestChunk(Vector2Int coord, bool create)
        {
            ChunkData c;

            lock (World.Instance.ChunkUpdateThreadLock)
            {
                if (chunks.TryGetValue(coord, out ChunkData chunk))
                    c = chunk;
                else if (!create)
                    c = null;
                else
                {
                    LoadChunk(coord);
                    c = chunks[coord];
                }
            }

            return c;
        }

        public void LoadChunk(Vector2Int coord)
        {
            // Nothing needs to be loaded if the chunk is already loaded.
            if (chunks.ContainsKey(coord))
                return;

            // Load Chunk from File
            if (World.Instance.settings.loadSaveDataOnStartup)
            {
                ChunkData chunk = SaveSystem.LoadChunk(worldName, coord);
                if (chunk != null)
                {
                    chunks.Add(coord, chunk);
                    return;
                }
            }


            chunks.Add(coord, new ChunkData(coord));
            chunks[coord].Populate();
        }

        public bool IsVoxelInWorld(Vector3 pos)
        {
            return pos.x is >= 0 and < VoxelData.WorldSizeInVoxels &&
                   pos.y is >= 0 and < VoxelData.ChunkHeight &&
                   pos.z is >= 0 and < VoxelData.WorldSizeInVoxels;
        }

        public void SetVoxel(Vector3 pos, byte value, byte direction)
        {
            // If the voxel is outside the world, we don't need to do anything with it.
            if (!IsVoxelInWorld(pos))
                return;

            // Find out the ChunkCoord value of our voxel's chunk.
            int x = Mathf.FloorToInt(pos.x / VoxelData.ChunkWidth);
            int z = Mathf.FloorToInt(pos.z / VoxelData.ChunkWidth);

            // Then reverse that to get the position of the chunk.
            x *= VoxelData.ChunkWidth;
            z *= VoxelData.ChunkWidth;

            // Check if the chunk exists. If not, create it.
            ChunkData chunk = RequestChunk(new Vector2Int(x, z), true);

            // Then create a Vector3Int with the position of our voxel *within* the chunk.
            Vector3Int voxel = new Vector3Int((int)(pos.x - x), (int)pos.y, (int)(pos.z - z));

            // Then set the voxel in our chunk.
            chunk.ModifyVoxel(voxel, value, direction);
        }

        [CanBeNull]
        public VoxelState GetVoxel(Vector3 pos)
        {
            // If the voxel is outside the world, we don't need to do anything with it and return null.
            if (!IsVoxelInWorld(pos))
                return null;

            // Find out the ChunkCoord value of our voxel's chunk.
            int x = Mathf.FloorToInt(pos.x / VoxelData.ChunkWidth);
            int z = Mathf.FloorToInt(pos.z / VoxelData.ChunkWidth);

            // Then reverse that to get the position of the chunk.
            x *= VoxelData.ChunkWidth;
            z *= VoxelData.ChunkWidth;

            // Check if the chunk exists.
            ChunkData chunk = RequestChunk(new Vector2Int(x, z), false);

            if (chunk == null)
                return null;

            // Then create a Vector3Int with the position of our voxel *within* the chunk.
            Vector3Int voxel = new Vector3Int((int)(pos.x - x), (int)pos.y, (int)(pos.z - z));

            // Then get the voxel in our chunk.
            return chunk.map[voxel.x, voxel.y, voxel.z];
        }
    }
}