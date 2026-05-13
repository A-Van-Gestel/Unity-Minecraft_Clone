using UnityEngine;

namespace UI
{
    /// <summary>
    /// Centralized manager for in-game UI states and transitions.
    /// Handles cursor locking, inventory toggling, and the pause menu activation.
    /// </summary>
    public class WorldUIManager : MonoBehaviour
    {
        public static WorldUIManager Instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            Instance = null;
        }

        #region Serialized Fields

        [Header("Menu References")]
        public GameObject creativeInventoryWindow;

        public GameObject cursorSlot;
        public PauseMenuController pauseMenuController;

        #endregion

        #region Properties

        /// <summary>
        /// Gets whether the game is currently in any UI state.
        /// </summary>
        public bool InUI { get; private set; }

        /// <summary>
        /// Gets or sets whether the creative inventory is currently open.
        /// </summary>
        public bool IsCreativeInventoryOpen
        {
            get => creativeInventoryWindow != null && creativeInventoryWindow.activeSelf;
            set
            {
                if (creativeInventoryWindow != null) creativeInventoryWindow.SetActive(value);
                if (cursorSlot != null) cursorSlot.SetActive(value);
                UpdateUIState();
            }
        }

        /// <summary>
        /// Gets or sets whether the pause menu is currently open.
        /// </summary>
        public bool IsPauseMenuOpen
        {
            get => _isPauseMenuOpen;
            set
            {
                _isPauseMenuOpen = value;
                if (pauseMenuController != null)
                {
                    if (_isPauseMenuOpen)
                        pauseMenuController.OpenPausePanel();
                    else
                        pauseMenuController.ClosePausePanel();
                }

                UpdateUIState();
            }
        }

        private bool _isPauseMenuOpen;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                UpdateUIState();

                // Check for null references
                if (creativeInventoryWindow == null) Debug.LogError("CreativeInventoryWindow is not assigned.");
                if (cursorSlot == null) Debug.LogError("CursorSlot is not assigned.");
                if (pauseMenuController == null) Debug.LogError("PauseMenuController is not assigned.");
            }
            else
            {
                Destroy(gameObject);
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            // Handle Escape key logic
            if (InputManager.Instance.EscapePressed)
            {
                HandleEscape();
            }

            // Handle Inventory toggle logic
            if (InputManager.Instance.ToggleInventoryPressed && !IsPauseMenuOpen)
            {
                IsCreativeInventoryOpen = !IsCreativeInventoryOpen;
            }
        }

        #endregion

        #region Logic

        private void HandleEscape()
        {
            // 1. If settings is open, return to Pause Menu
            if (pauseMenuController != null && pauseMenuController.settingsMenuObject != null && pauseMenuController.settingsMenuObject.activeSelf)
            {
                SettingsMenuController settingsController = pauseMenuController.settingsMenuObject.GetComponent<SettingsMenuController>();
                if (settingsController != null)
                    settingsController.OnDoneClicked();
            }
            // 2. If Pause Menu is open, close it
            else if (IsPauseMenuOpen)
            {
                IsPauseMenuOpen = false;
            }
            // 3. Otherwise, open Pause Menu
            else
            {
                IsPauseMenuOpen = true;
            }
        }

        private void UpdateUIState()
        {
            InUI = IsCreativeInventoryOpen || IsPauseMenuOpen;

            Cursor.lockState = InUI
                ? CursorLockMode.None // Makes cursor visible
                : CursorLockMode.Locked; // Makes cursor invisible and not able to go of screen

            // Toggle UI based on inUI state
            Cursor.visible = InUI;
        }

        #endregion
    }
}
