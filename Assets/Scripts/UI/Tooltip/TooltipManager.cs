using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace UI.Tooltip
{
    /// <summary>
    /// Global manager for the UI Tooltip system.
    /// Instantiates and positions the tooltip prefab based on mouse position.
    /// </summary>
    public class TooltipManager : MonoBehaviour
    {
        public static TooltipManager Instance { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            Instance = null;
        }

        [Header("References")]
        [Tooltip("The canvas where the tooltip should be parented.")]
        [SerializeField]
        private Canvas _parentCanvas;

        [Tooltip("The tooltip UI prefab.")]
        [SerializeField]
        private GameObject _tooltipPrefab;

        [Header("Configuration")]
        [Tooltip("The delay in seconds before a hovered tooltip is displayed.")]
        [SerializeField]
        private float _hoverDelay = 0.4f;

        [Tooltip("How the tooltip is positioned.")]
        [SerializeField]
        private TooltipHoverPosition _hoverMode = TooltipHoverPosition.FollowMouse;

        public float HoverDelay => _hoverDelay;

        private GameObject _activeTooltip;
        private TextMeshProUGUI _tooltipText;
        private RectTransform _tooltipRect;
        private RectTransform _activeTriggerRect;
        private TooltipHoverPosition _activeHoverMode;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        private void Update()
        {
            if (_activeTooltip == null || !_activeTooltip.activeSelf) return;

            if (_activeHoverMode == TooltipHoverPosition.FollowMouse)
            {
                UpdateFollowMousePosition();
            }
        }

        /// <summary>
        /// Displays the tooltip with the given text.
        /// Optionally accepts the trigger's RectTransform and a per-trigger hover position override.
        /// </summary>
        /// <param name="content">The tooltip text to display.</param>
        /// <param name="triggerRect">The RectTransform of the element that triggered this tooltip.</param>
        /// <param name="positionOverride">Per-trigger position override; uses the manager's default when null.</param>
        public static void Show(string content, RectTransform triggerRect = null, TooltipHoverPosition? positionOverride = null)
        {
            if (Instance == null) return;
            Instance.ShowInternal(content, triggerRect, positionOverride);
        }

        /// <summary>
        /// Hides the active tooltip.
        /// </summary>
        public static void Hide()
        {
            if (Instance == null) return;
            Instance.HideInternal();
        }

        private void ShowInternal(string content, RectTransform triggerRect, TooltipHoverPosition? positionOverride)
        {
            if (_parentCanvas == null || _tooltipPrefab == null)
            {
                Debug.LogWarning("[TooltipManager] Missing Canvas or Prefab references.");
                return;
            }

            if (_activeTooltip == null)
            {
                _activeTooltip = Instantiate(_tooltipPrefab, _parentCanvas.transform);
                _tooltipRect = _activeTooltip.GetComponent<RectTransform>();
                _tooltipText = _activeTooltip.GetComponentInChildren<TextMeshProUGUI>();

                // Ensure Pivot is top-left so the offset logic works predictably
                _tooltipRect.pivot = new Vector2(0, 1);
            }

            _activeTriggerRect = triggerRect;
            _activeHoverMode = positionOverride ?? _hoverMode;

            if (_tooltipText != null)
                _tooltipText.text = content;

            // Force layout rebuild so sizing is correct for boundary checks
            LayoutRebuilder.ForceRebuildLayoutImmediate(_tooltipRect);

            switch (_activeHoverMode)
            {
                case TooltipHoverPosition.FollowMouse:
                    UpdateFollowMousePosition();
                    break;
                case TooltipHoverPosition.TopLeft:
                    UpdateAnchoredPosition();
                    break;
            }

            _activeTooltip.SetActive(true);

            // Set as last sibling so it renders on top of everything
            _activeTooltip.transform.SetAsLastSibling();
        }

        private void HideInternal()
        {
            if (_activeTooltip != null)
                _activeTooltip.SetActive(false);

            _activeTriggerRect = null;
        }

        private void UpdateFollowMousePosition()
        {
            if (_tooltipRect == null || _parentCanvas == null) return;

            // Use our centralized InputManager if available, fallback to new Input System
            Vector2 mousePos = InputManager.Instance != null
                ? InputManager.Instance.MousePosition
                : Mouse.current.position.ReadValue();

            // The tooltip's pivot is forced to (0, 1) in ShowInternal,
            // so finalPos is exactly the top-left corner of the tooltip.
            // We multiply rect dimensions by scaleFactor to get actual screen pixels.
            float tooltipWidth = _tooltipRect.rect.width * _parentCanvas.scaleFactor;
            float tooltipHeight = _tooltipRect.rect.height * _parentCanvas.scaleFactor;

            Vector2 finalPos = Vector2.zero;

            // X positioning: Put it on the side with the most space
            if (mousePos.x < Screen.width / 2f)
            {
                finalPos.x = mousePos.x + 10f; // Cursor is on left, put on right
            }
            else
            {
                finalPos.x = mousePos.x - tooltipWidth - 10f; // Cursor is on right, put on left
            }

            // Hard clamp X to screen bounds
            finalPos.x = Mathf.Clamp(finalPos.x, 0f, Screen.width - tooltipWidth);

            // Y positioning: Put it on the side with the most space
            if (mousePos.y > Screen.height / 2f)
            {
                finalPos.y = mousePos.y - 10f; // Cursor is on top, put below (Y goes down)
            }
            else
            {
                finalPos.y = mousePos.y + tooltipHeight + 10f; // Cursor is on bottom, put above
            }

            // Hard clamp Y to screen bounds (Pivot is Top-Left, so Y is the top edge)
            finalPos.y = Mathf.Clamp(finalPos.y, tooltipHeight, Screen.height);

            // Setting position directly works perfectly for Overlay canvases
            _tooltipRect.position = finalPos;
        }

        /// <summary>
        /// Positions the tooltip anchored to the trigger element.
        /// TopLeft: tooltip's top-left corner aligns with the trigger's top-right corner.
        /// Falls back to the opposite side when there is not enough screen space.
        /// </summary>
        private void UpdateAnchoredPosition()
        {
            if (_tooltipRect == null || _parentCanvas == null || _activeTriggerRect == null) return;

            float scaleFactor = _parentCanvas.scaleFactor;
            float tooltipWidth = _tooltipRect.rect.width * scaleFactor;
            float tooltipHeight = _tooltipRect.rect.height * scaleFactor;

            // Get the trigger's screen-space corners (bottom-left, top-left, top-right, bottom-right)
            Vector3[] triggerCorners = new Vector3[4];
            _activeTriggerRect.GetWorldCorners(triggerCorners);

            float triggerRight = triggerCorners[2].x;
            float triggerLeft = triggerCorners[0].x;
            float triggerTop = triggerCorners[1].y;

            Vector2 finalPos;

            // X: prefer placing to the right of the trigger; fall back to the left
            if (triggerRight + tooltipWidth <= Screen.width)
            {
                finalPos.x = triggerRight;
            }
            else
            {
                finalPos.x = triggerLeft - tooltipWidth;
            }

            // Y: align top edges; the pivot is top-left so finalPos.y is the top edge
            finalPos.y = triggerTop;

            // Hard clamp to screen bounds
            finalPos.x = Mathf.Clamp(finalPos.x, 0f, Screen.width - tooltipWidth);
            finalPos.y = Mathf.Clamp(finalPos.y, tooltipHeight, Screen.height);

            _tooltipRect.position = finalPos;
        }
    }
}
