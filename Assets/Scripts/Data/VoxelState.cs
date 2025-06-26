using System;
using Jobs.BurstData;

namespace Data
{
    [Serializable]
    public struct VoxelState : IEquatable<VoxelState>
    {
        // Packed data field - All voxel data is packed into this uint.
        // Use the properties unpack and access the data.
        private uint _packedData;

        // --- Public Properties using Packed Data ---

        #region Packed Data Properties

        public byte id
        {
            get => BurstVoxelDataBitMapping.GetId(_packedData);
            set => _packedData = BurstVoxelDataBitMapping.SetId(_packedData, value); // Direct set, handle consequences elsewhere (like ModifyVoxel)
        }

        public byte orientation
        {
            get => BurstVoxelDataBitMapping.GetOrientation(_packedData);
            set => _packedData = BurstVoxelDataBitMapping.SetOrientation(_packedData, value); // Direct set
        }

        /// <summary>
        /// Returns the highest light level between sunlight and blocklight
        /// </summary>
        public byte light => BurstVoxelDataBitMapping.GetLight(_packedData);

        public byte Sunlight
        {
            get => BurstVoxelDataBitMapping.GetSunlight(_packedData);
            set => _packedData = BurstVoxelDataBitMapping.SetBlockLight(_packedData, value);
        }

        public byte Blocklight
        {
            get => BurstVoxelDataBitMapping.GetBlocklight(_packedData);
            set => _packedData = BurstVoxelDataBitMapping.SetBlockLight(_packedData, value);
        }

        #endregion

        // --- Constructors ---

        #region Constructors

        /// Create a new voxel state from a block id.
        public VoxelState(byte blockId)
        {
            _packedData = BurstVoxelDataBitMapping.PackVoxelData(
                blockId, // blockId
                0, // SunLight = 0
                0, // BlockLight = 0
                1 // Orientation = 1 (Front)
            );
        }

        /// Create a new voxel state from all its components.
        public VoxelState(byte blockId, byte sunLightLevel, byte blockLightLevel, byte orientation)
        {
            _packedData = BurstVoxelDataBitMapping.PackVoxelData(blockId, sunLightLevel, blockLightLevel, orientation);
        }

        /// Create a new voxel state from its raw packed data.
        public VoxelState(uint packedData)
        {
            _packedData = packedData;
        }

        #endregion

        // --- Other Properties ---
        public BlockType Properties => World.Instance.blockTypes[id];

        /// <summary>
        /// Returns the highest light level between sunlight and blocklight as a float between 0 and 1.
        /// </summary>
        public float lightAsFloat => light * VoxelData.UnitOfLight;

        // --- Operator Overloads for comparison ---
        public static bool operator ==(VoxelState a, VoxelState b)
        {
            return a._packedData == b._packedData;
        }

        public static bool operator !=(VoxelState a, VoxelState b)
        {
            return a._packedData != b._packedData;
        }

        public bool Equals(VoxelState other)
        {
            return _packedData == other._packedData;
        }

        public override bool Equals(object obj)
        {
            return obj is VoxelState other && this == other;
        }

        public override int GetHashCode()
        {
            return _packedData.GetHashCode();
        }

        public override string ToString()
        {
            return $"VoxelState: {{ Id = {id}, Light = {light}, Orientation = {orientation}, Properties = {Properties} }}";
        }
    }
}