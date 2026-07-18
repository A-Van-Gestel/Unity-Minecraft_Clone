using System;

namespace Commands
{
    /// <summary>
    /// <c>/setblock X Y Z &lt;block&gt;</c> — places a block at an absolute voxel cell through the
    /// existing modification path (CMD-3 §8.1; useful for validation repros). Uses ForcePlace
    /// (decided 2026-07-18 — placement-tag rules would reject overwriting stone; UNBREAKABLE still
    /// refuses). Unloaded targets route to the persistent pending-mods queue and apply on load.
    /// </summary>
    public sealed class SetBlockCommand : IConsoleCommand
    {
        private static readonly string[] s_noAliases = Array.Empty<string>();

        /// <inheritdoc/>
        public string Name => "setblock";

        /// <inheritdoc/>
        public string[] Aliases => s_noAliases;

        /// <inheritdoc/>
        public string Usage => "/setblock X Y Z <block>";

        /// <inheritdoc/>
        public CommandResult Execute(CommandContext ctx, CommandArgs args)
        {
            if (args.Count != 4 || args[3].Type != CommandTokenType.Word)
                return CommandResult.Error($"Usage: {Usage}");

            if (!CommandArgUtility.TryParseCoord(args[0], "X", out int x, out string coordError) ||
                !CommandArgUtility.TryParseCoord(args[1], "Y", out int y, out coordError) ||
                !CommandArgUtility.TryParseCoord(args[2], "Z", out int z, out coordError))
                return CommandResult.Error(coordError);

            if (y < 0 || y >= VoxelData.ChunkHeight)
                return CommandResult.Error($"Y must be in [0, {VoxelData.ChunkHeight}).");

            if (Math.Abs((long)x) > CommandArgUtility.AddressableLimitVoxels ||
                Math.Abs((long)z) > CommandArgUtility.AddressableLimitVoxels)
                return CommandResult.Error("Target is permanently outside the addressable world (±2^31 voxels).");

            if (ctx.World == null)
                return CommandResult.Error("No world is loaded.");

            if (!CommandArgUtility.TryResolveBlockId(ctx.World, args[3].Text, out ushort id, out string nameError))
                return CommandResult.Error(nameError);

            string blockName = ctx.World.BlockTypes[id].blockName;
            bool targetLoaded = ctx.World.PlaceBlockCommand(x, y, z, id);

            return CommandResult.Info(targetLoaded
                ? $"Placed {blockName} at ({x}, {y}, {z})."
                : $"{blockName} queued for ({x}, {y}, {z}) — chunk not loaded; applies when it loads.");
        }
    }
}
