namespace UI.Enums
{
    /// <summary>
    /// Controls how much information is displayed in inventory item tooltips.
    /// Used by <see cref="Tooltip.BlockTooltipBuilder"/> to determine the detail level.
    /// </summary>
    public enum TooltipDetail : byte
    {
        /// <summary>Shows only the block name.</summary>
        NameOnly,

        /// <summary>Adds block ID, stack size, tags, lighting, and placement info.</summary>
        Standard,

        /// <summary>Shows all internal engine data including texture IDs, fluid properties, and collision bounds.</summary>
        Technical,
    }
}
