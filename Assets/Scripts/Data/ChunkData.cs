using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using Jobs;
using Jobs.BurstData;
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
        public uint[] map = new uint[VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth];


        [NonSerialized]
        [CanBeNull]
        public Chunk chunk;

        [NonSerialized]
        public bool isPopulated;

        // --- lighting ---
        [NonSerialized]
        public bool hasLightChangesToProcess = false;

        [NonSerialized]
        private Queue<LightQueueNode> _sunlightBfsQueue = new Queue<LightQueueNode>();

        [NonSerialized]
        private Queue<LightQueueNode> _blocklightBfsQueue = new Queue<LightQueueNode>();

        public int SunLightQueueCount => _sunlightBfsQueue.Count;
        public int BlockLightQueueCount => _blocklightBfsQueue.Count;


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
        public void Populate(NativeArray<uint> jobOutputMap)
        {
            jobOutputMap.CopyTo(map);
            isPopulated = true;

            World.Instance.worldData.modifiedChunks.Add(this);
        }

        #endregion

        #region Modifier Methods

        // --- Modifier Methods --
        public void ModifyVoxel(Vector3Int pos, byte newId, byte direction, bool immediateUpdate = false)
        {
            if (!IsVoxelInChunk(pos)) return;
            if (World.Instance is null) return;

            // Get the current state of the voxel from the flat voxel map
            int index = GetIndexFromPosition(pos.x, pos.y, pos.z);
            uint oldPackedData = map[index];
            byte oldId = BurstVoxelDataBitMapping.GetId(oldPackedData);

            if (oldId == newId) // No change if the block ID is the same
                return;

            // --- Capture Old State ---
            byte oldBlocklight = BurstVoxelDataBitMapping.GetBlocklight(oldPackedData);
            byte oldSunlight = BurstVoxelDataBitMapping.GetSunlight(oldPackedData);

            BlockType[] blockTypes = World.Instance.blockTypes;
            BlockType oldProps = blockTypes[oldId];

            // --- Update The Map ---
            // The new block's light level is initially set to its own emission value (usually 0 for non-light sources).
            // The LightingJob will then fill it with propagated light from neighbors.
            BlockType newProps = blockTypes[newId];
            uint newPackedData = BurstVoxelDataBitMapping.PackVoxelData(newId, 0, newProps.lightEmission, direction);
            map[index] = newPackedData;

            // --- Queue Lighting Updates ---
            // 1. Queue the modified block itself for both channels.
            //    This tells the LightingJob to handle any light removal at this position.
            AddToSunLightQueue(pos, oldSunlight);
            AddToBlockLightQueue(pos, oldBlocklight);

            // 2. If opacity changed, we must also trigger a top-down rescan for the entire column.
            if (newProps.opacity != oldProps.opacity)
            {
                World.Instance.worldData.QueueSunlightRecalculation(new Vector2Int(pos.x + position.x, pos.z + position.y));
            }

            // --- Notify World of Changes for Mesh Rebuilds ---
            World.Instance.NotifyChunkModified(this.position, pos);

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
        /// Entry point for any block light updates.
        /// </summary>
        public void AddToBlockLightQueue(Vector3Int localPos, byte oldLightLevel)
        {
            if (World.Instance.settings.enableLighting)
            {
                _blocklightBfsQueue.Enqueue(new LightQueueNode { position = localPos, oldLightLevel = oldLightLevel });
                hasLightChangesToProcess = true;
            }
        }

        /// <summary>
        /// Entry point for any sunlight updates.
        /// </summary>
        public void AddToSunLightQueue(Vector3Int localPos, byte oldLightLevel)
        {
            if (World.Instance.settings.enableLighting)
            {
                _sunlightBfsQueue.Enqueue(new LightQueueNode { position = localPos, oldLightLevel = oldLightLevel });
                hasLightChangesToProcess = true;
            }
        }

        /// <summary>
        /// Method to get the block light queue as a NativeQueue for the job
        /// </summary>
        public NativeQueue<LightQueueNode> GetBlocklightQueueForJob(Allocator allocator)
        {
            var nativeQueue = new NativeQueue<LightQueueNode>(allocator);

            // Dequeue each item from the managed queue and enqueue it into the native one.
            while (BlockLightQueueCount > 0)
            {
                nativeQueue.Enqueue(_blocklightBfsQueue.Dequeue());
            }

            // The managed queue is now empty and ready for new requests.
            return nativeQueue;
        }

        /// <summary>
        /// Method to get the sunlight queue as a NativeQueue for the job
        /// </summary>
        public NativeQueue<LightQueueNode> GetSunlightQueueForJob(Allocator allocator)
        {
            var nativeQueue = new NativeQueue<LightQueueNode>(allocator);

            // Dequeue each item from the managed queue and enqueue it into the native one.
            while (SunLightQueueCount > 0)
            {
                nativeQueue.Enqueue(_sunlightBfsQueue.Dequeue());
            }

            // The managed queue is now empty and ready for new requests.
            return nativeQueue;
        }

        public void RecalculateNaturalLight()
        {
            var mapData = new NativeArray<uint>(map, Allocator.TempJob);
            var blockTypes = World.Instance.GetBlockTypesJobData(Allocator.TempJob);
            var sunlitVoxels = new NativeList<Vector3Int>(Allocator.TempJob);
            var lightJob = new InitialSunlightJob
            {
                Map = mapData,
                BlockTypes = blockTypes,
                SunlightPropagationQueue = sunlitVoxels
            };


            // Schedule job to run on all X/Z columns
            JobHandle handle = lightJob.Schedule();
            handle.Complete();

            // Copy data back and dispose
            lightJob.Map.CopyTo(map);

            // After job completes, add all sunlit voxels to the queue.
            foreach (Vector3Int voxel in sunlitVoxels)
            {
                AddToSunLightQueue(voxel, 0); // The old light was 0 before this.
            }

            mapData.Dispose();
            blockTypes.Dispose();
            sunlitVoxels.Dispose();
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
        public NativeArray<uint> GetMapForJob(Allocator allocator)
        {
            return new NativeArray<uint>(map, allocator);
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
                uint packedData = map[index];
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
            uint packedData = map[GetIndexFromPosition(pos.x, pos.y, pos.z)];
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
                byte id = BurstVoxelDataBitMapping.GetId(map[index]);

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