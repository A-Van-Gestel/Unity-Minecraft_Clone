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
        [ReadOnly]
        public string worldName;

        [ReadOnly]
        public int seed;

        [NonSerialized]
        public Dictionary<Vector2Int, ChunkData> chunks = new Dictionary<Vector2Int, ChunkData>();

        [NonSerialized]
        public HashSet<ChunkData> modifiedChunks = new HashSet<ChunkData>();

        #region Constructors

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

        #endregion

        #region Chunk Management

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

        /// Returns the global chunk coordinates for a given world position
        /// <param name="pos">The world position</param>
        public Vector2Int GetChunkCoordFor(Vector3 pos)
        {
            int x = Mathf.FloorToInt(pos.x / VoxelData.ChunkWidth) * VoxelData.ChunkWidth;
            int z = Mathf.FloorToInt(pos.z / VoxelData.ChunkWidth) * VoxelData.ChunkWidth;
            return new Vector2Int(x, z);
        }

        /// Returns the local voxel position in a chunk for a given world position
        /// <param name="pos">The world position</param>
        public Vector3Int GetLocalVoxelPositionInChunk(Vector3 pos)
        {
            Vector2Int chunkCoord = GetChunkCoordFor(pos);
            return new Vector3Int((int)(pos.x - chunkCoord.x), (int)pos.y, (int)(pos.z - chunkCoord.y));
        }

        #endregion

        #region Voxel Management

        public bool IsVoxelInWorld(Vector3 pos)
        {
            return pos.x is >= 0 and < VoxelData.WorldSizeInVoxels &&
                   pos.y is >= 0 and < VoxelData.ChunkHeight &&
                   pos.z is >= 0 and < VoxelData.WorldSizeInVoxels;
        }

        [CanBeNull]
        public VoxelState? GetVoxelState(Vector3 pos)
        {
            // If the voxel is outside the world, we don't need to do anything with it and return null.
            if (!IsVoxelInWorld(pos))
                return null;

            // Find out the global ChunkCoord value of our voxel's chunk.
            Vector2Int chunkCoord = GetChunkCoordFor(pos);

            // Check if the chunk exists.
            ChunkData chunk = RequestChunk(chunkCoord, false);

            if (chunk == null)
                return null;

            // Then create a Vector3Int with the position of our voxel *within* the chunk.
            Vector3Int voxelPos = GetLocalVoxelPositionInChunk(pos);

            // Then get the voxel in our chunk.
            return chunk.map[voxelPos.x, voxelPos.y, voxelPos.z];
        }

        public void SetVoxel(Vector3 pos, byte value, byte direction)
        {
            // If the voxel is outside the world, we don't need to do anything with it.
            if (!IsVoxelInWorld(pos))
                return;

            // Find out the global ChunkCoord value of our voxel's chunk.
            Vector2Int chunkCoord = GetChunkCoordFor(pos);

            // Check if the chunk exists. If not, create it.
            ChunkData chunk = RequestChunk(chunkCoord, true);

            // Then create a Vector3Int with the position of our voxel *within* the chunk.
            Vector3Int voxelPos = GetLocalVoxelPositionInChunk(pos);

            // Then set the voxel in our chunk.
            chunk.ModifyVoxel(voxelPos, value, direction);
        }

        /// Set light level of a voxel from a world position.
        public void SetLight(Vector3 pos, byte value)
        {
            if (!IsVoxelInWorld(pos))
                return;

            Vector2Int chunkCoord = GetChunkCoordFor(pos);
            ChunkData chunk = RequestChunk(chunkCoord, true); // Create chunk if it doesn't exist

            if (chunk == null) return;

            Vector3Int voxelPos = GetLocalVoxelPositionInChunk(pos);
            chunk.SetLight(voxelPos, value);
        }

        #endregion
    }
}