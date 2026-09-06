using UnityEngine;

namespace UI.Blur
{
    /// <summary>
    /// Declares the subtree below it as one UI compositing band, and keeps that subtree on the band's
    /// layer. Sits on a canvas root, or on any subtree that must composite separately from its siblings.
    /// </summary>
    /// <remarks>
    /// The enable-time sweep is a repair pass, not the mechanism: objects created later take the band
    /// layer at creation instead, since Unity does not inherit a layer on reparent and a one-shot sweep
    /// cannot see what does not exist yet. If the sweep is ever what makes a band correct, a creation
    /// site is missing its assignment.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class UIBlurBand : MonoBehaviour
    {
        [Tooltip("Which compositing band this subtree draws in. Higher bands draw over, and can frost, lower ones.")]
        [SerializeField]
        private UIBandId _band = UIBandId.Hud;

        /// <summary>The band this subtree draws in.</summary>
        public UIBandId Band => _band;

        /// <summary>The layer this subtree's objects carry, or -1 when that layer is undeclared.</summary>
        public int Layer => UIBandLayers.LayerOf(_band);

        private void OnEnable() => ApplyLayer();

        /// <summary>Assigns this subtree's band and applies its layer.</summary>
        /// <param name="band">The band this subtree draws in.</param>
        public void SetBand(UIBandId band)
        {
            _band = band;
            ApplyLayer();
        }

        /// <summary>Puts this GameObject and every current descendant on this band's layer.</summary>
        /// <remarks>Safe to call repeatedly; does nothing while the band's layer is undeclared.</remarks>
        public void ApplyLayer() => UIBandLayers.SetBandRecursively(gameObject, _band);

#if UNITY_EDITOR
        private void OnValidate() => UnityEditor.EditorApplication.delayCall += ApplyLayerDeferred;

        private void OnDisable() => UnityEditor.EditorApplication.delayCall -= ApplyLayerDeferred;

        /// <summary>
        /// Re-applies the layer a tick after an inspector edit. Assigning a layer during
        /// <c>OnValidate</c> raises Unity's <c>OnLayersChanged</c> SendMessage, which the engine forbids.
        /// </summary>
        private void ApplyLayerDeferred()
        {
            UnityEditor.EditorApplication.delayCall -= ApplyLayerDeferred;
            if (this != null && isActiveAndEnabled) ApplyLayer();
        }
#endif
    }
}
