using System.Collections.Generic;
using System.Text;
using Helpers;

namespace Benchmarks
{
    /// <summary>
    /// Renders the "Pipeline" section appended to the benchmark report (FP-3): per-phase stage-latency
    /// distributions, waste accounting, admission pressure, and the §7.1 regime verdict.
    /// <para>
    /// <b>§7.2 is binding here:</b> every verdict is printed directly beneath the complete inputs that
    /// produced it — all stop-reason tallies (not just the winner), exact percentiles plus a bucket
    /// histogram, and absolute counts beside every percentage. A later session must be able to reach a
    /// *different* conclusion from this text alone, without re-running a capture on a build that may no
    /// longer exist.
    /// </para>
    /// </summary>
    public static class PipelineReportSection
    {
        private const string RULE_VERSION = "§7.1 v1 (dominant/plurality stop reason; ordering axis at "
                                            + "waste ≥ 20%)";

        /// <summary>
        /// Appends the whole Pipeline section for every recorded phase.
        /// </summary>
        /// <param name="sb">The report builder.</param>
        /// <param name="phases">The telemetry's completed phases, in capture order.</param>
        public static void Append(StringBuilder sb, IReadOnlyList<PipelinePhaseMetrics> phases)
        {
            sb.AppendLine("<b>=== Pipeline Telemetry (FP) ===</b>");

            if (phases == null || phases.Count == 0)
            {
                sb.AppendLine("  No pipeline telemetry recorded (capture ran with telemetry disabled).");
                sb.AppendLine();
                return;
            }

            sb.AppendLine($"Verdict rule:        {RULE_VERSION}");
            sb.AppendLine("Raw results below are MANDATORY context, not decoration: the verdict is derived");
            sb.AppendLine("from them and can be re-derived differently from the same numbers.");
            sb.AppendLine();

            foreach (PipelinePhaseMetrics phase in phases)
                AppendPhase(sb, phase);
        }

        private static void AppendPhase(StringBuilder sb, PipelinePhaseMetrics phase)
        {
            sb.AppendLine($"<b>--- {phase.GroupName} / {phase.PhaseName} ---</b>");
            sb.AppendLine($"Duration:            {phase.DurationSeconds:F1} s   (rates below divide by THIS, " +
                          "not the nominal phase time — the last generation phase runs long)");
            sb.AppendLine($"Frames sampled:      {phase.FrameCount:N0}");

            // Saturation first: every number after it is a prefix of the phase, not the whole of it.
            if (phase.AnySaturation)
            {
                sb.AppendLine();
                sb.AppendLine("  ⚠ TRACE BUFFER SATURATED — the figures below cover only the first " +
                              $"{phase.TracesStarted:N0} chunks of this phase, not all of it.");
                if (phase.TracesSaturated) sb.AppendLine("     · trace table filled (later chunks untraced)");
                if (phase.SamplesSaturated) sb.AppendLine("     · latency sample cap reached (percentiles cover the kept samples)");
            }

            if (phase.FrameWindowWrapped)
            {
                sb.AppendLine("  Note: the per-frame detail window wrapped (a rolling window by design). The");
                sb.AppendLine("        stop-reason tallies below are still EXACT for the whole phase.");
            }

            AppendLatency(sb, phase);
            AppendWaste(sb, phase);
            AppendAdmission(sb, phase);
            AppendVerdict(sb, phase);

            sb.AppendLine();
        }

        private static void AppendLatency(StringBuilder sb, PipelinePhaseMetrics phase)
        {
            sb.AppendLine();
            sb.AppendLine("  Stage latency (ms) — only chunks that reached MeshApplied contribute:");

            var table = new ReportTable("Hop", "n", "min", "p50", "p95", "p99", "max");
            AppendHopRow(table, "enqueue→populated", phase.RequestToPopulatedTicks);
            AppendHopRow(table, "populated→lit", phase.PopulatedToLitTicks);
            AppendHopRow(table, "lit→meshApplied", phase.LitToMeshAppliedTicks);
            AppendHopRow(table, "enqueue→meshApplied", phase.RequestToMeshAppliedTicks);
            table.AppendTo(sb);

            // §7.2: the histogram is what lets a reader recompute a statistic this table did not choose.
            sb.AppendLine();
            sb.AppendLine("  Raw histogram — enqueue→meshApplied (every sample bucketed, none dropped):");
            AppendHistogram(sb, phase.RequestToMeshAppliedTicks);
        }

        private static void AppendHopRow(ReportTable table, string label, List<long> ticks)
        {
            if (ticks.Count == 0)
            {
                table.AddRow(label, "0", "-", "-", "-", "-", "-");
                return;
            }

            // Sorting in place is safe: the phase is closed, and nothing reads these in original order.
            ticks.Sort();

            table.AddRow(label,
                ticks.Count.ToString("N0"),
                Ms(ticks[0]),
                Ms(TraceStatistics.Percentile(ticks, 50)),
                Ms(TraceStatistics.Percentile(ticks, 95)),
                Ms(TraceStatistics.Percentile(ticks, 99)),
                Ms(ticks[^1]));
        }

