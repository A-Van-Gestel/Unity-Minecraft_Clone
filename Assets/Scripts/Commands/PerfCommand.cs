using System;
using System.Collections.Generic;
using System.Globalization;
using Diagnostics;
using Unity.Scripting.LifecycleManagement;

namespace Commands
{
    /// <summary>
    /// <c>/perf</c> — the performance monitor's console entry point. <c>/perf probe</c> starts the
    /// <see cref="EngineApiProbe"/>, which checks which profiling APIs work in the running build;
    /// <c>/perf</c> alone reports whether a probe is running and the last result. <c>/perf stats</c> prints
    /// exact worst-frame and percentile timings from <see cref="PerfStore"/>, and <c>/perf tier</c> reads or
    /// sets how much the monitor records.
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
        private const string MS_FORMAT = "0.00";
        private const int SLOT_NAME_WIDTH = 18;
        private const int FRAME_LABEL_WIDTH = 4;

        [NoAutoStaticsCleanup] // immutable table
        private static readonly string[] s_aliases = { "profiler" };

        [NoAutoStaticsCleanup] // immutable table
        private static readonly string[] s_subcommands = { PROBE_VERB, STATS_VERB, TIER_VERB };

        [NoAutoStaticsCleanup] // immutable table
        private static readonly string[] s_tierNames = CreateTierNames();

        /// <inheritdoc/>
        public string Name => "perf";

        /// <inheritdoc/>
        public string[] Aliases => s_aliases;

        /// <inheritdoc/>
        public string Usage => "/perf [probe | stats | tier [basic|frame|systems|capture]]";

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

            Array.Sort(summaries, slots, Comparer<PerfWindowSummary>.Create((a, b) => b.MeanMs.CompareTo(a.MeanMs)));

            lines.Add(new ConsoleLine(ConsoleLineSeverity.Info,
                $"World systems over the last {PerfStore.SlotFramesRecorded} frames (avg / p99 / worst ms):"));
            for (int i = 0; i < slots.Length; i++)
            {
                PerfWindowSummary s = summaries[i];
                lines.Add(new ConsoleLine(ConsoleLineSeverity.Info,
                    $"  {slots[i].ToString().PadRight(SLOT_NAME_WIDTH)} {Ms(s.MeanMs)} / {Ms(s.P99Ms)} / {Ms(s.MaxMs)}"));
            }

            return new CommandResult(lines.ToArray());
        }

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
            $"  {label.PadRight(FRAME_LABEL_WIDTH)} worst {Ms(s.MaxMs)}  p99 {Ms(s.P99Ms)}  p50 {Ms(s.P50Ms)}  avg {Ms(s.MeanMs)}";

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
