using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using Jobs.BurstData;
using Unity.Collections;
using UnityEngine;

namespace Data
{
    [Serializable]
    public class WorldData
    {
        [MyBox.ReadOnly]
        public string worldName;

        [MyBox.ReadOnly]
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

            if (chunks.TryGetValue(coord, out ChunkData chunk))
                c = chunk;
            else if (!create)
                c = null;
            else
            {
                LoadChunk(coord);
                c = chunks[coord];
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

            // Chunk doesn't exist on disk.
            // We do NOT create it here. We add a "placeholder" ChunkData object.
            // The asynchronous job system is responsible for populating it.
            // This prevents race conditions.
            chunks.Add(coord, new ChunkData(coord));
        }

        // This method is called by a modification that needs a chunk which may not exist yet.
        // We can't populate it here, but we can make sure the placeholder exists so the mod can be queued.
        public void EnsureChunkExists(Vector3 pos)
        {
            if (!IsVoxelInWorld(pos)) return;
            Vector2Int chunkCoord = GetChunkCoordFor(pos);
            if (!chunks.ContainsKey(chunkCoord))
            {
                // Create the placeholder and schedule its generation
                chunks.Add(chunkCoord, new ChunkData(chunkCoord));
                World.Instance.ScheduleGeneration(new ChunkCoord(pos));
            }
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
            ChunkData chunkData = RequestChunk(chunkCoord, false);

            if (chunkData == null)
                return null;

            // Then create a Vector3Int with the position of our voxel *within* the chunk.
            Vector3Int voxelPos = GetLocalVoxelPositionInChunk(pos);

            // Then get the voxel in our chunk.
            return chunkData.GetState(voxelPos);
        }

        public void SetVoxel(Vector3 pos, byte value, byte direction)
        {
            // If the voxel is outside the world, we don't need to do anything with it.
            if (!IsVoxelInWorld(pos))
                return;

            // // Before requesting the chunk, ensure it exists or is being created.
            // EnsureChunkExists(pos);

            // 1. Find out the global ChunkCoord value of our voxel's chunk.
            Vector2Int chunkCoord = GetChunkCoordFor(pos);
            ChunkData chunkData = RequestChunk(chunkCoord, create: true); // Ensure chunk data exists

            // If the chunk data is still null (e.g., from a save file), something is wrong. But we proceed.
            if (chunkData == null)
            {
                Debug.LogError($"Failed to get or create chunk for SetVoxel at {pos}");
                return;
            }

            // Then create a Vector3Int with the position of our voxel *within* the chunk.
            Vector3Int voxelPos = GetLocalVoxelPositionInChunk(pos);

            // 2. Perform the actual modification within the chunk data.
            chunkData.ModifyVoxel(voxelPos, value, direction);
            // 3. The local chunk will ALWAYS need a mesh update.
            // ModifyVoxel already adds the chunkData to modifiedChunks for saving,
            // but the mesh rebuild request happens here.
            if (chunkData.chunk != null)
            {
                World.Instance.RequestChunkMeshRebuild(chunkData.chunk, true);
            }

            // 4. ***Crucially, check if on a border and update neighbors.***
            // This handles the mesh update even if lighting doesn't change.
    
            // Check X-axis borders
            if (voxelPos.x == 0)
            {
                Vector2Int neighborCoord = chunkCoord + new Vector2Int(-VoxelData.ChunkWidth, 0);
                QueueNeighborRebuild(neighborCoord);
            }
            else if (voxelPos.x == VoxelData.ChunkWidth - 1)
            {
                Vector2Int neighborCoord = chunkCoord + new Vector2Int(VoxelData.ChunkWidth, 0);
                QueueNeighborRebuild(neighborCoord);
            }

            // Check Z-axis borders
            if (voxelPos.z == 0)
            {
                Vector2Int neighborCoord = chunkCoord + new Vector2Int(0, -VoxelData.ChunkWidth);
                QueueNeighborRebuild(neighborCoord);
            }
            else if (voxelPos.z == VoxelData.ChunkWidth - 1)
            {
                Vector2Int neighborCoord = chunkCoord + new Vector2Int(0, VoxelData.ChunkWidth);
                QueueNeighborRebuild(neighborCoord);
            }
        }

        private void QueueNeighborRebuild(Vector2Int neighborV2Coord)
        {
            // Try to get the neighbor's chunk data.
            if (chunks.TryGetValue(neighborV2Coord, out ChunkData neighborData))
            {
                // If the chunk object exists, request a rebuild.
                if (neighborData.chunk != null)
                {
                    World.Instance.RequestChunkMeshRebuild(neighborData.chunk, true);
                }
            }
        }

        /// <summary>
        /// Sets the light value for a voxel without triggering an immediate mesh rebuild.
        /// Instead, it adds the affected chunk's coordinate to a "dirty" set.
        /// </summary>
        public void SetLightSilent(Vector3 pos, byte value, HashSet<ChunkCoord> dirtyChunks)
        {
            if (!IsVoxelInWorld(pos))
                return;

            Vector2Int chunkV2Coord = GetChunkCoordFor(pos);
            ChunkCoord chunkCoord = new ChunkCoord(chunkV2Coord.x / VoxelData.ChunkWidth, chunkV2Coord.y / VoxelData.ChunkWidth);

            // Use TryGetValue for performance and safety.
            if (chunks.TryGetValue(chunkV2Coord, out ChunkData chunk) && chunk.isPopulated)
            {
                Vector3Int voxelPos = GetLocalVoxelPositionInChunk(pos);
                int index = voxelPos.x + VoxelData.ChunkWidth * (voxelPos.y + VoxelData.ChunkHeight * voxelPos.z);
                ushort packedData = chunk.map[index];

                // No need to check for brightness, the calling code already does.
                chunk.map[index] = BurstVoxelDataBitMapping.SetLight(packedData, value);

                // Add the chunk coordinate to the set of chunks that need a rebuild.
                dirtyChunks.Add(chunkCoord);
            }
        }

        /// <summary>
        ///  Helper method to get the raw voxel map for jobs.
        /// </summary>
        /// <param name="coord"></param>
        /// <param name="allocator"></param>
        /// <returns>Jobs compatible array of voxels</returns>
        public NativeArray<ushort> GetChunkMapForJob(Vector2Int coord, Allocator allocator)
        {
            ChunkData chunk = RequestChunk(coord, false);
            if (chunk != null)
            {
                return chunk.GetMapForJob(allocator);
            }

            // Return an empty array if chunk doesn't exist.
            // The mesh job will check IsCreated.
            return new NativeArray<ushort>(0, allocator);
        }

        #endregion

        #region Lighting Management

        public void QueueLightUpdate(Vector3 globalPos, byte oldLightLevel = 0)
        {
            if (!IsVoxelInWorld(globalPos)) return;

            Vector2Int chunkV2Coord = GetChunkCoordFor(globalPos);

            if (chunks.TryGetValue(chunkV2Coord, out ChunkData chunkData) && chunkData.isPopulated)
            {
                // Add the *modified block's position* to the chunk's internal light queue.
                Vector3Int localPos = GetLocalVoxelPositionInChunk(globalPos);
                chunkData.AddToLightQueue(localPos, oldLightLevel);
            }
        }

        #endregion
    }
}