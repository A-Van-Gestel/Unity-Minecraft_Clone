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

        /// <summary>TMP hex tint of the inline autocomplete ghost suffix — dim gray, PowerShell-style (CMD-5).</summary>
        private const string GHOST_COLOR_HEX = "8A8A8A";

        /// <summary>Scroll positions at or below this normalized value count as "at the bottom" for autoscroll.</summary>
        private const float AUTOSCROLL_EPSILON = 0.01f;

        private CommandEngine _engine;
        private GameObject _panel;
        private ScrollRect _scrollRect;
        private TextMeshProUGUI _historyText;
        private TMP_InputField _inputField;
        private TextMeshProUGUI _ghostText;
        private string _ghostSourceText;
        private int _ghostSourceCaret = -1;
        private string _ghostSuffix = "";
        private readonly StringBuilder _historyBuilder = new StringBuilder();
        private bool _historyDirty;
        private bool _autoscrollPending;

        // UI_BUGS #04 diagnostics (temporary — remove with the #04 instrumentation): watchdog
        // state that detects the panel changing active state outside Open()/Close().
        private bool _diagLastPanelActive;
        private bool _diagExpectedTransition;

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
            // UI_BUGS #04 diagnostic: catch the panel's activeSelf flipping without Open()/Close()
            // having run (candidate class 1 — external deactivation / state desync). Remove with
            // the #04 instrumentation.
            bool diagPanelActive = _panel != null && _panel.activeSelf;
            if (diagPanelActive != _diagLastPanelActive)
            {
                if (!_diagExpectedTransition)
                    Debug.LogWarning($"[UIBUG04] Console panel active state changed OUTSIDE Open()/Close(): {_diagLastPanelActive} -> {diagPanelActive}. {DiagUIBug04State()}, frame={Time.frameCount}");
                _diagExpectedTransition = false;
                _diagLastPanelActive = diagPanelActive;
            }

            if (!IsOpen)
                return;

            // ↑/↓ history recall + Tab autocomplete + RightArrow/End accept (UI action map — the
            // gameplay map is disabled while open).
            if (InputManager.Instance.ConsoleHistoryUpPressed)
                SetInputText(_engine.RecallPrevious());
            else if (InputManager.Instance.ConsoleHistoryDownPressed)
                SetInputText(_engine.RecallNext());
            else if (InputManager.Instance.ConsoleAutocompletePressed)
                ApplyAutocomplete();
            else if (InputManager.Instance.ConsoleAcceptSuggestionPressed)
                TryAcceptInlineSuggestion();

            RefreshGhostIfChanged();
        }

        /// <summary>
        /// Applies Tab autocomplete: replaces the field text with the engine's completion, and lists
        /// the candidates in history when the completion is ambiguous (≥2 matches). Re-activates the
        /// field because Tab can move EventSystem selection off it (the documented T-leak class).
        /// </summary>
        private void ApplyAutocomplete()
        {
            CommandCompletion completion = _engine.Complete(_inputField.text);

            _inputField.text = completion.CompletedText;
            _inputField.caretPosition = _inputField.text.Length;
            _inputField.ActivateInputField();

            if (completion.Candidates.Length > 1)
                _engine.PostLine(ConsoleLineSeverity.Info, string.Join("  ", completion.Candidates));
        }

        /// <summary>
        /// Accepts the inline ghost suggestion (RightArrow/End): fills the field to the single-candidate
        /// completion — but only when a ghost is actually showing and the caret is at the end, so a
        /// RightArrow/End that just moves the caret is left to the input field.
        /// </summary>
        private void TryAcceptInlineSuggestion()
        {
            if (_inputField.caretPosition != _inputField.text.Length)
                return;
            // Accept only when a ghost is actually showing — reuse the already-computed suffix rather
            // than recomputing the suggestion (ApplyAutocomplete runs the single Complete pass).
            if (string.IsNullOrEmpty(_ghostSuffix))
                return;

            ApplyAutocomplete(); // a single-candidate input fills to the full completion
        }

        /// <summary>Recomputes the inline ghost only when the field text or caret changed (no steady-state work).</summary>
        private void RefreshGhostIfChanged()
        {
            if (_inputField.text == _ghostSourceText && _inputField.caretPosition == _ghostSourceCaret)
                return;

            _ghostSourceText = _inputField.text;
            _ghostSourceCaret = _inputField.caretPosition;
            UpdateGhost();
        }

        /// <summary>
        /// Renders the gray inline suggestion after the caret. A fully transparent copy of the typed
        /// text reserves the exact width so the gray suffix lines up right at the caret; the suffix is
        /// hidden when there is none or the caret is not at the end of the line.
        /// </summary>
        private void UpdateGhost()
        {
            string text = _inputField.text;
            string suffix = (!string.IsNullOrEmpty(text) && _inputField.caretPosition == text.Length)
                ? _engine.Suggest(text)
                : "";
            _ghostSuffix = suffix; // cached so RightArrow/End accept needn't recompute the suggestion

            // Strip any literal </noparse> from the typed text so it can't terminate the transparent
            // prefix's guard and let injected markup render (same guard ConsoleTextFormatter applies).
            _ghostText.text = string.IsNullOrEmpty(suffix)
                ? ""
                : $"<color=#00000000><noparse>{ConsoleTextFormatter.StripNoparse(text)}</noparse></color><color=#{GHOST_COLOR_HEX}>{suffix}</color>";
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
            {
                // UI_BUGS #04 diagnostic — remove with the #04 instrumentation.
                Debug.Log($"[UIBUG04] Open() no-op (already open). {DiagUIBug04State()}");
                return;
            }

            _diagExpectedTransition = true; // UI_BUGS #04 diagnostic
            _panel.SetActive(true);
            _historyDirty = true;
            _autoscrollPending = true;
            StartCoroutine(FocusInputNextFrame());
            // UI_BUGS #04 diagnostic — remove with the #04 instrumentation.
            Debug.Log($"[UIBUG04] Open(): {DiagUIBug04State()}");
        }

        /// <summary>Closes the panel. Called by <see cref="WorldUIManager"/>.</summary>
        public void Close()
        {
            if (!IsOpen)
            {
                // UI_BUGS #04 diagnostic — remove with the #04 instrumentation.
                Debug.Log($"[UIBUG04] Close() no-op (already closed). {DiagUIBug04State()}");
                return;
            }

            _diagExpectedTransition = true; // UI_BUGS #04 diagnostic
            _inputField.DeactivateInputField();
            _inputField.text = "";
            _ghostText.text = "";
            _ghostSourceText = null;
            _ghostSourceCaret = -1;
            _ghostSuffix = "";
            _panel.SetActive(false);
            // UI_BUGS #04 diagnostic — remove with the #04 instrumentation.
            Debug.Log($"[UIBUG04] Close(): {DiagUIBug04State()}");
        }

        // UI_BUGS #04 diagnostics (temporary — remove with the #04 instrumentation): a disable
        // of this component means an ANCESTOR GameObject was deactivated (or the object is being
        // torn down) — the watchdog above cannot see that, so log it here.
        private void OnEnable()
        {
            Debug.Log($"[UIBUG04] ConsoleUI OnEnable. {DiagUIBug04State()}, frame={Time.frameCount}");
        }

        private void OnDisable()
        {
            Debug.LogWarning($"[UIBUG04] ConsoleUI OnDisable — ancestor deactivated or teardown. {DiagUIBug04State()}, frame={Time.frameCount}");
        }

        /// <summary>
        /// UI_BUGS #04 diagnostic: panel/input-field state summary — discriminates "panel inactive"
        /// (state desync) from "panel active but field unfocused/hidden" (focus loss). Remove with
        /// the #04 instrumentation.
        /// </summary>
        /// <returns>A log-friendly summary of the panel and input-field state.</returns>
        public string DiagUIBug04State()
        {
            if (_panel == null)
                return "panel=null";
            return $"panelActiveSelf={_panel.activeSelf}, panelInHierarchy={_panel.activeInHierarchy}, " +
                   $"inputGoActive={(_inputField != null && _inputField.gameObject.activeInHierarchy)}, " +
                   $"inputFocused={(_inputField != null && _inputField.isFocused)}, " +
                   $"inputText='{(_inputField != null ? _inputField.text : "<null>")}'";
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
            // UI_BUGS #04 diagnostic — remove with the #04 instrumentation.
            Debug.Log($"[UIBUG04] FocusInputNextFrame ran. {DiagUIBug04State()}, frame={Time.frameCount}");
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

            BuildGhostOverlay();
        }

        /// <summary>
        /// Builds the inline-suggestion ghost: a non-interactive TMP text laid over the input field's
        /// own text, sharing its viewport, rect, font, size, and alignment so a transparent-prefixed
        /// suffix aligns to the caret. Parented under the text component's viewport for identical clipping.
        /// </summary>
        private void BuildGhostOverlay()
        {
            TMP_Text src = _inputField.textComponent;

            GameObject ghostGo = new GameObject("GhostSuggestion", typeof(RectTransform));
            ghostGo.transform.SetParent(src.rectTransform.parent, false);

            RectTransform srcRect = src.rectTransform;
            RectTransform ghostRect = (RectTransform)ghostGo.transform;
            ghostRect.anchorMin = srcRect.anchorMin;
            ghostRect.anchorMax = srcRect.anchorMax;
            ghostRect.pivot = srcRect.pivot;
            ghostRect.offsetMin = srcRect.offsetMin;
            ghostRect.offsetMax = srcRect.offsetMax;
            ghostRect.SetAsLastSibling();

            _ghostText = ghostGo.AddComponent<TextMeshProUGUI>();
            _ghostText.font = src.font;
            _ghostText.fontSize = FONT_SIZE;
            _ghostText.alignment = src.alignment;
            _ghostText.margin = src.margin;
            _ghostText.richText = true;
            _ghostText.raycastTarget = false;
            _ghostText.textWrappingMode = TextWrappingModes.NoWrap;
            _ghostText.overflowMode = TextOverflowModes.Overflow;
            _ghostText.color = Color.white;
            _ghostText.text = "";
        }
    }
}
