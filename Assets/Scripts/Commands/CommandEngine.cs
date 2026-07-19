using System;
using System.Collections.Generic;

namespace Commands
{
    /// <summary>
    /// The command console engine: submit a line via <see cref="Execute"/>, get classified output.
    /// Pure C# and instance-based — the UI (CMD-1) is a stateless view over it, and headless callers
    /// (validation suites, future scripting) drive the identical code path.
    /// <para>
    /// Owns the tokenizer → registry → dispatch flow, the single pending confirmation, and the
    /// preallocated output/command-history rings (↑/↓ recall included, so every front-end shares
    /// the same recall semantics). Commands are mandatory-<c>/</c>-prefixed; the unprefixed
    /// namespace is reserved for future chat. <c>/help</c> is registered by construction.
    /// </para>
    /// </summary>
    public sealed class CommandEngine
    {
        private const int OUTPUT_CAPACITY = 256; // output ring: enough scrollback for a session without unbounded growth
        private const int COMMAND_HISTORY_CAPACITY = 64; // ↑/↓ recall depth

        /// <summary>Hint shown when input lacks the mandatory <c>/</c> prefix.</summary>
        public const string UnprefixedHint = "Commands start with '/' — try /help";

        /// <summary>Error shown for a bare <c>/</c> with no command name.</summary>
        public const string MissingCommandError = "Missing command name — try /help";

        /// <summary>Notice shown when a non-yes/no line cancels a pending confirmation.</summary>
        public const string ConfirmationCancelledNotice = "Pending confirmation cancelled.";

        /// <summary>Suffix the engine appends to every confirmation prompt.</summary>
        public const string ConfirmationSuffix = " [yes/no]";

        private readonly CommandRegistry _registry = new CommandRegistry();
        private readonly CommandContext _context;
        private readonly CommandRingBuffer<ConsoleLine> _output = new CommandRingBuffer<ConsoleLine>(OUTPUT_CAPACITY);
        private readonly CommandRingBuffer<string> _commandHistory = new CommandRingBuffer<string>(COMMAND_HISTORY_CAPACITY);
        private readonly List<CommandToken> _tokenScratch = new List<CommandToken>();
        private readonly List<CommandToken> _completionScratch = new List<CommandToken>();

        private PendingConfirmation _pending;

        /// <summary>The recall cursor: index into <see cref="_commandHistory"/>, or Count when not recalling.</summary>
        private int _recallCursor;

        /// <summary>The command registry (register additional commands here).</summary>
        public CommandRegistry Registry => _registry;

        /// <summary>The execution context handed to every command.</summary>
        public CommandContext Context => _context;

        /// <summary>The retained output lines, oldest → newest.</summary>
        public IReadOnlyList<ConsoleLine> Output => _output;

        /// <summary>The retained submitted commands, oldest → newest.</summary>
        public IReadOnlyList<string> CommandHistory => _commandHistory;

        /// <summary>True when a confirmation is pending on the next submitted line.</summary>
        public bool HasPendingConfirmation => _pending != null;

        /// <summary>Raised for every line appended to the output ring (the UI's render hook).</summary>
        public event Action<ConsoleLine> LineAppended;

        /// <summary>Initializes an engine with <c>/help</c> pre-registered.</summary>
        /// <param name="context">The execution context (a default local-player one when null).</param>
        public CommandEngine(CommandContext context = null)
        {
            _context = context ?? new CommandContext();
            _registry.Register(new HelpCommand(_registry));
        }

        /// <summary>
        /// Submits one console line: echoes it, resolves any pending confirmation, enforces the
        /// <c>/</c> prefix, tokenizes, dispatches, and appends all resulting lines to the output ring.
        /// </summary>
        /// <param name="line">The raw input line.</param>
        /// <returns>The lines this submission produced (excluding the echo).</returns>
        public CommandResult Execute(string line)
        {
            string trimmed = line?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                return CommandResult.Empty;

            AppendLine(new ConsoleLine(ConsoleLineSeverity.Info, trimmed));
            _commandHistory.Add(trimmed);
            _recallCursor = _commandHistory.Count;

            // Confirmation replies are unprefixed by design, so this check MUST precede the
            // '/'-prefix rule. An unrelated line cancels with a notice and then processes normally.
            CommandResult result;
            if (_pending != null)
            {
                PendingConfirmation pending = _pending;
                _pending = null;

                if (IsYes(trimmed))
                {
                    result = pending.Continue();
                }
                else if (IsNo(trimmed))
                {
                    result = CommandResult.Info(ConfirmationCancelledNotice);
                }
                else
                {
                    AppendResult(CommandResult.Info(ConfirmationCancelledNotice));
                    result = Process(trimmed);
                }
            }
            else
            {
                result = Process(trimmed);
            }

            AppendResult(result);
            return result;
        }

        /// <summary>
        /// Appends a system-originated line to the output ring, outside any command execution —
        /// e.g. a teleport arrival-hold outcome posted frames after the command returned. Raises
        /// <see cref="LineAppended"/> like any other line.
        /// </summary>
        /// <param name="severity">The line's severity.</param>
        /// <param name="text">The line text.</param>
        public void PostLine(ConsoleLineSeverity severity, string text)
        {
            AppendLine(new ConsoleLine(severity, text));
        }

