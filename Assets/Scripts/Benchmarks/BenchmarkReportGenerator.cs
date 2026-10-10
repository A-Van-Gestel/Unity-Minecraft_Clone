using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Diagnostics;
using UnityEngine;

namespace Benchmarks
{
    /// <summary>
    /// Contains the output of a benchmark report generation: the formatted report text
    /// and the file path where the plain-text version was saved.
    /// </summary>
    public struct BenchmarkReportResult
    {
        /// <summary>The full report with Unity rich-text tags (suitable for TMP display).</summary>
        public string ReportRichText;

        /// <summary>The absolute file path of the saved plain-text report, or null if writing failed.</summary>
        public string LogFilePath;
    }

    /// <summary>
    /// Generates a structured performance report from benchmark metrics and writes it to disk.
    /// Reuses <see cref="BenchmarkEnvironment.DescribeSystem"/> for the system/build/Burst header
    /// and <see cref="BenchmarkEnvironment.WriteReportToDisk"/> for file output.
    /// </summary>
    public static class BenchmarkReportGenerator
    {
        /// <summary>
        /// Generates the full benchmark report, logs it to the console, writes a plain-text
        /// copy to disk, and returns both the rich-text report and the saved file path.
        /// </summary>
        /// <param name="collector">The completed metrics collector containing per-phase results.</param>
        /// <param name="generationSpeeds">The generation speed phases used (m/s).</param>
        /// <param name="loadingSpeeds">The loading speed phases used (m/s).</param>
        /// <param name="timePerPhase">Duration of each timed speed phase in seconds.</param>
        /// <param name="regionSize">The actual region size used (in chunks), after auto-scaling.</param>
        /// <param name="configuredRegionSize">The user-configured region size (in chunks), before auto-scaling.</param>
        /// <param name="generationWaypointCount">Number of generation waypoints built.</param>
        /// <param name="loadingWaypointCount">Number of loading waypoints built.</param>
        /// <param name="totalDuration">Wall-clock duration of the entire benchmark run.</param>
        /// <param name="savedVSyncCount">The VSync count that was saved before forcing it off.</param>
        /// <param name="savedTargetFrameRate">The target frame rate that was saved before uncapping.</param>
        /// <param name="pipelineSettings">Pipeline tuning captured at run start (FP-6) — the values the
        /// FP stop-reason tallies must be read against.</param>
        /// <param name="settingsDifference">The <see cref="SettingsDifferenceReport"/> block captured at run start.</param>
        /// <returns>A <see cref="BenchmarkReportResult"/> containing the report text and file path.</returns>
        public static BenchmarkReportResult GenerateAndWriteReport(
            BenchmarkMetricsCollector collector,
            float[] generationSpeeds,
            float[] loadingSpeeds,
            float timePerPhase,
            BenchmarkRouteGeometry routeGeometry,
            int generationWaypointCount,
            int loadingWaypointCount,
            TimeSpan totalDuration,
            int savedVSyncCount,
            int savedTargetFrameRate,
            PipelineSettingsSnapshot pipelineSettings,
            string settingsDifference)
        {
            StringBuilder sb = new StringBuilder(4096);

            AppendHeader(sb, totalDuration);
            sb.Append(BenchmarkEnvironment.DescribeSystem());
            sb.Append(settingsDifference);
            AppendConfiguration(sb, generationSpeeds, loadingSpeeds, timePerPhase, routeGeometry,
                generationWaypointCount, loadingWaypointCount, savedVSyncCount, savedTargetFrameRate);
            pipelineSettings.AppendTo(sb);
            AppendMonitorConfiguration(sb);
            AppendOverallSummary(sb, collector.CompletedPhases, totalDuration);
            AppendGroupedPhases(sb, collector.CompletedPhases);

            // FP-3: the pipeline-internal section, reported ALONGSIDE frame health rather than replacing it
            // (§1 non-goals). No-op when the capture ran with telemetry disabled.
            PipelineReportSection.Append(sb, PipelineTelemetry.CompletedPhases);

            string report = sb.ToString();
            Debug.Log(report);
            string filePath = BenchmarkEnvironment.WriteReportToDisk(report, "BenchmarkRun");

            return new BenchmarkReportResult
            {
                ReportRichText = report,
                LogFilePath = filePath,
            };
        }

