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
        public static unsafe int CalculateBlendedTerrainHeight(
            int globalX,
            int globalZ,
            ref FastNoiseLite selectionNoise,
            ref NativeArray<StandardBiomeAttributesJobData> biomes,
            ref NativeArray<FastNoiseLite> terrainNoises)
        {
            // Retrieve top 9 overlapping cells natively
            // (Extracting all 9 cells guarantees no Voronoi borders can be popped abruptly by Top K thresholds bounding out active neighbors)
            selectionNoise.GetCellularEdgeData(globalX, globalZ, out FastNoiseLite.CellularEdgeData edgeData);

            // Extract all 9 biome indices and radii
            int* b = stackalloc int[9];
            float* rad = stackalloc float[9];
            for (int i = 0; i < 9; i++)
            {
                b[i] = GetBiomeIndex(edgeData.Hashes[i], biomes.Length);
                rad[i] = biomes[b[i]].BlendRadius;
            }

            // Calculate a generic wide 1.0f envelope to linearly mix the radii themselves across the boundary crossings.
            float trSum = 0f;
            float localBlendRadiusSum = 0f;
            float dist0 = edgeData.Distances[0];

            for (int i = 0; i < 9; i++)
            {
                float tr = math.max(0f, 1f - (edgeData.Distances[i] - dist0));
                trSum += tr;
                localBlendRadiusSum += tr * rad[i];
            }

            float localBlendRadius = localBlendRadiusSum / trSum;

            // Add an organic low-frequency wiggle to the dynamically mixed blend radius.
            float wiggle = selectionNoise.GetNoise(globalX * 0.25f, globalZ * 0.25f) * 0.5f * localBlendRadius;
            float activeRadius = math.max(0.001f, localBlendRadius + wiggle);

            // 1. Calculate Raw Linear Inverse-Distance Weights targeting the boundary edge
            float* raw = stackalloc float[9];
            float totalRaw = 0f;
            for (int i = 0; i < 9; i++)
            {
                raw[i] = math.max(0f, 1f - (edgeData.Distances[i] - dist0) / activeRadius);
                totalRaw += raw[i];
            }

            // 2. Normalize weights and 3. Smoothstep
            float* w = stackalloc float[9];
            float totalSmooth = 0f;
            for (int i = 0; i < 9; i++)
            {
                float norm = raw[i] / totalRaw;
                w[i] = math.smoothstep(0f, 1f, norm);
                totalSmooth += w[i];
            }

            // 4. Calculate final height
            float finalHeight = 0f;
            for (int i = 0; i < 9; i++)
            {
                if (w[i] > 0.001f)
                {
                    finalHeight += EvaluateHeight(globalX, globalZ, b[i], ref biomes, ref terrainNoises) * (w[i] / totalSmooth);
                }
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
