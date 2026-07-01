using System;
using System.Text;
using Data;
using UI.Enums;
using UnityEngine;

namespace UI.Tooltip
{
    /// <summary>
    /// Builds rich-text tooltip strings for block items in the inventory UI.
    /// Reads the current <see cref="TooltipDetail"/> level from <see cref="SettingsManager"/>
    /// and formats the <see cref="BlockType"/> data accordingly.
    /// </summary>
    public static class BlockTooltipBuilder
    {
        // ── Formatting constants ──────────────────────────────────────────

        private const string TITLE_START = "<color=#AA77FF><b>";
        private const string TITLE_END = "</b></color>";
        private const string SEPARATOR = "<color=#999>────────────────</color>";
        private const string TECH_SEPARATOR = "\n\n<color=#999>── Technical ──</color>";
        private const string INLINE_SEP = " <color=#888>|</color> ";
        private const string CHECK = "✔";
        private const string CROSS = "❌";
        private const string LIGHT_ICON = "☀";

        // ── Public API ────────────────────────────────────────────────────

        /// <summary>
        /// Builds a tooltip string for the given block type using the detail level
        /// from the current <see cref="Settings.itemTooltipDetail"/> setting.
        /// </summary>
        /// <param name="blockType">The block type data to format.</param>
        /// <param name="blockId">The numeric ID (index) of the block in the database.</param>
        /// <returns>A rich-text formatted tooltip string.</returns>
        public static string Build(BlockType blockType, byte blockId)
        {
            TooltipDetail detail = SettingsManager.LoadSettings().itemTooltipDetail;
            return Build(blockType, blockId, detail);
        }

        /// <summary>
        /// Builds a tooltip string for the given block type at the specified detail level.
        /// </summary>
        /// <param name="blockType">The block type data to format.</param>
        /// <param name="blockId">The numeric ID (index) of the block in the database.</param>
        /// <param name="detail">The verbosity level for the tooltip.</param>
        /// <returns>A rich-text formatted tooltip string.</returns>
        public static string Build(BlockType blockType, byte blockId, TooltipDetail detail)
        {
            if (detail == TooltipDetail.NameOnly)
                return blockType.blockName;

            StringBuilder sb = new StringBuilder(256);

            // ── Title ──
            sb.Append(TITLE_START).Append(blockType.blockName).Append(TITLE_END);
            sb.Append('\n').Append(SEPARATOR);

            // ── Tier 1: Basic Info ──
            AppendBasicInfo(sb, blockType, blockId);

            // ── Tier 2: Gameplay Info ──
            AppendGameplayInfo(sb, blockType);

            // ── Tier 3: Technical Info ──
            if (detail == TooltipDetail.Technical)
                AppendTechnicalInfo(sb, blockType);

            return sb.ToString();
        }

        // ── Tier 1: Basic ─────────────────────────────────────────────────

        private static void AppendBasicInfo(StringBuilder sb, BlockType blockType, byte blockId)
        {
            // Line: ID + Stack Size
            sb.Append('\n');
            sb.Append("ID: ").Append(blockId);
            sb.Append(INLINE_SEP).Append("Stack: ").Append(blockType.stackSize);

            // Line: Shape + Solid
            sb.Append('\n');
            sb.Append("Shape: ").Append(FormatPascalCase(blockType.renderShape.ToString()));
            sb.Append(INLINE_SEP).Append("Solid: ").Append(blockType.isSolid ? CHECK : CROSS);
        }

        // ── Tier 2: Gameplay ──────────────────────────────────────────────

        private static void AppendGameplayInfo(StringBuilder sb, BlockType blockType)
        {
            // Tags
            if (blockType.tags != BlockTags.NONE)
            {
                sb.Append('\n');
                sb.Append("Tags: ").Append(FormatFlags(blockType.tags));
            }

            // Fluid type (only if not None)
            if (blockType.fluidType != FluidType.None)
            {
                sb.Append('\n');
                sb.Append("Fluid: ").Append(FormatPascalCase(blockType.fluidType.ToString()));
            }

            // Opacity
            sb.Append('\n');
            sb.Append("Opacity: ").Append(blockType.opacity);
            if (blockType.IsOpaque)
                sb.Append(" (Opaque)");
            else if (blockType.IsFullyTransparentToLight)
                sb.Append(" (Transparent)");

            // Light emission (only if emitting)
            if (blockType.IsLightSource)
            {
                sb.Append('\n');
                sb.Append("Light: ").Append(blockType.lightEmission).Append(" ").Append(LIGHT_ICON);
            }

            // Metadata schema (only if non-trivial)
            if (blockType.metadataSchema != MetadataSchema.None)
            {
                sb.Append('\n');
                sb.Append("Orientation: ").Append(FormatPascalCase(blockType.metadataSchema.ToString()));

                if (blockType.placementMetadataMode != PlacementMetadataMode.None)
                {
                    sb.Append(" → ").Append(FormatPascalCase(blockType.placementMetadataMode.ToString()));
                }
            }

            // Collision bounds (only if custom)
            if (blockType.isSolid && blockType.collisionBounds.HasCustomBounds)
            {
                sb.Append('\n');
                sb.Append("Collision: ").Append(FormatPascalCase(blockType.collisionBounds.mode.ToString()));
            }

            // Can replace tags — the player-facing placement mask (only if non-empty)
            if (blockType.placementCanReplaceTags != BlockTags.NONE)
            {
                sb.Append('\n');
                sb.Append("Can Replace: ").Append(FormatFlags(blockType.placementCanReplaceTags));
            }
        }