        // ── Report Sections ──────────────────────────────────────────────

        private static void AppendHeader(StringBuilder sb, TimeSpan totalDuration)
        {
            sb.AppendLine("<b>--- BENCHMARK RUN PERFORMANCE REPORT ---</b>");
            sb.AppendLine($"Date:                {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Total runtime:       {BenchmarkEnvironment.FormatDuration(totalDuration)}");
            sb.AppendLine();
        }

        private static void AppendConfiguration(
            StringBuilder sb,
            float[] generationSpeeds,
            float[] loadingSpeeds,
            float timePerPhase,
            BenchmarkRouteGeometry routeGeometry,
            int generationWaypointCount,
            int loadingWaypointCount,
            int savedVSyncCount,
            int savedTargetFrameRate)
        {
            sb.AppendLine("<b>=== Configuration ===</b>");

            sb.AppendLine($"Phase duration:      {timePerPhase:F0} s");
            sb.AppendLine($"Generation speeds:   {string.Join("; ", generationSpeeds)} m/s");
            sb.AppendLine($"Loading speeds:      {string.Join("; ", loadingSpeeds)} m/s");
            sb.AppendLine($"Generation WPs:      {generationWaypointCount}");
            sb.AppendLine($"Loading WPs:         {loadingWaypointCount}");
            sb.AppendLine();

            // FP-9b: the route is DERIVED from the speeds and phase duration, so these are outputs, not
            // settings. Printed because two captures whose routes differ are not comparable, and before FP-9b
            // nothing in the report said so — the generation sweep had silently collapsed from 12 waypoints
            // to 4 across the FP-8 view-distance sweep.
            sb.AppendLine("  Route (derived — not configurable):");
            sb.AppendLine($"    Region:            {routeGeometry.RegionChunks} chunks (derived)");
            sb.AppendLine($"    Sweep rows:        {routeGeometry.Rows}  (row stride {routeGeometry.RowStrideChunks} chunks = 2 x LoadDistance)");
            sb.AppendLine($"    Route length:      {routeGeometry.RouteLengthMeters:N0} m");
            sb.AppendLine($"    Timed travel:      {routeGeometry.TimedTravelMeters:N0} m  (what the speed phases consume)");
            sb.AppendLine($"    Loading tour:      {routeGeometry.TourChunks} chunks square" +
                          (routeGeometry.TourWasShrunk
                              ? $"  ** SHRUNK from {BenchmarkRouteGeometry.LoadingTourChunks} — the timed phases do not cover it, so the loading pass GENERATED terrain. Capture not comparable. **"
                              : "  (fixed — independent of view distance)"));

            // FP-11c: both derived, and both needed to read the coverage line below — a sweep that is slower
            // or shorter than the reader assumes explains a coverage shortfall that would otherwise look like
            // a pipeline result.
            sb.AppendLine($"    Ensure sweep:      {BenchmarkRouteGeometry.EnsureGeneratedSpeed:F0} m/s over " +
                          $"{routeGeometry.TourLengthMeters:N0} m = {routeGeometry.EnsureGeneratedSeconds:F1} s " +
                          "(closed circuit, incl. the return leg the loading pass flies)");
            AppendTourCoverage(sb);

            sb.AppendLine($"VSync override:      Forced Off (was: {(savedVSyncCount > 0 ? "On" : "Off")})");
            sb.AppendLine($"FPS cap override:    Uncapped (was: {(savedTargetFrameRate > 0 ? savedTargetFrameRate.ToString() : "Uncapped")})");
            sb.AppendLine();
        }

