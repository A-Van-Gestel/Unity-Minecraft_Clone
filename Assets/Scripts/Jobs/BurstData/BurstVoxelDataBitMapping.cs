using Unity.Burst;

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
        private const ushort ID_MASK = 0x00FF; // Bits 0-7   (00000000 11111111) (8-bits, Values 0-256)
        private const ushort LIGHT_MASK = 0x0F00; // Bits 8-11 (00001111 00000000) (4-bits, Values 0-15)
        private const ushort ORIENTATION_MASK = 0x3000; // Bits 12-13 (00110000 00000000) (2-bits, Values 0-3)
        // Bits 14-15 are reserved (0xC000)

        private const int ID_SHIFT = 0;
        private const int LIGHT_SHIFT = 8;
        private const int ORIENTATION_SHIFT = 12;

        // --- Unpacking ---
        public static byte GetId(ushort packedData)
        {
            return (byte)((packedData & ID_MASK) >> ID_SHIFT);
        }

        public static byte GetLight(ushort packedData)
        {
            return (byte)((packedData & LIGHT_MASK) >> LIGHT_SHIFT);
        }

        // Here we replace the array lookup with a Burst-compatible switch statement.
        public static byte GetOrientation(ushort packedData)
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
    }
}