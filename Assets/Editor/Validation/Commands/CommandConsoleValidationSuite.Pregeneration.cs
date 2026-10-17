using System.Collections.Generic;
using Commands;
using Data;
using Helpers;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;
using StartRefusal = Helpers.PregenerationPlan.StartRefusal;

namespace Editor.Validation.Commands
{
    /// <summary>
    /// The CMD-6 <c>/chunk generate</c> baselines (design CHUNK_PREGENERATION.md §3.3): the grammar and arity tiers, the
    /// radius and corner forms, the confirmation tier, the refusal texts, <c>cancel</c>, and the progress and outcome
    /// lines <c>WorldUIManager</c> posts. Driven against <see cref="CommandTeleportTestWorld"/>, where no run can ever
    /// start in edit mode: every start ends in a refusal, which is how the "start was reached" assertions read it.
    /// <para><b>Prove-red (by temporary mutation of <see cref="ChunkCommand"/>):</b> replacing the arity error text reds
    /// B59; dropping the addressable-world bound on the radius reds B60; forbidding relative corners reds B61;
    /// confirming at <c>&gt;=</c> instead of <c>&gt;</c> the confirmation count reds B62; giving the persistence
    /// refusal the no-world text reds B63; turning the no-active-run error into Info reds B64; turning the stalled
    /// outcome line into Info reds B65.</para>
    /// </summary>
    public static partial class CommandConsoleValidationSuite
    {
        /// <summary>The player's Unity position for the radius and corner forms: voxel (40, -21), chunk (2, -2).</summary>
        private const float PREGEN_PLAYER_X = 40.5f;

        private const float PREGEN_PLAYER_Y = 64f;
        private const float PREGEN_PLAYER_Z = -20.5f;

        /// <summary>The text <see cref="ChunkCommand.DescribeRefusal"/> falls back to for an unmapped refusal.</summary>
        private const string PREGEN_FALLBACK_PREFIX = "Pre-generation refused:";

        private const string PREGEN_NO_WORLD = "No world is loaded.";

        /// <summary>Mirrors <c>CommandArgUtility.RelativeNeedsPlayerError</c>, which is internal to the runtime assembly.</summary>
        private const string PREGEN_RELATIVE_NEEDS_PLAYER = "Relative coordinates require a loaded player.";

