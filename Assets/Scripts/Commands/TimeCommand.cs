using System;

namespace Commands
{
    /// <summary>
    /// <c>/time set &lt;0..1&gt;</c> — sets the global light level (CMD-3 §8.1: 0 = midnight,
    /// 1 = noon). Until RF-1 ships a real day/night clock this IS the world's time-of-day state
    /// (persisted as <c>timeOfDay</c>); <c>add</c> is deliberately absent — time arithmetic has no
    /// meaning without a clock (decided 2026-07-18). Enables deterministic lighting repros.
    /// </summary>
    public sealed class TimeCommand : IConsoleCommand
    {
        private static readonly string[] s_noAliases = Array.Empty<string>();

        /// <inheritdoc/>
        public string Name => "time";

        /// <inheritdoc/>
        public string[] Aliases => s_noAliases;

        /// <inheritdoc/>
        public string Usage => "/time set <0..1>";

        /// <inheritdoc/>
        public CommandResult Execute(CommandContext ctx, CommandArgs args)
        {
            if (args.Count != 2 || args[0].Type != CommandTokenType.Word ||
                !string.Equals(args[0].Text, "set", StringComparison.OrdinalIgnoreCase))
                return CommandResult.Error($"Usage: {Usage}");

            if (args[1].Type != CommandTokenType.Number || args[1].Number < 0f || args[1].Number > 1f)
                return CommandResult.Error($"Time must be a number in [0, 1] (0 = midnight, 1 = noon). Usage: {Usage}");

            if (ctx.World == null)
                return CommandResult.Error("No world is loaded.");

            ctx.World.globalLightLevel = args[1].Number;
            ctx.World.SetGlobalLightValue();
            return CommandResult.Info($"Time of day set to {CommandArgUtility.Invariant(args[1].Number)} (0 = midnight, 1 = noon).");
        }
    }
}
