using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using Jobs;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Pool;

namespace Data
{
    [Serializable]
    public class WorldData
    {
        [MyBox.ReadOnly]
        public string worldName;

        [MyBox.ReadOnly]
        public int seed;

        [MyBox.ReadOnly]
        public long creationDate;

        [NonSerialized]
        public Dictionary<Vector2Int, ChunkData> Chunks = new Dictionary<Vector2Int, ChunkData>();

        [NonSerialized]
        public HashSet<ChunkData> ModifiedChunks = new HashSet<ChunkData>();

        [NonSerialized]
        public Dictionary<Vector2Int, HashSet<Vector2Int>> SunlightRecalculationQueue = new Dictionary<Vector2Int, HashSet<Vector2Int>>();

        #region Constructors

        public WorldData(string worldName, int seed)
        {
            this.worldName = worldName;
            this.seed = seed;
            creationDate = DateTime.Now.Ticks;
        }

        public WorldData(WorldData wD)
        {
            worldName = wD.worldName;
            seed = wD.seed;
            creationDate = wD.creationDate;
        }

        #endregion

        #region Chunk Management

        public ChunkData RequestChunk(Vector2Int chunkVector2Coord, bool allowChunkDataCreation)
        {
            ChunkData c;

            if (Chunks.TryGetValue(chunkVector2Coord, out ChunkData chunk))
                c = chunk;
            else if (!allowChunkDataCreation)
                c = null;
            else
            {
                LoadChunk(chunkVector2Coord);
                c = Chunks[chunkVector2Coord];
            }

            return c;
        }

        public void LoadChunk(Vector2Int chunkVector2Coord)
        {
            // Nothing needs to be loaded if the chunk is already loaded.
            if (Chunks.ContainsKey(chunkVector2Coord))
                return;

            // Load Chunk from File
            if (World.Instance.settings.EnablePersistence)
            {
                // PHASE 3 TODO-old: Replace the legacy save-system code below with ChunkStorageManager.LoadChunkAsync
                // TODO-new: This was the original place where chunks where loaded from disk, I believe this is the correct place (eg: data related), but is currently moved into World class itself.
                /*
                ChunkData chunk = SaveSystem.LoadChunk(worldName, chunkVector2Coord);
                if (chunk != null)
                {
                    Chunks.Add(chunkVector2Coord, chunk);
                    return;
                }
                */
            }

            // Chunk doesn't exist on disk (or loading is disabled/not yet implemented).
            // We create a "placeholder" ChunkData object.
            // The asynchronous job system is responsible for populating it.
            Chunks.Add(chunkVector2Coord, World.Instance.ChunkPool.GetChunkData(chunkVector2Coord)); // Create placeholder using POOL
        }


        /// <summary>
        /// This method is called by a modification that needs a chunk which may not exist yet.
        /// We can't populate it here, but we can make sure the placeholder exists so the mod can be queued.
        /// </summary>
        /// <param name="worldPos">The world position</param>
        /// <returns>Boolean representing if chunk already existed (TRUE), or if a placeholder was created (FALSE) or outside the world (FALSE)</returns>
        public bool EnsureChunkExists(Vector3 worldPos)
        {
            // Outside the world, nothing to do.
            if (!IsVoxelInWorld(worldPos)) return false;

            Vector2Int chunkCoord = GetChunkCoordFor(worldPos);
            if (!Chunks.ContainsKey(chunkCoord))
            {
                // Create the placeholder
                Chunks.Add(chunkCoord, World.Instance.ChunkPool.GetChunkData(chunkCoord)); // Create placeholder using POOL
                return false;
            }

            return true;
        }

        /// Returns the global chunk coordinates for a given world position
        /// <param name="worldPos">The world position</param>
        public Vector2Int GetChunkCoordFor(Vector3 worldPos)
        {
            int x = Mathf.FloorToInt(worldPos.x / VoxelData.ChunkWidth) * VoxelData.ChunkWidth;
            int z = Mathf.FloorToInt(worldPos.z / VoxelData.ChunkWidth) * VoxelData.ChunkWidth;
            return new Vector2Int(x, z);
        }

        /// <summary>
        /// Get the local voxel position in a chunk for a given world position.
        /// </summary>
        /// <param name="worldPos">The world position</param>
        /// <returns>The local voxel position in a chunk for the given world position</returns>
        public Vector3Int GetLocalVoxelPositionInChunk(Vector3 worldPos)
        {
            Vector2Int chunkCoord = GetChunkCoordFor(worldPos);
            return new Vector3Int((int)(worldPos.x - chunkCoord.x), (int)worldPos.y, (int)(worldPos.z - chunkCoord.y));
        }

        #endregion

        #region Voxel Management

        /// <summary>
        /// Checks if a voxel is within the world bounds.
        /// </summary>
        /// <param name="worldPos">The world position</param>
        /// <returns>True if the voxel is within the world bounds, false otherwise.</returns>
        public bool IsVoxelInWorld(Vector3 worldPos)
        {
            return worldPos.x is >= 0 and < VoxelData.WorldSizeInVoxels &&
                   worldPos.y is >= 0 and < VoxelData.ChunkHeight &&
                   worldPos.z is >= 0 and < VoxelData.WorldSizeInVoxels;
        }

