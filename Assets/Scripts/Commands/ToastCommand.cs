using System;
using UI.Toast;

namespace Commands
{
    /// <summary>
    /// <c>/toast</c> — raises test cards on the toast surface so its stacking, its independent timers and
    /// its mid-stack gap closure can be seen without waiting on a real consumer.
    /// </summary>
    /// <remarks>
    /// The verification instrument for the toast system rather than a player feature. The staggered
    /// durations are the point: raising N cards that all expire together proves nothing, while cards that
    /// expire out of order exercise the one thing most likely to be subtly wrong — a card leaving from the
    /// <i>middle</i> of the stack, whose gap the layout group has to close without overlapping its
    /// neighbors.
    /// </remarks>
    public sealed class ToastCommand : IConsoleCommand
    {
        private static readonly string[] s_noAliases = Array.Empty<string>();

        /// <summary>How many cards a bare <c>/toast</c> raises.</summary>
        private const int DEFAULT_COUNT = 3;

        /// <summary>
        /// Upper bound on the card count: exactly what one anchor accepts before it starts dropping.
        /// </summary>
        /// <remarks>
        /// Taken from the manager rather than written down, so the reply cannot claim to have raised cards
        /// the manager silently discarded.
        /// </remarks>
        private const int MAX_COUNT = ToastManager.AnchorCapacity;

        /// <summary>Dwell of the first card, in seconds.</summary>
        private const float BASE_DWELL_SECONDS = 3f;

        /// <summary>Spacing between successive expiry ranks, in seconds.</summary>
        private const float DWELL_STEP_SECONDS = 1.5f;

        /// <summary>Sub-command that raises the icon-glyph shortlist.</summary>
        private const string GLYPH_PROBE_KEYWORD = "glyphs";

        /// <summary>Seconds each glyph candidate stays up. Short, because eight of them queue through.</summary>
        private const float GLYPH_PROBE_DWELL_SECONDS = 2.5f;

        /// <summary>
        /// The icon-glyph shortlist: every candidate Monocraft reports a character-table entry for.
        /// </summary>
        /// <remarks>
        /// <c>U+26A0 WARNING SIGN</c> — the obvious mark for a warning — is <b>absent</b> from the atlas, so
        /// the warning glyph has to be chosen from substitutes. Measured 2026-09-02 against
        /// <c>Assets/Fonts/Monocraft/Monocraft.asset</c>.
        /// </remarks>
        private static readonly (string Glyph, string Label)[] s_glyphCandidates =
        {
            ("!", "U+0021 EXCLAMATION (warning)"),
            ("‼", "U+203C DOUBLE EXCLAMATION (warning)"),
            ("▲", "U+25B2 BLACK UP TRIANGLE (warning)"),
            ("⚡", "U+26A1 HIGH VOLTAGE (warning)"),
            ("×", "U+00D7 MULTIPLICATION SIGN (error)"),
            ("✘", "U+2718 HEAVY BALLOT X (error)"),
            ("❌", "U+274C CROSS MARK (error)"),
            ("X", "U+0058 LATIN CAPITAL X (error)"),
        };

        /// <inheritdoc/>
        public string Name => "toast";

        /// <inheritdoc/>
        public string[] Aliases => s_noAliases;

        /// <inheritdoc/>
        public string Usage => "/toast [count] [topright|topleft|bottomright|bottomleft] [info|warning|error] | /toast glyphs";

        /// <inheritdoc/>
        public CommandResult Execute(CommandContext ctx, CommandArgs args)
        {
            if (ToastManager.Instance == null)
                return CommandResult.Error("No toast surface in the scene.");

            int count = DEFAULT_COUNT;
            ToastAnchor anchor = ToastAnchor.None;
            ToastVariant variant = ToastVariant.Info;

            for (int i = 0; i < args.Count; i++)
            {
                CommandToken token = args[i];

                if (token.Type == CommandTokenType.Number)
                {
                    if (!token.IsInteger || token.Integer < 1 || token.Integer > MAX_COUNT)
                        return CommandResult.Error($"Card count must be a whole number from 1 to {MAX_COUNT}.");

                    count = token.Integer;
                    continue;
                }

                if (string.Equals(token.Text, GLYPH_PROBE_KEYWORD, StringComparison.OrdinalIgnoreCase))
                    return ShowGlyphProbe(anchor);

                // Anchor and variant are both bare words, so each token is offered to both parsers and the
                // order they are typed in does not matter.
                if (TryParseAnchor(token.Text, out ToastAnchor parsedAnchor))
                {
                    anchor = parsedAnchor;
                    continue;
                }

                if (!TryParseVariant(token.Text, out variant))
                    return CommandResult.Error($"Unknown anchor or variant '{token.Text}'. Usage: {Usage}");
            }

            for (int i = 0; i < count; i++)
            {
                float dwell = BASE_DWELL_SECONDS + ExpiryRank(i) * DWELL_STEP_SECONDS;

                ToastManager.Show(new ToastRequest(
                    $"Test card {i + 1} of {count}",
                    $"Dismisses after {dwell:0.0}s",
                    null,
                    dwell,
                    anchor,
                    variant));
            }

            string where = anchor == ToastAnchor.None ? "the default anchor" : anchor.ToString();
            string midStackHint = count >= 2
                ? " The second card goes first, so watch the stack close a gap from the middle."
                : string.Empty;

            return CommandResult.Info(
                $"Raised {count} card{(count == 1 ? "" : "s")} at {where}, " +
                $"{DWELL_STEP_SECONDS:0.0}s apart.{midStackHint}");
        }

