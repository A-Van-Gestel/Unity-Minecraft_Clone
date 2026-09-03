using System;
using System.Collections;
using TMPro;
using UI.Builders;
using UnityEngine;
using UnityEngine.UI;

namespace UI.Toast
{
    /// <summary>
    /// One toast card: the view (icon, title, subtitle over a frosted backdrop) and the lifetime that fades
    /// it in, holds it, and shrinks it back out again.
    /// </summary>
    /// <remarks>
    /// Built in code and pooled by <see cref="ToastManager"/> — never instantiated per toast. The card
    /// drives its own exit by shrinking its <see cref="LayoutElement"/> height to zero rather than removing
    /// itself: the parent's <see cref="VerticalLayoutGroup"/> then closes the gap as a normal rebuild, which
    /// is what makes a card expiring in the <i>middle</i> of the stack slide shut instead of snapping.
    /// <para>
    /// The backdrop is frosted glass, using the blur material <see cref="ToastManager"/> owns for this
    /// card's <see cref="ToastVariant"/> — the card never allocates one, because cards are pooled and a
    /// per-card instance would leak one material per toast ever shown. When blur is unavailable or
    /// suppressed the factory paints the variant's flat color instead.
    /// </para>
    /// <para>
    /// Because cards are reused, <see cref="Show"/> rewrites <b>every</b> variant-dependent property rather
    /// than only the ones a request sets — a card last shown as an error must not come back tinted red.
    /// </para>
    /// </remarks>
    public class ToastCard : MonoBehaviour
    {
        #region Style constants

        /// <summary>Card width in canvas reference pixels. The stack container sizes itself to this.</summary>
        private const float CARD_WIDTH = 340f;

        /// <summary>Inner padding on all four sides.</summary>
        private const int CARD_PADDING = 12;

        /// <summary>Gap between the icon slot and the text column.</summary>
        private const float ICON_GAP = 10f;

        /// <summary>Square edge of the icon slot. Collapses entirely when the card has no icon at all.</summary>
        private const float ICON_SIZE = 44f;

        /// <summary>Glyph point size as a fraction of the icon slot, leaving the mark a little breathing room.</summary>
        private const float GLYPH_FONT_SCALE = 0.8f;

        /// <summary>Gap between the title and the subtitle.</summary>
        private const float TEXT_SPACING = 2f;

        private const float TITLE_FONT_SIZE = 21f;
        private const float SUBTITLE_FONT_SIZE = 16f;

        /// <summary>Seconds the card takes to fade in.</summary>
        private const float ENTER_SECONDS = 0.22f;

        /// <summary>Seconds the card takes to fade and shrink away.</summary>
        private const float EXIT_SECONDS = 0.3f;

        private static readonly Color s_subtitleColor = new Color(0.72f, 0.74f, 0.78f, 1f);

        #endregion

        private RectTransform _rect;
        private Image _backdrop;
        private CanvasGroup _group;
        private LayoutElement _layout;
        private GameObject _iconObject;
        private Image _iconImage;
        private GameObject _glyphObject;
        private TextMeshProUGUI _glyphText;
        private TextMeshProUGUI _titleText;
        private TextMeshProUGUI _subtitleText;
        private Coroutine _lifetime;
        private Action<ToastCard> _onFinished;

        /// <summary>
        /// The flat color this card paints when blur is unavailable or suppressed, from its variant's
        /// style. Held because <see cref="SetBackdrop"/> is re-called mid-life by the manager's backdrop
        /// swap and must not fall back to a neutral color on a warning card.
        /// </summary>
        private Color _flatBackdrop;

        /// <summary>The variant this card is currently showing, so the manager can re-resolve its material.</summary>
        public ToastVariant Variant { get; private set; }

