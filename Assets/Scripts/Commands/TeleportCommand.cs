using System;
using System.Collections.Generic;

namespace Commands
{
    /// <summary>
    /// <c>/teleport [@target] X Y Z</c> and <c>/teleport [@target] X Z</c> (surface-resolved Y) —
    /// the CMD-2 / WS-4c teleport (§4.3). Coordinates are absolute voxel space, integer literals
    /// only in v1. Validation tiers: hard errors (arity, unknown selector, beyond the addressable
    /// world) reject outright; warnings (out-of-range Y, outside the TF-14 border fence, beyond the
    /// noise-degradation radius) collect into a single yes/no confirmation. Execution goes through
    /// <see cref="World.TeleportPlayer"/>, which owns the §3.3 arrival hold.
    /// </summary>
    public sealed class TeleportCommand : IConsoleCommand
    {
        private static readonly string[] s_aliases = { "tp" };

        /// <summary>Voxel radius beyond which float-driven noise degrades (2²⁴) — the far-warn tier (§4.3).</summary>
        private const long NOISE_DEGRADATION_RADIUS_VOXELS = 1L << 24;

        /// <summary>
        /// Largest coordinate magnitude treated as inside the addressable world: a small margin under
        /// <see cref="int.MaxValue"/> keeps neighbor-chunk voxel-origin math (<c>chunk×16</c> ± a chunk)
        /// from wrapping at the extreme edge (§4.3's ±2³¹⁻ᵋ error tier).
        /// </summary>
        private const long ADDRESSABLE_LIMIT_VOXELS = int.MaxValue - 32L;

        /// <inheritdoc/>
        public string Name => "teleport";

        /// <inheritdoc/>
        public string[] Aliases => s_aliases;

        /// <inheritdoc/>
        public string Usage => "/teleport [@player] X Y Z | /teleport [@player] X Z";

        /// <inheritdoc/>
        public CommandResult Execute(CommandContext ctx, CommandArgs args)
        {
            // --- Selector (optional; defaults to the local player) ---
            int coordStart = 0;
            if (args.Count > 0 && args[0].Type == CommandTokenType.Selector)
            {
                if (!ctx.Selectors.TryResolve(args[0], out CommandTarget _, out string selectorError))
                    return ErrorWithUsage(selectorError);
                coordStart = 1;
            }

            // --- Arity: 3 coords (X Y Z) or 2 (X Z, surface-resolved Y) ---
            int coordCount = args.Count - coordStart;
            if (coordCount != 2 && coordCount != 3)
                return ErrorWithUsage("Expected X Y Z or X Z coordinates.");

            bool resolveSurfaceY = coordCount == 2;

            if (!TryParseCoord(args[coordStart], "X", out int x, out string coordError))
                return ErrorWithUsage(coordError);

            int y = 0;
            if (!resolveSurfaceY && !TryParseCoord(args[coordStart + 1], "Y", out y, out coordError))
                return ErrorWithUsage(coordError);

            if (!TryParseCoord(args[coordStart + (resolveSurfaceY ? 1 : 2)], "Z", out int z, out coordError))
                return ErrorWithUsage(coordError);

            // --- Hard error tier: permanently outside the addressable world ---
            if (Math.Abs((long)x) > ADDRESSABLE_LIMIT_VOXELS || Math.Abs((long)z) > ADDRESSABLE_LIMIT_VOXELS)
                return CommandResult.Error("Destination is permanently outside the addressable world (±2^31 voxels).");

            if (ctx.World == null)
                return CommandResult.Error("No world is loaded.");

            // --- Warn tier: collect every applicable warning into ONE confirmation (§4.3) ---
            List<string> warnings = new List<string>();

            if (!resolveSurfaceY && (y < 0 || y >= VoxelData.ChunkHeight))
                warnings.Add($"Y {y} is outside the world's [0, {VoxelData.ChunkHeight}) range — you may fall into the void");

            if (!ctx.World.IsVoxelInsideBorder(x, z))
                warnings.Add("destination is outside the world border — you will be clamped back to the fence edge on arrival");

            if (Math.Abs((long)x) > NOISE_DEGRADATION_RADIUS_VOXELS || Math.Abs((long)z) > NOISE_DEGRADATION_RADIUS_VOXELS)
                warnings.Add($"destination is beyond ±{NOISE_DEGRADATION_RADIUS_VOXELS} voxels — terrain artifacts expected");

            World world = ctx.World;
            if (warnings.Count > 0)
            {
                string prompt = "Teleport anyway? " + string.Join("; ", warnings) + ".";
                return CommandResult.Confirm(prompt, () => ExecuteTeleport(world, x, y, z, resolveSurfaceY));
            }

            return ExecuteTeleport(world, x, y, z, resolveSurfaceY);
        }

        /// <summary>Runs the actual teleport and reports the destination.</summary>
        /// <param name="world">The world to teleport through (non-null).</param>
        /// <param name="x">Destination voxel X.</param>
        /// <param name="y">Destination voxel Y (ignored when <paramref name="resolveSurfaceY"/>).</param>
        /// <param name="z">Destination voxel Z.</param>
        /// <param name="resolveSurfaceY">Whether Y resolves from the surface on arrival-release.</param>
        /// <returns>The teleport-started confirmation line.</returns>
        private static CommandResult ExecuteTeleport(World world, int x, int y, int z, bool resolveSurfaceY)
        {
            world.TeleportPlayer(x, y, z, resolveSurfaceY);
            string yText = resolveSurfaceY ? "surface" : y.ToString();
            return CommandResult.Info($"Teleporting to ({x}, {yText}, {z})…");
        }

        /// <summary>
        /// Parses one coordinate token as an integer. Distinguishes a typed decimal (usage error)
        /// from an integer literal too large for <see cref="int"/> (which the tokenizer downgraded
        /// to a float) — the latter is the §4.3 addressable-world error.
        /// </summary>
        /// <param name="token">The coordinate token.</param>
        /// <param name="axis">The axis name for error text.</param>
        /// <param name="value">The parsed coordinate.</param>
        /// <param name="error">The error text on failure; null on success.</param>
        /// <returns>True when parsed.</returns>
        private static bool TryParseCoord(CommandToken token, string axis, out int value, out string error)
        {
            if (token.Type == CommandTokenType.Number && token.IsInteger)
            {
                value = token.Integer;
                error = null;
                return true;
            }

            value = 0;
            if (token.Type == CommandTokenType.Number)
            {
                bool looksDecimal = token.Text.IndexOf('.') >= 0 ||
                                    token.Text.IndexOf('e') >= 0 || token.Text.IndexOf('E') >= 0;
                error = looksDecimal
                    ? $"{axis} must be an integer voxel coordinate."
                    : $"{axis} is permanently outside the addressable world (±2^31 voxels).";
                return false;
            }

            error = $"{axis} is not a number.";
            return false;
        }

        /// <summary>Builds an error line followed by the usage string.</summary>
        /// <param name="text">The error text.</param>
        /// <returns>The two-line result.</returns>
        private CommandResult ErrorWithUsage(string text)
        {
            return new CommandResult(new[]
            {
                new ConsoleLine(ConsoleLineSeverity.Error, text),
                new ConsoleLine(ConsoleLineSeverity.Info, Usage),
            });
        }
    }
}
