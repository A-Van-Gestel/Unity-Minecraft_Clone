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

            // UI_BUGS #04: heal any child destroyed while the console is open before Update and
            // LateUpdate dereference it (both read _inputField/_ghostText/_scrollRect/_historyText).
            // Bail this frame only if the panel itself is gone.
            if (!RebuildMissingChildren())
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
            // Nothing to render while closed: a line posted to a closed console (e.g. the teleport
            // arrival-hold outcome after an Esc-close mid-hold) must not drive a canvas rebuild on
            // the inactive panel subtree. Open() re-marks both flags, so no posted line is lost.
            if (!IsOpen)
                return;

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

        /// <summary>Opens the panel and focuses the input field (T-leak-guarded), self-healing any destroyed child first. Called by <see cref="WorldUIManager"/>.</summary>
        /// <returns>True when the console opened (or was already open); false when the panel itself is gone and it could not open.</returns>
        public bool Open()
        {
            if (IsOpen)
                return true;

            // A heavy chunk-churn / origin-rebase can destroy the input field (and, defensively,
            // any built child) out from under the live view (UI_BUGS #04). Reconstruct whatever is
            // missing before showing, so the console can never open unusable.
            if (!RebuildMissingChildren())
                return false; // The panel itself is gone — a full teardown Awake owns, not Open().

            _panel.SetActive(true);
            _historyDirty = true;
            _autoscrollPending = true;
            StartCoroutine(FocusInputNextFrame());
            return true;
        }

        /// <summary>Closes the panel. Called by <see cref="WorldUIManager"/>.</summary>
        public void Close()
        {
            if (!IsOpen)
                return;

            _inputField.DeactivateInputField();
            _inputField.text = "";
            _ghostText.text = "";
            ResetGhostTracking();
            _panel.SetActive(false);
        }

        /// <summary>
        /// Reconstructs any built object destroyed out from under the view (UI_BUGS #04) so
        /// <see cref="Open"/> can never present a broken console. Heals at whatever level died: the
        /// whole panel (rebuilt under the surviving canvas), or an individual build-unit (history
        /// view, input field, ghost overlay). Clears a stray remnant first so a partial survivor
        /// cannot leave a duplicate.
        /// </summary>
        /// <returns>True when the panel exists (already whole or healed); false only if the rebuild failed.</returns>
        private bool RebuildMissingChildren()
        {
            if (_panel == null)
            {
                // The ConsolePanel child was destroyed but this component (and its canvas) survive,
                // so rebuild the whole panel + contents rather than giving up. Only the Console
                // GameObject / canvas being destroyed is unrecoverable — but then this code can't run.
                Debug.LogWarning($"Console panel destroyed externally; rebuilding it (UI_BUGS #04, root cause unresolved). frame={Time.frameCount}");
                DestroyChildByName(transform, "ConsolePanel");
                BuildPanel();
                ResetGhostTracking();
                _historyDirty = true; // repopulate history from the engine's ring on the next LateUpdate
                if (_panel == null)
                {
                    Debug.LogError("Console panel rebuild failed; cannot open (UI_BUGS #04).");
                    return false;
                }

                return true; // BuildPanel reconstructed the panel, history, and input field.
            }

            RectTransform panelRect = (RectTransform)_panel.transform;

            if (_scrollRect == null || _historyText == null)
            {
                Debug.LogWarning($"Console history view destroyed externally; rebuilding (UI_BUGS #04, root cause unresolved). frame={Time.frameCount}");
                DestroyChildByName(panelRect, "History");
                BuildHistoryView(panelRect);
                _historyDirty = true; // repopulate from the engine's ring on the next LateUpdate
            }

            if (_inputField == null)
            {
                Debug.LogWarning($"Console input field destroyed externally; rebuilding (UI_BUGS #04, root cause unresolved). frame={Time.frameCount}");
                DestroyChildByName(panelRect, "Input");
                BuildInputField(panelRect);
                ResetGhostTracking();
                // Mid-open heal must refocus; Open()'s own coroutine covers the closed->open case (panel not yet active here).
                if (IsOpen)
                    _inputField.ActivateInputField();
            }
            else if (_ghostText == null)
            {
                // Only the inline-suggestion overlay died — rebuild it alone, preserving the live field and its typed text.
                Debug.LogWarning($"Console ghost overlay destroyed externally; rebuilding (UI_BUGS #04, root cause unresolved). frame={Time.frameCount}");
                BuildGhostOverlay();
                ResetGhostTracking();
            }
            else if (!_inputField.gameObject.activeSelf)
            {
                // Belt-and-suspenders for the demoted "field alive but GameObject inactive" hypothesis.
                _inputField.gameObject.SetActive(true);
            }

            return true;
        }

        /// <summary>Clears the cached inline-ghost tracking so it recomputes against the current field (mirrors <see cref="Close"/>).</summary>
        private void ResetGhostTracking()
        {
            _ghostSourceText = null;
            _ghostSourceCaret = -1;
            _ghostSuffix = "";
        }

        /// <summary>Destroys a direct child of <paramref name="parent"/> by name when a remnant survives, so a rebuild cannot duplicate it.</summary>
        /// <param name="parent">The transform whose child to clear.</param>
        /// <param name="childName">The child GameObject name ("ConsolePanel", "History", or "Input").</param>
        private static void DestroyChildByName(Transform parent, string childName)
        {
            Transform remnant = parent.Find(childName);
            if (remnant != null)
                Destroy(remnant.gameObject);
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

        /// <summary>Builds the overlay canvas (once, in <see cref="Awake"/>) then the panel and its contents.</summary>
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

            BuildPanel();
        }

        /// <summary>
        /// Builds the panel backdrop and its history + input children under this view's canvas.
        /// Separate from the one-time canvas setup so it can be re-called to self-heal a destroyed
        /// panel (UI_BUGS #04) without duplicating the canvas components on this GameObject.
        /// </summary>
        private void BuildPanel()
        {
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
            inputGo.AddComponent<InputFieldDeathSentinel>(); // UI_BUGS #04 tripwire — see the class below.

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

        // UI_BUGS #04 tripwire (permanent while root cause unresolved): the field self-heals, but this
        // logs the exact frame it dies mid-play for correlation. scene.isLoaded is false during scene /
        // play-session / app teardown, so normal shutdown is suppressed.
        private sealed class InputFieldDeathSentinel : MonoBehaviour
        {
            private void OnDestroy()
            {
                if (!gameObject.scene.isLoaded)
                    return;
                Debug.LogWarning($"Console input field destroyed externally mid-play (UI_BUGS #04, root cause unresolved). frame={Time.frameCount}");
            }
        }
    }
}
