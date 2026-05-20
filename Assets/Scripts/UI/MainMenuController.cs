using System;
using Data;
using Data.Enums;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

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
        public GameObject creditsMenuObject;

        #endregion

        #region Main Menu UI Elements

        [Header("Main Menu UI Elements")]
        public TextMeshProUGUI versionField;

        #endregion

        #endregion


        public void Awake()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

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
                SettingsMenuController settingsController = settingsMenuObject.GetComponent<SettingsMenuController>();
                if (settingsController != null)
                {
                    settingsController.onSettingsClosed.AddListener(OnSettingsClosed);
                }
            }

            // Wire up the CreditsMenuController's OnCreditsClosed callback
            if (creditsMenuObject != null)
            {
                CreditsMenuController creditsController = creditsMenuObject.GetComponent<CreditsMenuController>();
                if (creditsController != null)
                {
                    creditsController.onCreditsClosed.AddListener(OnCreditsClosed);
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
        /// Transitions from the main menu to the credits screen.
        /// </summary>
        public void EnterCredits()
        {
            mainMenuObject.SetActive(false);
            creditsMenuObject.SetActive(true);
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

        /// <summary>
        /// Called by the CreditsMenuController's OnCreditsClosed event.
        /// Transitions back to the main menu.
        /// </summary>
        private void OnCreditsClosed()
        {
            creditsMenuObject.SetActive(false);
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

        /// <summary>
        /// Automatically configures and launches a benchmark profiling session.
        /// Bypasses the World Select Menu.
        /// </summary>
        public void RunBenchmark()
        {
            WorldLaunchState.CurrentMode = RuntimeMode.Benchmark;
            WorldLaunchState.WorldName = $"Benchmark_{DateTime.Now:yyyyMMdd_HHmmss}";
            WorldLaunchState.Seed = 0; // Deterministic seed
            WorldLaunchState.IsNewGame = true;

            SceneManager.LoadScene("Scenes/World", LoadSceneMode.Single);
        }
    }
}
