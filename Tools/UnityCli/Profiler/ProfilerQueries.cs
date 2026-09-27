// Profiler queries for agents, run through the Unity CLI against a live Editor:
//
//   unity command run_script --file Tools/UnityCli/Profiler/ProfilerQueries.cs \
//       --entry UnityCli.ProfilerQueries.<Method> --args '[...]' --result-only
//
// Every parameter must be passed in --args: run_script does not apply C# default values.
// Lives outside Assets/ on purpose: neither Unity nor dotnet build compiles it, and editing it
// never triggers an import or domain reload. run_script compiles it in memory on every call.
// Replaces the Unity_Profiler_* MCP tools (Documentation/Architecture/UNITY_CLI_EDITOR_BRIDGE.md §6).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor.Profiling;
using UnityEditorInternal;

namespace UnityCli
{
    /// <summary>
    /// Read-only queries over the frames currently held by the Editor Profiler, whether recorded live
    /// or loaded from a <c>.data</c> capture. Every entry returns a compact plain-text report so an
    /// agent reads one short string instead of a JSON tree. Frame arguments of <c>-1</c> mean
    /// "first/last available frame". Single-frame queries take a thread <b>index</b> from
    /// <see cref="Threads"/> for that frame (0 is always the main thread); range queries take a thread
    /// <b>name</b>, because the other threads' indices change from frame to frame.
    /// </summary>
    public static class ProfilerQueries
    {
        private const int MAIN_THREAD_INDEX = 0;
        private const string MAIN_THREAD_NAME = "Main Thread";
        private const int ALL_FRAMES = -1;
        private const string GC_ALLOC_MARKER = "GC.Alloc";
        private const int PATH_TAIL_SEGMENTS = 2;
        private const int WORST_FRAMES_SHOWN = 5;
        private const double PERCENTILE_95 = 0.95;
        private const double BYTES_PER_KB = 1024.0;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Reports whether frames are loaded, the frame range, and the last frame's time.</summary>
        /// <returns>A one-block status report.</returns>
        public static string Status()
        {
            var sb = new StringBuilder();
            int first = ProfilerDriver.firstFrameIndex;
            int last = ProfilerDriver.lastFrameIndex;
            sb.Append("recording=").Append(ProfilerDriver.enabled)
              .Append(" firstFrame=").Append(first)
              .Append(" lastFrame=").Append(last);
            if (last < 0)
                return sb.Append(" | no frames: record in play mode or call Load(path)").ToString();

            sb.Append(" frames=").Append(last - first + 1);
            using (HierarchyFrameDataView view = OpenView(last, MAIN_THREAD_INDEX))
            {
                if (view.valid)
                    sb.Append(" lastFrameCpuMs=").Append(Ms(view.frameTimeMs));
            }

            return sb.ToString();
        }

        /// <summary>Replaces the Profiler's frames with a saved capture.</summary>
        /// <param name="path">A <c>.data</c> file; relative paths resolve against the project root.</param>
        /// <returns>The <see cref="Status"/> report after loading, or the failure reason.</returns>
        public static string Load(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                return "not found: " + fullPath;

            return ProfilerDriver.LoadProfile(fullPath, false)
                ? "loaded " + Path.GetFileName(fullPath) + " | " + Status()
                : "LoadProfile failed: " + fullPath;
        }

        /// <summary>Drops every frame the Profiler holds, e.g. after inspecting a loaded capture.</summary>
        /// <returns>The <see cref="Status"/> report after clearing.</returns>
        public static string Clear()
        {
            ProfilerDriver.ClearAllFrames();
            return "cleared | " + Status();
        }

        /// <summary>Lists the thread indices and names captured in one frame.</summary>
        /// <param name="frame">Frame index, or -1 for the last frame.</param>
        /// <returns>One line per thread, with consecutive identically named threads collapsed to a range.</returns>
        public static string Threads(int frame)
        {
            if (!TryResolveFrame(ref frame, out string error))
                return error;

            var sb = new StringBuilder();
            sb.Append("frame ").Append(frame).Append(" threads:\n");
            string runLabel = null;
            int runStart = 0;
            int threadIndex = 0;
            for (;; threadIndex++)
            {
                string label;
                using (RawFrameDataView raw = ProfilerDriver.GetRawFrameDataView(frame, threadIndex))
                {
                    if (!raw.valid)
                        break;

                    label = string.IsNullOrEmpty(raw.threadGroupName) ? raw.threadName : raw.threadGroupName + " / " + raw.threadName;
                }

                if (label == runLabel)
                    continue;

                AppendThreadRun(sb, runLabel, runStart, threadIndex - 1);
                runLabel = label;
                runStart = threadIndex;
            }

            AppendThreadRun(sb, runLabel, runStart, threadIndex - 1);
            return sb.ToString();
        }

