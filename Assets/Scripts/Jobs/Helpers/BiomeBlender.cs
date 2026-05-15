using System;
using Data.WorldTypes;
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
        /// Calculates the blended terrain height at a global (x, z) column using Multi-Noise splines.
        /// Returns float (not int) to preserve sub-block precision for the Dynamic Density Band.
        /// </summary>
        /// <param name="borderFade">0.0 at a Voronoi cell boundary, 1.0 deep inside the primary biome.
        /// Used by the generation job to attenuate 3D density near biome borders, preventing cliff tearing.</param>
        public static unsafe float CalculateBlendedTerrainHeight(
            int globalX,
            int globalZ,
            ref FastNoiseLite selectionNoise,
            ref NativeArray<StandardBiomeAttributesJobData> biomes,
            ref MultiNoiseData multiNoise,
            out float borderFade)
        {
            selectionNoise.GetCellularEdgeData(globalX, globalZ, out FastNoiseLite.CellularEdgeData edgeData);

            int* b = stackalloc int[9];
            float* rad = stackalloc float[9];
            float* bw = stackalloc float[9];
            BlendCurve* curves = stackalloc BlendCurve[9];
            for (int i = 0; i < 9; i++)
            {
                b[i] = GetBiomeIndex(edgeData.Hashes[i], biomes.Length);
                rad[i] = biomes[b[i]].BlendRadius;
                bw[i] = biomes[b[i]].BlendWeight;
                curves[i] = biomes[b[i]].BlendCurve;
            }

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

            float wiggle = selectionNoise.GetNoise(globalX * 0.25f, globalZ * 0.25f) * 0.5f * localBlendRadius;
            float activeRadius = math.max(0.001f, localBlendRadius + wiggle);

            // Border fade: how deep we are inside the primary Voronoi cell.
            // (dist1 - dist0) / activeRadius gives 0.0 at the boundary and grows toward 1.0 inside.
            float edgeGap = edgeData.Distances[1] - dist0;
            borderFade = math.saturate(edgeGap / activeRadius);

            float* raw = stackalloc float[9];
            float totalRaw = 0f;
            for (int i = 0; i < 9; i++)
            {
                raw[i] = math.max(0f, 1f - (edgeData.Distances[i] - dist0) / activeRadius) * bw[i];
                totalRaw += raw[i];
            }

            float* w = stackalloc float[9];
            float totalSmooth = 0f;
            for (int i = 0; i < 9; i++)
            {
                float norm = raw[i] / totalRaw;
                w[i] = ApplyCurve(norm, curves[i]);
                totalSmooth += w[i];
            }

            float finalHeight = 0f;
            for (int i = 0; i < 9; i++)
            {
                if (w[i] > 0.001f)
                {
                    finalHeight += EvaluateMultiNoiseHeight(globalX, globalZ, b[i], ref biomes, ref multiNoise)
                                   * (w[i] / totalSmooth);
                }
            }

            return finalHeight;
        }

        /// <summary>
        /// Legacy overload using single terrain noise per biome. Retained for <c>GetVoxel</c> spawn-point fallback.
        /// </summary>
        [Obsolete("Use the MultiNoiseData overload. Retained for GetVoxel legacy fallback.")]
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

            // Extract all 9 biome indices, radii, blend weights, and curves
            int* b = stackalloc int[9];
            float* rad = stackalloc float[9];
            float* bw = stackalloc float[9];
            BlendCurve* curves = stackalloc BlendCurve[9];
            for (int i = 0; i < 9; i++)
            {
                b[i] = GetBiomeIndex(edgeData.Hashes[i], biomes.Length);
                rad[i] = biomes[b[i]].BlendRadius;
                bw[i] = biomes[b[i]].BlendWeight;
                curves[i] = biomes[b[i]].BlendCurve;
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

            // 1. Calculate Raw Linear Inverse-Distance Weights, scaled by per-biome BlendWeight.
            // Multiplying BEFORE normalization ensures that low-weight biomes (e.g., Mountains 0.2)
            // barely influence neighbors, while deep inside the biome they still normalize to 100%.
            float* raw = stackalloc float[9];
            float totalRaw = 0f;
            for (int i = 0; i < 9; i++)
            {
                raw[i] = math.max(0f, 1f - (edgeData.Distances[i] - dist0) / activeRadius) * bw[i];
                totalRaw += raw[i];
            }

            // 2. Normalize weights and 3. Apply per-biome interpolation curve
            float* w = stackalloc float[9];
            float totalSmooth = 0f;
            for (int i = 0; i < 9; i++)
            {
                float norm = raw[i] / totalRaw;
                w[i] = ApplyCurve(norm, curves[i]);
                totalSmooth += w[i];
            }

            // 4. Calculate final height
            float finalHeight = 0f;
            for (int i = 0; i < 9; i++)
            {
                if (w[i] > 0.001f)
                {
                    finalHeight += EvaluateLegacyHeight(globalX, globalZ, b[i], ref biomes, ref terrainNoises)
                                   * (w[i] / totalSmooth);
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

        /// <summary>
        /// Evaluates terrain height using the Multi-Noise spline system:
        /// <c>BaseTerrainHeight + Continentalness + (PeaksValleys * Erosion)</c>.
        /// </summary>
        private static float EvaluateMultiNoiseHeight(
            int x, int z, int biomeIdx,
            ref NativeArray<StandardBiomeAttributesJobData> biomes,
            ref MultiNoiseData mn)
        {
            StandardBiomeAttributesJobData b = biomes[biomeIdx];
            float cont = mn.ContinentalnessSplines[biomeIdx].Evaluate(
                mn.ContinentalnessNoises[biomeIdx].GetNoise(x, z));
            float erosion = mn.ErosionSplines[biomeIdx].Evaluate(
                mn.ErosionNoises[biomeIdx].GetNoise(x, z));
            float pv = mn.PeaksValleysSplines[biomeIdx].Evaluate(
                mn.PeaksValleysNoises[biomeIdx].GetNoise(x, z));
            return b.BaseTerrainHeight + cont + (pv * erosion);
        }

        /// <summary>
        /// Legacy single-noise height evaluation: <c>BaseTerrainHeight + noise * TerrainAmplitude</c>.
        /// </summary>
        private static float EvaluateLegacyHeight(
            int x, int z, int biomeIdx,
            ref NativeArray<StandardBiomeAttributesJobData> biomes,
            ref NativeArray<FastNoiseLite> noises)
        {
            StandardBiomeAttributesJobData b = biomes[biomeIdx];
            float noise = noises[biomeIdx].GetNoise(x, z);
            return b.BaseTerrainHeight + noise * b.TerrainAmplitude;
        }

        /// <summary>
        /// Applies the selected interpolation curve to a normalized [0,1] weight value.
        /// Each curve reshapes how the biome's influence ramps across the transition zone.
        /// </summary>
        private static float ApplyCurve(float t, BlendCurve curve)
        {
            switch (curve)
            {
                case BlendCurve.Linear:
                    return t;
                case BlendCurve.SmootherStep:
                    // Quintic: 6t⁵ − 15t⁴ + 10t³
                    return t * t * t * (t * (t * 6f - 15f) + 10f);
                case BlendCurve.SmoothStep:
                default:
                    // Hermite: 3t² − 2t³ (equivalent to math.smoothstep(0, 1, t))
                    return t * t * (3f - 2f * t);
            }
        }
    }
}
