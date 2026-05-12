namespace UI.Tooltip
{
    /// <summary>
    /// Defines how a tooltip should be positioned relative to the cursor or hovered element.
    /// </summary>
    public enum TooltipHoverPosition
    {
        /// <summary>
        /// The tooltip will dynamically follow the mouse cursor.
        /// </summary>
        FollowMouse,

        /// <summary>
        /// The tooltip's top-left corner aligns with the trigger element's top-right corner.
        /// Falls back to the left side if there is not enough screen space on the right.
        /// </summary>
        TopLeft,

        /// <summary>
        /// The tooltip is horizontally centered and placed directly above the trigger element.
        /// Useful for hotbar/toolbar item names.
        /// </summary>
        BottomCenter,
    }
}