        /// <summary>
        /// Sums managed allocations per allocating sample over a frame range. A <c>GC.Alloc</c> sample is
        /// credited to its parent, which is the code that allocated.
        /// </summary>
        /// <param name="firstFrame">First frame, or -1 for the first available.</param>
        /// <param name="lastFrame">Last frame, or -1 for the last available.</param>
        /// <param name="top">Number of allocators to list.</param>
        /// <param name="threadName">Thread name as <see cref="Threads"/> prints it ("Main Thread", "Job / Worker 0").</param>
        /// <returns>Total bytes, allocating-frame count and the top allocators.</returns>
        public static string OverallGc(int firstFrame, int lastFrame, int top, string threadName)
        {
            if (!TryResolveRange(ref firstFrame, ref lastFrame, out string error))
                return error;

            var bytesByAllocator = new Dictionary<string, double>();
            var framesByAllocator = new Dictionary<string, int>();
            var seenThisFrame = new HashSet<string>();
            double totalBytes = 0;
            int allocatingFrames = 0;
            int validFrames = 0;

            for (int frame = firstFrame; frame <= lastFrame; frame++)
            {
                int thread = FindThreadIndex(frame, threadName);
                if (thread < 0)
                    continue;

                using (HierarchyFrameDataView view = OpenView(frame, thread))
                {
                    if (!view.valid)
                        continue;

                    validFrames++;
                    seenThisFrame.Clear();
                    double frameBytes = 0;
                    Walk(view, (item, parent, depth) =>
                    {
                        double selfBytes = SelfGcBytes(view, item);
                        if (selfBytes <= 0)
                            return;

                        string allocator = AllocatorName(view, item, parent);
                        Accumulate(bytesByAllocator, allocator, selfBytes);
                        if (seenThisFrame.Add(allocator))
                            framesByAllocator[allocator] = framesByAllocator.TryGetValue(allocator, out int n) ? n + 1 : 1;
                        frameBytes += selfBytes;
                    });

                    totalBytes += frameBytes;
                    if (frameBytes > 0)
                        allocatingFrames++;
                }
            }

            if (validFrames == 0)
                return "no frames in " + firstFrame + ".." + lastFrame + " have a thread named '" + threadName + "'";

            var sb = new StringBuilder();
            sb.Append("GC frames ").Append(firstFrame).Append("..").Append(lastFrame)
              .Append(" thread '").Append(threadName)
              .Append("': total=").Append(Kb(totalBytes))
              .Append(" allocatingFrames=").Append(allocatingFrames).Append('/').Append(validFrames).Append('\n');
            sb.Append("  totalKB    frames  avgKB/frame  allocator\n");
            foreach (KeyValuePair<string, double> entry in TopByValue(bytesByAllocator, top))
            {
                int frames = framesByAllocator[entry.Key];
                sb.Append("  ").Append(Kb(entry.Value).PadLeft(9))
                  .Append("  ").Append(frames.ToString(Inv).PadLeft(6))
                  .Append("  ").Append(Kb(entry.Value / frames).PadLeft(11))
                  .Append("  ").Append(entry.Key).Append('\n');
            }

            return sb.ToString();
        }

