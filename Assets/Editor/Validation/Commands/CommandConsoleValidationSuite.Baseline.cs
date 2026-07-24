using System;
using System.Collections.Generic;
using System.IO;
using Commands;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Commands
{
    public static partial class CommandConsoleValidationSuite
    {
        /// <summary>Output-ring capacity mirrored from <c>CommandEngine</c> (pinned by B17 so a capacity change is a conscious suite update).</summary>
        private const int OUTPUT_CAPACITY = 256;

        /// <summary>Command-history capacity mirrored from <c>CommandEngine</c> (pinned by B16).</summary>
        private const int COMMAND_HISTORY_CAPACITY = 64;

        /// <summary>A registry-pluggable stub command whose behavior each scenario scripts via a delegate.</summary>
        private sealed class StubCommand : IConsoleCommand
        {
            private readonly Func<CommandContext, CommandArgs, CommandResult> _body;

            public string Name { get; }
            public string[] Aliases { get; }
            public string Usage { get; }

            /// <summary>How many times <see cref="Execute"/> ran.</summary>
            public int Invocations { get; private set; }

            public StubCommand(string name, string[] aliases, string usage,
                Func<CommandContext, CommandArgs, CommandResult> body)
            {
                Name = name;
                Aliases = aliases ?? Array.Empty<string>();
                Usage = usage;
                _body = body;
            }

            public CommandResult Execute(CommandContext ctx, CommandArgs args)
            {
                Invocations++;
                return _body?.Invoke(ctx, args) ?? CommandResult.Empty;
            }
        }

        /// <summary>Registers the CMD-0 engine baselines.</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("B1: Tokenizer — words and quoted strings (quotes stripped, whitespace preserved inside quotes)", Baseline_TokenizerWordsAndQuotes));
            scenarios.Add(new Scenario("B2: Tokenizer — an unterminated quoted string is a parse error", Baseline_TokenizerUnterminatedQuote));
            scenarios.Add(new Scenario("B3: Tokenizer — signed ints/floats parse invariant-culture; comma-decimal, lone '-', and '3.5x' stay words", Baseline_TokenizerNumbers));
            scenarios.Add(new Scenario("B4: Tokenizer — '@name' is a selector, bare '@' a word, '~'-prefixed tokens classify as reserved Relative", Baseline_TokenizerSelectorsAndRelative));
            scenarios.Add(new Scenario("B5: Unprefixed input is rejected with the mandatory-'/' hint (chat namespace reserved)", Baseline_UnprefixedRejected));
            scenarios.Add(new Scenario("B6: A bare '/' is a missing-command error with a /help hint", Baseline_BareSlash));
            scenarios.Add(new Scenario("B7: An unknown command errors, names the command, and hints /help", Baseline_UnknownCommand));
            scenarios.Add(new Scenario("B8: Aliases dispatch, and command names resolve case-insensitively", Baseline_AliasAndCaseInsensitiveDispatch));
            scenarios.Add(new Scenario("B9: A '~' token no longer rejects before dispatch — the command runs and receives it as a Relative arg (CMD-4)", Baseline_RelativeReachesDispatch));
            scenarios.Add(new Scenario("B10: The submitted line is echoed into the output ring as an Info line ahead of its results", Baseline_EchoSubmittedLine));
            scenarios.Add(new Scenario("B11: Confirmation — 'yes' and 'y' execute the continuation and clear the pending state", Baseline_ConfirmYes));
            scenarios.Add(new Scenario("B12: Confirmation — 'no' and 'n' cancel with a notice; the continuation never runs", Baseline_ConfirmNo));
            scenarios.Add(new Scenario("B13: Confirmation — unrelated input cancels with a notice and is then processed normally (checked before the '/' rule)", Baseline_ConfirmUnrelatedCancelsThenProcesses));
            scenarios.Add(new Scenario("B14: Confirmation — a continuation may chain a second confirmation (nested yes → yes)", Baseline_ConfirmNested));
            scenarios.Add(new Scenario("B15: History recall — ↑ walks back and clamps at oldest, ↓ walks forward to null, submit resets the cursor", Baseline_HistoryRecall));
            scenarios.Add(new Scenario("B16: Command-history ring wraps at capacity, dropping the oldest submissions", Baseline_CommandHistoryWrap));
            scenarios.Add(new Scenario("B17: Output ring wraps at capacity, dropping the oldest lines deterministically", Baseline_OutputRingWrap));
            scenarios.Add(new Scenario("B18: /help lists every registered command sorted by name, with usage and aliases", Baseline_HelpListsRegistry));
            scenarios.Add(new Scenario("B19: Registering a duplicate name or alias throws (never a silent override)", Baseline_DuplicateRegistrationThrows));
            scenarios.Add(new Scenario("B20: Selector resolver — '@player' (any case) resolves LocalPlayer; unknown selectors error", Baseline_SelectorResolver));
            scenarios.Add(new Scenario("B21: Text formatter — severity→color mapping is stable (Info/Warning/Error hex) (CMD-1)", Baseline_FormatterSeverityColors));
            scenarios.Add(new Scenario("B22: Text formatter — output is noparse-guarded so user-typed TMP markup cannot inject tags (CMD-1)", Baseline_FormatterNoparseGuard));
            scenarios.Add(new Scenario("B23: Input-bypass tripwire — no runtime script reads Keyboard.current outside InputManager (console map-disable must gate ALL keys) (CMD-1)", Baseline_KeyboardBypassTripwire));
        }

        // --- Tokenizer ---

        private static bool Baseline_TokenizerWordsAndQuotes()
        {
            List<CommandToken> tokens = new List<CommandToken>();
            bool ok = Expect(CommandTokenizer.Tokenize("say \"hello there world\" foo", tokens, out _), "tokenize succeeds");
            ok &= Expect(tokens.Count == 3, $"3 tokens, got {tokens.Count}");
            ok &= Expect(tokens[0].Type == CommandTokenType.Word && tokens[0].Text == "say", "token 0 is Word 'say'");
            ok &= Expect(tokens[1].Type == CommandTokenType.Word && tokens[1].Text == "hello there world", "quoted token keeps inner whitespace, quotes stripped");
            ok &= Expect(tokens[2].Text == "foo", "token 2 is 'foo'");

            ok &= Expect(CommandTokenizer.Tokenize("", tokens, out _) && tokens.Count == 0, "empty input tokenizes to zero tokens");
            return ok;
        }

        private static bool Baseline_TokenizerUnterminatedQuote()
        {
            List<CommandToken> tokens = new List<CommandToken>();
            bool ok = Expect(!CommandTokenizer.Tokenize("say \"oops", tokens, out string error), "unterminated quote fails");
            ok &= Expect(error == CommandTokenizer.UnterminatedQuoteError, $"error is the canonical message, got '{error}'");
            return ok;
        }

        private static bool Baseline_TokenizerNumbers()
        {
            List<CommandToken> tokens = new List<CommandToken>();
            bool ok = Expect(CommandTokenizer.Tokenize("42 -5 +2 3.5 -0.5 3,5 - 3.5x NaN", tokens, out _), "tokenize succeeds");
            ok &= Expect(tokens[0].Type == CommandTokenType.Number && tokens[0].IsInteger && tokens[0].Integer == 42, "'42' is int 42");
            ok &= Expect(tokens[1].Type == CommandTokenType.Number && tokens[1].IsInteger && tokens[1].Integer == -5, "'-5' is int -5");
            ok &= Expect(tokens[2].Type == CommandTokenType.Number && tokens[2].IsInteger && tokens[2].Integer == 2, "'+2' is int 2");
            ok &= Expect(tokens[3].Type == CommandTokenType.Number && !tokens[3].IsInteger && Math.Abs(tokens[3].Number - 3.5f) < 1e-6f, "'3.5' is float 3.5");
            ok &= Expect(tokens[4].Type == CommandTokenType.Number && Math.Abs(tokens[4].Number - (-0.5f)) < 1e-6f, "'-0.5' is float -0.5");
            ok &= Expect(tokens[5].Type == CommandTokenType.Word, "'3,5' is a WORD — comma-decimal must never parse (invariant-culture pin)");
            ok &= Expect(tokens[6].Type == CommandTokenType.Word, "lone '-' is a word");
            ok &= Expect(tokens[7].Type == CommandTokenType.Word, "'3.5x' is a word");
            ok &= Expect(tokens[8].Type == CommandTokenType.Word, "'NaN' is a word (digit/sign/dot gate)");
            return ok;
        }

        private static bool Baseline_TokenizerSelectorsAndRelative()
        {
            List<CommandToken> tokens = new List<CommandToken>();
            bool ok = Expect(CommandTokenizer.Tokenize("@player @ ~ ~5", tokens, out _), "tokenize succeeds");
            ok &= Expect(tokens[0].Type == CommandTokenType.Selector && tokens[0].Text == "@player", "'@player' is a selector");
            ok &= Expect(tokens[1].Type == CommandTokenType.Word, "bare '@' is a word");
            ok &= Expect(tokens[2].Type == CommandTokenType.Relative, "'~' is Relative");
            ok &= Expect(tokens[3].Type == CommandTokenType.Relative, "'~5' is Relative");
            return ok;
        }

        // --- Prefix, dispatch, echo ---

        private static bool Baseline_UnprefixedRejected()
        {
            CommandEngine engine = new CommandEngine();
            CommandResult result = engine.Execute("teleport 1 2 3");
            bool ok = Expect(result.Lines.Count == 1, "exactly one line");
            ok &= Expect(result.Lines[0].Severity == ConsoleLineSeverity.Error, "severity Error");
            ok &= Expect(result.Lines[0].Text == CommandEngine.UnprefixedHint, $"the canonical hint, got '{result.Lines[0].Text}'");
            return ok;
        }

        private static bool Baseline_BareSlash()
        {
            CommandEngine engine = new CommandEngine();
            CommandResult result = engine.Execute("/");
            bool ok = Expect(result.Lines.Count == 1 && result.Lines[0].Severity == ConsoleLineSeverity.Error, "one Error line");
            ok &= Expect(result.Lines[0].Text == CommandEngine.MissingCommandError, $"missing-command message, got '{result.Lines[0].Text}'");
            return ok;
        }

        private static bool Baseline_UnknownCommand()
        {
            CommandEngine engine = new CommandEngine();
            CommandResult result = engine.Execute("/nope 1 2");
            bool ok = Expect(result.Lines.Count == 1 && result.Lines[0].Severity == ConsoleLineSeverity.Error, "one Error line");
            ok &= Expect(result.Lines[0].Text.Contains("'nope'"), "names the unknown command");
            ok &= Expect(result.Lines[0].Text.Contains("/help"), "hints /help");
            return ok;
        }

        private static bool Baseline_AliasAndCaseInsensitiveDispatch()
        {
            CommandEngine engine = new CommandEngine();
            StubCommand stub = new StubCommand("test", new[] { "t2" }, "/test", null);
            engine.Registry.Register(stub);

            engine.Execute("/test");
            engine.Execute("/t2");
            engine.Execute("/TEST");
            engine.Execute("/TeSt");
            engine.Execute("/T2");
            return Expect(stub.Invocations == 5, $"5 dispatches via name/alias/case variants, got {stub.Invocations}");
        }

        private static bool Baseline_RelativeReachesDispatch()
        {
            CommandEngine engine = new CommandEngine();
            bool sawRelative = false;
            StubCommand stub = new StubCommand("test", null, "/test", (ctx, args) =>
            {
                sawRelative = args.Count == 2 && args[0].Type == CommandTokenType.Relative;
                return CommandResult.Empty;
            });
            engine.Registry.Register(stub);

            engine.Execute("/test ~ 5");
            bool ok = Expect(stub.Invocations == 1, "the command runs (the pre-dispatch '~' gate is gone, CMD-4)");
            ok &= Expect(sawRelative, "the command receives the '~' token as a Relative arg");
            return ok;
        }

        private static bool Baseline_EchoSubmittedLine()
        {
            CommandEngine engine = new CommandEngine();
            engine.Execute("/help");
            bool ok = Expect(engine.Output.Count >= 2, "echo + at least one result line");
            ok &= Expect(engine.Output[0].Severity == ConsoleLineSeverity.Info && engine.Output[0].Text == "/help", "output[0] is the Info echo of the submitted line");
            ok &= Expect(engine.Output[1].Text.StartsWith("Available commands"), "results follow the echo");
            return ok;
        }

        // --- Confirmation state machine ---

        /// <summary>Builds an engine plus a stub whose execution requests confirmation before running <paramref name="confirmed"/>.</summary>
        private static CommandEngine NewConfirmEngine(Func<CommandResult> confirmed, out StubCommand stub)
        {
            CommandEngine engine = new CommandEngine();
            stub = new StubCommand("danger", null, "/danger",
                (ctx, args) => CommandResult.Confirm("Really do the dangerous thing?", confirmed));
            engine.Registry.Register(stub);
            return engine;
        }

        private static bool Baseline_ConfirmYes()
        {
            bool ok = true;
            foreach (string reply in new[] { "yes", "y", "YES" })
            {
                int ran = 0;
                CommandEngine engine = NewConfirmEngine(() =>
                {
                    ran++;
                    return CommandResult.Info("executed");
                }, out _);
                engine.Execute("/danger");
                ok &= Expect(engine.HasPendingConfirmation, "pending after /danger");
                ok &= Expect(engine.Output[^1].Text.EndsWith(CommandEngine.ConfirmationSuffix), "prompt line carries the [yes/no] suffix");

                CommandResult result = engine.Execute(reply);
                ok &= Expect(ran == 1, $"continuation ran once on '{reply}'");
                ok &= Expect(result.Lines.Count == 1 && result.Lines[0].Text == "executed", "continuation's output returned");
                ok &= Expect(!engine.HasPendingConfirmation, "pending cleared");
            }

            return ok;
        }

        private static bool Baseline_ConfirmNo()
        {
            bool ok = true;
            foreach (string reply in new[] { "no", "n", "No" })
            {
                int ran = 0;
                CommandEngine engine = NewConfirmEngine(() =>
                {
                    ran++;
                    return CommandResult.Info("executed");
                }, out _);
                engine.Execute("/danger");
                CommandResult result = engine.Execute(reply);
                ok &= Expect(ran == 0, $"continuation did NOT run on '{reply}'");
                ok &= Expect(result.Lines.Count == 1 && result.Lines[0].Text == CommandEngine.ConfirmationCancelledNotice, "cancel notice returned");
                ok &= Expect(!engine.HasPendingConfirmation, "pending cleared");
            }

            return ok;
        }

        private static bool Baseline_ConfirmUnrelatedCancelsThenProcesses()
        {
            // Unrelated COMMAND: cancels, then /help runs normally.
            int ran = 0;
            CommandEngine engine = NewConfirmEngine(() =>
            {
                ran++;
                return CommandResult.Empty;
            }, out _);
            engine.Execute("/danger");
            CommandResult result = engine.Execute("/help");
            bool ok = Expect(ran == 0, "continuation did not run");
            ok &= Expect(!engine.HasPendingConfirmation, "pending cleared");
            ok &= Expect(result.Lines.Count > 0 && result.Lines[0].Text.StartsWith("Available commands"), "the unrelated command executed normally");
            bool sawNotice = false;
            for (int i = 0; i < engine.Output.Count; i++)
                sawNotice |= engine.Output[i].Text == CommandEngine.ConfirmationCancelledNotice;
            ok &= Expect(sawNotice, "cancel notice was emitted to the output ring");

            // Unrelated UNPREFIXED text: the confirmation check runs BEFORE the '/' rule —
            // cancels first, then the normal pipeline rejects the unprefixed line.
            engine.Execute("/danger");
            CommandResult rejected = engine.Execute("hello world");
            ok &= Expect(ran == 0, "continuation still did not run");
            ok &= Expect(rejected.Lines.Count == 1 && rejected.Lines[0].Text == CommandEngine.UnprefixedHint, "unprefixed unrelated input then hits the prefix rule");
            ok &= Expect(!engine.HasPendingConfirmation, "pending cleared again");
            return ok;
        }

        private static bool Baseline_ConfirmNested()
        {
            int ran = 0;
            CommandEngine engine = NewConfirmEngine(
                () => CommandResult.Confirm("Absolutely sure?", () =>
                {
                    ran++;
                    return CommandResult.Info("done");
                }),
                out _);
            engine.Execute("/danger");
            engine.Execute("yes");
            bool ok = Expect(engine.HasPendingConfirmation, "second confirmation pending after the first yes");
            ok &= Expect(ran == 0, "inner continuation not yet run");
            CommandResult result = engine.Execute("yes");
            ok &= Expect(ran == 1 && result.Lines.Count == 1 && result.Lines[0].Text == "done", "inner continuation ran on the second yes");
            ok &= Expect(!engine.HasPendingConfirmation, "no pending left");
            return ok;
        }

        // --- History ---

        private static bool Baseline_HistoryRecall()
        {
            CommandEngine engine = new CommandEngine();
            engine.Execute("/help");
            engine.Execute("/first");
            engine.Execute("/second");

            bool ok = Expect(engine.RecallPrevious() == "/second", "↑ recalls the newest submission");
            ok &= Expect(engine.RecallPrevious() == "/first", "↑ again walks back");
            ok &= Expect(engine.RecallPrevious() == "/help", "↑ reaches the oldest");
            ok &= Expect(engine.RecallPrevious() == "/help", "↑ clamps at the oldest");
            ok &= Expect(engine.RecallNext() == "/first", "↓ walks forward");
            ok &= Expect(engine.RecallNext() == "/second", "↓ reaches the newest");
            ok &= Expect(engine.RecallNext() == null, "↓ past the newest returns null (live line)");

            engine.Execute("/third");
            ok &= Expect(engine.RecallPrevious() == "/third", "submit resets the recall cursor to the newest");

            ok &= Expect(new CommandEngine().RecallPrevious() == null, "↑ with no history returns null");
            return ok;
        }

        private static bool Baseline_CommandHistoryWrap()
        {
            CommandEngine engine = new CommandEngine();
            const int total = COMMAND_HISTORY_CAPACITY + 6;
            for (int i = 0; i < total; i++)
                engine.Execute($"/c{i}");

            bool ok = Expect(engine.CommandHistory.Count == COMMAND_HISTORY_CAPACITY,
                $"history holds exactly {COMMAND_HISTORY_CAPACITY}, got {engine.CommandHistory.Count}");
            ok &= Expect(engine.CommandHistory[0] == "/c6", $"oldest retained is /c6, got '{engine.CommandHistory[0]}'");
            ok &= Expect(engine.CommandHistory[^1] == $"/c{total - 1}", "newest retained is the last submission");
            return ok;
        }

        private static bool Baseline_OutputRingWrap()
        {
            CommandEngine engine = new CommandEngine();
            // Every unknown command contributes exactly 2 lines: the echo and the error.
            const int SUBMISSIONS = 200;
            for (int i = 0; i < SUBMISSIONS; i++)
                engine.Execute($"/c{i}");

            const int totalLines = SUBMISSIONS * 2;
            const int dropped = totalLines - OUTPUT_CAPACITY;
            bool ok = Expect(engine.Output.Count == OUTPUT_CAPACITY, $"output holds exactly {OUTPUT_CAPACITY}, got {engine.Output.Count}");
            // Line index `dropped` is the first retained: even indices are echoes of submission index/2.
            ok &= Expect(engine.Output[0].Text == $"/c{dropped / 2}", $"oldest retained line is the echo of submission {dropped / 2}, got '{engine.Output[0].Text}'");
            ok &= Expect(engine.Output[^1].Severity == ConsoleLineSeverity.Error, "newest retained line is the last error");
            return ok;
        }

        // --- Help, registry, selectors ---

        private static bool Baseline_HelpListsRegistry()
        {
            CommandEngine engine = new CommandEngine();
            engine.Registry.Register(new StubCommand("test", new[] { "t2" }, "/test <x>", null));

            CommandResult result = engine.Execute("/help");
            bool ok = Expect(result.Lines.Count == 3, $"header + 2 commands, got {result.Lines.Count}");
            ok &= Expect(result.Lines[0].Text == "Available commands (2):", "header counts the registry");
            ok &= Expect(result.Lines[1].Text.Contains("/help"), "sorted: /help first");
            ok &= Expect(result.Lines[2].Text.Contains("/test <x>") && result.Lines[2].Text.Contains("aliases: t2"), "usage + aliases shown");
            return ok;
        }

        private static bool Baseline_DuplicateRegistrationThrows()
        {
            CommandEngine engine = new CommandEngine();
            engine.Registry.Register(new StubCommand("test", new[] { "t2" }, "/test", null));

            bool ok = Expect(Throws(() => engine.Registry.Register(new StubCommand("help", null, "/help", null))),
                "re-registering an existing NAME throws");
            ok &= Expect(Throws(() => engine.Registry.Register(new StubCommand("TEST", null, "/x", null))),
                "name clash is case-insensitive");
            ok &= Expect(Throws(() => engine.Registry.Register(new StubCommand("other", new[] { "t2" }, "/other", null))),
                "an ALIAS clashing with an existing alias throws");
            ok &= Expect(Throws(() => engine.Registry.Register(new StubCommand("other", new[] { "help" }, "/other", null))),
                "an alias clashing with an existing name throws");
            return ok;
        }

        private static bool Baseline_SelectorResolver()
        {
            TargetSelectorResolver resolver = new TargetSelectorResolver();
            CommandToken player = new CommandToken(CommandTokenType.Selector, "@player");
            CommandToken upper = new CommandToken(CommandTokenType.Selector, "@PLAYER");
            CommandToken entity = new CommandToken(CommandTokenType.Selector, "@entity-5");

            bool ok = Expect(resolver.TryResolve(player, out CommandTarget target, out _) && target.Kind == CommandTargetKind.LocalPlayer,
                "@player resolves to LocalPlayer");
            ok &= Expect(resolver.TryResolve(upper, out _, out _), "@PLAYER resolves case-insensitively");
            ok &= Expect(!resolver.TryResolve(entity, out _, out string error) && error.Contains("Unknown target"),
                "@entity-5 is an unknown target in v1");
            return ok;
        }

        // --- CMD-1: ConsoleTextFormatter (the UI's pure formatting seam) ---

        private static bool Baseline_FormatterSeverityColors()
        {
            bool ok = Expect(
                UI.ConsoleTextFormatter.Format(new ConsoleLine(ConsoleLineSeverity.Info, "hello"))
                == $"<color={UI.ConsoleTextFormatter.InfoColor}><noparse>hello</noparse></color>",
                "Info line formats with the Info color");
            ok &= Expect(UI.ConsoleTextFormatter.ColorOf(ConsoleLineSeverity.Warning) == UI.ConsoleTextFormatter.WarningColor,
                "Warning maps to WarningColor");
            ok &= Expect(UI.ConsoleTextFormatter.ColorOf(ConsoleLineSeverity.Error) == UI.ConsoleTextFormatter.ErrorColor,
                "Error maps to ErrorColor");
            return ok;
        }

        private static bool Baseline_FormatterNoparseGuard()
        {
            string formatted = UI.ConsoleTextFormatter.Format(
                new ConsoleLine(ConsoleLineSeverity.Info, "sneaky </noparse><color=red>injected"));
            bool ok = Expect(!formatted.Contains("</noparse><color=red>"),
                "a literal </noparse> in user text cannot terminate the guard");
            ok &= Expect(formatted.Contains("<noparse>sneaky <color=red>injected</noparse>"),
                "the rest of the text (markup included) stays inert inside the guard");
            ok &= Expect(UI.ConsoleTextFormatter.Format(new ConsoleLine(ConsoleLineSeverity.Error, null))
                         == $"<color={UI.ConsoleTextFormatter.ErrorColor}><noparse></noparse></color>",
                "null text formats as an empty guarded string");
            return ok;
        }

        // --- CMD-1: input-bypass tripwire ---

        /// <summary>Files allowed to read <c>Keyboard.current</c> — the single input choke point.</summary>
        private static readonly string[] s_keyboardReadAllowlist = { "InputManager.cs" };

        /// <summary>
        /// B23: scans every runtime script for direct <c>Keyboard.current</c> reads. The console's
        /// input blocking works by disabling the Gameplay action map, which only gates consumers
        /// that route through <c>InputManager</c> — a direct device read silently bypasses it
        /// (the original bug: typing "/help" fired the L-key lighting benchmark). Debug/benchmark
        /// trigger keys must use <c>InputManager.DebugKeyPressed</c>.
        /// </summary>
        private static bool Baseline_KeyboardBypassTripwire()
        {
            string scriptsRoot = Path.Combine(Application.dataPath, "Scripts");
            bool ok = true;
            foreach (string file in Directory.GetFiles(scriptsRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (!File.ReadAllText(file).Contains("Keyboard.current"))
                    continue;

                string fileName = Path.GetFileName(file);
                ok &= Expect(Array.IndexOf(s_keyboardReadAllowlist, fileName) >= 0,
                    $"{fileName} reads Keyboard.current directly — the console's Gameplay-map disable cannot gate it; route through InputManager.DebugKeyPressed instead");
            }

            return ok;
        }

        /// <summary>Whether <paramref name="action"/> throws an <see cref="ArgumentException"/>.</summary>
        /// <param name="action">The action expected to throw.</param>
        /// <returns>True when it threw.</returns>
        private static bool Throws(Action action)
        {
            try
            {
                action();
            }
            catch (ArgumentException)
            {
                return true;
            }

            return false;
        }
    }
}
