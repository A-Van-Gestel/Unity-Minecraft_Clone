using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace UI
{
    /// <summary>
    /// Shell controller for the Settings menu.
    /// Manages tab switching, scroll rect, Done button, and delegates all UI generation
    /// and value binding to <see cref="SettingsUIGenerator"/>.
    /// <para>
    /// Designed to be reusable across scenes (Main Menu, In-Game Pause Menu).
    /// The <see cref="IsInGame"/> property determines whether
    /// <see cref="MyBox.InitializationFieldAttribute"/> fields are locked.
    /// </para>
    /// </summary>
    public class SettingsMenuController : MonoBehaviour
    {
        #region Serialized Fields

        [Header("Generator")]
        [Tooltip("Reference to the SettingsUIGenerator that builds and binds all UI controls.")]
        [SerializeField]
        private SettingsUIGenerator _generator;

        [Header("Scroll")]
        [SerializeField]
        private ScrollRect _scrollRect;

        [Header("Events")]
        [Tooltip("Invoked when the user clicks Done. Parent menus should subscribe to handle closing/transitioning.")]
        public UnityEvent onSettingsClosed;

        #endregion

        #region Runtime State

        /// <summary>
        /// Runtime tab button array, populated from the generator after generation.
        /// </summary>
        private Button[] _runtimeTabButtons;

        /// <summary>
        /// Runtime tab content panel array, populated from the generator after generation.
        /// </summary>
        private GameObject[] _runtimeTabContents;

        /// <summary>
        /// Set by the parent menu before enabling the settings panel.
        /// When true (opened from Pause Menu), <see cref="MyBox.InitializationFieldAttribute"/> fields
        /// are non-interactable. When false (opened from Main Menu), all fields are interactable.
        /// </summary>
        public bool IsInGame { get; set; }

        /// <summary>Reference to the active deferred rebind coroutine, so it can be stopped on disable.</summary>
        private Coroutine _deferredRebindCoroutine;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            if (_scrollRect == null)
                _scrollRect = GetComponentInChildren<ScrollRect>(true);

            // One-time generation: reflect, instantiate tabs + controls, bind callbacks
            if (_generator != null)
            {
                _generator.Generate();
                _generator.GetTabArrays(out _runtimeTabButtons, out _runtimeTabContents);
            }
        }

        private void OnEnable()
        {
            SwitchTab(0);
        }

        private void OnDisable()
        {
            if (_deferredRebindCoroutine != null)
            {
                StopCoroutine(_deferredRebindCoroutine);
                _deferredRebindCoroutine = null;
            }
        }

        /// <summary>
        /// Waits one frame for <c>Start()</c> to complete on newly activated controls,
        /// then rebinds values to restore "Label: Value" captions that were reset by
        /// <c>TMP_Dropdown.RefreshShownValue()</c>.
        /// </summary>
        private IEnumerator DeferredRebind()
        {
            yield return null;

            if (_generator != null)
                _generator.RebindValues(IsInGame);

            _deferredRebindCoroutine = null;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Switches to the specified settings tab by index.
        /// Activates the corresponding content panel, disables all others,
        /// swaps the ScrollRect content, resets scroll to the top,
        /// and rebinds control values (which must happen after activation
        /// so that dropdown captions are not reset by RefreshShownValue).
        /// </summary>
        /// <param name="tabIndex">Zero-based tab index.</param>
        public void SwitchTab(int tabIndex)
        {
            if (_runtimeTabContents == null || _runtimeTabButtons == null) return;
            if (tabIndex < 0 || tabIndex >= _runtimeTabContents.Length) return;

            for (int i = 0; i < _runtimeTabContents.Length; i++)
            {
                if (_runtimeTabContents[i] != null) _runtimeTabContents[i].SetActive(i == tabIndex);
                if (i < _runtimeTabButtons.Length && _runtimeTabButtons[i] != null)
                    _runtimeTabButtons[i].interactable = (i != tabIndex);
            }

            // Swap the ScrollRect content to the newly active tab
            if (_scrollRect != null && _runtimeTabContents[tabIndex] != null)
            {
                _scrollRect.content = _runtimeTabContents[tabIndex].GetComponent<RectTransform>();
                _scrollRect.verticalNormalizedPosition = 1f; // Scroll to top
            }

            // Newly activated dropdowns may have their Start() method run later this frame,
            // which calls RefreshShownValue() and resets our "Label: Value" captions.
            // A one-frame deferred rebind corrects this after Start() completes.
            if (gameObject.activeInHierarchy)
            {
                if (_deferredRebindCoroutine != null)
                    StopCoroutine(_deferredRebindCoroutine);

                _deferredRebindCoroutine = StartCoroutine(DeferredRebind());
            }
        }

        /// <summary>
        /// Called by the Done button. Persists all settings to disk and invokes the
        /// <see cref="onSettingsClosed"/> event so parent menus can transition.
        /// <para>
        /// Because settings are applied immediately by the generator's onValueChanged bindings,
        /// this method only needs to persist the already-updated Settings object.
        /// </para>
        /// </summary>
        public void OnDoneClicked()
        {
            SettingsManager.SaveSettings(SettingsManager.LoadSettings());
            onSettingsClosed?.Invoke();
        }

        /// <summary>
        /// Clears all benchmark saves from disk. Can be hooked up to a UI Button.
        /// </summary>
        /// <remarks
        /// TODO: Implement into settings UI.
        /// </remarks>
        public void ClearAllBenchmarks()
        {
            SaveSystem.ClearAllBenchmarks();
        }

        #endregion
    }
}