        /// <summary>Lists the samples with the highest total (inclusive) time in one frame.</summary>
        /// <param name="frame">Frame index, or -1 for the last frame.</param>
        /// <param name="top">Number of samples to list.</param>
        /// <param name="targetFrameMs">Frame budget the frame time is compared against (16.67 = 60 FPS).</param>
        /// <param name="thread">Thread index (0 = main thread).</param>
        /// <returns>Frame time versus budget, then total/self ms, calls and GC per sample with its path tail.</returns>
        public static string FrameTopTime(int frame, int top, float targetFrameMs, int thread)
        {
            if (!TryResolveFrame(ref frame, out string error))
                return error;

            using (HierarchyFrameDataView view = OpenView(frame, thread))
            {
                if (!view.valid)
                    return "no data for frame " + frame + " thread " + thread;

                var items = new List<KeyValuePair<int, double>>();
                Walk(view, (item, parent, depth) =>
                    items.Add(new KeyValuePair<int, double>(item, view.GetItemColumnDataAsDouble(item, HierarchyFrameDataView.columnTotalTime))));
                items.Sort((a, b) => b.Value.CompareTo(a.Value));

                var sb = new StringBuilder();
                AppendFrameHeader(sb, view, frame, thread, targetFrameMs);
                sb.Append("  totalMs   selfMs  calls      gcKB  sample\n");
                for (int i = 0; i < items.Count && i < top; i++)
                {
                    int item = items[i].Key;
                    sb.Append("  ").Append(Ms(items[i].Value).PadLeft(7))
                      .Append("  ").Append(Ms(view.GetItemColumnDataAsDouble(item, HierarchyFrameDataView.columnSelfTime)).PadLeft(7))
                      .Append("  ").Append(view.GetItemColumnData(item, HierarchyFrameDataView.columnCalls).PadLeft(5))
                      .Append("  ").Append(Kb(view.GetItemColumnDataAsDouble(item, HierarchyFrameDataView.columnGcMemory)).PadLeft(8))
                      .Append("  ").Append(PathTail(view.GetItemPath(item))).Append('\n');
                }

                return sb.ToString();
            }
        }

        /// <summary>
        /// Sums self (exclusive) time per sample name across one frame's whole hierarchy, which shows
        /// where the time is actually spent rather than which parent contains it.
        /// </summary>
        /// <param name="frame">Frame index, or -1 for the last frame.</param>
        /// <param name="top">Number of sample names to list.</param>
        /// <param name="thread">Thread index (0 = main thread).</param>
        /// <returns>Frame time, then self ms and share of the frame per sample name.</returns>
        public static string FrameSelfTime(int frame, int top, int thread)
        {
            if (!TryResolveFrame(ref frame, out string error))
                return error;

            using (HierarchyFrameDataView view = OpenView(frame, thread))
            {
                if (!view.valid)
                    return "no data for frame " + frame + " thread " + thread;

                var selfByName = new Dictionary<string, double>();
                Walk(view, (item, parent, depth) =>
                    Accumulate(selfByName, view.GetItemName(item), view.GetItemColumnDataAsDouble(item, HierarchyFrameDataView.columnSelfTime)));

                double frameMs = view.frameTimeMs;
                var sb = new StringBuilder();
                sb.Append("frame ").Append(frame).Append(" thread ").Append(thread)
                  .Append(" (").Append(ThreadLabel(view)).Append("): cpu=").Append(Ms(frameMs)).Append("ms\n");
                sb.Append("   selfMs  share  sample\n");
                foreach (KeyValuePair<string, double> entry in TopByValue(selfByName, top))
                {
                    sb.Append("  ").Append(Ms(entry.Value).PadLeft(7))
                      .Append("  ").Append(Percent(entry.Value, frameMs).PadLeft(5))
                      .Append("  ").Append(entry.Key).Append('\n');
                }

                return sb.ToString();
            }
        }