        private static void AppendHistogram(StringBuilder sb, List<long> ticks)
        {
            if (ticks.Count == 0)
            {
                sb.AppendLine("    (no completed chunks)");
                return;
            }

            List<double> ms = new List<double>(ticks.Count);
            foreach (long t in ticks) ms.Add(PipelineTelemetry.TicksToMs(t));

            int[] buckets = TraceStatistics.Histogram(ms);
            var table = new ReportTable("Bucket", "count", "% of n");
            for (int i = 0; i < buckets.Length; i++)
            {
                if (buckets[i] == 0) continue;
                table.AddRow(TraceStatistics.BucketLabel(i), buckets[i].ToString("N0"),
                    $"{100.0 * buckets[i] / ticks.Count:F1}%");
            }

            table.AppendTo(sb);
        }

        private static void AppendWaste(StringBuilder sb, PipelinePhaseMetrics phase)
        {
            sb.AppendLine();
            sb.AppendLine("  Waste accounting — absolute counts beside every percentage (§7.2):");

            int started = phase.TracesStarted;
            var table = new ReportTable("Disposition", "count", "% of traces started", "waste?");

            for (int i = 0; i < PipelineTelemetry.DispositionCount; i++)
            {
                TraceDisposition d = (TraceDisposition)i;
                int count = phase.DispositionCounts[i];
                table.AddRow(d.ToString(), count.ToString("N0"),
                    started > 0 ? $"{100.0 * count / started:F1}%" : "-",
                    IsWaste(d) ? "WASTE" : "");
            }

            // Formatted, not a "100.0%" literal: every other cell goes through :F1 and picks up the
            // running culture's decimal separator, so a literal would print a period among commas.
            table.AddRow("-- traces started --", started.ToString("N0"),
                started > 0 ? $"{100.0:F1}%" : "-", "");
            table.AppendTo(sb);
        }

        private static void AppendAdmission(StringBuilder sb, PipelinePhaseMetrics phase)
        {
            sb.AppendLine();
            sb.AppendLine("  Admission pressure — FULL stop-reason tallies, never only the winner (§7.2):");

            var table = new ReportTable("Pass", "NotRun", "OutOfWork", "Quota", "Ceiling", "InFlightCap", "AllDeclined");
            for (int pass = 0; pass < PipelineTelemetry.PassCount; pass++)
            {
                string[] cells = new string[PipelineTelemetry.StopReasonCount + 1];
                cells[0] = ((PipelinePass)pass).ToString();
                for (int reason = 0; reason < PipelineTelemetry.StopReasonCount; reason++)
                    cells[reason + 1] = phase.StopReasonCounts[pass, reason].ToString("N0");
                table.AddRow(cells);
            }

            table.AppendTo(sb);

            sb.AppendLine($"    Panic gate closed:  {phase.GateClosedFrames:N0} / {phase.FrameCount:N0} frames" +
                          (phase.FrameCount > 0 ? $" ({100.0 * phase.GateClosedFrames / phase.FrameCount:F1}%)" : ""));
        }

        private static void AppendVerdict(StringBuilder sb, PipelinePhaseMetrics phase)
        {
            int waste = 0, terminal = 0;
            for (int i = 0; i < PipelineTelemetry.DispositionCount; i++)
            {
                TraceDisposition d = (TraceDisposition)i;
                if (d == TraceDisposition.Pending) continue;

                terminal += phase.DispositionCounts[i];
                if (IsWaste(d)) waste += phase.DispositionCounts[i];
            }

            RegimeVerdict verdict = PipelineRegimeVerdict.Evaluate(phase.StopReasonCounts, waste, terminal);

            // The verdict's complete input vector, verbatim and immediately above the verdict line, so
            // disagreeing with the rule needs only this report (§7.2).
            sb.AppendLine();
            sb.AppendLine("  Verdict inputs (verbatim):");
            sb.AppendLine($"    dominant stop reason = {verdict.DominantReason}, runner-up = {verdict.RunnerUpReason}");
            sb.AppendLine($"    waste = {waste:N0} / {terminal:N0} terminal traces = {verdict.WasteFraction * 100:F1}% " +
                          $"(ordering threshold {PipelineRegimeVerdict.OrderingWasteThreshold * 100:F0}%)");
            sb.AppendLine($"    rule = {RULE_VERSION}");

            string ordering = verdict.OrderingBound ? " + ORDERING-BOUND" : "";
            sb.AppendLine($"  <b>VERDICT: {verdict.Primary}{ordering}</b>");
        }

        /// <summary>Whether a disposition represents work the pipeline completed and then threw away.</summary>
        private static bool IsWaste(TraceDisposition d)
        {
            // InFlightAtPhaseEnd is deliberately NOT waste — the capture stopped first, the pipeline did
            // nothing wrong. Rerequested is not waste either: it counts churn, and its work may still land.
            return d == TraceDisposition.DiscardedOutOfRange
                   || d == TraceDisposition.LoadStranded
                   || d == TraceDisposition.UnloadedBeforeMeshApplied;
        }

        private static string Ms(long ticks) => $"{PipelineTelemetry.TicksToMs(ticks):F1}";
    }
}
