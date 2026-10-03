using System;
using System.Collections.Generic;
using Diagnostics;
using Unity.Scripting.LifecycleManagement;

namespace Commands
{
    /// <summary>
    /// <c>/perf</c> — the performance monitor's console entry point. <c>/perf probe</c> starts the
    /// <see cref="EngineApiProbe"/>, which checks which profiling APIs work in the running build;
    /// <c>/perf</c> alone reports whether a probe is running and the last result.
    /// </summary>
    /// <remarks>
    /// The probe samples over several frames, but a command returns synchronously, so the result arrives
    /// as a toast and is read back with <c>/perf</c> rather than posted into the console later.
    /// </remarks>
    public sealed class PerfCommand : IConsoleCommand, IArgumentCompleter
    {
        private const string PROBE_VERB = "probe";

        [NoAutoStaticsCleanup] // immutable table
        private static readonly string[] s_aliases = { "profiler" };

        [NoAutoStaticsCleanup] // immutable table
        private static readonly string[] s_subcommands = { PROBE_VERB };

        /// <inheritdoc/>
        public string Name => "perf";

        /// <inheritdoc/>
        public string[] Aliases => s_aliases;

        /// <inheritdoc/>
        public string Usage => "/perf [probe]";

        /// <inheritdoc/>
        public CommandResult Execute(CommandContext ctx, CommandArgs args)
        {
            if (args.Count == 0) return Status();

            string verb = args[0].Text.ToLowerInvariant();
            if (verb != PROBE_VERB)
                return CommandResult.Error($"Unknown sub-command '{verb}'. Usage: {Usage}");

            return EngineApiProbe.TryStart(out string reason)
                ? CommandResult.Info("Engine API probe started — about two seconds, with one deliberate GC hitch. " +
                                     "Run /perf when the toast appears.")
                : CommandResult.Error(reason);
        }

        /// <inheritdoc/>
        public string[] CompleteArgument(int argIndex, string partial, CommandContext ctx)
        {
            if (argIndex != 0) return Array.Empty<string>();

            return PROBE_VERB.StartsWith(partial, StringComparison.OrdinalIgnoreCase)
                ? s_subcommands
                : Array.Empty<string>();
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
    }
}
