using System;
using Data.Enums;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace UI
{
    public class MainMenuController : MonoBehaviour
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

        #endregion


        public void Awake()
        {
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

            // Wire up the SettingsMenuController's OnSettingsClosed callback
            if (settingsMenuObject != null)
            {
                var settingsController = settingsMenuObject.GetComponent<SettingsMenuController>();
                if (settingsController != null)
                {
                    settingsController.onSettingsClosed.AddListener(OnSettingsClosed);
                }
            }
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
            mainMenuObject.SetActive(false);
            settingsMenuObject.SetActive(true);
        }

        /// <summary>
        /// Called by the SettingsMenuController's OnSettingsClosed event.
        /// Transitions back to the main menu.
        /// </summary>
        private void OnSettingsClosed()
        {
            settingsMenuObject.SetActive(false);
            mainMenuObject.SetActive(true);
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
    }
}
