using Unity.Burst;
using Unity.Mathematics;

namespace Jobs.BurstData
{
    /// <summary>
    /// This class contains an exact copy of the bit-packing logic from VoxelData.
    /// It is separated into its own file to ensure it contains NO managed static fields
    /// (like arrays), which would prevent it from being used by a Burst-compiled job.
    /// </summary>
    [BurstCompile]
    public static class BurstVoxelDataBitMapping
    {
        // --- Constants for Bit Packing ---
        // Using Hex for clarity with bit positions
        private const uint ID_MASK = 0x000000FF; // Bits 0-7  (8-bits, Values 0-256)
        private const uint SUNLIGHT_MASK = 0x00000F00; // Bits 8-11 (4-bits, Values 0-15)
        private const uint BLOCKLIGHT_MASK = 0x0000F000; // Bits 12-15 (4-bits, Values 0-15)
        private const uint ORIENTATION_MASK = 0x00030000; // Bits 16-17 (2-bits, Values 0-3)
        // Bits 18-32 are reserved

        private const int ID_SHIFT = 0;
        private const int SUNLIGHT_SHIFT = 8;
        private const int BLOCKLIGHT_SHIFT = 12;
        private const int ORIENTATION_SHIFT = 16;

        // Inverse map for packing
        private static byte GetOrientationIndex(byte orientation)
        {
            switch (orientation)
            {
                case 1: return 0; // Front/North maps to index 0
                case 0: return 1; // Back/South maps to index 1
                case 4: return 2; // Left/West  maps to index 2
                case 5: return 3; // Right/East maps to index 3
                default: return 0; // Default to index 0 (Front) for any invalid orientation
            }
        }

        // --- Packing ---
        // Creates the initial packed value
        public static uint PackVoxelData(byte id, byte sunLight, byte blockLight, byte orientation)
        {
            uint packedData = 0;
            packedData |= (uint)((id & 0xFF) << ID_SHIFT); // ID: Ensure only 8 bits
            packedData |= (uint)((sunLight & 0xF) << SUNLIGHT_SHIFT); // Sunlight Level: Ensure only 4 bits
            packedData |= (uint)((blockLight & 0xF) << BLOCKLIGHT_SHIFT); // Blocklight Level: Ensure only 4 bits

            // Pack Orientation by getting its index from our helper method
            byte orientationIndex = GetOrientationIndex(orientation);
            packedData |= (uint)((orientationIndex & 0x3) << ORIENTATION_SHIFT); // Orientation: Ensure only 2 bits

            return packedData;
        }

        // --- Unpacking / Getters ---
        public static byte GetId(uint packedData)
        {
            return (byte)((packedData & ID_MASK) >> ID_SHIFT);
        }

        /// <summary>
        /// Returns the highest light level between sunlight and blocklight
        /// </summary>
        public static byte GetLight(uint packedData)
        {
            uint sunlightLevel = GetSunlight(packedData);
            uint blocklightLevel = GetBlocklight(packedData);
            return (byte)math.max(sunlightLevel, blocklightLevel);
        }

        public static byte GetSunlight(uint packedData)
        {
            return (byte)((packedData & SUNLIGHT_MASK) >> SUNLIGHT_SHIFT);
        }

        public static byte GetBlocklight(uint packedData)
        {
            return (byte)((packedData & BLOCKLIGHT_MASK) >> BLOCKLIGHT_SHIFT);
        }

        // Here we replace the array lookup with a Burst-compatible switch statement.
        public static byte GetOrientation(uint packedData)
        {
            byte orientationIndex = (byte)((packedData & ORIENTATION_MASK) >> ORIENTATION_SHIFT);
            switch (orientationIndex)
            {
                case 0: return 1; // Index 0 maps to Orientation 1 (Front/North)
                case 1: return 0; // Index 1 maps to Orientation 0 (Back/South)
                case 2: return 4; // Index 2 maps to Orientation 4 (Left/West)
                case 3: return 5; // Index 3 maps to Orientation 5 (Right/East)
                default: return 1; // Fallback to Front/North
            }
        }

        // --- Packing / Setters ---
        public static uint SetId(uint packedData, byte id)
        {
            return (packedData & ~ID_MASK) | (uint)((id & 0xFF) << ID_SHIFT);
        }

        public static uint SetSunLight(uint packedData, byte sunLightLevel)
        {
            return (packedData & ~SUNLIGHT_MASK) | (uint)((sunLightLevel & 0xF) << SUNLIGHT_SHIFT);
        }

        public static uint SetBlockLight(uint packedData, byte blockLightLevel)
        {
            return (packedData & ~BLOCKLIGHT_MASK) | (uint)((blockLightLevel & 0xF) << BLOCKLIGHT_SHIFT);
        }

        public static uint SetOrientation(uint packedData, byte orientation)
        {
            byte orientationIndex = GetOrientationIndex(orientation);
            return (packedData & ~ORIENTATION_MASK) | (uint)((orientationIndex & 0x3) << ORIENTATION_SHIFT);
        }
    }
}