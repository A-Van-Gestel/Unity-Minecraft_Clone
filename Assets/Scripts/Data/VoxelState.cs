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

        /// <summary>
        /// Gets or sets the block ID.
        /// </summary>
        public ushort ID
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => BurstVoxelDataBitMapping.GetId(_packedData);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => _packedData = BurstVoxelDataBitMapping.SetId(_packedData, value); // Direct set, handle consequences elsewhere (like ModifyVoxel)
        }

        /// <summary>
        /// Gets or sets the orientation index of the voxel.
        /// </summary>
        public byte Orientation
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => BurstVoxelDataBitMapping.GetOrientation(_packedData);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => _packedData = BurstVoxelDataBitMapping.SetOrientation(_packedData, value); // Direct set
        }

        /// <summary>
        /// Returns the highest light level between sunlight and blocklight.
        /// </summary>
        /// <value>A byte from 0 to 15 representing the maximum light.</value>
        public byte Light
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => BurstVoxelDataBitMapping.GetLight(_packedData);
        }

        /// <summary>
        /// Gets or sets the incoming sunlight level (0-15).
        /// </summary>
        public byte Sunlight
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => BurstVoxelDataBitMapping.GetSunLight(_packedData);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => _packedData = BurstVoxelDataBitMapping.SetSunLight(_packedData, value);
        }

        /// <summary>
        /// Gets or sets the incoming blocklight level (0-15).
        /// </summary>
        public byte Blocklight
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => BurstVoxelDataBitMapping.GetBlockLight(_packedData);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => _packedData = BurstVoxelDataBitMapping.SetBlockLight(_packedData, value);
        }

        /// <summary>
        /// Gets or sets the fluid level of the voxel.
        /// </summary>
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

        /// <summary>
        /// Creates a new voxel state from a block id.
        /// </summary>
        /// <param name="blockId">The block ID to initialize.</param>
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

        /// <summary>
        /// Creates a new voxel state from all its components.
        /// </summary>
        /// <param name="blockId">The ID of the block.</param>
        /// <param name="sunLightLevel">The initial sunlight level (0-15).</param>
        /// <param name="blockLightLevel">The initial blocklight level (0-15).</param>
        /// <param name="orientation">The face orientation (default 1).</param>
        /// <param name="fluidLevel">The fluid level (default 0).</param>
        public VoxelState(byte blockId, byte sunLightLevel, byte blockLightLevel, byte orientation = 1, byte fluidLevel = 0)
        {
            _packedData = BurstVoxelDataBitMapping.PackVoxelData(blockId, sunLightLevel, blockLightLevel, orientation, fluidLevel);
        }

        /// <summary>
        /// Creates a new voxel state from its raw packed uint representation.
        /// </summary>
        /// <param name="packedData">The raw 32-bit packed data.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public VoxelState(uint packedData)
        {
            _packedData = packedData;
        }

        #endregion

        // --- Other Properties ---
        /// <summary>
        /// Convenience accessor for the actual <see cref="BlockType"/> properties.
        /// </summary>
        public BlockType Properties => World.Instance.BlockTypes[ID];

        /// <summary>
        /// Returns the highest light level between sunlight and blocklight as a float between 0 and 1.
        /// </summary>
        /// <value>A float from 0 to 1 representing the light intensity.</value>
        public float LightAsFloat
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Light * VoxelData.UnitOfLight;
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
            return obj is VoxelState other && this == other; // TODO: Burst: Loading managed type 'Object' is not supported
        }

        public override int GetHashCode()
        {
            return _packedData.GetHashCode();
        }

        public override string ToString()
        {
            return $"VoxelState: {{ Id = {ID}, Light = {Light}, Orientation = {Orientation}, Properties = {Properties} }}";
        }

        #endregion
    }
}
