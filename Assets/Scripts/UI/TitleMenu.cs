using System.IO;
using MyBox;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace UI
{
    public class TitleMenu : MonoBehaviour
    {
    #region Variables
    #region Menu Objects
        [Header("Menu Objects")]
        public GameObject mainMenuObject;
        public GameObject settingsMenuObject;
    #endregion

    #region Main Menu UI Elements
        [Header("Main Menu UI Elements")]
        public TextMeshProUGUI seedField;
        public TextMeshProUGUI versionField;
    #endregion

    #region Settings Menu UI Elements
        [Header("Settings Menu UI Elements")]
        // View Distance
        public Slider viewDistanceSlider;
        public TextMeshProUGUI viewDistanceText;

        // Mouse Sensitivity
        public Slider mouseSensitivitySlider;
        public TextMeshProUGUI mouseSensitivityText;
        
        // Cloud Style
        public TMP_Dropdown cloudStyleDropdown;

        // Toggles
        public Toggle threadingToggle;
        public Toggle chunkAnimationToggle;
    #endregion

        private Settings _settings;
        private readonly string _settingFilePath = Application.dataPath + "/settings.json";
    #endregion


        public void Awake()
        {
            // Create settings file if it doesn't yet exist, after that, load it.
            if (!File.Exists(_settingFilePath) || Application.isEditor)
            {
                Debug.Log("No settings file found, creating new one.");
                _settings = new Settings();
                string jsonExport = JsonUtility.ToJson(_settings, true);
                File.WriteAllText(_settingFilePath, jsonExport);
                AssetDatabase.Refresh(); // Refresh Unity's asset database.
            }

#if !UNITY_EDITOR
        string jsonImport = File.ReadAllText(settingFilePath);
        settings = JsonUtility.FromJson<Settings>(jsonImport);
# endif

            versionField.text = $"v{_settings.version}";
        }

        public void StartGame()
        {
            VoxelData.Seed = VoxelData.CalculateSeed(seedField.text);
            SceneManager.LoadScene("Scenes/World", LoadSceneMode.Single);
        }


        public void EnterSettings()
        {
            // View Distance
            viewDistanceSlider.value = _settings.viewDistance;
            UpdateViewDistanceSlider();
            // Mouse Sensitivity
            mouseSensitivitySlider.value = _settings.mouseSensitivityX;
            UpdateMouseSensitivitySlider();
            // Cloud Style
            cloudStyleDropdown.value = (int)_settings.clouds;
            // Toggles
            threadingToggle.isOn = _settings.enableThreading;
            chunkAnimationToggle.isOn = _settings.enableChunkLoadAnimations;

            mainMenuObject.SetActive(false);
            settingsMenuObject.SetActive(true);
        }

        public void LeaveSettings()
        {
            _settings.viewDistance = (int)viewDistanceSlider.value;
            _settings.mouseSensitivityX = mouseSensitivitySlider.value;
            _settings.mouseSensitivityY = mouseSensitivitySlider.value;
            _settings.clouds = (CloudStyle)cloudStyleDropdown.value;
            _settings.enableThreading = threadingToggle.isOn;
            _settings.enableChunkLoadAnimations = chunkAnimationToggle.isOn;

            string jsonExport = JsonUtility.ToJson(_settings, true);
            File.WriteAllText(_settingFilePath, jsonExport);

            mainMenuObject.SetActive(true);
            settingsMenuObject.SetActive(false);
        }

        public void QuitGame()
        {
            // save any game data here
#if UNITY_EDITOR
            // Application.Quit() does not work in the editor so UnityEditor.EditorApplication.isPlaying need to be set to false to quit the game.
            UnityEditor.EditorApplication.isPlaying = false;
#else
         Application.Quit();
#endif
        }

    #region UI UpdateSliderValues
        public void UpdateViewDistanceSlider()
        {
            viewDistanceText.text = $"View Distance: {viewDistanceSlider.value}";
        }

        public void UpdateMouseSensitivitySlider()
        {
            mouseSensitivityText.text = $"Mouse Sensitivity: {mouseSensitivitySlider.value:f1}";
        }
    #endregion
    }
}