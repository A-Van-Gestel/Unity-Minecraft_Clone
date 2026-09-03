using UnityEngine;

namespace UI.Toast
{
    /// <summary>
    /// One transient card to show: what it says, what it shows beside the text, how long it dwells, and
    /// which corner it stacks in.
    /// </summary>
    /// <remarks>
    /// A struct the caller fills rather than a widening parameter list on <c>Show</c>, so a new kind of
    /// notice adds a variant and a style rather than changing the manager's signature.
    /// Passed by <c>in</c> at every seam — it is copied into the card's own state on show, and never held.
    /// </remarks>
    public readonly struct ToastRequest
    {
        /// <summary>The card's headline. A card with no title is not shown.</summary>
        public readonly string Title;

        /// <summary>The line under the title, or null/empty to collapse that row.</summary>
        public readonly string Subtitle;

        /// <summary>The card's icon sprite, or null to fall back to <see cref="Glyph"/>.</summary>
        public readonly Sprite Icon;

        /// <summary>
        /// A text glyph for the icon slot, used when <see cref="Icon"/> is null. Null or empty falls back
        /// to the variant's default glyph.
        /// </summary>
        /// <remarks>
        /// The slot resolves sprite → this → the variant's glyph → collapsed, so a consumer can override
        /// the look without authoring a sprite. Whatever is passed must exist in the project font's atlas,
        /// which is static — an absent codepoint renders as a blank box rather than falling back.
        /// </remarks>
        public readonly string Glyph;

        /// <summary>Seconds the card stays before it begins its exit. Zero or less uses the manager's default.</summary>
        public readonly float DwellSeconds;

        /// <summary>Which corner to stack in. <see cref="ToastAnchor.None"/> uses the manager's default.</summary>
        public readonly ToastAnchor Anchor;

        /// <summary>What kind of notice this is, selecting the card's accent, glyph and default dwell.</summary>
        public readonly ToastVariant Variant;

        /// <summary>Whether this request carries enough to draw a card.</summary>
        /// <remarks>
        /// Title-only, because the subtitle and the icon both have defined empty states while a card with no
        /// text at all would show as a bare rectangle with a dwell timer — a bug that is silent on screen.
        /// </remarks>
        public bool IsShowable => !string.IsNullOrWhiteSpace(Title);

        /// <summary>Creates a toast request.</summary>
        /// <param name="title">The card's headline.</param>
        /// <param name="subtitle">The line under the title, or null to collapse that row.</param>
        /// <param name="icon">The card's icon, or null to collapse the icon slot.</param>
        /// <param name="dwellSeconds">Seconds to dwell; zero or less uses the variant's default.</param>
        /// <param name="anchor">Which corner to stack in; <see cref="ToastAnchor.None"/> uses the default.</param>
        /// <param name="variant">What kind of notice this is; defaults to a neutral card.</param>
        /// <param name="glyph">Icon glyph override, used when no sprite is given; null uses the variant's.</param>
        public ToastRequest(string title, string subtitle = null, Sprite icon = null,
            float dwellSeconds = 0f, ToastAnchor anchor = ToastAnchor.None,
            ToastVariant variant = ToastVariant.Info, string glyph = null)
        {
            Title = title;
            Subtitle = subtitle;
            Icon = icon;
            DwellSeconds = dwellSeconds;
            Anchor = anchor;
            Variant = variant;
            Glyph = glyph;
        }
    }
}
