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
            // Retrieve top 4 overlapping cells natively
            // (Extracting 4 cells is crucial to prevent instant discontinuity flips at perfect 4-junction corners)
            selectionNoise.GetCellularEdgeData(globalX, globalZ,
                out int hash0, out float dist0,
                out int hash1, out float dist1,
                out int hash2, out float dist2,
                out int hash3, out float dist3);

            // Add an organic low-frequency wiggle to the blend radius.
            // This prevents Voronoi boundaries from looking overtly straight or artificial.
            float wiggle = selectionNoise.GetNoise(globalX * 0.25f, globalZ * 0.25f) * 0.5f * blendRadius;
            float activeRadius = math.max(0.001f, blendRadius + wiggle);

            // 1. Calculate Raw Linear Inverse-Distance Weights targeting the boundary edge
            // At the cell border, dist1 equals dist0 -> raw1 = 1.0.
            // Deep inside cell 0, raw1 = 0.0.
            float raw0 = 1f;
            float raw1 = math.max(0f, 1f - (dist1 - dist0) / activeRadius);
            float raw2 = math.max(0f, 1f - (dist2 - dist0) / activeRadius);
            float raw3 = math.max(0f, 1f - (dist3 - dist0) / activeRadius);

            float totalRaw = raw0 + raw1 + raw2 + raw3;

            // 2. Normalize weights over the 4-biome overlap
            float norm0 = raw0 / totalRaw;
            float norm1 = raw1 / totalRaw;
            float norm2 = raw2 / totalRaw;
            float norm3 = raw3 / totalRaw;

            // 3. Smoothstep the normalized ratios.
            // By normalizing FIRST, the steepest point of the S-curve aligns perfectly
            // atop the Voronoi boundary (norm = 0.5), eliminating unnatural interpolation plateaus.
            float w0 = math.smoothstep(0f, 1f, norm0);
            float w1 = math.smoothstep(0f, 1f, norm1);
            float w2 = math.smoothstep(0f, 1f, norm2);
            float w3 = math.smoothstep(0f, 1f, norm3);

            float totalSmooth = w0 + w1 + w2 + w3;

            // 4. Calculate final height
            int b0 = GetBiomeIndex(hash0, biomes.Length);
            float finalHeight = EvaluateHeight(globalX, globalZ, b0, ref biomes, ref terrainNoises) * (w0 / totalSmooth);

            if (w1 > 0.001f)
            {
                int b1 = GetBiomeIndex(hash1, biomes.Length);
                finalHeight += EvaluateHeight(globalX, globalZ, b1, ref biomes, ref terrainNoises) * (w1 / totalSmooth);
            }

            if (w2 > 0.001f)
            {
                int b2 = GetBiomeIndex(hash2, biomes.Length);
                finalHeight += EvaluateHeight(globalX, globalZ, b2, ref biomes, ref terrainNoises) * (w2 / totalSmooth);
            }

            if (w3 > 0.001f)
            {
                int b3 = GetBiomeIndex(hash3, biomes.Length);
                finalHeight += EvaluateHeight(globalX, globalZ, b3, ref biomes, ref terrainNoises) * (w3 / totalSmooth);
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
