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
        /// <summary>Wrap period mask (2²² − 1 blocks) for the float-only wiggle snoise on the Precise64 path.</summary>
        private const int WIGGLE_WRAP_MASK = (1 << 22) - 1;

        /// <summary>Half the wiggle wrap period — offsets the wrap seams away from the spawn region.</summary>
        private const int WIGGLE_WRAP_HALF = 1 << 21;

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
            bool isSingleBiomeMode,
            int forceBiomeIndex,
            out float borderFade)
        {
            if (isSingleBiomeMode)
            {
                borderFade = 1.0f;
                return EvaluateMultiNoiseHeight(globalX, globalZ, forceBiomeIndex, ref biomes, ref multiNoise);
            }

            selectionNoise.GetCellularEdgeData(globalX, globalZ, out FastNoiseLite.CellularEdgeData edgeData);

            const int N = FastNoiseLite.CellularEdgeData.MaxCells;
            int* b = stackalloc int[N];
            float* rad = stackalloc float[N];
            float* bw = stackalloc float[N];
            BlendCurve* curves = stackalloc BlendCurve[N];
            for (int i = 0; i < N; i++)
            {
                b[i] = GetBiomeIndex(edgeData.Hashes[i], biomes.Length);
                rad[i] = biomes[b[i]].BlendRadius;
                bw[i] = biomes[b[i]].BlendWeight;
                curves[i] = biomes[b[i]].BlendCurve;
            }

            float trSum = 0f;
            float localBlendRadiusSum = 0f;
            float dist0 = edgeData.Distances[0];

            for (int i = 0; i < N; i++)
            {
                float tr = math.max(0f, 1f - (edgeData.Distances[i] - dist0));
                trSum += tr;
                localBlendRadiusSum += tr * rad[i];
            }

            float localBlendRadius = localBlendRadiusSum / trSum;

            // Organic wiggle using continuous simplex noise (not Cellular — CellValue has step discontinuities).
            // Prime offsets prevent axis-alignment with terrain noise; frequency ~0.001 matches the original 0.25x Cellular scale.
            // Precise64: snoise is float-only, so its inputs wrap to a 2^22-block period (seams half-period
            // offset from spawn) — the wiggle pattern repeats invisibly instead of flattening far out.
            bool preciseNoise = selectionNoise.GetCoordinatePrecision() == FastNoiseLite.CoordinatePrecision.Precise64;
            int wgx = preciseNoise ? ((globalX + WIGGLE_WRAP_HALF) & WIGGLE_WRAP_MASK) - WIGGLE_WRAP_HALF : globalX;
            int wgz = preciseNoise ? ((globalZ + WIGGLE_WRAP_HALF) & WIGGLE_WRAP_MASK) - WIGGLE_WRAP_HALF : globalZ;
            float wiggle = noise.snoise(new float2(wgx * 0.001f + 7919f, wgz * 0.001f + 6271f)) * 0.5f * localBlendRadius;
            float activeRadius = math.max(0.001f, localBlendRadius + wiggle);

            // Border fade: how deep we are inside the primary Voronoi cell.
            // Uses the same activeRadius and per-biome BlendCurve as the height blending,
            // then scales by BlendWeight so low-weight biomes (e.g., Mountains 0.2) fade faster.
            float edgeGap = edgeData.Distances[1] - dist0;
            float linearFade = math.saturate(edgeGap / activeRadius);
            borderFade = ApplyCurve(linearFade, curves[0]) * math.saturate(bw[0]);

            float* raw = stackalloc float[N];
            float totalRaw = 0f;
            for (int i = 0; i < N; i++)
            {
                raw[i] = math.max(0f, 1f - (edgeData.Distances[i] - dist0) / activeRadius) * bw[i];
                totalRaw += raw[i];
            }

            float* w = stackalloc float[N];
            float totalSmooth = 0f;
            for (int i = 0; i < N; i++)
            {
                float norm = raw[i] / totalRaw;
                w[i] = ApplyCurve(norm, curves[i]);
                totalSmooth += w[i];
            }

            float finalHeight = 0f;
            for (int i = 0; i < N; i++)
            {
                if (w[i] > 0.001f)
                {
                    finalHeight += EvaluateMultiNoiseHeight(globalX, globalZ, b[i], ref biomes, ref multiNoise)
                                   * (w[i] / totalSmooth);
                }
            }

            return finalHeight;
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
