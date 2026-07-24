using System;
using Data;
using Helpers;

namespace Commands
{
    /// <summary>
    /// <c>/chunk info</c> — dumps the pipeline state of the player's current chunk (CMD-3 §8.1):
    /// the lifecycle/lighting flags plus the visual's mesh state. Pairs with chunk-lifecycle
    /// debugging; queryable anywhere, including over not-yet-loaded chunks.
    /// </summary>
    public sealed class ChunkInfoCommand : IConsoleCommand
    {
        private static readonly string[] s_noAliases = Array.Empty<string>();

        /// <inheritdoc/>
        public string Name => "chunk";

        /// <inheritdoc/>
        public string[] Aliases => s_noAliases;

        /// <inheritdoc/>
        public string Usage => "/chunk info";

        /// <inheritdoc/>
        public CommandResult Execute(CommandContext ctx, CommandArgs args)
        {
            if (args.Count != 1 || args[0].Type != CommandTokenType.Word ||
                !string.Equals(args[0].Text, "info", StringComparison.OrdinalIgnoreCase))
                return CommandResult.Error($"Usage: {Usage}");

            if (ctx.World == null || ctx.Player == null)
                return CommandResult.Error("No world is loaded.");

            ChunkCoord chunkCoord = WorldOrigin.UnityToChunk(ctx.Player.transform.position);
            if (!ctx.World.worldData.TryGetChunk(chunkCoord.ToVoxelOrigin(), out var data) || data == null)
                return CommandResult.Info($"Chunk ({chunkCoord.X}, {chunkCoord.Z}): not loaded.");

            string visual = data.Chunk == null
                ? "none"
                : data.Chunk.HasMeshApplied
                    ? "mesh applied"
                    : "no mesh yet";

            return new CommandResult(new[]
            {
                new ConsoleLine(ConsoleLineSeverity.Info, $"Chunk ({chunkCoord.X}, {chunkCoord.Z}):"),
                new ConsoleLine(ConsoleLineSeverity.Info,
                    $"  IsPopulated={data.IsPopulated}  IsLoading={data.IsLoading}  Visual={visual}"),
                new ConsoleLine(ConsoleLineSeverity.Info,
                    $"  NeedsInitialLighting={data.NeedsInitialLighting}  HasLightChangesToProcess={data.HasLightChangesToProcess}"),
                new ConsoleLine(ConsoleLineSeverity.Info,
                    $"  NeedsEdgeCheck={data.NeedsEdgeCheck}  IsAwaitingMainThreadProcess={data.IsAwaitingMainThreadProcess}"),
            });
        }
    }
}
