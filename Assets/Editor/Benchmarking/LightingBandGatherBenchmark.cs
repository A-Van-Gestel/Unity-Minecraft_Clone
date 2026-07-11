using System.Diagnostics;
using System.IO;
using System.Text;
using Editor.Validation.Lighting;
using Editor.Validation.Lighting.Framework;
using Helpers;
using Jobs;
using Unity.Jobs;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Editor.Benchmarking
{
    /// <summary>
    /// Editor A/B microbenchmark for the LI-2 banded lighting gather (see
    /// <c>Documentation/Design/PERFORMANCE_IMPROVEMENTS_REPORT.md</c> §LI-2). For identical
    /// harness-built worlds it times <c>NeighborhoodLightingJob.Run()</c> — the in-job gather +
    /// scans + BFS the band restricts — under <see cref="LightingBandGatherMode.FullHeight"/> vs
    /// <see cref="LightingBandGatherMode.Derived"/>, across three job shapes:
    /// <list type="bullet">
    /// <item><b>no-op relight</b> — no queued work: pure gather + emission scan + stability, the fixed
    /// per-job floor the band attacks most directly;</item>
    /// <item><b>lamp BFS</b> — a queued lamp placement over a dark steady field: gather + scans + a
    /// full 15-radius blocklight spread (the steady-state relight shape);</item>
    /// <item><b>edge check</b> — the border consistency pass over a consistent world: gather + the
    /// 4-border scan (full-height 8,192 columns vs band-clamped).</item>
    /// </list>
    /// Two floor heights vary the derived band (tight vs mid). <b>Editor Mono numbers are
    /// SCREENING-ONLY</b> (per the perf-benchmark protocol) — the shippable capture is the in-game
    /// IL2CPP flag A/B (<c>World.EnableLightingBandGather</c>). Editor-only; never compiled into a build.
    /// </summary>
    internal static class LightingBandGatherBenchmark
    {
        private const int WARMUP_RUNS = 4;
        private const int SAMPLE_RUNS = 24;
        private const int GRID = 3;

        /// <summary>Timing distribution of one (floor, leg, mode) cell, in microseconds.</summary>
        private struct Cell
        {
            public double MeanUs;
            public double MinUs;
            public double MedianUs;
            public double StdDevUs;
            public int BandHeight;
        }

        [MenuItem("Minecraft Clone/Benchmarks/Lighting Band Gather (LI-2)")]
        private static void Run()
        {
            string outPath = Path.Combine(Application.temporaryCachePath, "lighting_band_gather_bench.txt");
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== LI-2 Lighting Band Gather A/B (EDITOR SCREENING — not a shippable capture) ===");
            sb.AppendLine($"grid {GRID}x{GRID}, {SAMPLE_RUNS} samples after {WARMUP_RUNS} warmups, timing NeighborhoodLightingJob.Run() only");
            sb.AppendLine();

            foreach (int floorY in new[] { 10, 60 })
            {
                sb.AppendLine($"--- floor y={floorY} ---");
                AppendLegComparison(sb, "no-op relight", floorY, lampY: -1, edgeCheck: false);
                AppendLegComparison(sb, "lamp BFS     ", floorY, lampY: floorY + 20, edgeCheck: false);
                AppendLegComparison(sb, "edge check   ", floorY, lampY: -1, edgeCheck: true);
                sb.AppendLine();
            }

            string report = sb.ToString();
            Debug.Log(report);
            File.WriteAllText(outPath, report);
            Debug.Log($"[LightingBandGatherBenchmark] report written to {outPath}");
        }

        /// <summary>Measures one leg in both band modes and appends the comparison row.</summary>
        /// <param name="sb">The report builder.</param>
        /// <param name="label">The leg label.</param>
        /// <param name="floorY">Superflat floor height.</param>
        /// <param name="lampY">Lamp height for the lamp leg, or −1 for no lamp.</param>
        /// <param name="edgeCheck">Whether the job runs the border consistency pass.</param>
        private static void AppendLegComparison(StringBuilder sb, string label, int floorY, int lampY, bool edgeCheck)
        {
            Cell full = MeasureLeg(LightingBandGatherMode.FullHeight, floorY, lampY, edgeCheck);
            Cell band = MeasureLeg(LightingBandGatherMode.Derived, floorY, lampY, edgeCheck);
            double deltaPct = full.MeanUs > 0 ? (band.MeanUs - full.MeanUs) / full.MeanUs * 100.0 : 0;

            sb.AppendLine($"{label}  full(h=128): mean {full.MeanUs,8:F1} us  min {full.MinUs,8:F1}  med {full.MedianUs,8:F1}  sd {full.StdDevUs,7:F1}");
            sb.AppendLine($"{label}  band(h={band.BandHeight,3}): mean {band.MeanUs,8:F1} us  min {band.MinUs,8:F1}  med {band.MedianUs,8:F1}  sd {band.StdDevUs,7:F1}  delta {deltaPct,6:F1}%");
        }

        /// <summary>
        /// Builds a converged superflat world in the given band mode and samples the job time for the
        /// requested leg. The lamp leg cycles break→converge→place around each sample so every timed
        /// run sees the identical dark-field + queued-lamp input; the no-op and edge legs are
        /// state-preserving (pristine merge) and loop tightly.
        /// </summary>
        /// <param name="mode">The band mode under test.</param>
        /// <param name="floorY">Superflat floor height.</param>
        /// <param name="lampY">Lamp height, or −1 for no lamp.</param>
        /// <param name="edgeCheck">Whether the job runs the border consistency pass.</param>
        /// <returns>The cell's timing distribution.</returns>
        private static Cell MeasureLeg(LightingBandGatherMode mode, int floorY, int lampY, bool edgeCheck)
        {
            using LightingTestWorld world = new LightingTestWorld(GRID);
            world.BandGatherMode = mode;
            world.FillSuperflatFloor(floorY, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();
            world.RunInitialLighting();

            Vector2Int center = new Vector2Int(1, 1);
            Vector3Int lampPos = new Vector3Int(24, lampY, 24);

            double[] samples = new double[SAMPLE_RUNS];
            int bandHeight = ChunkMath.CHUNK_HEIGHT;
            Stopwatch sw = new Stopwatch();

            for (int i = -WARMUP_RUNS; i < SAMPLE_RUNS; i++)
            {
                if (lampY >= 0) world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);

                LightingTestWorld.LightingJobFlight flight = world.BeginLightingJob(center, edgeCheck);
                bandHeight = flight.BandHeight;

                // Time the job in isolation. flight.Job shares the flight's native containers, so this
                // run drains the BFS queues and writes the padded volume — the band mode under test is
                // already baked into BandHeight/BandTopLight at BeginLightingJob time.
                NeighborhoodLightingJob job = flight.Job;
                sw.Restart();
                job.Run();
                sw.Stop();
                if (i >= 0) samples[i] = sw.Elapsed.TotalMilliseconds * 1000.0;

                // CompleteLightingJob re-runs the job internally (a second, redundant execution) and
                // merges its result. That re-run sees EMPTY BFS queues (this timed run drained them),
                // so the lamp field is re-derived from the emission-sync scan (PASS -2 re-seeds the
                // emissive lamp from the voxel snapshot) rather than the queued placement node — the two
                // converge to the identical field. Clear the timed run's appended mods first so the
                // completion routes each cross-chunk mod exactly once (interior legs emit none anyway).
                flight.Mods.Clear();
                flight.PullBackClaims.Clear();
                world.CompleteLightingJob(flight);

                if (lampY >= 0)
                {
                    // Reset to the dark steady field so the next timed sample is identical.
                    world.BreakBlock(lampPos);
                    world.RunToConvergence();
                }
            }

            return BuildCell(samples, bandHeight);
        }

        /// <summary>Computes the distribution stats for one cell.</summary>
        /// <param name="samples">The per-run timings in microseconds.</param>
        /// <param name="bandHeight">The band height the leg's jobs ran with.</param>
        /// <returns>The populated cell.</returns>
        private static Cell BuildCell(double[] samples, int bandHeight)
        {
            double[] sorted = (double[])samples.Clone();
            System.Array.Sort(sorted);

            double sum = 0;
            foreach (double s in samples) sum += s;
            double mean = sum / samples.Length;

            double variance = 0;
            foreach (double s in samples) variance += (s - mean) * (s - mean);
            variance /= samples.Length;

            return new Cell
            {
                MeanUs = mean,
                MinUs = sorted[0],
                MedianUs = sorted[sorted.Length / 2],
                StdDevUs = System.Math.Sqrt(variance),
                BandHeight = bandHeight,
            };
        }
    }
}
