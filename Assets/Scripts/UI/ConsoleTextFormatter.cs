using Commands;

namespace UI
{
    /// <summary>
    /// Pure formatting policy for console output: maps a <see cref="ConsoleLine"/> to the TMP
    /// rich-text markup the history view renders. Kept free of Unity types so the validation
    /// suite pins the severity→color mapping headless.
    /// </summary>
    public static class ConsoleTextFormatter
    {
        /// <summary>Hex color (TMP rich-text form) for informational lines.</summary>
        public const string InfoColor = "#E8E8E8";

        /// <summary>Hex color for warning lines (confirmation prompts included).</summary>
        public const string WarningColor = "#FFC24B";

        /// <summary>Hex color for error lines.</summary>
        public const string ErrorColor = "#FF6060";

        /// <summary>
        /// Formats one line as TMP rich text: severity color wrapping the text, with the text
        /// itself protected by <c>&lt;noparse&gt;</c> so user-typed markup cannot inject tags
        /// (any literal <c>&lt;/noparse&gt;</c> in the text is stripped via <see cref="StripNoparse"/>
        /// to keep the guard intact).
        /// </summary>
        /// <param name="line">The console line to format.</param>
        /// <returns>The rich-text string for the history view.</returns>
        public static string Format(ConsoleLine line)
        {
            string safe = StripNoparse(line.Text);
            return $"<color={ColorOf(line.Severity)}><noparse>{safe}</noparse></color>";
        }

        /// <summary>
        /// Removes every literal <c>&lt;/noparse&gt;</c> from <paramref name="text"/> so it cannot
        /// terminate a surrounding <c>&lt;noparse&gt;</c> guard and let injected markup render. Loops
        /// until none remain: a single pass can splice a fresh <c>&lt;/noparse&gt;</c> from the
        /// surrounding characters (e.g. <c>&lt;/nop&lt;/noparse&gt;arse&gt;</c>). Shared by
        /// <see cref="Format"/> and the CMD-5 inline-ghost overlay.
        /// </summary>
        /// <param name="text">The raw user text to neutralize (null treated as empty).</param>
        /// <returns>The text with all <c>&lt;/noparse&gt;</c> occurrences removed.</returns>
        public static string StripNoparse(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            while (text.Contains("</noparse>"))
                text = text.Replace("</noparse>", "");
            return text;
        }

        /// <summary>The rich-text color for a severity.</summary>
        /// <param name="severity">The line severity.</param>
        /// <returns>The hex color string.</returns>
        public static string ColorOf(ConsoleLineSeverity severity)
        {
            switch (severity)
            {
                case ConsoleLineSeverity.Warning: return WarningColor;
                case ConsoleLineSeverity.Error: return ErrorColor;
                default: return InfoColor;
            }
        }
    }
}
