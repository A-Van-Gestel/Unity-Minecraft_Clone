using System.Text;
using UnityEngine;

namespace Benchmarks
{
    /// <summary>
    /// Renders the full-world fluid stress pass's per-phase <b>attribution</b> report — the data that closes the
    /// TG-4 §5 gate left open by the isolated tick benchmark: in the <i>real</i> ocean frame, is the behavior
    /// <b>Tick</b> or the <b>Mesh</b> rebuild it triggers the dominant main-thread cost? Reuses
    /// <see cref="BenchmarkEnvironment"/> for the system/build header and disk writer, and <see cref="ReportTable"/>
    /// for layout. The console log + on-disk copy is the deliverable (no results-screen UI, matching
    /// <c>FluidTickBenchmark</c>).
    /// </summary>
    public static class FluidStressReportGenerator
    {
        /// <summary>
        /// Builds the report from the collected phases, logs it, writes a plain-text copy to disk, and returns both
        /// the rich-text report (for the results screen) and the saved file path.
        /// </summary>
        /// <param name="collector">The completed collector holding the Baseline + Flood phases.</param>
        /// <param name="regionChunks">Side length of the square flood region, in chunks.</param>
        /// <returns>The rich-text report and the absolute log path (path <c>null</c> if writing failed).</returns>
        public static BenchmarkReportResult GenerateAndWrite(FluidStressMetricsCollector collector, int regionChunks)
        {
            StringBuilder sb = new StringBuilder(4096);

            sb.AppendLine("<color=cyan><b>--- FULL-WORLD FLUID STRESS PASS — FRAME ATTRIBUTION REPORT ---</b></color>");
            sb.AppendLine("Real loaded world, real throttled pipeline (mesh + lighting + cross-chunk). Per-frame " +
                          "Tick / Apply / Mesh / Light split via WorldFrameProfiler (Stopwatch, IL2CPP-valid).");
            sb.AppendLine("Frame = wall ms (unscaledDeltaTime, includes render/GPU). Sub-phases = main-thread " +
                          "World.Update interior. avg + peak are both true per-frame.");
            sb.AppendLine($"Flood region: {regionChunks}×{regionChunks} = {regionChunks * regionChunks} chunks. " +
                          $"Deterministic suspended basin — solid floor at y{FluidBenchmarkScenarios.SkyFloorY}, water " +
                          $"cap y{FluidBenchmarkScenarios.SkyWaterBaseY}–{FluidBenchmarkScenarios.SkyWaterTopY} — " +
                          $"flooding + overflowing across chunk borders (substrate stamped + settled pre-baseline).");
            sb.AppendLine();
            sb.Append(BenchmarkEnvironment.DescribeSystem());

            AppendAttributionTable(sb, collector, peak: false);
            AppendAttributionTable(sb, collector, peak: true);
            AppendContextTable(sb, collector);
            AppendVerdict(sb, collector);

            string report = sb.ToString();
            Debug.Log(report);
            string path = BenchmarkEnvironment.WriteReportToDisk(report, "FluidStressPass");

            return new BenchmarkReportResult
            {
                ReportRichText = report,
                LogFilePath = path,
            };
        }

        /// <summary>Appends the per-phase sub-phase split (avg or peak ms) with Tick% / Mesh% of the sub-phase total.</summary>
        private static void AppendAttributionTable(StringBuilder sb, FluidStressMetricsCollector collector, bool peak)
        {
            sb.AppendLine();
            sb.AppendLine($"<b>=== Attribution — {(peak ? "PEAK" : "AVG")} ms/frame ===</b>");

            ReportTable table = new ReportTable("Phase", "Frame", "Tick", "Apply", "Mesh", "Light", "Tick%", "Mesh%");

            foreach (FluidStressMetricsCollector.PhaseResult p in collector.Phases)
            {
                double frame = peak ? p.FrameMsPeak : p.FrameMsAvg;
                double tick = peak ? p.TickMsPeak : p.TickMsAvg;
                double apply = peak ? p.ApplyMsPeak : p.ApplyMsAvg;
                double mesh = peak ? p.MeshMsPeak : p.MeshMsAvg;
                double light = peak ? p.LightMsPeak : p.LightMsAvg;

                // Percentages are of the AVG sub-phase total (the stable attribution denominator), shown on both
                // tables so the avg/peak rows share one reference split.
                double subTotal = p.SubPhaseMsAvg;
                double tickPct = subTotal > 0 ? p.TickMsAvg / subTotal * 100.0 : 0;
                double meshPct = subTotal > 0 ? p.MeshMsAvg / subTotal * 100.0 : 0;

                table.AddRow(
                    p.Name,
                    frame.ToString("F3"),
                    tick.ToString("F3"),
                    apply.ToString("F3"),
                    mesh.ToString("F3"),
                    light.ToString("F3"),
                    tickPct.ToString("F1"),
                    meshPct.ToString("F1"));
            }

            table.AppendTo(sb);
        }

