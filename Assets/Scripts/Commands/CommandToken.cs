namespace Commands
{
    /// <summary>Classification of one tokenized command-line argument.</summary>
    public enum CommandTokenType
    {
        /// <summary>A plain word (including quoted strings and anything that is not a number/selector/relative token).</summary>
        Word,

        /// <summary>A signed integer or float literal (invariant culture).</summary>
        Number,

        /// <summary>A target selector: <c>@</c> followed by at least one character (e.g. <c>@player</c>).</summary>
        Selector,

        /// <summary>A <c>~</c>-prefixed token — reserved for relative coordinates (rejected until v2).</summary>
        Relative,
    }

    /// <summary>
    /// One tokenized command argument: its classification, raw text (quotes stripped for quoted words),
    /// and — for <see cref="CommandTokenType.Number"/> — its parsed numeric value.
    /// </summary>
    public readonly struct CommandToken
    {
        /// <summary>The token's classification.</summary>
        public readonly CommandTokenType Type;

        /// <summary>The token text. Quoted words are stored without their quotes; selectors keep their <c>@</c>.</summary>
        public readonly string Text;

        /// <summary>The parsed numeric value (0 for non-number tokens).</summary>
        public readonly float Number;

        /// <summary>True when this is a <see cref="CommandTokenType.Number"/> written as an integer literal.</summary>
        public readonly bool IsInteger;

        /// <summary>The parsed integer value (0 unless <see cref="IsInteger"/>).</summary>
        public readonly int Integer;

        /// <summary>Initializes a non-numeric token.</summary>
        /// <param name="type">The token's classification.</param>
        /// <param name="text">The token text.</param>
        public CommandToken(CommandTokenType type, string text)
        {
            Type = type;
            Text = text;
            Number = 0f;
            IsInteger = false;
            Integer = 0;
        }

        /// <summary>Initializes a numeric token.</summary>
        /// <param name="text">The token text as typed.</param>
        /// <param name="number">The parsed float value.</param>
        /// <param name="isInteger">Whether the literal was an integer.</param>
        /// <param name="integer">The parsed integer value (when <paramref name="isInteger"/>).</param>
        public CommandToken(string text, float number, bool isInteger, int integer)
        {
            Type = CommandTokenType.Number;
            Text = text;
            Number = number;
            IsInteger = isInteger;
            Integer = integer;
        }
    }
}
