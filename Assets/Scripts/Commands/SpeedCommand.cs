using System;

namespace Commands
{
    /// <summary>
    /// <c>/speed &lt;n&gt;</c> — sets the flying speed multiplier to an exact value (CMD-3 §8.1;
    /// the scroll keybind only increments, this sets). Must be positive.
    /// </summary>
    public sealed class SpeedCommand : IConsoleCommand
    {
        private static readonly string[] s_noAliases = Array.Empty<string>();

        /// <inheritdoc/>
        public string Name => "speed";

        /// <inheritdoc/>
        public string[] Aliases => s_noAliases;

        /// <inheritdoc/>
        public string Usage => "/speed <n>";

        /// <inheritdoc/>
        public CommandResult Execute(CommandContext ctx, CommandArgs args)
        {
            if (args.Count != 1 || args[0].Type != CommandTokenType.Number)
                return CommandResult.Error($"Usage: {Usage}");

            float speed = args[0].Number;
            if (speed <= 0f)
                return CommandResult.Error("Speed must be positive.");

            if (ctx.Player == null || ctx.Player.VoxelRigidbody == null)
                return CommandResult.Error("No world is loaded.");

            ctx.Player.VoxelRigidbody.flyingSpeed = speed;
            return CommandResult.Info($"Flying speed set to {CommandArgUtility.Invariant(speed)}.");
        }
    }
}