        /// <summary>Appends per-phase frame-count / duration / frame-time / FPS / GC context.</summary>
        private static void AppendContextTable(StringBuilder sb, FluidStressMetricsCollector collector)
        {
            sb.AppendLine();
            sb.AppendLine("<b>=== Frame context ===</b>");

            ReportTable table = new ReportTable("Phase", "Frames", "Duration", "Frame avg", "Frame peak", "Min FPS", "GC/frame");

            foreach (FluidStressMetricsCollector.PhaseResult p in collector.Phases)
            {
                double minFps = p.FrameMsPeak > 0 ? 1000.0 / p.FrameMsPeak : 0;
                table.AddRow(
                    p.Name,
                    p.Frames.ToString(),
                    $"{p.DurationSeconds:F1} s",
                    $"{p.FrameMsAvg:F2} ms",
                    $"{p.FrameMsPeak:F2} ms",
                    minFps.ToString("F0"),
                    $"{p.GcKbAvg:F1} KB");
            }

            table.AppendTo(sb);
        }

        /// <summary>
        /// Appends the §5 verdict, comparing the flood phase's Tick vs Mesh cost. A perfect parallel TG-4
        /// re-architects only the Tick; if Mesh dominates the flood frame instead, parallelizing the tick wins
        /// little and the mesh-rebuild path is the real bottleneck.
        /// </summary>
        private static void AppendVerdict(StringBuilder sb, FluidStressMetricsCollector collector)
        {
            sb.AppendLine();
            sb.AppendLine("<b>=== Verdict (TG-4 §5 attribution) ===</b>");

            if (collector.Phases.Count == 0)
            {
                sb.AppendLine("  No phases recorded.");
                return;
            }

            // The flood phase is the last recorded phase.
            FluidStressMetricsCollector.PhaseResult flood = collector.Phases[collector.Phases.Count - 1];

            sb.AppendLine($"  Flood phase: \"{flood.Name}\"");
            sb.AppendLine($"  Tick  avg {flood.TickMsAvg:F3} ms / peak {flood.TickMsPeak:F3} ms");
            sb.AppendLine($"  Mesh  avg {flood.MeshMsAvg:F3} ms / peak {flood.MeshMsPeak:F3} ms");
            sb.AppendLine($"  Apply avg {flood.ApplyMsAvg:F3} ms / Light avg {flood.LightMsAvg:F3} ms");

            string dominant = flood.TickMsPeak >= flood.MeshMsPeak ? "TICK" : "MESH";
            if (dominant == "TICK")
            {
                sb.AppendLine("  → TICK dominates the flood frame: TG-4's parallel-for-fluid tick re-architecture " +
                              "targets the dominant cost — committing the Phase-3 fluid→Burst engineering is justified.");
            }
            else
            {
                sb.AppendLine("  → MESH dominates the flood frame: the per-edit mesh rebuild, not the tick, is the " +
                              "real ocean-frame bottleneck. Parallelizing the tick alone (TG-4) would leave it — " +
                              "reconsider scope before committing the Phase-3 fluid→Burst engineering.");
            }

            sb.AppendLine();
            sb.AppendLine("  (Sub-phases are the main-thread World.Update interior; generation/pool/render are " +
                          "excluded from the split but included in Frame ms.)");
        }
    }
}
