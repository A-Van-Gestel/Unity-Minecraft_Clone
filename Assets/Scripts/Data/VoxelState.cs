using System;
using System.Runtime.CompilerServices;
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

        public ushort id
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => BurstVoxelDataBitMapping.GetId(_packedData);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => _packedData = BurstVoxelDataBitMapping.SetId(_packedData, value); // Direct set, handle consequences elsewhere (like ModifyVoxel)
        }

        public byte orientation
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => BurstVoxelDataBitMapping.GetOrientation(_packedData);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => _packedData = BurstVoxelDataBitMapping.SetOrientation(_packedData, value); // Direct set
        }

        /// <summary>
        /// Returns the highest light level between sunlight and blocklight
        /// </summary>
        public byte light
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => BurstVoxelDataBitMapping.GetLight(_packedData);
        }

        public byte Sunlight
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => BurstVoxelDataBitMapping.GetSunLight(_packedData);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => _packedData = BurstVoxelDataBitMapping.SetSunLight(_packedData, value);
        }

        public byte Blocklight
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => BurstVoxelDataBitMapping.GetBlockLight(_packedData);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => _packedData = BurstVoxelDataBitMapping.SetBlockLight(_packedData, value);
        }

        public byte FluidLevel
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => BurstVoxelDataBitMapping.GetFluidLevel(_packedData);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => _packedData = BurstVoxelDataBitMapping.SetFluidLevel(_packedData, value);
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
                1, // Orientation = 1 (Front)
                0 // FluidLevel = 0
            );
        }

        /// Create a new voxel state from all its components.
        public VoxelState(byte blockId, byte sunLightLevel, byte blockLightLevel, byte orientation = 1, byte fluidLevel = 0)
        {
            _packedData = BurstVoxelDataBitMapping.PackVoxelData(blockId, sunLightLevel, blockLightLevel, orientation, fluidLevel);
        }

        /// Create a new voxel state from its raw packed data.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
        public float lightAsFloat
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => light * VoxelData.UnitOfLight;
        }

        // --- Operator Overloads for comparison ---

        #region Overides

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(VoxelState a, VoxelState b)
        {
            return a._packedData == b._packedData;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

        #endregion
    }
}