        /// <summary>
        /// Builds a card hierarchy under <paramref name="parent"/>, inactive and ready to be shown.
        /// </summary>
        /// <param name="parent">The anchor container to parent under.</param>
        /// <param name="blurInstance">
        /// The manager's shared blur material, or null to paint the flat fallback.
        /// </param>
        /// <returns>The created card.</returns>
        /// <remarks>
        /// Construction lives here rather than on the manager so the manager owns only stacking, pooling and
        /// the request queue — a second card style becomes a second builder, not a branch in the manager.
        /// </remarks>
        public static ToastCard Create(Transform parent, Material blurInstance, in ToastStyle style)
        {
            GameObject root = new GameObject("ToastCard", typeof(RectTransform));
            root.transform.SetParent(parent, false);

            ToastCard card = root.AddComponent<ToastCard>();
            card.Build(blurInstance, in style);
            root.SetActive(false);
            return card;
        }

        /// <summary>Builds this card components and children. Called once, by <see cref="Create"/>.</summary>
        /// <param name="blurInstance">The variant's blur material, or null for the flat fallback.</param>
        /// <param name="style">The style the card is first built with.</param>
        private void Build(Material blurInstance, in ToastStyle style)
        {
            _rect = (RectTransform)transform;

            _backdrop = gameObject.AddComponent<Image>();
            _flatBackdrop = style.FlatBackdrop;
            SetBackdrop(blurInstance);

            // Clips the text while the card shrinks on exit; without it the content overflows the
            // collapsing rect and the card appears to slide under its neighbor rather than close.
            gameObject.AddComponent<RectMask2D>();

            _group = gameObject.AddComponent<CanvasGroup>();

            // The TooltipManager rule, and here a correctness requirement rather than a flicker guard: the
            // toast canvas sorts above every menu, so a card that took raycasts could eat a click on the
            // pause menu underneath it.
            _group.blocksRaycasts = false;
            _group.interactable = false;

            HorizontalLayoutGroup row = gameObject.AddComponent<HorizontalLayoutGroup>();
            row.padding = new RectOffset(CARD_PADDING, CARD_PADDING, CARD_PADDING, CARD_PADDING);
            row.spacing = ICON_GAP;
            row.childAlignment = TextAnchor.MiddleLeft;
            row.childControlWidth = true;
            row.childControlHeight = true;
            row.childForceExpandWidth = false;
            row.childForceExpandHeight = false;

            _layout = gameObject.AddComponent<LayoutElement>();
            _layout.preferredWidth = CARD_WIDTH;

            BuildIcon();
            BuildTextColumn(in style);
        }

        /// <summary>
        /// Points this card's backdrop at the shared blur material, or paints the flat fallback.
        /// </summary>
        /// <param name="blurInstance">The shared blur material, or null to force the flat fallback.</param>
        /// <remarks>
        /// Re-callable at any point in a card's life, because the manager swaps every live card to the flat
        /// backdrop while a full-screen panel is up: a blurred panel does not composite over the UI beneath it, it
        /// replaces it (UI_BLUR_BACKDROP_SYSTEM.md §4.2), so a frosted card at this canvas's sorting order
        /// would paint un-dimmed world over a dimmed pause screen.
        /// </remarks>
        public void SetBackdrop(Material blurInstance)
        {
            // The helper encodes the blur doc's authoring rules — vertex color stays opaque white, tint
            // lives on the material — so no call site has to remember them. The flat color is this card's
            // own variant tint, not a shared constant.
            RuntimeUIFactory.ApplyBlurBackground(_backdrop, blurInstance, _flatBackdrop);
        }

        /// <summary>Builds the square icon slot. Deactivated — and so skipped by layout — when unused.</summary>
        private void BuildIcon()
        {
            _iconObject = new GameObject("Icon", typeof(RectTransform));
            _iconObject.transform.SetParent(transform, false);

            _iconImage = _iconObject.AddComponent<Image>();
            _iconImage.preserveAspect = true;

            LayoutElement iconLayout = _iconObject.AddComponent<LayoutElement>();
            iconLayout.preferredWidth = ICON_SIZE;
            iconLayout.preferredHeight = ICON_SIZE;
            iconLayout.flexibleWidth = 0f;

            BuildGlyphSlot();
        }

