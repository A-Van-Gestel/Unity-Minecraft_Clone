using System;
using System.Collections.Generic;
using System.Text;
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
        /// <returns>A <see cref="BenchmarkReportResult"/> containing the report text and file path.</returns>
        public static BenchmarkReportResult GenerateAndWriteReport(
            BenchmarkMetricsCollector collector,
            float[] generationSpeeds,
            float[] loadingSpeeds,
            float timePerPhase,
            int regionSize,
            int configuredRegionSize,
            int generationWaypointCount,
            int loadingWaypointCount,
            TimeSpan totalDuration,
            int savedVSyncCount,
            int savedTargetFrameRate)
        {
            StringBuilder sb = new StringBuilder(4096);

            AppendHeader(sb, totalDuration);
            sb.Append(BenchmarkEnvironment.DescribeSystem());
            AppendConfiguration(sb, generationSpeeds, loadingSpeeds, timePerPhase, regionSize,
                configuredRegionSize, generationWaypointCount, loadingWaypointCount, savedVSyncCount, savedTargetFrameRate);
            AppendOverallSummary(sb, collector.CompletedPhases, totalDuration);
            AppendGroupedPhases(sb, collector.CompletedPhases);

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
            int regionSize,
            int configuredRegionSize,
            int generationWaypointCount,
            int loadingWaypointCount,
            int savedVSyncCount,
            int savedTargetFrameRate)
        {
            sb.AppendLine("<b>=== Configuration ===</b>");

            string regionLabel = regionSize != configuredRegionSize
                ? $"{regionSize} chunks (configured: {configuredRegionSize}, auto-scaled)"
                : $"{regionSize} chunks";

            sb.AppendLine($"Region size:         {regionLabel}");
            sb.AppendLine($"Phase duration:      {timePerPhase:F0} s");
            sb.AppendLine($"Generation speeds:   {string.Join("; ", generationSpeeds)} m/s");
            sb.AppendLine($"Loading speeds:      {string.Join("; ", loadingSpeeds)} m/s");
            sb.AppendLine($"Generation WPs:      {generationWaypointCount}");
            sb.AppendLine($"Loading WPs:         {loadingWaypointCount}");
            sb.AppendLine($"VSync override:      Forced Off (was: {(savedVSyncCount > 0 ? "On" : "Off")})");
            sb.AppendLine($"FPS cap override:    Uncapped (was: {(savedTargetFrameRate > 0 ? savedTargetFrameRate.ToString() : "Uncapped")})");
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
            sb.AppendLine();
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
    }
}
