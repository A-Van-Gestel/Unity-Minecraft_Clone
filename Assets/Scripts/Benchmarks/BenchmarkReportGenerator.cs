using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Benchmarks
{
    /// <summary>
    /// Generates a structured performance report from benchmark metrics and writes it to disk.
    /// Reuses <see cref="BenchmarkEnvironment.DescribeSystem"/> for the system/build/Burst header
    /// and <see cref="BenchmarkEnvironment.WriteReportToDisk"/> for file output.
    /// </summary>
    public static class BenchmarkReportGenerator
    {
        /// <summary>
        /// Generates the full benchmark report, logs it to the console, and writes a plain-text
        /// copy to <c>Application.persistentDataPath/Benchmarks/</c>.
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
        public static void GenerateAndWriteReport(
            BenchmarkMetricsCollector collector,
            float[] generationSpeeds,
            float[] loadingSpeeds,
            float timePerPhase,
            int regionSize,
            int configuredRegionSize,
            int generationWaypointCount,
            int loadingWaypointCount,
            TimeSpan totalDuration)
        {
            StringBuilder sb = new StringBuilder(4096);

            AppendHeader(sb, totalDuration);
            sb.Append(BenchmarkEnvironment.DescribeSystem());
            AppendConfiguration(sb, generationSpeeds, loadingSpeeds, timePerPhase, regionSize,
                configuredRegionSize, generationWaypointCount, loadingWaypointCount);
            AppendOverallSummary(sb, collector.CompletedPhases, totalDuration);
            AppendGroupedPhases(sb, collector.CompletedPhases);

            string report = sb.ToString();
            Debug.Log(report);
            BenchmarkEnvironment.WriteReportToDisk(report, "BenchmarkRun");
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
            int loadingWaypointCount)
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
            double overallPeakCpu = 0;

            foreach (PhaseMetrics phase in phases)
            {
                totalSamples += phase.SampleCount;
                totalDurationSeconds += phase.DurationSeconds;
                weightedCpuSum += phase.AvgCpuTimeMs * phase.SampleCount;
                weightedWallSum += phase.AvgWallTimeMs * phase.SampleCount;
                weightedGcSum += phase.AvgGcAllocKb * phase.SampleCount;
                overallPeakCpu = Math.Max(overallPeakCpu, phase.PeakCpuTimeMs);
            }

            double avgCpu = totalSamples > 0 ? weightedCpuSum / totalSamples : 0;
            double avgWall = totalSamples > 0 ? weightedWallSum / totalSamples : 0;
            double avgGc = totalSamples > 0 ? weightedGcSum / totalSamples : 0;

            sb.AppendLine($"Total phases:        {phases.Count}");
            sb.AppendLine($"Total samples:       {totalSamples:N0}");
            sb.AppendLine($"Wall-clock runtime:  {BenchmarkEnvironment.FormatDuration(totalDuration)}");
            sb.AppendLine($"Phase duration sum:  {BenchmarkEnvironment.FormatDuration(TimeSpan.FromSeconds(totalDurationSeconds))}");
            sb.AppendLine($"Avg CPU time:        {avgCpu:F1} ms");
            sb.AppendLine($"Peak CPU time:       {overallPeakCpu:F1} ms");
            sb.AppendLine($"Avg Wall time:       {avgWall:F1} ms");
            sb.AppendLine($"Avg GC alloc:        {avgGc:F1} KB");
            sb.AppendLine();
        }

        private static void AppendGroupedPhases(StringBuilder sb, IReadOnlyList<PhaseMetrics> phases)
        {
            string currentGroup = null;

            // Accumulators for group totals
            int groupSamples = 0;
            double groupDuration = 0;
            double groupWeightedCpu = 0;
            double groupPeakCpu = 0;
            int groupPhaseCount = 0;

            foreach (PhaseMetrics phase in phases)
            {
                if (phase.GroupName != currentGroup)
                {
                    if (currentGroup != null && groupPhaseCount > 1)
                        AppendGroupTotal(sb, groupDuration, groupSamples, groupWeightedCpu, groupPeakCpu);
                    else if (currentGroup != null)
                        sb.AppendLine();

                    currentGroup = phase.GroupName;
                    groupSamples = 0;
                    groupDuration = 0;
                    groupWeightedCpu = 0;
                    groupPeakCpu = 0;
                    groupPhaseCount = 0;

                    sb.AppendLine($"<b>=== {currentGroup} ===</b>");
                    sb.AppendLine($"  {"Phase",-18} {"Duration",9} {"Avg CPU",9} {"Peak CPU",9} {"Avg Wall",9} {"Peak Wall",10} {"Avg GC",8} {"Peak GC",8}");
                }

                groupSamples += phase.SampleCount;
                groupDuration += phase.DurationSeconds;
                groupWeightedCpu += phase.AvgCpuTimeMs * phase.SampleCount;
                groupPeakCpu = Math.Max(groupPeakCpu, phase.PeakCpuTimeMs);
                groupPhaseCount++;

                sb.AppendLine($"  {phase.PhaseName,-18} {BenchmarkEnvironment.FormatDuration(TimeSpan.FromSeconds(phase.DurationSeconds)),9} " +
                              $"{phase.AvgCpuTimeMs,8:F1}ms {phase.PeakCpuTimeMs,8:F1}ms " +
                              $"{phase.AvgWallTimeMs,8:F1}ms {phase.PeakWallTimeMs,9:F1}ms " +
                              $"{phase.AvgGcAllocKb,6:F1} KB {phase.PeakGcAllocKb,6:F1} KB");
            }

            if (currentGroup != null && groupPhaseCount > 1)
                AppendGroupTotal(sb, groupDuration, groupSamples, groupWeightedCpu, groupPeakCpu);

            sb.AppendLine();
        }

        private static void AppendGroupTotal(StringBuilder sb, double duration, int samples, double weightedCpu, double peakCpu)
        {
            double avgCpu = samples > 0 ? weightedCpu / samples : 0;
            sb.AppendLine($"  {"-- Group Total --",-18} {BenchmarkEnvironment.FormatDuration(TimeSpan.FromSeconds(duration)),9}   Avg CPU: {avgCpu:F1} ms   Peak CPU: {peakCpu:F1} ms");
            sb.AppendLine();
        }

    }
}