        /// <summary>
        /// The order the card raised at <paramref name="index"/> expires in, as a rank from zero.
        /// </summary>
        /// <param name="index">The card's position in the raise order.</param>
        /// <returns>Its expiry rank; lower ranks dismiss first.</returns>
        /// <remarks>
        /// The first two ranks are swapped, and that swap is the entire reason this command exists. Dwells
        /// that simply grow with the index expire the cards in the order they were raised — so the card
        /// leaving is always the <i>top</i> of the stack, and the mid-stack case never occurs however many
        /// cards are raised. Swapping the first two makes the second card, the middle of the three on
        /// screen, the first to go: the layout group then has to close a hole with a neighbor above it and
        /// a neighbor below it.
        /// </remarks>
        private static int ExpiryRank(int index) => index switch
        {
            0 => 1,
            1 => 0,
            _ => index,
        };

        /// <summary>Parses a variant name, case-insensitively.</summary>
        /// <param name="text">The token text to match.</param>
        /// <param name="variant">Receives the parsed variant.</param>
        /// <returns>True when the text named a variant.</returns>
        private static bool TryParseVariant(string text, out ToastVariant variant)
        {
            switch (text.ToLowerInvariant())
            {
                case "info":
                    variant = ToastVariant.Info;
                    return true;
                case "warning":
                case "warn":
                    variant = ToastVariant.Warning;
                    return true;
                case "error":
                case "err":
                    variant = ToastVariant.Error;
                    return true;
                default:
                    variant = ToastVariant.Info;
                    return false;
            }
        }

        /// <summary>
        /// Raises one card per candidate icon glyph, so the shortlist can be judged at real icon size.
        /// </summary>
        /// <param name="anchor">Where to raise them.</param>
        /// <returns>What was raised.</returns>
        /// <remarks>
        /// The project font's atlas is <b>static</b>, so a codepoint it lacks renders as a blank box with no
        /// fallback — and a font can equally carry a table entry whose glyph is empty. Neither case is
        /// visible from <c>HasCharacter</c>, which is why a shortlist is looked at rather than trusted.
        /// <para>
        /// Kept after the warning/error defaults were chosen, for two reasons: a new variant needs the same
        /// eyeball test before its glyph can be trusted, and this is the only caller that exercises
        /// <see cref="ToastRequest.Glyph"/>, the per-request override.
        /// </para>
        /// </remarks>
        private static CommandResult ShowGlyphProbe(ToastAnchor anchor)
        {
            foreach ((string glyph, string label) in s_glyphCandidates)
            {
                ToastManager.Show(new ToastRequest(
                    label,
                    $"glyph: {glyph}",
                    null,
                    GLYPH_PROBE_DWELL_SECONDS,
                    anchor,
                    ToastVariant.Info,
                    glyph));
            }

            return CommandResult.Info(
                $"Raised {s_glyphCandidates.Length} glyph candidates, {GLYPH_PROBE_DWELL_SECONDS:0.0}s each. " +
                "A blank icon slot means the font has no real glyph for that codepoint.");
        }

        /// <summary>Parses an anchor name, case-insensitively.</summary>
        /// <param name="text">The token text to match.</param>
        /// <param name="anchor">Receives the parsed anchor.</param>
        /// <returns>True when the text named a real corner.</returns>
        /// <remarks>
        /// Hand-matched rather than <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>, which would
        /// accept "None" and the raw ordinals as valid input.
        /// </remarks>
        private static bool TryParseAnchor(string text, out ToastAnchor anchor)
        {
            switch (text.ToLowerInvariant())
            {
                case "topright":
                    anchor = ToastAnchor.TopRight;
                    return true;
                case "topleft":
                    anchor = ToastAnchor.TopLeft;
                    return true;
                case "bottomright":
                    anchor = ToastAnchor.BottomRight;
                    return true;
                case "bottomleft":
                    anchor = ToastAnchor.BottomLeft;
                    return true;
                default:
                    anchor = ToastAnchor.None;
                    return false;
            }
        }
    }
}
