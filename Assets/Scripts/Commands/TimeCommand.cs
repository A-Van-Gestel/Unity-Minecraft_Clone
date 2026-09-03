using System;
using System.Globalization;

namespace Commands
{
    /// <summary>
    /// <c>/time</c> — reads and drives the world's day/night clock (RF-1). Bare <c>/time</c> queries;
    /// <c>set</c> jumps to a named time or a raw tick within the day, <c>add</c> advances the clock,
    /// and <c>freeze</c>/<c>resume</c> hold or release it. The freeze state persists with the world.
    /// </summary>
    /// <remarks>
    /// Ticks follow Minecraft's anchoring — 24000 ticks per day with tick 0 at sunrise — so the
    /// familiar constants (<c>day</c> 1000, <c>noon</c> 6000, <c>night</c> 13000, <c>midnight</c>
    /// 18000) mean here what they mean there. This replaces the pre-RF-1 <c>/time set &lt;0..1&gt;</c>
    /// form, which set a light level directly because there was no clock to set.
    /// </remarks>
    public sealed class TimeCommand : IConsoleCommand, IArgumentCompleter
    {
        private static readonly string[] s_noAliases = Array.Empty<string>();

        /// <summary>The subcommand set, in the order help-adjacent surfaces should show them.</summary>
        private static readonly string[] s_subcommands = { "set", "add", "freeze", "resume" };

        /// <summary>Named times accepted by <c>set</c>, and offered as completions.</summary>
        private static readonly string[] s_namedTimes = { "sunrise", "day", "noon", "sunset", "night", "midnight" };

        /// <summary>Day-tick value of each entry in <see cref="s_namedTimes"/>, same order (Minecraft parity).</summary>
        private static readonly int[] s_namedTimeTicks = { 23000, 1000, 6000, 12000, 13000, 18000 };

        /// <summary>
        /// Largest tick magnitude accepted from the console: 2²⁴, the last point at which consecutive
        /// <see cref="float"/> values are still one apart. Past it "a whole number of ticks" stops
        /// meaning anything — the nearest representable neighbors are two or more ticks away — and the
        /// float→long cast eventually goes out of range. ~699 in-world days, far beyond any real argument.
        /// </summary>
        private const long MAX_EXACT_TICK_MAGNITUDE = 1L << 24;

        /// <summary>How far from an integer a tick argument may sit and still count as whole.</summary>
        private const float TICK_INTEGRALITY_TOLERANCE = 1e-4f;

        /// <summary>Minutes in an in-world hour, for the human-readable clock in the query line.</summary>
        private const int MINUTES_PER_HOUR = 60;

        private const int HOURS_PER_DAY = 24;

        /// <inheritdoc/>
        public string Name => "time";

        /// <inheritdoc/>
        public string[] Aliases => s_noAliases;

        /// <inheritdoc/>
        public string Usage => "/time [set <sunrise|day|noon|sunset|night|midnight|ticks> | add <ticks> | freeze | resume]";

        /// <inheritdoc/>
        public CommandResult Execute(CommandContext ctx, CommandArgs args)
        {
            if (ctx.World == null)
                return CommandResult.Error("No world is loaded.");

            WorldTimeManager clock = ctx.World.TimeManager;
            if (clock == null)
                return CommandResult.Error("The world clock is not running yet.");

            if (args.Count == 0)
                return Report(clock);

            if (args[0].Type != CommandTokenType.Word)
                return CommandResult.Error($"Usage: {Usage}");

            string subcommand = args[0].Text;

            if (string.Equals(subcommand, "freeze", StringComparison.OrdinalIgnoreCase))
                return SetFrozen(clock, args, true);

            if (string.Equals(subcommand, "resume", StringComparison.OrdinalIgnoreCase))
                return SetFrozen(clock, args, false);

            if (string.Equals(subcommand, "set", StringComparison.OrdinalIgnoreCase))
                return ExecuteSet(clock, args);

            if (string.Equals(subcommand, "add", StringComparison.OrdinalIgnoreCase))
                return ExecuteAdd(clock, args);

            return CommandResult.Error($"Usage: {Usage}");
        }

        /// <summary>Handles <c>/time freeze</c> and <c>/time resume</c>.</summary>
        /// <param name="clock">The world clock.</param>
        /// <param name="args">The full argument list, including the subcommand word.</param>
        /// <param name="frozen">The freeze state to apply.</param>
        /// <returns>The result line.</returns>
        private CommandResult SetFrozen(WorldTimeManager clock, CommandArgs args, bool frozen)
        {
            if (args.Count != 1)
                return CommandResult.Error($"Usage: {Usage}");

            clock.IsFrozen = frozen;
            return CommandResult.Info(frozen
                ? $"Time frozen at {ClockText(clock)}."
                : $"Time resumed at {ClockText(clock)}.");
        }

        /// <summary>Handles <c>/time set &lt;named|ticks&gt;</c>.</summary>
        /// <param name="clock">The world clock.</param>
        /// <param name="args">The full argument list, including the <c>set</c> word.</param>
        /// <returns>The result line.</returns>
        private CommandResult ExecuteSet(WorldTimeManager clock, CommandArgs args)
        {
            if (args.Count != 2)
                return CommandResult.Error($"Usage: {Usage}");

            if (!TryParseDayTime(args[1], out int dayTicks))
                return CommandResult.Error(
                    $"Unknown time '{args[1].Text}' — use a name (sunrise, day, noon, sunset, night, midnight) " +
                    $"or a whole tick in [0, {WorldTimeManager.TicksPerDay - 1}].");

            clock.SetDayTime(dayTicks);
            return CommandResult.Info($"Time set to {ClockText(clock)}.");
        }

