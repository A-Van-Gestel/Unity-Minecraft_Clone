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
    }
}
