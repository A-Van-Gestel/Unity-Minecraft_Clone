namespace UI.Toast
{
    /// <summary>
    /// Which screen corner a toast card's stack grows from.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="UI.Tooltip.TooltipHoverPosition"/>, including the <see cref="None"/>-means-default
    /// convention, so a reader who knows one knows the other. Each anchor owns its own stack and its own
    /// card cap: a card raised at one corner never delays a card raised at another.
    /// </remarks>
    public enum ToastAnchor
    {
        /// <summary>Defer to the manager's default anchor.</summary>
        None,

        /// <summary>Stack from the top-right corner downward. The manager's default.</summary>
        TopRight,

        /// <summary>Stack from the top-left corner downward.</summary>
        TopLeft,

        /// <summary>Stack from the bottom-right corner upward.</summary>
        BottomRight,

        /// <summary>Stack from the bottom-left corner upward.</summary>
        BottomLeft,
    }
}
