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

            AppendAvgTable(sb, collector);
            AppendWorstFrameTable(sb, collector);
            AppendIndependentPeaksTable(sb, collector);
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

        /// <summary>
        /// Appends the per-phase <b>average</b> sub-phase split. Tick% / Mesh% / Light% are shares of the whole
        /// <b>Frame</b> (not of the sub-phase total) so they line up with the frame-relative reading the report's
        /// narrative uses; the sub-phases do not sum to 100% because generation/pool/render sit outside the split.
        /// </summary>
        private static void AppendAvgTable(StringBuilder sb, FluidStressMetricsCollector collector)
        {
            sb.AppendLine();
            sb.AppendLine("<b>=== Attribution — AVG ms/frame (sustained cost) ===</b>");
            sb.AppendLine("(Tick% / Mesh% / Light% are shares of the whole Frame; they don't sum to 100% — " +
                          "generation/pool/render are outside the split.)");

            ReportTable table = new ReportTable("Phase", "Frame", "Tick", "Apply", "Mesh", "Light", "Tick%", "Mesh%", "Light%");

            foreach (FluidStressMetricsCollector.PhaseResult p in collector.Phases)
            {
                double denom = p.FrameMsAvg > 0 ? p.FrameMsAvg : 1.0;
                table.AddRow(
                    p.Name,
                    p.FrameMsAvg.ToString("F3"),
                    p.TickMsAvg.ToString("F3"),
                    p.ApplyMsAvg.ToString("F3"),
                    p.MeshMsAvg.ToString("F3"),
                    p.LightMsAvg.ToString("F3"),
                    (p.TickMsAvg / denom * 100.0).ToString("F1"),
                    (p.MeshMsAvg / denom * 100.0).ToString("F1"),
                    (p.LightMsAvg / denom * 100.0).ToString("F1"));
            }

            table.AppendTo(sb);
        }

        /// <summary>
        /// Appends the composition of the single <b>worst whole-frame</b> per phase — the frame that set
        /// <see cref="FluidStressMetricsCollector.PhaseResult.FrameMsPeak"/> and its own Tick/Apply/Mesh/Light.
        /// Because this is ONE real frame, the Tick% / Mesh% / Light% (shares of that frame) substantiate an
        /// "X% of the worst frame" statement — unlike the independent per-metric peaks below.
        /// </summary>
        private static void AppendWorstFrameTable(StringBuilder sb, FluidStressMetricsCollector collector)
        {
            sb.AppendLine();
            sb.AppendLine("<b>=== Worst single frame — composition ===</b>");
            sb.AppendLine("(ONE real frame: the max-Frame-ms frame and its own breakdown. % are shares of that frame.)");

            ReportTable table = new ReportTable("Phase", "Frame", "Tick", "Apply", "Mesh", "Light", "Tick%", "Mesh%", "Light%");

            foreach (FluidStressMetricsCollector.PhaseResult p in collector.Phases)
            {
                double denom = p.FrameMsPeak > 0 ? p.FrameMsPeak : 1.0;
                table.AddRow(
                    p.Name,
                    p.FrameMsPeak.ToString("F3"),
                    p.PeakFrameTickMs.ToString("F3"),
                    p.PeakFrameApplyMs.ToString("F3"),
                    p.PeakFrameMeshMs.ToString("F3"),
                    p.PeakFrameLightMs.ToString("F3"),
                    (p.PeakFrameTickMs / denom * 100.0).ToString("F1"),
                    (p.PeakFrameMeshMs / denom * 100.0).ToString("F1"),
                    (p.PeakFrameLightMs / denom * 100.0).ToString("F1"));
            }

            table.AppendTo(sb);
        }

        /// <summary>
        /// Appends the <b>independent</b> per-metric maxima — the largest each sub-phase reached across all frames.
        /// These are spike <i>magnitudes</i> (e.g. the worst dam-break tick), and a row is NOT one frame: the cells
        /// may come from different frames and from a different frame than the worst-frame table above. Kept because
        /// the spike magnitude is the TG-4 justification, but labeled so it is never read as a composition.
        /// </summary>
        private static void AppendIndependentPeaksTable(StringBuilder sb, FluidStressMetricsCollector collector)
        {
            sb.AppendLine();
            sb.AppendLine("<b>=== Per-metric peaks (independent maxima) ===</b>");
            sb.AppendLine("(Max each sub-phase reached across all frames — spike magnitudes. A row is NOT one frame: " +
                          "the cells need not share a frame with each other or with the worst frame above.)");

            ReportTable table = new ReportTable("Phase", "Frame", "Tick", "Apply", "Mesh", "Light");

            foreach (FluidStressMetricsCollector.PhaseResult p in collector.Phases)
            {
                table.AddRow(
                    p.Name,
                    p.FrameMsPeak.ToString("F3"),
                    p.TickMsPeak.ToString("F3"),
                    p.ApplyMsPeak.ToString("F3"),
                    p.MeshMsPeak.ToString("F3"),
                    p.LightMsPeak.ToString("F3"));
            }

            table.AppendTo(sb);
        }

        /// <summary>Appends per-phase frame-count / duration / frame-time / FPS / GC context.</summary>
        private static void AppendContextTable(StringBuilder sb, FluidStressMetricsCollector collector)
        {
            sb.AppendLine();
            sb.AppendLine("<b>=== Frame context ===</b>");

            ReportTable table = new ReportTable("Phase", "Frames", "Duration", "Frame avg", "Frame peak", "Min FPS", "GC/frame avg", "GC/frame peak");

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
                    $"{p.GcKbAvg:F1} KB",
                    $"{p.GcKbPeak:F1} KB");
            }

            table.AppendTo(sb);
        }

        /// <summary>
        /// Appends the §5 verdict. TG-4 (parallel-for-fluid) re-architects <b>only the Tick</b>, so the verdict
        /// separates the two questions it must answer rather than a single Tick-vs-Mesh peak compare:
        /// <list type="number">
        /// <item><b>The spike</b> — which sub-phase owns the single <i>worst</i> frame (the stutter the player feels);
        /// TG-4 helps only if that is the Tick.</item>
        /// <item><b>The sustained cost</b> — which sub-phase dominates the <i>average</i> frame; if that is Light
        /// (or Mesh) rather than Tick, TG-4 leaves the average ocean-frame smoothness untouched.</item>
        /// </list>
        /// Both dominants weigh all four sub-phases (including Light), so the verdict can no longer report
        /// "TICK dominates" while Lighting actually owns the sustained frame.
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
            FluidStressMetricsCollector.PhaseResult flood = collector.Phases[^1];

            // Sustained = the sub-phase that dominates the AVERAGE frame; spike = the sub-phase that dominates the
            // single WORST whole-frame. Both consider all four sub-phases (Light included).
            string sustained = Dominant(flood.TickMsAvg, flood.ApplyMsAvg, flood.MeshMsAvg, flood.LightMsAvg, out double sustainedMs);
            string spike = Dominant(flood.PeakFrameTickMs, flood.PeakFrameApplyMs, flood.PeakFrameMeshMs, flood.PeakFrameLightMs, out double spikeMs);

            double frameAvg = flood.FrameMsAvg > 0 ? flood.FrameMsAvg : 1.0;
            double framePeak = flood.FrameMsPeak > 0 ? flood.FrameMsPeak : 1.0;

            sb.AppendLine($"  Flood phase: \"{flood.Name}\"");
            sb.AppendLine($"  Sustained (avg {flood.FrameMsAvg:F2} ms/frame): {sustained}-dominated — " +
                          $"{sustainedMs:F3} ms = {sustainedMs / frameAvg * 100.0:F0}% of the avg frame.");
            sb.AppendLine($"  Worst frame ({flood.FrameMsPeak:F2} ms): {spike}-dominated — " +
                          $"{spikeMs:F3} ms = {spikeMs / framePeak * 100.0:F0}% of that frame.");
            sb.AppendLine($"  Tick spike magnitude (max single-frame tick): {flood.TickMsPeak:F3} ms. " +
                          $"Mesh: avg {flood.MeshMsAvg:F3} ms / peak {flood.MeshMsPeak:F3} ms.");
            sb.AppendLine();

            bool tickOwnsSpike = spike == "Tick";
            bool tickOwnsSustained = sustained == "Tick";

            if (tickOwnsSpike)
                sb.AppendLine("  → TG-4 (parallel-for-fluid) targets the TICK, which owns the worst-frame spike — " +
                              "committing the Phase-3 fluid→Burst engineering is justified to remove that stutter.");
            else
                sb.AppendLine($"  → The worst frame is {spike}-bound, not Tick-bound — TG-4 (which re-architects only " +
                              "the tick) would NOT remove this spike; reconsider scope before committing Phase-3.");

            if (tickOwnsSustained)
                sb.AppendLine("  → The Tick also dominates the sustained frame, so TG-4 improves both the spike and the average.");
            else
                sb.AppendLine($"  → But the SUSTAINED frame is {sustained}-dominated, which TG-4 does not touch — " +
                              $"average ocean-frame smoothness needs the {sustained} lever, not (only) the tick.");

            sb.AppendLine();
            sb.AppendLine("  (Sub-phases are the main-thread World.Update interior; generation/pool/render are " +
                          "excluded from the split but included in Frame ms. \"Worst frame\" is ONE frame; the " +
                          "per-metric peaks table lists independent maxima.)");
        }

        /// <summary>
        /// Returns the label of the largest of the four sub-phase values and, via <paramref name="value"/>, that
        /// largest value. Ties resolve in Tick → Apply → Mesh → Light order.
        /// </summary>
        private static string Dominant(double tick, double apply, double mesh, double light, out double value)
        {
            value = tick;
            string name = "Tick";
            if (apply > value)
            {
                value = apply;
                name = "Apply";
            }

            if (mesh > value)
            {
                value = mesh;
                name = "Mesh";
            }

            if (light > value)
            {
                value = light;
                name = "Light";
            }

            return name;
        }
    }
}
