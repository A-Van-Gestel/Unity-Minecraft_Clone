using System;
using System.Collections.Generic;

namespace Commands
{
    /// <summary>
    /// A confirmation a command requested before proceeding: the prompt shown to the user and the
    /// continuation the engine runs if the next submitted line answers yes. The engine holds at most
    /// one pending confirmation; a continuation may itself return another one (they chain naturally).
    /// </summary>
    public sealed class PendingConfirmation
    {
        /// <summary>The prompt shown to the user (the engine appends the yes/no hint).</summary>
        public readonly string Prompt;

        /// <summary>The continuation executed when the user confirms.</summary>
        public readonly Func<CommandResult> Continue;

        /// <summary>Initializes a pending confirmation.</summary>
        /// <param name="prompt">The prompt shown to the user.</param>
        /// <param name="continuation">The continuation executed on confirmation.</param>
        public PendingConfirmation(string prompt, Func<CommandResult> continuation)
        {
            Prompt = prompt;
            Continue = continuation;
        }
    }

    /// <summary>
    /// Outcome of one submitted line: zero or more output lines, plus an optional pending
    /// confirmation the engine should hold for the next submitted line.
    /// </summary>
    public readonly struct CommandResult
    {
        private static readonly ConsoleLine[] s_noLines = Array.Empty<ConsoleLine>();

        private readonly ConsoleLine[] _lines;

        /// <summary>The output lines produced (never null).</summary>
        public IReadOnlyList<ConsoleLine> Lines => _lines ?? s_noLines;

        /// <summary>The confirmation this result requests, or null.</summary>
        public readonly PendingConfirmation Pending;

        /// <summary>Initializes a result.</summary>
        /// <param name="lines">The output lines (null treated as empty).</param>
        /// <param name="pending">An optional confirmation request.</param>
        public CommandResult(ConsoleLine[] lines, PendingConfirmation pending = null)
        {
            _lines = lines;
            Pending = pending;
        }

        /// <summary>A result with no output and no confirmation.</summary>
        public static CommandResult Empty => new CommandResult(null);

        /// <summary>Creates a single-line informational result.</summary>
        /// <param name="text">The line text.</param>
        /// <returns>The result.</returns>
        public static CommandResult Info(string text) =>
            new CommandResult(new[] { new ConsoleLine(ConsoleLineSeverity.Info, text) });

        /// <summary>Creates a single-line warning result.</summary>
        /// <param name="text">The line text.</param>
        /// <returns>The result.</returns>
        public static CommandResult Warning(string text) =>
            new CommandResult(new[] { new ConsoleLine(ConsoleLineSeverity.Warning, text) });

        /// <summary>Creates a single-line error result.</summary>
        /// <param name="text">The line text.</param>
        /// <returns>The result.</returns>
        public static CommandResult Error(string text) =>
            new CommandResult(new[] { new ConsoleLine(ConsoleLineSeverity.Error, text) });

        /// <summary>
        /// Creates a result that requests confirmation before proceeding. The engine renders the
        /// prompt (with its yes/no suffix) — the result itself carries no lines, so the prompt is
        /// never shown twice.
        /// </summary>
        /// <param name="prompt">The warning/prompt text.</param>
        /// <param name="continuation">The continuation executed on confirmation.</param>
        /// <returns>The result.</returns>
        public static CommandResult Confirm(string prompt, Func<CommandResult> continuation) =>
            new CommandResult(null, new PendingConfirmation(prompt, continuation));
    }
}
