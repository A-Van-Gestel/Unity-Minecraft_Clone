using System.Runtime.CompilerServices;
using Data;
using Jobs.BurstData;
using Unity.Mathematics;

namespace Helpers
{
    /// <summary>
    /// Main-thread facade over <see cref="BurstVoxelMetadataUtility"/>. Provides
    /// the same encode/decode/validation surface for schema-aware metadata
    /// without requiring callers to reference the Burst namespace.
    /// </summary>
    /// <remarks>
    /// <para>Per <c>Documentation/Design/PER_BLOCK_METADATA_SCHEMAS.md §7.1</c>, main-thread
    /// code should call into this class; jobs should call <see cref="BurstVoxelMetadataUtility"/>
    /// directly. Both share the same frozen bit layouts (§5.3).</para>
    /// <para>The implementations here are strict pass-throughs. This indirection exists so
    /// future main-thread-only helpers (exception-throwing variants, string decoders for
    /// debug UI, etc.) can be added without disturbing the Burst surface.</para>
    /// </remarks>
    public static class VoxelMetadataUtility
    {
        // ===== Encode =====

        /// <inheritdoc cref="BurstVoxelMetadataUtility.EncodeFluidLevel"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte EncodeFluidLevel(byte level)
            => BurstVoxelMetadataUtility.EncodeFluidLevel(level);

        /// <inheritdoc cref="BurstVoxelMetadataUtility.EncodeAxis3"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte EncodeAxis3(byte axis)
            => BurstVoxelMetadataUtility.EncodeAxis3(axis);

        /// <inheritdoc cref="BurstVoxelMetadataUtility.EncodeFacing6"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte EncodeFacing6(byte facing)
            => BurstVoxelMetadataUtility.EncodeFacing6(facing);

        /// <inheritdoc cref="BurstVoxelMetadataUtility.EncodeFacing6Roll2"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte EncodeFacing6Roll2(byte facing, byte roll)
            => BurstVoxelMetadataUtility.EncodeFacing6Roll2(facing, roll);

        /// <inheritdoc cref="BurstVoxelMetadataUtility.EncodeHorizontalOnly"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte EncodeHorizontalOnly(byte yaw)
            => BurstVoxelMetadataUtility.EncodeHorizontalOnly(yaw);

        // ===== Decode =====

        /// <inheritdoc cref="BurstVoxelMetadataUtility.DecodeFluidLevel"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte DecodeFluidLevel(byte meta)
            => BurstVoxelMetadataUtility.DecodeFluidLevel(meta);

        /// <inheritdoc cref="BurstVoxelMetadataUtility.DecodeAxis3"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte DecodeAxis3(byte meta)
            => BurstVoxelMetadataUtility.DecodeAxis3(meta);

        /// <inheritdoc cref="BurstVoxelMetadataUtility.DecodeFacing6"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte DecodeFacing6(byte meta)
            => BurstVoxelMetadataUtility.DecodeFacing6(meta);

        /// <inheritdoc cref="BurstVoxelMetadataUtility.DecodeFacing6Roll2Facing"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte DecodeFacing6Roll2Facing(byte meta)
            => BurstVoxelMetadataUtility.DecodeFacing6Roll2Facing(meta);

        /// <inheritdoc cref="BurstVoxelMetadataUtility.DecodeFacing6Roll2Roll"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte DecodeFacing6Roll2Roll(byte meta)
            => BurstVoxelMetadataUtility.DecodeFacing6Roll2Roll(meta);

        /// <inheritdoc cref="BurstVoxelMetadataUtility.DecodeHorizontalOnly"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte DecodeHorizontalOnly(byte meta)
            => BurstVoxelMetadataUtility.DecodeHorizontalOnly(meta);

        // ===== Validation =====

        /// <inheritdoc cref="BurstVoxelMetadataUtility.IsValidMeta"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsValidMeta(MetadataSchema schema, byte meta)
            => BurstVoxelMetadataUtility.IsValidMeta(schema, meta);

        /// <inheritdoc cref="BurstVoxelMetadataUtility.NormalizeMeta"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte NormalizeMeta(MetadataSchema schema, byte meta, byte defaultMeta)
            => BurstVoxelMetadataUtility.NormalizeMeta(schema, meta, defaultMeta);

        /// <inheritdoc cref="BurstVoxelMetadataUtility.DominantAxisFromLookVector"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte DominantAxisFromLookVector(float3 lookVector)
            => BurstVoxelMetadataUtility.DominantAxisFromLookVector(lookVector);
    }
}
