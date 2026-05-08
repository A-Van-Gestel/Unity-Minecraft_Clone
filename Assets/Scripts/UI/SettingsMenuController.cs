using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace UI
{
    /// <summary>
    /// Self-contained controller for the Settings menu.
    /// Manages tab switching, settings load/save, and UI bindings.
    /// Designed to be reusable across scenes (Main Menu, In-Game Pause Menu).
    /// </summary>
    public class SettingsMenuController : MonoBehaviour
    {
        #region Tab System

        [Header("Tab System")]
        [SerializeField]
        private Button[] _tabButtons;

        [SerializeField]
        private GameObject[] _tabContents;

        #endregion

        #region General Tab UI Elements

        [Header("General Tab")]
        [SerializeField]
        private TMP_Dropdown _uiScaleDropdown;

        [SerializeField]
        private Slider _mouseSensitivitySlider;

        [SerializeField]
        private TextMeshProUGUI _mouseSensitivityText;

        [SerializeField]
        private Toggle _chunkAnimationToggle;

        #endregion

        #region Graphics Tab UI Elements

        [Header("Graphics Tab")]
        [SerializeField]
        private Slider _viewDistanceSlider;

        [SerializeField]
        private TextMeshProUGUI _viewDistanceText;

        [SerializeField]
        private TMP_Dropdown _cloudStyleDropdown;

        #endregion

        #region Dev Tab UI Elements

        [Header("Dev Tab (Editor & Development Builds Only)")]
        [Tooltip("The Dev tab button. Kept separate from the main tab arrays so it is always appended last at runtime.")]
        [SerializeField]
        private Button _devTabButton;

        [Tooltip("The Dev tab content panel. Kept separate from the main tab arrays so it is always appended last at runtime.")]
        [SerializeField]
        private GameObject _devTabContent;

        [SerializeField]
        private Toggle _simulateMigrationCorruptionToggle;

        #endregion

        #region Events

        [Header("Events")]
        [Tooltip("Invoked when the user clicks Done. Parent menus should subscribe to handle closing/transitioning.")]
        public UnityEvent onSettingsClosed;

        #endregion

        [Header("Scroll")]
        [SerializeField]
        private ScrollRect _scrollRect;

        private Settings _settings;

        /// <summary>
        /// Runtime-combined tab arrays that include the Dev tab (when visible).
        /// Built once per OnEnable from the serialized arrays + Dev fields.
        /// </summary>
        private Button[] _runtimeTabButtons;

        private GameObject[] _runtimeTabContents;

        #region Unity Lifecycle

        private void Awake()
        {
            if (_scrollRect == null)
                _scrollRect = GetComponentInChildren<ScrollRect>(true);
        }

        private void OnEnable()
        {
            _settings = SettingsManager.LoadSettings();
            ConfigureDevTabVisibility();
            RegisterListeners();
            InitializeUI();
            SwitchTab(0);
        }

        private void OnDisable()
        {
            UnregisterListeners();
        }

        #endregion

        #region Public API

        /// <summary>
        /// Switches to the specified settings tab by index.
        /// Called by tab button OnClick events.
        /// </summary>
        /// <param name="tabIndex">Zero-based tab index.</param>
        public void SwitchTab(int tabIndex)
        {
            if (_runtimeTabContents == null || _runtimeTabButtons == null) return;

            for (int i = 0; i < _runtimeTabContents.Length; i++)
            {
                if (_runtimeTabContents[i] != null) _runtimeTabContents[i].SetActive(i == tabIndex);
                if (i < _runtimeTabButtons.Length && _runtimeTabButtons[i] != null)
                    _runtimeTabButtons[i].interactable = (i != tabIndex);
            }

            // Swap the ScrollRect content to the newly active tab
            if (_scrollRect != null && tabIndex < _runtimeTabContents.Length && _runtimeTabContents[tabIndex] != null)
            {
                _scrollRect.content = _runtimeTabContents[tabIndex].GetComponent<RectTransform>();
                _scrollRect.verticalNormalizedPosition = 1f; // Scroll to top
            }
        }

        /// <summary>
        /// Called by the Done button. Saves all settings to disk and invokes the OnSettingsClosed event.
        /// </summary>
        public void OnDoneClicked()
        {
            SaveSettings();
            onSettingsClosed?.Invoke();
        }

        /// <summary>
        /// Updates the view distance label text to match the slider value.
        /// Called at runtime by the slider's onValueChanged event.
        /// </summary>
        public void UpdateViewDistanceLabel()
        {
            if (_viewDistanceText != null && _viewDistanceSlider != null)
                _viewDistanceText.text = $"View Distance: {_viewDistanceSlider.value}";
        }

        /// <summary>
        /// Updates the mouse sensitivity label text to match the slider value.
        /// Called at runtime by the slider's onValueChanged event.
        /// </summary>
        public void UpdateMouseSensitivityLabel()
        {
            if (_mouseSensitivityText != null && _mouseSensitivitySlider != null)
                _mouseSensitivityText.text = $"Mouse Sensitivity: {_mouseSensitivitySlider.value:f2}";
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// Registers runtime onValueChanged listeners for all UI controls.
        /// This replaces fragile prefab-based event wiring that broke during the refactor.
        /// </summary>
        private void RegisterListeners()
        {
            if (_uiScaleDropdown != null)
            {
                _uiScaleDropdown.onValueChanged.RemoveListener(OnUIScaleChanged);
                _uiScaleDropdown.onValueChanged.AddListener(OnUIScaleChanged);
            }

            if (_viewDistanceSlider != null)
            {
                _viewDistanceSlider.onValueChanged.RemoveListener(OnViewDistanceSliderChanged);
                _viewDistanceSlider.onValueChanged.AddListener(OnViewDistanceSliderChanged);
            }

            if (_mouseSensitivitySlider != null)
            {
                _mouseSensitivitySlider.onValueChanged.RemoveListener(OnMouseSensitivitySliderChanged);
                _mouseSensitivitySlider.onValueChanged.AddListener(OnMouseSensitivitySliderChanged);
            }

            if (_devTabButton != null)
            {
                _devTabButton.onClick.RemoveListener(OnDevTabClicked);
                _devTabButton.onClick.AddListener(OnDevTabClicked);
            }
        }

        /// <summary>
        /// Unregisters all runtime listeners to prevent duplicates on re-enable.
        /// </summary>
        private void UnregisterListeners()
        {
            if (_uiScaleDropdown != null)
                _uiScaleDropdown.onValueChanged.RemoveListener(OnUIScaleChanged);

            if (_viewDistanceSlider != null)
                _viewDistanceSlider.onValueChanged.RemoveListener(OnViewDistanceSliderChanged);

            if (_mouseSensitivitySlider != null)
                _mouseSensitivitySlider.onValueChanged.RemoveListener(OnMouseSensitivitySliderChanged);

            if (_devTabButton != null)
                _devTabButton.onClick.RemoveListener(OnDevTabClicked);
        }

        /// <summary>
        /// Populates all UI elements from the current settings.
        /// </summary>
        private void InitializeUI()
        {
            // UI Scale
            if (_uiScaleDropdown != null)
                _uiScaleDropdown.SetValueWithoutNotify(_settings.uiScale);

            // View Distance
            if (_viewDistanceSlider != null)
            {
                _viewDistanceSlider.SetValueWithoutNotify(_settings.viewDistance);
                UpdateViewDistanceLabel();
            }

            // Mouse Sensitivity
            if (_mouseSensitivitySlider != null)
            {
                _mouseSensitivitySlider.SetValueWithoutNotify(_settings.mouseSensitivityX);
                UpdateMouseSensitivityLabel();
            }

            // Cloud Style
            if (_cloudStyleDropdown != null)
                _cloudStyleDropdown.SetValueWithoutNotify((int)_settings.clouds);

            // Toggles
            if (_chunkAnimationToggle != null)
                _chunkAnimationToggle.SetIsOnWithoutNotify(_settings.enableChunkLoadAnimations);

            // Dev Settings
            if (_simulateMigrationCorruptionToggle != null)
                _simulateMigrationCorruptionToggle.SetIsOnWithoutNotify(_settings.Dev.simulateMigrationCorruption);
        }

        /// <summary>
        /// Reads all UI element values back into the Settings object and saves to disk.
        /// </summary>
        private void SaveSettings()
        {
            if (_uiScaleDropdown != null) _settings.uiScale = _uiScaleDropdown.value;
            if (_viewDistanceSlider != null) _settings.viewDistance = (int)_viewDistanceSlider.value;
            if (_mouseSensitivitySlider != null)
            {
                _settings.mouseSensitivityX = _mouseSensitivitySlider.value;
                _settings.mouseSensitivityY = _mouseSensitivitySlider.value;
            }

            if (_cloudStyleDropdown != null) _settings.clouds = (CloudStyle)_cloudStyleDropdown.value;
            if (_chunkAnimationToggle != null) _settings.enableChunkLoadAnimations = _chunkAnimationToggle.isOn;

            // Dev Settings
            if (_simulateMigrationCorruptionToggle != null)
                _settings.Dev.simulateMigrationCorruption = _simulateMigrationCorruptionToggle.isOn;

            SettingsManager.SaveSettings(_settings);
        }

        /// <summary>
        /// Callback for the view distance slider's onValueChanged event.
        /// </summary>
        /// <param name="value">The new slider value.</param>
        private void OnViewDistanceSliderChanged(float value)
        {
            UpdateViewDistanceLabel();
        }

        /// <summary>
        /// Callback for the mouse sensitivity slider's onValueChanged event.
        /// </summary>
        /// <param name="value">The new slider value.</param>
        private void OnMouseSensitivitySliderChanged(float value)
        {
            UpdateMouseSensitivityLabel();
        }

        /// <summary>
        /// Applies UI scale changes in real-time as the user adjusts the dropdown.
        /// </summary>
        /// <param name="value">The new scale index (0=Small, 1=Standard, 2=Large).</param>
        private void OnUIScaleChanged(int value)
        {
            UIScaleController scaler = FindAnyObjectByType<UIScaleController>();
            if (scaler != null) scaler.ApplyScale(value);
        }

        /// <summary>
        /// Callback for the Dev tab button click event.
        /// Switches to the dynamically assigned Dev tab index.
        /// </summary>
        private void OnDevTabClicked()
        {
            if (_tabButtons != null)
            {
                SwitchTab(_tabButtons.Length);
            }
        }

        /// <summary>
        /// Builds the runtime tab arrays from the serialized normal tabs,
        /// then conditionally appends the Dev tab as the last entry.
        /// In Release builds, the Dev tab button and content are hidden entirely.
        /// </summary>
        private void ConfigureDevTabVisibility()
        {
            bool showDevTab = Debug.isDebugBuild && _devTabButton != null && _devTabContent != null;

            if (showDevTab)
            {
                // Append Dev tab at the end of the runtime arrays
                _runtimeTabButtons = new Button[_tabButtons.Length + 1];
                _tabButtons.CopyTo(_runtimeTabButtons, 0);
                _runtimeTabButtons[_tabButtons.Length] = _devTabButton;

                _runtimeTabContents = new GameObject[_tabContents.Length + 1];
                _tabContents.CopyTo(_runtimeTabContents, 0);
                _runtimeTabContents[_tabContents.Length] = _devTabContent;

                // Ensure the Dev tab button is visually the last in the TabBar layout
                _devTabButton.transform.SetAsLastSibling();
                _devTabButton.gameObject.SetActive(true);
            }
            else
            {
                // No Dev tab — runtime arrays are just the serialized arrays
                _runtimeTabButtons = _tabButtons;
                _runtimeTabContents = _tabContents;

                // Ensure Dev tab UI is hidden in release builds
                if (_devTabButton != null) _devTabButton.gameObject.SetActive(false);
                if (_devTabContent != null) _devTabContent.SetActive(false);
            }
        }

        #endregion
    }
}
