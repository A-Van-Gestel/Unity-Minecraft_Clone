using System.Collections.Generic;
using UnityEngine;

namespace Benchmarks
{
    /// <summary>
    /// Accumulates the full-world fluid stress pass's per-phase metrics. Unlike
    /// <see cref="BenchmarkMetricsCollector"/> (which samples <see cref="PerformanceMonitor"/>'s ~10 Hz event for
    /// whole-frame stats), this is pumped <b>every frame</b> by <see cref="FluidStressController"/> so the
    /// per-frame <see cref="WorldFrameProfiler"/> sub-phase costs (Tick / Apply / Mesh / Light) are captured at
    /// full resolution — the attribution is the whole point, and its <i>peaks</i> (the once-per-second tick frame,
    /// the mesh cascade that follows) are lost by a throttled sampler.
    /// <para>
    /// Whole-frame cost is taken as <see cref="Time.unscaledDeltaTime"/> (a true per-frame wall time including
    /// render/GPU), and the sub-phases from <see cref="WorldFrameProfiler"/> (true per-frame, main-thread
    /// <c>World.Update</c> interior). Both are exact per frame, so avg <b>and</b> peak are meaningful. GC/frame is
    /// read from <see cref="PerformanceMonitor"/> for context (smoothed).
    /// </para>
    /// </summary>
    public sealed class FluidStressMetricsCollector
    {
        /// <summary>The finalized statistics for one recorded phase (e.g. "Baseline", "Flood").</summary>
        public readonly struct PhaseResult
        {
            /// <summary>Phase label.</summary>
            public readonly string Name;

            /// <summary>Number of frames sampled.</summary>
            public readonly int Frames;

            /// <summary>Wall-clock duration of the phase, in seconds.</summary>
            public readonly double DurationSeconds;

            /// <summary>Average / peak whole-frame wall time (ms), from <see cref="Time.unscaledDeltaTime"/>.</summary>
            public readonly double FrameMsAvg, FrameMsPeak;

            /// <summary>Average / peak behavior-tick time (ms) — <c>ProcessTickUpdates</c>.</summary>
            public readonly double TickMsAvg, TickMsPeak;

            /// <summary>Average / peak modification-drain time (ms) — <c>World.ApplyModifications</c>.</summary>
            public readonly double ApplyMsAvg, ApplyMsPeak;

            /// <summary>Average / peak main-thread mesh time (ms) — mesh-job process + schedule + <c>CreateMesh</c>.</summary>
            public readonly double MeshMsAvg, MeshMsPeak;

            /// <summary>Average / peak main-thread lighting time (ms) — lighting-job process + schedule.</summary>
            public readonly double LightMsAvg, LightMsPeak;

            /// <summary>Average managed GC allocation per frame (KB), smoothed (context only).</summary>
            public readonly double GcKbAvg;

            /// <summary>Constructs a phase result.</summary>
            public PhaseResult(string name, int frames, double durationSeconds,
                double frameMsAvg, double frameMsPeak,
                double tickMsAvg, double tickMsPeak,
                double applyMsAvg, double applyMsPeak,
                double meshMsAvg, double meshMsPeak,
                double lightMsAvg, double lightMsPeak,
                double gcKbAvg)
            {
                Name = name;
                Frames = frames;
                DurationSeconds = durationSeconds;
                FrameMsAvg = frameMsAvg;
                FrameMsPeak = frameMsPeak;
                TickMsAvg = tickMsAvg;
                TickMsPeak = tickMsPeak;
                ApplyMsAvg = applyMsAvg;
                ApplyMsPeak = applyMsPeak;
                MeshMsAvg = meshMsAvg;
                MeshMsPeak = meshMsPeak;
                LightMsAvg = lightMsAvg;
                LightMsPeak = lightMsPeak;
                GcKbAvg = gcKbAvg;
            }

            /// <summary>The sub-phase total (Tick+Apply+Mesh+Light) of the average frame — the fluid-relevant Update cost.</summary>
            public double SubPhaseMsAvg => TickMsAvg + ApplyMsAvg + MeshMsAvg + LightMsAvg;
        }

        private readonly List<PhaseResult> _phases = new List<PhaseResult>();

        /// <summary>All finalized phases, in record order.</summary>
        public IReadOnlyList<PhaseResult> Phases => _phases;

        // Active-phase accumulators.
        private bool _active;
        private string _name;
        private int _frames;
        private double _startTime;
        private double _frameSum, _framePeak;
        private double _tickSum, _tickPeak;
        private double _applySum, _applyPeak;
        private double _meshSum, _meshPeak;
        private double _lightSum, _lightPeak;
        private double _gcSum;

        /// <summary>Begins a new measured phase, resetting all accumulators.</summary>
        /// <param name="name">Phase label for the report.</param>
        public void BeginPhase(string name)
        {
            _active = true;
            _name = name;
            _frames = 0;
            _startTime = Time.realtimeSinceStartupAsDouble;
            _frameSum = _framePeak = 0;
            _tickSum = _tickPeak = 0;
            _applySum = _applyPeak = 0;
            _meshSum = _meshPeak = 0;
            _lightSum = _lightPeak = 0;
            _gcSum = 0;
        }

        /// <summary>
        /// Records one frame's metrics into the active phase. Call once per frame, <b>after</b> <c>World.Update</c>
        /// has run (so <see cref="WorldFrameProfiler"/>'s published per-frame values are current).
        /// </summary>
        public void SampleFrame()
        {
            if (!_active) return;

            _frames++;

            double frameMs = Time.unscaledDeltaTime * 1000.0;
            Accumulate(frameMs, ref _frameSum, ref _framePeak);
            Accumulate(WorldFrameProfiler.LastFrameTickMs, ref _tickSum, ref _tickPeak);
            Accumulate(WorldFrameProfiler.LastFrameApplyMs, ref _applySum, ref _applyPeak);
            Accumulate(WorldFrameProfiler.LastFrameMeshMs, ref _meshSum, ref _meshPeak);
            Accumulate(WorldFrameProfiler.LastFrameLightMs, ref _lightSum, ref _lightPeak);

            if (PerformanceMonitor.Instance != null)
                _gcSum += PerformanceMonitor.Instance.GcAllocationPerFrame.GetAverage() / 1024.0;
        }

        /// <summary>Finalizes the active phase and appends its <see cref="PhaseResult"/>.</summary>
        public void EndPhase()
        {
            if (!_active) return;
            _active = false;

            double duration = Time.realtimeSinceStartupAsDouble - _startTime;
            int n = _frames;
            double inv = n > 0 ? 1.0 / n : 0;

            _phases.Add(new PhaseResult(
                _name, n, duration,
                _frameSum * inv, _framePeak,
                _tickSum * inv, _tickPeak,
                _applySum * inv, _applyPeak,
                _meshSum * inv, _meshPeak,
                _lightSum * inv, _lightPeak,
                _gcSum * inv));
        }

        /// <summary>Adds <paramref name="value"/> to a running sum and updates the peak.</summary>
        private static void Accumulate(double value, ref double sum, ref double peak)
        {
            sum += value;
            if (value > peak) peak = value;
        }
    }
}
