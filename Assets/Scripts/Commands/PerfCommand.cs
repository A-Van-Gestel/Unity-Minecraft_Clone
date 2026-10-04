using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Diagnostics;
using Unity.Scripting.LifecycleManagement;

namespace Commands
{
    /// <summary>
    /// <c>/perf</c> — the performance monitor's console entry point. <c>/perf probe</c> starts the
    /// <see cref="EngineApiProbe"/>, which checks which profiling APIs work in the running build;
    /// <c>/perf</c> alone reports whether a probe is running and the last result. <c>/perf stats</c> prints
    /// exact worst-frame and percentile timings and allocation from <see cref="PerfStore"/>, <c>/perf hitches</c>
    /// lists the held hitch records, and <c>/perf tier</c> reads or sets how much the monitor records.
    /// </summary>
    /// <remarks>
    /// The probe samples over several frames, but a command returns synchronously, so the result arrives
    /// as a toast and is read back with <c>/perf</c> rather than posted into the console later.
    /// </remarks>
    public sealed class PerfCommand : IConsoleCommand, IArgumentCompleter
    {
        private const string PROBE_VERB = "probe";
        private const string STATS_VERB = "stats";
        private const string TIER_VERB = "tier";
        private const string HITCHES_VERB = "hitches";
        private const string MS_FORMAT = "0.00";
        private const int SLOT_NAME_WIDTH = 18;
        private const int FRAME_LABEL_WIDTH = 7;

        [NoAutoStaticsCleanup] // immutable table
        private static readonly string[] s_aliases = { "profiler" };

        [NoAutoStaticsCleanup] // immutable table
        private static readonly string[] s_subcommands = { PROBE_VERB, STATS_VERB, HITCHES_VERB, TIER_VERB };

        [NoAutoStaticsCleanup] // immutable table
        private static readonly string[] s_tierNames = CreateTierNames();

        /// <inheritdoc/>
        public string Name => "perf";

        /// <inheritdoc/>
        public string[] Aliases => s_aliases;

        /// <inheritdoc/>
        public string Usage => "/perf [probe | stats | hitches | tier [basic|frame|systems|capture]]";

        /// <inheritdoc/>
        public CommandResult Execute(CommandContext ctx, CommandArgs args)
        {
            if (args.Count == 0) return Status();

            string verb = args[0].Text.ToLowerInvariant();
            switch (verb)
            {
                case PROBE_VERB:
                    return EngineApiProbe.TryStart(out string reason)
                        ? CommandResult.Info("Engine API probe started — about two seconds, with one deliberate GC hitch. " +
                                             "Run /perf when the toast appears.")
                        : CommandResult.Error(reason);
                case STATS_VERB:
                    return Stats();
                case TIER_VERB:
                    return args.Count > 1 ? SetTier(args[1].Text) : CommandResult.Info($"Monitor detail: {PerfStore.Tier}.");
                case HITCHES_VERB:
                    return Hitches();
                default:
                    return CommandResult.Error($"Unknown sub-command '{verb}'. Usage: {Usage}");
            }
        }

        /// <inheritdoc/>
        public string[] CompleteArgument(int argIndex, string partial, CommandContext ctx)
        {
            if (argIndex == 0) return Matching(s_subcommands, partial);

            // Arg 1 is only meaningful after 'tier'; the completer cannot see the subcommand.
            return argIndex == 1 ? Matching(s_tierNames, partial) : Array.Empty<string>();
        }

        /// <summary>Reports a running probe, the last result, or how to start one.</summary>
        /// <returns>The readout.</returns>
        private static CommandResult Status()
        {
            if (EngineApiProbe.IsRunning)
                return CommandResult.Info("Engine API probe running — results in a moment.");

            string[] summary = EngineApiProbe.LastSummary;
            if (summary == null)
                return CommandResult.Info("No engine API probe has run this session. '/perf probe' checks which " +
                                          "profiling APIs work in this build.");

            List<ConsoleLine> lines = new List<ConsoleLine>(summary.Length + 2)
            {
                new ConsoleLine(ConsoleLineSeverity.Info, "Last engine API probe:"),
            };

            foreach (string line in summary)
                lines.Add(new ConsoleLine(ConsoleLineSeverity.Info, $"  {line}"));

            lines.Add(EngineApiProbe.LastReportPath != null
                ? new ConsoleLine(ConsoleLineSeverity.Info, $"Report: {EngineApiProbe.LastReportPath}")
                : new ConsoleLine(ConsoleLineSeverity.Warning, "The report could not be written — see the log."));

            return new CommandResult(lines.ToArray());
        }

        /// <summary>The <c>/perf stats</c> and <c>/perf hitches</c> readouts as one block of text, for a log.</summary>
        /// <returns>The lines, newline-separated.</returns>
        public static string FormatSummary()
        {
            StringBuilder text = new StringBuilder();
            foreach (ConsoleLine line in Stats().Lines) text.AppendLine(line.Text);
            foreach (ConsoleLine line in Hitches().Lines) text.AppendLine(line.Text);
            return text.ToString();
        }

