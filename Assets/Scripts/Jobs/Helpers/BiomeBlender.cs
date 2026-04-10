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
        /// Evaluates the true mathematical heights of the top 3 closest biomes and performs
        /// an Inverse-Distance-Weighted (IDW) interpolation between them based on Voronoi Edge distance.
        /// </summary>
        public static int CalculateBlendedTerrainHeight(
            int globalX,
            int globalZ,
            float blendRadius,
            ref FastNoiseLite selectionNoise,
            ref NativeArray<StandardBiomeAttributesJobData> biomes,
            ref NativeArray<FastNoiseLite> terrainNoises)
        {
            // Retrieve top 3 overlapping cells natively
            selectionNoise.GetCellularEdgeData(globalX, globalZ,
                out int hash0, out float dist0,
                out int hash1, out float dist1,
                out int hash2, out float dist2);

            // We use a linear falloff clamped by the global blend radius.
            // dist0 is the closest cell (always).
            // At the cell border, dist1 equals dist0 -> weight1 = 1.0
            // In the dead center of a cell, (dist1 - dist0) is large -> weight1 = 0.0
            float w0 = 1f;
            float w1 = math.max(0f, 1f - (dist1 - dist0) / blendRadius);
            float w2 = math.max(0f, 1f - (dist2 - dist0) / blendRadius);

            float totalWeight = w0 + w1 + w2;

            int b0 = GetBiomeIndex(hash0, biomes.Length);
            float finalHeight = EvaluateHeight(globalX, globalZ, b0, ref biomes, ref terrainNoises) * (w0 / totalWeight);

            if (w1 > 0.001f)
            {
                int b1 = GetBiomeIndex(hash1, biomes.Length);
                finalHeight += EvaluateHeight(globalX, globalZ, b1, ref biomes, ref terrainNoises) * (w1 / totalWeight);
            }

            if (w2 > 0.001f)
            {
                int b2 = GetBiomeIndex(hash2, biomes.Length);
                finalHeight += EvaluateHeight(globalX, globalZ, b2, ref biomes, ref terrainNoises) * (w2 / totalWeight);
            }

            return (int)math.floor(finalHeight);
        }

        private static int GetBiomeIndex(int hash, int biomesLength)
        {
            // FastNoiseLite natively maps cellular hash to a [-1, 1] interval.
            float noiseValue = hash * (1.0f / 2147483648.0f);

            // Replicate FastNoiseConfig.normalizeToZeroOne = true
            noiseValue = (noiseValue + 1.0f) * 0.5f;

            int idx = (int)math.floor(noiseValue * biomesLength);
            return math.clamp(idx, 0, biomesLength - 1);
        }

        private static float EvaluateHeight(int x, int z, int biomeIdx, ref NativeArray<StandardBiomeAttributesJobData> biomes, ref NativeArray<FastNoiseLite> noises)
        {
            StandardBiomeAttributesJobData b = biomes[biomeIdx];
            float noise = noises[biomeIdx].GetNoise(x, z);
            return b.BaseTerrainHeight + noise * b.TerrainAmplitude;
        }
    }
}