        /// <summary>
        /// Summarizes CPU frame time over a range and ranks sample names by average self time, to find
        /// sustained costs rather than single spikes. The frame-time statistics are the whole frame's,
        /// whichever thread is named; the sample ranking is that thread's.
        /// </summary>
        /// <param name="firstFrame">First frame, or -1 for the first available.</param>
        /// <param name="lastFrame">Last frame, or -1 for the last available.</param>
        /// <param name="targetFrameMs">Frame budget for the over-budget count (16.67 = 60 FPS).</param>
        /// <param name="top">Number of sample names to list.</param>
        /// <param name="threadName">Thread name as <see cref="Threads"/> prints it ("Main Thread", "Job / Worker 0").</param>
        /// <returns>Min/avg/p95/max frame time, over-budget count, worst frames and top self-time samples.</returns>
        public static string FrameRangeSummary(int firstFrame, int lastFrame, float targetFrameMs, int top, string threadName)
        {
            if (!TryResolveRange(ref firstFrame, ref lastFrame, out string error))
                return error;

            var frameTimes = new List<KeyValuePair<int, double>>();
            var selfByName = new Dictionary<string, double>();
            for (int frame = firstFrame; frame <= lastFrame; frame++)
            {
                int thread = FindThreadIndex(frame, threadName);
                if (thread < 0)
                    continue;

                using (HierarchyFrameDataView view = OpenView(frame, thread))
                {
                    if (!view.valid)
                        continue;

                    frameTimes.Add(new KeyValuePair<int, double>(frame, view.frameTimeMs));
                    Walk(view, (item, parent, depth) =>
                        Accumulate(selfByName, view.GetItemName(item), view.GetItemColumnDataAsDouble(item, HierarchyFrameDataView.columnSelfTime)));
                }
            }

            if (frameTimes.Count == 0)
                return "no frames in " + firstFrame + ".." + lastFrame + " have a thread named '" + threadName + "'";

            var sorted = new List<double>(frameTimes.Count);
            double sum = 0;
            int overBudget = 0;
            foreach (KeyValuePair<int, double> ft in frameTimes)
            {
                sorted.Add(ft.Value);
                sum += ft.Value;
                if (ft.Value > targetFrameMs)
                    overBudget++;
            }

            sorted.Sort();
            int count = sorted.Count;
            double p95 = sorted[Math.Min(count - 1, (int)Math.Ceiling(count * PERCENTILE_95) - 1)];
            frameTimes.Sort((a, b) => b.Value.CompareTo(a.Value));

            var sb = new StringBuilder();
            sb.Append("frames ").Append(firstFrame).Append("..").Append(lastFrame)
              .Append(" thread '").Append(threadName).Append("' (").Append(count).Append(" valid)\n");
            sb.Append("  frameMs min=").Append(Ms(sorted[0]))
              .Append(" avg=").Append(Ms(sum / count))
              .Append(" p95=").Append(Ms(p95))
              .Append(" max=").Append(Ms(sorted[count - 1]))
              .Append(" | over ").Append(Ms(targetFrameMs)).Append("ms: ").Append(overBudget).Append('/').Append(count).Append('\n');
            sb.Append("  worst frames:");
            for (int i = 0; i < frameTimes.Count && i < WORST_FRAMES_SHOWN; i++)
                sb.Append(' ').Append(frameTimes[i].Key).Append('=').Append(Ms(frameTimes[i].Value)).Append("ms");

            sb.Append("\n  avgSelfMs  sample\n");
            foreach (KeyValuePair<string, double> entry in TopByValue(selfByName, top))
                sb.Append("  ").Append(Ms(entry.Value / count).PadLeft(9)).Append("  ").Append(entry.Key).Append('\n');

            return sb.ToString();
        }

        // Matches "Group / Name" or the bare name. The main thread is always index 0, so it skips the scan.
        private static int FindThreadIndex(int frame, string threadName)
        {
            if (threadName == MAIN_THREAD_NAME)
                return MAIN_THREAD_INDEX;

            for (int threadIndex = 0;; threadIndex++)
            {
                using (RawFrameDataView raw = ProfilerDriver.GetRawFrameDataView(frame, threadIndex))
                {
                    if (!raw.valid)
                        return -1;
                    if (raw.threadName == threadName || raw.threadGroupName + " / " + raw.threadName == threadName)
                        return threadIndex;
                }
            }
        }

        private static HierarchyFrameDataView OpenView(int frame, int thread) =>
            ProfilerDriver.GetHierarchyFrameDataView(frame, thread,
                HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                HierarchyFrameDataView.columnTotalTime, false);

        // Iterative depth-first walk; profiler hierarchies can be deeper than is safe to recurse.
        private static void Walk(HierarchyFrameDataView view, Action<int, int, int> visit)
        {
            var stack = new Stack<(int item, int parent, int depth)>();
            var children = new List<int>();
            int root = view.GetRootItemID();
            view.GetItemChildren(root, children);
            foreach (int child in children)
                stack.Push((child, root, 0));

            while (stack.Count > 0)
            {
                (int item, int parent, int depth) = stack.Pop();
                visit(item, parent, depth);
                if (!view.HasItemChildren(item))
                    continue;

                view.GetItemChildren(item, children);
                foreach (int child in children)
                    stack.Push((child, item, depth + 1));
            }
        }

