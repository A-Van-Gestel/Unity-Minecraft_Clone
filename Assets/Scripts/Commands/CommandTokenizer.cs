using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Commands
{
    /// <summary>
    /// Splits a command line (the text after the <c>/</c> prefix) into classified tokens.
    /// Whitespace-separated; double quotes group a multi-word token; numbers parse with the
    /// invariant culture so a host locale (e.g. comma-decimal) can never change command semantics.
    /// </summary>
    public static class CommandTokenizer
    {
        /// <summary>Error text for a quoted string that never closes.</summary>
        public const string UnterminatedQuoteError = "Unterminated quoted string.";

        /// <summary>
        /// Tokenizes <paramref name="input"/> into <paramref name="tokens"/> (the list is cleared first).
        /// </summary>
        /// <param name="input">The command line, without its <c>/</c> prefix.</param>
        /// <param name="tokens">Receives the classified tokens.</param>
        /// <param name="error">The parse error on failure; null on success.</param>
        /// <returns>True when tokenization succeeded.</returns>
        public static bool Tokenize(string input, List<CommandToken> tokens, out string error)
        {
            tokens.Clear();
            error = null;
            if (string.IsNullOrEmpty(input))
                return true;

            StringBuilder current = new StringBuilder();
            int i = 0;
            while (i < input.Length)
            {
                char c = input[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                if (c == '"')
                {
                    // Quoted token: always a Word, quotes stripped, may contain whitespace.
                    current.Clear();
                    i++;
                    bool closed = false;
                    while (i < input.Length)
                    {
                        if (input[i] == '"')
                        {
                            closed = true;
                            i++;
                            break;
                        }

                        current.Append(input[i]);
                        i++;
                    }

                    if (!closed)
                    {
                        error = UnterminatedQuoteError;
                        return false;
                    }

                    tokens.Add(new CommandToken(CommandTokenType.Word, current.ToString()));
                    continue;
                }

                current.Clear();
                while (i < input.Length && !char.IsWhiteSpace(input[i]) && input[i] != '"')
                {
                    current.Append(input[i]);
                    i++;
                }

                tokens.Add(Classify(current.ToString()));
            }

            return true;
        }

        /// <summary>Classifies one unquoted token as relative, selector, number, or word.</summary>
        /// <param name="text">The raw token text.</param>
        /// <returns>The classified token.</returns>
        private static CommandToken Classify(string text)
        {
            if (text[0] == '~')
                return new CommandToken(CommandTokenType.Relative, text);

            if (text[0] == '@' && text.Length > 1)
                return new CommandToken(CommandTokenType.Selector, text);

            // Only attempt numeric parsing on digit/sign/dot starts, so words like "NaN"
            // or "Infinity" (which float.TryParse would accept) stay words.
            char first = text[0];
            if (char.IsDigit(first) || first == '-' || first == '+' || first == '.')
            {
                if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int intValue))
                    return new CommandToken(text, intValue, isInteger: true, integer: intValue);

                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float floatValue))
                    return new CommandToken(text, floatValue, isInteger: false, integer: 0);
            }

            return new CommandToken(CommandTokenType.Word, text);
        }
    }
}