        // ── Tier 3: Technical ─────────────────────────────────────────────

        private static void AppendTechnicalInfo(StringBuilder sb, BlockType blockType)
        {
            sb.Append(TECH_SEPARATOR);

            // Rendering flags
            sb.Append('\n');
            sb.Append("Render Neighbor Faces: ").Append(blockType.renderNeighborFaces ? CHECK : CROSS);
            sb.Append('\n');
            sb.Append("Has Behavior: ").Append(blockType.isActive ? CHECK : CROSS);

            // Default metadata
            sb.Append('\n');
            sb.Append("Default Metadata: ").Append(blockType.defaultMetadata);

            // Texture IDs per face
            sb.Append('\n');
            sb.Append("Textures: [B:").Append(blockType.backFaceTexture);
            sb.Append(" F:").Append(blockType.frontFaceTexture);
            sb.Append(" T:").Append(blockType.topFaceTexture);
            sb.Append(" Bo:").Append(blockType.bottomFaceTexture);
            sb.Append(" L:").Append(blockType.leftFaceTexture);
            sb.Append(" R:").Append(blockType.rightFaceTexture);
            sb.Append(']');

            // Collision bounds detail (min/max)
            if (blockType.isSolid && blockType.collisionBounds.HasCustomBounds)
            {
                sb.Append('\n');
                Vector3 min = blockType.collisionBounds.min;
                Vector3 max = blockType.collisionBounds.max;
                sb.Append("Bounds: (");
                sb.Append(min.x.ToString("F1")).Append(", ");
                sb.Append(min.y.ToString("F1")).Append(", ");
                sb.Append(min.z.ToString("F1")).Append(") → (");
                sb.Append(max.x.ToString("F1")).Append(", ");
                sb.Append(max.y.ToString("F1")).Append(", ");
                sb.Append(max.z.ToString("F1")).Append(')');
            }

            // Fluid internals (only for fluid blocks)
            if (blockType.fluidType != FluidType.None)
            {
                sb.Append('\n');
                sb.Append("Flow: ").Append(blockType.flowLevels).Append(" levels");
                sb.Append(", ").Append((blockType.spreadChance * 100f).ToString("F0")).Append("% spread");

                if (blockType.waterfallsMaxSpread)
                    sb.Append(", waterfall spread");

                if (blockType.infiniteSourceRegeneration)
                    sb.Append(", ∞ source");

                sb.Append('\n');
                sb.Append("Default Level: ").Append(blockType.fluidLevel);
                sb.Append(INLINE_SEP).Append("Shader ID: ").Append(blockType.fluidShaderID);
            }
        }

        // ── Formatting Helpers ────────────────────────────────────────────

        /// <summary>
        /// Converts a PascalCase enum name to a spaced display name.
        /// Example: "CustomMesh" → "Custom Mesh", "PlayerYawCardinal" → "Player Yaw Cardinal".
        /// </summary>
        private static string FormatPascalCase(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;

            StringBuilder sb = new StringBuilder(name.Length + 4);
            sb.Append(name[0]);

            for (int i = 1; i < name.Length; i++)
            {
                // Insert space before an uppercase letter that follows a lowercase letter
                if (char.IsUpper(name[i]) && char.IsLower(name[i - 1]))
                    sb.Append(' ');

                sb.Append(name[i]);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Formats a <see cref="BlockTags"/> flags enum into a comma-separated, title-case string.
        /// Example: SOLID | ROCK | MINERAL → "Solid, Rock, Mineral".
        /// </summary>
        private static string FormatFlags(BlockTags tags)
        {
            if (tags == BlockTags.NONE) return "None";

            StringBuilder sb = new StringBuilder();

            foreach (BlockTags flag in Enum.GetValues(typeof(BlockTags)))
            {
                if (flag == BlockTags.NONE) continue;

                // Skip composite values — only process single-bit flags
                uint val = (uint)flag;
                if ((val & (val - 1)) != 0) continue;

                if ((tags & flag) == 0) continue;

                if (sb.Length > 0) sb.Append(", ");
                sb.Append(FormatScreamingCase(flag.ToString()));
            }

            return sb.ToString();
        }

        /// <summary>
        /// Converts a SCREAMING_CASE name to Title Case.
        /// Example: "GRAVITY_AFFECTED" → "Gravity Affected", "SOLID" → "Solid".
        /// </summary>
        private static string FormatScreamingCase(string screaming)
        {
            string[] parts = screaming.Split('_');

            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                parts[i] = char.ToUpper(parts[i][0]) + parts[i].Substring(1).ToLower();
            }

            return string.Join(" ", parts);
        }
    }
}
