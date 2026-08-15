using TMPro;
using UI.Builders;
using UnityEngine;
using UnityEngine.UI;

namespace Benchmarks
{
    /// <summary>
    /// Composes the benchmark HUD and results screen at runtime from <see cref="RuntimeUIFactory"/>
    /// primitives, so no scene or prefab edit is involved. This class owns the benchmark's palette and
    /// layout; the factory owns construction and the blur-material contract.
    /// </summary>
    public static class BenchmarkUIBuilder
    {
        // The scene UI canvas sits at sortingOrder 0, and a blurred panel is opaque — it replaces the
        // pixels beneath it rather than compositing over them (UI_BLUR_BACKDROP_SYSTEM.md §4.2). Sorting
        // the HUD *below* the scene canvas is what lets the pause menu cover it (UI_BUGS #06); at a
        // positive order the HUD punched a hole back to the un-blurred world over the paused screen.
        private const int HUD_SORT_ORDER = -10;
        private const int RESULTS_SORT_ORDER = 200;

        private const float BUTTON_HEIGHT = 50f;
        private const float LABEL_FONT_SIZE = 16f;

        private static readonly Color s_hudBackgroundColor = new Color(0f, 0f, 0f, 0.7f);
        private static readonly Color s_resultsOverlayColor = new Color(0f, 0f, 0f, 0.85f);
        private static readonly Color s_resultsPanelColor = new Color(0.15f, 0.15f, 0.15f, 0.95f);

        // Blur tints. Material properties, not vertex colors — Unity gamma-converts these on upload
        // while leaving Image.color alone, so the two knobs are not interchangeable (blur doc §4.3).
        private static readonly Color s_hudBlurTint = new Color(0.7f, 0.7f, 0.7f, 1f);
        private static readonly Color s_resultsBlurTint = new Color(0.15f, 0.15f, 0.15f, 1f);

        // Button colors matched to the project's Button.prefab style
        private static readonly Color s_buttonNormalColor = new Color(0.55f, 0.55f, 0.55f, 1f);
        private static readonly Color s_buttonHighlightColor = new Color(0.65f, 0.65f, 0.65f, 1f);
        private static readonly Color s_buttonPressedColor = new Color(0.4f, 0.4f, 0.4f, 1f);
        private static readonly Color s_buttonTextColor = Color.white;

        private static readonly RuntimeUIFactory.ButtonColors s_buttonColors =
            new RuntimeUIFactory.ButtonColors(s_buttonNormalColor, s_buttonHighlightColor, s_buttonPressedColor);

        private static readonly RuntimeUIFactory.ScrollAreaChrome s_reportChrome =
            new RuntimeUIFactory.ScrollAreaChrome(
                new Color(0.1f, 0.1f, 0.1f, 0.8f),
                new Color(0.15f, 0.15f, 0.15f, 0.8f),
                new Color(0.5f, 0.5f, 0.5f, 0.8f));

        /// <summary>
        /// Creates the runtime HUD overlay showing benchmark progress and live metrics.
        /// </summary>
        /// <param name="controller">The benchmark controller to wire into the HUD.</param>
        /// <param name="blurMaterial">Optional UI blur material for the HUD background. Pass null for a flat panel.</param>
        /// <returns>The configured <see cref="BenchmarkHUD"/> component.</returns>
        public static BenchmarkHUD CreateHUD(BenchmarkController controller, Material blurMaterial = null)
        {
            GameObject canvasObj = RuntimeUIFactory.CreateCanvas("BenchmarkHUD_Canvas", HUD_SORT_ORDER);

            // Panel anchored to top-center
            GameObject panel = RuntimeUIFactory.CreatePanel("HUD_Panel", canvasObj.transform);
            RectTransform panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.2f, 0.85f);
            panelRect.anchorMax = new Vector2(0.8f, 1f);
            panelRect.offsetMin = Vector2.zero;
            panelRect.offsetMax = Vector2.zero;

            Image panelImage = panel.AddComponent<Image>();
            RuntimeUIFactory.ApplyBlurBackground(panelImage,
                RuntimeUIFactory.CreateBlurMaterialInstance(blurMaterial, s_hudBlurTint),
                s_hudBackgroundColor);

