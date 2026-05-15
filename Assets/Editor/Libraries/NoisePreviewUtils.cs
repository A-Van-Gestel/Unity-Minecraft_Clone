using Data.WorldTypes;
using Editor.WorldTools;
using Libraries;
using Unity.Mathematics;

namespace Editor.Libraries
{
    /// <summary>
    /// Shared utility class for noise evaluation and texture generation in Editor preview windows.
    /// </summary>
    public static class NoisePreviewUtils
    {
        /// <summary>
        /// Evaluates a noise value at the specified coordinates, handling special 3D sampling (e.g. for caves/lodes).
        /// </summary>
        public static float EvaluateNoiseVal(FastNoiseLite noise, float worldX, float worldZ, float sliceY, NoisePreviewWindow.NoiseTarget target, CaveMode caveMode)
        {
            if (target == NoisePreviewWindow.NoiseTarget.Lode || (target == NoisePreviewWindow.NoiseTarget.CaveLayer && caveMode == CaveMode.Cheese))
            {
                return noise.GetNoise(worldX, sliceY, worldZ);
            }
            else if (target == NoisePreviewWindow.NoiseTarget.CaveLayer && caveMode == CaveMode.Spaghetti)
            {
                float ab = noise.GetNoise(worldX, sliceY);
                float bc = noise.GetNoise(sliceY, worldZ);
                float ac = noise.GetNoise(worldX, worldZ);
                float ba = noise.GetNoise(sliceY, worldX);
                float cb = noise.GetNoise(worldZ, sliceY);
                float ca = noise.GetNoise(worldZ, worldX);
                return (ab + bc + ac + ba + cb + ca) / 6f;
            }
            else if (target == NoisePreviewWindow.NoiseTarget.CaveLayer && caveMode == CaveMode.Noodle)
            {
                return 1.0f - math.abs(noise.GetNoise(worldX, sliceY, worldZ));
            }

            return noise.GetNoise(worldX, worldZ);
        }
    }
}
