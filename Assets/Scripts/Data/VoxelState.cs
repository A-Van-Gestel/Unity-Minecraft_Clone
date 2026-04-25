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
        /// Gets or sets the raw 8-bit metadata byte.
        /// </summary>
        /// <remarks>
        /// This is the schema-agnostic accessor introduced in <c>PER_BLOCK_METADATA_SCHEMAS.md §7.2</c>.
        /// To interpret the byte, use <see cref="BurstVoxelMetadataUtility"/> together with the
        /// block's <see cref="MetadataSchema"/> from <see cref="BlockTypeJobData.MetadataSchema"/>
        /// (or <see cref="BlockType.metadataSchema"/> on the main thread).
        /// </remarks>
        public byte Meta
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => BurstVoxelDataBitMapping.GetMeta(_packedData);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => _packedData = BurstVoxelDataBitMapping.SetMeta(_packedData, value);
        }

        /// <summary>
        /// Gets or sets the orientation of the voxel using the legacy world-face encoding
        /// (i.e. <see cref="MetadataSchema.None"/> semantics).
        /// </summary>
        /// <remarks>
        /// <para>Burst-safe: forwards to <see cref="BurstVoxelDataBitMapping"/> with no managed lookups.</para>
        /// <para>This is a transitional accessor per <c>PER_BLOCK_METADATA_SCHEMAS.md §7.2</c>.
        /// It does <b>not</b> consult the block's <see cref="MetadataSchema"/> — for schema-aware decoding,
        /// use <see cref="GetOrientation(MetadataSchema)"/> / <see cref="SetOrientation(byte, MetadataSchema)"/>
        /// and pass the schema retrieved from <see cref="BlockTypeJobData.MetadataSchema"/> (jobs)
        /// or <see cref="BlockType.metadataSchema"/> (main thread).</para>
        /// <para>This property will be removed once all call sites have migrated to the schema-parametric
        /// helpers or to direct <see cref="Meta"/> reads.</para>
        /// </remarks>
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
        /// Gets or sets the fluid level of the voxel using the legacy bit-slicing encoding
        /// (i.e. <see cref="MetadataSchema.None"/> semantics).
        /// </summary>
        /// <remarks>
        /// <para>Burst-safe: forwards to <see cref="BurstVoxelDataBitMapping"/> with no managed lookups.
        /// This accessor is on a hot path inside <c>VoxelMeshHelper</c> when run from
        /// <c>MeshGenerationJob</c>, so it must remain fully Burst-compatible.</para>
        /// <para>This is a transitional accessor per <c>PER_BLOCK_METADATA_SCHEMAS.md §7.2</c>.
        /// For schema-aware decoding, use <see cref="GetFluidLevel(MetadataSchema)"/> /
        /// <see cref="SetFluidLevel(byte, MetadataSchema)"/> and pass the schema retrieved from
        /// <see cref="BlockTypeJobData.MetadataSchema"/> (jobs) or <see cref="BlockType.metadataSchema"/>
        /// (main thread).</para>
        /// <para>This property will be removed once all call sites have migrated to the schema-parametric
        /// helpers or to direct <see cref="Meta"/> reads.</para>
        /// </remarks>
        public byte FluidLevel
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => BurstVoxelDataBitMapping.GetFluidLevel(_packedData);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => _packedData = BurstVoxelDataBitMapping.SetFluidLevel(_packedData, value);
        }

        // --- Schema-Aware Accessors (§7.2) ---
        // These are the recommended Burst-safe replacements for the legacy Orientation/FluidLevel
        // properties above. The caller passes the schema (typically from BlockTypeJobData.MetadataSchema
        // in jobs or BlockType.metadataSchema on the main thread), keeping VoxelState free of any
        // managed singleton lookup. Once Phase 2 has migrated all call sites, the legacy properties
        // can be removed.

        /// <summary>
        /// Schema-aware getter for the voxel's orientation. Burst-safe.
        /// </summary>
        /// <param name="schema">The block's <see cref="MetadataSchema"/>.</param>
        /// <returns>
        /// <list type="bullet">
        ///   <item><description><see cref="MetadataSchema.None"/>: legacy world-face value (0-5) via <see cref="BurstVoxelDataBitMapping.GetOrientation"/>.</description></item>
        ///   <item><description><see cref="MetadataSchema.Facing6"/>: facing 0-5 from bits 0-2.</description></item>
        ///   <item><description><see cref="MetadataSchema.Facing6Roll2"/>: facing component (bits 0-2); the roll component is preserved in the underlying byte and is not returned here.</description></item>
        ///   <item><description><see cref="MetadataSchema.Axis3"/> / <see cref="MetadataSchema.FluidLevel4"/>: orientation is not meaningful — returns <c>0</c>.</description></item>
        /// </list>
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte GetOrientation(MetadataSchema schema)
        {
            switch (schema)
            {
                case MetadataSchema.Facing6:
                    return BurstVoxelMetadataUtility.DecodeFacing6(Meta);
                case MetadataSchema.Facing6Roll2:
                    return BurstVoxelMetadataUtility.DecodeFacing6Roll2Facing(Meta);
                case MetadataSchema.Axis3:
                case MetadataSchema.FluidLevel4:
                    return 0;
                default:
                    // None and any unknown schema fall back to the legacy world-face decode.
                    return BurstVoxelDataBitMapping.GetOrientation(_packedData);
            }
        }

        /// <summary>
        /// Schema-aware setter for the voxel's orientation. Burst-safe.
        /// </summary>
        /// <param name="value">The new orientation value (semantic range depends on the schema).</param>
        /// <param name="schema">The block's <see cref="MetadataSchema"/>.</param>
        /// <remarks>
        /// <list type="bullet">
        ///   <item><description><see cref="MetadataSchema.None"/>: writes via the legacy <see cref="BurstVoxelDataBitMapping.SetOrientation"/>.</description></item>
        ///   <item><description><see cref="MetadataSchema.Facing6"/>: replaces the metadata byte with the encoded facing.</description></item>
        ///   <item><description><see cref="MetadataSchema.Facing6Roll2"/>: replaces only the facing component; preserves the roll bits in bits 3-4.</description></item>
        ///   <item><description><see cref="MetadataSchema.Axis3"/> / <see cref="MetadataSchema.FluidLevel4"/>: orientation is not meaningful — no-op.</description></item>
        /// </list>
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetOrientation(byte value, MetadataSchema schema)
        {
            switch (schema)
            {
                case MetadataSchema.Facing6:
                    _packedData = BurstVoxelDataBitMapping.SetMeta(
                        _packedData, BurstVoxelMetadataUtility.EncodeFacing6(value));
                    break;
                case MetadataSchema.Facing6Roll2:
                {
                    // Preserve roll bits (3-4); only update the facing component (0-2).
                    byte currentMeta = BurstVoxelDataBitMapping.GetMeta(_packedData);
                    byte rollBits = (byte)(currentMeta & BurstVoxelMetadataUtility.FACING6_ROLL2_ROLL_MASK_SHIFTED);
                    byte newMeta = (byte)(BurstVoxelMetadataUtility.EncodeFacing6(value) | rollBits);
                    _packedData = BurstVoxelDataBitMapping.SetMeta(_packedData, newMeta);
                    break;
                }
                case MetadataSchema.Axis3:
                case MetadataSchema.FluidLevel4:
                    // Orientation is not meaningful for these schemas; no-op.
                    break;
                default:
                    // None and any unknown schema fall back to the legacy world-face encode.
                    _packedData = BurstVoxelDataBitMapping.SetOrientation(_packedData, value);
                    break;
            }
        }

        /// <summary>
        /// Schema-aware getter for the voxel's fluid level. Burst-safe.
        /// </summary>
        /// <param name="schema">The block's <see cref="MetadataSchema"/>.</param>
        /// <returns>
        /// <list type="bullet">
        ///   <item><description><see cref="MetadataSchema.None"/>: legacy fluid level (0-15) via <see cref="BurstVoxelDataBitMapping.GetFluidLevel"/>.</description></item>
        ///   <item><description><see cref="MetadataSchema.FluidLevel4"/>: fluid level (0-15) from bits 0-3.</description></item>
        ///   <item><description><see cref="MetadataSchema.Axis3"/>, <see cref="MetadataSchema.Facing6"/>, <see cref="MetadataSchema.Facing6Roll2"/>: fluid level is not meaningful — returns <c>0</c>.</description></item>
        /// </list>
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte GetFluidLevel(MetadataSchema schema)
        {
            switch (schema)
            {
                case MetadataSchema.FluidLevel4:
                    return BurstVoxelMetadataUtility.DecodeFluidLevel(Meta);
                case MetadataSchema.Axis3:
                case MetadataSchema.Facing6:
                case MetadataSchema.Facing6Roll2:
                    return 0;
                default:
                    // None and any unknown schema fall back to the legacy fluid-level decode.
                    return BurstVoxelDataBitMapping.GetFluidLevel(_packedData);
            }
        }

        /// <summary>
        /// Schema-aware setter for the voxel's fluid level. Burst-safe.
        /// </summary>
        /// <param name="value">The new fluid level (0-15).</param>
        /// <param name="schema">The block's <see cref="MetadataSchema"/>.</param>
        /// <remarks>
        /// <list type="bullet">
        ///   <item><description><see cref="MetadataSchema.None"/>: writes via the legacy <see cref="BurstVoxelDataBitMapping.SetFluidLevel"/>.</description></item>
        ///   <item><description><see cref="MetadataSchema.FluidLevel4"/>: replaces the metadata byte with the encoded fluid level.</description></item>
        ///   <item><description><see cref="MetadataSchema.Axis3"/>, <see cref="MetadataSchema.Facing6"/>, <see cref="MetadataSchema.Facing6Roll2"/>: fluid level is not meaningful — no-op.</description></item>
        /// </list>
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetFluidLevel(byte value, MetadataSchema schema)
        {
            switch (schema)
            {
                case MetadataSchema.FluidLevel4:
                    _packedData = BurstVoxelDataBitMapping.SetMeta(
                        _packedData, BurstVoxelMetadataUtility.EncodeFluidLevel(value));
                    break;
                case MetadataSchema.Axis3:
                case MetadataSchema.Facing6:
                case MetadataSchema.Facing6Roll2:
                    // Fluid level is not meaningful for these schemas; no-op.
                    break;
                default:
                    // None and any unknown schema fall back to the legacy fluid-level encode.
                    _packedData = BurstVoxelDataBitMapping.SetFluidLevel(_packedData, value);
                    break;
            }
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
                BurstVoxelDataBitMapping.BuildMetaLegacy(orientation: 1, fluidLevel: 0, isFluid: false)); // Default: Front orientation
        }

        /// <summary>
        /// Creates a new voxel state from all its components using the legacy
        /// orientation/fluid-level encoding.
        /// </summary>
        /// <param name="blockId">The ID of the block.</param>
        /// <param name="sunLightLevel">The initial sunlight level (0-15).</param>
        /// <param name="blockLightLevel">The initial blocklight level (0-15).</param>
        /// <param name="orientation">The face orientation (default 1).</param>
        /// <param name="fluidLevel">The fluid level (default 0).</param>
        /// <remarks>
        /// Transitional constructor — encodes the meta byte using the legacy
        /// <see cref="BurstVoxelDataBitMapping.BuildMetaLegacy"/> rule. Schema-aware callers should
        /// prefer <see cref="VoxelState(uint)"/> with a pre-computed packed value, or
        /// construct via <see cref="BurstVoxelDataBitMapping.PackVoxelData(ushort, byte, byte, byte)"/>
        /// using a meta byte produced by <c>BurstVoxelMetadataUtility</c>.
        /// </remarks>
        public VoxelState(byte blockId, byte sunLightLevel, byte blockLightLevel, byte orientation = 1, byte fluidLevel = 0)
        {
            _packedData = BurstVoxelDataBitMapping.PackVoxelData(
                blockId, sunLightLevel, blockLightLevel,
                BurstVoxelDataBitMapping.BuildMetaLegacy(orientation, fluidLevel, isFluid: false));
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
            return $"VoxelState: {{ Id = {ID}, Light = {Light}, Meta = 0x{Meta:X2}, Orientation = {Orientation}, Properties = {Properties} }}";
        }

        #endregion
    }
}
