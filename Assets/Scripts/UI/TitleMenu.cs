using System;
using System.IO;
using Data.Enums;
using TMPro;
using UnityEditor;
using UnityEngine;
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
        public GameObject worldSelectMenuObject;

        #endregion

        #region Main Menu UI Elements

        [Header("Main Menu UI Elements")]
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
        public Toggle chunkAnimationToggle;

        #endregion

        private Settings _settings;
        private readonly string _settingFilePath = Application.dataPath + "/settings.json";

        #endregion


        public void Awake()
        {
            // TODO: Extract settings loading logic into a single Settings class / singleton
            // Create settings file if it doesn't yet exist, after that, load it.
            if (!File.Exists(_settingFilePath) || Application.isEditor)
            {
                Debug.Log("No settings file found, creating new one.");
                _settings = new Settings();
                string jsonExport = JsonUtility.ToJson(_settings, true);
                File.WriteAllText(_settingFilePath, jsonExport);
#if UNITY_EDITOR
                AssetDatabase.Refresh(); // Refresh Unity's asset database.
# endif
            }

#if !UNITY_EDITOR
        string jsonImport = File.ReadAllText(_settingFilePath);
        _settings = JsonUtility.FromJson<Settings>(jsonImport);
# endif

            // --- VERSION STRING LOGIC ---
#if UNITY_EDITOR
            // In the Editor, we fetch the live date and the chosen enum from EditorPrefs.
            // (We cast from int back to the string representation of the Enum)
            int stageInt = EditorPrefs.GetInt("MC_DevStage", 2); // 2 = Alpha
            string stageString = stageInt == 5 ? "" : $" - {(DevelopmentStage)stageInt}"; // <-- Cannot resolve symbol 'DevelopmentStage' :71

            versionField.text = $"v{DateTime.Now:yyyy-MM-dd}{stageString} (Editor)";
#else
            // In a built game, the PreBuild hook has already baked the final string into Application.version
            versionField.text = $"v{Application.version}";
#endif
        }

        public void StartGame()
        {
            mainMenuObject.SetActive(false);
            worldSelectMenuObject.SetActive(true);
        }

        public void BackToMainMenu()
        {
            worldSelectMenuObject.SetActive(false);
            mainMenuObject.SetActive(true);
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
            // Application.Quit() does not work in the editor so UnityEditor.EditorApplication.isPlaying need to be set too false to quit the game.
            EditorApplication.isPlaying = false;
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
