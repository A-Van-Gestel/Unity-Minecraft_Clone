using UI;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Input
{
    /// <summary>
    /// Allows a toolbar slot to be selected by tapping it on mobile.
    /// Ignores taps when the inventory is open so that
    /// <see cref="DragAndDropHandler"/> can handle slot interactions instead.
    /// </summary>
    public class ToolbarSlotButton : MonoBehaviour, IPointerClickHandler
    {
        /// <summary>The 0-based hotbar slot index this button represents.</summary>
        public int SlotIndex { get; set; }

        /// <summary>The toolbar to notify when this slot is tapped.</summary>
        public Toolbar Toolbar { get; set; }

        /// <inheritdoc />
        public void OnPointerClick(PointerEventData eventData)
        {
            if (Toolbar == null) return;
            if (WorldUIManager.Instance != null && WorldUIManager.Instance.IsCreativeInventoryOpen) return;

            Toolbar.SelectSlot(SlotIndex);
        }
    }
}
