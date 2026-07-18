using System.Collections;
using System.Text;
using Commands;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace UI
{
    /// <summary>
    /// The in-game command console view (CMD-1): a bottom-left translucent panel with a scrollable
    /// TMP history over a <see cref="TMP_InputField"/>, driving a <see cref="CommandEngine"/>.
    /// <para>
    /// A stateless view over the engine — all output/history/confirmation state lives in
    /// <see cref="Engine"/>. The whole hierarchy (own overlay canvas included) is built in code at
    /// runtime (TouchControls precedent), so no scene or prefab edits are involved. Spawned and
    /// owned by <see cref="WorldUIManager"/>, which also owns the InUI/cursor/action-map policy.
    /// </para>
    /// </summary>
    public class ConsoleUI : MonoBehaviour
    {
        // --- Layout (canvas-scaled reference pixels, 1920×1080) ---
        private const float REFERENCE_WIDTH = 1920f;
        private const float REFERENCE_HEIGHT = 1080f;
        private const int SORT_ORDER = 100; // above the HUD canvas
        private const float PANEL_WIDTH = 680f;
        private const float PANEL_HEIGHT = 440f;
        private const float PANEL_MARGIN = 12f;
        private const float INPUT_HEIGHT = 40f;
        private const float INNER_PADDING = 8f;
        private const float FONT_SIZE = 20f;

        // --- Colors ---
        private static readonly Color s_panelBG = new Color(0f, 0f, 0f, 0.55f);
        private static readonly Color s_inputBG = new Color(0f, 0f, 0f, 0.75f);
        private static readonly Color s_inputText = new Color(0.95f, 0.95f, 0.95f, 1f);

        /// <summary>Scroll positions at or below this normalized value count as "at the bottom" for autoscroll.</summary>
        private const float AUTOSCROLL_EPSILON = 0.01f;

        private CommandEngine _engine;
        private GameObject _panel;
        private ScrollRect _scrollRect;
        private TextMeshProUGUI _historyText;
        private TMP_InputField _inputField;
        private readonly StringBuilder _historyBuilder = new StringBuilder();
        private bool _historyDirty;
        private bool _autoscrollPending;

        /// <summary>The console engine this view drives (commands register on its <see cref="CommandEngine.Registry"/>).</summary>
        public CommandEngine Engine => _engine;

        /// <summary>Whether the console panel is currently open.</summary>
        public bool IsOpen => _panel != null && _panel.activeSelf;

        private void Awake()
        {
            _engine = new CommandEngine();
            _engine.LineAppended += OnLineAppended;

            BuildHierarchy();
            _panel.SetActive(false);
        }

        private void OnDestroy()
        {
            if (_engine != null)
                _engine.LineAppended -= OnLineAppended;
        }

        private void Update()
        {
            if (!IsOpen)
                return;

            // ↑/↓ history recall (UI action map — the gameplay map is disabled while open).
            if (InputManager.Instance.ConsoleHistoryUpPressed)
                SetInputText(_engine.RecallPrevious());
            else if (InputManager.Instance.ConsoleHistoryDownPressed)
                SetInputText(_engine.RecallNext());
        }

        private void LateUpdate()
        {
            // Coalesce per-line append events into at most one text rebuild + scroll per frame.
            if (_historyDirty)
            {
                _historyDirty = false;
                RebuildHistoryText();
            }

            if (_autoscrollPending)
            {
                _autoscrollPending = false;
                Canvas.ForceUpdateCanvases();
                _scrollRect.verticalNormalizedPosition = 0f;
            }
        }

        /// <summary>Opens the panel and focuses the input field (T-leak-guarded). Called by <see cref="WorldUIManager"/>.</summary>
        public void Open()
        {
            if (IsOpen)
                return;

            _panel.SetActive(true);
            _historyDirty = true;
            _autoscrollPending = true;
            StartCoroutine(FocusInputNextFrame());
        }

        /// <summary>Closes the panel. Called by <see cref="WorldUIManager"/>.</summary>
        public void Close()
        {
            if (!IsOpen)
                return;

            _inputField.DeactivateInputField();
            _inputField.text = "";
            _panel.SetActive(false);
        }

        /// <summary>
        /// Focuses the input field one frame after opening, then clears it — the opening T press
        /// otherwise leaks a "t" into the field (known Input System + TMP interaction).
        /// </summary>
        private IEnumerator FocusInputNextFrame()
        {
            yield return null;
            _inputField.text = "";
            _inputField.ActivateInputField();
        }

        /// <summary>Marks the history view dirty; autoscrolls only when the view was already at the bottom.</summary>
        /// <param name="line">The appended line (content comes from the engine's ring on rebuild).</param>
        private void OnLineAppended(ConsoleLine line)
        {
            _historyDirty = true;
            if (_scrollRect == null || _scrollRect.verticalNormalizedPosition <= AUTOSCROLL_EPSILON)
                _autoscrollPending = true;
        }

        /// <summary>Rebuilds the history text from the engine's output ring (stays consistent when the ring drops old lines).</summary>
        private void RebuildHistoryText()
        {
            _historyBuilder.Clear();
            var output = _engine.Output;
            for (int i = 0; i < output.Count; i++)
            {
                if (i > 0)
                    _historyBuilder.Append('\n');
                _historyBuilder.Append(ConsoleTextFormatter.Format(output[i]));
            }

            _historyText.text = _historyBuilder.ToString();
        }

        /// <summary>Submit handler: empty input closes the console; anything else executes and refocuses.</summary>
        /// <param name="text">The submitted field text.</param>
        private void OnSubmit(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                if (WorldUIManager.Instance != null)
                    WorldUIManager.Instance.IsConsoleOpen = false;
                return;
            }

            _engine.Execute(text);
            _inputField.text = "";
            _inputField.ActivateInputField();
            _autoscrollPending = true;
        }

        /// <summary>Places recalled history in the field with the caret at the end (null clears the field).</summary>
        /// <param name="recalled">The recalled command, or null for the live empty line.</param>
        private void SetInputText(string recalled)
        {
            _inputField.text = recalled ?? "";
            _inputField.caretPosition = _inputField.text.Length;
            _inputField.ActivateInputField();
        }

        // ──────────────────────────────────────────────
        //  Hierarchy construction (runtime, code-only)
        // ──────────────────────────────────────────────

        /// <summary>Builds the overlay canvas, panel, scrollable history, and input field.</summary>
        private void BuildHierarchy()
        {
            // Own overlay canvas so the console sorts above the HUD without touching scene objects.
            Canvas canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SORT_ORDER;
            CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(REFERENCE_WIDTH, REFERENCE_HEIGHT);
            gameObject.AddComponent<GraphicRaycaster>();

            // Panel: bottom-left translucent backdrop.
            _panel = new GameObject("ConsolePanel", typeof(RectTransform), typeof(Image));
            _panel.transform.SetParent(transform, false);
            RectTransform panelRect = (RectTransform)_panel.transform;
            panelRect.anchorMin = Vector2.zero;
            panelRect.anchorMax = Vector2.zero;
            panelRect.pivot = Vector2.zero;
            panelRect.anchoredPosition = new Vector2(PANEL_MARGIN, PANEL_MARGIN);
            panelRect.sizeDelta = new Vector2(PANEL_WIDTH, PANEL_HEIGHT);
            _panel.GetComponent<Image>().color = s_panelBG;

            BuildHistoryView(panelRect);
            BuildInputField(panelRect);
        }

        /// <summary>Builds the scroll view + wrapped TMP history text filling the panel above the input row.</summary>
        /// <param name="panelRect">The panel to parent under.</param>
        private void BuildHistoryView(RectTransform panelRect)
        {
            GameObject scrollGo = DefaultControls.CreateScrollView(new DefaultControls.Resources());
            scrollGo.name = "History";
            scrollGo.transform.SetParent(panelRect, false);

            RectTransform scrollRectTransform = (RectTransform)scrollGo.transform;
            scrollRectTransform.anchorMin = new Vector2(0f, 0f);
            scrollRectTransform.anchorMax = new Vector2(1f, 1f);
            scrollRectTransform.offsetMin = new Vector2(INNER_PADDING, INPUT_HEIGHT + INNER_PADDING * 2f);
            scrollRectTransform.offsetMax = new Vector2(-INNER_PADDING, -INNER_PADDING);

            _scrollRect = scrollGo.GetComponent<ScrollRect>();
            _scrollRect.horizontal = false;
            if (_scrollRect.horizontalScrollbar != null)
                Destroy(_scrollRect.horizontalScrollbar.gameObject);
            _scrollRect.horizontalScrollbar = null;
            scrollGo.GetComponent<Image>().color = Color.clear;

            // Content: a single wrapped TMP text that grows vertically; ContentSizeFitter drives content height.
            RectTransform content = _scrollRect.content;
            GameObject textGo = new GameObject("HistoryText", typeof(RectTransform));
            textGo.transform.SetParent(content, false);
            RectTransform textRect = (RectTransform)textGo.transform;
            textRect.anchorMin = new Vector2(0f, 1f);
            textRect.anchorMax = new Vector2(1f, 1f);
            textRect.pivot = new Vector2(0.5f, 1f);
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            _historyText = textGo.AddComponent<TextMeshProUGUI>();
            _historyText.fontSize = FONT_SIZE;
            _historyText.richText = true;
            _historyText.textWrappingMode = TextWrappingModes.Normal;
            _historyText.text = "";

            ContentSizeFitter textFitter = textGo.AddComponent<ContentSizeFitter>();
            textFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            ContentSizeFitter contentFitter = content.gameObject.AddComponent<ContentSizeFitter>();
            contentFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            VerticalLayoutGroup contentLayout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            contentLayout.childControlHeight = true;
            contentLayout.childControlWidth = true;
            contentLayout.childForceExpandHeight = false;
        }

        /// <summary>Builds the single-line TMP input field docked to the panel bottom.</summary>
        /// <param name="panelRect">The panel to parent under.</param>
        private void BuildInputField(RectTransform panelRect)
        {
            GameObject inputGo = TMP_DefaultControls.CreateInputField(new TMP_DefaultControls.Resources());
            inputGo.name = "Input";
            inputGo.transform.SetParent(panelRect, false);

            RectTransform inputRect = (RectTransform)inputGo.transform;
            inputRect.anchorMin = new Vector2(0f, 0f);
            inputRect.anchorMax = new Vector2(1f, 0f);
            inputRect.pivot = new Vector2(0.5f, 0f);
            inputRect.offsetMin = new Vector2(INNER_PADDING, INNER_PADDING);
            inputRect.offsetMax = new Vector2(-INNER_PADDING, INNER_PADDING + INPUT_HEIGHT);

            inputGo.GetComponent<Image>().color = s_inputBG;

            _inputField = inputGo.GetComponent<TMP_InputField>();
            _inputField.lineType = TMP_InputField.LineType.SingleLine;
            _inputField.richText = false;
            _inputField.restoreOriginalTextOnEscape = true;
            _inputField.textComponent.fontSize = FONT_SIZE;
            _inputField.textComponent.color = s_inputText;
            if (_inputField.placeholder is TextMeshProUGUI placeholder)
            {
                placeholder.text = "Type /help for commands…";
                placeholder.fontSize = FONT_SIZE;
            }

            _inputField.onSubmit.AddListener(OnSubmit);
        }
    }
}
