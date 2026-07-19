using Data;
using Input;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Centralized singleton that wraps the Input System's InputActionAsset.
/// All game scripts read input state from this manager instead of calling
/// <c>UnityEngine.Input</c> directly.
/// </summary>
/// <remarks>
/// Attach this component to a persistent GameObject in the scene and assign
/// the <c>GameInputActions</c> asset in the Inspector. Call
/// <see cref="EnableGameplay"/> or <see cref="EnableUI"/> to switch
/// which action map is active.
/// </remarks>
public class InputManager : MonoBehaviour
{
    /// <summary>Singleton instance, set in <see cref="Awake"/>.</summary>
    public static InputManager Instance { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void DomainReset()
    {
        Instance = null;
    }

    [Header("Input Actions Asset")]
    [Tooltip("Drag the GameInputActions asset here.")]
    [SerializeField]
    private InputActionAsset _inputActions;

    // ──────────────────────────────────────────────
    //  Cached Action Maps
    // ──────────────────────────────────────────────

    private InputActionMap _gameplayMap;
    private InputActionMap _uiMap;

    // ──────────────────────────────────────────────
    //  Cached Gameplay Actions
    // ──────────────────────────────────────────────

    private InputAction _moveAction;
    private InputAction _lookAction;
    private InputAction _jumpAction;
    private InputAction _crouchAction;
    private InputAction _sprintAction;
    private InputAction _attackAction;
    private InputAction _useAction;
    private InputAction _scrollAction;
    private InputAction _altModifierAction;
    private InputAction _toggleInventoryAction;
    private InputAction _escapeAction;
    private InputAction _toggleBlockHighlightAction;
    private InputAction _toggleDebugScreenAction;
    private InputAction _saveWorldAction;
    private InputAction _toggleChunkBordersAction;
    private InputAction _toggleFlyingAction;
    private InputAction _cycleVisModeAction;
    private InputAction _debugCodeAction;
    private InputAction _toggleNoclipAction;
    private InputAction _toggleConsoleAction;
    private InputAction _hotbar1Action;
    private InputAction _hotbar2Action;
    private InputAction _hotbar3Action;
    private InputAction _hotbar4Action;
    private InputAction _hotbar5Action;
    private InputAction _hotbar6Action;
    private InputAction _hotbar7Action;
    private InputAction _hotbar8Action;
    private InputAction _hotbar9Action;

    // ──────────────────────────────────────────────
    //  Cached UI Actions
    // ──────────────────────────────────────────────

    private InputAction _pointAction;
    private InputAction _uiClickAction;
    private InputAction _cancelAction;
    private InputAction _historyUpAction;
    private InputAction _historyDownAction;
    private InputAction _autocompleteAction;
    private InputAction _acceptSuggestionAction;

    // ──────────────────────────────────────────────
    //  Hotbar array (indexed 0–8 for slots 1–9)
    // ──────────────────────────────────────────────

    private InputAction[] _hotbarActions;

    // ==============================================
    //  PUBLIC READ-ONLY PROPERTIES — Gameplay
    // ==============================================

    #region Gameplay Properties

    /// <summary>WASD / stick movement as a normalized Vector2. Composited with touch joystick on mobile.</summary>
    public Vector2 MoveInput
    {
        get
        {
            Vector2 result = _moveAction.ReadValue<Vector2>();
            if (TouchControls.Instance != null)
                result += TouchControls.Instance.MoveInput;
            return Vector2.ClampMagnitude(result, 1f);
        }
    }

    /// <summary>
    /// Normalization factor to convert raw <c>Mouse.delta</c> pixel values to
    /// values equivalent to the old <c>Input.GetAxis("Mouse X/Y")</c>.
    /// The legacy Input Manager applied a default sensitivity of 0.1.
    /// </summary>
    private const float MOUSE_DELTA_SCALE = 0.1f;

    /// <summary>Mouse delta / right-stick look, scaled to match legacy Input Manager sensitivity. Composited with touch look on mobile.</summary>
    public Vector2 LookInput
    {
        get
        {
            Vector2 result = _lookAction.ReadValue<Vector2>() * MOUSE_DELTA_SCALE;
            if (TouchControls.Instance != null)
                result += TouchControls.Instance.LookDelta;
            return result;
        }
    }

    /// <summary><c>true</c> during the frame the Jump button was first pressed.</summary>
    public bool JumpPressed => _jumpAction.WasPressedThisFrame()
                               || (TouchControls.Instance != null && TouchControls.Instance.JumpPressed);

    /// <summary><c>true</c> while the Jump button is held down.</summary>
    public bool JumpHeld => _jumpAction.IsPressed()
                            || (TouchControls.Instance != null && TouchControls.Instance.JumpHeld);

    /// <summary>Analog jump value (0 or 1 for digital bindings).</summary>
    public float JumpValue => Mathf.Max(_jumpAction.ReadValue<float>(),
        TouchControls.Instance != null ? TouchControls.Instance.JumpValue : 0f);

    /// <summary>Analog crouch value (0 or 1 for digital bindings).</summary>
    public float CrouchValue => Mathf.Max(_crouchAction.ReadValue<float>(),
        TouchControls.Instance != null ? TouchControls.Instance.CrouchValue : 0f);

    /// <summary><c>true</c> during the frame the Sprint button was first pressed.</summary>
    public bool SprintPressed => _sprintAction.WasPressedThisFrame();

    /// <summary><c>true</c> during the frame the Sprint button was released.</summary>
    public bool SprintReleased => _sprintAction.WasReleasedThisFrame();

    /// <summary><c>true</c> during the frame the Attack (LMB) button was first pressed.</summary>
    public bool AttackPressed => _attackAction.WasPressedThisFrame()
                                 || (TouchControls.Instance != null && TouchControls.Instance.AttackPressed);

    /// <summary><c>true</c> during the frame the Use (RMB) button was first pressed.</summary>
    public bool UsePressed => _useAction.WasPressedThisFrame()
                              || (TouchControls.Instance != null && TouchControls.Instance.UsePressed);

    /// <summary>Mouse scroll-wheel delta on the Y axis.</summary>
    public float ScrollValue => _scrollAction.ReadValue<Vector2>().y;

    /// <summary><c>true</c> while the Alt modifier key is held.</summary>
    public bool AltModifierHeld => _altModifierAction.IsPressed();

    /// <summary><c>true</c> during the frame the Toggle Inventory button was pressed.</summary>
    public bool ToggleInventoryPressed => _toggleInventoryAction.WasPressedThisFrame()
                                          || (TouchControls.Instance != null && TouchControls.Instance.ToggleInventoryPressed);

    /// <summary><c>true</c> during the frame the Escape button was pressed.</summary>
    public bool EscapePressed => _escapeAction.WasPressedThisFrame()
                                 || (TouchControls.Instance != null && TouchControls.Instance.EscapePressed);

    /// <summary><c>true</c> during the frame the Toggle Block Highlight button was pressed.</summary>
    public bool ToggleBlockHighlightPressed => _toggleBlockHighlightAction.WasPressedThisFrame();

    /// <summary><c>true</c> during the frame the Toggle Debug Screen button was pressed.</summary>
    public bool ToggleDebugScreenPressed => _toggleDebugScreenAction.WasPressedThisFrame()
                                            || (TouchControls.Instance != null && TouchControls.Instance.ToggleDebugPressed);

    /// <summary><c>true</c> during the frame the Save World button was pressed.</summary>
    public bool SaveWorldPressed => _saveWorldAction.WasPressedThisFrame();

    /// <summary><c>true</c> during the frame the Toggle Chunk Borders button was pressed.</summary>
    public bool ToggleChunkBordersPressed => _toggleChunkBordersAction.WasPressedThisFrame();

    /// <summary><c>true</c> during the frame the Toggle Flying button was pressed.</summary>
    public bool ToggleFlyingPressed => _toggleFlyingAction.WasPressedThisFrame()
                                       || (TouchControls.Instance != null && TouchControls.Instance.ToggleFlyingPressed);

    /// <summary><c>true</c> during the frame the Cycle Visualization Mode button was pressed.</summary>
    public bool CycleVisModePressed => _cycleVisModeAction.WasPressedThisFrame();

    /// <summary><c>true</c> during the frame the Debug Code button was pressed.</summary>
    public bool DebugCodePressed => _debugCodeAction.WasPressedThisFrame();

    /// <summary><c>true</c> during the frame the Toggle Noclip button was pressed.</summary>
    public bool ToggleNoclipPressed => _toggleNoclipAction.WasPressedThisFrame()
                                       || (TouchControls.Instance != null && TouchControls.Instance.ToggleNoclipPressed);

    /// <summary><c>true</c> during the frame the Toggle Console button (T) was pressed.</summary>
    public bool ToggleConsolePressed => _toggleConsoleAction.WasPressedThisFrame();

    #endregion

    // ==============================================
    //  PUBLIC READ-ONLY PROPERTIES — UI
    // ==============================================

    #region UI Properties

    /// <summary>Screen-space mouse / pointer position.</summary>
    public Vector2 MousePosition => _pointAction.ReadValue<Vector2>();

    /// <summary><c>true</c> during the frame the left mouse button was first pressed (UI context).</summary>
    public bool UIClickPressed => _uiClickAction.WasPressedThisFrame();

    /// <summary><c>true</c> during the frame Escape was pressed on the UI map (the console's close path while the Gameplay map is disabled).</summary>
    public bool ConsoleCancelPressed => _cancelAction.WasPressedThisFrame();

    /// <summary><c>true</c> during the frame ↑ was pressed on the UI map (console history recall).</summary>
    public bool ConsoleHistoryUpPressed => _historyUpAction.WasPressedThisFrame();

    /// <summary><c>true</c> during the frame ↓ was pressed on the UI map (console history recall).</summary>
    public bool ConsoleHistoryDownPressed => _historyDownAction.WasPressedThisFrame();

    /// <summary><c>true</c> during the frame Tab was pressed on the UI map (console Tab autocomplete).</summary>
    public bool ConsoleAutocompletePressed => _autocompleteAction.WasPressedThisFrame();

    /// <summary><c>true</c> during the frame RightArrow/End was pressed on the UI map (accept the console's inline suggestion).</summary>
    public bool ConsoleAcceptSuggestionPressed => _acceptSuggestionAction.WasPressedThisFrame();

    #endregion

    // ==============================================
    //  PUBLIC METHODS — Hotbar
    // ==============================================

    /// <summary>
    /// Returns <c>true</c> if the hotbar key for the given slot index (0–8) was pressed this frame.
    /// </summary>
    /// <param name="index">Slot index from 0 (key "1") to 8 (key "9").</param>
    /// <returns><c>true</c> the frame the key was first pressed.</returns>
    public bool HotbarPressed(int index)
    {
        if (index < 0 || index >= _hotbarActions.Length)
            return false;

        return _hotbarActions[index].WasPressedThisFrame();
    }

    // ==============================================
    //  PUBLIC METHODS — Debug/Benchmark Trigger Keys
    // ==============================================

    /// <summary>
    /// Gameplay-gated raw key check for configurable debug/benchmark trigger keys
    /// (<c>[SerializeField] Key</c> fields that would be overkill as InputActions).
    /// True the frame <paramref name="key"/> was pressed, but only while the Gameplay map is
    /// enabled — so UI states that suppress gameplay input (the command console, via
    /// <c>EnableUI</c>) suppress these keys too. Never read <c>Keyboard.current</c> directly
    /// outside this class; the Command Console suite's bypass tripwire fails on it.
    /// </summary>
    /// <param name="key">The keyboard key to test.</param>
    /// <returns>True when pressed this frame and gameplay input is active.</returns>
    public bool DebugKeyPressed(Key key)
    {
        return _gameplayMap != null && _gameplayMap.enabled
                                    && Keyboard.current != null && Keyboard.current[key].wasPressedThisFrame;
    }

    // ==============================================
    //  PUBLIC METHODS — Action Map Switching
    // ==============================================

    /// <summary>
    /// Enables the Gameplay action map and disables the UI action map.
    /// </summary>
    public void EnableGameplay()
    {
        _uiMap.Disable();
        _gameplayMap.Enable();
    }

    /// <summary>
    /// Enables the UI action map and disables the Gameplay action map.
    /// </summary>
    public void EnableUI()
    {
        _gameplayMap.Disable();
        _uiMap.Enable();
    }

    /// <summary>
    /// Enables both Gameplay and UI action maps simultaneously.
    /// Used when the game needs UI input (mouse position, clicks) alongside gameplay input.
    /// </summary>
    public void EnableAll()
    {
        _gameplayMap.Enable();
        _uiMap.Enable();
    }

    // ==============================================
    //  BINDING DISPLAY STRINGS
    // ==============================================

    /// <summary>
    /// Returns a human-readable display string for the binding of any action.
    /// For example, <c>GetBindingDisplayString(GameAction.ToggleFlying)</c> returns <c>"F1"</c>.
    /// </summary>
    /// <param name="action">The action to get the binding display string for.</param>
    /// <returns>The display string for the first binding of the action, or <c>"?"</c> if not found.</returns>
    public string GetBindingDisplayString(GameAction action)
    {
        InputAction inputAction = _inputActions.FindAction(action.ToString(), false);
        return inputAction != null ? inputAction.GetBindingDisplayString(options: InputBinding.DisplayStringOptions.DontUseShortDisplayNames) : "?";
    }

    // ==============================================
    //  UNITY LIFECYCLE
    // ==============================================

    private void Awake()
    {
        // --- Singleton ---
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        // --- Resolve Action Maps ---
        _gameplayMap = _inputActions.FindActionMap("Gameplay", throwIfNotFound: true);
        _uiMap = _inputActions.FindActionMap("UI", throwIfNotFound: true);

        // --- Cache Gameplay Actions ---
        _moveAction = _gameplayMap.FindAction("Move", throwIfNotFound: true);
        _lookAction = _gameplayMap.FindAction("Look", throwIfNotFound: true);
        _jumpAction = _gameplayMap.FindAction("Jump", throwIfNotFound: true);
        _crouchAction = _gameplayMap.FindAction("Crouch", throwIfNotFound: true);
        _sprintAction = _gameplayMap.FindAction("Sprint", throwIfNotFound: true);
        _attackAction = _gameplayMap.FindAction("Attack", throwIfNotFound: true);
        _useAction = _gameplayMap.FindAction("Use", throwIfNotFound: true);
        _scrollAction = _gameplayMap.FindAction("Scroll", throwIfNotFound: true);
        _altModifierAction = _gameplayMap.FindAction("AltModifier", throwIfNotFound: true);
        _toggleInventoryAction = _gameplayMap.FindAction("ToggleInventory", throwIfNotFound: true);
        _escapeAction = _gameplayMap.FindAction("Escape", throwIfNotFound: true);
        _toggleBlockHighlightAction = _gameplayMap.FindAction("ToggleBlockHighlight", throwIfNotFound: true);
        _toggleDebugScreenAction = _gameplayMap.FindAction("ToggleDebugScreen", throwIfNotFound: true);
        _saveWorldAction = _gameplayMap.FindAction("SaveWorld", throwIfNotFound: true);
        _toggleChunkBordersAction = _gameplayMap.FindAction("ToggleChunkBorders", throwIfNotFound: true);
        _toggleFlyingAction = _gameplayMap.FindAction("ToggleFlying", throwIfNotFound: true);
        _cycleVisModeAction = _gameplayMap.FindAction("CycleVisMode", throwIfNotFound: true);
        _debugCodeAction = _gameplayMap.FindAction("DebugCode", throwIfNotFound: true);
        _toggleNoclipAction = _gameplayMap.FindAction("ToggleNoclip", throwIfNotFound: true);
        _toggleConsoleAction = _gameplayMap.FindAction("ToggleConsole", throwIfNotFound: true);

        _hotbar1Action = _gameplayMap.FindAction("Hotbar1", throwIfNotFound: true);
        _hotbar2Action = _gameplayMap.FindAction("Hotbar2", throwIfNotFound: true);
        _hotbar3Action = _gameplayMap.FindAction("Hotbar3", throwIfNotFound: true);
        _hotbar4Action = _gameplayMap.FindAction("Hotbar4", throwIfNotFound: true);
        _hotbar5Action = _gameplayMap.FindAction("Hotbar5", throwIfNotFound: true);
        _hotbar6Action = _gameplayMap.FindAction("Hotbar6", throwIfNotFound: true);
        _hotbar7Action = _gameplayMap.FindAction("Hotbar7", throwIfNotFound: true);
        _hotbar8Action = _gameplayMap.FindAction("Hotbar8", throwIfNotFound: true);
        _hotbar9Action = _gameplayMap.FindAction("Hotbar9", throwIfNotFound: true);

        _hotbarActions = new[]
        {
            _hotbar1Action, _hotbar2Action, _hotbar3Action,
            _hotbar4Action, _hotbar5Action, _hotbar6Action,
            _hotbar7Action, _hotbar8Action, _hotbar9Action,
        };

        // --- Cache UI Actions ---
        _pointAction = _uiMap.FindAction("Point", throwIfNotFound: true);
        _uiClickAction = _uiMap.FindAction("Click", throwIfNotFound: true);
        _cancelAction = _uiMap.FindAction("Cancel", throwIfNotFound: true);
        _historyUpAction = _uiMap.FindAction("HistoryUp", throwIfNotFound: true);
        _historyDownAction = _uiMap.FindAction("HistoryDown", throwIfNotFound: true);
        _autocompleteAction = _uiMap.FindAction("ConsoleAutocomplete", throwIfNotFound: true);
        _acceptSuggestionAction = _uiMap.FindAction("ConsoleAcceptSuggestion", throwIfNotFound: true);

        // --- Touch Controls (mobile only) ---
        if (Application.isMobilePlatform)
        {
            GameObject touchObj = new GameObject("TouchControls");
            touchObj.transform.SetParent(transform);
            touchObj.AddComponent<TouchControls>();
        }
    }

    private void OnEnable()
    {
        // Default: both maps enabled so gameplay + UI cursor both work.
        EnableAll();
    }

    private void OnDisable()
    {
        _gameplayMap?.Disable();
        _uiMap?.Disable();
    }
}
