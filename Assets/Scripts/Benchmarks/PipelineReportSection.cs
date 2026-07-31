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
        // Bumped from v1 by FP-7: the plurality is now capability-weighted (§7.1.1's defect), and the waste
        // axis no longer counts requests that were never admitted. A v1 report and a v2 report are NOT
        // comparable on either axis — which is what this string exists to make impossible to miss.
        private const string RULE_VERSION = "§7.1 v2 (participation-weighted plurality over passes able to "
                                            + "emit each reason; ordering axis at waste ≥ 20% of admitted "
                                            + "terminal traces, min 30)";

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

            AppendIntegrityWarnings(sb, phase);

            AppendLatency(sb, phase);
            AppendWaste(sb, phase);
            AppendAdmission(sb, phase);
            AppendVerdict(sb, phase);

            sb.AppendLine();
        }

        /// <summary>
        /// Renders the two ways this phase's tallies can be untrustworthy as <i>inputs to the §7.1 v2
        /// verdict</i>, as opposed to merely truncated (which the saturation banner above covers).
        /// </summary>
        /// <param name="sb">The report builder.</param>
        /// <param name="phase">The phase being rendered.</param>
        /// <remarks>
        /// Deliberately checked <b>here</b> and not only by the development-build asserts in
        /// <see cref="PipelineTelemetry.RecordPassStop"/>: those are compiled out of a Release player, which
        /// is the build a capture should be taken in (the P-4 budgets are frame-time-proportional, so a
        /// Development Build measures a different admission regime). A warning that reaches the console of a
        /// build nobody captures with is not a guard. Both conditions are derived from data the report
        /// already carries, so this costs nothing and cannot itself go stale.
        /// </remarks>
        private static void AppendIntegrityWarnings(StringBuilder sb, PipelinePhaseMetrics phase)
        {
            // A tally in a cell the capability matrix says is impossible means the matrix is stale, and every
            // share weighted by it is wrong (§7.1.1's defect, which is what FP-7b turned out to be).
            for (int pass = 0; pass < PipelineTelemetry.PassCount; pass++)
            {
                for (int reason = 0; reason < PipelineTelemetry.StopReasonCount; reason++)
                {
                    int count = phase.StopReasonCounts[pass, reason];
                    if (count == 0 || PipelineRegimeVerdict.CanEmit((PipelinePass)pass, (PassStopReason)reason))
                        continue;

                    sb.AppendLine();
                    sb.AppendLine($"  ⚠ CAPABILITY MATRIX STALE — {(PipelinePass)pass} recorded " +
                                  $"{(PassStopReason)reason} {count:N0}×, which PipelineRegimeVerdict.CanEmit");
                    sb.AppendLine("     says it cannot emit. Those frames are EXCLUDED from the verdict below,");
                    sb.AppendLine("     so the regime is computed from an incomplete picture. Fix CanEmit and");
                    sb.AppendLine("     re-derive from the raw tallies before trusting the verdict.");
                }
            }

            if (!phase.AnyPassDoubleRecorded) return;

            sb.AppendLine();
            sb.AppendLine("  ⚠ DOUBLE-RECORDED PASS — a pass reported a stop reason more than once in a frame:");
            for (int pass = 0; pass < PipelineTelemetry.PassCount; pass++)
            {
                if (phase.PassDoubleRecorded[pass])
                    sb.AppendLine($"     · {(PipelinePass)pass}");
            }

            sb.AppendLine("     The §7.1 v2 denominator assumes one report per pass per frame, so those passes");
            sb.AppendLine("     vote with inflated weight. Shares stay ≤ 1 either way — this is the ONLY");
            sb.AppendLine("     symptom, so the verdict below cannot be trusted without re-deriving by hand.");
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
                    PipelineRegimeVerdict.IsWaste(d) ? "WASTE" : "");
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

            // The v2 weighting is recomputable from the table above only if the reader knows which cells
            // count toward which reason — so name the eligible passes per contested reason (§7.2).
            sb.AppendLine("    Verdict weighting (§7.1 v2) — a reason is scored only over passes able to emit it:");
            for (int reason = (int)PassStopReason.OutOfWork; reason < PipelineTelemetry.StopReasonCount; reason++)
            {
                string eligible = "";
                for (int pass = 0; pass < PipelineTelemetry.PassCount; pass++)
                {
                    if (!PipelineRegimeVerdict.CanEmit((PipelinePass)pass, (PassStopReason)reason)) continue;
                    if (eligible.Length > 0) eligible += ", ";
                    eligible += ((PipelinePass)pass).ToString();
                }

                sb.AppendLine($"      {(PassStopReason)reason,-12} <- {eligible}");
            }

            sb.AppendLine($"    Panic gate closed:  {phase.GateClosedFrames:N0} / {phase.FrameCount:N0} frames" +
                          (phase.FrameCount > 0 ? $" ({100.0 * phase.GateClosedFrames / phase.FrameCount:F1}%)" : ""));
        }

        private static void AppendVerdict(StringBuilder sb, PipelinePhaseMetrics phase)
        {
            int waste = 0, terminal = 0;
            for (int i = 0; i < PipelineTelemetry.DispositionCount; i++)
            {
                TraceDisposition d = (TraceDisposition)i;
                if (!PipelineRegimeVerdict.IsInWasteDenominator(d)) continue;

                terminal += phase.DispositionCounts[i];
                if (PipelineRegimeVerdict.IsWaste(d)) waste += phase.DispositionCounts[i];
            }

            int abandoned = phase.DispositionCounts[(int)TraceDisposition.AbandonedBeforeAdmission];

            RegimeVerdict verdict = PipelineRegimeVerdict.Evaluate(phase.StopReasonCounts, waste, terminal);

            // The verdict's complete input vector, verbatim and immediately above the verdict line, so
            // disagreeing with the rule needs only this report (§7.2).
            sb.AppendLine();
            sb.AppendLine("  Verdict inputs (verbatim):");
            sb.AppendLine($"    dominant stop reason = {verdict.DominantReason} " +
                          $"({verdict.DominantShare * 100:F1}% of eligible pass-frames), " +
                          $"runner-up = {verdict.RunnerUpReason} ({verdict.RunnerUpShare * 100:F1}%)");
            sb.AppendLine($"    waste = {waste:N0} / {terminal:N0} terminal traces = {verdict.WasteFraction * 100:F1}% " +
                          $"(ordering threshold {PipelineRegimeVerdict.OrderingWasteThreshold * 100:F0}%, " +
                          $"min {PipelineRegimeVerdict.MinOrderingTerminalTraces:N0} traces to decide)");

            // §7.2: the denominator EXCLUDES a population, so state which and how large — otherwise the
            // fraction above cannot be recomputed from the disposition table beside it.
            sb.AppendLine($"    denominator excludes {abandoned:N0} AbandonedBeforeAdmission " +
                          "(requested then unloaded before admission — no stage ran, so not pipeline work)");
            sb.AppendLine($"    eligible observations = {verdict.EligibleObservations:N0} " +
                          $"(min {PipelineRegimeVerdict.MinRegimeObservations:N0} to decide a regime)");
            sb.AppendLine($"    rule = {RULE_VERSION}");

            // "Not ordering-bound" and "could not tell" are different claims and must never render alike —
            // the second is the one a reader would otherwise mistake for a clean bill of health.
            string ordering = verdict.OrderingDecidable
                ? verdict.OrderingBound ? " + ORDERING-BOUND" : ""
                : " + ordering axis UNDECIDABLE (too few terminal traces)";

            // A phase that is not a measurement gets NEITHER axis. The ordering axis would otherwise make the
            // same category error the primary regime just stopped making: a drain-and-unload that happens to
            // close enough traces would print ORDERING-BOUND for deliberately discarding work, which is its
            // whole job. FP-8's transitions had zero traces so this never surfaced, but UnloadChunks does
            // stamp dispositions, so it is reachable rather than theoretical.
            if (!phase.RegimeBearing)
            {
                sb.AppendLine("  <b>VERDICT: NO REGIME (not a measurement phase — this phase drains and " +
                              "unloads by design, so neither axis describes the pipeline)</b>");
                return;
            }

            // Three ways the primary regime can be absent, all of which a reader must be able to tell apart
            // (FP-9a). Only the third is a statement about the pipeline.
            string primary;
            if (!verdict.PrimaryDecidable)
            {
                primary = verdict.EligibleObservations == 0
                    ? "NO DATA (no pass reported a stop reason)"
                    : $"UNDECIDABLE (only {verdict.EligibleObservations:N0} eligible observations, " +
                      $"need {PipelineRegimeVerdict.MinRegimeObservations:N0})";
            }
            else
            {
                primary = verdict.Primary.ToString();
            }

            sb.AppendLine($"  <b>VERDICT: {primary}{ordering}</b>");
        }

        private static string Ms(long ticks) => $"{PipelineTelemetry.TicksToMs(ticks):F1}";
    }
}
