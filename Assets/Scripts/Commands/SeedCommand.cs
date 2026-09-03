using System;

namespace Commands
{
    /// <summary>
    /// <c>/seed</c> — prints the loaded world's generation seed (CMD-3 §8.1). Reads
    /// <c>WorldData.seed</c> (the per-session authoritative copy) through the context facade.
    /// </summary>
    public sealed class SeedCommand : IConsoleCommand
    {
        private static readonly string[] s_noAliases = Array.Empty<string>();

        /// <inheritdoc/>
        public string Name => "seed";

        /// <inheritdoc/>
        public string[] Aliases => s_noAliases;

        /// <inheritdoc/>
        public string Usage => "/seed";

        /// <inheritdoc/>
        public CommandResult Execute(CommandContext ctx, CommandArgs args)
        {
            if (args.Count != 0)
                return CommandResult.Error($"'/seed' takes no arguments. Usage: {Usage}");

            if (ctx.World == null || ctx.World.worldData == null)
                return CommandResult.Error("No world is loaded.");

            return CommandResult.Info($"Seed: {ctx.World.worldData.seed}");
        }
    }
}
