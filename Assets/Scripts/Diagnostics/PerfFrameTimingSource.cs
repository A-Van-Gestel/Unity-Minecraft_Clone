using System;
using Unity.Profiling;
using UnityEngine;

namespace Diagnostics
{
    /// <summary>
    /// Reads the latest <see cref="FrameTimingManager"/> timings once per frame; created and disposed by
    /// <see cref="PerfStore"/> with the Frame tier. While it exists it also records the frame-time counters
    /// (<c>GPU Frame Time</c>, <c>CPU Main Thread Frame Time</c>), so that a player with the project's frame-timing
    /// stats setting off still collects timings on demand: the setting stays off, and sessions that never ask for
    /// timings never collect them.
    /// <para>Main thread only; dispose to stop the recorders.</para>
    /// </summary>
    public sealed class PerfFrameTimingSource : IDisposable
    {
        /// <summary>Timings requested per read: several frames can complete between two reads.</summary>
        public const int LatestTimingCount = 4;

        private const string GPU_FRAME_TIME_COUNTER = "GPU Frame Time";
        private const string MAIN_THREAD_FRAME_TIME_COUNTER = "CPU Main Thread Frame Time";
        private const int RECORDER_CAPACITY = 1;

        private readonly FrameTiming[] _timings = new FrameTiming[LatestTimingCount];
        private ProfilerRecorder _gpuRecorder;
        private ProfilerRecorder _mainThreadRecorder;
        private bool _isDisposed;

        /// <summary>Starts the frame-time recorders.</summary>
        public PerfFrameTimingSource()
        {
            _gpuRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, GPU_FRAME_TIME_COUNTER, RECORDER_CAPACITY);
            _mainThreadRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, MAIN_THREAD_FRAME_TIME_COUNTER, RECORDER_CAPACITY);
        }

        /// <summary>Whether <see cref="Dispose"/> has run.</summary>
        public bool IsDisposed => _isDisposed;

        /// <summary>Captures the latest frame timings, read back with <see cref="GetTiming"/>.</summary>
        /// <returns>How many timings are available, newest first; 0 when none.</returns>
        public int Capture()
        {
            if (_isDisposed) return 0;

            FrameTimingManager.CaptureFrameTimings();
            return (int)FrameTimingManager.GetLatestTimings(LatestTimingCount, _timings);
        }

        /// <summary>Reads a timing from the last <see cref="Capture"/>.</summary>
        /// <param name="index">0 for the newest, below the count <see cref="Capture"/> returned.</param>
        /// <returns>The timing.</returns>
        public FrameTiming GetTiming(int index) => _timings[index];

        /// <summary>Stops the recorders. Safe to call more than once.</summary>
        public void Dispose()
        {
            if (_isDisposed) return;

            _gpuRecorder.Dispose();
            _mainThreadRecorder.Dispose();
            _isDisposed = true;
        }
    }
}