        /// <summary>Steps the recall cursor back (↑). Clamps at the oldest command.</summary>
        /// <returns>The recalled command, or null when there is no history.</returns>
        public string RecallPrevious()
        {
            if (_commandHistory.Count == 0)
                return null;
            if (_recallCursor > 0)
                _recallCursor--;
            return _commandHistory[_recallCursor];
        }

        /// <summary>Steps the recall cursor forward (↓). Past the newest command returns null (the UI clears its field).</summary>
        /// <returns>The recalled command, or null when back at the live (empty) line.</returns>
        public string RecallNext()
        {
            if (_recallCursor >= _commandHistory.Count)
                return null;
            _recallCursor++;
            return _recallCursor < _commandHistory.Count ? _commandHistory[_recallCursor] : null;
        }

        /// <summary>
        /// Computes the Tab-completion for a partial input line (CMD-5, §8.3). Pure — no output is
        /// posted; the caller applies <see cref="CommandCompletion.CompletedText"/> to the field and
        /// lists the candidates itself. The first token completes against registered command names
        /// (primary names only); once the name is complete, completion delegates to the command's
        /// <see cref="IArgumentCompleter"/> (when it implements one). Completion always operates at the
        /// end of the line — caret position is not consulted.
        /// </summary>
        /// <param name="input">The current (partial) input line, with or without its leading <c>/</c>.</param>
        /// <returns>The completion result; <see cref="CommandCompletion.Unchanged"/> when nothing applies.</returns>
        public CommandCompletion Complete(string input)
        {
            string text = input ?? "";
            bool hasSlash = text.StartsWith("/", StringComparison.Ordinal);

            // Only slash-commands complete. Non-empty unprefixed input is chat-reserved and left alone;
            // empty input offers the whole command list (and supplies the leading '/').
            if (!hasSlash && text.Length > 0)
                return CommandCompletion.Unchanged(input);

            string body = hasSlash ? text.Substring(1) : text;
            bool trailingSpace = body.Length > 0 && char.IsWhiteSpace(body[body.Length - 1]);

            if (!CommandTokenizer.Tokenize(body, _completionScratch, out _))
                return CommandCompletion.Unchanged(input);

            int tokenCount = _completionScratch.Count;

            // Command-name stage: nothing typed yet, or the first token is still being typed.
            if (tokenCount == 0 || (tokenCount == 1 && !trailingSpace))
            {
                string namePartial = tokenCount == 0 ? "" : _completionScratch[0].Text;
                List<string> matches = new List<string>();
                foreach (IConsoleCommand cmd in _registry.Commands)
                    if (cmd.Name.StartsWith(namePartial, StringComparison.OrdinalIgnoreCase))
                        matches.Add(cmd.Name);

                if (matches.Count == 0)
                    return CommandCompletion.Unchanged(input);

                // A single match completes fully with a trailing space (ready for arguments); multiple
                // matches advance only to their shared prefix (bash/Minecraft behavior).
                string completedName = matches.Count == 1 ? matches[0] + " " : LongestCommonPrefix(matches);
                return new CommandCompletion("/" + completedName, matches.ToArray());
            }

            // Argument stage: the command name is complete; delegate to an opt-in completer.
            if (!_registry.TryResolve(_completionScratch[0].Text, out IConsoleCommand command) ||
                !(command is IArgumentCompleter completer))
                return CommandCompletion.Unchanged(input);

            // With a trailing space the caret starts a fresh argument (empty partial); otherwise the
            // last token is the in-progress argument being completed.
            int argIndex = trailingSpace ? tokenCount - 1 : tokenCount - 2;
            string argPartial = trailingSpace ? "" : _completionScratch[tokenCount - 1].Text;

            string[] candidates = completer.CompleteArgument(argIndex, argPartial, _context);
            if (candidates == null || candidates.Length == 0)
                return CommandCompletion.Unchanged(input);

            string replacement = candidates.Length == 1 ? candidates[0] + " " : LongestCommonPrefix(candidates);

            string completedLine;
            if (trailingSpace)
            {
                completedLine = input + replacement;
            }
            else
            {
                // The partial is a literal tail of the raw input only for unquoted tokens; the quoted
                // edge (partial with quotes stripped) can't be substring-replaced safely, so decline.
                if (!input.EndsWith(argPartial, StringComparison.Ordinal))
                    return CommandCompletion.Unchanged(input);
                completedLine = input.Substring(0, input.Length - argPartial.Length) + replacement;
            }

            return new CommandCompletion(completedLine, candidates);
        }

