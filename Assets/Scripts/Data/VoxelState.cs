using System;

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
            get { return VoxelData.GetId(_packedData); }
            set { _packedData = VoxelData.SetId(_packedData, value); } // Direct set, handle consequences elsewhere (like ModifyVoxel)
        }

        public byte orientation
        {
            get { return VoxelData.GetOrientation(_packedData); }
            set { _packedData = VoxelData.SetOrientation(_packedData, value); } // Direct set
        }

        public byte light
        {
            get { return VoxelData.GetLight(_packedData); }
            set
            {
                // Direct set to the packed data.
                // The complex propagation logic is now located in ChunkData.
                _packedData = VoxelData.SetLight(_packedData, value);
            }
        }
        #endregion

        // --- Constructors ---
        #region Constructors
        /// Create a new voxel state from a block id.
        public VoxelState(byte blockId)
        {
            _packedData = VoxelData.PackVoxelData(
                blockId, // blockId
                0, // Light = 0
                1 // Orientation = 1 (Front)
            );
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
    }
}