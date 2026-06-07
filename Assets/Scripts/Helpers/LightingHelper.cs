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
        /// Stamps sunlight=15 on every voxel in a uint packed data array.
        /// Used when lighting is disabled to ensure full brightness.
        /// </summary>
        public static void StampFullBrightSunlight(NativeArray<uint> map)
        {
            for (int v = 0; v < map.Length; v++)
                map[v] = BurstVoxelDataBitMapping.SetSunLight(map[v], 15);
        }

        /// <summary>
        /// Stamps sunlight=15 on every voxel in a ushort light data array.
        /// Used when lighting is disabled to ensure full brightness.
        /// </summary>
        public static void StampFullBrightSunlight(NativeArray<ushort> lightMap)
        {
            for (int v = 0; v < lightMap.Length; v++)
                lightMap[v] = LightBitMapping.SetSkyLight(lightMap[v], 15);
        }
    }
}