        /// <summary>
        /// The inline suggestion suffix for a partial input line (CMD-5 ghost text) — the gray text
        /// shown after the caret. Non-empty only when completion is unambiguous (exactly one
        /// candidate); an ambiguous prefix shows nothing inline and is resolved with Tab (common
        /// prefix + candidate list). Pure — derived from <see cref="Complete"/>, so the UI just renders
        /// the returned suffix and the suite asserts on it directly.
        /// </summary>
        /// <param name="input">The current (partial) input line.</param>
        /// <returns>The suffix to display in gray after the input, or an empty string when none applies.</returns>
        public string Suggest(string input)
        {
            string text = input ?? "";
            CommandCompletion completion = Complete(text);

            // Only a single, unambiguous candidate produces a ghost; multiple candidates stay a Tab affair.
            if (completion.Candidates.Length != 1 ||
                !completion.CompletedText.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                return "";

            // The completion appends a trailing space after a full single match; the ghost shows only
            // the visible remainder of the word.
            return completion.CompletedText.Substring(text.Length).TrimEnd();
        }

        /// <summary>Runs the prefix → tokenize → dispatch pipeline on a line with no confirmation pending.</summary>
        /// <param name="trimmed">The trimmed, non-empty input line.</param>
        /// <returns>The produced result (its pending confirmation, if any, is stored by the caller via <see cref="AppendResult"/>).</returns>
        private CommandResult Process(string trimmed)
        {
            if (trimmed[0] != '/')
                return CommandResult.Error(UnprefixedHint);

            if (!CommandTokenizer.Tokenize(trimmed.Substring(1), _tokenScratch, out string tokenError))
                return CommandResult.Error(tokenError);

            if (_tokenScratch.Count == 0)
                return CommandResult.Error(MissingCommandError);

            // Relative '~' tokens are no longer rejected here (CMD-4): coord-consuming commands
            // resolve them against the player; other commands reject them via their own arg checks.
            string name = _tokenScratch[0].Text;
            if (!_registry.TryResolve(name, out IConsoleCommand command))
                return CommandResult.Error($"Unknown command '{name}' — try /help");

            CommandToken[] argTokens = new CommandToken[_tokenScratch.Count - 1];
            for (int i = 1; i < _tokenScratch.Count; i++)
                argTokens[i - 1] = _tokenScratch[i];

            return command.Execute(_context, new CommandArgs(argTokens));
        }

        /// <summary>Appends a result's lines to the output ring and stores its pending confirmation.</summary>
        /// <param name="result">The result to append.</param>
        private void AppendResult(CommandResult result)
        {
            IReadOnlyList<ConsoleLine> lines = result.Lines;
            foreach (ConsoleLine line in lines)
                AppendLine(line);

            if (result.Pending != null)
            {
                _pending = result.Pending;
                AppendLine(new ConsoleLine(ConsoleLineSeverity.Warning, result.Pending.Prompt + ConfirmationSuffix));
            }
        }

        /// <summary>Appends one line to the output ring and raises <see cref="LineAppended"/>.</summary>
        /// <param name="consoleLine">The line to append.</param>
        private void AppendLine(ConsoleLine consoleLine)
        {
            _output.Add(consoleLine);
            LineAppended?.Invoke(consoleLine);
        }

        /// <summary>The longest common prefix of the given values, compared case-insensitively but
        /// returned in the first value's casing (so completion emits the canonical block/command name).</summary>
        /// <param name="values">The candidate strings (at least one).</param>
        /// <returns>Their shared leading prefix (possibly empty).</returns>
        private static string LongestCommonPrefix(List<string> values)
        {
            string prefix = values[0];
            for (int i = 1; i < values.Count && prefix.Length > 0; i++)
            {
                string other = values[i];
                int max = Math.Min(prefix.Length, other.Length);
                int j = 0;
                while (j < max && char.ToLowerInvariant(prefix[j]) == char.ToLowerInvariant(other[j]))
                    j++;
                prefix = prefix.Substring(0, j);
            }

            return prefix;
        }

        /// <summary>The longest common prefix of the given values (array overload; see the list overload).</summary>
        /// <param name="values">The candidate strings (at least one).</param>
        /// <returns>Their shared leading prefix (possibly empty).</returns>
        private static string LongestCommonPrefix(string[] values)
        {
            string prefix = values[0];
            for (int i = 1; i < values.Length && prefix.Length > 0; i++)
            {
                string other = values[i];
                int max = Math.Min(prefix.Length, other.Length);
                int j = 0;
                while (j < max && char.ToLowerInvariant(prefix[j]) == char.ToLowerInvariant(other[j]))
                    j++;
                prefix = prefix.Substring(0, j);
            }

            return prefix;
        }

        /// <summary>Whether a line is an affirmative confirmation reply.</summary>
        /// <param name="trimmed">The trimmed input line.</param>
        /// <returns>True for <c>yes</c>/<c>y</c> (case-insensitive).</returns>
        private static bool IsYes(string trimmed) =>
            string.Equals(trimmed, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "y", StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether a line is a negative confirmation reply.</summary>
        /// <param name="trimmed">The trimmed input line.</param>
        /// <returns>True for <c>no</c>/<c>n</c> (case-insensitive).</returns>
        private static bool IsNo(string trimmed) =>
            string.Equals(trimmed, "no", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "n", StringComparison.OrdinalIgnoreCase);
    }
}
