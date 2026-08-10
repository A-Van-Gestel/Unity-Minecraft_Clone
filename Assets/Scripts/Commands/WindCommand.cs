using System;

namespace Commands
{
    /// <summary>
    /// <c>/wind</c> — reads and sets the shared wind vector that drives cloud drift and FL-1 foliage
    /// sway. Bare <c>/wind</c> queries; <c>set</c> takes a speed plus a compass direction (the
    /// user-facing form), <c>vector</c> takes the raw XZ components (the developer form), and
    /// <c>off</c> becalms the world. Persisted to level.dat on the next save; a future weather
    /// system (RF-7) takes ownership of the value.
    /// </summary>
    /// <remarks>
    /// Directions are compass degrees measured <b>toward</b> the direction the wind blows —
    /// 0° = north = +Z, 90° = east = +X, increasing clockwise — so the angle and the stored
    /// velocity vector always point the same way (no meteorological "wind from the north" flip).
    /// </remarks>
    public sealed class WindCommand : IConsoleCommand, IArgumentCompleter
    {
        private static readonly string[] s_noAliases = Array.Empty<string>();

        /// <summary>The subcommand set, in the order <c>/help</c>-adjacent surfaces should show them.</summary>
        private static readonly string[] s_subcommands = { "set", "vector", "off" };

        /// <summary>
        /// Canonical compass names, indexed by 45° steps clockwise from north. Doubles as the
        /// completion candidate list and the query's direction naming table.
        /// </summary>
        private static readonly string[] s_compassNames =
        {
            "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest",
        };

        /// <summary>Short compass aliases accepted on input only (never offered as completions, where they would be ambiguous).</summary>
        private static readonly string[] s_compassAbbreviations = { "n", "ne", "e", "se", "s", "sw", "w", "nw" };

        /// <summary>Degrees per compass point (eight points around the circle).</summary>
        private const double DEGREES_PER_COMPASS_POINT = 45.0;

        private const double DEGREES_PER_TURN = 360.0;

        /// <inheritdoc/>
        public string Name => "wind";

        /// <inheritdoc/>
        public string[] Aliases => s_noAliases;

        /// <inheritdoc/>
        public string Usage => "/wind [set <speed> <direction> | vector <x> <z> | off]";

        /// <inheritdoc/>
        public CommandResult Execute(CommandContext ctx, CommandArgs args)
        {
            if (ctx.World == null)
                return CommandResult.Error("No world is loaded.");

            if (args.Count == 0)
                return Report(ctx.World);

            if (args[0].Type != CommandTokenType.Word)
                return CommandResult.Error($"Usage: {Usage}");

            string subcommand = args[0].Text;

            if (string.Equals(subcommand, "off", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Count != 1)
                    return CommandResult.Error($"Usage: {Usage}");

                ctx.World.SetWind(0f, 0f);
                return CommandResult.Info("Wind stopped — the world is becalmed.");
            }

            if (string.Equals(subcommand, "set", StringComparison.OrdinalIgnoreCase))
                return ExecuteSet(ctx, args);

            if (string.Equals(subcommand, "vector", StringComparison.OrdinalIgnoreCase))
                return ExecuteVector(ctx, args);

            return CommandResult.Error($"Usage: {Usage}");
        }

        /// <summary>Handles <c>/wind set &lt;speed&gt; &lt;direction&gt;</c>.</summary>
        /// <param name="ctx">The command context (world already null-checked).</param>
        /// <param name="args">The full argument list, including the <c>set</c> word.</param>
        /// <returns>The result line.</returns>
        private CommandResult ExecuteSet(CommandContext ctx, CommandArgs args)
        {
            if (args.Count != 3 || args[1].Type != CommandTokenType.Number)
                return CommandResult.Error($"Usage: {Usage}");

            // Deliberately uncapped above zero — float range is the only limit (2026-08-10).
            float speed = args[1].Number;
            if (speed < 0f)
                return CommandResult.Error("Speed must be zero or positive (use a direction to reverse the wind).");

            if (!TryParseDirection(args[2], out double degrees))
                return CommandResult.Error(
                    $"Unknown direction '{args[2].Text}' — use a compass name (north, ne, east, …) or degrees (0 = north, 90 = east).");

            // Compass 'toward': 0° = +Z, 90° = +X, clockwise — hence sin on X and cos on Z.
            double radians = degrees * Math.PI / 180.0;
            float x = (float)(speed * Math.Sin(radians));
            float z = (float)(speed * Math.Cos(radians));
            ctx.World.SetWind(x, z);

            return CommandResult.Info(
                $"Wind set to {CommandArgUtility.Invariant(speed)} blocks/s blowing toward " +
                $"{CompassName(degrees)} ({CommandArgUtility.Invariant((float)degrees)}°) — " +
                $"vector ({CommandArgUtility.Invariant(x)}, {CommandArgUtility.Invariant(z)}).");
        }

