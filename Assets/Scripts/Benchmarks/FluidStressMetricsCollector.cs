using System;
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
    /// <c>World.Update</c> interior). Both are exact per frame, so avg <b>and</b> peak are meaningful.
    /// </para>
    /// <para>
    /// <b>Two distinct kinds of "peak" are tracked, because conflating them misattributes the frame:</b>
    /// <list type="bullet">
    /// <item>the <b>independent</b> per-metric maxima (<c>*MsPeak</c>) — the largest each sub-phase ever reached,
    /// which need NOT share a frame; these are spike <i>magnitudes</i>; and</item>
    /// <item>the <b>composition of the single worst whole-frame</b> (<c>PeakFrame*Ms</c>) — the Tick/Apply/Mesh/Light
    /// of the one frame with the maximum <see cref="PhaseResult.FrameMsPeak"/>. Only these substantiate a
    /// "<i>X % of the worst frame</i>" statement, because they are one real frame.</item>
    /// </list>
    /// GC/frame is a true per-frame managed-allocation delta (<see cref="GC.GetTotalMemory(bool)"/>, the same
    /// IL2CPP-valid method <see cref="PerformanceMonitor"/> uses), tracked as avg + peak — <b>not</b> a smoothed
    /// moving average, so a per-frame allocation spike is not averaged away.
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

            /// <summary>Average behavior-tick / modification-drain / mesh / lighting time (ms) over all frames.</summary>
            public readonly double TickMsAvg, ApplyMsAvg, MeshMsAvg, LightMsAvg;

            /// <summary>
            /// <b>Independent</b> per-metric maxima (ms): the largest each sub-phase reached across all frames.
            /// These are spike <i>magnitudes</i> and need NOT come from the same frame as each other or as
            /// <see cref="FrameMsPeak"/> — do not read them as one frame's composition (use <c>PeakFrame*Ms</c> for that).
            /// </summary>
            public readonly double TickMsPeak, ApplyMsPeak, MeshMsPeak, LightMsPeak;

            /// <summary>
            /// Composition of the single worst whole-frame — the Tick/Apply/Mesh/Light of the frame that set
            /// <see cref="FrameMsPeak"/>. This is ONE real frame, so a "X % of the worst frame" claim is
            /// substantiated only by these fields.
            /// </summary>
            public readonly double PeakFrameTickMs, PeakFrameApplyMs, PeakFrameMeshMs, PeakFrameLightMs;

            /// <summary>True per-frame managed GC allocation (KB): average / peak, measured as a per-frame delta (not smoothed).</summary>
            public readonly double GcKbAvg, GcKbPeak;

            /// <summary>Constructs a phase result.</summary>
            public PhaseResult(string name, int frames, double durationSeconds,
                double frameMsAvg, double frameMsPeak,
                double tickMsAvg, double tickMsPeak,
                double applyMsAvg, double applyMsPeak,
                double meshMsAvg, double meshMsPeak,
                double lightMsAvg, double lightMsPeak,
                double peakFrameTickMs, double peakFrameApplyMs, double peakFrameMeshMs, double peakFrameLightMs,
                double gcKbAvg, double gcKbPeak)
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
                PeakFrameTickMs = peakFrameTickMs;
                PeakFrameApplyMs = peakFrameApplyMs;
                PeakFrameMeshMs = peakFrameMeshMs;
                PeakFrameLightMs = peakFrameLightMs;
                GcKbAvg = gcKbAvg;
                GcKbPeak = gcKbPeak;
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
        private double _peakFrameTick, _peakFrameApply, _peakFrameMesh, _peakFrameLight;
        private double _gcSum, _gcPeak;
        private long _lastGcMemory;

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
            _peakFrameTick = _peakFrameApply = _peakFrameMesh = _peakFrameLight = 0;
            _gcSum = _gcPeak = 0;

            // Re-baseline the GC counter so the first sampled frame's delta is this phase's allocation, not the
            // backlog accumulated since the previous phase / startup.
            _lastGcMemory = GC.GetTotalMemory(false);
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
            double tickMs = WorldFrameProfiler.LastFrameTickMs;
            double applyMs = WorldFrameProfiler.LastFrameApplyMs;
            double meshMs = WorldFrameProfiler.LastFrameMeshMs;
            double lightMs = WorldFrameProfiler.LastFrameLightMs;

            // Capture the worst whole-frame's composition BEFORE updating _framePeak, so the snapshot is the
            // breakdown of the frame that sets the new maximum — keeping the worst-frame attribution to one real frame.
            if (frameMs > _framePeak)
            {
                _peakFrameTick = tickMs;
                _peakFrameApply = applyMs;
                _peakFrameMesh = meshMs;
                _peakFrameLight = lightMs;
            }

            Accumulate(frameMs, ref _frameSum, ref _framePeak);
            Accumulate(tickMs, ref _tickSum, ref _tickPeak);
            Accumulate(applyMs, ref _applySum, ref _applyPeak);
            Accumulate(meshMs, ref _meshSum, ref _meshPeak);
            Accumulate(lightMs, ref _lightSum, ref _lightPeak);

            // True per-frame managed GC allocation: the GC.GetTotalMemory delta since the previous frame (the
            // method PerformanceMonitor uses — IL2CPP-valid), clamped at 0 across collections. NOT a moving
            // average, so per-frame allocation peaks survive into GcKbPeak.
            long currentGc = GC.GetTotalMemory(false);
            double gcKb = Math.Max(0L, currentGc - _lastGcMemory) / 1024.0;
            _lastGcMemory = currentGc;
            Accumulate(gcKb, ref _gcSum, ref _gcPeak);
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
                _peakFrameTick, _peakFrameApply, _peakFrameMesh, _peakFrameLight,
                _gcSum * inv, _gcPeak));
        }

        /// <summary>Adds <paramref name="value"/> to a running sum and updates the peak.</summary>
        private static void Accumulate(double value, ref double sum, ref double peak)
        {
            sum += value;
            if (value > peak) peak = value;
        }
    }
}
