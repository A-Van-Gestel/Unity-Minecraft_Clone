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
        [SerializeField] private Canvas _parentCanvas;

        [Tooltip("The tooltip UI prefab.")]
        [SerializeField] private GameObject _tooltipPrefab;

        [Header("Configuration")]
        [Tooltip("The delay in seconds before a hovered tooltip is displayed.")]
        [SerializeField] private float _hoverDelay = 0.4f;

        [Tooltip("How the tooltip is positioned.")]
        [SerializeField] private TooltipHoverPosition _hoverMode = TooltipHoverPosition.FollowMouse;

        public float HoverDelay => _hoverDelay;

        private GameObject _activeTooltip;
        private TextMeshProUGUI _tooltipText;
        private RectTransform _tooltipRect;

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

            if (_hoverMode == TooltipHoverPosition.FollowMouse)
            {
                UpdateTooltipPosition();
            }
        }

        /// <summary>
        /// Displays the tooltip with the given text.
        /// </summary>
        public static void Show(string content)
        {
            if (Instance == null) return;
            Instance.ShowInternal(content);
        }

        /// <summary>
        /// Hides the active tooltip.
        /// </summary>
        public static void Hide()
        {
            if (Instance == null) return;
            Instance.HideInternal();
        }

        private void ShowInternal(string content)
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

            if (_tooltipText != null)
                _tooltipText.text = content;

            // Force layout rebuild so sizing is correct for boundary checks
            LayoutRebuilder.ForceRebuildLayoutImmediate(_tooltipRect);

            if (_hoverMode == TooltipHoverPosition.FollowMouse)
            {
                UpdateTooltipPosition();
            }

            _activeTooltip.SetActive(true);

            // Set as last sibling so it renders on top of everything
            _activeTooltip.transform.SetAsLastSibling();
        }

        private void HideInternal()
        {
            if (_activeTooltip != null)
                _activeTooltip.SetActive(false);
        }

        private void UpdateTooltipPosition()
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
    }
}
