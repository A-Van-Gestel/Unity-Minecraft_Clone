using UnityEngine;
using UnityEngine.EventSystems;

namespace Input
{
    /// <summary>
    /// Lightweight touch button that tracks pressed state with frame-accurate press detection.
    /// Attach to a GameObject with an <see cref="UnityEngine.UI.Image"/> for raycasting.
    /// </summary>
    public class TouchButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        /// <summary>Whether the button is currently held down.</summary>
        public bool IsPressed { get; private set; }

        /// <summary>Whether the button was first pressed during the current frame.</summary>
        public bool WasPressedThisFrame => _pressedFrame == Time.frameCount;

        private int _pressedFrame = -1;

        /// <inheritdoc />
        public void OnPointerDown(PointerEventData eventData)
        {
            IsPressed = true;
            _pressedFrame = Time.frameCount;
        }

        /// <inheritdoc />
        public void OnPointerUp(PointerEventData eventData)
        {
            IsPressed = false;
        }

        private void OnDisable()
        {
            IsPressed = false;
        }
    }
}