        /// <summary>
        /// Renders the measured loading-tour coverage (FP-11a) — the lines that say whether the loading pass
        /// measured loading or partly re-measured generation.
        /// </summary>
        /// <param name="sb">The report builder.</param>
        /// <remarks>
        /// Read from the static rather than passed in, exactly as the FP pipeline section reads
        /// <see cref="PipelineTelemetry.CompletedPhases"/>. An unmeasurable result prints as NOT MEASURED: a
        /// missing footprint must never render as a clean 100 %.
        /// <para>Both instants are printed. The gap between them is how much of the tour the panic gate
        /// deferred out of the ensure sweep and the transition drain then finished — a P-8 signal in its own
        /// right, and invisible from either figure alone.</para>
        /// </remarks>
        private static void AppendTourCoverage(StringBuilder sb)
        {
            if (!BenchmarkTourCoverage.HasMeasurement)
            {
                sb.AppendLine("    Tour coverage:     ** NOT MEASURED — the loading pass's numbers cannot be " +
                              "attributed to loading. **");
                return;
            }

            sb.AppendLine($"    Tour coverage:     {BenchmarkTourCoverage.EnsurePassCoveredChunks:N0} / " +
                          $"{BenchmarkTourCoverage.RequiredChunks:N0} chunks after the ensure sweep " +
                          $"({BenchmarkTourCoverage.EnsurePassCoverageFraction * 100f:F1} %)");
            sb.AppendLine($"                       {BenchmarkTourCoverage.CoveredChunks:N0} / " +
                          $"{BenchmarkTourCoverage.RequiredChunks:N0} on disk when the loading pass starts " +
                          $"({BenchmarkTourCoverage.CoverageFraction * 100f:F1} %)" +
                          (BenchmarkTourCoverage.IsSufficient
                              ? ""
                              : "  ** the loading pass GENERATED the remainder. **"));
        }

        /// <summary>
        /// The performance monitor's settings for the run: its tier decides which per-frame columns exist, and the hitch
        /// thresholds — as the detector applies them, invalid settings replaced by the defaults — decide what the Hitches
        /// column counts, so two reports compare only when these match.
        /// </summary>
        /// <param name="sb">The report builder.</param>
        private static void AppendMonitorConfiguration(StringBuilder sb)
        {
            sb.AppendLine("<b>=== Performance Monitor ===</b>");
            sb.AppendLine($"Monitor detail:      {PerfStore.Tier} (benchmarks run at Frame or above)");
            sb.AppendLine($"Hitch threshold:     over {PerfHitchDetector.ResolveMinMs(PerfStore.HitchMinMs):0.##} ms or " +
                          $"{PerfHitchDetector.ResolveMedianFactor(PerfStore.HitchMedianFactor):0.##} x the " +
                          $"median of the last {PerfHitchDetector.BaselineFrames} frames, whichever is higher");
            sb.AppendLine();
        }

