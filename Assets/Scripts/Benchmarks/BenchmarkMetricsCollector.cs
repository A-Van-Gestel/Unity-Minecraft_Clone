using System;
using System.Collections.Generic;
using UnityEngine;

namespace Benchmarks
{
    /// <summary>
    /// Accumulated performance statistics for a single benchmark phase (e.g., one speed tier
    /// within the generation or loading pass). Populated from <see cref="PerformanceMonitor"/>
    /// snapshots received via the <see cref="PerformanceMonitor.OnMetricsSampled"/> event.
    /// </summary>
    public struct PhaseMetrics
    {
        /// <summary>Display name for this phase (e.g., "10 m/s").</summary>
        public string PhaseName;

        /// <summary>Logical group this phase belongs to (e.g., "Generation Pass", "Loading Pass", "Transition").</summary>
        public string GroupName;

        /// <summary>Wall-clock duration of the phase in seconds.</summary>
        public float DurationSeconds;

        /// <summary>Number of <see cref="PerformanceMonitor.FrameMetricSnapshot"/> samples received during this phase.</summary>
        public int SampleCount;

        /// <summary>Average CPU frame time in milliseconds across all samples.</summary>
        public double AvgCpuTimeMs;

        /// <summary>Peak CPU frame time observed in any single sample.</summary>
        public double PeakCpuTimeMs;

        /// <summary>Average wall-clock frame time in milliseconds across all samples.</summary>
        public double AvgWallTimeMs;

        /// <summary>Peak wall-clock frame time observed in any single sample.</summary>
        public double PeakWallTimeMs;

        /// <summary>Average managed GC allocation per sample in kilobytes.</summary>
        public double AvgGcAllocKb;

        /// <summary>Peak managed GC allocation observed in any single sample.</summary>
        public double PeakGcAllocKb;
    }

    /// <summary>
    /// Lightweight metrics collector that subscribes to <see cref="PerformanceMonitor.OnMetricsSampled"/>
    /// and accumulates per-phase statistics during a benchmark run. Not a MonoBehaviour — instantiated
    /// by <see cref="BenchmarkController"/> and disposed when the benchmark completes.
    /// <para>The event handler performs pure arithmetic with zero allocations. All storage is
    /// pre-allocated in the constructor.</para>
    /// </summary>
    public class BenchmarkMetricsCollector
    {
        private readonly List<PhaseMetrics> _completedPhases;

        // Active phase accumulators
        private string _activePhaseName;
        private string _activeGroupName;
        private float _phaseStartTime;
        private int _sampleCount;
        private double _cpuTimeSum;
        private double _cpuTimePeak;
        private double _wallTimeSum;
        private double _wallTimePeak;
        private double _gcAllocSum;
        private double _gcAllocPeak;
        private bool _hasActivePhase;
        private bool _isRecording;
        private PerformanceMonitor _cachedMonitor;

        /// <summary>
        /// Read-only access to all completed phase results.
        /// </summary>
        public IReadOnlyList<PhaseMetrics> CompletedPhases => _completedPhases;

        /// <summary>
        /// Creates a new collector with pre-allocated storage.
        /// </summary>
        /// <param name="expectedPhaseCount">Expected number of phases to avoid list resizing.</param>
        public BenchmarkMetricsCollector(int expectedPhaseCount = 16)
        {
            _completedPhases = new List<PhaseMetrics>(expectedPhaseCount);
        }

        /// <summary>
        /// Subscribes to <see cref="PerformanceMonitor.OnMetricsSampled"/> to begin receiving
        /// frame metric snapshots. Does nothing if <see cref="PerformanceMonitor.Instance"/> is null.
        /// </summary>
        public void StartRecording()
        {
            if (PerformanceMonitor.Instance == null)
            {
                Debug.LogWarning("[BenchmarkMetricsCollector] PerformanceMonitor.Instance is null. " +
                                 "Metrics will not be collected.");
                return;
            }

            _cachedMonitor = PerformanceMonitor.Instance;
            _cachedMonitor.OnMetricsSampled += OnMetricsSampled;
            _isRecording = true;
        }

        /// <summary>
        /// Unsubscribes from the performance monitor event. Safe to call multiple times.
        /// Ends any active phase before unsubscribing.
        /// Uses the cached reference from <see cref="StartRecording"/> to safely unsubscribe
        /// even if <see cref="PerformanceMonitor.Instance"/> has been destroyed.
        /// </summary>
        public void StopRecording()
        {
            if (_hasActivePhase)
                EndPhase();

            if (!_isRecording) return;
            _isRecording = false;

            if (_cachedMonitor != null)
                _cachedMonitor.OnMetricsSampled -= OnMetricsSampled;

            _cachedMonitor = null;
        }

        /// <summary>
        /// Begins tracking a new phase. If a phase is already active, it is ended first
        /// (equivalent to calling <see cref="EndPhase"/> followed by a new <see cref="BeginPhase"/>).
        /// </summary>
        /// <param name="phaseName">Display name for this phase (e.g., "10 m/s").</param>
        /// <param name="groupName">Logical group (e.g., "Generation Pass").</param>
        public void BeginPhase(string phaseName, string groupName)
        {
            if (_hasActivePhase)
                EndPhase();

            _activePhaseName = phaseName;
            _activeGroupName = groupName;
            _phaseStartTime = Time.realtimeSinceStartup;
            _sampleCount = 0;
            _cpuTimeSum = 0;
            _cpuTimePeak = 0;
            _wallTimeSum = 0;
            _wallTimePeak = 0;
            _gcAllocSum = 0;
            _gcAllocPeak = 0;
            _hasActivePhase = true;
        }

        /// <summary>
        /// Ends the current phase, computes averages from accumulated samples,
        /// and appends the result to <see cref="CompletedPhases"/>.
        /// </summary>
        public void EndPhase()
        {
            if (!_hasActivePhase) return;

            float duration = Time.realtimeSinceStartup - _phaseStartTime;

            _completedPhases.Add(new PhaseMetrics
            {
                PhaseName = _activePhaseName,
                GroupName = _activeGroupName,
                DurationSeconds = duration,
                SampleCount = _sampleCount,
                AvgCpuTimeMs = _sampleCount > 0 ? _cpuTimeSum / _sampleCount : 0,
                PeakCpuTimeMs = _cpuTimePeak,
                AvgWallTimeMs = _sampleCount > 0 ? _wallTimeSum / _sampleCount : 0,
                PeakWallTimeMs = _wallTimePeak,
                AvgGcAllocKb = _sampleCount > 0 ? _gcAllocSum / _sampleCount : 0,
                PeakGcAllocKb = _gcAllocPeak,
            });

            _hasActivePhase = false;
        }

        /// <summary>
        /// Event handler for <see cref="PerformanceMonitor.OnMetricsSampled"/>.
        /// Pure arithmetic — zero allocations.
        /// </summary>
        private void OnMetricsSampled(PerformanceMonitor.FrameMetricSnapshot snapshot)
        {
            if (!_hasActivePhase) return;

            _sampleCount++;
            _cpuTimeSum += snapshot.CpuTimeMs;
            _cpuTimePeak = Math.Max(_cpuTimePeak, snapshot.CpuTimeMs);
            _wallTimeSum += snapshot.WallTimeMs;
            _wallTimePeak = Math.Max(_wallTimePeak, snapshot.WallTimeMs);
            _gcAllocSum += snapshot.GcAllocKb;
            _gcAllocPeak = Math.Max(_gcAllocPeak, snapshot.GcAllocKb);
        }
    }
}