        /// <summary>Handles <c>/wind vector &lt;x&gt; &lt;z&gt;</c> — the raw stored form.</summary>
        /// <param name="ctx">The command context (world already null-checked).</param>
        /// <param name="args">The full argument list, including the <c>vector</c> word.</param>
        /// <returns>The result line.</returns>
        private CommandResult ExecuteVector(CommandContext ctx, CommandArgs args)
        {
            if (args.Count != 3 || args[1].Type != CommandTokenType.Number || args[2].Type != CommandTokenType.Number)
                return CommandResult.Error($"Usage: {Usage}");

            float x = args[1].Number;
            float z = args[2].Number;
            float speed = (float)Math.Sqrt(x * x + z * z);

            ctx.World.SetWind(x, z);
            return CommandResult.Info(
                $"Wind vector set to ({CommandArgUtility.Invariant(x)}, {CommandArgUtility.Invariant(z)}) blocks/s " +
                $"— {CommandArgUtility.Invariant(speed)} blocks/s.");
        }

        /// <summary>Builds the bare-<c>/wind</c> query line.</summary>
        /// <param name="world">The world holding the wind (non-null).</param>
        /// <returns>The report line.</returns>
        private static CommandResult Report(World world)
        {
            float x = world.WindX;
            float z = world.WindZ;
            float speed = (float)Math.Sqrt(x * x + z * z);

            if (speed <= 0f)
                return CommandResult.Info("Wind: calm (0 blocks/s) — clouds and foliage are still.");

            double degrees = Normalize(Math.Atan2(x, z) * 180.0 / Math.PI);
            return CommandResult.Info(
                $"Wind: {CommandArgUtility.Invariant(speed)} blocks/s blowing toward {CompassName(degrees)} " +
                $"({CommandArgUtility.Invariant((float)degrees)}°) — vector ({CommandArgUtility.Invariant(x)}, {CommandArgUtility.Invariant(z)}).");
        }

        /// <summary>
        /// Parses a direction token as either a compass name/abbreviation or a degree number,
        /// normalized into <c>[0, 360)</c>.
        /// </summary>
        /// <param name="token">The direction token.</param>
        /// <param name="degrees">The parsed compass bearing in degrees.</param>
        /// <returns>True when the token is a recognized direction.</returns>
        private static bool TryParseDirection(CommandToken token, out double degrees)
        {
            if (token.Type == CommandTokenType.Number)
            {
                degrees = Normalize(token.Number);
                return true;
            }

            if (token.Type == CommandTokenType.Word)
            {
                for (int i = 0; i < s_compassNames.Length; i++)
                {
                    if (string.Equals(token.Text, s_compassNames[i], StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(token.Text, s_compassAbbreviations[i], StringComparison.OrdinalIgnoreCase))
                    {
                        degrees = i * DEGREES_PER_COMPASS_POINT;
                        return true;
                    }
                }
            }

            degrees = 0.0;
            return false;
        }

        /// <summary>Names the nearest of the eight compass points to a bearing.</summary>
        /// <param name="degrees">The bearing in degrees (any value; normalized internally).</param>
        /// <returns>The canonical compass name.</returns>
        private static string CompassName(double degrees)
        {
            int point = (int)Math.Round(Normalize(degrees) / DEGREES_PER_COMPASS_POINT) % s_compassNames.Length;
            return s_compassNames[point];
        }

        /// <summary>Wraps a bearing into <c>[0, 360)</c>, keeping negatives meaningful.</summary>
        /// <param name="degrees">The raw bearing.</param>
        /// <returns>The equivalent bearing in <c>[0, 360)</c>.</returns>
        private static double Normalize(double degrees)
        {
            double wrapped = degrees % DEGREES_PER_TURN;
            return wrapped < 0.0 ? wrapped + DEGREES_PER_TURN : wrapped;
        }

        /// <inheritdoc/>
        public string[] CompleteArgument(int argIndex, string partial, CommandContext ctx)
        {
            // Arg 0 is the subcommand; arg 2 of 'set' is the direction. Speeds and raw vector
            // components are free numbers with no candidates.
            if (argIndex == 0)
                return Matching(s_subcommands, partial);

            return argIndex == 2 ? Matching(s_compassNames, partial) : Array.Empty<string>();
        }

        /// <summary>Filters a candidate list by a case-insensitive prefix.</summary>
        /// <param name="candidates">The full candidate list.</param>
        /// <param name="partial">The prefix typed so far.</param>
        /// <returns>The matching candidates, in canonical casing.</returns>
        private static string[] Matching(string[] candidates, string partial)
        {
            if (string.IsNullOrEmpty(partial))
                return candidates;

            int count = 0;
            foreach (string candidate in candidates)
            {
                if (candidate.StartsWith(partial, StringComparison.OrdinalIgnoreCase)) count++;
            }

            if (count == 0) return Array.Empty<string>();

            string[] matches = new string[count];
            int next = 0;
            foreach (string candidate in candidates)
            {
                if (candidate.StartsWith(partial, StringComparison.OrdinalIgnoreCase)) matches[next++] = candidate;
            }

            return matches;
        }
    }
}
