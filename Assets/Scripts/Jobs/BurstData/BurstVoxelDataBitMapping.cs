using System.Runtime.CompilerServices;
using Unity.Burst;

namespace Jobs.BurstData
{
    /// <summary>
    /// This class contains an exact copy of the bit-packing logic from VoxelData.
    /// It implements the "Context-Sensitive Metadata" pattern, allowing for 16-bit IDs
    /// and shared storage for Fluid/Orientation states.
    /// </summary>
    [BurstCompile]
    public static class BurstVoxelDataBitMapping
    {
        // --- Constants for Bit Packing ---
        // Layout: [ID: 16][Reserved: 8][Meta: 8] = 32 bits
        // Bits 16-23 are reserved for future use (formerly sunlight + blocklight).
        public const uint ID_MASK = 0x0000FFFF; // Bits 0-15  (16-bits, Values 0-65,535)
        public const uint META_MASK = 0xFF000000; // Bits 24-31 (8-bits, Values 0-255)

        public const int ID_SHIFT = 0;
        public const int META_SHIFT = 24;

        // --- Internal Masks within the 8-bit Metadata field ---
        // These apply AFTER shifting the meta bits down to 0.
        public const byte META_VAL_FLUID_MASK = 0xF; // 4 bits (0-15)
        public const byte META_VAL_ORIENT_MASK = 0x7; // 3 bits (0-7)

        // --- Fluid falling-flag sub-encoding (within the 4-bit fluid-level nibble) ---
        // Minecraft Beta 1.3.2 uses fluid level >= 8 to mark a vertically falling block; the lower 3 bits carry
        // the "effective level" of the upstream block. Source of truth for both the managed BlockBehavior.Fluids
        // tick and the Burst FluidTickJob — keep the encoding here so the two paths can never drift.
        public const byte FLUID_FALLING_FLAG = 8; // level >= 8 means falling
        public const byte FLUID_EFFECTIVE_LEVEL_MASK = 0x7; // lower 3 bits = effective level

        /// <summary>Returns true if the fluid level encodes a vertically falling block (level &gt;= 8).</summary>
        /// <param name="fluidLevel">The 4-bit fluid level (0-15).</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsFluidFalling(byte fluidLevel) => fluidLevel >= FLUID_FALLING_FLAG;

        /// <summary>Strips the falling flag, returning the horizontal effective level (0-7).</summary>
        /// <param name="fluidLevel">The 4-bit fluid level (0-15).</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetEffectiveFluidLevel(byte fluidLevel) => (byte)(fluidLevel & FLUID_EFFECTIVE_LEVEL_MASK);

        /// <summary>Builds a falling-flagged fluid level from a horizontal effective level (0-7).</summary>
        /// <param name="effectiveLevel">The horizontal effective level (0-7).</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte MakeFluidFalling(byte effectiveLevel) => (byte)(effectiveLevel | FLUID_FALLING_FLAG);

        // --- Helpers ---

        /// <summary>Maps World Orientation (Face Index) -> Internal Storage Index (0-5).</summary>
        /// <param name="orientation">The world orientation to convert.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte GetOrientationIndex(byte orientation)
        {
            // Standard VoxelData.FaceChecks indices:
            // 0=Back, 1=Front, 2=Top, 3=Bottom, 4=Left, 5=Right
            return orientation switch
            {
                1 => 0, // Front/North maps to index 0 (Default)
                0 => 1, // Back/South maps to index 1
                4 => 2, // Left/West  maps to index 2
                5 => 3, // Right/East maps to index 3
                2 => 4, // Top        maps to index 4
                3 => 5, // Bottom     maps to index 5
                _ => 0,
            };
        }

        // --- Packing ---

        /// <summary>
        /// Packs voxel data into a single uint with the given raw metadata byte.
        /// </summary>
        /// <param name="id">The block ID (0-65535).</param>
        /// <param name="meta">The raw 8-bit metadata byte. Schema-aware callers should encode this value
        /// using <c>BurstVoxelMetadataUtility</c> (per <c>PER_BLOCK_METADATA_SCHEMAS.md §7.1</c>).
        /// Transitional callers that still use legacy orientation/fluid-level inputs can compute the
        /// byte via <see cref="BuildMetaLegacy"/>.</param>
        /// <returns>A packed uint containing all the voxel state data.</returns>
        /// <remarks>
        /// Layout: [ID:16][Reserved:8][Meta:8]. Bits 16-23 are reserved (zeroed) for future use.
        /// Light data is stored separately in the <c>ushort LightData[]</c> array per section.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint PackVoxelData(ushort id, byte meta)
        {
            return (uint)(id << ID_SHIFT) | (uint)(meta << META_SHIFT);
        }

        /// <summary>
        /// Computes a metadata byte using the legacy "fluid-level OR orientation-storage-index" rule.
        /// </summary>
        /// <param name="orientation">The world orientation (face index 0-5).</param>
        /// <param name="fluidLevel">The fluid level (0-15).</param>
        /// <param name="isFluid">If <see langword="true"/>, force the fluid-level encoding even when <paramref name="fluidLevel"/> is zero.</param>
        /// <returns>A raw metadata byte: fluid level (bits 0-3) when <paramref name="isFluid"/> or <paramref name="fluidLevel"/> &gt; 0; otherwise the legacy orientation storage index (bits 0-2).</returns>
        /// <remarks>
        /// <para>Transitional helper per <c>PER_BLOCK_METADATA_SCHEMAS.md §7.1</c>. Use it only at call sites
        /// that have not yet migrated to schema-aware meta encoding via
        /// <c>BurstVoxelMetadataUtility</c>. After Phase 2 callsite migration this helper can be removed
        /// alongside the other legacy compatibility shims.</para>
        /// <para>This is the same encoding the old 6-argument <c>PackVoxelData</c> performed internally —
        /// extracted into a named helper so each transitional callsite is explicit about using legacy
        /// semantics rather than schema-aware semantics.</para>
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte BuildMetaLegacy(byte orientation, byte fluidLevel, bool isFluid)
        {
            if (isFluid || fluidLevel > 0)
            {
                return (byte)(fluidLevel & META_VAL_FLUID_MASK);
            }

            return GetOrientationIndex(orientation);
        }

