using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Benchmarks;
using Libraries;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Editor.Validation
{
    /// <summary>
    /// Validation test harness and performance benchmark for <see cref="FastNoiseLite"/>.
    /// Covers correctness (golden-value regression, determinism, output range), domain warp,
    /// and per-noise-type throughput benchmarks via Burst-compiled jobs.
    /// </summary>
    /// <remarks>
    /// <para>Run via <c>Minecraft Clone &gt; Dev &gt; Validate FastNoiseLite</c>.</para>
    /// <para>On first run (no golden file), captures current output as the reference baseline.
    /// Subsequent runs validate against that baseline to catch regressions.
    /// To regenerate the baseline after an intentional change, delete the golden file and re-run.</para>
    /// </remarks>
    internal static class FastNoiseLiteTests
    {
        #region Constants

        private const string MENU_PATH = "Minecraft Clone/Dev/Validate FastNoiseLite";
        private const float TOLERANCE = 1e-5f;
        private const int SEED = 1337;
        private const float FREQUENCY = 0.02f;
        private const int FRACTAL_OCTAVES = 4;
        private const float WARP_AMP = 30f;

        // ReSharper disable once InconsistentNaming
        private const int BENCH_GRID_2D = 2048;

        // ReSharper disable once InconsistentNaming
        private const int BENCH_GRID_3D = 160;
        private const int BENCH_RUNS = 30;
        private const int BENCH_WARMUP = 2;
        private const float BENCH_SPACING = 0.1f;

        #endregion

        #region Test Coordinates

        private static readonly float2[] s_coords2D =
        {
            new float2(0f, 0f), new float2(1.5f, 2.3f), new float2(-7.8f, 3.14f), new float2(100.5f, -50.25f), new float2(-1000f, 1000f), new float2(0.001f, 0.002f), new float2(42.42f, 42.42f), new float2(16f, 16f),
        };

        private static readonly float3[] s_coords3D =
        {
            new float3(0f, 0f, 0f), new float3(1.5f, 2.3f, -0.7f), new float3(-7.8f, 3.14f, 6.28f), new float3(100.5f, -50.25f, 75f), new float3(-1000f, 1000f, -500f), new float3(0.001f, 0.002f, 0.003f), new float3(42.42f, 42.42f, 42.42f), new float3(16f, 64f, 16f),
        };

        #endregion

        #region Config Structs

        private struct NoiseTestConfig
        {
            public string Name;
            public FastNoiseLite.NoiseType NoiseType;
            public FastNoiseLite.FractalType FractalType;
            public int Octaves;
            public FastNoiseLite.CellularDistanceFunction CellDistFunc;
            public FastNoiseLite.CellularReturnType CellReturnType;
            public FastNoiseLite.RotationType3D RotationType;
            public float Frequency;
            public bool Normalize;
        }

        private struct WarpTestConfig
        {
            public string Name;
            public FastNoiseLite.DomainWarpType WarpType;
            public FastNoiseLite.FractalType FractalType;
            public int Octaves;
            public float Amp;
        }

        #endregion

        #region Config Builders

        private static NoiseTestConfig[] BuildNoiseConfigs()
        {
            return new[]
            {
                NC("OpenSimplex2", FastNoiseLite.NoiseType.OpenSimplex2),
                NC("OpenSimplex2S", FastNoiseLite.NoiseType.OpenSimplex2S),
                NC("Cellular", FastNoiseLite.NoiseType.Cellular),
                NC("Perlin", FastNoiseLite.NoiseType.Perlin),
                NC("ValueCubic", FastNoiseLite.NoiseType.ValueCubic),
                NC("Value", FastNoiseLite.NoiseType.Value),

                NCF("Perlin_FBm", FastNoiseLite.NoiseType.Perlin, FastNoiseLite.FractalType.FBm),
                NCF("Perlin_Ridged", FastNoiseLite.NoiseType.Perlin, FastNoiseLite.FractalType.Ridged),
                NCF("Perlin_PingPong", FastNoiseLite.NoiseType.Perlin, FastNoiseLite.FractalType.PingPong),
                NCF("OS2_FBm", FastNoiseLite.NoiseType.OpenSimplex2, FastNoiseLite.FractalType.FBm),
                NCF("Value_FBm", FastNoiseLite.NoiseType.Value, FastNoiseLite.FractalType.FBm),

                NCC("Cell_Euclidean", FastNoiseLite.CellularDistanceFunction.Euclidean, FastNoiseLite.CellularReturnType.Distance),
                NCC("Cell_Manhattan", FastNoiseLite.CellularDistanceFunction.Manhattan, FastNoiseLite.CellularReturnType.Distance),
                NCC("Cell_Hybrid", FastNoiseLite.CellularDistanceFunction.Hybrid, FastNoiseLite.CellularReturnType.Distance),
                NCC("Cell_CellValue", FastNoiseLite.CellularDistanceFunction.EuclideanSq, FastNoiseLite.CellularReturnType.CellValue),
                NCC("Cell_Dist2", FastNoiseLite.CellularDistanceFunction.EuclideanSq, FastNoiseLite.CellularReturnType.Distance2),
                NCC("Cell_Dist2Add", FastNoiseLite.CellularDistanceFunction.EuclideanSq, FastNoiseLite.CellularReturnType.Distance2Add),
                NCC("Cell_Dist2Sub", FastNoiseLite.CellularDistanceFunction.EuclideanSq, FastNoiseLite.CellularReturnType.Distance2Sub),
                NCC("Cell_Dist2Mul", FastNoiseLite.CellularDistanceFunction.EuclideanSq, FastNoiseLite.CellularReturnType.Distance2Mul),
                NCC("Cell_Dist2Div", FastNoiseLite.CellularDistanceFunction.EuclideanSq, FastNoiseLite.CellularReturnType.Distance2Div),

                new NoiseTestConfig
                {
                    Name = "Perlin_XZPlanes", NoiseType = FastNoiseLite.NoiseType.Perlin,
                    RotationType = FastNoiseLite.RotationType3D.ImproveXZPlanes, Frequency = FREQUENCY,
                },
                new NoiseTestConfig
                {
                    Name = "Perlin_Norm01", NoiseType = FastNoiseLite.NoiseType.Perlin,
                    Frequency = FREQUENCY, Normalize = true,
                },
            };
        }

        private static WarpTestConfig[] BuildWarpConfigs()
        {
            return new[]
            {
                new WarpTestConfig { Name = "Warp_OS2", WarpType = FastNoiseLite.DomainWarpType.OpenSimplex2, Amp = WARP_AMP },
                new WarpTestConfig { Name = "Warp_OS2Red", WarpType = FastNoiseLite.DomainWarpType.OpenSimplex2Reduced, Amp = WARP_AMP },
                new WarpTestConfig { Name = "Warp_Grid", WarpType = FastNoiseLite.DomainWarpType.BasicGrid, Amp = WARP_AMP },
                new WarpTestConfig
                {
                    Name = "Warp_OS2_Prog", WarpType = FastNoiseLite.DomainWarpType.OpenSimplex2,
                    FractalType = FastNoiseLite.FractalType.DomainWarpProgressive, Octaves = 3, Amp = WARP_AMP,
                },
                new WarpTestConfig
                {
                    Name = "Warp_OS2_Indep", WarpType = FastNoiseLite.DomainWarpType.OpenSimplex2,
                    FractalType = FastNoiseLite.FractalType.DomainWarpIndependent, Octaves = 3, Amp = WARP_AMP,
                },
            };
        }

        private static NoiseTestConfig NC(string name, FastNoiseLite.NoiseType type) =>
            new NoiseTestConfig
            {
                Name = name, NoiseType = type, Frequency = FREQUENCY,
                CellDistFunc = FastNoiseLite.CellularDistanceFunction.EuclideanSq,
                CellReturnType = FastNoiseLite.CellularReturnType.Distance,
            };

        private static NoiseTestConfig NCF(string name, FastNoiseLite.NoiseType type, FastNoiseLite.FractalType frac) =>
            new NoiseTestConfig
            {
                Name = name, NoiseType = type, FractalType = frac, Octaves = FRACTAL_OCTAVES,
                Frequency = FREQUENCY, CellDistFunc = FastNoiseLite.CellularDistanceFunction.EuclideanSq,
                CellReturnType = FastNoiseLite.CellularReturnType.Distance,
            };

        private static NoiseTestConfig NCC(string name, FastNoiseLite.CellularDistanceFunction dist,
            FastNoiseLite.CellularReturnType ret) =>
            new NoiseTestConfig
            {
                Name = name, NoiseType = FastNoiseLite.NoiseType.Cellular, Frequency = FREQUENCY,
                CellDistFunc = dist, CellReturnType = ret,
            };

        #endregion

        #region Noise Configuration

        private static FastNoiseLite ConfigureNoise(in NoiseTestConfig cfg)
        {
            FastNoiseLite fnl = FastNoiseLite.Create(SEED);
            fnl.SetFrequency(cfg.Frequency);
            fnl.SetNoiseType(cfg.NoiseType);
            fnl.SetFractalType(cfg.FractalType);
            if (cfg.Octaves > 0) fnl.SetFractalOctaves(cfg.Octaves);
            fnl.SetCellularDistanceFunction(cfg.CellDistFunc);
            fnl.SetCellularReturnType(cfg.CellReturnType);
            fnl.SetRotationType3D(cfg.RotationType);
            fnl.SetNormalizeToZeroOne(cfg.Normalize);
            return fnl;
        }

        private static FastNoiseLite ConfigureWarp(in WarpTestConfig cfg)
        {
            FastNoiseLite fnl = FastNoiseLite.Create(SEED);
            fnl.SetFrequency(FREQUENCY);
            fnl.SetDomainWarpType(cfg.WarpType);
            fnl.SetDomainWarpAmp(cfg.Amp);
            fnl.SetFractalType(cfg.FractalType);
            if (cfg.Octaves > 0) fnl.SetFractalOctaves(cfg.Octaves);
            return fnl;
        }

        #endregion

        #region Golden File Path

        private static string GoldenFilePath =>
            Path.Combine(Application.dataPath, "Editor", "Validation", "FastNoiseLiteGoldenValues.txt");

        #endregion

        #region Menu Entry

        [MenuItem(MENU_PATH)]
        public static void Run()
        {
            FastNoiseLite.InitializeLookupTables();

            Runner runner = new Runner();
            runner.RunPropertyTests();
            runner.RunGoldenValueTests();
            runner.RunBenchmarks();
            runner.PrintSummary();
        }

        #endregion

        #region Runner

        /// <summary>
        /// Encapsulates per-run state so no mutable static fields survive across menu invocations.
        /// </summary>
        private sealed class Runner
        {
            private int _passed;
            private int _failed;
            private readonly StringBuilder _benchReport = new StringBuilder();

            // ===== Property Tests =====

            public void RunPropertyTests()
            {
                NoiseTestConfig[] configs = BuildNoiseConfigs();

                Test_Determinism(configs);
                Test_OutputRange(configs);
                Test_SeedSensitivity();
                Test_NormalizeRange();
                Test_BatchGridBitIdentical(configs);
                Test_FactoryMethods();
                Test_CoordinatePrecision();
            }

            /// <summary>
            /// Evaluating the same config twice with the same inputs must produce identical results.
            /// </summary>
            private void Test_Determinism(NoiseTestConfig[] configs)
            {
                foreach (NoiseTestConfig cfg in configs)
                {
                    FastNoiseLite a = ConfigureNoise(in cfg);
                    FastNoiseLite b = ConfigureNoise(in cfg);

                    foreach (float2 c in s_coords2D)
                    {
                        float va = a.GetNoise(c.x, c.y);
                        float vb = b.GetNoise(c.x, c.y);
                        AssertEqual(va, vb, $"Determinism 2D {cfg.Name} ({c.x},{c.y})");
                    }

                    foreach (float3 c in s_coords3D)
                    {
                        float va = a.GetNoise(c.x, c.y, c.z);
                        float vb = b.GetNoise(c.x, c.y, c.z);
                        AssertEqual(va, vb, $"Determinism 3D {cfg.Name} ({c.x},{c.y},{c.z})");
                    }
                }
            }

            /// <summary>
            /// Non-normalized noise must output in [-1, 1]. Normalized in [0, 1].
            /// </summary>
            private void Test_OutputRange(NoiseTestConfig[] configs)
            {
                foreach (NoiseTestConfig cfg in configs)
                {
                    FastNoiseLite fnl = ConfigureNoise(in cfg);
                    float lo = cfg.Normalize ? 0f : -1f;
                    const float hi = 1f;

                    foreach (float2 c in s_coords2D)
                    {
                        float v = fnl.GetNoise(c.x, c.y);
                        AssertTrue(v >= lo - TOLERANCE && v <= hi + TOLERANCE,
                            $"Range 2D {cfg.Name} ({c.x},{c.y}): {v} not in [{lo},{hi}]");
                    }

                    foreach (float3 c in s_coords3D)
                    {
                        float v = fnl.GetNoise(c.x, c.y, c.z);
                        AssertTrue(v >= lo - TOLERANCE && v <= hi + TOLERANCE,
                            $"Range 3D {cfg.Name} ({c.x},{c.y},{c.z}): {v} not in [{lo},{hi}]");
                    }
                }
            }

            /// <summary>
            /// Changing the seed must produce different output for at least one test coordinate.
            /// </summary>
            private void Test_SeedSensitivity()
            {
                FastNoiseLite fnlA = FastNoiseLite.Create(SEED);
                fnlA.SetFrequency(FREQUENCY);
                fnlA.SetNoiseType(FastNoiseLite.NoiseType.Perlin);

                FastNoiseLite fnlB = FastNoiseLite.Create(SEED + 12345);
                fnlB.SetFrequency(FREQUENCY);
                fnlB.SetNoiseType(FastNoiseLite.NoiseType.Perlin);

                bool anyDifferent = false;
                foreach (float2 c in s_coords2D)
                {
                    if (math.abs(fnlA.GetNoise(c.x, c.y) - fnlB.GetNoise(c.x, c.y)) > TOLERANCE)
                    {
                        anyDifferent = true;
                        break;
                    }
                }

                AssertTrue(anyDifferent, "Seed sensitivity: different seeds must produce different output");
            }

            /// <summary>
            /// Normalized output must equal (raw + 1) * 0.5 for any noise type.
            /// </summary>
            private void Test_NormalizeRange()
            {
                FastNoiseLite raw = FastNoiseLite.Create(SEED);
                raw.SetFrequency(FREQUENCY);
                raw.SetNoiseType(FastNoiseLite.NoiseType.Perlin);
                raw.SetNormalizeToZeroOne(false);

                FastNoiseLite norm = FastNoiseLite.Create(SEED);
                norm.SetFrequency(FREQUENCY);
                norm.SetNoiseType(FastNoiseLite.NoiseType.Perlin);
                norm.SetNormalizeToZeroOne(true);

                foreach (float2 c in s_coords2D)
                {
                    float expected = (raw.GetNoise(c.x, c.y) + 1f) * 0.5f;
                    float actual = norm.GetNoise(c.x, c.y);
                    AssertNear(expected, actual, TOLERANCE,
                        $"Normalize 2D ({c.x},{c.y}): expected {expected}, got {actual}");
                }
            }

            /// <summary>
            /// GetNoiseGrid must produce bit-identical output to calling GetNoise in a scalar loop.
            /// Tests all noise configs at integer coordinates matching the batch API.
            /// </summary>
            private void Test_BatchGridBitIdentical(NoiseTestConfig[] configs)
            {
                const int GRID_SIZE = 8;
                const int START_X = -100;
                const int START_Y = 50;
                const int START_Z = -25;

                NativeArray<float> gridOutput2D = new NativeArray<float>(GRID_SIZE * GRID_SIZE, Allocator.TempJob);
                NativeArray<float> gridOutput3D = new NativeArray<float>(GRID_SIZE * GRID_SIZE * GRID_SIZE, Allocator.TempJob);

                try
                {
                    foreach (NoiseTestConfig cfg in configs)
                    {
                        FastNoiseLite fnl = ConfigureNoise(in cfg);

                        // 2D: compare batch vs scalar
                        fnl.GetNoiseGrid(START_X, START_Y, GRID_SIZE, GRID_SIZE, gridOutput2D);
                        for (int iy = 0; iy < GRID_SIZE; iy++)
                        for (int ix = 0; ix < GRID_SIZE; ix++)
                        {
                            float scalar = fnl.GetNoise(START_X + ix, START_Y + iy);
                            float batch = gridOutput2D[iy * GRID_SIZE + ix];
                            AssertEqual(scalar, batch,
                                $"BatchGrid2D {cfg.Name} ({START_X + ix},{START_Y + iy})");
                        }

                        // 3D: compare batch vs scalar
                        fnl.GetNoiseGrid(START_X, START_Y, START_Z, GRID_SIZE, GRID_SIZE, GRID_SIZE, gridOutput3D);
                        for (int iz = 0; iz < GRID_SIZE; iz++)
                        for (int iy = 0; iy < GRID_SIZE; iy++)
                        for (int ix = 0; ix < GRID_SIZE; ix++)
                        {
                            float scalar = fnl.GetNoise(
                                START_X + ix, START_Y + iy, START_Z + iz);
                            float batch = gridOutput3D[iz * GRID_SIZE * GRID_SIZE + iy * GRID_SIZE + ix];
                            AssertEqual(scalar, batch,
                                $"BatchGrid3D {cfg.Name} ({START_X + ix},{START_Y + iy},{START_Z + iz})");
                        }
                    }
                }
                finally
                {
                    gridOutput2D.Dispose();
                    gridOutput3D.Dispose();
                }
            }

            /// <summary>
            /// Factory methods must produce bit-identical output to equivalent manual configuration.
            /// </summary>
            private void Test_FactoryMethods()
            {
                // CreateSimple must match Create() + SetNoiseType(OpenSimplex2) + SetFrequency()
                FastNoiseLite simple = FastNoiseLite.CreateSimple(SEED, FREQUENCY);

                FastNoiseLite manualSimple = FastNoiseLite.Create(SEED);
                manualSimple.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
                manualSimple.SetFrequency(FREQUENCY);

                foreach (float2 c in s_coords2D)
                    AssertEqual(simple.GetNoise(c.x, c.y), manualSimple.GetNoise(c.x, c.y),
                        $"CreateSimple 2D ({c.x},{c.y})");

                foreach (float3 c in s_coords3D)
                    AssertEqual(simple.GetNoise(c.x, c.y, c.z), manualSimple.GetNoise(c.x, c.y, c.z),
                        $"CreateSimple 3D ({c.x},{c.y},{c.z})");

                // CreateFBm must match Create() + SetNoiseType + SetFrequency + SetFractalType(FBm)
                // + SetFractalOctaves + SetFractalGain(0.5) + SetFractalLacunarity(2.0)
                FastNoiseLite fbm = FastNoiseLite.CreateFBm(SEED, FREQUENCY, FRACTAL_OCTAVES);

                FastNoiseLite manualFbm = FastNoiseLite.Create(SEED);
                manualFbm.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
                manualFbm.SetFrequency(FREQUENCY);
                manualFbm.SetFractalType(FastNoiseLite.FractalType.FBm);
                manualFbm.SetFractalOctaves(FRACTAL_OCTAVES);
                manualFbm.SetFractalGain(0.5f);
                manualFbm.SetFractalLacunarity(2.0f);

                foreach (float2 c in s_coords2D)
                    AssertEqual(fbm.GetNoise(c.x, c.y), manualFbm.GetNoise(c.x, c.y),
                        $"CreateFBm 2D ({c.x},{c.y})");

                foreach (float3 c in s_coords3D)
                    AssertEqual(fbm.GetNoise(c.x, c.y, c.z), manualFbm.GetNoise(c.x, c.y, c.z),
                        $"CreateFBm 3D ({c.x},{c.y},{c.z})");

                // CreateFBm default octaves (3) must work
                FastNoiseLite fbmDefault = FastNoiseLite.CreateFBm(SEED, FREQUENCY);

                FastNoiseLite manualFbmDefault = FastNoiseLite.Create(SEED);
                manualFbmDefault.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
                manualFbmDefault.SetFrequency(FREQUENCY);
                manualFbmDefault.SetFractalType(FastNoiseLite.FractalType.FBm);
                manualFbmDefault.SetFractalOctaves(3);
                manualFbmDefault.SetFractalGain(0.5f);
                manualFbmDefault.SetFractalLacunarity(2.0f);

                foreach (float3 c in s_coords3D)
                    AssertEqual(fbmDefault.GetNoise(c.x, c.y, c.z), manualFbmDefault.GetNoise(c.x, c.y, c.z),
                        $"CreateFBm default octaves 3D ({c.x},{c.y},{c.z})");

                // Factory methods must be seed-sensitive
                FastNoiseLite simpleA = FastNoiseLite.CreateSimple(SEED, FREQUENCY);
                FastNoiseLite simpleB = FastNoiseLite.CreateSimple(SEED + 12345, FREQUENCY);

                bool anyDifferent = false;
                foreach (float3 c in s_coords3D)
                {
                    if (math.abs(simpleA.GetNoise(c.x, c.y, c.z) - simpleB.GetNoise(c.x, c.y, c.z)) > TOLERANCE)
                    {
                        anyDifferent = true;
                        break;
                    }
                }

                AssertTrue(anyDifferent, "CreateSimple seed sensitivity: different seeds must produce different output");

                // Output range for factory-created instances
                foreach (float3 c in s_coords3D)
                {
                    float v = simple.GetNoise(c.x, c.y, c.z);
                    AssertTrue(v >= -1f - TOLERANCE && v <= 1f + TOLERANCE,
                        $"CreateSimple range 3D ({c.x},{c.y},{c.z}): {v} not in [-1,1]");

                    float vf = fbm.GetNoise(c.x, c.y, c.z);
                    AssertTrue(vf >= -1f - TOLERANCE && vf <= 1f + TOLERANCE,
                        $"CreateFBm range 3D ({c.x},{c.y},{c.z}): {vf} not in [-1,1]");
                }
            }

            /// <summary>
            /// Precise64 coordinate-pipeline guards:
            /// (1) in-band, Precise64 tracks Classic32 within a small drift tolerance;
            /// (2) at ±2³⁰, Precise64 output still varies sample-to-sample (no precision collapse);
            /// (3) at ±2³⁰, Classic32 collapses into constant runs — pins the preserved
            ///     "Far Lands" behavior, and doubles as the prove-red for (2): a Precise64
            ///     pipeline that secretly narrows to float fails (2) exactly the way (3) passes.
            /// </summary>
            private void Test_CoordinatePrecision()
            {
                // Continuous-output configs only — piecewise-constant outputs (Cellular CellValue)
                // legitimately repeat between adjacent samples and would false-fail the variance checks.
                var smoothTypes = new[]
                {
                    FastNoiseLite.NoiseType.Perlin,
                    FastNoiseLite.NoiseType.OpenSimplex2,
                    FastNoiseLite.NoiseType.OpenSimplex2S,
                    FastNoiseLite.NoiseType.Value,
                };

                const long FAR_BASE = 1L << 30;
                const int SAMPLES = 64;
                const float IN_BAND_DRIFT_TOLERANCE = 5e-3f;

                foreach (FastNoiseLite.NoiseType type in smoothTypes)
                foreach (bool fbm in new[] { false, true })
                {
                    string name = $"{type}{(fbm ? "+FBm" : "")}";

                    FastNoiseLite classic = FastNoiseLite.Create(SEED);
                    classic.SetFrequency(FREQUENCY);
                    classic.SetNoiseType(type);
                    if (fbm)
                    {
                        classic.SetFractalType(FastNoiseLite.FractalType.FBm);
                        classic.SetFractalOctaves(FRACTAL_OCTAVES);
                    }

                    FastNoiseLite precise = classic;
                    precise.SetCoordinatePrecision(FastNoiseLite.CoordinatePrecision.Precise64);

                    // (1) In-band parity: same terrain within ULP-drift tolerance.
                    for (int i = 0; i < SAMPLES; i++)
                    {
                        int x = -32768 + i * 1021;
                        int z = 17 + i * 769;
                        AssertNear(classic.GetNoise(x, z), precise.GetNoise(x, z), IN_BAND_DRIFT_TOLERANCE,
                            $"Precision in-band 2D {name} ({x},{z})");
                        AssertNear(classic.GetNoise(x, 61, z), precise.GetNoise(x, 61, z), IN_BAND_DRIFT_TOLERANCE,
                            $"Precision in-band 3D {name} ({x},61,{z})");
                    }

                    // (2) + (3) Far-band adjacent-sample variance, both signs.
                    foreach (long sign in new[] { 1L, -1L })
                    {
                        int preciseNonZero = 0;
                        int classicZero = 0;
                        float prevPrecise = precise.GetNoise(sign * FAR_BASE, 12345.0);
                        float prevClassic = classic.GetNoise(sign * FAR_BASE, 12345.0);
                        for (int i = 1; i < SAMPLES; i++)
                        {
                            double x = sign * FAR_BASE + i;
                            float vp = precise.GetNoise(x, 12345.0);
                            float vc = classic.GetNoise(x, 12345.0);
                            if (math.abs(vp - prevPrecise) > 1e-6f) preciseNonZero++;
                            if (vc == prevClassic) classicZero++;
                            prevPrecise = vp;
                            prevClassic = vc;
                        }

                        AssertTrue(preciseNonZero >= (SAMPLES - 1) / 4,
                            $"Precision far-band {name} @ {sign * FAR_BASE}: Precise64 collapsed " +
                            $"({preciseNonZero}/{SAMPLES - 1} adjacent samples vary)");
                        AssertTrue(classicZero >= (SAMPLES - 1) * 4 / 5,
                            $"Precision far-band {name} @ {sign * FAR_BASE}: Classic32 unexpectedly precise " +
                            $"({classicZero}/{SAMPLES - 1} adjacent samples identical) — Far Lands behavior lost");
                    }
                }
            }

            // ===== Golden Value Tests =====

            public void RunGoldenValueTests()
            {
                NoiseTestConfig[] noiseConfigs = BuildNoiseConfigs();
                WarpTestConfig[] warpConfigs = BuildWarpConfigs();
                string goldenPath = GoldenFilePath;

                if (!File.Exists(goldenPath))
                {
                    GenerateGoldenFile(noiseConfigs, warpConfigs, goldenPath);
                    Debug.Log($"[FastNoiseLiteTests] Golden file generated at: {goldenPath}\n" +
                              "Re-run to validate against baseline.");
                    return;
                }

                float[] golden = ReadGoldenFile(goldenPath);
                float[] current = EvaluateAll(noiseConfigs, warpConfigs);

                if (golden.Length != current.Length)
                {
                    Fail($"Golden value count mismatch: file has {golden.Length}, expected {current.Length}. " +
                         "Delete the golden file and regenerate.");
                    return;
                }

                int goldenIdx = 0;

                foreach (NoiseTestConfig cfg in noiseConfigs)
                {
                    foreach (float2 c in s_coords2D)
                    {
                        AssertNear(golden[goldenIdx], current[goldenIdx], TOLERANCE,
                            $"Golden 2D {cfg.Name} ({c.x},{c.y}): expected {golden[goldenIdx]}, got {current[goldenIdx]}");
                        goldenIdx++;
                    }

                    foreach (float3 c in s_coords3D)
                    {
                        AssertNear(golden[goldenIdx], current[goldenIdx], TOLERANCE,
                            $"Golden 3D {cfg.Name} ({c.x},{c.y},{c.z}): expected {golden[goldenIdx]}, got {current[goldenIdx]}");
                        goldenIdx++;
                    }
                }

                foreach (WarpTestConfig cfg in warpConfigs)
                {
                    foreach (float2 c in s_coords2D)
                    {
                        AssertNear(golden[goldenIdx], current[goldenIdx], TOLERANCE,
                            $"Golden Warp2D {cfg.Name} x ({c.x},{c.y})");
                        goldenIdx++;
                        AssertNear(golden[goldenIdx], current[goldenIdx], TOLERANCE,
                            $"Golden Warp2D {cfg.Name} y ({c.x},{c.y})");
                        goldenIdx++;
                    }

                    foreach (float3 c in s_coords3D)
                    {
                        AssertNear(golden[goldenIdx], current[goldenIdx], TOLERANCE,
                            $"Golden Warp3D {cfg.Name} x ({c.x},{c.y},{c.z})");
                        goldenIdx++;
                        AssertNear(golden[goldenIdx], current[goldenIdx], TOLERANCE,
                            $"Golden Warp3D {cfg.Name} y ({c.x},{c.y},{c.z})");
                        goldenIdx++;
                        AssertNear(golden[goldenIdx], current[goldenIdx], TOLERANCE,
                            $"Golden Warp3D {cfg.Name} z ({c.x},{c.y},{c.z})");
                        goldenIdx++;
                    }
                }
            }

            private static float[] EvaluateAll(NoiseTestConfig[] noiseConfigs, WarpTestConfig[] warpConfigs)
            {
                var values = new List<float>(1024);

                foreach (NoiseTestConfig cfg in noiseConfigs)
                {
                    FastNoiseLite fnl = ConfigureNoise(in cfg);
                    foreach (float2 c in s_coords2D) values.Add(fnl.GetNoise(c.x, c.y));
                    foreach (float3 c in s_coords3D) values.Add(fnl.GetNoise(c.x, c.y, c.z));
                }

                foreach (WarpTestConfig cfg in warpConfigs)
                {
                    FastNoiseLite fnl = ConfigureWarp(in cfg);

                    foreach (float2 c in s_coords2D)
                    {
                        double wx = c.x, wy = c.y;
                        fnl.DomainWarp(ref wx, ref wy);
                        values.Add((float)wx);
                        values.Add((float)wy);
                    }

                    foreach (float3 c in s_coords3D)
                    {
                        double wx = c.x, wy = c.y, wz = c.z;
                        fnl.DomainWarp(ref wx, ref wy, ref wz);
                        values.Add((float)wx);
                        values.Add((float)wy);
                        values.Add((float)wz);
                    }
                }

                return values.ToArray();
            }

            private static void GenerateGoldenFile(NoiseTestConfig[] noiseConfigs, WarpTestConfig[] warpConfigs,
                string path)
            {
                float[] values = EvaluateAll(noiseConfigs, warpConfigs);

                StringBuilder sb = new StringBuilder(values.Length * 20);
                sb.AppendLine($"# FastNoiseLite Golden Values — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"# Seed={SEED}  Frequency={FREQUENCY}  Configs={noiseConfigs.Length}+{warpConfigs.Length}");
                sb.AppendLine($"# Total values: {values.Length}");
                sb.AppendLine("#");

                int idx = 0;

                foreach (NoiseTestConfig cfg in noiseConfigs)
                {
                    sb.AppendLine($"# {cfg.Name} (2D×{s_coords2D.Length} + 3D×{s_coords3D.Length})");
                    for (int i = 0; i < s_coords2D.Length + s_coords3D.Length; i++)
                        sb.AppendLine(values[idx++].ToString("R", CultureInfo.InvariantCulture));
                }

                foreach (WarpTestConfig cfg in warpConfigs)
                {
                    int count = s_coords2D.Length * 2 + s_coords3D.Length * 3;
                    sb.AppendLine($"# {cfg.Name} (2D×{s_coords2D.Length}×2 + 3D×{s_coords3D.Length}×3)");
                    for (int i = 0; i < count; i++)
                        sb.AppendLine(values[idx++].ToString("R", CultureInfo.InvariantCulture));
                }

                File.WriteAllText(path, sb.ToString());
            }

            private static float[] ReadGoldenFile(string path)
            {
                string[] lines = File.ReadAllLines(path);
                var values = new List<float>(512);
                foreach (string line in lines)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed[0] == '#') continue;
                    if (float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                        values.Add(v);
                }

                return values.ToArray();
            }

            // ===== Benchmarks =====

            public void RunBenchmarks()
            {
                _benchReport.AppendLine();
                _benchReport.AppendLine("<b>=== FASTNOISELITE BENCHMARK ===</b>");
                _benchReport.AppendLine($"Grid: {BENCH_GRID_2D}×{BENCH_GRID_2D} (2D), " +
                                        $"{BENCH_GRID_3D}×{BENCH_GRID_3D}×{BENCH_GRID_3D} (3D), " +
                                        $"{BENCH_RUNS} runs (median of), {BENCH_WARMUP} warmup, spacing={BENCH_SPACING}");
                _benchReport.AppendLine();
                _benchReport.Append(BenchmarkEnvironment.DescribeSystem());
                _benchReport.AppendLine(
                    $"{"Config",-28} {"2D ms",8} {"±%",5} {"2D M/s",8}  {"3D ms",8} {"±%",5} {"3D M/s",8}");
                _benchReport.AppendLine(new string('-', 76));

                BenchmarkConfig[] benchConfigs =
                {
                    new BenchmarkConfig("Perlin", FastNoiseLite.NoiseType.Perlin, FastNoiseLite.FractalType.None),
                    new BenchmarkConfig("OpenSimplex2", FastNoiseLite.NoiseType.OpenSimplex2, FastNoiseLite.FractalType.None),
                    new BenchmarkConfig("OpenSimplex2S", FastNoiseLite.NoiseType.OpenSimplex2S, FastNoiseLite.FractalType.None),
                    new BenchmarkConfig("Cellular", FastNoiseLite.NoiseType.Cellular, FastNoiseLite.FractalType.None),
                    new BenchmarkConfig("ValueCubic", FastNoiseLite.NoiseType.ValueCubic, FastNoiseLite.FractalType.None),
                    new BenchmarkConfig("Value", FastNoiseLite.NoiseType.Value, FastNoiseLite.FractalType.None),
                    new BenchmarkConfig("Perlin+FBm4", FastNoiseLite.NoiseType.Perlin, FastNoiseLite.FractalType.FBm),
                    new BenchmarkConfig("OS2+FBm4", FastNoiseLite.NoiseType.OpenSimplex2, FastNoiseLite.FractalType.FBm),
                    new BenchmarkConfig("Cellular+FBm4", FastNoiseLite.NoiseType.Cellular, FastNoiseLite.FractalType.FBm),
                    new BenchmarkConfig("Value+FBm4", FastNoiseLite.NoiseType.Value, FastNoiseLite.FractalType.FBm),
                    new BenchmarkConfig("Perlin+Ridged4", FastNoiseLite.NoiseType.Perlin, FastNoiseLite.FractalType.Ridged),
                    new BenchmarkConfig("Perlin+PingPong4", FastNoiseLite.NoiseType.Perlin, FastNoiseLite.FractalType.PingPong),
                };

                foreach (BenchmarkConfig bc in benchConfigs)
                {
                    FastNoiseLite fnl = FastNoiseLite.Create(SEED);
                    fnl.SetFrequency(FREQUENCY);
                    fnl.SetNoiseType(bc.Type);
                    fnl.SetFractalType(bc.Fractal);
                    if (bc.Fractal != FastNoiseLite.FractalType.None)
                        fnl.SetFractalOctaves(FRACTAL_OCTAVES);

                    BenchResult r2D = RunBench2D(fnl);
                    BenchResult r3D = RunBench3D(fnl);

                    const double SAMPLES_2D = (double)BENCH_GRID_2D * BENCH_GRID_2D;
                    const double SAMPLES_3D = (double)BENCH_GRID_3D * BENCH_GRID_3D * BENCH_GRID_3D;
                    double tp2D = r2D.MedianMs > 0 ? SAMPLES_2D / r2D.MedianMs / 1000.0 : 0;
                    double tp3D = r3D.MedianMs > 0 ? SAMPLES_3D / r3D.MedianMs / 1000.0 : 0;

                    _benchReport.AppendLine(
                        $"{bc.Name,-28} {r2D.MedianMs,8:F2} {r2D.SpreadPct,4:F1}% {tp2D,8:F2}" +
                        $"  {r3D.MedianMs,8:F2} {r3D.SpreadPct,4:F1}% {tp3D,8:F2}");
                }

                // Batch API comparison — both paths evaluate the SAME integer coordinate grid
                _benchReport.AppendLine();
                _benchReport.AppendLine("<b>=== BATCH vs SCALAR (same integer coords) ===</b>");
                _benchReport.AppendLine(
                    $"{"Config",-28} {"Scalar",8} {"Batch",8} {"Δ%",6}  {"Scalar",8} {"Batch",8} {"Δ%",6}");
                _benchReport.AppendLine(
                    $"{"",28} {"2D ms",8} {"2D ms",8} {"2D",6}  {"3D ms",8} {"3D ms",8} {"3D",6}");
                _benchReport.AppendLine(new string('-', 76));

                BenchmarkConfig[] batchConfigs =
                {
                    new BenchmarkConfig("Perlin", FastNoiseLite.NoiseType.Perlin, FastNoiseLite.FractalType.None),
                    new BenchmarkConfig("OpenSimplex2", FastNoiseLite.NoiseType.OpenSimplex2, FastNoiseLite.FractalType.None),
                    new BenchmarkConfig("Cellular", FastNoiseLite.NoiseType.Cellular, FastNoiseLite.FractalType.None),
                    new BenchmarkConfig("Value", FastNoiseLite.NoiseType.Value, FastNoiseLite.FractalType.None),
                    new BenchmarkConfig("Perlin+FBm4", FastNoiseLite.NoiseType.Perlin, FastNoiseLite.FractalType.FBm),
                    new BenchmarkConfig("OS2+FBm4", FastNoiseLite.NoiseType.OpenSimplex2, FastNoiseLite.FractalType.FBm),
                };

                foreach (BenchmarkConfig bc in batchConfigs)
                {
                    FastNoiseLite fnl = FastNoiseLite.Create(SEED);
                    fnl.SetFrequency(FREQUENCY);
                    fnl.SetNoiseType(bc.Type);
                    fnl.SetFractalType(bc.Fractal);
                    if (bc.Fractal != FastNoiseLite.FractalType.None)
                        fnl.SetFractalOctaves(FRACTAL_OCTAVES);

                    BenchResult s2D = RunBenchScalarIntGrid2D(fnl);
                    BenchResult b2D = RunBenchBatch2D(fnl);
                    BenchResult s3D = RunBenchScalarIntGrid3D(fnl);
                    BenchResult b3D = RunBenchBatch3D(fnl);

                    double delta2D = s2D.MedianMs > 0 ? (b2D.MedianMs - s2D.MedianMs) / s2D.MedianMs * 100.0 : 0;
                    double delta3D = s3D.MedianMs > 0 ? (b3D.MedianMs - s3D.MedianMs) / s3D.MedianMs * 100.0 : 0;

                    _benchReport.AppendLine(
                        $"{bc.Name,-28} {s2D.MedianMs,8:F2} {b2D.MedianMs,8:F2} {delta2D,5:F1}%" +
                        $"  {s3D.MedianMs,8:F2} {b3D.MedianMs,8:F2} {delta3D,5:F1}%");
                }
            }

            private static BenchResult RunBench2D(FastNoiseLite fnl)
            {
                const int COUNT = BENCH_GRID_2D * BENCH_GRID_2D;
                NativeArray<float> results = new NativeArray<float>(COUNT, Allocator.TempJob);

                for (int i = 0; i < BENCH_WARMUP; i++)
                {
                    new NoiseBench2DJob
                    {
                        Noise = fnl, GridSize = BENCH_GRID_2D, Spacing = BENCH_SPACING, Results = results,
                    }.Schedule().Complete();
                }

                double[] samples = new double[BENCH_RUNS];
                for (int i = 0; i < BENCH_RUNS; i++)
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    new NoiseBench2DJob
                    {
                        Noise = fnl, GridSize = BENCH_GRID_2D, Spacing = BENCH_SPACING, Results = results,
                    }.Schedule().Complete();
                    sw.Stop();
                    samples[i] = sw.Elapsed.TotalMilliseconds;
                }

                results.Dispose();
                return BenchResult.FromSamples(samples);
            }

            private static BenchResult RunBench3D(FastNoiseLite fnl)
            {
                const int COUNT = BENCH_GRID_3D * BENCH_GRID_3D * BENCH_GRID_3D;
                NativeArray<float> results = new NativeArray<float>(COUNT, Allocator.TempJob);

                for (int i = 0; i < BENCH_WARMUP; i++)
                {
                    new NoiseBench3DJob
                    {
                        Noise = fnl, GridSize = BENCH_GRID_3D, Spacing = BENCH_SPACING, Results = results,
                    }.Schedule().Complete();
                }

                double[] samples = new double[BENCH_RUNS];
                for (int i = 0; i < BENCH_RUNS; i++)
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    new NoiseBench3DJob
                    {
                        Noise = fnl, GridSize = BENCH_GRID_3D, Spacing = BENCH_SPACING, Results = results,
                    }.Schedule().Complete();
                    sw.Stop();
                    samples[i] = sw.Elapsed.TotalMilliseconds;
                }

                results.Dispose();
                return BenchResult.FromSamples(samples);
            }

            private static BenchResult RunBenchScalarIntGrid2D(FastNoiseLite fnl)
            {
                const int COUNT = BENCH_GRID_2D * BENCH_GRID_2D;
                NativeArray<float> results = new NativeArray<float>(COUNT, Allocator.TempJob);

                for (int i = 0; i < BENCH_WARMUP; i++)
                {
                    new NoiseScalarIntGrid2DJob
                    {
                        Noise = fnl, GridSize = BENCH_GRID_2D, Results = results,
                    }.Schedule().Complete();
                }

                double[] samples = new double[BENCH_RUNS];
                for (int i = 0; i < BENCH_RUNS; i++)
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    new NoiseScalarIntGrid2DJob
                    {
                        Noise = fnl, GridSize = BENCH_GRID_2D, Results = results,
                    }.Schedule().Complete();
                    sw.Stop();
                    samples[i] = sw.Elapsed.TotalMilliseconds;
                }

                results.Dispose();
                return BenchResult.FromSamples(samples);
            }

            private static BenchResult RunBenchScalarIntGrid3D(FastNoiseLite fnl)
            {
                const int COUNT = BENCH_GRID_3D * BENCH_GRID_3D * BENCH_GRID_3D;
                NativeArray<float> results = new NativeArray<float>(COUNT, Allocator.TempJob);

                for (int i = 0; i < BENCH_WARMUP; i++)
                {
                    new NoiseScalarIntGrid3DJob
                    {
                        Noise = fnl, GridSize = BENCH_GRID_3D, Results = results,
                    }.Schedule().Complete();
                }

                double[] samples = new double[BENCH_RUNS];
                for (int i = 0; i < BENCH_RUNS; i++)
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    new NoiseScalarIntGrid3DJob
                    {
                        Noise = fnl, GridSize = BENCH_GRID_3D, Results = results,
                    }.Schedule().Complete();
                    sw.Stop();
                    samples[i] = sw.Elapsed.TotalMilliseconds;
                }

                results.Dispose();
                return BenchResult.FromSamples(samples);
            }

            private static BenchResult RunBenchBatch2D(FastNoiseLite fnl)
            {
                const int COUNT = BENCH_GRID_2D * BENCH_GRID_2D;
                NativeArray<float> results = new NativeArray<float>(COUNT, Allocator.TempJob);

                for (int i = 0; i < BENCH_WARMUP; i++)
                {
                    new NoiseBatchBench2DJob
                    {
                        Noise = fnl, GridSize = BENCH_GRID_2D, StartX = 0, StartY = 0, Results = results,
                    }.Schedule().Complete();
                }

                double[] samples = new double[BENCH_RUNS];
                for (int i = 0; i < BENCH_RUNS; i++)
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    new NoiseBatchBench2DJob
                    {
                        Noise = fnl, GridSize = BENCH_GRID_2D, StartX = 0, StartY = 0, Results = results,
                    }.Schedule().Complete();
                    sw.Stop();
                    samples[i] = sw.Elapsed.TotalMilliseconds;
                }

                results.Dispose();
                return BenchResult.FromSamples(samples);
            }

            private static BenchResult RunBenchBatch3D(FastNoiseLite fnl)
            {
                const int COUNT = BENCH_GRID_3D * BENCH_GRID_3D * BENCH_GRID_3D;
                NativeArray<float> results = new NativeArray<float>(COUNT, Allocator.TempJob);

                for (int i = 0; i < BENCH_WARMUP; i++)
                {
                    new NoiseBatchBench3DJob
                    {
                        Noise = fnl, GridSize = BENCH_GRID_3D, StartX = 0, StartY = 0, StartZ = 0,
                        Results = results,
                    }.Schedule().Complete();
                }

                double[] samples = new double[BENCH_RUNS];
                for (int i = 0; i < BENCH_RUNS; i++)
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    new NoiseBatchBench3DJob
                    {
                        Noise = fnl, GridSize = BENCH_GRID_3D, StartX = 0, StartY = 0, StartZ = 0,
                        Results = results,
                    }.Schedule().Complete();
                    sw.Stop();
                    samples[i] = sw.Elapsed.TotalMilliseconds;
                }

                results.Dispose();
                return BenchResult.FromSamples(samples);
            }

            // ===== Reporting =====

            public void PrintSummary()
            {
                StringBuilder sb = new StringBuilder();

                if (_failed == 0)
                    sb.AppendLine($"<color=lime>[FastNoiseLiteTests] All {_passed} tests passed.</color>");
                else
                    sb.AppendLine(
                        $"<color=red>[FastNoiseLiteTests] {_passed} passed, {_failed} FAILED.</color>");

                sb.Append(_benchReport);

                string fullReport = sb.ToString();
                Debug.Log(fullReport);
                BenchmarkEnvironment.WriteReportToDisk(fullReport, "FastNoiseLiteBenchmark");
            }

            // ===== Assertion Helpers =====

            private void AssertTrue(bool condition, string msg)
            {
                if (condition)
                    _passed++;
                else
                    Fail(msg);
            }

            private void AssertEqual(float a, float b, string msg)
            {
                if (math.asuint(a) == math.asuint(b))
                    _passed++;
                else
                    Fail($"{msg}: {a} != {b}");
            }

            private void AssertNear(float expected, float actual, float tolerance, string msg)
            {
                if (math.abs(expected - actual) <= tolerance)
                    _passed++;
                else
                    Fail($"{msg}: expected {expected}, got {actual}, delta {math.abs(expected - actual)}");
            }

            private void Fail(string msg)
            {
                _failed++;
                Debug.LogError($"[FastNoiseLiteTests] FAIL: {msg}");
            }
        }

        #endregion

        #region Benchmark Helpers

        private readonly struct BenchmarkConfig
        {
            public readonly string Name;
            public readonly FastNoiseLite.NoiseType Type;
            public readonly FastNoiseLite.FractalType Fractal;

            public BenchmarkConfig(string name, FastNoiseLite.NoiseType type, FastNoiseLite.FractalType fractal)
            {
                Name = name;
                Type = type;
                Fractal = fractal;
            }
        }

        /// <summary>
        /// Holds the statistical summary of a single benchmark configuration's timed runs.
        /// </summary>
        private readonly struct BenchResult
        {
            public readonly double MedianMs;
            private readonly double _minMs;
            private readonly double _maxMs;

            /// <summary>Spread as a percentage of the median: (max − min) / median × 100.</summary>
            public double SpreadPct => MedianMs > 0 ? (_maxMs - _minMs) / MedianMs * 100.0 : 0;

            private BenchResult(double median, double min, double max)
            {
                MedianMs = median;
                _minMs = min;
                _maxMs = max;
            }

            /// <summary>
            /// Sorts the samples, then returns the median, min, and max.
            /// </summary>
            public static BenchResult FromSamples(double[] samples)
            {
                Array.Sort(samples);
                double median = samples[samples.Length / 2];
                return new BenchResult(median, samples[0], samples[^1]);
            }
        }

        #endregion

        #region Burst Benchmark Jobs

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Fast, OptimizeFor = OptimizeFor.Performance)]
        private struct NoiseBench2DJob : IJob
        {
            public FastNoiseLite Noise;
            public int GridSize;
            public float Spacing;

            [WriteOnly]
            public NativeArray<float> Results;

            public void Execute()
            {
                int idx = 0;
                for (int y = 0; y < GridSize; y++)
                for (int x = 0; x < GridSize; x++)
                    Results[idx++] = Noise.GetNoise(x * Spacing, y * Spacing);
            }
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Fast, OptimizeFor = OptimizeFor.Performance)]
        private struct NoiseBench3DJob : IJob
        {
            public FastNoiseLite Noise;
            public int GridSize;
            public float Spacing;

            [WriteOnly]
            public NativeArray<float> Results;

            public void Execute()
            {
                int idx = 0;
                for (int z = 0; z < GridSize; z++)
                for (int y = 0; y < GridSize; y++)
                for (int x = 0; x < GridSize; x++)
                    Results[idx++] = Noise.GetNoise(x * Spacing, y * Spacing, z * Spacing);
            }
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Fast, OptimizeFor = OptimizeFor.Performance)]
        private struct NoiseScalarIntGrid2DJob : IJob
        {
            public FastNoiseLite Noise;
            public int GridSize;

            [WriteOnly]
            public NativeArray<float> Results;

            public void Execute()
            {
                int idx = 0;
                for (int y = 0; y < GridSize; y++)
                for (int x = 0; x < GridSize; x++)
                    Results[idx++] = Noise.GetNoise(x, y);
            }
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Fast, OptimizeFor = OptimizeFor.Performance)]
        private struct NoiseScalarIntGrid3DJob : IJob
        {
            public FastNoiseLite Noise;
            public int GridSize;

            [WriteOnly]
            public NativeArray<float> Results;

            public void Execute()
            {
                int idx = 0;
                for (int z = 0; z < GridSize; z++)
                for (int y = 0; y < GridSize; y++)
                for (int x = 0; x < GridSize; x++)
                    Results[idx++] = Noise.GetNoise(x, y, z);
            }
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Fast, OptimizeFor = OptimizeFor.Performance)]
        private struct NoiseBatchBench2DJob : IJob
        {
            public FastNoiseLite Noise;
            public int GridSize;
            public int StartX;
            public int StartY;

            [WriteOnly]
            public NativeArray<float> Results;

            public void Execute()
            {
                Noise.GetNoiseGrid(StartX, StartY, GridSize, GridSize, Results);
            }
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Fast, OptimizeFor = OptimizeFor.Performance)]
        private struct NoiseBatchBench3DJob : IJob
        {
            public FastNoiseLite Noise;
            public int GridSize;
            public int StartX;
            public int StartY;
            public int StartZ;

            [WriteOnly]
            public NativeArray<float> Results;

            public void Execute()
            {
                Noise.GetNoiseGrid(StartX, StartY, StartZ, GridSize, GridSize, GridSize, Results);
            }
        }

        #endregion
    }
}