        private static void AppendOverallSummary(StringBuilder sb, IReadOnlyList<PhaseMetrics> phases, TimeSpan totalDuration)
        {
            sb.AppendLine("<b>=== Overall Summary ===</b>");

            if (phases.Count == 0)
            {
                sb.AppendLine("  No phases recorded.");
                sb.AppendLine();
                return;
            }

            int totalSamples = 0;
            double totalDurationSeconds = 0;
            double weightedCpuSum = 0;
            double weightedWallSum = 0;
            double weightedGcSum = 0;
            double weightedWallFpsSum = 0;
            double weightedCpuFpsSum = 0;
            double weightedTotalMemSum = 0;
            double overallPeakCpu = 0;
            double overallMinWallFps = double.MaxValue;
            double overallMinCpuFps = double.MaxValue;
            double overallPeakTotalMem = 0;

            foreach (PhaseMetrics phase in phases)
            {
                totalSamples += phase.SampleCount;
                totalDurationSeconds += phase.DurationSeconds;
                weightedCpuSum += phase.AvgCpuTimeMs * phase.SampleCount;
                weightedWallSum += phase.AvgWallTimeMs * phase.SampleCount;
                weightedGcSum += phase.AvgGcAllocKb * phase.SampleCount;
                weightedWallFpsSum += phase.AvgWallFps * phase.SampleCount;
                weightedCpuFpsSum += phase.AvgCpuFps * phase.SampleCount;
                weightedTotalMemSum += phase.AvgTotalMemMb * phase.SampleCount;

                overallPeakCpu = Math.Max(overallPeakCpu, phase.PeakCpuTimeMs);
                overallMinWallFps = Math.Min(overallMinWallFps, phase.MinWallFps);
                overallMinCpuFps = Math.Min(overallMinCpuFps, phase.MinCpuFps);
                overallPeakTotalMem = Math.Max(overallPeakTotalMem, phase.PeakTotalMemMb);
            }

            double avgCpu = totalSamples > 0 ? weightedCpuSum / totalSamples : 0;
            double avgWall = totalSamples > 0 ? weightedWallSum / totalSamples : 0;
            double avgGc = totalSamples > 0 ? weightedGcSum / totalSamples : 0;
            double avgWallFps = totalSamples > 0 ? weightedWallFpsSum / totalSamples : 0;
            double avgCpuFps = totalSamples > 0 ? weightedCpuFpsSum / totalSamples : 0;
            double avgTotalMem = totalSamples > 0 ? weightedTotalMemSum / totalSamples : 0;

            if (overallMinWallFps >= double.MaxValue) overallMinWallFps = 0;
            if (overallMinCpuFps >= double.MaxValue) overallMinCpuFps = 0;

            sb.AppendLine($"Total phases:        {phases.Count}");
            sb.AppendLine($"Total samples:       {totalSamples:N0}");
            sb.AppendLine($"Wall-clock runtime:  {BenchmarkEnvironment.FormatDuration(totalDuration)}");
            sb.AppendLine($"Phase duration sum:  {BenchmarkEnvironment.FormatDuration(TimeSpan.FromSeconds(totalDurationSeconds))}");
            sb.AppendLine($"Avg CPU time:        {avgCpu:F1} ms");
            sb.AppendLine($"Peak CPU time:       {overallPeakCpu:F1} ms");
            sb.AppendLine($"Avg Wall time:       {avgWall:F1} ms");
            sb.AppendLine($"Avg GC alloc:        {avgGc:F1} KB");
            sb.AppendLine($"Avg Wall FPS:        {avgWallFps:F1}");
            sb.AppendLine($"Min Wall FPS:        {overallMinWallFps:F1}");
            sb.AppendLine($"Avg CPU FPS:         {avgCpuFps:F1}");
            sb.AppendLine($"Min CPU FPS:         {overallMinCpuFps:F1}");
            sb.AppendLine($"Avg Total Memory:    {avgTotalMem:F1} MB");
            sb.AppendLine($"Peak Total Memory:   {overallPeakTotalMem:F1} MB");
            AppendOverallFrameHealth(sb, phases);
            sb.AppendLine();
        }

        /// <summary>The raw worst frame, hitch frames and collections across every phase with per-frame statistics.</summary>
        /// <param name="sb">The report builder.</param>
        /// <param name="phases">The completed phases.</param>
        private static void AppendOverallFrameHealth(StringBuilder sb, IReadOnlyList<PhaseMetrics> phases)
        {
            float worstWallMs = 0f;
            string worstPhase = null;
            int frames = 0;
            int collections = 0;
            int hitchFrames = 0;
            bool hitchesMeasured = true;

            foreach (PhaseMetrics phase in phases)
            {
                if (!phase.HasFrameStats) continue;

                frames += phase.FrameStats.FrameCount;
                collections += phase.FrameStats.GcCollections;
                if (phase.HitchFrames < 0) hitchesMeasured = false;
                else hitchFrames += phase.HitchFrames;

                if (phase.FrameStats.Wall.Count == 0 || phase.FrameStats.Wall.Max <= worstWallMs) continue;

                worstWallMs = phase.FrameStats.Wall.Max;
                worstPhase = $"{phase.GroupName} {phase.PhaseName}";
            }

            if (frames == 0)
            {
                sb.AppendLine("Per-frame stats:     not recorded");
                return;
            }

            sb.AppendLine($"Frames recorded:     {frames:N0} (every frame, unsmoothed)");
            sb.AppendLine($"Worst frame (wall):  {worstWallMs:F1} ms in {worstPhase}");
            sb.AppendLine($"Hitch frames:        {(hitchesMeasured ? hitchFrames.ToString("N0") : "n/a (the hitch detector was reset)")}");
            sb.AppendLine($"GC collections:      {collections:N0}");
        }

