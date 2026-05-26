using Data.WorldTypes;
using Jobs.Data;
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
        /// Evaluates a noise value at the specified coordinates, handling 3D sampling for cave/lode modes.
        /// </summary>
        /// <param name="noise">The noise instance to evaluate.</param>
        /// <param name="worldX">Global X coordinate.</param>
        /// <param name="worldZ">Global Z coordinate.</param>
        /// <param name="sliceY">Y slice height for 3D evaluation.</param>
        /// <param name="is3D">If true, evaluates as a 3D noise slice (cave/lode). If false, evaluates as 2D terrain noise.</param>
        /// <param name="caveMode">Cave evaluation mode. Only used when <paramref name="is3D"/> is true.</param>
        public static float EvaluateNoiseVal(FastNoiseLite noise, float worldX, float worldZ, float sliceY, bool is3D, CaveMode caveMode = CaveMode.Cheese)
        {
            if (!is3D)
            {
                return noise.GetNoise(worldX, worldZ);
            }

            if (caveMode == CaveMode.Spaghetti)
            {
                float ab = noise.GetNoise(worldX, sliceY);
                float bc = noise.GetNoise(sliceY, worldZ);
                float ac = noise.GetNoise(worldX, worldZ);
                float ba = noise.GetNoise(sliceY, worldX);
                float cb = noise.GetNoise(worldZ, sliceY);
                float ca = noise.GetNoise(worldZ, worldX);
                return (ab + bc + ac + ba + cb + ca) / 6f;
            }

            if (caveMode == CaveMode.Noodle)
            {
                float raw = noise.GetNoise(worldX, sliceY, worldZ);
                return 1.0f - (math.sqrt(raw * raw + StandardCaveLayerJobData.NoodleSmoothRadiusSq) - StandardCaveLayerJobData.NoodleSmoothOffset);
            }

            // Cheese (default) and Lode — standard 3D evaluation
            return noise.GetNoise(worldX, sliceY, worldZ);
        }
    }
}
