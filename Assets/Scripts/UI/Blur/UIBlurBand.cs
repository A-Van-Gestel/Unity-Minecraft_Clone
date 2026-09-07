using UnityEngine;

namespace UI.Blur
{
    /// <summary>
    /// Declares the subtree below it as one UI compositing band. Sits on a canvas root, or on any
    /// subtree that must composite separately from its siblings.
    /// </summary>
    /// <remarks>
    /// The band is carried by a nested <see cref="Canvas"/> with <c>overrideSorting</c>, so one component
    /// routes a whole subtree and no per-object data is written. The subtree is also kept on the UI
    /// GameObject layer, which is what excludes it from the renderer's own draw passes.
    /// </remarks>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Canvas))]
    public sealed class UIBlurBand : MonoBehaviour
    {
        [Tooltip("Which compositing band this subtree draws in. Higher bands draw over, and can frost, lower ones.")]
        [SerializeField]
        private UIBandId _band = UIBandId.Hud;

        private Canvas _canvas;

        /// <summary>The band this subtree draws in.</summary>
        public UIBandId Band => _band;

        /// <summary>The sorting layer id this subtree's canvas carries.</summary>
        public int SortingLayerId => UIBandLayers.SortingLayerIdOf(_band);

        private void OnEnable()
        {
            Apply();
            UIBandRegistry.Register(this);
        }

        private void OnDisable()
        {
            UIBandRegistry.Unregister(this);
#if UNITY_EDITOR
            UnityEditor.EditorApplication.delayCall -= ApplyDeferred;
#endif
        }

        /// <summary>Assigns this subtree's band and routes it.</summary>
        /// <param name="band">The band this subtree draws in.</param>
        public void SetBand(UIBandId band)
        {
            _band = band;

            // Re-registering is idempotent, and the registry reads the band off the component, so this
            // only has to keep an enabled component present.
            if (isActiveAndEnabled) UIBandRegistry.Register(this);
            Apply();
        }

        /// <summary>
        /// Routes this subtree into its band: the canvas takes the band's sorting layer, and the subtree
        /// is put on the UI layer so the renderer's own passes skip it.
        /// </summary>
        /// <remarks>Safe to call repeatedly; does nothing while the band's sorting layer is undeclared.</remarks>
        public void Apply()
        {
            if (_canvas == null) _canvas = GetComponent<Canvas>();

            UIBandLayers.SetUILayerRecursively(gameObject);

            int sortingLayerId = SortingLayerId;
            if (UIBandLayers.SortingValueOf(_band) < 0) return;

            // Order matters: a nested canvas ignores sortingLayerID while it is still inheriting, so the
            // opt-out has to come first. A root canvas already owns its sorting and forces the flag off.
            if (!_canvas.isRootCanvas) _canvas.overrideSorting = true;

            _canvas.sortingLayerID = sortingLayerId;

#if UNITY_EDITOR
            WarnIfInteractiveWithoutRaycaster();
            WarnIfOffCanvasPlane();
#endif
        }

#if UNITY_EDITOR
        /// <summary>
        /// Reports a nested band root whose interactive content can render but never be clicked.
        /// </summary>
        /// <remarks>
        /// uGUI registers a <c>Graphic</c> against its nearest canvas, and a <c>GraphicRaycaster</c> only
        /// serves the canvas on its own object — so the canvas this component needs for banding moves the
        /// subtree out of the parent raycaster's reach. The UI still draws, which is what makes the loss
        /// of input silent.
        /// </remarks>
        private void WarnIfInteractiveWithoutRaycaster()
        {
            if (_canvas.isRootCanvas || GetComponent<UnityEngine.UI.GraphicRaycaster>() != null) return;
            if (GetComponentInChildren<UnityEngine.UI.Selectable>(true) == null) return;

            Debug.LogWarning($"UIBlurBand on '{name}' banded a nested canvas holding interactive UI, but " +
                             "the object has no GraphicRaycaster. Its buttons will draw and never " +
                             "receive pointer events.", this);
        }

        /// <summary>Reports UI in this band that sits off the canvas plane.</summary>
        /// <remarks>
        /// An overlay canvas ignores local Z; a screen-space canvas rendered through a perspective
        /// camera projects anything off its plane at a different scale.
        /// </remarks>
        private void WarnIfOffCanvasPlane()
        {
            if (!UIBandLayers.TryFindDepthOffset(gameObject, out Transform offender)) return;

            Debug.LogWarning($"UIBlurBand on '{name}': '{offender.name}' has local Z " +
                             $"{offender.localPosition.z}. A screen-space canvas renders through a " +
                             "perspective camera, so it will be scaled off-size.", offender);
        }

        private void OnValidate() => UnityEditor.EditorApplication.delayCall += ApplyDeferred;

        /// <summary>
        /// Re-routes a tick after an inspector edit. Assigning a layer during <c>OnValidate</c> raises
        /// Unity's <c>OnLayersChanged</c> SendMessage, which the engine forbids.
        /// </summary>
        private void ApplyDeferred()
        {
            UnityEditor.EditorApplication.delayCall -= ApplyDeferred;
            if (this != null && isActiveAndEnabled) Apply();
        }
#endif
    }
}
