using System;
using System.Diagnostics;
using System.Text;
using Helpers.UI;
using TMPro;
using UnityEngine;
using UnityEngine.Profiling;

namespace Benchmarks
{
    /// <summary>
    /// Lightweight runtime overlay that displays live benchmark progress and performance metrics.
    /// Updates on a configurable timer (default 0.2 s) following the <c>DebugScreen</c> pattern:
    /// pre-allocated <see cref="StringBuilder"/> is <c>.Clear()</c>'d and reused each cycle,
    /// producing only a single small string allocation per update from <c>.ToString()</c>.
    /// </summary>
    public class BenchmarkHUD : MonoBehaviour
    {
        private const float DEFAULT_UPDATE_RATE = 0.2f;
        private const int PROGRESS_BAR_WIDTH = 20;
        private static readonly double s_tickToMs = 1000.0 / Stopwatch.Frequency;

        private BenchmarkController _controller;
        private TextMeshProUGUI _statusText;
        private readonly StringBuilder _sb = new StringBuilder(512);
        private float _updateTimer;
        private const float UPDATE_RATE = DEFAULT_UPDATE_RATE;

        /// <summary>
        /// Initializes the HUD with its data source and display target.
        /// </summary>
        /// <param name="controller">The benchmark controller to read live state from.</param>
        /// <param name="statusText">The TMP component to write formatted text into.</param>
        public void Initialize(BenchmarkController controller, TextMeshProUGUI statusText)
        {
            _controller = controller;
            _statusText = statusText;
        }

        private void Update()
        {
            if (_controller == null || _statusText == null || !_controller.IsRunning) return;

            _updateTimer += Time.unscaledDeltaTime;
            if (_updateTimer < UPDATE_RATE) return;
            _updateTimer = 0f;

            _sb.Clear();

            // Line 1: Overall benchmark progress
            float overall = _controller.OverallProgress;
            if (overall >= 0f)
            {
                _sb.Append("Overall ");
                AppendProgressBar(_sb, overall);
                _sb.Append(' ');
                _sb.Append((int)(overall * 100f));
                _sb.Append('%');
            }
            else
            {
                _sb.Append("Overall [  initializing...  ]");
            }

            _sb.Append("    <mspace=0.55em>");
            _sb.AppendElapsedTime(_controller.ElapsedSeconds);
            _sb.Append("</mspace>");
            _sb.AppendLine();

            // Line 2: Current pass and phase
            _sb.Append(_controller.CurrentGroupName ?? "—");
            if (!string.IsNullOrEmpty(_controller.CurrentPhaseName))
            {
                _sb.Append(" — ");
                _sb.Append(_controller.CurrentPhaseName);
            }

            _sb.AppendLine();

            // Line 3: Phase progress bar
            float progress = _controller.Progress;
            if (progress < 0f)
            {
                _sb.Append("[  settling...  ]");
            }
            else
            {
                AppendProgressBar(_sb, progress);
                _sb.Append(' ');
                int pct = (int)(progress * 100f);
                _sb.Append(pct);
                _sb.Append('%');
            }

            _sb.AppendLine();

            // Line 4: Live performance metrics (monospace for stable alignment)
            if (PerformanceMonitor.Instance != null)
            {
                PerformanceMonitor pm = PerformanceMonitor.Instance;
                double cpuMs = pm.CpuFrameTime.GetAverage() * s_tickToMs;
                double wallMs = pm.WallFrameTime.GetAverage() * s_tickToMs;
                double gcKb = pm.GcAllocationPerFrame.GetAverage() / 1024.0;

                _sb.Append("<mspace=0.55em>");
                _sb.Append("CPU: ");
                _sb.AppendFixedPadded(cpuMs, 1, 6);
                _sb.Append(" ms | Wall: ");
                _sb.AppendFixedPadded(wallMs, 1, 6);
                _sb.Append(" ms | GC: ");
                _sb.AppendFixedPadded(gcKb, 1, 8);
                _sb.Append(" KB");
                _sb.Append("</mspace>");
                _sb.AppendLine();

                // Line 5: FPS and Memory
                int wallFps = Mathf.RoundToInt(pm.WallFPS);
                int cpuFps = Mathf.RoundToInt(pm.CpuFPS);
                double nativeMb = Profiler.GetTotalAllocatedMemoryLong() / (1024.0 * 1024.0);
                double managedMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
                double totalMb = nativeMb + managedMb;

                _sb.Append("<mspace=0.55em>");
                _sb.Append("FPS: ");
                _sb.AppendIntPadded(wallFps, 3);
                _sb.Append(" (CPU: ");
                _sb.AppendIntPadded(cpuFps, 3);
                _sb.Append(") | Mem: ");
                _sb.AppendFixedPadded(totalMb, 1, 6);
                _sb.Append(" MB (Nat: ");
                _sb.AppendFixedPadded(nativeMb, 1, 6);
                _sb.Append(" + Man: ");
                _sb.AppendFixedPadded(managedMb, 1, 6);
                _sb.Append(")");
                _sb.Append("</mspace>");
            }

            _statusText.text = _sb.ToString();
        }

        /// <summary>
        /// Appends a text-based progress bar like <c>[========&gt;           ]</c>.
        /// </summary>
        private static void AppendProgressBar(StringBuilder sb, float progress)
        {
            int filled = (int)(progress * PROGRESS_BAR_WIDTH);
            if (filled > PROGRESS_BAR_WIDTH) filled = PROGRESS_BAR_WIDTH;

            sb.Append('[');
            for (int i = 0; i < PROGRESS_BAR_WIDTH; i++)
            {
                if (i < filled) sb.Append('=');
                else if (i == filled) sb.Append('>');
                else sb.Append(' ');
            }

            sb.Append(']');
        }
    }
}