            // Add padding via VerticalLayoutGroup
            VerticalLayoutGroup layout = panel.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(16, 16, 8, 8);
            layout.childAlignment = TextAnchor.MiddleCenter;

            GameObject textObj = RuntimeUIFactory.CreateTMPText("HUD_StatusText", panel.transform, 16,
                TextAlignmentOptions.Center, Color.white);
            TextMeshProUGUI statusText = textObj.GetComponent<TextMeshProUGUI>();

            BenchmarkHUD hud = canvasObj.AddComponent<BenchmarkHUD>();
            hud.Initialize(controller, statusText);

            return hud;
        }

        /// <summary>
        /// Creates the post-run results screen with a scrollable report and action buttons. Shared by both automated
        /// runners. The root GameObject starts inactive.
        /// </summary>
        /// <param name="title">Heading shown above the report (e.g. "Benchmark Complete").</param>
        /// <param name="blurMaterial">Optional UI blur material for the background overlay. Pass null for a flat overlay.</param>
        /// <returns>The configured <see cref="BenchmarkResultsScreen"/> component.</returns>
        public static BenchmarkResultsScreen CreateResultsScreen(string title = "Benchmark Complete",
            Material blurMaterial = null)
        {
            GameObject canvasObj = RuntimeUIFactory.CreateCanvas("BenchmarkResults_Canvas", RESULTS_SORT_ORDER);

            // Full-screen dark overlay
            GameObject overlay = RuntimeUIFactory.CreatePanel("Results_Overlay", canvasObj.transform);
            RuntimeUIFactory.StretchToParent(overlay.GetComponent<RectTransform>());

            Image overlayImage = overlay.AddComponent<Image>();
            RuntimeUIFactory.ApplyBlurBackground(overlayImage,
                RuntimeUIFactory.CreateBlurMaterialInstance(blurMaterial, s_resultsBlurTint),
                s_resultsOverlayColor);

            // Centered content panel
            GameObject contentPanel = RuntimeUIFactory.CreatePanel("Results_ContentPanel", overlay.transform);
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
            GameObject titleObj = RuntimeUIFactory.CreateTMPText("Results_Title", contentPanel.transform, 24,
                TextAlignmentOptions.Center, Color.white);
            TextMeshProUGUI titleText = titleObj.GetComponent<TextMeshProUGUI>();
            titleText.text = title;
            titleText.fontStyle = FontStyles.Bold;
            LayoutElement titleLayout = titleObj.AddComponent<LayoutElement>();
            titleLayout.preferredHeight = 40;
            titleLayout.flexibleHeight = 0;

            // Scrollable report text area
            GameObject scrollArea = RuntimeUIFactory.CreateScrollableTextArea("Results_ReportScroll",
                contentPanel.transform, 14, s_reportChrome, Color.white, out TextMeshProUGUI reportText);
            LayoutElement scrollLayout = scrollArea.AddComponent<LayoutElement>();
            scrollLayout.flexibleHeight = 1;

            // Button row
            GameObject buttonRow = RuntimeUIFactory.CreatePanel("Results_ButtonRow", contentPanel.transform);
            HorizontalLayoutGroup buttonLayout = buttonRow.AddComponent<HorizontalLayoutGroup>();
            buttonLayout.spacing = 20;
            buttonLayout.childAlignment = TextAnchor.MiddleCenter;
            buttonLayout.childForceExpandWidth = false;
            buttonLayout.childForceExpandHeight = true;
            LayoutElement buttonRowLayout = buttonRow.AddComponent<LayoutElement>();
            buttonRowLayout.preferredHeight = 50;
            buttonRowLayout.flexibleHeight = 0;

            Button openFolderBtn = RuntimeUIFactory.CreateButton("Open Log Folder", buttonRow.transform, 200,
                BUTTON_HEIGHT, s_buttonColors, s_buttonTextColor, LABEL_FONT_SIZE);
            Button returnBtn = RuntimeUIFactory.CreateButton("Return to Main Menu", buttonRow.transform, 250,
                BUTTON_HEIGHT, s_buttonColors, s_buttonTextColor, LABEL_FONT_SIZE);

            BenchmarkResultsScreen screen = canvasObj.AddComponent<BenchmarkResultsScreen>();
            screen.Initialize(reportText, openFolderBtn, returnBtn);

            canvasObj.SetActive(false);
            return screen;
        }
    }
}
