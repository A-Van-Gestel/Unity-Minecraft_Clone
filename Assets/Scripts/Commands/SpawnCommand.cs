using System;
using Helpers;

namespace Commands
{
    /// <summary>
    /// <c>/spawn</c> — teleports the player to the world's canonical spawn point (CMD-3 §8.1),
    /// reusing CMD-2's <see cref="World.TeleportPlayer"/> execution and §3.3 arrival hold. The
    /// destination is computed in integer chunk math from the chunk-relative spawn point, so it is
    /// exact at any distance.
    /// </summary>
    public sealed class SpawnCommand : IConsoleCommand
    {
        private static readonly string[] s_noAliases = Array.Empty<string>();

        /// <inheritdoc/>
        public string Name => "spawn";

        /// <inheritdoc/>
        public string[] Aliases => s_noAliases;

        /// <inheritdoc/>
        public string Usage => "/spawn";

        /// <inheritdoc/>
        public CommandResult Execute(CommandContext ctx, CommandArgs args)
        {
            if (args.Count != 0)
                return CommandResult.Error($"'/spawn' takes no arguments. Usage: {Usage}");

            if (ctx.World == null)
                return CommandResult.Error("No world is loaded.");

            var spawn = ctx.World.WorldSpawnPoint;
            int voxelX = spawn.Chunk.X * ChunkMath.CHUNK_WIDTH + (int)Math.Floor(spawn.localPosition.x);
            int voxelY = (int)Math.Floor(spawn.localPosition.y);
            int voxelZ = spawn.Chunk.Z * ChunkMath.CHUNK_WIDTH + (int)Math.Floor(spawn.localPosition.z);

            ctx.World.TeleportPlayer(voxelX, voxelY, voxelZ, resolveSurfaceY: false);
            return CommandResult.Info($"Teleporting to spawn ({voxelX}, {voxelY}, {voxelZ})…");
        }
    }
}
