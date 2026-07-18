using System;
using Helpers;

namespace Commands
{
    /// <summary>
    /// <c>/setspawn</c> — sets the world's canonical spawn point to the player's current position
    /// (CMD-3 §8.1). Writes the existing <c>World.WorldSpawnPoint</c> (chunk-relative, so exact at
    /// any distance); persisted to level.dat on the next save.
    /// </summary>
    public sealed class SetSpawnCommand : IConsoleCommand
    {
        private static readonly string[] s_noAliases = Array.Empty<string>();

        /// <inheritdoc/>
        public string Name => "setspawn";

        /// <inheritdoc/>
        public string[] Aliases => s_noAliases;

        /// <inheritdoc/>
        public string Usage => "/setspawn";

        /// <inheritdoc/>
        public CommandResult Execute(CommandContext ctx, CommandArgs args)
        {
            if (args.Count != 0)
                return CommandResult.Error($"'/setspawn' takes no arguments. Usage: {Usage}");

            if (ctx.World == null || ctx.Player == null)
                return CommandResult.Error("No world is loaded.");

            var spawn = WorldOrigin.UnityToRelative(ctx.Player.transform.position);
            ctx.World.SetSpawnPoint(spawn);
            return CommandResult.Info($"Spawn point set to your position: {spawn} (persisted on the next save).");
        }
    }
}
