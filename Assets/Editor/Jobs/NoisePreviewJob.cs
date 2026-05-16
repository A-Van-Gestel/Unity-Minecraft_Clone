using System.Runtime.InteropServices;
using Jobs.Data;
using Libraries;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Editor.Jobs
{
    /// <summary>
    /// Evaluation mode for the noise preview job.
    /// Mirrors <c>WorldGenPreviewWindow.NoiseChannelMode</c> but as a Burst-safe integer enum.
    /// </summary>
    public enum NoisePreviewMode
    {
        RawNoise = 0,
        SplineNoise = 1,
        CombinedHeight = 2,
        DensitySlice = 3,
    }

    /// <summary>
    /// Burst-compiled parallel job for 2D noise preview evaluation.
    /// Each work item evaluates one pixel in the output texture.
    /// Used by the Noise Channels tab and World Blending tab for high-performance preview generation.
    /// </summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public struct NoisePreviewJob : IJobParallelFor
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
        public NoisePreviewMode Mode;

        [ReadOnly]
        public float BaseTerrainHeight;

        [ReadOnly]
        public float DensityAmplitude;

        [ReadOnly]
        public int ChunkHeight;

        [ReadOnly]
        public int SliceY;

        [MarshalAs(UnmanagedType.U1)]
        [ReadOnly]
        public bool UseSpline;

        [MarshalAs(UnmanagedType.U1)]
        [ReadOnly]
        public bool Enable3DDensity;

        [MarshalAs(UnmanagedType.U1)]
        [ReadOnly]
        public bool EnableDensityWarp;

        #endregion

        #region Noise Instances

        /// <summary>Noise instance for single-channel raw/spline modes.</summary>
        [ReadOnly]
        public FastNoiseLite ChannelNoise;

        /// <summary>Spline for single-channel spline modes.</summary>
        [ReadOnly]
        public BurstSpline ChannelSpline;

        /// <summary>Continentalness noise (Combined Height / Density Slice).</summary>
        [ReadOnly]
        public FastNoiseLite ContNoise;

        /// <summary>Erosion noise (Combined Height / Density Slice).</summary>
        [ReadOnly]
        public FastNoiseLite ErosionNoise;

        /// <summary>Peaks &amp; Valleys noise (Combined Height / Density Slice).</summary>
        [ReadOnly]
        public FastNoiseLite PvNoise;

        [ReadOnly]
        public BurstSpline ContSpline;

        [ReadOnly]
        public BurstSpline ErosionSpline;

        [ReadOnly]
        public BurstSpline PvSpline;

        /// <summary>3D density noise (Density Slice mode only).</summary>
        [ReadOnly]
        public FastNoiseLite DensityNoise;

        /// <summary>Domain warp noise for density (Density Slice mode only).</summary>
        [ReadOnly]
        public FastNoiseLite DensityWarpNoise;

        #endregion

        /// <summary>RGBA32 pixel output. Length = TextureSize * TextureSize.</summary>
        [WriteOnly]
        [NativeDisableParallelForRestriction]
        public NativeArray<byte> OutputPixels;

        public void Execute(int index)
        {
            int x = index % TextureSize;
            int z = index / TextureSize;

            float worldX = x * Zoom + OffsetX;
            float worldZ = z * Zoom + OffsetZ;

            byte r, g, b;

            switch (Mode)
            {
                case NoisePreviewMode.CombinedHeight:
                {
                    float c = ContSpline.Evaluate(ContNoise.GetNoise(worldX, worldZ));
                    float e = ErosionSpline.Evaluate(ErosionNoise.GetNoise(worldX, worldZ));
                    float p = PvSpline.Evaluate(PvNoise.GetNoise(worldX, worldZ));
                    float height = BaseTerrainHeight + c + (p * e);
                    float v = math.saturate(height / ChunkHeight);
                    byte bv = (byte)(v * 255f);
                    r = bv;
                    g = bv;
                    b = bv;
                    break;
                }

                case NoisePreviewMode.DensitySlice:
                {
                    float c = ContSpline.Evaluate(ContNoise.GetNoise(worldX, worldZ));
                    float e = ErosionSpline.Evaluate(ErosionNoise.GetNoise(worldX, worldZ));
                    float p = PvSpline.Evaluate(PvNoise.GetNoise(worldX, worldZ));
                    float baseHeight = BaseTerrainHeight + c + (p * e);
                    float density = baseHeight - SliceY;

                    if (Enable3DDensity)
                    {
                        float dx = worldX, dy = SliceY, dz = worldZ;
                        if (EnableDensityWarp)
                            DensityWarpNoise.DomainWarp(ref dx, ref dy, ref dz);
                        density += DensityNoise.GetNoise(dx, dy, dz) * DensityAmplitude;
                    }

                    // Positive (solid) = warm, negative (air) = cool, zero = white
                    if (density > 0f)
                    {
                        float t = math.saturate(density / 30f);
                        r = (byte)math.lerp(255f, 204f, t);
                        g = (byte)math.lerp(255f, 77f, t);
                        b = (byte)math.lerp(255f, 26f, t);
                    }
                    else
                    {
                        float t = math.saturate(-density / 30f);
                        r = (byte)math.lerp(255f, 38f, t);
                        g = (byte)math.lerp(255f, 102f, t);
                        b = (byte)math.lerp(255f, 217f, t);
                    }

                    break;
                }

                case NoisePreviewMode.SplineNoise:
                {
                    float raw = ChannelNoise.GetNoise(worldX, worldZ);
                    float splineOut = ChannelSpline.Evaluate(raw);
                    float v = math.saturate((splineOut + 50f) / 100f);
                    byte bv = (byte)(v * 255f);
                    r = bv;
                    g = bv;
                    b = bv;
                    break;
                }

                default: // RawNoise
                {
                    float v = (ChannelNoise.GetNoise(worldX, worldZ) + 1f) * 0.5f;
                    v = math.saturate(v);
                    byte bv = (byte)(v * 255f);
                    r = bv;
                    g = bv;
                    b = bv;
                    break;
                }
            }

            // Write RGBA32 (4 bytes per pixel)
            int byteIdx = index * 4;
            OutputPixels[byteIdx] = r;
            OutputPixels[byteIdx + 1] = g;
            OutputPixels[byteIdx + 2] = b;
            OutputPixels[byteIdx + 3] = 255;
        }
    }
}
