using Jobs.Data;
using Libraries;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Jobs.Helpers
{
    /// <summary>
    /// Burst-compatible helper struct to handle smooth transitions and interpolation across Biome boundaries.
    /// Replaces strict Voronoi cell edges with an N-tap smoothing convolution.
    /// </summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Default)]
    public static class BiomeBlender
    {
        /// <summary>
        /// A 5-tap kernel layout evaluating the current block and four directional neighbors
        /// at an 8-block offset. Averages the Heights to smooth harsh biome cliffs natively.
        /// </summary>
        public static int CalculateBlendedTerrainHeight(
            int globalX,
            int globalZ,
            ref FastNoiseLite selectionNoise,
            ref NativeArray<StandardBiomeAttributesJobData> biomes,
            ref NativeArray<FastNoiseLite> terrainNoises)
        {
            // 5-tap cross blend
            // Center, Left, Right, Back, Forward (8 block radius)
            // Weighting heavily favors the center block (50%), then 12.5% per edge to create a rolling hill transition.
            NativeArray<int> dx = new NativeArray<int>(5, Allocator.Temp) { [0] = 0, [1] = -8, [2] = 8, [3] = 0, [4] = 0 };
            NativeArray<int> dz = new NativeArray<int>(5, Allocator.Temp) { [0] = 0, [1] = 0, [2] = 0, [3] = -8, [4] = 8 };
            NativeArray<float> weights = new NativeArray<float>(5, Allocator.Temp) { [0] = 0.5f, [1] = 0.125f, [2] = 0.125f, [3] = 0.125f, [4] = 0.125f };

            float totalHeight = 0f;

            for (int i = 0; i < 5; i++)
            {
                int sampleX = globalX + dx[i];
                int sampleZ = globalZ + dz[i];

                float bNoise = selectionNoise.GetNoise(sampleX, sampleZ);
                // Noise is normalized [0,1], no need for +1 offsets
                int bIndex = (int)math.floor(bNoise * biomes.Length);
                bIndex = math.clamp(bIndex, 0, biomes.Length - 1);

                StandardBiomeAttributesJobData biome = biomes[bIndex];
                float hNoise = terrainNoises[bIndex].GetNoise(sampleX, sampleZ);

                float evaluatedHeight = biome.BaseTerrainHeight + hNoise * biome.TerrainAmplitude;
                totalHeight += evaluatedHeight * weights[i];
            }

            dx.Dispose();
            dz.Dispose();
            weights.Dispose();

            return (int)math.floor(totalHeight);
        }
    }
}
