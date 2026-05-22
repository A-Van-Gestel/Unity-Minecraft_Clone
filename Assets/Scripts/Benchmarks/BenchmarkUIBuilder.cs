using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Benchmarks
{
    /// <summary>
    /// Static utility that programmatically creates the benchmark HUD and results screen
    /// UI hierarchies at runtime. Avoids the need to edit scene or prefab files directly.
    /// All GameObjects are created under a dedicated screen-space overlay Canvas with
    /// <see cref="CanvasScaler"/> configured for 1920×1080 reference resolution.
    /// </summary>
    public static class BenchmarkUIBuilder
    {
        private static readonly Color s_hudBackgroundColor = new Color(0f, 0f, 0f, 0.7f);
        private static readonly Color s_resultsOverlayColor = new Color(0f, 0f, 0f, 0.85f);
        private static readonly Color s_resultsPanelColor = new Color(0.15f, 0.15f, 0.15f, 0.95f);

        // Button colors matched to the project's Button.prefab style
        private static readonly Color s_buttonNormalColor = new Color(0.55f, 0.55f, 0.55f, 1f);
        private static readonly Color s_buttonHighlightColor = new Color(0.65f, 0.65f, 0.65f, 1f);
        private static readonly Color s_buttonPressedColor = new Color(0.4f, 0.4f, 0.4f, 1f);
        private static readonly Color s_buttonTextColor = Color.white;

        // Shader property IDs for UIBlur material tinting
        private static readonly int s_multiplyColorId = Shader.PropertyToID("_MultiplyColor");
        // private static readonly int s_additiveColorId = Shader.PropertyToID("_AdditiveColor");

        /// <summary>
        /// Creates the runtime HUD overlay showing benchmark progress and live metrics.
        /// </summary>
        /// <param name="controller">The benchmark controller to wire into the HUD.</param>
        /// <returns>The configured <see cref="BenchmarkHUD"/> component.</returns>
        /// <param name="blurMaterial">Optional UI blur material for the HUD background. Pass null to skip.</param>
        public static BenchmarkHUD CreateHUD(BenchmarkController controller, Material blurMaterial = null)
        {
            // Canvas
            GameObject canvasObj = CreateCanvas("BenchmarkHUD_Canvas", 100);

            // Semi-transparent panel anchored to top-center
            GameObject panel = CreatePanel("HUD_Panel", canvasObj.transform);
            RectTransform panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.2f, 0.85f);
            panelRect.anchorMax = new Vector2(0.8f, 1f);
            panelRect.offsetMin = Vector2.zero;
            panelRect.offsetMax = Vector2.zero;

            Image panelImage = panel.AddComponent<Image>();
            if (blurMaterial != null)
            {
                panelImage.material = new Material(blurMaterial);
                panelImage.material.SetColor(s_multiplyColorId, new Color(0.7f, 0.7f, 0.7f, 1f));
                panelImage.color = Color.white;
            }
            else
            {
                panelImage.color = s_hudBackgroundColor;
            }

            // Add padding via VerticalLayoutGroup
            VerticalLayoutGroup layout = panel.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(16, 16, 8, 8);
            layout.childAlignment = TextAnchor.MiddleCenter;

            // Status text
            GameObject textObj = CreateTMPText("HUD_StatusText", panel.transform, 16, TextAlignmentOptions.Center);
            TextMeshProUGUI statusText = textObj.GetComponent<TextMeshProUGUI>();

            // Attach HUD component
            BenchmarkHUD hud = canvasObj.AddComponent<BenchmarkHUD>();
            hud.Initialize(controller, statusText);

            return hud;
        }

        /// <summary>
        /// Creates the post-benchmark results screen with a scrollable report and action buttons.
        /// The root GameObject starts inactive.
        /// </summary>
        /// <param name="controller">The benchmark controller to wire into the results screen.</param>
        /// <returns>The configured <see cref="BenchmarkResultsScreen"/> component.</returns>
        /// <param name="blurMaterial">Optional UI blur material for the background overlay. Pass null to skip.</param>
        public static BenchmarkResultsScreen CreateResultsScreen(BenchmarkController controller, Material blurMaterial = null)
        {
            // Canvas
            GameObject canvasObj = CreateCanvas("BenchmarkResults_Canvas", 200);

            // Full-screen dark overlay
            GameObject overlay = CreatePanel("Results_Overlay", canvasObj.transform);
            RectTransform overlayRect = overlay.GetComponent<RectTransform>();
            overlayRect.anchorMin = Vector2.zero;
            overlayRect.anchorMax = Vector2.one;
            overlayRect.offsetMin = Vector2.zero;
            overlayRect.offsetMax = Vector2.zero;

            Image overlayImage = overlay.AddComponent<Image>();
            if (blurMaterial != null)
            {
                overlayImage.material = new Material(blurMaterial);
                overlayImage.material.SetColor(s_multiplyColorId, new Color(0.15f, 0.15f, 0.15f, 1f));
                overlayImage.color = Color.white;
            }
            else
            {
                overlayImage.color = s_resultsOverlayColor;
            }

            // Centered content panel
            GameObject contentPanel = CreatePanel("Results_ContentPanel", overlay.transform);
            RectTransform contentRect = contentPanel.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0.05f, 0.05f);
            contentRect.anchorMax = new Vector2(0.95f, 0.95f);
            contentRect.offsetMin = Vector2.zero;
            contentRect.offsetMax = Vector2.zero;

            Image contentImage = contentPanel.AddComponent<Image>();
            contentImage.color = s_resultsPanelColor;

            VerticalLayoutGroup contentLayout = contentPanel.AddComponent<VerticalLayoutGroup>();
            contentLayout.padding = new RectOffset(20, 20, 20, 20);
            contentLayout.spacing = 10;
            contentLayout.childForceExpandWidth = true;
            contentLayout.childForceExpandHeight = false;

            // Title
            GameObject titleObj = CreateTMPText("Results_Title", contentPanel.transform, 24, TextAlignmentOptions.Center);
            TextMeshProUGUI titleText = titleObj.GetComponent<TextMeshProUGUI>();
            titleText.text = "Benchmark Complete";
            titleText.fontStyle = FontStyles.Bold;
            LayoutElement titleLayout = titleObj.AddComponent<LayoutElement>();
            titleLayout.preferredHeight = 40;
            titleLayout.flexibleHeight = 0;

            // Scrollable report text area
            GameObject scrollArea = CreateScrollableTextArea("Results_ReportScroll", contentPanel.transform, out TextMeshProUGUI reportText);
            LayoutElement scrollLayout = scrollArea.AddComponent<LayoutElement>();
            scrollLayout.flexibleHeight = 1;

            // Button row
            GameObject buttonRow = CreatePanel("Results_ButtonRow", contentPanel.transform);
            HorizontalLayoutGroup buttonLayout = buttonRow.AddComponent<HorizontalLayoutGroup>();
            buttonLayout.spacing = 20;
            buttonLayout.childAlignment = TextAnchor.MiddleCenter;
            buttonLayout.childForceExpandWidth = false;
            buttonLayout.childForceExpandHeight = true;
            LayoutElement buttonRowLayout = buttonRow.AddComponent<LayoutElement>();
            buttonRowLayout.preferredHeight = 50;
            buttonRowLayout.flexibleHeight = 0;

            Button openFolderBtn = CreateButton("Open Log Folder", buttonRow.transform, 200);
            Button returnBtn = CreateButton("Return to Main Menu", buttonRow.transform, 250);

            // Attach results screen component
            BenchmarkResultsScreen screen = canvasObj.AddComponent<BenchmarkResultsScreen>();
            screen.Initialize(controller, reportText, openFolderBtn, returnBtn);

            canvasObj.SetActive(false);
            return screen;
        }

        // ── Primitive Builders ───────────────────────────────────────────

        private static GameObject CreateCanvas(string name, int sortingOrder)
        {
            GameObject obj = new GameObject(name);
            Canvas canvas = obj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;

            CanvasScaler scaler = obj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            obj.AddComponent<GraphicRaycaster>();
            return obj;
        }

        private static GameObject CreatePanel(string name, Transform parent)
        {
            GameObject obj = new GameObject(name, typeof(RectTransform));
            obj.transform.SetParent(parent, false);
            return obj;
        }

        private static GameObject CreateTMPText(string name, Transform parent, int fontSize,
            TextAlignmentOptions alignment)
        {
            GameObject obj = new GameObject(name, typeof(RectTransform));
            obj.transform.SetParent(parent, false);

            TextMeshProUGUI text = obj.AddComponent<TextMeshProUGUI>();
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = Color.white;
            text.richText = true;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;

            return obj;
        }

        private static GameObject CreateScrollableTextArea(string name, Transform parent,
            out TextMeshProUGUI reportText)
        {
            // ScrollRect container
            GameObject scrollObj = new GameObject(name, typeof(RectTransform));
            scrollObj.transform.SetParent(parent, false);

            RectTransform scrollRect = scrollObj.GetComponent<RectTransform>();
            scrollRect.anchorMin = Vector2.zero;
            scrollRect.anchorMax = Vector2.one;
            scrollRect.offsetMin = Vector2.zero;
            scrollRect.offsetMax = Vector2.zero;

            Image scrollBg = scrollObj.AddComponent<Image>();
            scrollBg.color = new Color(0.1f, 0.1f, 0.1f, 0.8f);

            ScrollRect scroll = scrollObj.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;

            const float scrollbarWidth = 12f;

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
            scrollbarBg.color = new Color(0.15f, 0.15f, 0.15f, 0.8f);

            // Scrollbar handle
            GameObject handleArea = CreatePanel("HandleArea", scrollbarObj.transform);
            RectTransform handleAreaRect = handleArea.GetComponent<RectTransform>();
            handleAreaRect.anchorMin = Vector2.zero;
            handleAreaRect.anchorMax = Vector2.one;
            handleAreaRect.offsetMin = Vector2.zero;
            handleAreaRect.offsetMax = Vector2.zero;

            GameObject handle = CreatePanel("Handle", handleArea.transform);
            RectTransform handleRect = handle.GetComponent<RectTransform>();
            handleRect.anchorMin = Vector2.zero;
            handleRect.anchorMax = Vector2.one;
            handleRect.offsetMin = Vector2.zero;
            handleRect.offsetMax = Vector2.zero;

            Image handleImage = handle.AddComponent<Image>();
            handleImage.color = new Color(0.5f, 0.5f, 0.5f, 0.8f);

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

            // TMP text — LayoutElement drives height via TMP's preferred height calculation
            GameObject textObj = CreateTMPText("ReportText", content.transform, 14, TextAlignmentOptions.TopLeft);
            reportText = textObj.GetComponent<TextMeshProUGUI>();

            return scrollObj;
        }

        private static Button CreateButton(string label, Transform parent, float width)
        {
            GameObject btnObj = new GameObject($"Button_{label.Replace(" ", "")}", typeof(RectTransform));
            btnObj.transform.SetParent(parent, false);

            Image btnImage = btnObj.AddComponent<Image>();
            btnImage.color = s_buttonNormalColor;

            Button button = btnObj.AddComponent<Button>();
            ColorBlock colors = button.colors;
            colors.normalColor = s_buttonNormalColor;
            colors.highlightedColor = s_buttonHighlightColor;
            colors.pressedColor = s_buttonPressedColor;
            colors.selectedColor = s_buttonNormalColor;
            button.colors = colors;
            button.targetGraphic = btnImage;

            LayoutElement layoutElement = btnObj.AddComponent<LayoutElement>();
            layoutElement.preferredWidth = width;
            layoutElement.preferredHeight = 50;

            // Button label
            GameObject textObj = CreateTMPText("Label", btnObj.transform, 16, TextAlignmentOptions.Center);
            RectTransform textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            TextMeshProUGUI text = textObj.GetComponent<TextMeshProUGUI>();
            text.text = label;
            text.color = s_buttonTextColor;

            return button;
        }
    }
}
