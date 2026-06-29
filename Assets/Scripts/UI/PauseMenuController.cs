using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UI
{
    public class PauseMenuController : MonoBehaviour
    {
        [Header("Menu Objects")]
        public GameObject pauseMenuPanel;

        public GameObject settingsMenuObject;
        public GameObject helpMenuObject;

        private SettingsMenuController _settingsController;
        private HelpMenuController _helpController;

        private void Awake()
        {
            // Check for null references
            if (pauseMenuPanel == null) Debug.LogError("PauseMenuPanel is not assigned.");
            if (settingsMenuObject == null) Debug.LogError("SettingsMenuObject is not assigned.");
            if (helpMenuObject == null) Debug.LogError("HelpMenuObject is not assigned.");

            // Initialize settings controller
            if (settingsMenuObject != null)
            {
                _settingsController = settingsMenuObject.GetComponent<SettingsMenuController>();
                if (_settingsController != null)
                {
                    _settingsController.onSettingsClosed.AddListener(OnSettingsClosed);
                }
            }

            // Initialize help controller
            if (helpMenuObject != null)
            {
                _helpController = helpMenuObject.GetComponent<HelpMenuController>();
                if (_helpController != null)
                {
                    _helpController.onHelpClosed.AddListener(OnHelpClosed);
                }
            }
        }

        #region UI Panel Controls (Called by WorldUIManager)

        /// <summary>
        /// Activates the pause menu visual panel.
        /// </summary>
        public void OpenPausePanel()
        {
            if (pauseMenuPanel != null)
                pauseMenuPanel.SetActive(true);
        }

        /// <summary>
        /// Deactivates all pause-related UI panels.
        /// </summary>
        public void ClosePausePanel()
        {
            if (pauseMenuPanel != null)
                pauseMenuPanel.SetActive(false);

            if (settingsMenuObject != null)
                settingsMenuObject.SetActive(false);

            if (helpMenuObject != null)
                helpMenuObject.SetActive(false);
        }

        #endregion

        #region Button Callbacks

        /// <summary>
        /// Resumes the game by closing the pause menu via the UI Manager.
        /// </summary>
        public void Resume()
        {
            WorldUIManager.Instance.IsPauseMenuOpen = false;
        }

        /// <summary>
        /// Transitions from the pause panel to the settings menu.
        /// </summary>
        public void EnterSettings()
        {
            // Disable the pause menu
            if (pauseMenuPanel != null)
                pauseMenuPanel.SetActive(false);

            // Enable the settings menu in 'in-game' mode
            if (settingsMenuObject != null)
            {
                if (_settingsController != null)
                    _settingsController.IsInGame = true;

                settingsMenuObject.SetActive(true);
            }
        }

        /// <summary>
        /// Transitions from the pause panel to the help menu.
        /// </summary>
        public void EnterHelp()
        {
            if (pauseMenuPanel != null)
                pauseMenuPanel.SetActive(false);

            if (helpMenuObject != null)
                helpMenuObject.SetActive(true);
        }

        /// <summary>
        /// Triggered when the Help menu is closed. Re-opens the pause panel.
        /// </summary>
        private void OnHelpClosed()
        {
            if (helpMenuObject != null)
                helpMenuObject.SetActive(false);

            if (pauseMenuPanel != null)
                pauseMenuPanel.SetActive(true);
        }

        /// <summary>
        /// Triggered when the Settings menu is closed. Re-opens the pause panel.
        /// </summary>
        private void OnSettingsClosed()
        {
            if (settingsMenuObject != null)
                settingsMenuObject.SetActive(false);

            if (pauseMenuPanel != null)
                pauseMenuPanel.SetActive(true);

            // Reload settings to apply any changes made
            World.Instance.settings = SettingsManager.LoadSettings();
            World.Instance.OnSettingsChanged();
        }

        /// <summary>
        /// Saves world data and returns to the main menu scene.
        /// </summary>
        public void SaveAndQuitToMainMenu()
        {
            World.Instance.SaveWorldData();
            SceneManager.LoadScene("MainMenu");
        }

        /// <summary>
        /// Saves world data and exits the application.
        /// </summary>
        public void SaveAndQuitToDesktop()
        {
            World.Instance.SaveWorldData();
#if UNITY_EDITOR
            EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        #endregion
    }
}
