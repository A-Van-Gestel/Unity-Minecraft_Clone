using System;
using Helpers;

namespace Commands
{
    /// <summary>
    /// <c>/set-world-border &lt;radius&gt;|off</c> — sets the TF-14 per-world gameplay border
    /// (CMD-3 §8.1). A shrink that would strand the player outside warns + confirms (the fence
    /// re-clamps them inward every FixedUpdate). Persisted to level.dat on the next save.
    /// </summary>
    public sealed class WorldBorderCommand : IConsoleCommand, IArgumentCompleter
    {
        private static readonly string[] s_noAliases = Array.Empty<string>();
        private static readonly string[] s_offCandidate = { "off" };

        /// <inheritdoc/>
        public string Name => "set-world-border";

        /// <inheritdoc/>
        public string[] Aliases => s_noAliases;

        /// <inheritdoc/>
        public string Usage => "/set-world-border <radius>|off";

        /// <inheritdoc/>
        public CommandResult Execute(CommandContext ctx, CommandArgs args)
        {
            if (args.Count != 1)
                return CommandResult.Error($"Usage: {Usage}");

            int radius;
            if (args[0].Type == CommandTokenType.Word && string.Equals(args[0].Text, "off", StringComparison.OrdinalIgnoreCase))
            {
                radius = 0;
            }
            else if (args[0].Type == CommandTokenType.Number && args[0].IsInteger && args[0].Integer > 0)
            {
                radius = args[0].Integer;
            }
            else
            {
                return CommandResult.Error($"Radius must be a positive integer (or 'off' to disable). Usage: {Usage}");
            }

            if (ctx.World == null)
                return CommandResult.Error("No world is loaded.");

            World world = ctx.World;

            // Shrink-strand check: [-radius, radius) cell semantics, mirroring World.IsVoxelInsideBorder.
            if (radius > 0 && ctx.Player != null)
            {
                var playerCell = WorldOrigin.UnityToVoxelCell(ctx.Player.transform.position);
                bool inside = playerCell.x >= -radius && playerCell.x < radius &&
                              playerCell.z >= -radius && playerCell.z < radius;
                if (!inside)
                    return CommandResult.Confirm(
                        $"You are outside the new ±{radius} border — the fence will clamp you back inside. Set it anyway?",
                        () => ApplyBorder(world, radius));
            }

            return ApplyBorder(world, radius);
        }

        /// <summary>Applies the new border radius and reports it.</summary>
        /// <param name="world">The world to set the border on (non-null).</param>
        /// <param name="radius">The new half-extent in voxels; 0 disables.</param>
        /// <returns>The confirmation line.</returns>
        private static CommandResult ApplyBorder(World world, int radius)
        {
            world.SetBorderRadius(radius);
            return CommandResult.Info(radius > 0
                ? $"World border set to ±{radius} voxels (persisted on the next save)."
                : "World border disabled (persisted on the next save).");
        }

        /// <inheritdoc/>
        public string[] CompleteArgument(int argIndex, string partial, CommandContext ctx)
        {
            // Only the literal 'off' is completable; a radius is a free number with no candidates.
            if (argIndex == 0 && "off".StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                return s_offCandidate;
            return Array.Empty<string>();
        }
    }
}
