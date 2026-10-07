using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Helpers.UI;
using TMPro;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Editor.Benchmarking
{
    /// <summary>
    /// Editor allocation check of the debug HUD's graph path: <see cref="GraphRenderer.AddSamples"/> on an instance
    /// of the real graph prefab, with its TMP axis labels, in a preview scene. Reports nanoseconds and managed bytes
    /// per call, next to a control that allocates (<see cref="StringBuilder.Append(int)"/>, which builds a string on
    /// Unity's Mono).
    /// Also checks the Y-axis label text fresh graphs show for a known sample, across formats that take the
    /// allocation-free path and formats that must fall back to <c>string.Format</c>.
    /// In the Editor every TMP <c>SetText</c> overload also builds the label's string (<c>#if UNITY_EDITOR</c> in
    /// <c>TMP_Text</c>), so a changed label always allocates there and never in a player. The floor case measures
    /// that cost alone: a rising run allocating the floor and no more is allocation-free in a player.
    /// Judge the second run after a recompile: the first one carries the reloaded domain's one-time initialization.
    /// Run from <b>Minecraft Clone → Benchmarks → Debug HUD Allocations</b>.
    /// </summary>
    public static class DebugHudAllocationBenchmark
    {
        private const int ITERATIONS = 100_000;
        private const int WARMUP_ITERATIONS = 1_000;
        private const double NANOSECONDS_PER_MILLISECOND = 1e6;

        /// <summary>The Profiler counter of managed bytes allocated in the current frame; a menu call spans one frame.</summary>
        private const string GC_ALLOCATED_COUNTER = "GC Allocated In Frame";

        private const string GRAPH_PREFAB_PATH = "Assets/Prefabs/Debug/Graph Renderer.prefab";

        /// <summary>PerformanceMonitor's default history: 10 s at a 0.05 s poll rate.</summary>
        private const int HISTORY_SIZE = 200;

        /// <summary>Rise per call, large enough that every Y label's one-decimal value changes on every call.</summary>
        private const float RISING_STEP = 1f;

        /// <summary>A spike every two windows, so the axis keeps snapping up and then decaying back.</summary>
        private const int SPIKE_PERIOD = HISTORY_SIZE * 2;

        private const float SPIKE_VALUE = 1000f;
        private const float BASELINE_VALUE = 1f;
        private const int CONTROL_VALUE = 12345;

        /// <summary>A sample whose labels are known: the axis ceiling is the peak plus 10 % headroom.</summary>
        private const float LABEL_CHECK_SAMPLE = 12.34f;

        private const float AXIS_HEADROOM = 1.1f;

        /// <summary>
        /// Y-axis formats the label check renders and compares with invariant <see cref="string.Format(IFormatProvider, string, object)"/>.
        /// The first four take <see cref="GraphRenderer"/>'s allocation-free path (the prefab's own format first);
        /// the last four fall back to <c>string.Format</c>, logging one expected yFormat warning each.
        /// </summary>
        private static readonly string[] s_checkedYFormats =
        {
            "{0:F1} ms", "{0:F0}", "Peak {0:F3} KB", "{0:F6}",
            "{{0:F1}}", "{0:F1} }}", "{0:F7}", "{0:N1}",
        };

        /// <summary>Runs every measurement and the label check, logging one line per case.</summary>
        [MenuItem("Minecraft Clone/Benchmarks/Debug HUD Allocations")]
        public static void RunBenchmark()
        {
            GraphRenderer prefab = AssetDatabase.LoadAssetAtPath<GraphRenderer>(GRAPH_PREFAB_PATH);
            if (prefab == null)
            {
                Debug.LogError($"[BENCHMARK] Debug HUD Allocations: no GraphRenderer at {GRAPH_PREFAB_PATH}.");
                return;
            }

            float[] samples = new float[2];
            StringBuilder controlBuilder = new StringBuilder();
            Scene scene = EditorSceneManager.NewPreviewScene();
            ProfilerRecorder allocated = ProfilerRecorder.StartNew(ProfilerCategory.Memory, GC_ALLOCATED_COUNTER);
            try
            {
                GameObject canvas = new GameObject("Benchmark Canvas", typeof(RectTransform), typeof(Canvas));
                SceneManager.MoveGameObjectToScene(canvas, scene);

                GraphRenderer graph = CreateGraph(prefab, canvas.transform);
                float rising = 0f;
                int call = 0;
                Report("GraphRenderer.AddSamples, rising (every Y label changes)", allocated, n =>
                {
                    for (int i = 0; i < n; i++)
                    {
                        rising += RISING_STEP;
                        samples[0] = rising;
                        samples[1] = rising * 0.5f;
                        graph.AddSamples(samples);
                    }
                });
                Report("GraphRenderer.AddSamples, spike and decay", allocated, n =>
                {
                    for (int i = 0; i < n; i++)
                    {
                        float value = call++ % SPIKE_PERIOD == 0 ? SPIKE_VALUE : BASELINE_VALUE;
                        samples[0] = value;
                        samples[1] = value;
                        graph.AddSamples(samples);
                    }
                });
                Report("GraphRenderer.AddSamples, steady", allocated, n =>
                {
                    samples[0] = BASELINE_VALUE;
                    samples[1] = BASELINE_VALUE;
                    for (int i = 0; i < n; i++)
                        graph.AddSamples(samples);
                });
                TMP_Text floorLabel = FindLabelObject(graph, "Y-Axis 0");
                float floorValue = 0f;
                Report($"TMP SetText(StringBuilder) alone, changing text x{graph.gridLineCount + 1} (Editor floor)", allocated, n =>
                {
                    for (int i = 0; i < n; i++)
                    {
                        for (int label = 0; label <= graph.gridLineCount; label++)
                        {
                            floorValue += RISING_STEP;
                            controlBuilder.Clear();
                            controlBuilder.AppendFixed(floorValue, 1).Append(" ms");
                            floorLabel.SetText(controlBuilder);
                        }
                    }
                });
                Report("Control: StringBuilder.Append(int) (allocates)", allocated, n =>
                {
                    for (int i = 0; i < n; i++)
                    {
                        controlBuilder.Clear();
                        controlBuilder.Append(CONTROL_VALUE);
                    }
                });

                CheckLabels(prefab, canvas.transform, samples);
            }
            finally
            {
                allocated.Dispose();
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static GraphRenderer CreateGraph(GraphRenderer prefab, Transform parent, string yFormat = null)
        {
            GraphRenderer graph = Object.Instantiate(prefab, parent);
            if (yFormat != null)
                graph.yFormat = yFormat;

            graph.Initialize(new GraphRenderer.GraphConfig
            {
                Lines = new[]
                {
                    new GraphRenderer.LineEntry { Color = Color.cyan, Name = "A" },
                    new GraphRenderer.LineEntry { Color = Color.red, Name = "B" },
                },
                HistorySize = HISTORY_SIZE,
            });
            return graph;
        }

        /// <summary>
        /// Feeds a fresh graph per format in <see cref="s_checkedYFormats"/> one known sample and compares every Y
        /// label with invariant <c>string.Format</c>, so a label path that stopped writing, wrote the wrong value, or
        /// parsed a format it cannot render shows as a mismatch rather than as an allocation-free pass. A mismatch
        /// logs an error.
        /// </summary>
        private static void CheckLabels(GraphRenderer prefab, Transform parent, float[] samples)
        {
            samples[0] = LABEL_CHECK_SAMPLE;
            samples[1] = 0f;
            float ceiling = LABEL_CHECK_SAMPLE * AXIS_HEADROOM;
            int mismatches = 0;
            StringBuilder report = new StringBuilder();
            foreach (string format in s_checkedYFormats)
            {
                GraphRenderer graph = CreateGraph(prefab, parent, format);
                graph.AddSamples(samples);

                int gridLabels = graph.gridLineCount + 1;
                for (int index = 0; index < gridLabels; index++)
                {
                    float fraction = (float)(index + 1) / gridLabels;
                    string expected = string.Format(CultureInfo.InvariantCulture, format, ceiling * fraction);
                    string actual = FindLabel(graph, $"Y-Axis {index}");
                    if (actual == expected) continue;

                    mismatches++;
                    report.Append($" \"{format}\" Y-Axis {index} = '{actual}' (expected '{expected}');");
                }
            }

            if (mismatches == 0)
                Debug.Log($"[BENCHMARK] Label check PASS: every Y label of {s_checkedYFormats.Length} formats matches string.Format.");
            else
                Debug.LogError($"[BENCHMARK] Label check FAIL: {mismatches} Y labels differ from string.Format:{report}");
        }

        private static string FindLabel(GraphRenderer graph, string name)
        {
            TMP_Text label = FindLabelObject(graph, name);
            return label != null ? label.text : "<missing>";
        }

        private static TMP_Text FindLabelObject(GraphRenderer graph, string name)
        {
            foreach (TMP_Text label in graph.GetComponentsInChildren<TMP_Text>(true))
            {
                if (label.gameObject.name == name) return label;
            }

            return null;
        }

        /// <summary>
        /// Warms the case up, then times <see cref="ITERATIONS"/> calls and logs nanoseconds and managed bytes per call.
        /// The bytes come from the Profiler's GC allocation counter, which counts every allocation as it happens: the
        /// heap size from <see cref="GC.GetTotalMemory"/> misses allocations that reuse just-freed memory.
        /// </summary>
        private static void Report(string label, ProfilerRecorder allocated, Action<int> run)
        {
            run(WARMUP_ITERATIONS);

            long allocatedBefore = allocated.CurrentValue;
            Stopwatch stopwatch = Stopwatch.StartNew();
            run(ITERATIONS);
            stopwatch.Stop();
            double bytesPerCall = (allocated.CurrentValue - allocatedBefore) / (double)ITERATIONS;

            double nsPerCall = stopwatch.Elapsed.TotalMilliseconds * NANOSECONDS_PER_MILLISECOND / ITERATIONS;
            Debug.Log($"[BENCHMARK] {label}: {nsPerCall:F0} ns/call, {bytesPerCall:F1} B/call");
        }
    }
}