        static partial void AddPregenerationScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("B59: /chunk generate — grammar and arity errors; graceful without a world (CMD-6 PG-3)", Pregen_GrammarAndArity));
            scenarios.Add(new Scenario("B60: /chunk generate <radius> — square centered on the player's chunk; no size cap, addressable-world and no-player refusals (CMD-6 PG-3)", Pregen_RadiusForm));
            scenarios.Add(new Scenario("B61: /chunk generate <x1> <z1> <x2> <z2> — any corner order, negative floor division, '~' player-relative (CMD-6 PG-3)", Pregen_CornerForm));
            scenarios.Add(new Scenario("B62: /chunk generate confirmation tier — above 10,000 chunks asks; 'no' starts nothing, 'yes' reaches the start (CMD-6 PG-3)", Pregen_ConfirmationTier));
            scenarios.Add(new Scenario("B63: /chunk generate refusals — a huge area asks instead of refusing; border, no world, persistence off; disk estimate; every StartRefusal has distinct text (CMD-6 PG-3)", Pregen_Refusals));
            scenarios.Add(new Scenario("B64: /chunk generate cancel with no active run is an error (CMD-6 PG-3)", Pregen_CancelWithoutRun));
            scenarios.Add(new Scenario("B65: Pre-generation console lines — one line per new 10 % step; outcome severity and text per status (CMD-6 PG-3)", Pregen_ConsoleLines));
        }

        private static bool Pregen_GrammarAndArity()
        {
            using (CommandTeleportTestWorld stub = new CommandTeleportTestWorld())
            {
                bool ok = ExpectTeleportError(stub.Engine, "/chunk generate", "Usage", "a bare subcommand is a usage error");
                ok &= ExpectTeleportError(stub.Engine, "/chunk generate 1 2", "Expected a radius, two corners", "2 numbers is an arity error");
                ok &= ExpectTeleportError(stub.Engine, "/chunk generate 1 2 3", "Expected a radius, two corners", "3 numbers is an arity error");
                ok &= ExpectTeleportError(stub.Engine, "/chunk generate 1 2 3 4 5", "Expected a radius, two corners", "5 numbers is an arity error");
                ok &= ExpectTeleportError(stub.Engine, "/chunk generate cancel now", "Expected a radius, two corners", "cancel takes no argument");
                ok &= ExpectTeleportError(stub.Engine, "/chunk info extra", "Usage", "info takes no argument");

                if (!ok) return false;
            }

            // Grammar passes, then the world check: a worldless context must fail gracefully, not throw.
            CommandEngine worldless = new CommandEngine();
            ConsoleCommandInstaller.RegisterAll(worldless.Registry);
            bool noWorldOk = ExpectChunkLine(worldless, "/chunk generate 4", PREGEN_NO_WORLD, "the radius form without a world");
            noWorldOk &= ExpectChunkLine(worldless, "/chunk generate cancel", PREGEN_NO_WORLD, "cancel without a world");
            noWorldOk &= ExpectChunkLine(worldless, "/chunk generate 1 2 3 4", PREGEN_NO_WORLD, "the corner form without a world");
            return noWorldOk;
        }

        private static bool Pregen_RadiusForm()
        {
            using CommandTeleportTestWorld stub = new CommandTeleportTestWorld();
            PlacePregenPlayer(stub);

            string square = PromptOf(stub.Engine, "/chunk generate 50");
            bool ok = Expect(square != null && square.Contains("10,201 chunks") && square.Contains("chunks (-48, -52) to (52, 48)"),
                $"radius 50 is the 101 x 101 square around chunk (2, -2) and asks to confirm, got '{square}'");

            // 99 x 99 = 9,801 is under the confirmation count, so the start is reached directly (refused: no world).
            ok &= ExpectChunkLine(stub.Engine, "/chunk generate 49", PREGEN_NO_WORLD, "radius 49 starts without a confirmation");
            ok &= ExpectChunkLine(stub.Engine, "/chunk generate 0", PREGEN_NO_WORLD, "radius 0 parses");
            // No size cap: a radius whose square holds 251,001 chunks still only asks.
            string large = PromptOf(stub.Engine, "/chunk generate 250");
            ok &= Expect(large != null && large.Contains("251,001 chunks"),
                $"a large radius asks to confirm rather than refusing, got '{large}'");

            // The one bound is the addressable world, checked before the square is built so the corner math cannot overflow.
            const string pastWorld = "reaches past the addressable world";
            // From chunk (2, -2), 134,217,723 reaches the last addressable chunk (134,217,725); one more is past it.
            ok &= Expect(PromptOf(stub.Engine, "/chunk generate 134217723") != null, "the largest addressable radius still asks");
            ok &= ExpectChunkError(stub.Engine, "/chunk generate 134217724", pastWorld, "a radius past the addressable world");
            ok &= ExpectChunkError(stub.Engine, "/chunk generate 2147483647", pastWorld, "int.MaxValue as a radius");

            ok &= ExpectChunkLine(stub.Engine, "/chunk generate -1", "Radius must be 0 or more.", "a negative radius");
            ok &= ExpectChunkLine(stub.Engine, "/chunk generate 1.5", "Radius must be a whole number of chunks.", "a fractional radius");
            ok &= ExpectChunkLine(stub.Engine, "/chunk generate abc", "Radius must be a whole number of chunks.", "a word radius");

            object playerTransform = Framework.ValidationReflection.GetInstanceField(stub.World, "_playerTransform");
            try
            {
                Framework.ValidationReflection.SetInstanceField(stub.World, "_playerTransform", null);
                ok &= ExpectChunkLine(stub.Engine, "/chunk generate 4", "No player is loaded.", "the radius form needs a player");
            }
            finally
            {
                Framework.ValidationReflection.SetInstanceField(stub.World, "_playerTransform", playerTransform);
            }

            return ok;
        }

        private static bool Pregen_CornerForm()
        {
            using CommandTeleportTestWorld stub = new CommandTeleportTestWorld();
            PlacePregenPlayer(stub);

            // x: 1615 -> 100 and -1600 -> -100 (floor division); z: -17 -> -2 and 1600 -> 100.
            string forward = PromptOf(stub.Engine, "/chunk generate 1615 -17 -1600 1600");
            string swapped = PromptOf(stub.Engine, "/chunk generate -1600 1600 1615 -17");
            bool ok = Expect(forward != null && forward.Contains("20,703 chunks") && forward.Contains("chunks (-100, -2) to (100, 100)"),
                $"two corners floor-divide to the covering chunk rectangle, got '{forward}'");
            ok &= Expect(forward == swapped, $"corner order does not matter, got '{forward}' vs '{swapped}'");

            // '~' is the player's voxel (40, -21): chunk (2, -2), and +1600 lands in chunks 102 and 98.
            string relative = PromptOf(stub.Engine, "/chunk generate ~ ~ ~1600 ~1600");
            ok &= Expect(relative != null && relative.Contains("chunks (2, -2) to (102, 98)"),
                $"'~' resolves against the player's voxel cell, got '{relative}'");

            ok &= ExpectTeleportError(stub.Engine, "/chunk generate 1.5 0 0 0", "X1 must be an integer voxel coordinate",
                "a fractional corner is rejected");

            object playerTransform = Framework.ValidationReflection.GetInstanceField(stub.World, "_playerTransform");
            try
            {
                Framework.ValidationReflection.SetInstanceField(stub.World, "_playerTransform", null);
                ok &= ExpectChunkLine(stub.Engine, "/chunk generate ~ 0 0 0", PREGEN_RELATIVE_NEEDS_PLAYER,
                    "'~' needs a player");
            }
            finally
            {
                Framework.ValidationReflection.SetInstanceField(stub.World, "_playerTransform", playerTransform);
            }

            return ok;
        }

        private static bool Pregen_ConfirmationTier()
        {
            using CommandTeleportTestWorld stub = new CommandTeleportTestWorld();

            // 100 x 100 = exactly 10,000 chunks: the count itself is not above the tier.
            bool ok = ExpectChunkLine(stub.Engine, "/chunk generate 0 0 1599 1599", PREGEN_NO_WORLD,
                "exactly the confirmation count starts without asking");

            // 100 x 101 = 10,100.
            const string overTier = "/chunk generate 0 0 1599 1615";
            CommandResult asked = stub.Engine.Execute(overTier);
            ok &= Expect(asked.Pending != null && asked.Pending.Prompt.Contains("10,100 chunks") &&
                         asked.Pending.Prompt.Contains("chunks (0, 0) to (99, 100)") &&
                         asked.Pending.Prompt.Contains("(25 stations)") && asked.Pending.Prompt.Contains("on disk") &&
                         asked.Pending.Prompt.Contains("resumes where it stopped"),
                $"one chunk row past the tier asks for confirmation with the station count, disk estimate and resume " +
                $"note, got '{asked.Pending?.Prompt}'");

            CommandResult declined = stub.Engine.Execute("no");
            ok &= Expect(!stub.Engine.HasPendingConfirmation && !ContainsLine(declined, PREGEN_NO_WORLD),
                "'no' starts nothing and clears the pending confirmation");

            stub.Engine.Execute(overTier);
            CommandResult accepted = stub.Engine.Execute("yes");
            ok &= Expect(accepted.Lines.Count > 0 && accepted.Lines[0].Severity == ConsoleLineSeverity.Error &&
                         accepted.Lines[0].Text == PREGEN_NO_WORLD,
                $"'yes' runs the start, which this fixture refuses, got '{(accepted.Lines.Count > 0 ? accepted.Lines[0].Text : "no lines")}'");
            return ok;
        }

        private static bool Pregen_Refusals()
        {
            using CommandTeleportTestWorld stub = new CommandTeleportTestWorld();

            // 501 x 501 = 251,001: no size cap, so it asks, with the disk estimate band (4.6-6.4 KB per chunk).
            string huge = PromptOf(stub.Engine, "/chunk generate 0 0 8000 8000");
            bool ok = Expect(huge != null && huge.Contains("251,001 chunks") && huge.Contains("about 1.2–1.6 GB on disk"),
                $"a huge area asks with its disk estimate instead of being refused, got '{huge}'");

            try
            {
                stub.SetBorderRadius(100);
                ok &= ExpectChunkLine(stub.Engine, "/chunk generate 0 0 200 200", "Part of the area lies outside the world border.",
                    "a corner outside the border");
            }
            finally
            {
                stub.SetBorderRadius(0);
            }

            ok &= ExpectChunkLine(stub.Engine, "/chunk generate 0 0 15 15", PREGEN_NO_WORLD, "no world loaded");

            try
            {
                Framework.ValidationReflection.SetInstanceField(stub.World, "_isWorldLoaded", true);
                ok &= ExpectChunkLine(stub.Engine, "/chunk generate 0 0 15 15",
                    "Chunk persistence is disabled, so generated chunks could not be saved.", "persistence off");
            }
            finally
            {
                Framework.ValidationReflection.SetInstanceField(stub.World, "_isWorldLoaded", false);
            }

            ok &= Expect(ChunkCommand.DescribeDiskEstimate(10_001, false, 0) == "about 46–64 MB on disk",
                $"the estimate band in MB, got '{ChunkCommand.DescribeDiskEstimate(10_001, false, 0)}'");
            ok &= Expect(ChunkCommand.DescribeDiskEstimate(4_000_000, false, 0) == "about 18.4–25.6 GB on disk",
                $"the estimate band in GB, got '{ChunkCommand.DescribeDiskEstimate(4_000_000, false, 0)}'");
            ok &= Expect(ChunkCommand.DescribeDiskEstimate(10_001, true, 50_000_000) ==
                         "about 46–64 MB on disk — more than the 50 MB free on the save drive",
                $"an estimate above the free space says so, got '{ChunkCommand.DescribeDiskEstimate(10_001, true, 50_000_000)}'");
            ok &= Expect(ChunkCommand.DescribeDiskEstimate(10_001, true, 1_000_000_000) == "about 46–64 MB on disk",
                "an estimate that fits adds no warning");

            HashSet<string> texts = new HashSet<string>();
            foreach (StartRefusal refusal in System.Enum.GetValues(typeof(StartRefusal)))
            {
                if (refusal == StartRefusal.None) continue;

                string text = ChunkCommand.DescribeRefusal(refusal);
                ok &= Expect(!string.IsNullOrEmpty(text) && !text.StartsWith(PREGEN_FALLBACK_PREFIX),
                    $"{refusal} has its own refusal text, got '{text}'");
                ok &= Expect(texts.Add(text), $"{refusal} has a refusal text no other refusal shares, got '{text}'");
            }

            return ok;
        }

        private static bool Pregen_CancelWithoutRun()
        {
            using CommandTeleportTestWorld stub = new CommandTeleportTestWorld();

            bool ok = ExpectChunkError(stub.Engine, "/chunk generate cancel", "No pre-generation run is active.", "cancel with no run");
            ok &= ExpectChunkError(stub.Engine, "/chunk generate CANCEL", "No pre-generation run is active.", "the subcommand word is case-insensitive");
            return ok;
        }

        private static bool Pregen_ConsoleLines()
        {
            static string Progress(long checkedStations, long stations, long generated, double elapsed, ref int step) =>
                ChunkCommand.ProgressLine(new PregenerationProgressReport(checkedStations, stations, generated, elapsed),
                    ref step);

            int lastStep = 0;
            bool ok = Expect(Progress(0, 100, 0, 0.0, ref lastStep) == null && lastStep == 0,
                "nothing checked yet posts no progress");

            ok &= Expect(Progress(5, 100, 10, 1.0, ref lastStep) == null && lastStep == 0,
                "5 of 100 stations is still below the first 10 % step");

            // 10 of 100 in 20 s: 90 left at the same pace is 180 s.
            string tenPercent = Progress(10, 100, 40, 20.0, ref lastStep);
            ok &= Expect(tenPercent == "Pre-generation 10 % — 10 of 100 stations checked, 40 chunks generated, about 3 min 0 s left." &&
                         lastStep == 1,
                $"the first step posts once with a pace-based estimate and advances lastStep, got '{tenPercent}' / {lastStep}");
            ok &= Expect(Progress(15, 100, 50, 25.0, ref lastStep) == null, "the same step posts only once");

            string thirty = Progress(35, 100, 90, 70.0, ref lastStep);
            ok &= Expect(thirty != null && thirty.Contains("30 %") && lastStep == 3,
                $"a jump over steps posts the newest one, got '{thirty}' / {lastStep}");
            ok &= Expect(Progress(35, 100, 90, 71.0, ref lastStep) == null, "an unchanged count posts nothing");
            ok &= Expect(Progress(100, 100, 200, 200.0, ref lastStep) == null, "100 % is left to the outcome line");

            string ninety = Progress(99, 100, 199, 198.0, ref lastStep);
            ok &= Expect(ninety != null && ninety.Contains("90 %"), $"99 of 100 is the 90 % step, got '{ninety}'");

            int fresh = 0;
            string hours = Progress(1_500, 10_000, 120_000, 4_000.0, ref fresh);
            ok &= Expect(hours != null && hours.Contains("1,500 of 10,000 stations checked") &&
                         hours.Contains("120,000 chunks generated") && hours.Contains("about 6 h 17 min left"),
                $"invariant thousands separators and an hours-scale estimate, got '{hours}'");

            ChunkRect threeByThree = new ChunkRect(new ChunkCoord(0, 0), new ChunkCoord(2, 2));
            ConsoleLine existing = ChunkCommand.EndedLine(new PregenerationOutcome
            {
                Status = PregenerationStatus.Completed,
                Rect = threeByThree,
                MissingFound = 0,
            });
            ok &= ExpectLine(existing, ConsoleLineSeverity.Info, "All 9 chunks already exist — nothing to generate.", "a run with nothing missing");

            ConsoleLine completed = ChunkCommand.EndedLine(new PregenerationOutcome
            {
                Status = PregenerationStatus.Completed,
                Rect = threeByThree,
                MissingFound = 42,
                Generated = 42,
                Stations = 3,
                ElapsedSeconds = 12.34,
            });
            ok &= ExpectLine(completed, ConsoleLineSeverity.Info,
                "Pre-generation complete: 42 chunks generated in 12.3 s over 3 stations.", "a completed run");

            ConsoleLine oneStation = ChunkCommand.EndedLine(new PregenerationOutcome
            {
                Status = PregenerationStatus.Completed,
                Rect = threeByThree,
                MissingFound = 9,
                Generated = 9,
                Stations = 1,
                ElapsedSeconds = 1.0,
            });
            ok &= ExpectLine(oneStation, ConsoleLineSeverity.Info,
                "Pre-generation complete: 9 chunks generated in 1.0 s over 1 station.", "a one-station run reads singular");

            PregenerationOutcome stalledOutcome = new PregenerationOutcome
            {
                Status = PregenerationStatus.CompletedWithStalls,
                Rect = threeByThree,
                Stalled = 2,
            };
            stalledOutcome.FirstStalled.Add(new ChunkCoord(1, 2));
            stalledOutcome.FirstStalled.Add(new ChunkCoord(3, 4));
            ConsoleLine stalled = ChunkCommand.EndedLine(stalledOutcome);
            ok &= Expect(stalled.Severity == ConsoleLineSeverity.Error && stalled.Text.Contains("2 chunks that never generated") &&
                         stalled.Text.Contains("ChunkCoord(1, 2)"),
                $"a stalled run is an Error naming the stalled chunks, got {stalled.Severity} '{stalled.Text}'");

            ConsoleLine canceled = ChunkCommand.EndedLine(new PregenerationOutcome
            {
                Status = PregenerationStatus.Canceled,
                Rect = threeByThree,
                CancelReason = "canceled by command",
                StationsChecked = 3,
                StationGridCount = 9,
            });
            ok &= Expect(canceled.Severity == ConsoleLineSeverity.Warning && canceled.Text.Contains("(canceled by command)") &&
                         canceled.Text.Contains("3 of 9 stations checked") && canceled.Text.Contains("resumes where it stopped"),
                $"a canceled run is a Warning with its reason, how far it got, and the resume note, got {canceled.Severity} '{canceled.Text}'");

            ConsoleLine noReason = ChunkCommand.EndedLine(new PregenerationOutcome
            {
                Status = PregenerationStatus.Canceled,
                Rect = threeByThree,
            });
            ok &= Expect(noReason.Text.Contains("no reason given"), $"a missing reason is spelled out, got '{noReason.Text}'");
            return ok;
        }

        /// <summary>Places the stub player at the Unity position the radius and corner forms are written against.</summary>
        /// <param name="stub">The fixture (identity origin, so Unity position equals voxel position).</param>
        private static void PlacePregenPlayer(CommandTeleportTestWorld stub)
        {
            stub.PlayerTransform.position = new Vector3(PREGEN_PLAYER_X, PREGEN_PLAYER_Y, PREGEN_PLAYER_Z);
        }

        /// <summary>Submits a line and returns the confirmation prompt it raised, declining it so none is left pending.</summary>
        /// <param name="engine">The engine to drive.</param>
        /// <param name="line">The line to submit.</param>
        /// <returns>The prompt, or null when no confirmation was raised.</returns>
        private static string PromptOf(CommandEngine engine, string line)
        {
            string prompt = engine.Execute(line).Pending?.Prompt;
            if (engine.HasPendingConfirmation)
                engine.Execute("no");
            return prompt;
        }

        /// <summary>Whether any line of a result has exactly the given text.</summary>
        private static bool ContainsLine(CommandResult result, string text)
        {
            foreach (ConsoleLine line in result.Lines)
            {
                if (line.Text == text) return true;
            }

            return false;
        }

        /// <summary>Asserts a console line's severity and exact text.</summary>
        /// <param name="line">The line under test.</param>
        /// <param name="severity">The expected severity.</param>
        /// <param name="text">The expected text.</param>
        /// <param name="label">The assertion label.</param>
        /// <returns>True when both match.</returns>
        private static bool ExpectLine(ConsoleLine line, ConsoleLineSeverity severity, string text, string label)
        {
            return Expect(line.Severity == severity && line.Text == text,
                $"{label} — expected {severity} '{text}', got {line.Severity} '{line.Text}'");
        }

        /// <summary>
        /// Submits a line expected to answer with an Error whose first line is exactly <paramref name="text"/> and to
        /// raise no confirmation.
        /// </summary>
        /// <param name="engine">The engine to drive.</param>
        /// <param name="line">The line to submit.</param>
        /// <param name="text">The expected first-line text.</param>
        /// <param name="label">The assertion label.</param>
        /// <returns>True when the expectation held.</returns>
        private static bool ExpectChunkLine(CommandEngine engine, string line, string text, string label)
        {
            CommandResult result = engine.Execute(line);
            bool ok = Expect(result.Pending == null && result.Lines.Count > 0 && result.Lines[0].Severity == ConsoleLineSeverity.Error &&
                             result.Lines[0].Text == text,
                $"{label} — expected Error '{text}', got " +
                (result.Lines.Count > 0 ? $"{result.Lines[0].Severity} '{result.Lines[0].Text}'" : "no lines") +
                (result.Pending != null ? " with a pending confirmation" : ""));
            if (engine.HasPendingConfirmation)
                engine.Execute("no");
            return ok;
        }

        /// <summary>
        /// Like <see cref="ExpectChunkLine"/> but the error text only has to contain a fragment — for refusals that
        /// embed a computed number.
        /// </summary>
        /// <param name="engine">The engine to drive.</param>
        /// <param name="line">The line to submit.</param>
        /// <param name="fragment">The required error-text fragment.</param>
        /// <param name="label">The assertion label.</param>
        /// <returns>True when the expectation held.</returns>
        private static bool ExpectChunkError(CommandEngine engine, string line, string fragment, string label)
        {
            CommandResult result = engine.Execute(line);
            bool ok = Expect(result.Pending == null && result.Lines.Count > 0 && result.Lines[0].Severity == ConsoleLineSeverity.Error &&
                             result.Lines[0].Text.Contains(fragment),
                $"{label} — expected Error containing '{fragment}', got " +
                (result.Lines.Count > 0 ? $"{result.Lines[0].Severity} '{result.Lines[0].Text}'" : "no lines") +
                (result.Pending != null ? " with a pending confirmation" : ""));
            if (engine.HasPendingConfirmation)
                engine.Execute("no");
            return ok;
        }
    }
}
