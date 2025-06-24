using System;
using Jobs.BurstData;

namespace Data
{
    [Serializable]
    public struct VoxelState : IEquatable<VoxelState>
    {
        // Packed data field - All voxel data is packed into this ushort.
        // Use the properties unpack and access the data.
        private ushort _packedData;

        // --- Public Properties using Packed Data ---
        #region Packed Data Properties
        public byte id
        {
            get { return BurstVoxelDataBitMapping.GetId(_packedData); }
            set { _packedData = BurstVoxelDataBitMapping.SetId(_packedData, value); } // Direct set, handle consequences elsewhere (like ModifyVoxel)
        }

        public byte orientation
        {
            get { return BurstVoxelDataBitMapping.GetOrientation(_packedData); }
            set { _packedData = BurstVoxelDataBitMapping.SetOrientation(_packedData, value); } // Direct set
        }

        public byte light
        {
            get { return BurstVoxelDataBitMapping.GetLight(_packedData); }
            set
            {
                // Direct set to the packed data.
                // The complex propagation logic is now located in ChunkData.
                _packedData = BurstVoxelDataBitMapping.SetLight(_packedData, value);
            }
        }
        #endregion

        // --- Constructors ---
        #region Constructors
        /// Create a new voxel state from a block id.
        public VoxelState(byte blockId)
        {
            _packedData = BurstVoxelDataBitMapping.PackVoxelData(
                blockId, // blockId
                0, // Light = 0
                1 // Orientation = 1 (Front)
            );
        }
        
        /// Create a new voxel state from all its components.
        public VoxelState(byte blockId, byte lightLevel, byte orientation)
        {
            _packedData = BurstVoxelDataBitMapping.PackVoxelData(blockId, lightLevel, orientation);
        }
        
        /// Create a new voxel state from its raw packed data.
        public VoxelState(ushort packedData)
        {
            _packedData = packedData;
        }
        #endregion

        // --- Other Properties ---
        public BlockType Properties
        {
            get { return World.Instance.blockTypes[id]; }
        }
        
        public float lightAsFloat
        {
            get { return light * VoxelData.UnitOfLight; }
        }

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