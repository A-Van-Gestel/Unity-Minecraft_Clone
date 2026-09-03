using System.Collections;
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

        /// <summary>
        /// Seconds the audio is faded down over before the world scene is torn down.
        /// </summary>
        /// <remarks>
        /// Short by design: this is a cut being softened, not a transition being staged. Every sounding
        /// layer is destroyed with the scene, so the fade cannot be one the player waits through — it only
        /// has to be long enough that the ear reads an ending rather than a dropout, and it doubles as cover
        /// for the save hitch that follows it.
        /// </remarks>
        private const float QUIT_FADE_SECONDS = 0.4f;

        private SettingsMenuController _settingsController;
        private HelpMenuController _helpController;

        /// <summary>Whether a quit is already in progress, so a second click cannot start another.</summary>
        private bool _quitting;

        /// <summary>
        /// Whether the quit fade is running, and the world is therefore on its way out.
        /// </summary>
        /// <remarks>
        /// Read by <see cref="WorldUIManager"/> so Escape cannot resume a world that is already saving:
        /// the fade outlives the pause panel, because this component does not live on it.
        /// </remarks>
        public bool IsQuitting => _quitting;

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
        /// Fades the audio out, saves world data, and returns to the main menu scene.
        /// </summary>
        public void SaveAndQuitToMainMenu()
        {
            if (_quitting) return;

            _quitting = true;
            StartCoroutine(FadeOutAndQuit(true));
        }

        /// <summary>
        /// Fades the audio out, saves world data, and exits the application.
        /// </summary>
        public void SaveAndQuitToDesktop()
        {
            if (_quitting) return;

            _quitting = true;
            StartCoroutine(FadeOutAndQuit(false));
        }

        /// <summary>
        /// Fades every sounding layer down together, then saves and leaves the world.
        /// </summary>
        /// <param name="toMainMenu">True to load the main menu; false to exit the application.</param>
        /// <returns>The coroutine enumerator.</returns>
        /// <remarks>
        /// <para>
        /// The listener rather than the individual layers: music, beds and emitters each own their own
        /// fades, but none of them survives the scene teardown that follows, so the only lever that can
        /// carry all of them off together is the one above them all.
        /// </para>
        /// <para>
        /// <b>Unscaled time</b>, because the pause menu this runs from may be holding the game still. The
        /// fade is deliberately <b>not</b> undone here: <see cref="AudioListener.volume"/> is global state
        /// that outlives the scene, and restoring it before the teardown it covers puts every still-live
        /// source back to full for the rest of the frame. <c>AudioSettingsController.Start</c> owns raising
        /// it again, on entry to whichever scene comes next.
        /// </para>
        /// </remarks>
        private IEnumerator FadeOutAndQuit(bool toMainMenu)
        {
            float startVolume = AudioListener.volume;

            for (float elapsed = 0f; elapsed < QUIT_FADE_SECONDS; elapsed += Time.unscaledDeltaTime)
            {
                AudioListener.volume = Mathf.Lerp(startVolume, 0f, elapsed / QUIT_FADE_SECONDS);
                yield return null;
            }

            AudioListener.volume = 0f;

            World.Instance.SaveWorldData();

            if (toMainMenu)
            {
                SceneManager.LoadScene("MainMenu");
                yield break;
            }

#if UNITY_EDITOR
            EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        #endregion
    }
}
