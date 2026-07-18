using System;
using System.Globalization;
using Data;

namespace Commands
{
    /// <summary>Shared argument-parsing helpers for the built-in command pack (CMD-3).</summary>
    internal static class CommandArgUtility
    {
        /// <summary>
        /// Largest coordinate magnitude treated as inside the addressable world: a small margin under
        /// <see cref="int.MaxValue"/> keeps neighbor-chunk voxel-origin math (<c>chunk×16</c> ± a chunk)
        /// from wrapping at the extreme edge (§4.3's ±2³¹⁻ᵋ error tier). Shared by /teleport and /setblock.
        /// </summary>
        public const long AddressableLimitVoxels = int.MaxValue - 32L;

        /// <summary>
        /// Parses one coordinate token as an integer voxel coordinate. Distinguishes a typed decimal
        /// (usage error) from an integer literal too large for <see cref="int"/> (which the tokenizer
        /// downgraded to a float) — the latter is the §4.3 addressable-world error.
        /// </summary>
        /// <param name="token">The coordinate token.</param>
        /// <param name="axis">The axis name for error text.</param>
        /// <param name="value">The parsed coordinate.</param>
        /// <param name="error">The error text on failure; null on success.</param>
        /// <returns>True when parsed.</returns>
        public static bool TryParseCoord(CommandToken token, string axis, out int value, out string error)
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

        /// <summary>
        /// Resolves a block name to its ID by scanning the block database's <c>blockName</c>s
        /// (case-insensitive) — never raw IDs (the BlockIDs rule; names are the user-facing surface).
        /// </summary>
        /// <param name="world">The world whose block database to scan (non-null).</param>
        /// <param name="blockName">The name as typed (quoted names supported by the tokenizer).</param>
        /// <param name="id">The resolved block ID.</param>
        /// <param name="error">The error text on failure; null on success.</param>
        /// <returns>True when resolved.</returns>
        public static bool TryResolveBlockId(World world, string blockName, out ushort id, out string error)
        {
            BlockType[] types = world.BlockTypes;
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i] != null && string.Equals(types[i].blockName, blockName, StringComparison.OrdinalIgnoreCase))
                {
                    id = (ushort)i;
                    error = null;
                    return true;
                }
            }

            id = 0;
            error = $"Unknown block '{blockName}'.";
            return false;
        }

        /// <summary>Parses an optional on/off word token.</summary>
        /// <param name="token">The token to interpret.</param>
        /// <param name="enabled">The parsed state on success.</param>
        /// <returns>True when the token is exactly "on" or "off" (case-insensitive).</returns>
        public static bool TryParseOnOff(CommandToken token, out bool enabled)
        {
            if (token.Type == CommandTokenType.Word)
            {
                if (string.Equals(token.Text, "on", StringComparison.OrdinalIgnoreCase))
                {
                    enabled = true;
                    return true;
                }

                if (string.Equals(token.Text, "off", StringComparison.OrdinalIgnoreCase))
                {
                    enabled = false;
                    return true;
                }
            }

            enabled = false;
            return false;
        }

        /// <summary>Formats a float invariant-culture (a Dutch host locale must never print `0,5`).</summary>
        /// <param name="value">The value to format.</param>
        /// <returns>The invariant string, up to three decimals.</returns>
        public static string Invariant(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