        /// <summary>Prints wall and CPU frame statistics, then per-slot statistics sorted by average cost.</summary>
        /// <returns>The readout.</returns>
        private static CommandResult Stats()
        {
            if (PerfStore.FramesRecorded == 0)
                return CommandResult.Info("The performance monitor has not recorded a frame yet.");

            List<ConsoleLine> lines = new List<ConsoleLine>
            {
                new ConsoleLine(ConsoleLineSeverity.Info,
                    $"Monitor detail {PerfStore.Tier}, last {PerfStore.FramesRecorded} frames (ms):"),
                new ConsoleLine(ConsoleLineSeverity.Info, FormatFrameLine("Wall", PerfStore.SummarizeWallMs())),
                new ConsoleLine(ConsoleLineSeverity.Info, FormatFrameLine("CPU", PerfStore.SummarizeCpuMs())),
            };

            AddGcLines(lines);
            AddFrameTimingLines(lines);

            if (PerfStore.Hitches != null)
            {
                lines.Add(new ConsoleLine(ConsoleLineSeverity.Info,
                    $"Hitches: {PerfStore.Hitches.HitchFrames} frames over {Ms(PerfStore.Hitches.ThresholdMs)} ms threshold, " +
                    $"{PerfStore.Hitches.RecordCount} held — /perf hitches"));
            }

            if (PerfStore.SlotFramesRecorded == 0)
            {
                lines.Add(new ConsoleLine(ConsoleLineSeverity.Info,
                    "Per-system times need the Systems detail level: /perf tier systems"));
                return new CommandResult(lines.ToArray());
            }

            PerfSlot[] slots = new PerfSlot[PerfStore.SlotCount];
            PerfWindowSummary[] summaries = new PerfWindowSummary[PerfStore.SlotCount];
            for (int i = 0; i < PerfStore.SlotCount; i++)
            {
                slots[i] = (PerfSlot)i;
                summaries[i] = PerfStore.SummarizeSlotMs(slots[i]);
            }

            Array.Sort(summaries, slots, Comparer<PerfWindowSummary>.Create((a, b) => b.Mean.CompareTo(a.Mean)));

            lines.Add(new ConsoleLine(ConsoleLineSeverity.Info,
                $"World systems over the last {PerfStore.SlotFramesRecorded} frames (avg / p99 / worst ms):"));
            for (int i = 0; i < slots.Length; i++)
            {
                PerfWindowSummary s = summaries[i];
                lines.Add(new ConsoleLine(ConsoleLineSeverity.Info,
                    $"  {slots[i].ToString().PadRight(SLOT_NAME_WIDTH)} {Ms(s.Mean)} / {Ms(s.P99)} / {Ms(s.Max)}"));
            }

            return new CommandResult(lines.ToArray());
        }

        /// <summary>Adds managed-allocation statistics over the frames with a usable figure, plus the collections held.</summary>
        private static void AddGcLines(List<ConsoleLine> lines)
        {
            PerfWindowSummary alloc = PerfStore.Summarize(PerfFrameField.GcAllocKb);
            lines.Add(new ConsoleLine(ConsoleLineSeverity.Info, alloc.Count == 0
                ? "  GC      no frame without a collection yet"
                : $"  GC      worst {Ms(alloc.Max)}  p99 {Ms(alloc.P99)}  p50 {Ms(alloc.P50)}  avg {Ms(alloc.Mean)} KB allocated " +
                  $"({alloc.Count} frames); {PerfStore.GcCollectionsHeld} collections"));

            if (PerfStore.GcShrinkFrames > 0)
            {
                lines.Add(new ConsoleLine(ConsoleLineSeverity.Warning,
                    $"  {PerfStore.GcShrinkFrames} frames shrank the heap without a counted collection; their allocation is unknown."));
            }
        }

        /// <summary>Adds GPU, render-thread and present-wait statistics, or why there are none.</summary>
        private static void AddFrameTimingLines(List<ConsoleLine> lines)
        {
            if (PerfStore.Tier < PerfTier.Frame)
            {
                lines.Add(new ConsoleLine(ConsoleLineSeverity.Info, "GPU and hitch readouts need the Frame detail level: /perf tier frame"));
                return;
            }

            PerfWindowSummary render = PerfStore.Summarize(PerfFrameField.RenderThreadMs);
            if (render.Count == 0)
            {
                lines.Add(new ConsoleLine(ConsoleLineSeverity.Warning,
                    $"  GPU     n/a — {PerfStore.FrameTimingsReceived} frame timings received, {PerfStore.FrameTimingsMatched} matched a frame"));
                return;
            }

            // Render-thread time arriving while GPU time does not means the platform reports no GPU time.
            PerfWindowSummary gpu = PerfStore.Summarize(PerfFrameField.GpuMs);
            lines.Add(gpu.Count == 0
                ? new ConsoleLine(ConsoleLineSeverity.Warning, "  GPU     n/a — frame timings arrive, but report no GPU time")
                : new ConsoleLine(ConsoleLineSeverity.Info, FormatFrameLine("GPU", gpu)));
            lines.Add(new ConsoleLine(ConsoleLineSeverity.Info, FormatFrameLine("Render", render)));
            lines.Add(new ConsoleLine(ConsoleLineSeverity.Info, FormatFrameLine("Present", PerfStore.Summarize(PerfFrameField.PresentWaitMs))));
        }

