using UnityEngine;

namespace UI.Toast
{
    /// <summary>
    /// The look one <see cref="ToastVariant"/> gives a card: its accent color, the glyph its icon slot
    /// falls back to, the tints its backdrop uses, and how long it dwells by default.
    /// </summary>
    /// <remarks>
    /// A value the card is handed rather than a branch inside it, so adding a variant is a row in
    /// <see cref="ToastStyles"/> and nothing else.
    /// </remarks>
    public readonly struct ToastStyle
    {
        /// <summary>Color of the icon glyph and the title.</summary>
        public readonly Color Accent;

        /// <summary>
        /// Glyph the icon slot shows when the request supplies neither a sprite nor a glyph of its own.
        /// Empty collapses the slot.
        /// </summary>
        public readonly string Glyph;

        /// <summary>Multiply tint for this variant's blur material instance.</summary>
        public readonly Color BlurTint;

        /// <summary>Flat backdrop painted when blur is unavailable or suppressed.</summary>
        public readonly Color FlatBackdrop;

        /// <summary>Seconds a card of this variant dwells when the request does not ask for a duration.</summary>
        public readonly float DefaultDwellSeconds;

        /// <summary>Creates a style.</summary>
        /// <param name="accent">Color of the icon glyph and the title.</param>
        /// <param name="glyph">Fallback icon glyph; empty collapses the slot.</param>
        /// <param name="blurTint">Multiply tint for the blur material.</param>
        /// <param name="flatBackdrop">Flat backdrop for the no-blur state.</param>
        /// <param name="defaultDwellSeconds">Default dwell in seconds.</param>
        public ToastStyle(Color accent, string glyph, Color blurTint, Color flatBackdrop,
            float defaultDwellSeconds)
        {
            Accent = accent;
            Glyph = glyph;
            BlurTint = blurTint;
            FlatBackdrop = flatBackdrop;
            DefaultDwellSeconds = defaultDwellSeconds;
        }
    }

    /// <summary>The style table: one <see cref="ToastStyle"/> per <see cref="ToastVariant"/>.</summary>
    /// <remarks>
    /// Accent colors are parsed from <see cref="ConsoleTextFormatter"/>'s severity constants rather than
    /// written again here, so the console and the toast surface cannot drift on what "warning" looks like.
    /// That class is deliberately free of Unity types — its colors are TMP hex strings — which is why they
    /// are parsed once into <see cref="Color"/> here instead of being shared as values.
    /// </remarks>
    public static class ToastStyles
    {
        /// <summary>Neutral blur tint, matching the console and the scene panels.</summary>
        private static readonly Color s_neutralBlurTint = new Color(0.415f, 0.415f, 0.415f, 1f);

        /// <summary>Neutral flat backdrop, matching the console's no-blur fallback.</summary>
        private static readonly Color s_neutralFlatBackdrop = new Color(0f, 0f, 0f, 0.55f);

        /// <summary>Title/glyph color for a neutral card.</summary>
        private static readonly Color s_infoAccent = new Color(0.96f, 0.96f, 0.96f, 1f);

        /// <summary>
        /// How far a variant's blur tint moves from neutral toward its accent.
        /// </summary>
        /// <remarks>
        /// Low, because <c>_MultiplyColor</c> multiplies the blurred world and a saturated tint turns the
        /// whole card into a color cast that the text then has to fight. Tuning value — adjust by eye.
        /// </remarks>
        private const float BLUR_TINT_STRENGTH = 0.35f;

        /// <summary>How far a variant's flat backdrop moves from black toward its accent.</summary>
        private const float FLAT_TINT_STRENGTH = 0.18f;

        /// <summary>Default dwell for a neutral card, in seconds.</summary>
        private const float INFO_DWELL_SECONDS = 4.5f;

        /// <summary>
        /// Default dwell for a warning or an error, in seconds.
        /// </summary>
        /// <remarks>
        /// Longer than a neutral card because these arrive unprompted and report something the player did
        /// not ask about, so they have to survive not being looked at immediately.
        /// </remarks>
        private const float ALERT_DWELL_SECONDS = 7f;

        private static readonly ToastStyle s_info = Build(s_infoAccent, string.Empty, INFO_DWELL_SECONDS);

        private static readonly ToastStyle s_warning = Build(Parse(ConsoleTextFormatter.WarningColor),
            WarningGlyph, ALERT_DWELL_SECONDS);

        private static readonly ToastStyle s_error = Build(Parse(ConsoleTextFormatter.ErrorColor),
            ErrorGlyph, ALERT_DWELL_SECONDS);

        /// <summary>
        /// Icon glyph for a warning: U+0021 EXCLAMATION MARK.
        /// </summary>
        /// <remarks>
        /// <b>Not</b> U+26A0 WARNING SIGN, which the project font does not contain — and its atlas is static,
        /// so nothing supplies it at runtime. A character-table entry can also point at an empty glyph, which
        /// <c>HasCharacter</c> cannot reveal, so the shortlist was rendered and judged by eye; the design doc
        /// §3.5 carries the measured coverage.
        /// </remarks>
        public const string WarningGlyph = "!";

        /// <summary>
        /// Icon glyph for an error: U+00D7 MULTIPLICATION SIGN.
        /// </summary>
        /// <remarks>
        /// Latin-1, so it is certain to be a real glyph rather than a character-table entry pointing at an
        /// empty one. Chosen by eye alongside <see cref="WarningGlyph"/>.
        /// </remarks>
        public const string ErrorGlyph = "×";

        /// <summary>
        /// The style for a variant.
        /// </summary>
        /// <param name="variant">The variant to style.</param>
        /// <returns>Its style; an unrecognized variant falls back to <see cref="ToastVariant.Info"/>.</returns>
        /// <remarks>
        /// Total by construction — the default arm means a variant added to the enum and forgotten here
        /// renders as a neutral card rather than as an unstyled one, so there is no "missing entry" state
        /// for a test to have to catch.
        /// </remarks>
        public static ToastStyle For(ToastVariant variant) => variant switch
        {
            ToastVariant.Warning => s_warning,
            ToastVariant.Error => s_error,
            _ => s_info,
        };

        /// <summary>Derives a full style from an accent color.</summary>
        /// <param name="accent">The variant's accent.</param>
        /// <param name="glyph">Its fallback icon glyph.</param>
        /// <param name="dwellSeconds">Its default dwell.</param>
        /// <returns>The assembled style.</returns>
        private static ToastStyle Build(Color accent, string glyph, float dwellSeconds)
        {
            Color blur = Color.Lerp(s_neutralBlurTint, accent, BLUR_TINT_STRENGTH);
            blur.a = 1f;

            Color flat = Color.Lerp(Color.black, accent, FLAT_TINT_STRENGTH);
            flat.a = s_neutralFlatBackdrop.a;

            return new ToastStyle(accent, glyph, blur, flat, dwellSeconds);
        }

        /// <summary>Parses one of the console's hex severity colors.</summary>
        /// <param name="html">The hex string, in TMP rich-text form.</param>
        /// <returns>The parsed color, or white when the string cannot be parsed.</returns>
        private static Color Parse(string html) =>
            ColorUtility.TryParseHtmlString(html, out Color parsed) ? parsed : Color.white;
    }
}