        /// <summary>
        /// Builds the text alternative to the icon sprite, occupying the same slot.
        /// </summary>
        /// <remarks>
        /// A sibling rather than a child of the sprite slot because exactly one of the two is ever active,
        /// and an inactive object is skipped by the layout group — which is what collapses the slot when a
        /// card has neither. The project font's atlas is static, so a glyph that is not in it renders as a
        /// blank box rather than falling back to another font.
        /// </remarks>
        private void BuildGlyphSlot()
        {
            _glyphObject = new GameObject("Glyph", typeof(RectTransform));
            _glyphObject.transform.SetParent(transform, false);

            _glyphText = _glyphObject.AddComponent<TextMeshProUGUI>();
            _glyphText.fontSize = ICON_SIZE * GLYPH_FONT_SCALE;
            _glyphText.alignment = TextAlignmentOptions.Center;
            _glyphText.richText = false;
            _glyphText.textWrappingMode = TextWrappingModes.NoWrap;
            _glyphText.overflowMode = TextOverflowModes.Overflow;

            LayoutElement glyphLayout = _glyphObject.AddComponent<LayoutElement>();
            glyphLayout.preferredWidth = ICON_SIZE;
            glyphLayout.preferredHeight = ICON_SIZE;
            glyphLayout.flexibleWidth = 0f;
        }

        /// <summary>Builds the title/subtitle column that takes whatever width the icon slot leaves.</summary>
        /// <param name="style">Supplies the title color a freshly built card starts at.</param>
        private void BuildTextColumn(in ToastStyle style)
        {
            GameObject column = new GameObject("Text", typeof(RectTransform));
            column.transform.SetParent(transform, false);

            VerticalLayoutGroup columnLayout = column.AddComponent<VerticalLayoutGroup>();
            columnLayout.spacing = TEXT_SPACING;
            columnLayout.childAlignment = TextAnchor.MiddleLeft;
            columnLayout.childControlWidth = true;
            columnLayout.childControlHeight = true;
            columnLayout.childForceExpandWidth = true;
            columnLayout.childForceExpandHeight = false;

            // Flexible rather than a computed width: the column takes whatever the icon slot leaves, so a
            // collapsed icon widens the text instead of leaving a hole.
            LayoutElement columnElement = column.AddComponent<LayoutElement>();
            columnElement.flexibleWidth = 1f;

            _titleText = CreateLabel("Title", column.transform, TITLE_FONT_SIZE, style.Accent,
                FontStyles.Bold);
            _subtitleText = CreateLabel("Subtitle", column.transform, SUBTITLE_FONT_SIZE, s_subtitleColor,
                FontStyles.Normal);
        }

        /// <summary>Creates one wrapped TMP label inside the text column.</summary>
        /// <param name="name">Name for the created GameObject.</param>
        /// <param name="parent">Transform to parent under.</param>
        /// <param name="fontSize">Font size in reference pixels.</param>
        /// <param name="color">Text color.</param>
        /// <param name="style">Font style.</param>
        /// <returns>The created text component.</returns>
        /// <remarks>
        /// Built through <see cref="RuntimeUIFactory.CreateTMPText"/> rather than by hand, so a future
        /// factory-wide text change — a default font asset, a different wrapping mode — reaches the toast
        /// cards along with the console and the benchmark UI. The factory already wraps rather than
        /// truncates, which is what lets a wrapped title make the card taller and move every card below it
        /// by exactly that much. Only the weight and the height-from-content are added here.
        /// </remarks>
        private static TextMeshProUGUI CreateLabel(string name, Transform parent, float fontSize, Color color,
            FontStyles style)
        {
            GameObject obj = RuntimeUIFactory.CreateTMPText(name, parent, fontSize,
                TextAlignmentOptions.MidlineLeft, color);

            TextMeshProUGUI text = obj.GetComponent<TextMeshProUGUI>();
            text.fontStyle = style;

            ContentSizeFitter fitter = obj.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            return text;
        }

