using System.Collections.Generic;
using Commands;
using UI;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Commands
{
    /// <summary>
    /// The CMD-5 Tab-completion baselines (§8.3): the pure <see cref="CommandEngine.Complete"/> core —
    /// command-name completion (single → trailing space, multi → longest common prefix, case-insensitive,
    /// full-pack coverage) and opt-in <see cref="IArgumentCompleter"/> argument completion (block names,
    /// <c>off</c>), driven headless. The UI wiring (Tab action, field text) is verified in-game per §7.
    /// </summary>
    public static partial class CommandConsoleValidationSuite
    {
        static partial void AddCompletionScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("B48: Complete — a unique command-name prefix completes with a trailing space; case-insensitive; no match is unchanged; empty/'/' offer the full pack (CMD-5)", Completion_CommandNames));
            scenarios.Add(new Scenario("B49: Complete — an ambiguous command-name prefix advances only to the longest common prefix and lists every candidate (CMD-5)", Completion_CommandNameCommonPrefix));
            scenarios.Add(new Scenario("B50: Complete — opt-in argument completion fills block names (canonical case) for /give arg0 and /setblock arg3; a non-completing slot is a no-op (CMD-5)", Completion_ArgumentBlockNames));
            scenarios.Add(new Scenario("B51: Complete — /set-world-border completes 'off'; a command without a completer and chat-reserved input are no-ops (CMD-5)", Completion_OptInBoundary));
            scenarios.Add(new Scenario("B52: Suggest — a unique prefix yields the trimmed inline ghost suffix; an ambiguous/empty/fully-typed input yields nothing (CMD-5)", Completion_InlineSuggestion));
            scenarios.Add(new Scenario("B53: StripNoparse — removes every </noparse> incl. one spliced from surrounding text (loop-until-stable); null/empty safe (CMD-5 ghost guard)", Completion_StripNoparseGuard));
            scenarios.Add(new Scenario("B54: Complete — a leading space no longer defeats completion (trimmed like Execute); trailing space stays semantic (CMD-5)", Completion_LeadingWhitespaceTrimmed));
        }

        /// <summary>An engine with the full production pack registered (no world facade — name completion needs none).</summary>
        /// <returns>The engine.</returns>
        private static CommandEngine NewPackEngine()
        {
            CommandEngine engine = new CommandEngine();
            ConsoleCommandInstaller.RegisterAll(engine.Registry);
            return engine;
        }

        private static bool Completion_CommandNames()
        {
            CommandEngine engine = NewPackEngine();

            CommandCompletion give = engine.Complete("/gi");
            bool ok = Expect(give.CompletedText == "/give " && give.Candidates.Length == 1 && give.Candidates[0] == "give",
                $"'/gi' completes to the single '/give ' (trailing space), got '{give.CompletedText}' with {give.Candidates.Length} candidates");

            CommandCompletion caseInsensitive = engine.Complete("/GI");
            ok &= Expect(caseInsensitive.CompletedText == "/give ",
                $"name matching is case-insensitive and emits canonical case, got '{caseInsensitive.CompletedText}'");

            CommandCompletion noMatch = engine.Complete("/zzz");
            ok &= Expect(noMatch.CompletedText == "/zzz" && noMatch.Candidates.Length == 0,
                "an unmatched command prefix leaves the input unchanged with no candidates");

            // The full pack is visible to completion: '/' and '' both offer every registered command.
            const int expected = ConsoleCommandInstaller.InstalledCommandCount + 1; // + the engine's own /help
            CommandCompletion slash = engine.Complete("/");
            ok &= Expect(slash.Candidates.Length == expected,
                $"'/' offers every registered command ({expected}), got {slash.Candidates.Length}");
            CommandCompletion empty = engine.Complete("");
            ok &= Expect(empty.Candidates.Length == expected,
                $"empty input offers the full command list ({expected}), got {empty.Candidates.Length}");
            return ok;
        }

        private static bool Completion_CommandNameCommonPrefix()
        {
            CommandEngine engine = NewPackEngine();

            // 's': seed, setblock, setspawn, spawn, speed, set-world-border → shared prefix 's' only.
            CommandCompletion s = engine.Complete("/s");
            bool ok = Expect(s.CompletedText == "/s", $"'/s' stays at the common prefix 's', got '{s.CompletedText}'");
            ok &= Expect(s.Candidates.Length >= 5, $"the 's' commands are all listed, got {s.Candidates.Length}");

            // 'set': setblock, setspawn, set-world-border → shared prefix 'set'; 'spawn' is excluded.
            CommandCompletion set = engine.Complete("/set");
            ok &= Expect(set.CompletedText == "/set", $"'/set' advances to the common prefix 'set', got '{set.CompletedText}'");
            bool hasSetblock = false, excludesSpawn = true;
            foreach (string c in set.Candidates)
            {
                if (c == "setblock") hasSetblock = true;
                if (c == "spawn") excludesSpawn = false;
            }

            ok &= Expect(hasSetblock && excludesSpawn, "'set' candidates include 'setblock' but not 'spawn'");
            return ok;
        }

        private static bool Completion_ArgumentBlockNames()
        {
            using CommandTeleportTestWorld stub = new CommandTeleportTestWorld();

            // /give arg 0: 'st' → the single 'Stone' (case-corrected) with a trailing space.
            CommandCompletion give = stub.Engine.Complete("/give st");
            bool ok = Expect(give.CompletedText == "/give Stone " && give.Candidates.Length == 1 && give.Candidates[0] == "Stone",
                $"'/give st' completes to the canonical 'Stone ', got '{give.CompletedText}'");

            // A fresh argument slot (trailing space) lists candidates; the fixture DB has Air + Stone,
            // whose common prefix is empty, so the text is unchanged but both are offered.
            CommandCompletion giveFresh = stub.Engine.Complete("/give ");
            ok &= Expect(giveFresh.CompletedText == "/give " && giveFresh.Candidates.Length == 2,
                $"'/give ' lists every block name without a shared prefix, got {giveFresh.Candidates.Length} candidates");

            // /setblock completes the block at arg index 3, leaving the coordinates intact.
            CommandCompletion setblock = stub.Engine.Complete("/setblock 5 64 5 st");
            ok &= Expect(setblock.CompletedText == "/setblock 5 64 5 Stone " && setblock.Candidates.Length == 1,
                $"'/setblock 5 64 5 st' completes the block at index 3, got '{setblock.CompletedText}'");

            // A coordinate slot (index < 3) has no completer → no-op.
            CommandCompletion coord = stub.Engine.Complete("/setblock 5 6");
            ok &= Expect(coord.CompletedText == "/setblock 5 6" && coord.Candidates.Length == 0,
                "a /setblock coordinate argument offers nothing (no completer for that index)");
            return ok;
        }

        private static bool Completion_OptInBoundary()
        {
            CommandEngine engine = NewPackEngine();

            // /set-world-border opts in for 'off' at arg 0.
            CommandCompletion off = engine.Complete("/set-world-border o");
            bool ok = Expect(off.CompletedText == "/set-world-border off " && off.Candidates.Length == 1 && off.Candidates[0] == "off",
                $"'/set-world-border o' completes to 'off ', got '{off.CompletedText}'");

            // A command that does NOT implement IArgumentCompleter is a no-op on its arguments.
            CommandCompletion noCompleter = engine.Complete("/seed ");
            ok &= Expect(noCompleter.CompletedText == "/seed " && noCompleter.Candidates.Length == 0,
                "a command without an argument completer leaves its argument unchanged");

            // Chat-reserved (unprefixed, non-empty) input is never completed.
            CommandCompletion chat = engine.Complete("hello");
            ok &= Expect(chat.CompletedText == "hello" && chat.Candidates.Length == 0,
                "unprefixed chat-reserved input is left untouched");
            return ok;
        }

        private static bool Completion_InlineSuggestion()
        {
            CommandEngine engine = NewPackEngine();

            // A unique command-name prefix ghosts the trimmed remainder of the name.
            bool ok = Expect(engine.Suggest("/gi") == "ve", $"'/gi' suggests 've', got '{engine.Suggest("/gi")}'");
            ok &= Expect(engine.Suggest("/GI") == "ve", "the ghost is case-insensitive on the prefix");

            // Ambiguous / empty / already-complete inputs show no inline ghost.
            ok &= Expect(engine.Suggest("/s") == "", "an ambiguous prefix suggests nothing inline (Tab lists it instead)");
            ok &= Expect(engine.Suggest("") == "", "empty input suggests nothing");
            ok &= Expect(engine.Suggest("/give") == "", "a fully-typed unique name suggests nothing (only a trailing space, trimmed away)");

            // Argument ghosts come from the opt-in completer (no world needed for the 'off' literal).
            ok &= Expect(engine.Suggest("/set-world-border o") == "ff", $"'/set-world-border o' suggests 'ff', got '{engine.Suggest("/set-world-border o")}'");

            // Block-name argument ghost needs the fixture's block database.
            using CommandTeleportTestWorld stub = new CommandTeleportTestWorld();
            ok &= Expect(stub.Engine.Suggest("/give st") == "one", $"'/give st' suggests block-name remainder 'one', got '{stub.Engine.Suggest("/give st")}'");
            ok &= Expect(stub.Engine.Suggest("/give ") == "", "a fresh slot with multiple block candidates suggests nothing inline");
            return ok;
        }

        private static bool Completion_StripNoparseGuard()
        {
            // Plain text is untouched.
            bool ok = Expect(ConsoleTextFormatter.StripNoparse("give stone") == "give stone",
                "plain text is unchanged");

            // A literal </noparse> is removed so it cannot terminate the guard.
            ok &= Expect(ConsoleTextFormatter.StripNoparse("a</noparse>b") == "ab",
                "a literal </noparse> is stripped");

            // Re-creation edge: a single pass would splice a fresh </noparse> from the surrounding
            // characters; the loop must remove that too.
            ok &= Expect(ConsoleTextFormatter.StripNoparse("</nop</noparse>arse>") == "",
                "a </noparse> spliced from surrounding text is also removed (loop-until-stable)");

            // Null/empty are safe and normalize to empty.
            ok &= Expect(ConsoleTextFormatter.StripNoparse(null) == "" && ConsoleTextFormatter.StripNoparse("") == "",
                "null/empty yield empty");
            return ok;
        }

        private static bool Completion_LeadingWhitespaceTrimmed()
        {
            CommandEngine engine = NewPackEngine();

            // A leading space must not defeat completion — it should behave like the trimmed line.
            CommandCompletion spaced = engine.Complete("  /gi");
            CommandCompletion clean = engine.Complete("/gi");
            bool ok = Expect(spaced.CompletedText == clean.CompletedText && spaced.CompletedText == "/give ",
                $"a leading space completes like the trimmed line, got '{spaced.CompletedText}'");
            ok &= Expect(spaced.Candidates.Length == 1, "the leading-space input still finds the single candidate");

            // Leading whitespace before the '/' still enters command-name completion.
            const int expected = ConsoleCommandInstaller.InstalledCommandCount + 1;
            CommandCompletion spacedSlash = engine.Complete("   /");
            ok &= Expect(spacedSlash.Candidates.Length == expected,
                $"leading whitespace before '/' still offers the full pack ({expected}), got {spacedSlash.Candidates.Length}");
            return ok;
        }
    }
}
