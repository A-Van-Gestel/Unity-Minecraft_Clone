using System;
using Jobs.BurstData;
using Unity.Collections;

namespace Helpers
{
    /// <summary>
    /// Shared lighting utility methods used by snapshot and editor pipelines.
    /// Centralizes the "lighting disabled → full bright" stamp to prevent divergence.
    /// </summary>
    public static class LightingHelper
    {
        private const ushort BLOCKLIGHT_RGB_MASK = 0xFFF0;
        private const int SKY_MASK = 0xF;

        /// <summary>
        /// Stamps sky light=15 on every entry in a ushort light data array.
        /// Used when lighting is disabled to ensure full brightness.
        /// </summary>
        public static void StampFullBrightSunlight(NativeArray<ushort> lightMap)
        {
            for (int v = 0; v < lightMap.Length; v++)
                lightMap[v] = LightBitMapping.SetSkyLight(lightMap[v], 15);
        }

        /// <summary>
        /// Bulk-fills a managed LightData array with a uniform sky light level (blocklight R/G/B = 0).
        /// Used by the serializer for uniform-sky sections and by the pipeline when lighting is disabled.
        /// </summary>
        /// <param name="lightData">The managed ushort array to fill.</param>
        /// <param name="skyLevel">The uniform sky light level (0-15).</param>
        public static void FillUniformSkyLight(ushort[] lightData, byte skyLevel)
        {
            ushort packed = LightBitMapping.PackLightData(skyLevel, 0, 0, 0);
            Array.Fill(lightData, packed);
        }

        /// <summary>
        /// Bulk-fills a slice of a NativeArray with a uniform sky light level (blocklight R/G/B = 0).
        /// Used by <c>GetChunkLightMapForJob</c> to populate job input from compact sections.
        /// </summary>
        /// <param name="lightMap">The NativeArray to fill into.</param>
        /// <param name="offset">Start index in the array.</param>
        /// <param name="count">Number of elements to fill.</param>
        /// <param name="skyLevel">The uniform sky light level (0-15).</param>
        public static void FillUniformSkyLight(NativeArray<ushort> lightMap, int offset, int count, byte skyLevel)
        {
            ushort packed = LightBitMapping.PackLightData(skyLevel, 0, 0, 0);
            for (int i = 0; i < count; i++)
                lightMap[offset + i] = packed;
        }

        /// <summary>
        /// Single-pass scan of <paramref name="lightData"/> to classify content: blocklight presence,
        /// any non-zero light, sky uniformity, and the uniform sky level (if all sky values are equal).
        /// </summary>
        public static void ClassifyLightData(ushort[] lightData,
            out bool hasBlocklight, out bool hasAnyLight,
            out bool isUniformSky, out byte uniformSkyLevel)
        {
            hasBlocklight = false;
            hasAnyLight = false;
            isUniformSky = true;
            uniformSkyLevel = (byte)(lightData[0] & SKY_MASK);

            foreach (ushort t in lightData)
            {
                if ((t & BLOCKLIGHT_RGB_MASK) != 0)
                    hasBlocklight = true;

                if (t != 0)
                    hasAnyLight = true;

                if ((t & SKY_MASK) != uniformSkyLevel)
                    isUniformSky = false;

                if (hasBlocklight && !isUniformSky)
                    return;
            }
        }
    }
}