        private static void AppendGroupedPhases(StringBuilder sb, IReadOnlyList<PhaseMetrics> phases)
        {
            var groups = new List<List<PhaseMetrics>>();
            List<PhaseMetrics> currentGroup = null;
            string currentGroupName = null;

            foreach (PhaseMetrics phase in phases)
            {
                if (phase.GroupName != currentGroupName)
                {
                    currentGroupName = phase.GroupName;
                    currentGroup = new List<PhaseMetrics>();
                    groups.Add(currentGroup);
                }

                currentGroup.Add(phase);
            }

            foreach (var group in groups)
            {
                string groupName = group[0].GroupName;

                // Accumulate group-level totals for summary rows
                double groupDuration = 0;
                int groupSamples = 0;
                double groupWeightedCpu = 0;
                double groupPeakCpu = 0;
                double groupWeightedWallFps = 0;
                double groupMinWallFps = double.MaxValue;

                foreach (PhaseMetrics phase in group)
                {
                    groupDuration += phase.DurationSeconds;
                    groupSamples += phase.SampleCount;
                    groupWeightedCpu += phase.AvgCpuTimeMs * phase.SampleCount;
                    groupPeakCpu = Math.Max(groupPeakCpu, phase.PeakCpuTimeMs);
                    groupWeightedWallFps += phase.AvgWallFps * phase.SampleCount;
                    groupMinWallFps = Math.Min(groupMinWallFps, phase.MinWallFps);
                }

                bool hasTotal = group.Count > 1;
                double avgCpu = groupSamples > 0 ? groupWeightedCpu / groupSamples : 0;
                double avgWallFps = groupSamples > 0 ? groupWeightedWallFps / groupSamples : 0;
                if (groupMinWallFps >= double.MaxValue) groupMinWallFps = 0;

                // --- Performance Section ---
                sb.AppendLine($"<b>=== {groupName} — Performance ===</b>");
                var perfTable = new ReportTable("Phase", "Duration", "Avg CPU", "Peak CPU", "Avg Wall", "Peak Wall");
                foreach (PhaseMetrics phase in group)
                {
                    perfTable.AddRow(
                        phase.PhaseName,
                        BenchmarkEnvironment.FormatDuration(TimeSpan.FromSeconds(phase.DurationSeconds)),
                        $"{phase.AvgCpuTimeMs:F1} ms",
                        $"{phase.PeakCpuTimeMs:F1} ms",
                        $"{phase.AvgWallTimeMs:F1} ms",
                        $"{phase.PeakWallTimeMs:F1} ms");
                }

                if (hasTotal)
                {
                    perfTable.AddRow(
                        "-- Group Total --",
                        BenchmarkEnvironment.FormatDuration(TimeSpan.FromSeconds(groupDuration)),
                        $"{avgCpu:F1} ms",
                        $"{groupPeakCpu:F1} ms");
                }

                perfTable.AppendTo(sb);
                sb.AppendLine();

                AppendFrameHealth(sb, groupName, group);

                // --- FPS Section ---
                sb.AppendLine($"<b>=== {groupName} — FPS ===</b>");
                var fpsTable = new ReportTable("Phase", "Avg Wall FPS", "Min Wall FPS", "Avg CPU FPS", "Min CPU FPS");
                foreach (PhaseMetrics phase in group)
                {
                    fpsTable.AddRow(
                        phase.PhaseName,
                        $"{phase.AvgWallFps:F1}",
                        $"{phase.MinWallFps:F1}",
                        $"{phase.AvgCpuFps:F1}",
                        $"{phase.MinCpuFps:F1}");
                }

                if (hasTotal)
                {
                    fpsTable.AddRow(
                        "-- Group Total --",
                        $"{avgWallFps:F1}",
                        $"{groupMinWallFps:F1}");
                }

                fpsTable.AppendTo(sb);
                sb.AppendLine();

                // --- Memory Section ---
                sb.AppendLine($"<b>=== {groupName} — Memory ===</b>");
                var memTable = new ReportTable("Phase", "Avg Total", "Peak Total", "Avg Native", "Peak Native",
                    "Avg Rsvd", "Peak Rsvd", "Avg Managed", "Peak Managed");
                foreach (PhaseMetrics phase in group)
                {
                    memTable.AddRow(
                        phase.PhaseName,
                        $"{phase.AvgTotalMemMb:F1} MB",
                        $"{phase.PeakTotalMemMb:F1} MB",
                        $"{phase.AvgNativeAllocMb:F1} MB",
                        $"{phase.PeakNativeAllocMb:F1} MB",
                        $"{phase.AvgNativeReservedMb:F1} MB",
                        $"{phase.PeakNativeReservedMb:F1} MB",
                        $"{phase.AvgManagedMemMb:F1} MB",
                        $"{phase.PeakManagedMemMb:F1} MB");
                }

                memTable.AppendTo(sb);
                sb.AppendLine();

                // --- GC Allocations Section ---
                sb.AppendLine($"<b>=== {groupName} — GC Allocations ===</b>");
                var gcTable = new ReportTable("Phase", "Avg GC/frame", "Peak GC/frame");
                foreach (PhaseMetrics phase in group)
                {
                    gcTable.AddRow(
                        phase.PhaseName,
                        $"{phase.AvgGcAllocKb:F1} KB",
                        $"{phase.PeakGcAllocKb:F1} KB");
                }

                gcTable.AppendTo(sb);
                sb.AppendLine();
            }
        }

