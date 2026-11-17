using System;
using Unity.Profiling;
using Unity.Scripting.LifecycleManagement;

namespace Diagnostics
{
    /// <summary>
    /// Reads Unity's <c>Render</c>-category counters (draw calls, SetPass calls, geometry, buffer uploads) into the
    /// performance monitor's render gauges; created and disposed by <see cref="PerfStore"/> with the Systems tier.
    /// <para>Main thread only; dispose to stop the recorders.</para>
    /// </summary>
    public sealed class PerfRenderStatsSource : IDisposable
    {
        /// <summary>One recorded counter: the Unity counter name and the gauge its value lands in.</summary>
        public readonly struct Binding
        {
            /// <summary>The Unity <c>Render</c>-category counter name.</summary>
            public readonly string CounterName;

            /// <summary>The gauge that receives the counter's last value.</summary>
            public readonly PerfCounter Gauge;

            /// <summary>Pairs a Unity counter with its gauge.</summary>
            /// <param name="counterName">The Unity <c>Render</c>-category counter name.</param>
            /// <param name="gauge">The gauge that receives its value.</param>
            public Binding(string counterName, PerfCounter gauge)
            {
                CounterName = counterName;
                Gauge = gauge;
            }
        }

        private const int RECORDER_CAPACITY = 1;

        /// <summary>Every recorded counter with its gauge; the one place the pairing is written down.</summary>
        [NoAutoStaticsCleanup] // immutable table
        public static readonly Binding[] Bindings =
        {
            new Binding("SRP Batcher Draw Calls Count", PerfCounter.RenderSrpBatcherDraws),
            new Binding("Standard Draw Calls Count", PerfCounter.RenderStandardDraws),
            new Binding("SetPass Calls Count", PerfCounter.RenderSetPassCalls),
            new Binding("Triangles Count", PerfCounter.RenderTriangles),
            new Binding("Vertices Count", PerfCounter.RenderVertices),
            new Binding("Vertex Buffer Upload In Frame Bytes", PerfCounter.RenderVertexUploadBytes),
            new Binding("Index Buffer Upload In Frame Bytes", PerfCounter.RenderIndexUploadBytes),
        };

        private readonly ProfilerRecorder[] _recorders = new ProfilerRecorder[Bindings.Length];
        private bool _isDisposed;

        /// <summary>Starts one recorder per <see cref="Bindings"/> row.</summary>
        public PerfRenderStatsSource()
        {
            for (int i = 0; i < Bindings.Length; i++)
                _recorders[i] = ProfilerRecorder.StartNew(ProfilerCategory.Render, Bindings[i].CounterName, RECORDER_CAPACITY);
        }

        /// <summary>Whether <see cref="Dispose"/> has run.</summary>
        public bool IsDisposed => _isDisposed;

        /// <summary>Whether a row's recorder is attached to an existing Unity counter.</summary>
        /// <param name="row">An index into <see cref="Bindings"/>.</param>
        /// <returns>True when the counter exists in this build.</returns>
        public bool IsRecorderValid(int row) => !_isDisposed && _recorders[row].Valid;

        /// <summary>Writes each counter's last value — the previous rendered frame's — into its gauge.</summary>
        public void Sample()
        {
            if (_isDisposed) return;

            for (int i = 0; i < Bindings.Length; i++)
                PerfStore.SetGauge(Bindings[i].Gauge, ClampToGauge(_recorders[i].LastValue));
        }

        /// <summary>Fits a counter reading into a gauge's <c>int</c>, saturating instead of wrapping.</summary>
        /// <param name="value">The counter reading.</param>
        /// <returns>The reading clamped to 0 … <see cref="int.MaxValue"/>.</returns>
        public static int ClampToGauge(long value) => value <= 0 ? 0 : (int)Math.Min(value, int.MaxValue);

        /// <summary>Stops the recorders. Safe to call more than once.</summary>
        public void Dispose()
        {
            if (_isDisposed) return;

            for (int i = 0; i < _recorders.Length; i++)
                _recorders[i].Dispose();
            _isDisposed = true;
        }
    }
}
