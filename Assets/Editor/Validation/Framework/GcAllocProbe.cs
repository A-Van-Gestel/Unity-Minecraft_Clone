using System;
using UnityEngine;
using UnityEngine.Profiling;

namespace Editor.Validation.Framework
{
    /// <summary>
    /// Counts managed allocations for zero-allocation baselines, through the profiler's <c>GC.Alloc</c> marker rather
    /// than <c>GC.GetAllocatedBytesForCurrentThread</c> or a heap-size delta: under Unity's Mono both read 0 for
    /// garbage that was never retained. Pair every zero assertion with a control that must allocate
    /// (<see cref="IsLive"/>), so a probe that sees nothing cannot pass as a clean reading.
    /// </summary>
    public static class GcAllocProbe
    {
        /// <summary>Counts the managed allocations an action makes on this thread.</summary>
        /// <param name="action">The work to measure, created before the probe starts.</param>
        /// <returns>The number of <c>GC.Alloc</c> samples recorded.</returns>
        public static int Count(Action action)
        {
            Recorder recorder = Recorder.Get("GC.Alloc");
            recorder.enabled = false;
            recorder.FilterToCurrentThread();
            recorder.enabled = true;
            try
            {
                action();
            }
            finally
            {
                recorder.enabled = false;
                recorder.CollectFromAllThreads();
            }

            return recorder.sampleBlockCount;
        }

        /// <summary>Whether the probe saw its control allocate, so a zero reading after it means something.</summary>
        /// <param name="scenario">Scenario name for the log line.</param>
        /// <param name="controlLabel">What the control did, for the assertion text.</param>
        /// <param name="controlAllocations">GC.Alloc samples the control recorded.</param>
        /// <param name="ok">The scenario's running verdict, failed when an interactive probe is silent.</param>
        /// <returns>True when the zero-allocation assertion that follows can be trusted.</returns>
        /// <remarks>
        /// An interactive Editor records the marker, so a silent probe there is a broken probe and fails. A batch run
        /// may not record it; there the zero-allocation half reports INCONCLUSIVE, as the render suites do under
        /// <c>-nographics</c>, rather than failing a headless run over a measurement it cannot take.
        /// </remarks>
        public static bool IsLive(string scenario, string controlLabel, int controlAllocations, ref bool ok)
        {
            if (controlAllocations == 0 && Application.isBatchMode)
            {
                Debug.LogWarning($"  [INCONCLUSIVE] {scenario}: the GC.Alloc marker recorded nothing in batch mode — " +
                                 "allocations cannot be observed here.");
                return false;
            }

            bool live = controlAllocations > 0;
            string label = $"the probe sees {controlLabel} allocate (got {controlAllocations})";
            if (live) Debug.Log($"  [PASS] {label}");
            else Debug.LogError($"  [FAIL] {label}");

            ok &= live;
            return live;
        }
    }
}