        // The GC column is inclusive, so a sample's own allocation is its total minus its children's.
        private static double SelfGcBytes(HierarchyFrameDataView view, int item)
        {
            double total = view.GetItemColumnDataAsDouble(item, HierarchyFrameDataView.columnGcMemory);
            if (total <= 0 || !view.HasItemChildren(item))
                return total;

            var children = new List<int>();
            view.GetItemChildren(item, children);
            foreach (int child in children)
                total -= view.GetItemColumnDataAsDouble(child, HierarchyFrameDataView.columnGcMemory);
            return total;
        }

        private static string AllocatorName(HierarchyFrameDataView view, int item, int parent)
        {
            string name = view.GetItemName(item);
            if (name != GC_ALLOC_MARKER || parent == view.GetRootItemID())
                return name;

            return view.GetItemName(parent);
        }

        private static bool TryResolveFrame(ref int frame, out string error)
        {
            int first = ProfilerDriver.firstFrameIndex;
            int last = ProfilerDriver.lastFrameIndex;
            error = null;
            if (last < 0)
            {
                error = "no profiler frames: record in play mode or call Load(path)";
                return false;
            }

            if (frame == ALL_FRAMES)
                frame = last;
            if (frame >= first && frame <= last)
                return true;

            error = "frame " + frame + " outside " + first + ".." + last;
            return false;
        }

        private static bool TryResolveRange(ref int firstFrame, ref int lastFrame, out string error)
        {
            int first = ProfilerDriver.firstFrameIndex;
            int last = ProfilerDriver.lastFrameIndex;
            error = null;
            if (last < 0)
            {
                error = "no profiler frames: record in play mode or call Load(path)";
                return false;
            }

            if (firstFrame == ALL_FRAMES)
                firstFrame = first;
            if (lastFrame == ALL_FRAMES)
                lastFrame = last;
            if (firstFrame >= first && lastFrame <= last && firstFrame <= lastFrame)
                return true;

            error = "range " + firstFrame + ".." + lastFrame + " outside " + first + ".." + last;
            return false;
        }

        private static void AppendFrameHeader(StringBuilder sb, FrameDataView view, int frame, int thread, float targetFrameMs)
        {
            double frameMs = view.frameTimeMs;
            sb.Append("frame ").Append(frame).Append(" thread ").Append(thread)
              .Append(" (").Append(ThreadLabel(view)).Append("): cpu=").Append(Ms(frameMs))
              .Append("ms target=").Append(Ms(targetFrameMs)).Append("ms")
              .Append(frameMs > targetFrameMs ? " OVER BUDGET" : "").Append('\n');
        }

        private static string ThreadLabel(FrameDataView view) =>
            string.IsNullOrEmpty(view.threadGroupName) ? view.threadName : view.threadGroupName + " / " + view.threadName;

        private static void AppendThreadRun(StringBuilder sb, string label, int start, int end)
        {
            if (label == null)
                return;

            sb.Append("  [").Append(start);
            if (end > start)
                sb.Append("..").Append(end);
            sb.Append("] ").Append(label);
            if (end > start)
                sb.Append(" (x").Append(end - start + 1).Append(')');
            sb.Append('\n');
        }

        private static void Accumulate(Dictionary<string, double> totals, string key, double value)
        {
            if (value <= 0)
                return;

            totals[key] = totals.TryGetValue(key, out double current) ? current + value : value;
        }

        private static List<KeyValuePair<string, double>> TopByValue(Dictionary<string, double> totals, int top)
        {
            var list = new List<KeyValuePair<string, double>>(totals);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            if (list.Count > top)
                list.RemoveRange(top, list.Count - top);
            return list;
        }

        private static string PathTail(string path)
        {
            string[] segments = path.Split('/');
            if (segments.Length <= PATH_TAIL_SEGMENTS)
                return path;

            return ".../" + string.Join("/", segments, segments.Length - PATH_TAIL_SEGMENTS, PATH_TAIL_SEGMENTS);
        }

        private static string Ms(double ms) => ms.ToString("0.00", Inv);

        private static string Kb(double bytes) => (bytes / BYTES_PER_KB).ToString("0.0", Inv);

        private static string Percent(double part, double whole) =>
            whole > 0 ? (part / whole * 100.0).ToString("0.0", Inv) + "%" : "-";
    }
}
