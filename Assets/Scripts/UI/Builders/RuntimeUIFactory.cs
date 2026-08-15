using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace UI.Builders
{
    /// <summary>
    /// Shared primitives for UI hierarchies built in code rather than in a scene or prefab.
    /// <para>
    /// Deliberately holds no palette: every color is a parameter, so each screen keeps its own look and
    /// a restyle cannot silently propagate across unrelated surfaces (RUNTIME_UI_FACTORY.md §3). What
    /// the factory does own is the blur-material contract — see
    /// <see cref="CreateBlurMaterialInstance(Color, Color?)"/> and
    /// <see cref="ApplyBlurBackground"/>, which encode UI_BLUR_BACKDROP_SYSTEM.md §4's authoring rules
    /// so no call site has to remember them.
    /// </para>
    /// </summary>
    public static class RuntimeUIFactory
    {
        /// <summary>Canvas-scaler reference resolution every runtime-built canvas is authored against.</summary>
        public static readonly Vector2 ReferenceResolution = new Vector2(1920f, 1080f);

        /// <summary>Name of the UI blur consumer shader, listed in Always Included Shaders so player builds resolve it.</summary>
        private const string BLUR_SHADER_NAME = "Custom/MaskedUIBlur";

        private static readonly int s_multiplyColorId = Shader.PropertyToID("_MultiplyColor");
        private static readonly int s_additiveColorId = Shader.PropertyToID("_AdditiveColor");

        #region Canvas

        /// <summary>Creates a new screen-space overlay canvas GameObject with a scaler and raycaster.</summary>
        /// <param name="name">Name for the created GameObject.</param>
        /// <param name="sortingOrder">Canvas sorting order (the scene UI canvas sits at 0).</param>
        /// <param name="matchWidthOrHeight">Scaler width/height match blend.</param>
        /// <returns>The created canvas GameObject.</returns>
        public static GameObject CreateCanvas(string name, int sortingOrder, float matchWidthOrHeight = 0.5f)
        {
            GameObject obj = new GameObject(name);
            ConfigureCanvas(obj, sortingOrder, matchWidthOrHeight);
            return obj;
        }

        /// <summary>
        /// Adds the overlay canvas components to an existing GameObject, for a caller that hosts its
        /// canvas on an object it already owns rather than on a freshly created one.
        /// </summary>
        /// <param name="target">The GameObject to turn into an overlay canvas.</param>
        /// <param name="sortingOrder">Canvas sorting order (the scene UI canvas sits at 0).</param>
        /// <param name="matchWidthOrHeight">Scaler width/height match blend.</param>
        /// <returns>The added <see cref="Canvas"/>.</returns>
        public static Canvas ConfigureCanvas(GameObject target, int sortingOrder, float matchWidthOrHeight = 0.5f)
        {
            Canvas canvas = target.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;

            CanvasScaler scaler = target.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = ReferenceResolution;
            scaler.matchWidthOrHeight = matchWidthOrHeight;

            target.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        #endregion

        #region Blur backdrop

        /// <summary>
        /// Creates a tinted instance of the UI blur material by locating the shader itself, for callers
        /// that hold no material reference. The caller owns the returned instance and must destroy it.
        /// </summary>
        /// <param name="multiplyTint">Value for the material's <c>_MultiplyColor</c>.</param>
        /// <param name="additiveTint">Value for <c>_AdditiveColor</c>, or null to keep the shader default.</param>
        /// <returns>The material instance, or null when the shader could not be found.</returns>
        public static Material CreateBlurMaterialInstance(Color multiplyTint, Color? additiveTint = null)
        {
            Shader shader = Shader.Find(BLUR_SHADER_NAME);
            if (shader == null)
                return null;

            return Tint(new Material(shader), multiplyTint, additiveTint);
        }

        /// <summary>
        /// Creates a tinted instance of an existing UI blur material. The caller owns the returned
        /// instance and must destroy it.
        /// </summary>
        /// <param name="source">The material to instance from.</param>
        /// <param name="multiplyTint">Value for the material's <c>_MultiplyColor</c>.</param>
        /// <param name="additiveTint">Value for <c>_AdditiveColor</c>, or null to keep the source's value.</param>
        /// <returns>The material instance, or null when <paramref name="source"/> is null.</returns>
        public static Material CreateBlurMaterialInstance(Material source, Color multiplyTint,
            Color? additiveTint = null)
        {
            if (source == null)
                return null;

            return Tint(new Material(source), multiplyTint, additiveTint);
        }

        /// <summary>
        /// Points an <see cref="Image"/> at a blur material instance, or paints a flat color when none is
        /// available. Takes an already-created instance rather than creating one, so a caller whose build
        /// method can re-run stays free to allocate exactly once outside it.
        /// </summary>
        /// <param name="image">The backdrop graphic to configure.</param>
        /// <param name="blurInstance">The blur material instance, or null to force the fallback.</param>
        /// <param name="fallbackColor">Flat color painted when no blur material is available.</param>
        /// <returns>True when the blur material was applied; false when the flat fallback was used.</returns>
        public static bool ApplyBlurBackground(Image image, Material blurInstance, Color fallbackColor)
        {
            if (blurInstance == null)
            {
                image.color = fallbackColor;
                return false;
            }

            image.material = blurInstance;

            // Vertex color multiplies the composite, so anything but opaque white either tints the panel
            // or bleeds the un-blurred screen through it (blur doc §4.1, §4.5). Tint belongs on the
            // material instead, which is what the caller already set.
            image.color = Color.white;
            return true;
        }

        /// <summary>Applies the tint properties to a freshly created blur material instance.</summary>
        /// <param name="instance">The material to tint.</param>
        /// <param name="multiplyTint">Value for <c>_MultiplyColor</c>.</param>
        /// <param name="additiveTint">Value for <c>_AdditiveColor</c>, or null to leave it untouched.</param>
        /// <returns>The same instance, tinted.</returns>
        private static Material Tint(Material instance, Color multiplyTint, Color? additiveTint)
        {
            instance.SetColor(s_multiplyColorId, multiplyTint);
            if (additiveTint.HasValue)
                instance.SetColor(s_additiveColorId, additiveTint.Value);
            return instance;
        }

        #endregion

        #region Primitives

        /// <summary>Creates a bare <see cref="RectTransform"/> GameObject to hang layout or graphics on.</summary>
        /// <param name="name">Name for the created GameObject.</param>
        /// <param name="parent">Transform to parent under.</param>
        /// <returns>The created GameObject.</returns>
        public static GameObject CreatePanel(string name, Transform parent)
        {
            GameObject obj = new GameObject(name, typeof(RectTransform));
            obj.transform.SetParent(parent, false);
            return obj;
        }

        /// <summary>Stretches a <see cref="RectTransform"/> to fill its parent with no inset.</summary>
        /// <param name="rect">The rect to stretch.</param>
        public static void StretchToParent(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>Creates a wrapped, rich-text TMP label.</summary>
        /// <param name="name">Name for the created GameObject.</param>
        /// <param name="parent">Transform to parent under.</param>
        /// <param name="fontSize">Font size in reference pixels.</param>
        /// <param name="alignment">Text alignment.</param>
        /// <param name="color">Text color.</param>
        /// <returns>The created GameObject, carrying the <see cref="TextMeshProUGUI"/>.</returns>
        public static GameObject CreateTMPText(string name, Transform parent, float fontSize,
            TextAlignmentOptions alignment, Color color)
        {
            GameObject obj = new GameObject(name, typeof(RectTransform));
            obj.transform.SetParent(parent, false);

            TextMeshProUGUI text = obj.AddComponent<TextMeshProUGUI>();
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = color;
            text.richText = true;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;

            return obj;
        }

        /// <summary>
        /// Creates a vertically scrollable TMP text area with a masked viewport, an auto-hiding
        /// scrollbar, and a content container sized by the text's preferred height.
        /// </summary>
        /// <param name="name">Name for the created root GameObject.</param>
        /// <param name="parent">Transform to parent under.</param>
        /// <param name="fontSize">Font size for the contained text.</param>
        /// <param name="chrome">Colors for the area's background, scrollbar track, and handle.</param>
        /// <param name="textColor">Color of the contained text.</param>
        /// <param name="text">The created text component.</param>
        /// <returns>The scroll area's root GameObject.</returns>
        public static GameObject CreateScrollableTextArea(string name, Transform parent, float fontSize,
            ScrollAreaChrome chrome, Color textColor, out TextMeshProUGUI text)
        {
            const float scrollbarWidth = 12f;

            GameObject scrollObj = new GameObject(name, typeof(RectTransform));
            scrollObj.transform.SetParent(parent, false);
            StretchToParent((RectTransform)scrollObj.transform);

            Image scrollBg = scrollObj.AddComponent<Image>();
            scrollBg.color = chrome.Background;

            ScrollRect scroll = scrollObj.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;

            // Viewport with mask — leaves room on the right for the scrollbar
            GameObject viewport = CreatePanel("Viewport", scrollObj.transform);
            RectTransform viewRect = viewport.GetComponent<RectTransform>();
            viewRect.anchorMin = Vector2.zero;
            viewRect.anchorMax = Vector2.one;
            viewRect.offsetMin = new Vector2(10, 10);
            viewRect.offsetMax = new Vector2(-10 - scrollbarWidth, -10);

            viewport.AddComponent<RectMask2D>();
            scroll.viewport = viewRect;

            // Vertical scrollbar
            GameObject scrollbarObj = new GameObject("Scrollbar", typeof(RectTransform));
            scrollbarObj.transform.SetParent(scrollObj.transform, false);
            RectTransform scrollbarRect = scrollbarObj.GetComponent<RectTransform>();
            scrollbarRect.anchorMin = new Vector2(1, 0);
            scrollbarRect.anchorMax = Vector2.one;
            scrollbarRect.offsetMin = new Vector2(-scrollbarWidth, 10);
            scrollbarRect.offsetMax = new Vector2(0, -10);

            Image scrollbarBg = scrollbarObj.AddComponent<Image>();
            scrollbarBg.color = chrome.ScrollbarTrack;

            GameObject handleArea = CreatePanel("HandleArea", scrollbarObj.transform);
            StretchToParent(handleArea.GetComponent<RectTransform>());

            GameObject handle = CreatePanel("Handle", handleArea.transform);
            RectTransform handleRect = handle.GetComponent<RectTransform>();
            StretchToParent(handleRect);

            Image handleImage = handle.AddComponent<Image>();
            handleImage.color = chrome.ScrollbarHandle;

            Scrollbar scrollbar = scrollbarObj.AddComponent<Scrollbar>();
            scrollbar.handleRect = handleRect;
            scrollbar.targetGraphic = handleImage;
            scrollbar.direction = Scrollbar.Direction.BottomToTop;

            scroll.verticalScrollbar = scrollbar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;

            // Content container — uses VerticalLayoutGroup so TMP drives height via preferred size
            GameObject content = CreatePanel("Content", viewport.transform);
            RectTransform contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(0.5f, 1);
            contentRect.sizeDelta = Vector2.zero;

            VerticalLayoutGroup contentLayout = content.AddComponent<VerticalLayoutGroup>();
            contentLayout.childForceExpandWidth = true;
            contentLayout.childForceExpandHeight = false;
            contentLayout.childControlHeight = true;
            contentLayout.childControlWidth = true;

            ContentSizeFitter fitter = content.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.content = contentRect;

            GameObject textObj = CreateTMPText("ReportText", content.transform, fontSize,
                TextAlignmentOptions.TopLeft, textColor);
            text = textObj.GetComponent<TextMeshProUGUI>();

            return scrollObj;
        }

        /// <summary>Creates a labeled button with an explicit interaction color set.</summary>
        /// <param name="label">Button text, also used to name the GameObject.</param>
        /// <param name="parent">Transform to parent under.</param>
        /// <param name="width">Preferred layout width.</param>
        /// <param name="height">Preferred layout height.</param>
        /// <param name="colors">Normal/highlighted/pressed colors for the button and its graphic.</param>
        /// <param name="labelColor">Color of the button's label.</param>
        /// <param name="labelFontSize">Font size of the button's label.</param>
        /// <returns>The created <see cref="Button"/>.</returns>
        public static Button CreateButton(string label, Transform parent, float width, float height,
            ButtonColors colors, Color labelColor, float labelFontSize)
        {
            GameObject btnObj = new GameObject($"Button_{label.Replace(" ", "")}", typeof(RectTransform));
            btnObj.transform.SetParent(parent, false);

            Image btnImage = btnObj.AddComponent<Image>();
            btnImage.color = colors.Normal;

            Button button = btnObj.AddComponent<Button>();
            ColorBlock block = button.colors;
            block.normalColor = colors.Normal;
            block.highlightedColor = colors.Highlighted;
            block.pressedColor = colors.Pressed;
            block.selectedColor = colors.Normal;
            button.colors = block;
            button.targetGraphic = btnImage;

            LayoutElement layoutElement = btnObj.AddComponent<LayoutElement>();
            layoutElement.preferredWidth = width;
            layoutElement.preferredHeight = height;

            GameObject textObj = CreateTMPText("Label", btnObj.transform, labelFontSize,
                TextAlignmentOptions.Center, labelColor);
            StretchToParent(textObj.GetComponent<RectTransform>());
            textObj.GetComponent<TextMeshProUGUI>().text = label;

            return button;
        }

        #endregion

        #region Color groups

        /// <summary>Colors for a scroll area's non-text chrome.</summary>
        public readonly struct ScrollAreaChrome
        {
            /// <summary>Backdrop behind the scrolling content.</summary>
            public readonly Color Background;

            /// <summary>The scrollbar's track.</summary>
            public readonly Color ScrollbarTrack;

            /// <summary>The scrollbar's draggable handle.</summary>
            public readonly Color ScrollbarHandle;

            /// <summary>Creates a chrome color set.</summary>
            /// <param name="background">Backdrop behind the scrolling content.</param>
            /// <param name="scrollbarTrack">The scrollbar's track.</param>
            /// <param name="scrollbarHandle">The scrollbar's draggable handle.</param>
            public ScrollAreaChrome(Color background, Color scrollbarTrack, Color scrollbarHandle)
            {
                Background = background;
                ScrollbarTrack = scrollbarTrack;
                ScrollbarHandle = scrollbarHandle;
            }
        }

        /// <summary>The three interaction colors a button needs.</summary>
        public readonly struct ButtonColors
        {
            /// <summary>Resting color.</summary>
            public readonly Color Normal;

            /// <summary>Color while hovered or selected via navigation.</summary>
            public readonly Color Highlighted;

            /// <summary>Color while held down.</summary>
            public readonly Color Pressed;

            /// <summary>Creates a button color set.</summary>
            /// <param name="normal">Resting color.</param>
            /// <param name="highlighted">Color while hovered.</param>
            /// <param name="pressed">Color while held down.</param>
            public ButtonColors(Color normal, Color highlighted, Color pressed)
            {
                Normal = normal;
                Highlighted = highlighted;
                Pressed = pressed;
            }
        }

        #endregion
    }
}
