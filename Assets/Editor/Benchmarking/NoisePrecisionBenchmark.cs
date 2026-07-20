using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Libraries;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Editor.Benchmarking
{
    /// <summary>
    /// Editor A/B microbenchmark for the FastNoiseLite coordinate-precision pipeline (the
    /// world-scaling v2 noise rider). Times identical Burst-compiled sample grids through the
    /// <see cref="FastNoiseLite.CoordinatePrecision.Classic32"/> and
    /// <see cref="FastNoiseLite.CoordinatePrecision.Precise64"/> paths and reports median ms and
    /// the relative cost of the double pipeline per noise configuration (2D and 3D).
    /// Editor-only; never compiled into a build. Mono-editor numbers are indicative — re-measure
    /// under IL2CPP for shipping decisions (perf-benchmark protocol).
    /// </summary>
    internal static class NoisePrecisionBenchmark
    {
        private const int SEED = 1337;
        private const float FREQUENCY = 0.01f;
        private const int GRID_2D = 1024; // 1024² = ~1.05M samples
        private const int GRID_3D = 101; // 101³ = ~1.03M samples
        private const int RUNS = 15; // median of
        private const int WARMUP = 2;

        private struct BenchConfig
        {
            public string Name;
            public FastNoiseLite.NoiseType NoiseType;
            public FastNoiseLite.FractalType FractalType;
            public int Octaves;
        }

        [BurstCompile]
        private struct Bench2DJob : IJob
        {
            public FastNoiseLite Noise;
            public int GridSize;
            public long BaseX;
            [WriteOnly] public NativeArray<float> Sink;

            public void Execute()
            {
                float acc = 0f;
                for (int y = 0; y < GridSize; y++)
                for (int x = 0; x < GridSize; x++)
                    acc += Noise.GetNoise(BaseX + x, y);
                Sink[0] = acc;
            }
        }

        [BurstCompile]
        private struct Bench3DJob : IJob
        {
            public FastNoiseLite Noise;
            public int GridSize;
            public long BaseX;
            [WriteOnly] public NativeArray<float> Sink;

            public void Execute()
            {
                float acc = 0f;
                for (int z = 0; z < GridSize; z++)
                for (int y = 0; y < GridSize; y++)
                for (int x = 0; x < GridSize; x++)
                    acc += Noise.GetNoise(BaseX + x, y, z);
                Sink[0] = acc;
            }
        }

        [MenuItem("Minecraft Clone/Benchmarks/Noise Precision A-B (Far Lands rider)")]
        private static void Run()
        {
            FastNoiseLite.InitializeLookupTables();

            BenchConfig[] configs =
            {
                new BenchConfig { Name = "Perlin", NoiseType = FastNoiseLite.NoiseType.Perlin, FractalType = FastNoiseLite.FractalType.None },
                new BenchConfig { Name = "OpenSimplex2", NoiseType = FastNoiseLite.NoiseType.OpenSimplex2, FractalType = FastNoiseLite.FractalType.None },
                new BenchConfig { Name = "OpenSimplex2S", NoiseType = FastNoiseLite.NoiseType.OpenSimplex2S, FractalType = FastNoiseLite.FractalType.None },
                new BenchConfig { Name = "Cellular", NoiseType = FastNoiseLite.NoiseType.Cellular, FractalType = FastNoiseLite.FractalType.None },
                new BenchConfig { Name = "Value", NoiseType = FastNoiseLite.NoiseType.Value, FractalType = FastNoiseLite.FractalType.None },
                new BenchConfig { Name = "Perlin+FBm4", NoiseType = FastNoiseLite.NoiseType.Perlin, FractalType = FastNoiseLite.FractalType.FBm, Octaves = 4 },
                new BenchConfig { Name = "OS2+FBm4", NoiseType = FastNoiseLite.NoiseType.OpenSimplex2, FractalType = FastNoiseLite.FractalType.FBm, Octaves = 4 },
            };

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== NOISE PRECISION A/B (Classic32 vs Precise64) ===");
            sb.AppendLine($"Grid: {GRID_2D}x{GRID_2D} (2D), {GRID_3D}^3 (3D), median of {RUNS} runs, {WARMUP} warmup");
            sb.AppendLine();
            sb.AppendLine("Config                 2D classic   2D precise     Δ%    3D classic   3D precise     Δ%");
            sb.AppendLine("--------------------------------------------------------------------------------------");

            foreach (BenchConfig cfg in configs)
            {
                double c2 = Measure2D(cfg, FastNoiseLite.CoordinatePrecision.Classic32);
                double p2 = Measure2D(cfg, FastNoiseLite.CoordinatePrecision.Precise64);
                double c3 = Measure3D(cfg, FastNoiseLite.CoordinatePrecision.Classic32);
                double p3 = Measure3D(cfg, FastNoiseLite.CoordinatePrecision.Precise64);

                sb.AppendLine(
                    $"{cfg.Name,-20} {c2,10:F2}ms {p2,10:F2}ms {(p2 / c2 - 1) * 100,5:F1}% {c3,10:F2}ms {p3,10:F2}ms {(p3 / c3 - 1) * 100,5:F1}%");
            }

            string outPath = Path.Combine(Application.temporaryCachePath, "noise_precision_bench.txt");
            File.WriteAllText(outPath, sb.ToString());
            Debug.Log(sb + "\nReport: " + outPath);
        }

        private static FastNoiseLite Configure(in BenchConfig cfg, FastNoiseLite.CoordinatePrecision precision)
        {
            FastNoiseLite fnl = FastNoiseLite.Create(SEED);
            fnl.SetFrequency(FREQUENCY);
            fnl.SetNoiseType(cfg.NoiseType);
            fnl.SetFractalType(cfg.FractalType);
            if (cfg.Octaves > 0) fnl.SetFractalOctaves(cfg.Octaves);
            fnl.SetCoordinatePrecision(precision);
            return fnl;
        }

        private static double Measure2D(in BenchConfig cfg, FastNoiseLite.CoordinatePrecision precision)
        {
            var sink = new NativeArray<float>(1, Allocator.TempJob);
            try
            {
                var job = new Bench2DJob { Noise = Configure(in cfg, precision), GridSize = GRID_2D, BaseX = 0, Sink = sink };
                return MedianMs(() => job.Run());
            }
            finally
            {
                sink.Dispose();
            }
        }

        private static double Measure3D(in BenchConfig cfg, FastNoiseLite.CoordinatePrecision precision)
        {
            var sink = new NativeArray<float>(1, Allocator.TempJob);
            try
            {
                var job = new Bench3DJob { Noise = Configure(in cfg, precision), GridSize = GRID_3D, BaseX = 0, Sink = sink };
                return MedianMs(() => job.Run());
            }
            finally
            {
                sink.Dispose();
            }
        }

        private static double MedianMs(Action run)
        {
            for (int i = 0; i < WARMUP; i++) run();

            var samples = new double[RUNS];
            var sw = new Stopwatch();
            for (int i = 0; i < RUNS; i++)
            {
                sw.Restart();
                run();
                sw.Stop();
                samples[i] = sw.Elapsed.TotalMilliseconds;
            }

            Array.Sort(samples);
            return samples[RUNS / 2];
        }
    }
}
