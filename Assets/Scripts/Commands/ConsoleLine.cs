namespace Commands
{
    /// <summary>Severity of a console output line. The UI maps these to colors; the engine only classifies.</summary>
    public enum ConsoleLineSeverity
    {
        /// <summary>Normal informational output.</summary>
        Info,

        /// <summary>A warning (e.g. a confirmation prompt for a hazardous action).</summary>
        Warning,

        /// <summary>An error (parse failure, unknown command, rejected input).</summary>
        Error,
    }

    /// <summary>One line of console output: severity plus text.</summary>
    public readonly struct ConsoleLine
    {
        /// <summary>The line's severity classification.</summary>
        public readonly ConsoleLineSeverity Severity;

        /// <summary>The line's text (plain — presentation markup is the UI's job).</summary>
        public readonly string Text;

        /// <summary>Initializes a console line.</summary>
        /// <param name="severity">The line's severity classification.</param>
        /// <param name="text">The line's text.</param>
        public ConsoleLine(ConsoleLineSeverity severity, string text)
        {
            Severity = severity;
            Text = text;
        }
    }
}
