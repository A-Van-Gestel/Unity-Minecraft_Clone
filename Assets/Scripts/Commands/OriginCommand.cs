using System;
using Data;
using Helpers;

namespace Commands
{
    /// <summary>
    /// <c>/origin [force]</c> — dev tool over the WS-4 floating origin (CMD-3 §8.1). Bare
    /// <c>/origin</c> shows the current anchor; <c>/origin force</c> re-anchors onto the player's
    /// chunk immediately, making the WS-4b in-game shift gate scriptable instead of
    /// "fly 1024 units".
    /// </summary>
    public sealed class OriginCommand : IConsoleCommand
    {
        private static readonly string[] s_noAliases = Array.Empty<string>();

        /// <inheritdoc/>
        public string Name => "origin";

        /// <inheritdoc/>
        public string[] Aliases => s_noAliases;

        /// <inheritdoc/>
        public string Usage => "/origin [force]";

        /// <inheritdoc/>
        public CommandResult Execute(CommandContext ctx, CommandArgs args)
        {
            if (args.Count == 0)
            {
                ChunkCoord originChunk = WorldOrigin.OriginChunk;
                var originVoxel = WorldOrigin.OriginVoxel;
                string identityNote = WorldOrigin.IsIdentity ? " (identity — Unity and voxel space coincide)" : "";
                return CommandResult.Info(
                    $"Origin chunk: ({originChunk.X}, {originChunk.Z}), voxel ({originVoxel.x}, {originVoxel.z}){identityNote}");
            }

            if (args.Count == 1 && args[0].Type == CommandTokenType.Word &&
                string.Equals(args[0].Text, "force", StringComparison.OrdinalIgnoreCase))
            {
                if (ctx.World == null)
                    return CommandResult.Error("No world is loaded.");

                ctx.World.ForceOriginReanchor();
                ChunkCoord newOrigin = WorldOrigin.OriginChunk;
                return CommandResult.Info($"Origin re-anchored onto the player's chunk ({newOrigin.X}, {newOrigin.Z}).");
            }

            return CommandResult.Error($"Unknown argument. Usage: {Usage}");
        }
    }
}