        /// <summary>
        /// Fills this card from a request and starts its lifetime.
        /// </summary>
        /// <param name="request">What the card shows.</param>
        /// <param name="dwellSeconds">Seconds to hold before the exit animation.</param>
        /// <param name="onFinished">Invoked once the card has finished its exit and is free to reuse.</param>
        public void Show(in ToastRequest request, float dwellSeconds, in ToastStyle style,
            Material blurInstance, Action<ToastCard> onFinished)
        {
            _onFinished = onFinished;

            // Every variant-dependent property is rewritten here, not just the ones this request sets: a
            // pooled card last shown as an Error would otherwise come back red under a neutral title.
            Variant = request.Variant;
            _flatBackdrop = style.FlatBackdrop;
            _titleText.color = style.Accent;
            _glyphText.color = style.Accent;
            SetBackdrop(blurInstance);

            _titleText.text = request.Title;

            bool hasSubtitle = !string.IsNullOrWhiteSpace(request.Subtitle);
            _subtitleText.text = hasSubtitle ? request.Subtitle : string.Empty;
            _subtitleText.gameObject.SetActive(hasSubtitle);

            ApplyIcon(in request, in style);

            // Back to content-driven height: a pooled card still carries the zeroed height its last exit
            // drove, and would otherwise re-enter the stack collapsed.
            _layout.preferredHeight = -1f;
            _layout.minHeight = -1f;

            _group.alpha = 0f;
            gameObject.SetActive(true);
            transform.SetAsLastSibling();

            // Sized before the first frame is drawn, so the card never flashes at its previous height.
            LayoutRebuilder.ForceRebuildLayoutImmediate(_rect);

            if (_lifetime != null) StopCoroutine(_lifetime);
            _lifetime = StartCoroutine(LifetimeRoutine(dwellSeconds));
        }

        /// <summary>
        /// Fills the icon slot: an explicit sprite wins, then an explicit glyph, then the variant's glyph,
        /// then the slot collapses.
        /// </summary>
        /// <param name="request">The request being shown.</param>
        /// <param name="style">The variant's style, supplying the fallback glyph.</param>
        /// <remarks>
        /// Sprite first because it is the more specific medium — a consumer that authored art meant it —
        /// and the glyph exists so a variant can carry a recognizable mark without anyone authoring one.
        /// </remarks>
        private void ApplyIcon(in ToastRequest request, in ToastStyle style)
        {
            bool hasSprite = request.Icon != null;
            _iconImage.sprite = request.Icon;
            _iconObject.SetActive(hasSprite);

            string glyph = !string.IsNullOrEmpty(request.Glyph) ? request.Glyph : style.Glyph;
            bool hasGlyph = !hasSprite && !string.IsNullOrEmpty(glyph);

            _glyphText.text = hasGlyph ? glyph : string.Empty;
            _glyphObject.SetActive(hasGlyph);
        }

        /// <summary>Fades in, holds for the dwell, then shrinks and fades out.</summary>
        /// <param name="dwellSeconds">Seconds to hold at full opacity.</param>
        /// <remarks>
        /// Unscaled throughout, matching the tooltip auto-hide and the music scheduler timing: a toast must
        /// dismiss itself whatever the timescale is doing.
        /// </remarks>
        private IEnumerator LifetimeRoutine(float dwellSeconds)
        {
            for (float elapsed = 0f; elapsed < ENTER_SECONDS; elapsed += Time.unscaledDeltaTime)
            {
                _group.alpha = Mathf.Clamp01(elapsed / ENTER_SECONDS);
                yield return null;
            }

            _group.alpha = 1f;
            yield return new WaitForSecondsRealtime(dwellSeconds);

            // Both heights are driven, not just the preferred one: the card own layout group reports a
            // minimum from its content, and the parent would honor that floor and stop the collapse short.
            float startHeight = _rect.rect.height;
            for (float elapsed = 0f; elapsed < EXIT_SECONDS; elapsed += Time.unscaledDeltaTime)
            {
                float t = Mathf.Clamp01(elapsed / EXIT_SECONDS);
                float height = Mathf.Lerp(startHeight, 0f, t);

                _layout.preferredHeight = height;
                _layout.minHeight = height;
                _group.alpha = 1f - t;
                yield return null;
            }

            _lifetime = null;
            Retire();
        }

        /// <summary>Hides the card and hands it back to the manager free-list.</summary>
        private void Retire()
        {
            _group.alpha = 0f;
            gameObject.SetActive(false);

            Action<ToastCard> finished = _onFinished;
            _onFinished = null;
            finished?.Invoke(this);
        }
    }
}
