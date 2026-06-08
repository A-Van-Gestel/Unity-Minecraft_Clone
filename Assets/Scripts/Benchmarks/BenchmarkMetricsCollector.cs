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

        /// <summary>Average wall-clock FPS across all samples.</summary>
        public double AvgWallFps;

        /// <summary>Minimum wall-clock FPS observed in any single sample.</summary>
        public double MinWallFps;

        /// <summary>Average CPU FPS across all samples.</summary>
        public double AvgCpuFps;

        /// <summary>Minimum CPU FPS observed in any single sample.</summary>
        public double MinCpuFps;

        /// <summary>Average native memory allocation in MB.</summary>
        public double AvgNativeAllocMb;

        /// <summary>Peak native memory allocation in MB.</summary>
        public double PeakNativeAllocMb;

        /// <summary>Average native reserved memory in MB.</summary>
        public double AvgNativeReservedMb;

        /// <summary>Peak native reserved memory in MB.</summary>
        public double PeakNativeReservedMb;

        /// <summary>Average managed (GC) memory in MB.</summary>
        public double AvgManagedMemMb;

        /// <summary>Peak managed (GC) memory in MB.</summary>
        public double PeakManagedMemMb;

        /// <summary>Average total memory (native + managed) in MB.</summary>
        public double AvgTotalMemMb;

        /// <summary>Peak total memory (native + managed) in MB.</summary>
        public double PeakTotalMemMb;
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

        private double _wallFpsSum;
        private double _wallFpsMin;
        private double _cpuFpsSum;
        private double _cpuFpsMin;

        private double _nativeAllocSum;
        private double _nativeAllocPeak;
        private double _nativeReservedSum;
        private double _nativeReservedPeak;
        private double _managedMemSum;
        private double _managedMemPeak;
        private double _totalMemSum;
        private double _totalMemPeak;

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

            _wallFpsSum = 0;
            _wallFpsMin = double.MaxValue;
            _cpuFpsSum = 0;
            _cpuFpsMin = double.MaxValue;

            _nativeAllocSum = 0;
            _nativeAllocPeak = 0;
            _nativeReservedSum = 0;
            _nativeReservedPeak = 0;
            _managedMemSum = 0;
            _managedMemPeak = 0;
            _totalMemSum = 0;
            _totalMemPeak = 0;

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

                AvgWallFps = _sampleCount > 0 ? _wallFpsSum / _sampleCount : 0,
                MinWallFps = _sampleCount > 0 && _wallFpsMin < double.MaxValue ? _wallFpsMin : 0,
                AvgCpuFps = _sampleCount > 0 ? _cpuFpsSum / _sampleCount : 0,
                MinCpuFps = _sampleCount > 0 && _cpuFpsMin < double.MaxValue ? _cpuFpsMin : 0,

                AvgNativeAllocMb = _sampleCount > 0 ? _nativeAllocSum / _sampleCount : 0,
                PeakNativeAllocMb = _nativeAllocPeak,
                AvgNativeReservedMb = _sampleCount > 0 ? _nativeReservedSum / _sampleCount : 0,
                PeakNativeReservedMb = _nativeReservedPeak,
                AvgManagedMemMb = _sampleCount > 0 ? _managedMemSum / _sampleCount : 0,
                PeakManagedMemMb = _managedMemPeak,
                AvgTotalMemMb = _sampleCount > 0 ? _totalMemSum / _sampleCount : 0,
                PeakTotalMemMb = _totalMemPeak,
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

            _wallFpsSum += snapshot.WallFps;
            _wallFpsMin = Math.Min(_wallFpsMin, snapshot.WallFps);
            _cpuFpsSum += snapshot.CpuFps;
            _cpuFpsMin = Math.Min(_cpuFpsMin, snapshot.CpuFps);

            _nativeAllocSum += snapshot.NativeAllocMb;
            _nativeAllocPeak = Math.Max(_nativeAllocPeak, snapshot.NativeAllocMb);
            _nativeReservedSum += snapshot.NativeReservedMb;
            _nativeReservedPeak = Math.Max(_nativeReservedPeak, snapshot.NativeReservedMb);
            _managedMemSum += snapshot.ManagedMemMb;
            _managedMemPeak = Math.Max(_managedMemPeak, snapshot.ManagedMemMb);
            _totalMemSum += snapshot.TotalMemMb;
            _totalMemPeak = Math.Max(_totalMemPeak, snapshot.TotalMemMb);
        }
    }
}
