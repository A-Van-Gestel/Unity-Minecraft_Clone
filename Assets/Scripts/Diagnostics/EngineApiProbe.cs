using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Benchmarks;
using Data;
using Jobs;
using UI.Toast;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using Unity.Profiling.LowLevel.Unsafe;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Diagnostics
{
    /// <summary>
    /// A one-shot, opt-in check of which profiling APIs actually work in the running build — the
    /// performance monitor's capability probe. It answers four questions that decide what the monitor can
    /// rely on, and that differ between the Editor, Development and Master players:
    /// <list type="number">
    /// <item>Which <see cref="ProfilerRecorder"/> counters are valid and report non-zero values.</item>
    /// <item>Whether <see cref="GC.GetAllocatedBytesForCurrentThread"/> is live and survives a collection.</item>
    /// <item>Whether <see cref="ProfilerUnsafeUtility.Timestamp"/> is callable from Burst, and its cost.</item>
    /// <item>Whether <see cref="FrameTimingManager"/> reports GPU, render-thread and present-wait times.</item>
    /// </list>
    /// <para>
    /// Started from the console (<c>/perf probe</c>) or the Settings → Benchmark tab's action button; runs for
    /// <see cref="SAMPLE_FRAMES"/> frames on a <c>DontDestroyOnLoad</c> host, writes a plain-text report through
    /// <see cref="BenchmarkEnvironment.WriteReportToDisk"/>, and keeps a short summary for <c>/perf</c>.
    /// Nothing runs unless it is started, and it refuses to start under an automated capture.
    /// </para>
    /// </summary>
    public sealed class EngineApiProbe : MonoBehaviour
    {
        private const int SAMPLE_FRAMES = 120;
        private const int TIMING_CALLS = 1_000_000;
        private const int GC_PROBE_BYTES = 1 << 20;
        private const double NANOSECONDS_PER_SECOND = 1e9;
        private const double MEDIAN_FRACTION = 0.5;
        private const int REPORT_CAPACITY_CHARS = 32 * 1024;
        private const float TOAST_DWELL_SECONDS = 8f;
        private const string LOG_TAG = "[ApiProbe]";
        private const string REPORT_PREFIX = "EngineApiProbe";

        // Reported first so the counters the monitor would use are not buried among ~200 rows.
        [NoAutoStaticsCleanup] // immutable table
        private static readonly string[] s_keyCounters =
        {
            "GC Allocated In Frame", "GC Used Memory", "GC Reserved Memory", "System Used Memory",
            "Total Used Memory", "CPU Main Thread Frame Time", "CPU Render Thread Frame Time",
            "GPU Frame Time", "SRP Batcher Draw Calls Count", "Standard Draw Calls Count", "SetPass Calls Count",
            "Triangles Count",
        };

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static EngineApiProbe s_running;

        /// <summary>Whether a probe is currently sampling.</summary>
        public static bool IsRunning => s_running != null;

        /// <summary>Summary lines of the last completed probe this session, or null when none has run.</summary>
        [NoAutoStaticsCleanup] // reset in DomainReset
        public static string[] LastSummary { get; private set; }

        /// <summary>Where the last report was written, or null when none was (or the write failed).</summary>
        [NoAutoStaticsCleanup] // reset in DomainReset
        public static string LastReportPath { get; private set; }

        // Sampling state lives on the instance so OnDestroy can release the recorders if the run is cut short.
        private readonly FrameTiming[] _timing = new FrameTiming[1];
        private ProfilerRecorder[] _recorders;
        private int _startedRecorders;
        private List<ProfilerRecorderDescription> _counterDescriptions;
        private List<bool> _counterHandleValid;
        private int[] _nonZeroFrames;
        private double[] _lastValues;
        private double[] _gpuMs;
        private double[] _renderThreadMs;
        private double[] _presentWaitMs;
        private int _timedFrames;
        private int _handleCount;
        private int _markers;
        private int _releaseMarkers;
        private bool _frameTimingEnabled;
        private int _failedChecks;

        /// <summary>
        /// Starts a probe on a fresh <c>DontDestroyOnLoad</c> host.
        /// </summary>
        /// <param name="reason">Why the probe did not start; null on success.</param>
        /// <returns>True when a probe was started.</returns>
        public static bool TryStart(out string reason)
        {
            if (WorldLaunchState.IsAutomatedMode)
            {
                reason = "Not available during an automated capture — it would distort the capture's numbers.";
                return false;
            }

            if (s_running != null)
            {
                reason = "A probe is already running.";
                return false;
            }

            GameObject host = new GameObject(nameof(EngineApiProbe));
            DontDestroyOnLoad(host);
            s_running = host.AddComponent<EngineApiProbe>();
            reason = null;
            return true;
        }

        /// <summary>Clears the running reference and the last result on play-mode entry.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            s_running = null;
            LastSummary = null;
            LastReportPath = null;
        }

        private void Start()
        {
            StartCoroutine(Run());
        }

        // Also the release path when the run is cut short: a coroutine stopped from outside (host destroyed,
        // play mode exited) never reaches Run's finally.
        private void OnDestroy()
        {
            ReleaseRecorders();
            if (s_running == this) s_running = null;
        }

        /// <summary>
        /// Runs every check, then writes the report and publishes the summary. A check that throws is
        /// recorded as that check's answer and the run carries on, so the probe always finishes and can run again.
        /// </summary>
        /// <returns>The coroutine.</returns>
        private IEnumerator Run()
        {
            StringBuilder report = new StringBuilder(capacity: REPORT_CAPACITY_CHARS);
            List<string> summary = new List<string>();

            try
            {
                report.AppendLine("<b>=== Engine API capability probe ===</b>");
                RunCheck("System description", report, summary,
                    () => report.AppendLine(BenchmarkEnvironment.DescribeSystem()));

                // Timed before any recorder starts, so recorder overhead cannot inflate the per-call costs.
                RunCheck("Timestamp cost", report, summary, () => ProbeTimestampCosts(report, summary));
                RunCheck("GC allocation counters", report, summary, () => ProbeGcApis(report, summary));
                RunCheck("Burst timestamp", report, summary, () => ProbeBurstTimestamp(report, summary));

                if (RunCheck("Recorder setup", report, summary, BeginSampling))
                {
                    for (int frame = 0; frame < SAMPLE_FRAMES; frame++)
                    {
                        yield return null;
                        if (!RunCheck("Frame sampling", report, summary, SampleFrame)) break;
                    }

                    RunCheck("Frame timing report", report, summary, () => AppendFrameTiming(report, summary,
                        _frameTimingEnabled, _timedFrames, _gpuMs, _renderThreadMs, _presentWaitMs));
                    RunCheck("Recorder report", report, summary, () => AppendRecorders(report, summary, _handleCount,
                        _markers, _releaseMarkers, _counterDescriptions, _counterHandleValid, _recorders,
                        _nonZeroFrames, _lastValues));
                }
            }
            finally
            {
                // Destroy first: it is deferred to the end of the frame, so publishing still runs, and a throw
                // while publishing can no longer leave the probe latched as running.
                if (this != null) Destroy(gameObject);
                ReleaseRecorders();
                Publish(report, summary);
            }
        }

        /// <summary>Runs one check, turning an exception into that check's reported answer.</summary>
        /// <param name="section">The check's name in the report and summary.</param>
        /// <param name="report">Receives the failure, if any.</param>
        /// <param name="summary">Receives the one-line failure, if any.</param>
        /// <param name="check">The check.</param>
        /// <returns>True when the check completed without throwing.</returns>
        private bool RunCheck(string section, StringBuilder report, List<string> summary, Action check)
        {
            try
            {
                check();
                return true;
            }
            catch (Exception e)
            {
                _failedChecks++;
                report.AppendLine($"<b>--- {section}: THREW ---</b>");
                report.AppendLine($"{e.GetType().Name}: {e.Message}");
                report.AppendLine();
                summary.Add($"{section}: THREW {e.GetType().Name}: {e.Message}");
                Debug.LogException(e);
                return false;
            }
        }

        /// <summary>Writes the report, stores the summary for <c>/perf</c>, logs it, and raises the completion toast.</summary>
        /// <param name="report">The full report.</param>
        /// <param name="summary">The one-line answers.</param>
        private void Publish(StringBuilder report, List<string> summary)
        {
            LastSummary = summary.ToArray();
            LastReportPath = BenchmarkEnvironment.WriteReportToDisk(report.ToString(), REPORT_PREFIX);

            foreach (string line in summary)
                Debug.Log($"{LOG_TAG} {line}");

            if (ToastManager.Instance == null) return;

            ToastManager.Show(new ToastRequest(
                _failedChecks == 0
                    ? "Engine API probe finished"
                    : $"Engine API probe finished — {_failedChecks} check(s) threw",
                LastReportPath != null ? Path.GetFileName(LastReportPath) : "Report write failed — see the log",
                null, TOAST_DWELL_SECONDS,
                variant: _failedChecks == 0 ? ToastVariant.Info : ToastVariant.Warning));
        }

        /// <summary>Measures the per-call cost of the two managed timestamp sources.</summary>
        /// <param name="report">Receives the full results.</param>
        /// <param name="summary">Receives the one-line answer.</param>
        private static void ProbeTimestampCosts(StringBuilder report, List<string> summary)
        {
            // Both reads are native calls, which no compiler stage can elide, so the results need no sink.
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < TIMING_CALLS; i++)
                Stopwatch.GetTimestamp();
            double stopwatchNs = ElapsedNs(start) / TIMING_CALLS;

            start = Stopwatch.GetTimestamp();
            for (int i = 0; i < TIMING_CALLS; i++)
                _ = ProfilerUnsafeUtility.Timestamp;
            double profilerNs = ElapsedNs(start) / TIMING_CALLS;

            report.AppendLine("<b>--- Timestamp cost (managed, main thread) ---</b>");
            report.AppendLine($"Stopwatch.GetTimestamp:            {stopwatchNs:F1} ns/call ({TIMING_CALLS:N0} calls, " +
                              $"Stopwatch.Frequency {Stopwatch.Frequency:N0} Hz, high-res {Stopwatch.IsHighResolution})");
            report.AppendLine($"ProfilerUnsafeUtility.Timestamp:   {profilerNs:F1} ns/call");
            report.AppendLine();

            summary.Add($"Timestamp cost: Stopwatch {stopwatchNs:F1} ns, Profiler {profilerNs:F1} ns per call");
        }

        /// <summary>
        /// Checks the GC allocation counters against a known allocation and across a forced collection,
        /// beside the heap-delta method the current monitor uses.
        /// </summary>
        /// <param name="report">Receives the full results.</param>
        /// <param name="summary">Receives the one-line answer.</param>
        private static void ProbeGcApis(StringBuilder report, List<string> summary)
        {
            report.AppendLine("<b>--- GC allocation counters ---</b>");
            report.AppendLine($"Known allocation: {GC_PROBE_BYTES:N0} bytes, then GC.Collect() (one deliberate hitch).");

            int collectionsBefore = GC.CollectionCount(0);
            long threadBefore = SafeRead(GC.GetAllocatedBytesForCurrentThread, out string threadError);
            long heapBefore = GC.GetTotalMemory(false);

            byte[] block = new byte[GC_PROBE_BYTES];
            long threadAfter = SafeRead(GC.GetAllocatedBytesForCurrentThread, out _);
            long heapAfter = GC.GetTotalMemory(false);
            GC.KeepAlive(block);

            GC.Collect();
            long threadCollected = SafeRead(GC.GetAllocatedBytesForCurrentThread, out _);
            long heapCollected = GC.GetTotalMemory(false);
            int collections = GC.CollectionCount(0) - collectionsBefore;

            string threadVerdict = GcVerdict(threadError, threadAfter - threadBefore, threadCollected >= threadAfter);

            report.AppendLine($"GC.GetAllocatedBytesForCurrentThread: {threadVerdict}");
            report.AppendLine($"  delta over allocation {threadAfter - threadBefore:N0} B; after collect {threadCollected - threadAfter:N0} B");
            // Fixed at compile time, not probed: the .NET Framework API compatibility level does not expose it.
            report.AppendLine("GC.GetTotalAllocatedBytes:            not in this project's API surface (.NET Framework profile)");
            report.AppendLine($"GC.GetTotalMemory (heap delta):       +{heapAfter - heapBefore:N0} B over allocation; " +
                              $"{heapCollected - heapAfter:N0} B after collect (a collection hides the allocation)");
            report.AppendLine($"GC.CollectionCount(0) delta:          {collections} (MaxGeneration {GC.MaxGeneration})");
            report.AppendLine();

            summary.Add($"GC thread-alloc counter: {threadVerdict}");
        }

        /// <summary>Runs <see cref="TimestampProbeJob"/> and reports which path ran and the per-read cost.</summary>
        /// <param name="report">Receives the full results.</param>
        /// <param name="summary">Receives the one-line answer.</param>
        private static void ProbeBurstTimestamp(StringBuilder report, List<string> summary)
        {
            NativeArray<long> result = new NativeArray<long>(TimestampProbeJob.ResultLength, Allocator.TempJob);
            try
            {
                new TimestampProbeJob { Iterations = TIMING_CALLS, Result = result }.Schedule().Complete();

                bool burstRan = result[TimestampProbeJob.BurstRanIndex] == 1;
                ProfilerUnsafeUtility.TimestampConversionRatio ratio = ProfilerUnsafeUtility.TimestampToNanosecondsConversionRatio;
                long ticks = result[TimestampProbeJob.EndIndex] - result[TimestampProbeJob.StartIndex];
                double nsPerCall = ratio.Denominator != 0
                    ? (double)ticks * ratio.Numerator / ratio.Denominator / TIMING_CALLS
                    : double.NaN;

                report.AppendLine("<b>--- Timestamp inside a Burst job ---</b>");
                report.AppendLine($"BurstCompiler.IsEnabled:   {BurstCompiler.IsEnabled}");
                report.AppendLine($"Path that ran:             {(burstRan ? "Burst" : "MANAGED (Burst compilation unavailable or failed)")}");
                report.AppendLine($"ProfilerUnsafeUtility.Timestamp: {nsPerCall:F1} ns/call ({TIMING_CALLS:N0} calls, " +
                                  $"ratio {ratio.Numerator}/{ratio.Denominator} ns/tick)");
                report.AppendLine();

                summary.Add(burstRan
                    ? $"Burst timestamp: callable, {nsPerCall:F1} ns/call"
                    : "Burst timestamp: NOT verified — the job ran managed");
            }
            finally
            {
                result.Dispose();
            }
        }

        /// <summary>
        /// Enumerates every profiler handle and starts a recorder on each counter, ready for
        /// <see cref="SampleFrame"/>. Recorders are counted as they start, so a throw part-way through releases
        /// exactly the ones that exist.
        /// </summary>
        private void BeginSampling()
        {
            List<ProfilerRecorderHandle> handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            _handleCount = handles.Count;

            _counterDescriptions = new List<ProfilerRecorderDescription>();
            _counterHandleValid = new List<bool>();
            foreach (ProfilerRecorderHandle handle in handles)
            {
                ProfilerRecorderDescription description = ProfilerRecorderHandle.GetDescription(handle);
                if ((description.Flags & MarkerFlags.Counter) == 0)
                {
                    _markers++;
                    if ((description.Flags & MarkerFlags.AvailabilityNonDevelopment) != 0) _releaseMarkers++;
                    continue;
                }

                _counterDescriptions.Add(description);
                _counterHandleValid.Add(handle.Valid);
            }

            int counterCount = _counterDescriptions.Count;
            _nonZeroFrames = new int[counterCount];
            _lastValues = new double[counterCount];
            _gpuMs = new double[SAMPLE_FRAMES];
            _renderThreadMs = new double[SAMPLE_FRAMES];
            _presentWaitMs = new double[SAMPLE_FRAMES];
            _frameTimingEnabled = FrameTimingManager.IsFeatureEnabled();

            _recorders = new ProfilerRecorder[counterCount];
            for (int i = 0; i < counterCount; i++)
            {
                _recorders[i] = ProfilerRecorder.StartNew(_counterDescriptions[i].Category, _counterDescriptions[i].Name);
                _startedRecorders++;
            }
        }

        /// <summary>Reads every recorder and the latest frame timing for the frame just rendered.</summary>
        private void SampleFrame()
        {
            for (int i = 0; i < _startedRecorders; i++)
            {
                if (!_recorders[i].Valid) continue;

                double value = _recorders[i].LastValueAsDouble;
                _lastValues[i] = value;
                if (value != 0) _nonZeroFrames[i]++;
            }

            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, _timing) == 0) return;

            _gpuMs[_timedFrames] = _timing[0].gpuFrameTime;
            _renderThreadMs[_timedFrames] = _timing[0].cpuRenderThreadFrameTime;
            _presentWaitMs[_timedFrames] = _timing[0].cpuMainThreadPresentWaitTime;
            _timedFrames++;
        }

        /// <summary>Disposes the started recorders. Idempotent: called from the run's end and from <see cref="OnDestroy"/>.</summary>
        private void ReleaseRecorders()
        {
            if (_recorders == null) return;

            for (int i = 0; i < _startedRecorders; i++)
                _recorders[i].Dispose();

            _recorders = null;
            _startedRecorders = 0;
        }

        /// <summary>Reports the frame-timing samples as p50/max per field.</summary>
        /// <param name="report">Receives the full results.</param>
        /// <param name="summary">Receives the one-line answer.</param>
        /// <param name="featureEnabled">What <see cref="FrameTimingManager.IsFeatureEnabled"/> returned.</param>
        /// <param name="frames">Frames for which a timing was returned.</param>
        /// <param name="gpuMs">GPU frame times, ms.</param>
        /// <param name="renderThreadMs">Render-thread frame times, ms.</param>
        /// <param name="presentWaitMs">Main-thread present-wait times, ms.</param>
        private static void AppendFrameTiming(StringBuilder report, List<string> summary, bool featureEnabled, int frames,
            double[] gpuMs, double[] renderThreadMs, double[] presentWaitMs)
        {
            report.AppendLine("<b>--- FrameTimingManager ---</b>");
            report.AppendLine($"IsFeatureEnabled:   {featureEnabled}");
            report.AppendLine($"Frames with timing: {frames} of {SAMPLE_FRAMES} (sampled while the settings menu or console was open)");
            report.AppendLine($"gpuFrameTime:                 {DescribeSamples(gpuMs, frames)}");
            report.AppendLine($"cpuRenderThreadFrameTime:     {DescribeSamples(renderThreadMs, frames)}");
            report.AppendLine($"cpuMainThreadPresentWaitTime: {DescribeSamples(presentWaitMs, frames)}");
            report.AppendLine();

            // "Disabled" and "enabled but reporting zero" are different answers: the second means the
            // graphics API or driver gives no GPU timing even with the project setting on.
            string gpu = !featureEnabled ? "stats disabled in this build"
                : frames == 0 ? "enabled, but no timings returned"
                : $"GPU p50 {Percentile(gpuMs, frames, MEDIAN_FRACTION):F2} ms ({(Max(gpuMs, frames) > 0 ? "reporting" : "ALL ZERO")})";
            summary.Add($"Frame timing: {gpu}");
        }

        /// <summary>Reports every counter's validity and activity, key counters first.</summary>
        /// <param name="report">Receives the full results.</param>
        /// <param name="summary">Receives the one-line answers.</param>
        /// <param name="handleCount">All available handles (markers and counters).</param>
        /// <param name="markers">Handles that are markers.</param>
        /// <param name="releaseMarkers">Markers flagged as present in non-development players.</param>
        /// <param name="descriptions">The counters' descriptions.</param>
        /// <param name="handleValid">Each counter handle's <c>Valid</c>, same order.</param>
        /// <param name="recorders">Each counter's recorder, same order.</param>
        /// <param name="nonZeroFrames">Frames each recorder read a non-zero value, same order.</param>
        /// <param name="lastValues">Each recorder's last value, same order.</param>
        private static void AppendRecorders(StringBuilder report, List<string> summary, int handleCount, int markers,
            int releaseMarkers, List<ProfilerRecorderDescription> descriptions, List<bool> handleValid,
            ProfilerRecorder[] recorders, int[] nonZeroFrames, double[] lastValues)
        {
            int counters = descriptions.Count;
            int recorderValid = 0;
            int live = 0;
            for (int i = 0; i < counters; i++)
            {
                if (recorders[i].Valid) recorderValid++;
                if (nonZeroFrames[i] > 0) live++;
            }

            report.AppendLine("<b>--- ProfilerRecorder ---</b>");
            report.AppendLine($"Available handles: {handleCount:N0} ({markers:N0} markers, {releaseMarkers:N0} of them " +
                              $"flagged for non-development players; {counters:N0} counters)");
            report.AppendLine($"Counters: {recorderValid} recorder-valid, {live} non-zero in at least one of {SAMPLE_FRAMES} frames");
            report.AppendLine();
            report.AppendLine("Key counters:");
            // Every match, not the first: several names exist once per category (Render and UI Toolkit both
            // publish "Draw Calls Count"), and the first is not reliably the one that reports.
            foreach (string key in s_keyCounters)
            {
                bool found = false;
                for (int i = 0; i < counters; i++)
                {
                    if (descriptions[i].Name != key) continue;

                    found = true;
                    report.AppendLine($"  {DescribeCounter(descriptions[i], handleValid[i], recorders[i], nonZeroFrames[i], lastValues[i])}");
                }

                if (!found) report.AppendLine($"  {key,-30} NOT AVAILABLE");
            }

            report.AppendLine();
            report.AppendLine("All counters (name | category | unit | handle valid | recorder valid | non-zero frames | last value):");
            for (int i = 0; i < counters; i++)
                report.AppendLine($"  {DescribeCounter(descriptions[i], handleValid[i], recorders[i], nonZeroFrames[i], lastValues[i])}");
            report.AppendLine();

            summary.Add($"Recorders: {counters} counters, {recorderValid} valid, {live} non-zero; {markers:N0} markers");

            int gcIndex = descriptions.FindIndex(d => d.Name == s_keyCounters[0]);
            summary.Add(gcIndex < 0
                ? $"'{s_keyCounters[0]}': not available"
                : $"'{s_keyCounters[0]}': {(recorders[gcIndex].Valid ? "valid" : "INVALID")}, non-zero in {nonZeroFrames[gcIndex]} frames");
        }

        /// <summary>Formats one counter row.</summary>
        /// <param name="description">The counter.</param>
        /// <param name="handleValid">Its handle's <c>Valid</c>.</param>
        /// <param name="recorder">Its recorder.</param>
        /// <param name="nonZero">Frames it read non-zero.</param>
        /// <param name="lastValue">Its last value.</param>
        /// <returns>The row text.</returns>
        private static string DescribeCounter(ProfilerRecorderDescription description, bool handleValid,
            ProfilerRecorder recorder, int nonZero, double lastValue)
        {
            return $"{description.Name,-30} | {description.Category.Name,-10} | {description.UnitType,-10} | " +
                   $"{handleValid,-5} | {recorder.Valid,-5} | {nonZero,3} | {lastValue.ToString("G6", CultureInfo.InvariantCulture)}";
        }

        /// <summary>Reads a GC counter, turning an unsupported-API exception into an error string.</summary>
        /// <param name="read">The read.</param>
        /// <param name="error">The exception type and message, or null on success.</param>
        /// <returns>The value, or 0 on failure.</returns>
        private static long SafeRead(Func<long> read, out string error)
        {
            try
            {
                error = null;
                return read();
            }
            catch (Exception e)
            {
                error = $"{e.GetType().Name}: {e.Message}";
                return 0;
            }
        }

        /// <summary>Classifies a GC counter from its delta over the known allocation and its post-collect reading.</summary>
        /// <param name="error">The read error, or null.</param>
        /// <param name="delta">The counter's change over the known allocation.</param>
        /// <param name="monotonic">Whether the reading after the collection is not below the one before it.</param>
        /// <returns>The verdict text.</returns>
        private static string GcVerdict(string error, long delta, bool monotonic)
        {
            if (error != null) return $"UNSUPPORTED ({error})";
            if (delta < GC_PROBE_BYTES) return $"NOT LIVE (saw {delta:N0} B of a {GC_PROBE_BYTES:N0} B allocation)";
            return monotonic ? "live, monotonic across a collection" : "live, but DROPPED across a collection";
        }

        /// <summary>Formats p50/max of the first <paramref name="count"/> samples.</summary>
        /// <param name="samples">The samples.</param>
        /// <param name="count">How many are filled.</param>
        /// <returns>The text, or "no samples".</returns>
        private static string DescribeSamples(double[] samples, int count)
        {
            return count == 0
                ? "no samples"
                : $"p50 {Percentile(samples, count, MEDIAN_FRACTION):F3} ms | max {Max(samples, count):F3} ms";
        }

        /// <summary>Nearest-rank percentile of the first <paramref name="count"/> samples (sorts them in place).</summary>
        /// <param name="samples">The samples.</param>
        /// <param name="count">How many are filled (must be &gt; 0).</param>
        /// <param name="fraction">The percentile, 0–1.</param>
        /// <returns>The percentile value.</returns>
        private static double Percentile(double[] samples, int count, double fraction)
        {
            Array.Sort(samples, 0, count);
            int rank = Math.Max(1, (int)Math.Ceiling(fraction * count));
            return samples[rank - 1];
        }

        /// <summary>Largest of the first <paramref name="count"/> samples.</summary>
        /// <param name="samples">The samples.</param>
        /// <param name="count">How many are filled.</param>
        /// <returns>The maximum, or 0 when empty.</returns>
        private static double Max(double[] samples, int count)
        {
            double max = 0;
            for (int i = 0; i < count; i++)
                max = Math.Max(max, samples[i]);
            return max;
        }

        /// <summary>Nanoseconds since <paramref name="start"/>.</summary>
        /// <param name="start">A <see cref="Stopwatch.GetTimestamp"/> reading.</param>
        /// <returns>The elapsed time in nanoseconds.</returns>
        private static double ElapsedNs(long start)
        {
            return (Stopwatch.GetTimestamp() - start) * NANOSECONDS_PER_SECOND / Stopwatch.Frequency;
        }
    }
}
