using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;

namespace UI.Tooltip
{
    /// <summary>
    /// Component attached to UI elements to trigger tooltips on hover.
    /// </summary>
    public class TooltipTrigger : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler
    {
        [Tooltip("The text to display in the tooltip.")]
        public string text;

        [Tooltip("Per-trigger position override. When set, overrides the TooltipManager's default hover mode.")]
        public TooltipHoverPosition? HoverPositionOverride;

        [Tooltip("Maximum width of the tooltip as a percentage of the screen width (0.1 to 1.0).")]
        [Range(0.1f, 1f)]
        public float maxWidthPercentage = 0.8f;

        private Coroutine _delayCoroutine;

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (string.IsNullOrEmpty(text)) return;

            if (_delayCoroutine != null)
                StopCoroutine(_delayCoroutine);

            _delayCoroutine = StartCoroutine(ShowDelayCoroutine());
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            HideTooltip();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            HideTooltip();
        }

        private void HideTooltip()
        {
            if (_delayCoroutine != null)
            {
                StopCoroutine(_delayCoroutine);
                _delayCoroutine = null;
            }

            if (TooltipManager.Instance != null)
            {
                TooltipManager.Hide();
            }
        }

        private void OnDisable()
        {
            HideTooltip();
        }

        private IEnumerator ShowDelayCoroutine()
        {
            float delay = TooltipManager.Instance != null ? TooltipManager.Instance.HoverDelay : 0.4f;

            // Use realtime so tooltips work even when Time.timeScale is 0 (e.g. paused menu)
            yield return new WaitForSecondsRealtime(delay);

            TooltipManager.Show(text, (RectTransform)transform, HoverPositionOverride, maxWidthPercentage);
        }
    }
}
