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
            AppendPassCosts(sb, phase);
            AppendQuotaUtilisation(sb, phase);
            AppendAmplification(sb, phase);
            AppendParkedTime(sb, phase);
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

        /// <summary>
        /// Renders per-pass main-thread cost (P9-0) — the attribution no capture before this one carried.
        /// </summary>
        /// <param name="sb">The report builder.</param>
        /// <param name="phase">The phase being rendered.</param>
        /// <remarks>
        /// The zero-frame case prints NOT MEASURED rather than a table of zeros. A pass costing "0.0 ms"
        /// is a claim — that the pipeline's scheduling is free — and it is the opposite of what every
        /// capture to date suggests, so a profiler that silently failed to run must not be able to
        /// manufacture it.
        /// </remarks>
        private static void AppendPassCosts(StringBuilder sb, PipelinePhaseMetrics phase)
        {
            sb.AppendLine();
            sb.AppendLine("  Main-thread cost per pass (P9-0) — where the frame actually goes:");

            if (phase.ProfiledFrameCount == 0)
            {
                sb.AppendLine("    ⚠ NOT MEASURED — WorldFrameProfiler did not run for this phase, so no");
                sb.AppendLine("      per-pass attribution exists. This is NOT a claim that the passes were");
                sb.AppendLine("      free; treat every per-pass question as unanswered by this capture.");
                return;
            }

            double wallMs = phase.DurationSeconds * 1000.0;
            var table = new ReportTable("Pass", "total ms", "ms/frame", "ms/s", "% of wall");

            double summed = 0;
            for (int i = 0; i < WorldFrameProfiler.PhaseCount; i++)
            {
                double total = phase.PassMsTotals[i];
                summed += total;
                table.AddRow(((WorldFrameProfiler.Phase)i).ToString(),
                    total.ToString("F1"),
                    (total / phase.ProfiledFrameCount).ToString("F3"),
                    phase.DurationSeconds > 0f ? (total / phase.DurationSeconds).ToString("F1") : "-",
                    wallMs > 0 ? $"{100.0 * total / wallMs:F1}%" : "-");
            }

            table.AddRow("-- all timed regions --", summed.ToString("F1"),
                (summed / phase.ProfiledFrameCount).ToString("F3"),
                phase.DurationSeconds > 0f ? (summed / phase.DurationSeconds).ToString("F1") : "-",
                wallMs > 0 ? $"{100.0 * summed / wallMs:F1}%" : "-");
            table.AppendTo(sb);

            sb.AppendLine($"    Profiled frames:    {phase.ProfiledFrameCount:N0} / {phase.FrameCount:N0}");
            sb.AppendLine("    These regions are DISJOINT and cover only the instrumented interior of");
            sb.AppendLine("    World.Update — the remainder of the wall clock is rendering, physics, other");
            sb.AppendLine("    MonoBehaviours and any pipeline work no region brackets.");
        }

        /// <summary>
        /// Renders what each scheduling pass was granted versus what it served (P9-0). The stop-reason table
        /// above reports that a quota bound; this reports what the quota bought.
        /// </summary>
        /// <param name="sb">The report builder.</param>
        /// <param name="phase">The phase being rendered.</param>
        private static void AppendQuotaUtilisation(StringBuilder sb, PipelinePhaseMetrics phase)
        {
            sb.AppendLine();
            sb.AppendLine("  Quota utilisation (P9-0) — items served vs quota granted:");

            var table = new ReportTable("Pass", "frames", "granted", "served", "utilisation", "served/s");
            for (int pass = 0; pass < PipelineTelemetry.PassCount; pass++)
            {
                int frames = phase.PassWorkFrames[pass];
                if (frames == 0) continue;

                long granted = phase.PassQuotaGranted[pass];
                long served = phase.PassItemsServed[pass];
                table.AddRow(((PipelinePass)pass).ToString(),
                    frames.ToString("N0"),
                    granted.ToString("N0"),
                    served.ToString("N0"),
                    granted > 0 ? $"{100.0 * served / granted:F1}%" : "-",
                    phase.DurationSeconds > 0f ? (served / phase.DurationSeconds).ToString("F0") : "-");
            }

            table.AppendTo(sb);
            sb.AppendLine("    Only the two budgeted SCHEDULING passes report a granted/served pair; the");
            sb.AppendLine("    completion passes are bounded by a ms ceiling, not by a rate quota.");
            sb.AppendLine("    'frames' counts only frames where the pass had WORK AVAILABLE — idle frames are");
            sb.AppendLine("    excluded from both passes, so the two utilisations are computed over the same");
            sb.AppendLine("    kind of population and can be compared directly. Mesh frames refused by the");
            sb.AppendLine("    in-flight cap ARE included, at 0 served, since work existed and bought nothing.");
        }

        /// <summary>
        /// Renders work amplification — quota units per delivered chunk — split at first delivery (§10 q1).
        /// </summary>
        /// <param name="sb">The report builder.</param>
        /// <param name="phase">The phase being rendered.</param>
        /// <remarks>
        /// The split, not the total, is the load-bearing figure: it decides whether a deliver-then-refine
        /// lever has anything to reorder. A large pre-delivery share means correctness work is serialized
        /// ahead of visibility; a large post-delivery share means it is already happening afterwards.
        /// </remarks>
        private static void AppendAmplification(StringBuilder sb, PipelinePhaseMetrics phase)
        {
            sb.AppendLine();
            sb.AppendLine("  Work amplification (P9-0) — quota units per delivered chunk, split at first delivery:");

            int delivered = phase.DispositionCounts[(int)TraceDisposition.MeshApplied];
            if (delivered == 0)
            {
                sb.AppendLine("    (no chunk reached MeshApplied — amplification undefined for this phase)");
                return;
            }

            var table = new ReportTable("Quota unit", "pre-delivery", "per delivered chunk", "no live trace",
                "on wasted chunks", "unresolved", "total", "pass served");
            AppendAmplificationRow(table, "lighting schedules", delivered,
                phase.PreDeliveryLightSchedules, phase.UntracedLightSchedules,
                phase.WastedLightSchedules, phase.UnresolvedLightSchedules,
                phase.PassItemsServed[(int)PipelinePass.LightSchedule]);
            AppendAmplificationRow(table, "mesh schedules", delivered,
                phase.PreDeliveryMeshSchedules, phase.UntracedMeshSchedules,
                phase.WastedMeshSchedules, phase.UnresolvedMeshSchedules,
                phase.PassItemsServed[(int)PipelinePass.MeshSchedule]);
            table.AppendTo(sb);

            sb.AppendLine($"    Delivered chunks (MeshApplied): {delivered:N0}");
            sb.AppendLine("    'unresolved' is work on traces that ended without a verdict — superseded by a");
            sb.AppendLine("    re-request, still in flight when the phase ended, or never admitted. Neither");
            sb.AppendLine("    delivered nor wasted, but real quota spent.");

            // The four buckets partition every schedule stamped, so they must reconcile with the count the
            // quota table reports independently. A mismatch means one of the two paths lost events, and
            // neither number carries any other symptom — so it is checked here rather than trusted.
            AppendAmplificationReconciliation(sb, "lighting", phase.PreDeliveryLightSchedules,
                phase.UntracedLightSchedules, phase.WastedLightSchedules, phase.UnresolvedLightSchedules,
                phase.PassItemsServed[(int)PipelinePass.LightSchedule]);
            AppendAmplificationReconciliation(sb, "mesh", phase.PreDeliveryMeshSchedules,
                phase.UntracedMeshSchedules, phase.WastedMeshSchedules, phase.UnresolvedMeshSchedules,
                phase.PassItemsServed[(int)PipelinePass.MeshSchedule]);
            sb.AppendLine("    'no live trace' is dominated by POST-DELIVERY corrections (a trace closes at");
            sb.AppendLine("    MeshApplied), but also absorbs schedules for never-traced or already-unloaded");
            sb.AppendLine("    chunks — so it is an UPPER BOUND on correction work. Read it beside the");
            sb.AppendLine("    saturation banner: an unsaturated phase makes the bound tight.");
        }

        /// <summary>Adds one amplification row, with the bucket total and the pass's independently-counted total.</summary>
        /// <param name="table">Destination table.</param>
        /// <param name="label">Row label.</param>
        /// <param name="delivered">Chunks that reached <c>MeshApplied</c> (the per-chunk divisor).</param>
        /// <param name="preDelivery">Units spent before first delivery.</param>
        /// <param name="untraced">Units with no live trace.</param>
        /// <param name="wasted">Units on chunks whose traces ended in waste.</param>
        /// <param name="unresolved">Units on traces that ended without a verdict.</param>
        /// <param name="passServed">Units the pass reported serving, counted independently per frame.</param>
        private static void AppendAmplificationRow(ReportTable table, string label, int delivered,
            long preDelivery, long untraced, long wasted, long unresolved, long passServed)
        {
            table.AddRow(label,
                preDelivery.ToString("N0"),
                ((double)preDelivery / delivered).ToString("F2"),
                untraced.ToString("N0"),
                wasted.ToString("N0"),
                unresolved.ToString("N0"),
                (preDelivery + untraced + wasted + unresolved).ToString("N0"),
                passServed.ToString("N0"));
        }

        /// <summary>
        /// Checks the four amplification buckets against the pass's own served count and reports any gap.
        /// </summary>
        /// <param name="sb">The report builder.</param>
        /// <param name="label">Which pass is being reconciled.</param>
        /// <param name="preDelivery">Units spent before first delivery.</param>
        /// <param name="untraced">Units with no live trace.</param>
        /// <param name="wasted">Units on chunks whose traces ended in waste.</param>
        /// <param name="unresolved">Units on traces that ended without a verdict.</param>
        /// <param name="passServed">Units the pass reported serving.</param>
        /// <remarks>
        /// Two independent paths count the same events — one per scheduled item, one per frame — so they
        /// must agree. Silence here is the evidence that the amplification split covers everything the
        /// quota bought; a gap means one path dropped events and would otherwise leave no trace at all,
        /// since a smaller-than-true numerator still prints as a plausible ratio.
        /// </remarks>
        private static void AppendAmplificationReconciliation(StringBuilder sb, string label,
            long preDelivery, long untraced, long wasted, long unresolved, long passServed)
        {
            long bucketed = preDelivery + untraced + wasted + unresolved;
            if (bucketed == passServed) return;

            sb.AppendLine($"    ⚠ RECONCILIATION GAP ({label}) — buckets total {bucketed:N0} but the pass");
            sb.AppendLine($"      reported serving {passServed:N0} ({bucketed - passServed:+#;-#;0}). The two are");
            sb.AppendLine("      counted independently (per item vs per frame) and must agree, so one path");
            sb.AppendLine("      lost events. Treat the amplification split as incomplete until explained.");
        }

        /// <summary>
        /// Renders per-chunk parked time (§10 q4) — latency spent ineligible rather than un-served.
        /// </summary>
        /// <param name="sb">The report builder.</param>
        /// <param name="phase">The phase being rendered.</param>
        /// <remarks>
        /// The stop-reason instrument cannot see this class at all: MT-2 moves a blocked chunk out of the
        /// ready set, so it is never walked and never counted as an <c>AllDeclined</c> candidate. A phase
        /// with an idle lighting pass and a multi-second populated→lit hop is the signature.
        /// </remarks>
        private static void AppendParkedTime(StringBuilder sb, PipelinePhaseMetrics phase)
        {
            sb.AppendLine();
            sb.AppendLine("  Parked time per delivered chunk (P9-0, §10 q4) — time flagged but INELIGIBLE:");

            var table = new ReportTable("Measure", "n", "min", "p50", "p95", "p99", "max");
            AppendHopRow(table, "parked (lighting waiting set)", phase.ParkedTicksSamples);
            table.AppendTo(sb);

            sb.AppendLine("    Compare against populated→lit above: parking is neither a throughput ceiling,");
            sb.AppendLine("    an admission stall nor a budget, so any part of that hop it explains is");
            sb.AppendLine("    unreachable by quota or gate work.");
            sb.AppendLine("    ⚠ This is a LOWER BOUND on ineligibility, biased against the longest waiters:");
            sb.AppendLine("      · only chunks that reached MeshApplied contribute, so a chunk that waited and");
            sb.AppendLine("        was then unloaded is absent from every percentile above;");
            sb.AppendLine("      · the ~1 Hz fail-safe promotes the whole parked set at once, and a chunk the");
            sb.AppendLine("        scan does not reach before breaking sits in the READY set accruing nothing —");
            sb.AppendLine("        that time shows up as ReadyCount and a Quota/Ceiling stop, not as parked.");
            sb.AppendLine("    A wait spanning a phase boundary IS counted (the interval is keyed by chunk, not");
            sb.AppendLine("    by trace), and is credited in full to the phase it ends in.");
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
