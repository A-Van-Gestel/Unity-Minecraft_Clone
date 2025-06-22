using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using Jobs;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Data
{
    [Serializable]
    public class ChunkData
    {
        // The global position of the chunk. ie, (16, 16) NOT (1, 1). We want to be able to access
        // it as a Vector2Int, but Vector2Int's are not serialized so we won't be able
        // to save them. So we'll store them as int's.
        private int _x;
        private int _y;

        public Vector2Int position
        {
            get => new Vector2Int(_x, _y);
            set
            {
                _x = value.x;
                _y = value.y;
            }
        }

        [HideInInspector]
        public ushort[] map = new ushort[VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth];


        [NonSerialized]
        [CanBeNull]
        public Chunk chunk;

        [NonSerialized]
        public bool isPopulated;

        // --- lighting ---
        [NonSerialized]
        public bool hasLightChangesToProcess = false;

        [NonSerialized]
        private Queue<LightQueueNode> _lightBfsQueue = new Queue<LightQueueNode>();

        public int LightQueueCount => _lightBfsQueue.Count;


        #region Constructors and Initializers

        public ChunkData(Vector2Int pos)
        {
            position = pos;
        }

        public ChunkData(int x, int y)
        {
            _x = x;
            _y = y;
        }

        /// Populate the chunk with voxels from the world generator.
        public void Populate(NativeArray<ushort> jobOutputMap)
        {
            jobOutputMap.CopyTo(map);
            isPopulated = true;

            // // We still need to run the lighting calculation
            // if (World.Instance.settings.enableLighting)
            // {
            //     RecalculateNaturalLight();
            // }
            // else
            // {
            //     // If lighting is off, just set every block to full brightness.
            //     for (int i = 0; i < map.Length; i++)
            //     {
            //         map[i] = VoxelData.SetLight(map[i], 15);
            //     }
            // }

            World.Instance.worldData.modifiedChunks.Add(this);
        }

        #endregion

        #region Modifier Methods

        // --- Modifier Methods --
        public void ModifyVoxel(Vector3Int pos, byte id, byte direction, bool immediateUpdate = false)
        {
            if (!IsVoxelInChunk(pos)) return;
            if (World.Instance is null) return;

            // Get the current state of the voxel from the flat voxel map
            int index = GetIndexFromPosition(pos.x, pos.y, pos.z);
            ushort oldPackedData = map[index];
            byte oldId = VoxelData.GetId(oldPackedData);

            if (oldId == id) // No change if the block ID is the same
                return;

            // --- Critical Data Capture ---
            // Capture the old light level BEFORE modifying the map
            byte oldLightLevel = VoxelData.GetLight(oldPackedData);

            BlockType[] blockTypes = World.Instance.blockTypes;
            BlockType oldProps = blockTypes[oldId];
            BlockType newProps = blockTypes[id];

            // --- Update The Map ---
            // The new block's light level is initially set to its own emission value (usually 0 for non-light sources).
            // The LightingJob will then fill it with propagated light from neighbors.
            byte newLightLevel = 0; // In a full system, this would be newProps.lightEmission
            ushort newPackedData = VoxelData.PackVoxelData(id, newLightLevel, direction);
            map[index] = newPackedData;

            // --- Handle Lighting Updates ---
            if (World.Instance.settings.enableLighting)
            {
                // 1. Queue the MODIFIED block itself for a light update. This is always needed.
                AddToLightQueue(pos, oldLightLevel);

                // 2. Queue all 6 direct neighbors for a light update. This handles ambient light spread.
                for (int i = 0; i < 6; i++)
                {
                    Vector3Int neighborLocalPos = pos + VoxelData.FaceChecks[i];
                    VoxelState? neighborState = GetState(neighborLocalPos);
                    if (neighborState.HasValue)
                    {
                        Vector3 globalNeighborPos = new Vector3(neighborLocalPos.x + position.x, neighborLocalPos.y, neighborLocalPos.z + position.y);
                        World.Instance.worldData.QueueLightUpdate(globalNeighborPos, neighborState.Value.light);
                    }
                }

                // 3. If solidity changed, trigger a full re-evaluation of the sunlight in that column.
                if (oldProps.isSolid != newProps.isSolid)
                {
                    // This is simple and robust. It recalculates the entire column from the top down,
                    // automatically handling both adding and removing sunlight correctly.
                    CastSunlight(pos.x, pos.z, VoxelData.ChunkHeight - 1);
                }
            }

            // --- Handle Active Block and Saving ---
            if (newProps.isActive)
                chunk?.AddActiveVoxel(pos);
            else if (oldProps.isActive)
                chunk?.RemoveActiveVoxel(pos);

            World.Instance.worldData.modifiedChunks.Add(this);
        }

        #endregion

        // --- Lighting Methods ---

        #region Ligting Methods

        /// <summary>
        /// Entry point for any light updates.
        /// </summary>
        public void AddToLightQueue(Vector3Int localPos, byte oldLightLevel)
        {
            if (World.Instance.settings.enableLighting)
            {
                _lightBfsQueue.Enqueue(new LightQueueNode { position = localPos, oldLightLevel = oldLightLevel });
                hasLightChangesToProcess = true;
            }
        }

        /// <summary>
        /// Method to get the light queue as a NativeQueue for the job
        /// </summary>
        public NativeQueue<LightQueueNode> GetLightQueueForJob(Allocator allocator)
        {
            var nativeQueue = new NativeQueue<LightQueueNode>(allocator);

            // Dequeue each item from the managed queue and enqueue it into the native one.
            while (LightQueueCount > 0)
            {
                nativeQueue.Enqueue(_lightBfsQueue.Dequeue());
            }

            // The managed queue is now empty and ready for new requests.
            return nativeQueue;
        }

        // /// Sets the light value at a local position and handles propagation.
        // public void SetLight(Vector3Int pos, byte lightValue)
        // {
        //     if (!IsVoxelInChunk(pos))
        //     {
        //         // If the position is outside this chunk, Delegate to WorldData for cross-chunk lighting
        //         Vector3 globalPos = new Vector3(pos.x + position.x, pos.y, pos.z + position.y);
        //         World.Instance.worldData.SetLight(globalPos, lightValue);
        //         return;
        //     }
        //     
        //     // --- The "Disabled Lighting" override ---
        //     if (!World.Instance.settings.enableLighting)
        //     {
        //         // If lighting is disabled, force the light level to 15 and stop.
        //         // We only perform the write if it's not already 15 to avoid unnecessary updates.
        //         int flatIndex = GetIndexFromPosition(pos.x, pos.y, pos.z);
        //         if (VoxelData.GetLight(map[flatIndex]) != 15)
        //         {
        //             map[flatIndex] = VoxelData.SetLight(map[flatIndex], 15);
        //
        //             // Only add to update queue if a chunk object actually exists.
        //             if (chunk != null)
        //             {
        //                 World.Instance.RequestChunkMeshRebuild(this.chunk);
        //             }
        //         }
        //         return; // Return early to skip all propagation logic.
        //     }
        //     // --- End of "Disabled Lighting" logic. ---
        //
        //     int index = GetIndexFromPosition(pos.x, pos.y, pos.z);
        //     ushort currentStatePacked = map[index];
        //     byte oldLight = VoxelData.GetLight(currentStatePacked);
        //
        //     // Don't propagate if the light level is not actually increasing.
        //     if (oldLight <= lightValue) return; // No change needed.
        //
        //     // Update the light value in the struct and write it back.
        //     map[index] = VoxelData.SetLight(currentStatePacked, lightValue);
        //
        //     // Only add to update queue if a chunk object actually exists.
        //     if (this.chunk != null) 
        //     {
        //         // Recalculate which chunks need updating based on light changes.
        //         // This is a complex problem, a simple solution is to always update.
        //         World.Instance.RequestChunkMeshRebuild(this.chunk);
        //     }
        //
        //     // For a full lighting system, you would add this position to a light propagation queue.
        //     // For now, this implementation is a simplified but functional version.
        //     // A key aspect of a full system is that darkness (light removal) also needs to propagate.
        //     if (lightValue > 1)
        //     {
        //         PropagateLight(pos, lightValue);
        //     }
        // }

        public void RecalculateNaturalLight()
        {
            var mapData = new NativeArray<ushort>(map, Allocator.TempJob);
            var blockTypes = World.Instance.GetBlockTypesJobData(Allocator.TempJob);
            var sunlitVoxels = new NativeList<Vector3Int>(Allocator.TempJob);
            var lightJob = new NaturalLightJob
            {
                map = mapData,
                blockTypes = blockTypes,
                sunlitVoxels = sunlitVoxels
            };


            // Schedule job to run on all X/Z columns
            JobHandle handle = lightJob.Schedule();
            handle.Complete();

            // Copy data back and dispose
            lightJob.map.CopyTo(map);

            // After job completes, add all sunlit voxels to the queue.
            foreach (Vector3Int voxel in sunlitVoxels)
            {
                AddToLightQueue(voxel, 0); // The old light was 0 before this.
            }

            mapData.Dispose();
            blockTypes.Dispose();
            sunlitVoxels.Dispose();
        }

        // /// Propagates light outwards from a given local position.
        // public void PropagateLight(Vector3Int pos, byte lightValue)
        // {
        //     // Do nothing if lighting is disabled
        //     if (!World.Instance.settings.enableLighting) return;
        //
        //     // The light that will be cast to neighbors is one less than the current block's light.
        //     byte castLight = (byte)(lightValue - 1);
        //     if (castLight < 1) return;
        //
        //     for (int i = 0; i < 6; i++)
        //     {
        //         Vector3Int neighbourPos = pos + VoxelData.FaceChecks[i];
        //         VoxelState? neighbourState = GetState(neighbourPos);
        //
        //         if (neighbourState.HasValue)
        //         {
        //             // Only spread light into blocks that are darker than what we are casting.
        //             // Also consider the neighbor's opacity.
        //             byte neighborOpacity = neighbourState.Value.Properties.opacity;
        //             byte lightAfterOpacity = (byte)Mathf.Max(0, castLight - neighborOpacity);
        //             
        //             if (lightAfterOpacity > neighbourState.Value.light)
        //             {
        //                 // SetLight will handle the recursive propagation call.
        //                 SetLight(neighbourPos, lightAfterOpacity);
        //             }
        //         }
        //     }
        // }

        // public void RecalculateNaturalLightForColumn(int x, int z)
        // {
        //     // Use a HashSet to avoid queueing the same neighbor multiple times.
        //     var neighborsToUpdate = new HashSet<Vector3Int>();
        //     
        //     bool obstructed = false;
        //     // Loop from the top of the chunk down
        //     for (int y = VoxelData.ChunkHeight - 1; y >= 0; y--)
        //     {
        //         Vector3Int localPos = new Vector3Int(x, y, z);
        //         int mapIndex = GetIndexFromPosition(x, y, z);
        //         ushort packedData = map[mapIndex];
        //
        //         // Capture the light level BEFORE any changes.
        //         byte oldLight = VoxelData.GetLight(packedData);
        //         byte newLight;
        //
        //         if (obstructed)
        //         {
        //             newLight = 0;
        //         }
        //         else
        //         {
        //             // Check opacity of the block at the current position
        //             if (World.Instance.blockTypes[VoxelData.GetId(packedData)].opacity > 0)
        //             {
        //                 newLight = 0;
        //                 obstructed = true; // Sunlight is now blocked for all blocks below.
        //             }
        //             else
        //             {
        //                 newLight = 15; // Full sunlight
        //             }
        //         }
        //
        //         // If the light level changed, update the map and queue propagation
        //         if (oldLight != newLight)
        //         {
        //             map[mapIndex] = VoxelData.SetLight(packedData, newLight);
        //             AddToLightQueue(localPos, oldLight); 
        //             
        //             // We must also check the horizontal neighbors of this now-changed block.
        //             // This tells the lighting system to propagate light INWARDS from the sides.
        //             neighborsToUpdate.Add(localPos + Vector3Int.right);
        //             neighborsToUpdate.Add(localPos + Vector3Int.left);
        //             neighborsToUpdate.Add(localPos + new Vector3Int(0,0,1));
        //             neighborsToUpdate.Add(localPos + new Vector3Int(0,0,-1));
        //         }
        //     }
        //     
        //     // Now, iterate through the unique set of neighbors and queue them for an update.
        //     foreach (Vector3Int neighborPos in neighborsToUpdate)
        //     {
        //         // We must handle cases where the neighbor is outside this chunk.
        //         VoxelState? neighborState = GetState(neighborPos);
        //         if (neighborState.HasValue)
        //         {
        //             // We must use the GLOBAL position to queue the update.
        //             Vector3 globalNeighborPos = new Vector3(neighborPos.x + position.x, neighborPos.y, neighborPos.z + position.y);
        //             World.Instance.worldData.QueueLightUpdate(globalNeighborPos, neighborState.Value.light);
        //         }
        //     }
        // }

        /// <summary>
        /// Authoritative method to update a sunlight column after a block's solidity has changed.
        /// This should be called from ModifyVoxel.
        /// </summary>
        /// <param name="pos">The local position of the block that was changed.</param>
        /// <param name="wasSolid">True if the block at this position WAS solid and is now transparent.</param>
        private void UpdateSunlightAfterModification(Vector3Int pos, bool wasSolid)
        {
            // CASE 1: A block was REMOVED (or became transparent). We might need to ADD sunlight.
            if (wasSolid)
            {
                // First, check if the path to the sky is ACTUALLY clear from the block ABOVE the one we just removed.
                // This correctly handles the case from Bug Example 02 (digging under a 2-block thick roof).
                if (!IsSunlightObstructedAbove(pos.x, pos.z, pos.y))
                {
                    // The path is clear! Cast sunlight down from the position of the now-transparent block.
                    CastSunlight(pos.x, pos.z, pos.y);
                }
                // If the path is not clear, we don't cast sunlight. The regular lighting job will
                // propagate ambient light from neighbors into the new space.
            }
            // CASE 2: A block was ADDED (or became solid). We need to REMOVE sunlight.
            else
            {
                // Start removing sunlight from the block just BELOW the one we just placed.
                RemoveSunlight(pos.x, pos.z, pos.y - 1);
            }
        }

        /// <summary>
        /// Finds all blocks in a column that should be sunlit and queues them for an update.
        /// This is called when a block is REMOVED.
        /// </summary>
        private void CastSunlight(int x, int z, int startY)
        {
            // Start from the top of the column to ensure we are correctly propagating from a valid source.
            for (int y = VoxelData.ChunkHeight - 1; y >= 0; y--)
            {
                Vector3Int localPos = new Vector3Int(x, y, z);
                int mapIndex = GetIndexFromPosition(x, y, z);
                ushort packedData = map[mapIndex];

                // Check if we hit an opaque block.
                if (World.Instance.blockTypes[VoxelData.GetId(packedData)].opacity > 0)
                {
                    // We hit a solid block. Everything below this point is now dark.
                    // We must now trigger a REMOVAL pass from this point downwards.
                    RemoveSunlight(x, z, y - 1);
                    return; // Stop the casting pass.
                }

                // If we reach here, the block is transparent.
                byte oldLight = VoxelData.GetLight(packedData);

                // If it's not already at full sunlight, update it.
                if (oldLight < 15)
                {
                    map[mapIndex] = VoxelData.SetLight(packedData, 15);
                    // Queue this change. The LightingJob will handle propagating this new light *horizontally*.
                    AddToLightQueue(localPos, oldLight);
                }
            }
        }

        /// <summary>
        /// Finds all sunlit blocks in a column and queues them for a lighting REMOVAL update.
        /// This is called when a block is ADDED.
        /// </summary>
        private void RemoveSunlight(int x, int z, int startY)
        {
            // Loop down from the block that is now in shadow.
            for (int y = startY; y >= 0; y--)
            {
                Vector3Int localPos = new Vector3Int(x, y, z);
                int mapIndex = GetIndexFromPosition(x, y, z);
                ushort packedData = map[mapIndex];

                byte oldLight = VoxelData.GetLight(packedData);

                // Only remove light if it was previously lit.
                if (oldLight > 0)
                {
                    map[mapIndex] = VoxelData.SetLight(packedData, 0);
                    // Queue this removal. The LightingJob will handle propagating darkness horizontally
                    // and telling neighbors to re-light the area with their own ambient light.
                    AddToLightQueue(localPos, oldLight);
                }

                // If we hit an opaque block, we can stop, because everything below it should have already
                // been dark (or will be updated by the standard lighting propagation).
                if (World.Instance.blockTypes[VoxelData.GetId(packedData)].opacity > 0)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Checks the column from the top of the chunk down to a specific Y level to see if sunlight is obstructed.
        /// </summary>
        /// <param name="x">Local X coordinate of the column.</param>
        /// <param name="z">Local Z coordinate of the column.</param>
        /// <param name="startY">The Y level to check down to (exclusive).</param>
        /// <returns>True if an opaque block is found above startY, otherwise false.</returns>
        private bool IsSunlightObstructedAbove(int x, int z, int startY)
        {
            // Loop from the absolute top of the chunk down to the block just ABOVE our starting point.
            for (int y = VoxelData.ChunkHeight - 1; y > startY; y--)
            {
                ushort packedData = map[GetIndexFromPosition(x, y, z)];
                // If we find any block with opacity, the sun is blocked.
                if (World.Instance.blockTypes[VoxelData.GetId(packedData)].opacity > 0)
                {
                    return true; // Obstruction found.
                }
            }

            return false; // Path to sky is clear.
        }

        # endregion


        // --- Helper Methods ---

        #region Helper Methods

        /// Get the index of a voxel to access it in the flat voxel map from a 3D local chunk position.
        private int GetIndexFromPosition(int x, int y, int z)
        {
            return x + VoxelData.ChunkWidth * (y + VoxelData.ChunkHeight * z);
        }

        /// Jobs helper method for providing data to jobs
        public NativeArray<ushort> GetMapForJob(Allocator allocator)
        {
            return new NativeArray<ushort>(map, allocator);
        }

        /// Check if a local voxel position is within the bounds of this chunk.
        public bool IsVoxelInChunk(int x, int y, int z)
        {
            return x is >= 0 and < VoxelData.ChunkWidth &&
                   y is >= 0 and < VoxelData.ChunkHeight &&
                   z is >= 0 and < VoxelData.ChunkWidth;
        }

        /// Check if voxel is in chunk from local position.
        public bool IsVoxelInChunk(Vector3Int pos)
        {
            return IsVoxelInChunk(pos.x, pos.y, pos.z);
        }

        /// Gets a VoxelState struct from a local position.
        /// Handles lookups that are outside this chunk by asking the world.
        public VoxelState? GetState(Vector3Int pos)
        {
            if (IsVoxelInChunk(pos.x, pos.y, pos.z))
            {
                int index = GetIndexFromPosition(pos.x, pos.y, pos.z);
                ushort packedData = map[index];
                return new VoxelState(packedData);
            }

            // If it's not in this chunk, ask the world.
            Vector3 globalPos = new Vector3(pos.x + position.x, pos.y, pos.z + position.y);
            return World.Instance.worldData.GetVoxelState(globalPos);
        }

        /// Gets a VoxelState struct from a local position
        /// NOTE: Make sure to check if voxel is in chunk first.
        public VoxelState VoxelFromV3Int(Vector3Int pos)
        {
            ushort packedData = map[GetIndexFromPosition(pos.x, pos.y, pos.z)];
            return new VoxelState(packedData);
        }

        /// <summary>
        /// Gets the highest voxel in a column in the chunk of the given position.
        /// If no solid voxels are found, returns the world height.
        /// </summary>
        /// <param name="pos">Local position</param>
        /// <returns></returns>
        public Vector3Int GetHighestVoxel(Vector3Int pos)
        {
            const int yMax = VoxelData.ChunkHeight - 1;
            int x = pos.x;
            int z = pos.z;

            for (int y = yMax; y > 0; y--)
            {
                int index = GetIndexFromPosition(x, y, z);
                byte id = VoxelData.GetId(map[index]);

                if (World.Instance.blockTypes[id].isSolid)
                {
                    return new Vector3Int(x, y, z);
                }
            }

            return new Vector3Int(x, yMax, z);
        }

        #endregion
    }

    public struct LightQueueNode
    {
        public Vector3Int position;
        public byte oldLightLevel;
    }
}