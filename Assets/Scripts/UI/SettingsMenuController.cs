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
        [SerializeField] private Button[] _tabButtons;
        [SerializeField] private GameObject[] _tabContents;

        #endregion

        #region General Tab UI Elements

        [Header("General Tab")]
        [SerializeField] private TMP_Dropdown _uiScaleDropdown;
        [SerializeField] private Slider _mouseSensitivitySlider;
        [SerializeField] private TextMeshProUGUI _mouseSensitivityText;
        [SerializeField] private Toggle _chunkAnimationToggle;

        #endregion

        #region Graphics Tab UI Elements

        [Header("Graphics Tab")]
        [SerializeField] private Slider _viewDistanceSlider;
        [SerializeField] private TextMeshProUGUI _viewDistanceText;
        [SerializeField] private TMP_Dropdown _cloudStyleDropdown;

        #endregion

        #region Events

        [Header("Events")]
        [Tooltip("Invoked when the user clicks Done. Parent menus should subscribe to handle closing/transitioning.")]
        public UnityEvent onSettingsClosed;

        #endregion

        [Header("Scroll")]
        [SerializeField] private ScrollRect _scrollRect;

        private Settings _settings;

        #region Unity Lifecycle

        private void Awake()
        {
            if (_scrollRect == null)
                _scrollRect = GetComponentInChildren<ScrollRect>(true);
        }

        private void OnEnable()
        {
            _settings = SettingsManager.LoadSettings();
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
            if (_tabContents == null || _tabButtons == null) return;

            for (int i = 0; i < _tabContents.Length; i++)
            {
                if (_tabContents[i] != null) _tabContents[i].SetActive(i == tabIndex);
                if (i < _tabButtons.Length && _tabButtons[i] != null)
                    _tabButtons[i].interactable = (i != tabIndex);
            }

            // Swap the ScrollRect content to the newly active tab
            if (_scrollRect != null && tabIndex < _tabContents.Length && _tabContents[tabIndex] != null)
            {
                _scrollRect.content = _tabContents[tabIndex].GetComponent<RectTransform>();
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
                _mouseSensitivityText.text = $"Mouse Sensitivity: {_mouseSensitivitySlider.value:f1}";
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

        #endregion
    }
}
