using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Mathematics;

namespace Jobs.BurstData
{
    /// <summary>
    /// Bit-packing helpers for the <c>ushort</c> per-voxel light array introduced in Phase 2.
    /// Layout: <c>[Sky: 4][BlockR: 4][BlockG: 4][BlockB: 4]</c> = 16 bits total.
    /// </summary>
    [BurstCompile]
    public static class LightBitMapping
    {
        // --- Bit Layout Constants ---
        private const int SKY_SHIFT = 0;
        private const int BLOCK_R_SHIFT = 4;
        private const int BLOCK_G_SHIFT = 8;
        private const int BLOCK_B_SHIFT = 12;
        private const int CHANNEL_MASK = 0xF;

        // --- Composite masks for clearing multiple channels at once ---
        private const int BLOCKLIGHT_RGB_MASK = (CHANNEL_MASK << BLOCK_R_SHIFT) |
                                                (CHANNEL_MASK << BLOCK_G_SHIFT) |
                                                (CHANNEL_MASK << BLOCK_B_SHIFT);

        // --- Getters ---

        /// <summary>
        /// Extracts the sky light level (0-15) from the packed light data.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetSkyLight(ushort lightData)
        {
            return (byte)((lightData >> SKY_SHIFT) & CHANNEL_MASK);
        }

        /// <summary>
        /// Extracts the red blocklight channel (0-15) from the packed light data.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetBlocklightR(ushort lightData)
        {
            return (byte)((lightData >> BLOCK_R_SHIFT) & CHANNEL_MASK);
        }

        /// <summary>
        /// Extracts the green blocklight channel (0-15) from the packed light data.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetBlocklightG(ushort lightData)
        {
            return (byte)((lightData >> BLOCK_G_SHIFT) & CHANNEL_MASK);
        }

        /// <summary>
        /// Extracts the blue blocklight channel (0-15) from the packed light data.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetBlocklightB(ushort lightData)
        {
            return (byte)((lightData >> BLOCK_B_SHIFT) & CHANNEL_MASK);
        }

        /// <summary>
        /// Returns the maximum of the three blocklight RGB channels (0-15).
        /// Used for scalar compatibility and mob spawning checks.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetMaxBlocklight(ushort lightData)
        {
            int r = (lightData >> BLOCK_R_SHIFT) & CHANNEL_MASK;
            int g = (lightData >> BLOCK_G_SHIFT) & CHANNEL_MASK;
            int b = (lightData >> BLOCK_B_SHIFT) & CHANNEL_MASK;
            return (byte)math.max(r, math.max(g, b));
        }

        // --- Setters ---

        /// <summary>
        /// Updates the sky light level within the packed light data.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort SetSkyLight(ushort lightData, byte level)
        {
            return (ushort)((lightData & ~(CHANNEL_MASK << SKY_SHIFT)) |
                            ((level & CHANNEL_MASK) << SKY_SHIFT));
        }

        /// <summary>
        /// Updates the red blocklight channel within the packed light data.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort SetBlocklightR(ushort lightData, byte level)
        {
            return (ushort)((lightData & ~(CHANNEL_MASK << BLOCK_R_SHIFT)) |
                            ((level & CHANNEL_MASK) << BLOCK_R_SHIFT));
        }

        /// <summary>
        /// Updates the green blocklight channel within the packed light data.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort SetBlocklightG(ushort lightData, byte level)
        {
            return (ushort)((lightData & ~(CHANNEL_MASK << BLOCK_G_SHIFT)) |
                            ((level & CHANNEL_MASK) << BLOCK_G_SHIFT));
        }

        /// <summary>
        /// Updates the blue blocklight channel within the packed light data.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort SetBlocklightB(ushort lightData, byte level)
        {
            return (ushort)((lightData & ~(CHANNEL_MASK << BLOCK_B_SHIFT)) |
                            ((level & CHANNEL_MASK) << BLOCK_B_SHIFT));
        }

        /// <summary>
        /// Updates all three blocklight RGB channels at once, preserving the sunlight bits.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort SetBlocklightRGB(ushort lightData, byte r, byte g, byte b)
        {
            return (ushort)((lightData & ~BLOCKLIGHT_RGB_MASK) |
                            ((r & CHANNEL_MASK) << BLOCK_R_SHIFT) |
                            ((g & CHANNEL_MASK) << BLOCK_G_SHIFT) |
                            ((b & CHANNEL_MASK) << BLOCK_B_SHIFT));
        }

        // --- Full Packing ---

        /// <summary>
        /// Packs all four light channels into a single <c>ushort</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort PackLightData(byte sky, byte blockR, byte blockG, byte blockB)
        {
            return (ushort)(((sky & CHANNEL_MASK) << SKY_SHIFT) |
                            ((blockR & CHANNEL_MASK) << BLOCK_R_SHIFT) |
                            ((blockG & CHANNEL_MASK) << BLOCK_G_SHIFT) |
                            ((blockB & CHANNEL_MASK) << BLOCK_B_SHIFT));
        }
    }
}
