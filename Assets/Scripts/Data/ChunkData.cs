using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Helpers;
using JetBrains.Annotations;
using Jobs.BurstData;
using Unity.Collections;
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
        public ChunkSection[] sections; // For 128 height, this array has length 8.

        /// <summary>
        /// The heightmap for this chunk. Stores the Y-level of the highest opaque block for each column.
        /// </summary>
        public byte[] heightMap = new byte[VoxelData.ChunkWidth * VoxelData.ChunkWidth];


        [NonSerialized]
        [CanBeNull]
        public Chunk Chunk;

        [NonSerialized]
        public bool IsPopulated;

        // --- lighting ---
        /// <summary>
        /// A transient flag indicating that the chunk's data has been populated, but it has not yet undergone its initial, mandatory lighting calculation.
        /// </summary>
        [NonSerialized]
        public bool NeedsInitialLighting = false;

        /// <summary>
        /// A transient flag indicating that the chunk has pending general light changes that need to be processed on the main thread.
        /// </summary>
        [NonSerialized]
        public bool HasLightChangesToProcess = false;

        /// <summary>
        /// A transient flag indicating that a lighting job for this chunk has completed, but its results (e.g., cross-chunk modifications) are still pending processing on the main thread.
        /// </summary>
        [NonSerialized]
        public bool IsAwaitingMainThreadProcess = false;

        [NonSerialized]
        private readonly Queue<LightQueueNode> _sunlightBfsQueue = new Queue<LightQueueNode>();

        [NonSerialized]
        private readonly Queue<LightQueueNode> _blocklightBfsQueue = new Queue<LightQueueNode>();

        public int SunLightQueueCount => _sunlightBfsQueue.Count;
        public int BlockLightQueueCount => _blocklightBfsQueue.Count;

        public Queue<LightQueueNode> SunlightBfsQueue => _sunlightBfsQueue;
        public Queue<LightQueueNode> BlocklightBfsQueue => _blocklightBfsQueue;


        #region Constructors and Initializers

        public ChunkData(Vector2Int pos)
        {
            position = pos;
            InitializeSections();
        }

        public ChunkData(int x, int y)
        {
            _x = x;
            _y = y;
            InitializeSections();
        }

        private void InitializeSections()
        {
            int sectionCount = VoxelData.ChunkHeight / ChunkMath.SECTION_SIZE;
            sections = new ChunkSection[sectionCount];
        }

        /// Populate the chunk with voxels from the world generator.
        public void Populate(NativeArray<uint> jobOutputMap, NativeArray<byte> jobOutputHeightMap)
        {
            // Transfer data from the flat job array to the sections
            PopulateFromFlattened(jobOutputMap);

            jobOutputHeightMap.CopyTo(heightMap);
            IsPopulated = true;

            World.Instance.worldData.ModifiedChunks.Add(this);
        }

        /// <summary>
        /// Populates the ChunkSections from a flat NativeArray.
        /// Used when receiving data back from Jobs (Generation, Lighting).
        /// </summary>
        public void PopulateFromFlattened(NativeArray<uint> flatData)
        {
            int sectionVoxelCount = ChunkMath.SECTION_SIZE * ChunkMath.SECTION_SIZE * ChunkMath.SECTION_SIZE;

            for (int i = 0; i < sections.Length; i++)
            {
                int startIndex = i * sectionVoxelCount;

                // Create or reset section
                if (sections[i] == null) sections[i] = new ChunkSection();
                else
                {
                    // Reset data if reusing (though currently we construct new chunks usually)
                    Array.Clear(sections[i].voxels, 0, sections[i].voxels.Length);
                }

                bool hasData = false;

                // Copy data first
                // Note: We could optimize by checking for non-zero in flatData before copying,
                // but NativeArray.Copy is very fast.
                NativeArray<uint>.Copy(flatData, startIndex, sections[i].voxels, 0, sectionVoxelCount);

                // Check if slice was empty to determine if we keep the section
                for (int j = 0; j < sectionVoxelCount; j++)
                {
                    if (sections[i].voxels[j] != 0)
                    {
                        hasData = true;
                        break;
                    }
                }

                if (!hasData)
                {
                    sections[i] = null; // Discard empty section
                }
                else
                {
                    // Recalculate counts so IsFullySolid works correctly for meshing
                    sections[i].RecalculateCounts(World.Instance.blockTypes);
                }
            }
        }
        
        /// <summary>
        /// Populates this ChunkData instance with data from a loaded save file.
        /// </summary>
        public void PopulateFromSave(ChunkData loadedData)
        {
            this.heightMap = loadedData.heightMap;
            this.sections = loadedData.sections;
            
            // Copy Queues
            // We move the queues from the loaded object (temp) to this object (live)
            foreach(var node in loadedData.SunlightBfsQueue) this.AddToSunLightQueue(node.Position, node.OldLightLevel);
            foreach(var node in loadedData.BlocklightBfsQueue) this.AddToBlockLightQueue(node.Position, node.OldLightLevel);

            // If loaded data had flags, transfer them
            if (loadedData.HasLightChangesToProcess) this.HasLightChangesToProcess = true;

            // Recalculate counts
            if (World.Instance != null)
            {
                foreach(var section in sections) 
                    section?.RecalculateCounts(World.Instance.blockTypes);
            }

            this.IsPopulated = true;
            
            // Note: We don't copy position/x/y as they should match.
        }

        #endregion

        #region Modifier Methods

        // --- Modifier Methods --
        /// <summary>
        /// Modifies a single voxel within the chunk based on the data provided in a VoxelMod struct.
        /// This is the authoritative method for all block changes in the world. It handles:
        /// - Updating the voxel map with the new state (ID, orientation, fluid level).
        /// - Maintaining the chunk's heightmap for lighting calculations.
        /// - Queuing lighting updates for the modified block and its neighbors.
        /// - Notifying the World that the chunk has been modified for mesh and active voxel updates.
        /// </summary>
        /// <param name="localPos">The position of the voxel within this chunk (local coordinates).</param>
        /// <param name="mod">The VoxelMod struct containing all data for the new voxel state.</param>
        public void ModifyVoxel(Vector3Int localPos, VoxelMod mod)
        {
            if (!IsVoxelInChunk(localPos)) return;
            if (World.Instance is null) return;

            // Get the current state of the voxel using the new Section system
            uint oldPackedData = GetVoxel(localPos.x, localPos.y, localPos.z);

            // --- Create the new voxel data from the modification ---
            // The new block's light level is initially set to its own emission value (usually 0 for non-light sources).
            // The LightingJob will then fill it with propagated light from neighbors.
            BlockType newProps = World.Instance.blockTypes[mod.ID];
            uint newPackedData = BurstVoxelDataBitMapping.PackVoxelData(mod.ID, 0, newProps.lightEmission, mod.Orientation, mod.FluidLevel);

            // Check if the full voxel state has actually changed.
            if (oldPackedData == newPackedData)
                return;

            // --- Capture Old State for Lighting ---
            ushort oldId = BurstVoxelDataBitMapping.GetId(oldPackedData);
            byte oldBlocklight = BurstVoxelDataBitMapping.GetBlockLight(oldPackedData);
            byte oldSunlight = BurstVoxelDataBitMapping.GetSunLight(oldPackedData);
            BlockType oldProps = World.Instance.blockTypes[oldId];

            // --- Update The Map (Sections) ---
            SetVoxel(localPos.x, localPos.y, localPos.z, newPackedData, newProps, oldProps);

            // --- MAINTAIN HEIGHTMAP ---
            int heightmapIndex = localPos.x + VoxelData.ChunkWidth * localPos.z;
            byte currentHeight = heightMap[heightmapIndex];

            // Case 1: A light-obstructing block was placed ABOVE the current highest block.
            if (newProps.IsLightObstructing && localPos.y > currentHeight)
            {
                heightMap[heightmapIndex] = (byte)localPos.y;
            }
            // Case 2: The current highest light-obstructing block was removed or made fully transparent.
            else if (!newProps.IsLightObstructing && localPos.y == currentHeight)
            {
                // We need to scan downwards from here to find the NEW highest block.
                byte newHeight = 0;
                for (int y = localPos.y - 1; y >= 0; y--)
                {
                    uint checkPacked = GetVoxel(localPos.x, y, localPos.z);
                    ushort checkId = BurstVoxelDataBitMapping.GetId(checkPacked);
                    if (World.Instance.blockTypes[checkId].IsOpaque)
                    {
                        newHeight = (byte)y;
                        break; // Found the new highest block, stop scanning.
                    }
                }

                heightMap[heightmapIndex] = newHeight;
            }

            // --- Queue Lighting Updates ---

            // 1. Queue the modified block itself for light REMOVAL.
            AddToSunLightQueue(localPos, oldSunlight);
            AddToBlockLightQueue(localPos, oldBlocklight);

            // 2. "WAKE UP" NEIGHBORS to fill any new empty space with their light.
            for (int i = 0; i < 6; i++)
            {
                Vector3Int neighborPos = localPos + VoxelData.FaceChecks[i];
                if (IsVoxelInChunk(neighborPos))
                {
                    uint neighborPacked = GetVoxel(neighborPos.x, neighborPos.y, neighborPos.z);

                    byte neighborSunlight = BurstVoxelDataBitMapping.GetSunLight(neighborPacked);
                    if (neighborSunlight > 0)
                        AddToSunLightQueue(neighborPos, 0);

                    byte neighborBlocklight = BurstVoxelDataBitMapping.GetBlockLight(neighborPacked);
                    if (neighborBlocklight > 0)
                        AddToBlockLightQueue(neighborPos, 0);
                }
            }

            // 3. If opacity changed, queue a full vertical sunlight recalculation.
            if (newProps.opacity != oldProps.opacity)
            {
                World.Instance.worldData.QueueSunlightRecalculation(new Vector2Int(localPos.x + position.x, localPos.z + position.y));
            }

            // --- Notify World and Handle Active Voxels ---

            // Pass the immediateUpdate flag to the world so it can prioritize the mesh rebuild.
            World.Instance.NotifyChunkModified(position, localPos, mod.ImmediateUpdate);

            // If the chunk object exists, update its active voxel list immediately.
            // If not (e.g., during initial world gen), the active voxel scan in
            // OnDataPopulated() will handle finding this block later when the chunk is activated.
            if (Chunk != null)
            {
                if (newProps.isActive)
                    Chunk.AddActiveVoxel(localPos);
                else if (oldProps.isActive)
                    Chunk.RemoveActiveVoxel(localPos);
            }

            World.Instance.worldData.ModifiedChunks.Add(this);
        }

        #endregion

        // --- Chunk Section Methods ---

        #region Chunk Section Methods

        /// <summary>
        /// Sets the packed voxel data at the specified local coordinates.
        /// Automatically handles the creation of ChunkSections if they don't exist.
        /// </summary>
        /// <param name="x">Local X (0-15)</param>
        /// <param name="y">Local Y (0-ChunkHeight)</param>
        /// <param name="z">Local Z (0-15)</param>
        /// <param name="value">The packed uint data.</param>
        /// <param name="newBlockProperties">The properties of the block being set (can be null).</param>
        /// <param name="oldBlockProperties">The properties of the block being replaced (can be null).</param>
        public void SetVoxel(int x, int y, int z, uint value, [CanBeNull] BlockType newBlockProperties, [CanBeNull] BlockType oldBlockProperties)
        {
            int sectionY = y / ChunkMath.SECTION_SIZE;
            int localY = y % ChunkMath.SECTION_SIZE;

            // Create section if it doesn't exist (on write)
            if (sections[sectionY] == null)
            {
                // If writing "Air" to a null section, don't bother creating it
                if (value == 0) return;
                sections[sectionY] = new ChunkSection();
            }

            // Index logic: 16x16x16
            // Note: We manually calculate the local section index here because ChunkSection.Voxels is only 4096 long.
            // We cannot use ChunkMath.GetFlattenedIndex here because that returns the global index (e.g. 5000+).
            int index = x + (localY * ChunkMath.SECTION_SIZE) + (z * ChunkMath.SECTION_SIZE * ChunkMath.SECTION_SIZE);
            uint oldValue = sections[sectionY].voxels[index];

            // -- Update Counts --
            // Handle NonAirCount for optimization
            if (oldValue == 0 && value != 0) sections[sectionY].nonAirCount++;
            else if (oldValue != 0 && value == 0) sections[sectionY].nonAirCount--;

            // Handle OpaqueCount for meshing optimization
            bool isNewOpaque = newBlockProperties != null && newBlockProperties.IsOpaque;
            bool wasOldOpaque = oldBlockProperties != null && oldBlockProperties.IsOpaque;

            if (!wasOldOpaque && isNewOpaque) sections[sectionY].opaqueCount++;
            else if (wasOldOpaque && !isNewOpaque) sections[sectionY].opaqueCount--;

            // Set voxel
            sections[sectionY].voxels[index] = value;

            // Optional: If NonAirCount drops to 0, set Sections[sectionY] = null to free memory
        }

        /// <summary>
        /// Simplified SetVoxel for raw data setting where properties are unknown or assumed consistent (e.g. generation).
        /// Warning: This does NOT update OpaqueCount correctly. Use with caution or call RecalculateCounts afterwards.
        /// </summary>
        public void SetVoxel(int x, int y, int z, uint value)
        {
            SetVoxel(x, y, z, value, null, null);
        }

        /// <summary>
        /// Gets the packed voxel data at the specified local coordinates.
        /// Returns 0 (Air) if the ChunkSection is null.
        /// </summary>
        /// <param name="x">Local X (0-15)</param>
        /// <param name="y">Local Y (0-ChunkHeight)</param>
        /// <param name="z">Local Z (0-15)</param>
        /// <returns>The packed uint data.</returns>
        public uint GetVoxel(int x, int y, int z)
        {
            int sectionY = y / ChunkMath.SECTION_SIZE;

            // If section is null, it's implicitly Air
            if (sections[sectionY] == null) return 0;

            int localY = y % ChunkMath.SECTION_SIZE;
            int index = x + (localY * ChunkMath.SECTION_SIZE) + (z * ChunkMath.SECTION_SIZE * ChunkMath.SECTION_SIZE);
            return sections[sectionY].voxels[index];
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
                _blocklightBfsQueue.Enqueue(new LightQueueNode { Position = localPos, OldLightLevel = oldLightLevel });
                HasLightChangesToProcess = true;
            }
        }

        /// <summary>
        /// Entry point for any sunlight updates.
        /// </summary>
        public void AddToSunLightQueue(Vector3Int localPos, byte oldLightLevel)
        {
            if (World.Instance.settings.enableLighting)
            {
                _sunlightBfsQueue.Enqueue(new LightQueueNode { Position = localPos, OldLightLevel = oldLightLevel });
                HasLightChangesToProcess = true;
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

        /// <summary>
        /// Recalculates the sunlight for this chunk.
        /// </summary>
        public void RecalculateSunLightLight()
        {
            WorldData worldData = World.Instance.worldData;

            for (int x = 0; x < VoxelData.ChunkWidth; x++)
            {
                for (int z = 0; z < VoxelData.ChunkWidth; z++)
                {
                    // The global position of the column.
                    worldData.QueueSunlightRecalculation(new Vector2Int(position.x + x, position.y + z));
                }
            }
        }

        # endregion


        // --- Helper Methods ---

        #region Helper Methods

        /// Jobs helper method for providing data to jobs
        /// <param name="allocator">The allocator to use for the native array</param>
        /// <returns>Jobs compatible array of voxels</returns>
        public NativeArray<uint> GetMapForJob(Allocator allocator)
        {
            // Flatten sections into a single NativeArray
            int totalVoxels = VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth;
            var jobArray = new NativeArray<uint>(totalVoxels, allocator);

            int sectionVoxelCount = ChunkMath.SECTION_VOLUME;

            for (int i = 0; i < sections.Length; i++)
            {
                if (sections[i] != null)
                {
                    // Copy managed section array to native job array at correct offset
                    NativeArray<uint>.Copy(sections[i].voxels, 0, jobArray, i * sectionVoxelCount, sectionVoxelCount);
                }
                // If section is null, the jobArray already contains 0 (Air) from initialization
            }

            return jobArray;
        }

        /// Check if a local voxel position is within the bounds of this chunk.
        /// <param name="x">Local X coordinate</param>
        /// <param name="y">Local Y coordinate</param>
        /// <param name="z">Local Z coordinate</param>
        /// <returns>True if voxel is in chunk</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsVoxelInChunk(int x, int y, int z)
        {
            return x is >= 0 and < VoxelData.ChunkWidth &&
                   y is >= 0 and < VoxelData.ChunkHeight &&
                   z is >= 0 and < VoxelData.ChunkWidth;
        }

        /// Check if a local voxel position is within the bounds of this chunk.
        /// <param name="localPos">Local position</param>
        /// <returns>True if voxel is in chunk</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsVoxelInChunk(Vector3Int localPos)
        {
            return IsVoxelInChunk(localPos.x, localPos.y, localPos.z);
        }

        /// Gets a VoxelState struct from a local position.
        /// Handles lookups that are outside this chunk by asking the world.
        /// <param name="localPos">Local position</param>
        /// <returns>VoxelState struct or null if voxel is not in chunk</returns>
        [CanBeNull]
        public VoxelState? GetState(Vector3Int localPos)
        {
            if (IsVoxelInChunk(localPos.x, localPos.y, localPos.z))
            {
                uint packedData = GetVoxel(localPos.x, localPos.y, localPos.z);
                return new VoxelState(packedData);
            }

            // If it's not in this chunk, ask the world.
            Vector3 globalPos = new Vector3(localPos.x + position.x, localPos.y, localPos.z + position.y);
            return World.Instance.worldData.GetVoxelState(globalPos);
        }

        /// Gets a VoxelState struct from a local position
        /// <param name="localPos">Local position</param>
        /// <returns>VoxelState struct or null if voxel is not in chunk</returns>
        [CanBeNull]
        public VoxelState? VoxelFromV3Int(Vector3Int localPos)
        {
            if (!IsVoxelInChunk(localPos))
            {
                return null;
            }

            uint packedData = GetVoxel(localPos.x, localPos.y, localPos.z);
            return new VoxelState(packedData);
        }

        /// <summary>
        /// Gets the highest voxel in a column in the chunk of the given position.
        /// If no solid voxels are found, returns the world height.
        /// </summary>
        /// <param name="localPos">Local position</param>
        /// <returns>Local position of highest voxel</returns>
        public Vector3Int GetHighestVoxel(Vector3Int localPos)
        {
            // TODO: I believe this can be optimized by using the Chunk Height map, although not sure that the height map keeps track of structures.
            const int yMax = VoxelData.ChunkHeight - 1;
            int x = localPos.x;
            int z = localPos.z;

            for (int y = yMax; y > 0; y--)
            {
                uint packedData = GetVoxel(x, y, z);
                ushort id = BurstVoxelDataBitMapping.GetId(packedData);
                // Debug.Log($"Y: {y:D2} | VoxelState: {World.Instance.blockTypes[id]}");

                if (World.Instance.blockTypes[id].isSolid)
                {
                    return new Vector3Int(x, y, z);
                }
            }

            return new Vector3Int(x, yMax, z);
        }

        #endregion
    }

    public struct LightQueueNode : IEquatable<LightQueueNode>
    {
        public Vector3Int Position;
        public byte OldLightLevel;

        // --- Operator Overloads for comparison ---

        #region Overides

        public static bool operator ==(LightQueueNode a, LightQueueNode b)
        {
            return a.Position == b.Position && a.OldLightLevel == b.OldLightLevel;
        }

        public static bool operator !=(LightQueueNode a, LightQueueNode b)
        {
            return a.Position != b.Position || a.OldLightLevel != b.OldLightLevel;
        }

        public bool Equals(LightQueueNode other)
        {
            return Position == other.Position && OldLightLevel == other.OldLightLevel;
        }

        public override bool Equals(object obj)
        {
            return obj is LightQueueNode other && this == other;
        }

        public override int GetHashCode()
        {
            return Position.GetHashCode() ^ OldLightLevel.GetHashCode();
        }

        public override string ToString()
        {
            return $"LightQueueNode: {{ Position = {Position}, OldLightLevel = {OldLightLevel} }}";
        }

        #endregion
    }
}
