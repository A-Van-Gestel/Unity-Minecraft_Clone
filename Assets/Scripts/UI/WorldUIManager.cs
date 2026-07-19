using Commands;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

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

        /// <summary>The runtime-spawned console view (CMD-1). Built in <see cref="Awake"/> — no scene object involved.</summary>
        private ConsoleUI _console;

        /// <summary>
        /// Gets or sets whether the command console is open. Opening disables the Gameplay action
        /// map (so typing cannot trigger hotbar/toggles) and leaves the UI map driving Esc/↑/↓;
        /// closing restores both maps. Cursor/InUI follow via <see cref="UpdateUIState"/>.
        /// </summary>
        public bool IsConsoleOpen
        {
            get => _console != null && _console.IsOpen;
            set
            {
                if (_console == null || value == _console.IsOpen)
                {
                    // UI_BUGS #04 diagnostic — remove with the #04 instrumentation.
                    Debug.Log($"[UIBUG04] IsConsoleOpen={value} ignored ({(_console == null ? "console is null" : "already in that state")}). {DiagUIBug04Snapshot()}");
                    return;
                }

                if (value)
                {
                    _console.Open();
                    InputManager.Instance.EnableUI();
                }
                else
                {
                    _console.Close();
                    InputManager.Instance.EnableAll();
                }

                UpdateUIState();
                // UI_BUGS #04 diagnostic — remove with the #04 instrumentation.
                Debug.Log($"[UIBUG04] IsConsoleOpen -> {value}. {DiagUIBug04Snapshot()}");
            }
        }

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

                // Spawn the command console view (runtime-built UI — TouchControls precedent, no scene edit).
                GameObject consoleObj = new GameObject("Console");
                consoleObj.transform.SetParent(transform, false);
                _console = consoleObj.AddComponent<ConsoleUI>();
            }
            else
            {
                Destroy(gameObject);
            }
        }

        private void Start()
        {
            // Attach the console's world facade (§4.1 CMD-2). This cannot happen in Awake: the engine
            // is built when the console spawns above, and World.Instance is only reliably assigned
            // once every scene Awake has run. Start is the earliest guaranteed-safe point. Skipped
            // when no world exists (e.g. UI-only scenes) — world-touching commands then fail
            // gracefully with their no-world error.
            if (World.Instance != null)
            {
                _console.Engine.Context.AttachWorld(World.Instance, World.Instance.player);
                World.Instance.TeleportHoldEnded += OnTeleportHoldEnded;
            }

            // Registered even without a world: /help stays consistent, and world-touching commands
            // report "No world is loaded." through their null-facade guard (§4.1). The installer is
            // the shared production/suite registration list (§8.1.1).
            ConsoleCommandInstaller.RegisterAll(_console.Engine.Registry);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (World.Instance != null)
                World.Instance.TeleportHoldEnded -= OnTeleportHoldEnded;
        }

        /// <summary>Surfaces the teleport arrival-hold outcome (§3.3 CMD-2) in the console.</summary>
        /// <param name="timedOut">True when the fail-safe timeout released the hold.</param>
        private void OnTeleportHoldEnded(bool timedOut)
        {
            if (timedOut)
                _console.Engine.PostLine(ConsoleLineSeverity.Warning,
                    "Teleport hold timed out — the destination never became ready; you may fall.");
            else
                _console.Engine.PostLine(ConsoleLineSeverity.Info, "Arrived.");
        }

        private void Update()
        {
            // UI_BUGS #04 diagnostic: raw map-independent T probe — catches presses that
            // ToggleConsolePressed would swallow (disabled Gameplay map) or the !InUI guard
            // would eat. Remove with the #04 instrumentation.
            bool diagRawT = InputManager.Instance.DiagnosticRawKeyPressed(Key.T);

            // Console-open state: the Gameplay map is disabled, so only the UI map's Cancel (Esc)
            // is live — it closes the console and nothing else runs (Esc chain head, §4.2 CMD-1).
            if (IsConsoleOpen)
            {
                if (InputManager.Instance.ConsoleCancelPressed)
                    IsConsoleOpen = false;
                return;
            }

            // UI_BUGS #04 diagnostic: this is the failure-moment capture — a T press while the
            // console believes it is closed. Remove with the #04 instrumentation.
            if (diagRawT)
                Debug.Log($"[UIBUG04] Raw T while console closed: ToggleConsolePressed={InputManager.Instance.ToggleConsolePressed}. {DiagUIBug04Snapshot()}");

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

            // Handle Console open logic (T). Open-only: while open, T types into the field.
            if (InputManager.Instance.ToggleConsolePressed && !InUI)
            {
                IsConsoleOpen = true;
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
            // 2. If help menu is open, return to Pause Menu
            else if (pauseMenuController != null && pauseMenuController.helpMenuObject != null && pauseMenuController.helpMenuObject.activeSelf)
            {
                HelpMenuController helpController = pauseMenuController.helpMenuObject.GetComponent<HelpMenuController>();
                if (helpController != null)
                    helpController.OnDoneClicked();
            }
            // 3. If Pause Menu is open, close it
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

        /// <summary>
        /// UI_BUGS #04 diagnostic: one-line snapshot of every state the console-toggle path
        /// depends on. Remove with the #04 instrumentation.
        /// </summary>
        /// <returns>A log-friendly summary of InUI, menu states, action maps, console panel/field state, and EventSystem selection.</returns>
        public string DiagUIBug04Snapshot()
        {
            string consoleState = _console == null
                ? "console=null"
                : $"consoleGoActive={_console.gameObject.activeInHierarchy}, {_console.DiagUIBug04State()}";
            GameObject selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            return $"InUI={InUI}, inventoryOpen={IsCreativeInventoryOpen}, pauseOpen={_isPauseMenuOpen}, " +
                   $"gameplayMap={InputManager.Instance.DiagnosticGameplayMapEnabled}, uiMap={InputManager.Instance.DiagnosticUIMapEnabled}, " +
                   $"{consoleState}, selected={(selected != null ? selected.name : "none")}, frame={Time.frameCount}";
        }

        private void UpdateUIState()
        {
            InUI = IsCreativeInventoryOpen || IsPauseMenuOpen || IsConsoleOpen;

            Cursor.lockState = InUI
                ? CursorLockMode.None // Makes cursor visible
                : CursorLockMode.Locked; // Makes cursor invisible and not able to go of screen

            // Toggle UI based on inUI state
            Cursor.visible = InUI;
        }

        #endregion
    }
}
