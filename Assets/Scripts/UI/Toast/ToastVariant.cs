namespace UI.Toast
{
    /// <summary>
    /// What kind of notice a toast is, which selects its accent color, its default glyph and its
    /// default dwell.
    /// </summary>
    /// <remarks>
    /// Deliberately its own enum rather than a reuse of <see cref="Commands.ConsoleLineSeverity"/>: the
    /// toast surface must not depend on the command layer, and the two lists are only coincidentally
    /// similar — a variant such as an achievement belongs here and never in a console severity.
    /// </remarks>
    public enum ToastVariant
    {
        /// <summary>Neutral notice, carrying no accent coloring. The default.</summary>
        Info,

        /// <summary>Something the player should notice but that is not a failure.</summary>
        Warning,

        /// <summary>Something failed.</summary>
        Error,
    }
}