        // --- Unpacking / Getters ---

        /// <summary>
        /// Extracts the block ID from the packed voxel data.
        /// </summary>
        /// <param name="packedData">The packed uint data.</param>
        /// <returns>The block ID.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort GetId(uint packedData)
        {
            return (ushort)((packedData & ID_MASK) >> ID_SHIFT);
        }

        /// <summary>
        /// Returns the raw 8-bit metadata value.
        /// </summary>
        /// <param name="packedData">The packed uint data.</param>
        /// <returns>The 8-bit metadata value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetMeta(uint packedData)
        {
            return (byte)((packedData & META_MASK) >> META_SHIFT);
        }

        /// <summary>
        /// Extracts orientation from the Metadata bits.
        /// </summary>
        /// <param name="packedData">The packed uint data.</param>
        /// <returns>The world orientation.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetOrientation(uint packedData)
        {
            // Extract the raw meta byte
            byte meta = GetMeta(packedData);

            // Mask the first 3 bits for orientation index
            byte orientationIndex = (byte)(meta & META_VAL_ORIENT_MASK);

            // Inverse mapping: Storage Index -> World Orientation
            return orientationIndex switch
            {
                0 => 1, // Index 0 -> Front (North)
                1 => 0, // Index 1 -> Back (South)
                2 => 4, // Index 2 -> Left (West)
                3 => 5, // Index 3 -> Right (East)
                4 => 2, // Index 4 -> Top
                5 => 3, // Index 5 -> Bottom
                _ => 1,
            };
        }

        /// <summary>
        /// Extracts fluid level from the Metadata bits.
        /// </summary>
        /// <param name="packedData">The packed uint data.</param>
        /// <returns>The fluid level (0-15).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetFluidLevel(uint packedData)
        {
            // Extract the raw meta byte
            byte meta = GetMeta(packedData);

            // Mask the first 4 bits for fluid level
            return (byte)(meta & META_VAL_FLUID_MASK);
        }

        // --- Packing / Setters ---

        /// <summary>
        /// Updates the block ID within the packed voxel data.
        /// </summary>
        /// <param name="packedData">The original packed uint data.</param>
        /// <param name="id">The new block ID to set.</param>
        /// <returns>The updated packed uint data.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint SetId(uint packedData, ushort id)
        {
            return (packedData & ~ID_MASK) | (uint)(id << ID_SHIFT);
        }

        /// <summary>
        /// Sets the full 8-bit metadata field directly.
        /// </summary>
        /// <param name="packedData">The original packed uint data.</param>
        /// <param name="meta">The metadata value to set.</param>
        /// <returns>The updated packed uint data.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint SetMeta(uint packedData, byte meta)
        {
            return (packedData & ~META_MASK) | (uint)(meta << META_SHIFT);
        }

        /// <summary>
        /// Sets the orientation bits within the metadata field.
        /// Note: This blindly overwrites the lower 3 bits of the metadata.
        /// </summary>
        /// <param name="packedData">The original packed uint data.</param>
        /// <param name="orientation">The orientation value to set.</param>
        /// <returns>The updated packed uint data.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint SetOrientation(uint packedData, byte orientation)
        {
            // 1. Get current meta
            byte currentMeta = GetMeta(packedData);

            // 2. Calculate new orientation index
            byte orientationIndex = GetOrientationIndex(orientation);

            // 3. Clear the orientation bits (0-2) in the current meta, preserving bits 3-7
            //    ~(0x7) = 11111000
            byte preservedMeta = (byte)(currentMeta & ~META_VAL_ORIENT_MASK);

            // 4. Combine
            byte newMeta = (byte)(preservedMeta | (orientationIndex & META_VAL_ORIENT_MASK));

            // 5. Write back
            return SetMeta(packedData, newMeta);
        }

        /// <summary>
        /// Sets the fluid level bits within the metadata field.
        /// Note: This blindly overwrites the lower 4 bits of the metadata.
        /// </summary>
        /// <param name="packedData">The original packed uint data.</param>
        /// <param name="fluidLevel">The fluid level to set.</param>
        /// <returns>The updated packed uint data.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint SetFluidLevel(uint packedData, byte fluidLevel)
        {
            // 1. Get current meta
            byte currentMeta = GetMeta(packedData);

            // 2. Clear the fluid bits (0-3) in the current meta, preserving bits 4-7
            //    ~(0xF) = 11110000
            byte preservedMeta = (byte)(currentMeta & ~META_VAL_FLUID_MASK);

            // 3. Combine
            byte newMeta = (byte)(preservedMeta | (fluidLevel & META_VAL_FLUID_MASK));

            // 4. Write back
            return SetMeta(packedData, newMeta);
        }
    }
}
