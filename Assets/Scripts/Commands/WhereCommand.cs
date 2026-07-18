using System;
using Data;
using Helpers;

namespace Commands
{
    /// <summary>
    /// <c>/where</c> — prints the player's absolute voxel position, chunk, region-file address, and
    /// the current floating-origin anchor (CMD-3 §8.1). The WS-4 debugging companion to
    /// <c>/teleport</c>: every value is voxel-space, so far-coordinate sessions can be located and
    /// resumed exactly.
    /// </summary>
    public sealed class WhereCommand : IConsoleCommand
    {
        private static readonly string[] s_noAliases = Array.Empty<string>();

        /// <inheritdoc/>
        public string Name => "where";

        /// <inheritdoc/>
        public string[] Aliases => s_noAliases;

        /// <inheritdoc/>
        public string Usage => "/where";

        /// <inheritdoc/>
        public CommandResult Execute(CommandContext ctx, CommandArgs args)
        {
            if (args.Count != 0)
                return CommandResult.Error($"'/where' takes no arguments. Usage: {Usage}");

            if (ctx.World == null || ctx.Player == null)
                return CommandResult.Error("No world is loaded.");

            var unityPos = ctx.Player.transform.position;
            var voxelCell = WorldOrigin.UnityToVoxelCell(unityPos);
            ChunkCoord chunkCoord = WorldOrigin.UnityToChunk(unityPos);
            var (regionCoord, regionLocalX, regionLocalZ) = RegionAddressCodec
                .ForVersion(SaveSystem.CURRENT_VERSION)
                .ChunkVoxelPosToRegionAddress(chunkCoord.ToVoxelOrigin());

            ChunkCoord originChunk = WorldOrigin.OriginChunk;
            string originNote = WorldOrigin.IsIdentity ? " (identity)" : "";

            return new CommandResult(new[]
            {
                new ConsoleLine(ConsoleLineSeverity.Info, $"Voxel: ({voxelCell.x}, {voxelCell.y}, {voxelCell.z})"),
                new ConsoleLine(ConsoleLineSeverity.Info, $"Chunk: ({chunkCoord.X}, {chunkCoord.Z}) — region r.{regionCoord.x}.{regionCoord.y}.bin slot ({regionLocalX}, {regionLocalZ})"),
                new ConsoleLine(ConsoleLineSeverity.Info, $"Origin chunk: ({originChunk.X}, {originChunk.Z}){originNote}"),
            });
        }
    }
}