        /// <summary>Handles <c>/time add &lt;ticks&gt;</c>.</summary>
        /// <param name="clock">The world clock.</param>
        /// <param name="args">The full argument list, including the <c>add</c> word.</param>
        /// <returns>The result line.</returns>
        private CommandResult ExecuteAdd(WorldTimeManager clock, CommandArgs args)
        {
            if (args.Count != 2 || args[1].Type != CommandTokenType.Number)
                return CommandResult.Error($"Usage: {Usage}");

            if (!TryWholeTicks(args[1].Number, out long deltaTicks))
                return CommandResult.Error(
                    $"Ticks must be a whole number no larger than {Ticks(MAX_EXACT_TICK_MAGNITUDE)}.");

            clock.AddTicks(deltaTicks);
            return CommandResult.Info($"Time advanced by {Ticks(deltaTicks)} ticks to {ClockText(clock)}.");
        }

        /// <summary>Builds the bare-<c>/time</c> query line.</summary>
        /// <param name="clock">The world clock.</param>
        /// <returns>The report line.</returns>
        private static CommandResult Report(WorldTimeManager clock)
        {
            string frozen = clock.IsFrozen ? " (frozen)" : string.Empty;
            return CommandResult.Info($"Time: {ClockText(clock)}{frozen}.");
        }

        /// <summary>
        /// Renders the clock as the in-world time, day tick, day number, and current sky darken —
        /// enough to correlate a lighting observation with the clock that produced it.
        /// </summary>
        /// <param name="clock">The world clock.</param>
        /// <returns>The human-readable state.</returns>
        private static string ClockText(WorldTimeManager clock)
        {
            float hoursOfDay = clock.DayFraction * HOURS_PER_DAY;
            int hours = (int)hoursOfDay;
            int minutes = (int)((hoursOfDay - hours) * MINUTES_PER_HOUR);

            return $"{hours:00}:{minutes:00} (tick {Ticks(clock.DayTicks)}, day {Ticks(clock.ElapsedDays)}, " +
                   $"sky darken {Ticks(clock.SkyDarken)})";
        }

        /// <summary>
        /// Parses a <c>set</c> argument as either a named time or a whole day tick.
        /// </summary>
        /// <param name="token">The time token.</param>
        /// <param name="dayTicks">The parsed day tick.</param>
        /// <returns>True when the token is a recognized time.</returns>
        private static bool TryParseDayTime(CommandToken token, out int dayTicks)
        {
            if (token.Type == CommandTokenType.Number)
            {
                if (TryWholeTicks(token.Number, out long whole) &&
                    whole >= 0 && whole < WorldTimeManager.TicksPerDay)
                {
                    dayTicks = (int)whole;
                    return true;
                }

                dayTicks = 0;
                return false;
            }

            if (token.Type == CommandTokenType.Word)
            {
                for (int i = 0; i < s_namedTimes.Length; i++)
                {
                    if (string.Equals(token.Text, s_namedTimes[i], StringComparison.OrdinalIgnoreCase))
                    {
                        dayTicks = s_namedTimeTicks[i];
                        return true;
                    }
                }
            }

            dayTicks = 0;
            return false;
        }

        /// <summary>
        /// Accepts a tick argument only when it is a whole number within the range a
        /// <see cref="float"/> can express exactly — a fractional tick would silently truncate, and
        /// the console is the engine's deterministic-repro surface.
        /// </summary>
        /// <param name="value">The parsed numeric argument.</param>
        /// <param name="ticks">The whole tick value; zero when the argument is rejected.</param>
        /// <returns>True when the value is a usable whole tick count.</returns>
        private static bool TryWholeTicks(float value, out long ticks)
        {
            ticks = 0L;

            // Written as a negated range test rather than `> MAX`, so NaN — for which every
            // comparison is false — lands in the reject branch instead of slipping through.
            if (!(Math.Abs(value) <= MAX_EXACT_TICK_MAGNITUDE)) return false;

            // Safe now that the magnitude is bounded: an out-of-range float→long cast is undefined
            // in an unchecked context, and would hand the clock garbage rather than an error.
            long truncated = (long)value;

            // A tolerance, not an equality: the argument arrives as a float, so a typed "6000.0"
            // must read as whole while "6000.5" must not. Below 2^24 every tick is exactly
            // representable, so the gap this admits is far smaller than one tick.
            if (Math.Abs(value - truncated) > TICK_INTEGRALITY_TOLERANCE) return false;

            ticks = truncated;
            return true;
        }

        /// <summary>Formats an integer for output, independent of the host's locale.</summary>
        /// <param name="value">The value to format.</param>
        /// <returns>The invariant decimal text.</returns>
        private static string Ticks(long value) => value.ToString(CultureInfo.InvariantCulture);

        /// <inheritdoc/>
        public string[] CompleteArgument(int argIndex, string partial, CommandContext ctx)
        {
            if (argIndex == 0)
                return Matching(s_subcommands, partial);

            // Arg 1 is 'set's named time. 'add' takes a free tick count and gets the same candidates
            // offered — the completer cannot see the subcommand, the same limitation /wind accepts.
            return argIndex == 1 ? Matching(s_namedTimes, partial) : Array.Empty<string>();
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