        /// <summary>
        /// One group's exact per-frame statistics: every frame of each phase, unlike the averaged Peak columns. GC is the
        /// heap growth of frames without a collection; GPU is over the frames whose timing arrived.
        /// </summary>
        /// <param name="sb">The report builder.</param>
        /// <param name="groupName">The group's name.</param>
        /// <param name="group">The group's phases.</param>
        private static void AppendFrameHealth(StringBuilder sb, string groupName, List<PhaseMetrics> group)
        {
            sb.AppendLine($"<b>=== {groupName} — Frame Health (every frame) ===</b>");
            // The frame range is printed culture-invariant: it is the key a session file's rows are selected by.
            var table = new ReportTable("Phase", "Frames", "Wall p50", "Wall p99", "Worst", "CPU p50", "CPU p99",
                "GC p99", "GCs", "Hitches", "GPU p99", "First frame", "End frame");
            foreach (PhaseMetrics phase in group)
            {
                if (!phase.HasFrameStats)
                {
                    table.AddRow(phase.PhaseName, "not recorded");
                    continue;
                }

                PerfPhaseSummary stats = phase.FrameStats;
                table.AddRow(
                    phase.PhaseName,
                    $"{stats.FrameCount:N0}",
                    $"{stats.Wall.P50:F1} ms",
                    $"{stats.Wall.P99:F1} ms",
                    $"{stats.Wall.Max:F1} ms",
                    $"{stats.Cpu.P50:F1} ms",
                    $"{stats.Cpu.P99:F1} ms",
                    stats.GcAllocKb.Count > 0 ? $"{stats.GcAllocKb.P99:F1} KB" : "n/a",
                    $"{stats.GcCollections:N0}",
                    phase.HitchFrames >= 0 ? $"{phase.HitchFrames:N0}" : "n/a",
                    stats.Gpu.Count > 0 ? $"{stats.Gpu.P99:F1} ms" : "n/a",
                    stats.FirstFrame.ToString(CultureInfo.InvariantCulture),
                    stats.EndFrame.ToString(CultureInfo.InvariantCulture));
            }

            table.AppendTo(sb);
            sb.AppendLine();
        }
    }
}
