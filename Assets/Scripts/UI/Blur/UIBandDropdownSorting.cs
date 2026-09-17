using TMPro;
using UnityEngine;

namespace UI.Blur
{
    /// <summary>
    /// Keeps a <see cref="TMP_Dropdown"/>'s popup in the band its dropdown belongs to.
    /// </summary>
    /// <remarks>
    /// <see cref="TMP_Dropdown"/> stamps the popup's sorting layer from the first ancestor canvas with
    /// <c>isRootCanvas</c>, ignoring <c>overrideSorting</c> — so a dropdown inside a banded subtree has
    /// its popup sent to the root canvas's band instead of its own, where the panel that opened it
    /// paints over it. uGUI's own <c>Dropdown</c> tests <c>isRootCanvas || overrideSorting</c> and does
    /// not have the problem; this restores the same behavior for the TMP control.
    /// </remarks>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(TMP_Dropdown))]
    public sealed class UIBandDropdownSorting : MonoBehaviour
    {
        /// <summary>Re-bands the popup as soon as the dropdown parents it, before its blocker is built.</summary>
        private void OnTransformChildrenChanged() => ApplyBandToPopup();

        /// <summary>
        /// Moves every self-sorting canvas in this dropdown onto the enclosing band's sorting layer.
        /// </summary>
        /// <remarks>
        /// Only canvases that opted out of inherited sorting are touched, so the dropdown's own layout
        /// canvases are left alone. Does nothing outside a band, which is what keeps the component inert
        /// in scenes that have not been converted.
        /// </remarks>
        public void ApplyBandToPopup()
        {
            UIBlurBand band = GetComponentInParent<UIBlurBand>(true);
            if (band == null || UIBandLayers.SortingValueOf(band.Band) < 0) return;

            int sortingLayerId = band.SortingLayerId;

            foreach (Canvas canvas in GetComponentsInChildren<Canvas>(true))
                if (canvas.overrideSorting)
                    canvas.sortingLayerID = sortingLayerId;
        }
    }
}