        /// <summary>Lists the held hitch records, newest first.</summary>
        /// <returns>The readout.</returns>
        private static CommandResult Hitches()
        {
            PerfHitchDetector hitches = PerfStore.Hitches;
            if (hitches == null)
                return CommandResult.Info("Hitch detection needs the Frame detail level: /perf tier frame");
            if (hitches.RecordCount == 0)
            {
                return CommandResult.Info(hitches.IsWindowOpen
                    ? "A hitch was just detected; its record closes in a moment."
                    : $"No hitch recorded yet (threshold {Ms(hitches.ThresholdMs)} ms).");
            }

            List<ConsoleLine> lines = new List<ConsoleLine>(hitches.RecordCount + 1)
            {
                new ConsoleLine(ConsoleLineSeverity.Info,
                    $"{hitches.RecordCount} of {hitches.RecordsClosed} hitch records, newest first:"),
            };

            for (int age = 0; age < hitches.RecordCount; age++)
                lines.Add(new ConsoleLine(ConsoleLineSeverity.Info, FormatHitch(hitches, age)));

            return new CommandResult(lines.ToArray());
        }

        private static string FormatHitch(PerfHitchDetector hitches, int age)
        {
            PerfHitchRecord record = hitches.GetRecord(age);
            PerfFrame worst = hitches.GetRecordFrame(age, record.WorstRow);

            string text = $"  frame {record.HitchFrameIndex}: worst {Ms(record.WorstWallMs)} ms (over {Ms(record.ThresholdMs)}), " +
                          $"{record.HitchFrameCount} slow frame(s){(record.GcCorrelated ? ", GC collection" : "")}; " +
                          $"CPU {Ms(worst.CpuMs)}, GPU {MsOrNa(worst.GpuMs)}, present wait {MsOrNa(worst.PresentWaitMs)}";

            for (int rank = 0; rank < record.TopSlotCount; rank++)
            {
                record.GetTopSlot(rank, out PerfSlot slot, out float ms);
                text += $"{(rank == 0 ? "; top " : ", ")}{slot} {Ms(ms)}";
            }

            return text;
        }

        private static string MsOrNa(float value) => float.IsNaN(value) ? "n/a" : Ms(value);

        /// <summary>Sets the monitor tier through the settings, so the live-apply path and the Settings menu agree.</summary>
        /// <param name="text">The tier name, case-insensitive.</param>
        /// <returns>The confirmation or the error.</returns>
        private static CommandResult SetTier(string text)
        {
            if (!Enum.TryParse(text, true, out PerfTier tier) || !Enum.IsDefined(typeof(PerfTier), tier))
                return CommandResult.Error($"Unknown detail level '{text}'. Choose one of: {string.Join(", ", s_tierNames)}.");

            SettingsManager.LoadSettings().perfMonitorTier = tier;
            SettingsManager.NotifySettingChanged(nameof(Settings.perfMonitorTier));
            return CommandResult.Info($"Monitor detail set to {PerfStore.Tier}.");
        }

        private static string FormatFrameLine(string label, PerfWindowSummary s) =>
            $"  {label.PadRight(FRAME_LABEL_WIDTH)} worst {Ms(s.Max)}  p99 {Ms(s.P99)}  p50 {Ms(s.P50)}  avg {Ms(s.Mean)}";

        /// <summary>Formats milliseconds independent of the host's locale.</summary>
        private static string Ms(float value) => value.ToString(MS_FORMAT, CultureInfo.InvariantCulture);

        private static string[] CreateTierNames()
        {
            string[] names = Enum.GetNames(typeof(PerfTier));
            for (int i = 0; i < names.Length; i++)
                names[i] = names[i].ToLowerInvariant();
            return names;
        }

        /// <summary>Filters a candidate list by a case-insensitive prefix.</summary>
        private static string[] Matching(string[] candidates, string partial)
        {
            if (string.IsNullOrEmpty(partial)) return candidates;

            List<string> matches = new List<string>(candidates.Length);
            foreach (string candidate in candidates)
            {
                if (candidate.StartsWith(partial, StringComparison.OrdinalIgnoreCase)) matches.Add(candidate);
            }

            return matches.Count == 0 ? Array.Empty<string>() : matches.ToArray();
        }
    }
}
