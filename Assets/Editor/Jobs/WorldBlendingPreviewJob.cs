using System.Runtime.InteropServices;
using Data.WorldTypes;
using Jobs.Data;
using Jobs.Helpers;
using Libraries;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Editor.Jobs
{
    /// <summary>
    /// Render mode for the world blending preview job.
    /// </summary>
    public enum WorldBlendingMode
    {
        Heightmap = 0,
        BiomeVoronoi = 1,
        BlendWeightHeatmap = 2,
        BiomeBorderFade = 3,
    }

    /// <summary>
    /// Burst-compiled parallel job for multi-biome world blending preview.
    /// Each work item evaluates one pixel using <see cref="BiomeBlender.CalculateBlendedTerrainHeight"/>.
    /// </summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public struct WorldBlendingPreviewJob : IJobParallelFor
    {
        #region Configuration

        [ReadOnly]
        public int TextureSize;

        [ReadOnly]
        public float Zoom;

        [ReadOnly]
        public float OffsetX;

        [ReadOnly]
        public float OffsetZ;

        [ReadOnly]
        public WorldBlendingMode Mode;

        [ReadOnly]
        public int SeaLevel;

        [ReadOnly]
        public int BiomeCount;

        [ReadOnly]
        public int TargetBiomeIndex;

        [MarshalAs(UnmanagedType.U1)]
        [ReadOnly]
        public bool ShowWaterLevel;

        #endregion

        #region Noise & Biome Data

        public FastNoiseLite SelectionNoise;

        [ReadOnly]
        public NativeArray<StandardBiomeAttributesJobData> Biomes;

        [ReadOnly]
        public MultiNoiseData MultiNoise;

        #endregion

        /// <summary>RGBA32 pixel output. Length = TextureSize * TextureSize * 4.</summary>
        [WriteOnly]
        [NativeDisableParallelForRestriction]
        public NativeArray<byte> OutputPixels;

        public void Execute(int index)
        {
            int x = index % TextureSize;
            int z = index / TextureSize;

            float worldX = x * Zoom + OffsetX;
            float worldZ = z * Zoom + OffsetZ;

            int gx = (int)math.floor(worldX);
            int gz = (int)math.floor(worldZ);

            byte r, g, b;

            switch (Mode)
            {
                case WorldBlendingMode.Heightmap:
                    EvalHeightmap(gx, gz, out r, out g, out b);
                    break;
                case WorldBlendingMode.BiomeVoronoi:
                    EvalVoronoi(gx, gz, out r, out g, out b);
                    break;
                case WorldBlendingMode.BlendWeightHeatmap:
                    EvalHeatmap(gx, gz, out r, out g, out b);
                    break;
                case WorldBlendingMode.BiomeBorderFade:
                    EvalBorderFade(gx, gz, out r, out g, out b);
                    break;
                default:
                    r = 0;
                    g = 0;
                    b = 0;
                    break;
            }

            int byteIdx = index * 4;
            OutputPixels[byteIdx] = r;
            OutputPixels[byteIdx + 1] = g;
            OutputPixels[byteIdx + 2] = b;
            OutputPixels[byteIdx + 3] = 255;
        }

        #region Evaluators

        private void EvalHeightmap(int gx, int gz, out byte r, out byte g, out byte b)
        {
            float heightF = BiomeBlender.CalculateBlendedTerrainHeight(
                gx, gz, ref SelectionNoise, ref Biomes, ref MultiNoise, out _);
            int height = (int)math.floor(heightF);

            if (ShowWaterLevel && height < SeaLevel)
            {
                float depth = math.saturate((SeaLevel - height) / 30f);
                r = (byte)math.lerp(64f, 20f, depth);
                g = (byte)math.lerp(140f, 51f, depth);
                b = (byte)math.lerp(217f, 140f, depth);
                return;
            }

            float v = math.saturate(height / 128f);
            v = math.max(v, 0.05f);
            byte bv = (byte)(v * 255f);
            r = bv;
            g = bv;
            b = bv;
        }

        private void EvalVoronoi(int gx, int gz, out byte r, out byte g, out byte b)
        {
            float cellValue = SelectionNoise.GetNoise(gx, gz);
            int biomeIndex = math.clamp((int)math.floor(cellValue * BiomeCount), 0, BiomeCount - 1);

            GetBiomeColorRGB(biomeIndex, out float br, out float bg, out float bb);

            float heightF = BiomeBlender.CalculateBlendedTerrainHeight(
                gx, gz, ref SelectionNoise, ref Biomes, ref MultiNoise, out _);
            float brightness = math.clamp(heightF / 100f, 0.3f, 1.0f);

            r = (byte)(br * brightness * 255f);
            g = (byte)(bg * brightness * 255f);
            b = (byte)(bb * brightness * 255f);
        }

        private unsafe void EvalHeatmap(int gx, int gz, out byte r, out byte g, out byte b)
        {
            SelectionNoise.GetCellularEdgeData(gx, gz, out FastNoiseLite.CellularEdgeData edgeData);

            const int N = FastNoiseLite.CellularEdgeData.MaxCells;
            int* bi = stackalloc int[N];
            float* rad = stackalloc float[N];
            float* bw = stackalloc float[N];
            BlendCurve* curves = stackalloc BlendCurve[N];
            for (int i = 0; i < N; i++)
            {
                float nv = edgeData.Hashes[i] * (1.0f / 2147483648.0f);
                nv = (nv + 1.0f) * 0.5f;
                bi[i] = math.clamp((int)math.floor(nv * BiomeCount), 0, BiomeCount - 1);
                rad[i] = Biomes[bi[i]].BlendRadius;
                bw[i] = Biomes[bi[i]].BlendWeight;
                curves[i] = Biomes[bi[i]].BlendCurve;
            }

            float trSum = 0f, localBlendRadiusSum = 0f;
            float dist0 = edgeData.Distances[0];
            for (int i = 0; i < N; i++)
            {
                float tr = math.max(0f, 1f - (edgeData.Distances[i] - dist0));
                trSum += tr;
                localBlendRadiusSum += tr * rad[i];
            }

            float localBlendRadius = localBlendRadiusSum / trSum;
            float wiggle = noise.snoise(new float2(gx * 0.001f + 7919f, gz * 0.001f + 6271f)) * 0.5f * localBlendRadius;
            float activeRadius = math.max(0.001f, localBlendRadius + wiggle);

            float* raw = stackalloc float[N];
            float totalRaw = 0f;
            for (int i = 0; i < N; i++)
            {
                raw[i] = math.max(0f, 1f - (edgeData.Distances[i] - dist0) / activeRadius) * bw[i];
                totalRaw += raw[i];
            }

            float targetWeight = 0f, totalSmooth = 0f;
            for (int i = 0; i < N; i++)
            {
                float norm = raw[i] / totalRaw;
                float curved = ApplyCurve(norm, curves[i]);
                totalSmooth += curved;
                if (bi[i] == TargetBiomeIndex)
                    targetWeight += curved;
            }

            float finalWeight = totalSmooth > 0f ? targetWeight / totalSmooth : 0f;
            HeatmapToRGB(finalWeight, out r, out g, out b);
        }

        private void EvalBorderFade(int gx, int gz, out byte r, out byte g, out byte b)
        {
            BiomeBlender.CalculateBlendedTerrainHeight(
                gx, gz, ref SelectionNoise, ref Biomes, ref MultiNoise, out float borderFade);

            float cellValue = SelectionNoise.GetNoise(gx, gz);
            int biomeIndex = math.clamp((int)math.floor(cellValue * BiomeCount), 0, BiomeCount - 1);

            GetBiomeColorRGB(biomeIndex, out float br, out float bg, out float bb);

            float intensity = math.lerp(0.15f, 1.0f, borderFade);
            r = (byte)(br * intensity * 255f);
            g = (byte)(bg * intensity * 255f);
            b = (byte)(bb * intensity * 255f);
        }

        #endregion

        #region Helpers

        private static float ApplyCurve(float t, BlendCurve curve)
        {
            switch (curve)
            {
                case BlendCurve.Linear: return t;
                case BlendCurve.SmootherStep: return t * t * t * (t * (t * 6f - 15f) + 10f);
                default: return t * t * (3f - 2f * t);
            }
        }

        private static void HeatmapToRGB(float t, out byte r, out byte g, out byte b)
        {
            t = math.saturate(t);
            float fr, fg, fb;

            if (t < 0.25f)
            {
                float s = t / 0.25f;
                fr = 0;
                fg = 0;
                fb = s;
            }
            else if (t < 0.5f)
            {
                float s = (t - 0.25f) / 0.25f;
                fr = 0;
                fg = s;
                fb = 1f - s;
            }
            else if (t < 0.75f)
            {
                float s = (t - 0.5f) / 0.25f;
                fr = s;
                fg = 1f;
                fb = 0;
            }
            else
            {
                float s = (t - 0.75f) / 0.25f;
                fr = 1f;
                fg = 1f - s;
                fb = 0;
            }

            r = (byte)(fr * 255f);
            g = (byte)(fg * 255f);
            b = (byte)(fb * 255f);
        }

        private void GetBiomeColorRGB(int biomeIndex, out float r, out float g, out float b)
        {
            float3 c = Biomes[biomeIndex].DebugPreviewColor;
            r = c.x;
            g = c.y;
            b = c.z;
        }

        #endregion
    }
}
