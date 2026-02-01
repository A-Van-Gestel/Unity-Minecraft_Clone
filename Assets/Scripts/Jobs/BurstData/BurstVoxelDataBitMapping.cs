using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Mathematics;

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
        // Using Hex for clarity with bit positions
        private const uint ID_MASK = 0x0000FFFF; // Bits 0-15  (16-bits, Values 0-65,535)
        private const uint SUNLIGHT_MASK = 0x000F0000; // Bits 16-19 (4-bits, Values 0-15)
        private const uint BLOCKLIGHT_MASK = 0x00F00000; // Bits 20-23 (4-bits, Values 0-15)
        private const uint META_MASK = 0xFF000000; // Bits 24-31 (8-bits, Values 0-255)
        // All 32 bits are used.

        private const int ID_SHIFT = 0;
        private const int SUNLIGHT_SHIFT = 16;
        private const int BLOCKLIGHT_SHIFT = 20;
        private const int META_SHIFT = 24;

        // --- Internal Masks within the 8-bit Metadata field ---
        // These apply AFTER shifting the meta bits down to 0.
        private const byte META_VAL_FLUID_MASK = 0xF; // 4 bits (0-15)
        private const byte META_VAL_ORIENT_MASK = 0x7; // 3 bits (0-7)

        // --- Helpers ---

        /// Maps World Orientation (Face Index) -> Internal Storage Index (0-5)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte GetOrientationIndex(byte orientation)
        {
            // Standard VoxelData.FaceChecks indices:
            // 0=Back, 1=Front, 2=Top, 3=Bottom, 4=Left, 5=Right
            switch (orientation)
            {
                case 1: return 0; // Front/North maps to index 0 (Default)
                case 0: return 1; // Back/South maps to index 1
                case 4: return 2; // Left/West  maps to index 2
                case 5: return 3; // Right/East maps to index 3
                case 2: return 4; // Top        maps to index 4
                case 3: return 5; // Bottom     maps to index 5
                default: return 0; // Default to index 0 (Front) for any invalid orientation
            }
        }

        // --- Packing ---

        /// <summary>
        /// Packs all component data into a single uint.
        /// Orientation and FluidLevel now share the same 8-bit Metadata space.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint PackVoxelData(ushort id, byte sunLight, byte blockLight, byte orientation, byte fluidLevel)
        {
            uint packedData = 0;
            packedData |= (uint)((id) << ID_SHIFT); // ID: 16 bits
            packedData |= (uint)((sunLight & 0xF) << SUNLIGHT_SHIFT); // Sunlight: 4 bits
            packedData |= (uint)((blockLight & 0xF) << BLOCKLIGHT_SHIFT); // Blocklight: 4 bits

            // Metadata Logic:
            // Since Fluid and Orientation share the same bits, we prioritize FluidLevel if it exists.
            // A block defined as a Fluid in BlockTypes should use FluidLevel.
            // A block defined as Solid should use Orientation.
            // Here we combine them, assuming the caller sends 0 for the unused property.

            byte meta = 0;
            if (fluidLevel > 0)
            {
                meta = (byte)(fluidLevel & META_VAL_FLUID_MASK);
            }
            else
            {
                meta = GetOrientationIndex(orientation);
            }

            packedData |= (uint)((meta) << META_SHIFT); // Meta: 8 bits

            return packedData;
        }

        // --- Unpacking / Getters ---

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort GetId(uint packedData)
        {
            return (ushort)((packedData & ID_MASK) >> ID_SHIFT);
        }

        /// <summary>
        /// Returns the raw 8-bit metadata value.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetMeta(uint packedData)
        {
            return (byte)((packedData & META_MASK) >> META_SHIFT);
        }

        /// <summary>
        /// Returns the highest light level between sunlight and blocklight
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetLight(uint packedData)
        {
            // NOTE: Actual types are byte, but we use uint here to make sure the math.max function works correctly.
            uint sunLightLevel = GetSunLight(packedData);
            uint blockLightLevel = GetBlockLight(packedData);
            return (byte)math.max(sunLightLevel, blockLightLevel);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetSunLight(uint packedData)
        {
            return (byte)((packedData & SUNLIGHT_MASK) >> SUNLIGHT_SHIFT);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetBlockLight(uint packedData)
        {
            return (byte)((packedData & BLOCKLIGHT_MASK) >> BLOCKLIGHT_SHIFT);
        }

        /// <summary>
        /// Extracts orientation from the Metadata bits.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetOrientation(uint packedData)
        {
            // Extract the raw meta byte
            byte meta = GetMeta(packedData);

            // Mask the first 3 bits for orientation index
            byte orientationIndex = (byte)(meta & META_VAL_ORIENT_MASK);

            // Inverse mapping: Storage Index -> World Orientation
            switch (orientationIndex)
            {
                case 0: return 1; // Index 0 -> Front (North)
                case 1: return 0; // Index 1 -> Back (South)
                case 2: return 4; // Index 2 -> Left (West)
                case 3: return 5; // Index 3 -> Right (East)
                case 4: return 2; // Index 4 -> Top
                case 5: return 3; // Index 5 -> Bottom
                default: return 1; // Fallback to Front
            }
        }

        /// <summary>
        /// Extracts fluid level from the Metadata bits.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetFluidLevel(uint packedData)
        {
            // Extract the raw meta byte
            byte meta = GetMeta(packedData);

            // Mask the first 4 bits for fluid level
            return (byte)(meta & META_VAL_FLUID_MASK);
        }

        // --- Packing / Setters ---

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint SetId(uint packedData, ushort id)
        {
            return (packedData & ~ID_MASK) | (uint)((id) << ID_SHIFT);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint SetSunLight(uint packedData, byte sunLightLevel)
        {
            return (packedData & ~SUNLIGHT_MASK) | (uint)((sunLightLevel & 0xF) << SUNLIGHT_SHIFT);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint SetBlockLight(uint packedData, byte blockLightLevel)
        {
            return (packedData & ~BLOCKLIGHT_MASK) | (uint)((blockLightLevel & 0xF) << BLOCKLIGHT_SHIFT);
        }

        /// <summary>
        /// Sets the full 8-bit metadata field directly.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint SetMeta(uint packedData, byte meta)
        {
            return (packedData & ~META_MASK) | (uint)((meta) << META_SHIFT);
        }

        /// <summary>
        /// Sets the orientation bits within the metadata field.
        /// Note: This blindly overwrites the lower 3 bits of the metadata.
        /// </summary>
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