        /// <summary>
        /// Gets the voxel state at the given world position.
        /// </summary>
        /// <param name="worldPos">The world position</param>
        /// <returns>The `voxel state` at the given position or `null` if the voxel is `outside the world` or the `chunk doesn't exist`.</returns>
        [CanBeNull]
        public VoxelState? GetVoxelState(Vector3 worldPos)
        {
            // If the voxel is outside the world, we don't need to do anything with it and return null.
            if (!IsVoxelInWorld(worldPos))
                return null;

            // Find out the global ChunkCoord value of our voxel's chunk.
            Vector2Int chunkCoord = GetChunkCoordFor(worldPos);

            // Check if the chunk exists.
            ChunkData chunkData = RequestChunk(chunkCoord, false);

            if (chunkData == null)
                return null;

            // Then create a Vector3Int with the position of our voxel *within* the chunk.
            Vector3Int voxelPos = GetLocalVoxelPositionInChunk(worldPos);

            // Then get the voxel in our chunk.
            return chunkData.GetState(voxelPos);
        }

        /// <summary>
        /// Queues a mesh rebuild for the given chunk.
        /// </summary>
        /// <param name="chunkVector2Coord">The global chunk coordinates of the given chunk</param>
        private void QueueMeshRebuild(Vector2Int chunkVector2Coord)
        {
            // Try to get the chunk's data.
            if (Chunks.TryGetValue(chunkVector2Coord, out ChunkData chunkData))
            {
                // If the chunk object exists, request a rebuild.
                if (chunkData.Chunk != null)
                {
                    World.Instance.RequestChunkMeshRebuild(chunkData.Chunk, true);
                }
            }
        }

        /// <summary>
        /// Helper method to get the raw voxel map for jobs.
        /// </summary>
        /// <param name="chunkVector2Coord">The global chunk coordinates of the given chunk</param>
        /// <param name="allocator">The allocator to use for the native array</param>
        /// <returns>Jobs compatible array of voxels</returns>
        public NativeArray<uint> GetChunkMapForJob(Vector2Int chunkVector2Coord, Allocator allocator)
        {
            ChunkData chunk = RequestChunk(chunkVector2Coord, false);

            // Allocate the full height array for the job
            var jobArray = new NativeArray<uint>(VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth, allocator);

            if (chunk != null)
            {
                // Copy sections into the flat native array
                int sectionSize = 16 * 16 * 16;
                for (int i = 0; i < chunk.sections.Length; i++)
                {
                    if (chunk.sections[i] != null)
                    {
                        // Fast native copy
                        NativeArray<uint>.Copy(
                            chunk.sections[i].voxels, 0,
                            jobArray, i * sectionSize,
                            sectionSize);
                    }
                    // If null, the jobArray already contains 0 (Air) from initialization
                }
            }

            return jobArray;
        }

        #endregion

        #region Lighting Management

        /// <summary>
        /// Queues a light update for the given voxel.
        /// </summary>
        /// <param name="worldPos">The world position of the voxel</param>
        /// <param name="oldLightLevel">The old light level of the voxel (Defaults to `0`)</param>
        /// <param name="channel">The light channel to update (Defaults to `Block Channel`)</param>
        public void QueueLightUpdate(Vector3 worldPos, byte oldLightLevel = 0, LightChannel channel = LightChannel.Block)
        {
            if (!IsVoxelInWorld(worldPos)) return;

            Vector2Int chunkV2Coord = GetChunkCoordFor(worldPos);

            if (Chunks.TryGetValue(chunkV2Coord, out ChunkData chunkData) && chunkData.IsPopulated)
            {
                // Add the *modified block's position* to the chunk's internal light queue.
                Vector3Int localPos = GetLocalVoxelPositionInChunk(worldPos);
                if (channel == LightChannel.Block)
                    chunkData.AddToBlockLightQueue(localPos, oldLightLevel);
                else
                    chunkData.AddToSunLightQueue(localPos, oldLightLevel);

                // Mark the target chunk as needing a lighting update.
                chunkData.HasLightChangesToProcess = true;
            }
            else
            {
                // If chunk is unloaded, tell ModManager to mark this area as dirty.
                // We don't have exact block tracking for unloaded chunks, so we mark the *Column* for recalculation.
                ChunkCoord coord = new ChunkCoord(chunkV2Coord.x / VoxelData.ChunkWidth, chunkV2Coord.y / VoxelData.ChunkWidth);

                // Calculate local column (0-15)
                Vector3Int localPos = GetLocalVoxelPositionInChunk(worldPos);
                Vector2Int localCol = new Vector2Int(localPos.x, localPos.z);

                // OPTIMIZATION: Use pool for the temporary set passed to AddPending
                HashSet<Vector2Int> tempSet = HashSetPool<Vector2Int>.Get();
                tempSet.Add(localCol);

                // Add to persistent manager
                World.Instance.LightingStateManager.AddPending(coord, tempSet);

                // AddPending copies the elements into its own set, so we can immediately release this temp set
                HashSetPool<Vector2Int>.Release(tempSet);
            }
        }

        /// <summary>
        /// Queues a sunlight recalculation for the given column.
        /// </summary>
        /// <param name="columnPos">The column position</param>
        public void QueueSunlightRecalculation(Vector2Int columnPos)
        {
            Vector2Int chunkPos = GetChunkCoordFor(new Vector3(columnPos.x, 0, columnPos.y));

            // OPTIMIZATION: Grab from the global pool
            if (!SunlightRecalculationQueue.TryGetValue(chunkPos, out HashSet<Vector2Int> columns))
            {
                columns = HashSetPool<Vector2Int>.Get();
                SunlightRecalculationQueue[chunkPos] = columns;
            }

            columns.Add(columnPos);

            // Mark the target chunk as needing a lighting update.
            if (Chunks.TryGetValue(chunkPos, out ChunkData chunkData))
            {
                chunkData.HasLightChangesToProcess = true;
            }
        }

        #endregion
    }
}
