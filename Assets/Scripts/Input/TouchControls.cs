using System.Collections.Generic;
using Data;
using Data.Enums;
using TMPro;
using UI;
using UnityEngine;
using UnityEngine.InputSystem.EnhancedTouch;
using UnityEngine.UI;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;

namespace Input
{
    /// <summary>
    /// On-screen touch controls for mobile platforms.
    /// Creates a Canvas overlay with a floating virtual joystick, action buttons (Break / Place / Jump),
    /// and a top-left debug button row (Fly / Noclip / Debug).
    /// Processes multi-touch input and exposes state for <see cref="InputManager"/> to composite.
    /// </summary>
    [DefaultExecutionOrder(-10)]
    public class TouchControls : MonoBehaviour
    {
        /// <summary>Singleton instance, created by <see cref="InputManager"/> on mobile platforms.</summary>
        public static TouchControls Instance { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            Instance = null;
        }

        // ═══════════════════════════════════════════
        //  CONSTANTS
        // ═══════════════════════════════════════════

        private const float JOYSTICK_ZONE_WIDTH_FRAC = 0.35f;
        private const float JOYSTICK_ZONE_HEIGHT_FRAC = 0.45f;

        private const float JOYSTICK_BG_SIZE = 200f;
        private const float JOYSTICK_KNOB_SIZE = 80f;
        private const float JOYSTICK_MAX_DRAG = 80f;

        private const float ACTION_BTN_SIZE = 100f;
        private const float ACTION_BTN_MARGIN = 30f;
        private const float ACTION_BTN_GAP = 20f;

        private const float TOP_BTN_HEIGHT = 48f;
        private const float TOP_BTN_WIDTH = 100f;
        private const float TOP_BTN_SPACING = 8f;
        private const float TOP_BTN_MARGIN = 12f;

        private const float TOUCH_LOOK_SENSITIVITY = 0.1f;

        // ═══════════════════════════════════════════
        //  INPUT STATE  (read by InputManager)
        // ═══════════════════════════════════════════

        /// <summary>Virtual joystick movement as a normalized Vector2.</summary>
        public Vector2 MoveInput { get; private set; }

        /// <summary>Camera look delta from touch drag, pre-scaled to match mouse delta units.</summary>
        public Vector2 LookDelta { get; private set; }

        /// <summary><c>true</c> during the frame the Break (Attack) button was first pressed.</summary>
        public bool AttackPressed => _attackBtn != null && _attackBtn.WasPressedThisFrame;

        /// <summary><c>true</c> during the frame the Place (Use) button was first pressed.</summary>
        public bool UsePressed => _useBtn != null && _useBtn.WasPressedThisFrame;

        /// <summary><c>true</c> during the frame the Jump button was first pressed.</summary>
        public bool JumpPressed => _jumpBtn != null && _jumpBtn.WasPressedThisFrame;

        /// <summary><c>true</c> while the Jump button is held down.</summary>
        public bool JumpHeld => _jumpBtn != null && _jumpBtn.IsPressed;

        /// <summary>Analog jump value (1 while held, 0 otherwise).</summary>
        public float JumpValue => _jumpBtn != null && _jumpBtn.IsPressed ? 1f : 0f;

        /// <summary><c>true</c> during the frame the Fly toggle button was pressed.</summary>
        public bool ToggleFlyingPressed => _flyBtn != null && _flyBtn.WasPressedThisFrame;

        /// <summary><c>true</c> during the frame the Noclip toggle button was pressed.</summary>
        public bool ToggleNoclipPressed => _noclipBtn != null && _noclipBtn.WasPressedThisFrame;

        /// <summary><c>true</c> during the frame the Debug toggle button was pressed.</summary>
        public bool ToggleDebugPressed => _debugBtn != null && _debugBtn.WasPressedThisFrame;

        /// <summary><c>true</c> during the frame the Inventory button was pressed.</summary>
        public bool ToggleInventoryPressed => _inventoryBtn != null && _inventoryBtn.WasPressedThisFrame;

        /// <summary><c>true</c> during the frame the Pause (Escape) button was pressed.</summary>
        public bool EscapePressed => _pauseBtn != null && _pauseBtn.WasPressedThisFrame;

        /// <summary><c>true</c> while the Crouch button is held down.</summary>
        public bool CrouchHeld => _crouchBtn != null && _crouchBtn.IsPressed;

        /// <summary>Analog crouch value (1 while held, 0 otherwise).</summary>
        public float CrouchValue => _crouchBtn != null && _crouchBtn.IsPressed ? 1f : 0f;

        // ═══════════════════════════════════════════
        //  TOUCH TRACKING
        // ═══════════════════════════════════════════

        private int _joystickFingerId = -1;
        private Vector2 _joystickOrigin;
        private Vector2 _joystickCurrent;

        private int _lookFingerId = -1;

        // ═══════════════════════════════════════════
        //  UI REFERENCES
        // ═══════════════════════════════════════════

        private Canvas _canvas;
        private GameObject _controlsRoot;
        private RectTransform _controlsRootRect;
        private GameObject _persistentRoot;
        private RectTransform _joystickBg;
        private RectTransform _joystickKnob;

        private TouchButton _attackBtn;
        private TouchButton _useBtn;
        private TouchButton _jumpBtn;
        private TouchButton _flyBtn;
        private TouchButton _noclipBtn;
        private TouchButton _debugBtn;
        private TouchButton _inventoryBtn;
        private TouchButton _pauseBtn;
        private TouchButton _crouchBtn;

        private readonly List<RectTransform> _allButtonRects = new List<RectTransform>();

        private Sprite _circleSprite;
        private Sprite _roundedRectSprite;

        // ═══════════════════════════════════════════
        //  LIFECYCLE
        // ═══════════════════════════════════════════

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;

            _circleSprite = CreateCircleSprite(64);
            _roundedRectSprite = CreateRoundedRectSprite(64, 64, 12);
            BuildUI();
        }

        private void OnEnable()
        {
            EnhancedTouchSupport.Enable();
        }

        private void OnDisable()
        {
            EnhancedTouchSupport.Disable();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;

            if (_circleSprite != null)
            {
                Destroy(_circleSprite.texture);
                Destroy(_circleSprite);
            }

            if (_roundedRectSprite != null)
            {
                Destroy(_roundedRectSprite.texture);
                Destroy(_roundedRectSprite);
            }
        }

        private void Update()
        {
            bool showPersistent = ShouldShowPersistentControls();
            if (_persistentRoot.activeSelf != showPersistent)
                _persistentRoot.SetActive(showPersistent);

            bool showGameplay = showPersistent && !World.InUI;
            if (_controlsRoot.activeSelf != showGameplay)
            {
                _controlsRoot.SetActive(showGameplay);
                if (!showGameplay) ResetTouchState();
            }

            if (!showGameplay) return;

            ProcessTouches();
        }

        /// <summary>
        /// Persistent controls (top button row) are visible whenever the player is
        /// in the world scene and not in a benchmark run — even while a UI overlay
        /// (inventory, pause) is open, so the user can dismiss it via touch.
        /// </summary>
        private static bool ShouldShowPersistentControls()
        {
            if (World.Instance == null) return false;
            return WorldLaunchState.CurrentMode != RuntimeMode.Benchmark;
        }

        // ═══════════════════════════════════════════
        //  TOUCH PROCESSING
        // ═══════════════════════════════════════════

        private void ProcessTouches()
        {
            Vector2 frameLookDelta = Vector2.zero;
            bool joystickFingerStillActive = false;
            bool lookFingerStillActive = false;

            var activeTouches = Touch.activeTouches;

            foreach (Touch touch in activeTouches)
            {
                int fingerId = touch.finger.index;
                Vector2 screenPos = touch.screenPosition;

                switch (touch.phase)
                {
                    case TouchPhase.Began:
                        if (IsOverAnyButton(screenPos))
                            break;

                        if (_joystickFingerId == -1 && IsInJoystickZone(screenPos))
                        {
                            _joystickFingerId = fingerId;
                            _joystickOrigin = screenPos;
                            _joystickCurrent = screenPos;
                            ShowJoystick(screenPos);
                            joystickFingerStillActive = true;
                        }
                        else if (_lookFingerId == -1)
                        {
                            _lookFingerId = fingerId;
                            lookFingerStillActive = true;
                        }

                        break;

                    case TouchPhase.Moved:
                    case TouchPhase.Stationary:
                        if (fingerId == _joystickFingerId)
                        {
                            _joystickCurrent = screenPos;
                            UpdateJoystickVisual();
                            joystickFingerStillActive = true;
                        }
                        else if (fingerId == _lookFingerId)
                        {
                            frameLookDelta += touch.delta;
                            lookFingerStillActive = true;
                        }

                        break;

                    case TouchPhase.Ended:
                    case TouchPhase.Canceled:
                        if (fingerId == _joystickFingerId)
                        {
                            _joystickFingerId = -1;
                            HideJoystick();
                        }
                        else if (fingerId == _lookFingerId)
                        {
                            _lookFingerId = -1;
                        }

                        break;
                }
            }

            if (_joystickFingerId != -1 && !joystickFingerStillActive)
            {
                _joystickFingerId = -1;
                HideJoystick();
            }

            if (_lookFingerId != -1 && !lookFingerStillActive)
                _lookFingerId = -1;

            // Joystick → movement
            if (_joystickFingerId != -1)
            {
                Vector2 offset = _joystickCurrent - _joystickOrigin;
                float maxDrag = JOYSTICK_MAX_DRAG * _canvas.scaleFactor;
                MoveInput = Vector2.ClampMagnitude(offset / maxDrag, 1f);
            }
            else
            {
                MoveInput = Vector2.zero;
            }

            // Look delta scaled to match MOUSE_DELTA_SCALE units
            LookDelta = frameLookDelta * TOUCH_LOOK_SENSITIVITY;
        }

        private bool IsInJoystickZone(Vector2 screenPos)
        {
            Rect safe = Screen.safeArea;
            return screenPos.x < safe.x + safe.width * JOYSTICK_ZONE_WIDTH_FRAC
                   && screenPos.y < safe.y + safe.height * JOYSTICK_ZONE_HEIGHT_FRAC;
        }

        private bool IsOverAnyButton(Vector2 screenPos)
        {
            foreach (RectTransform buttonRect in _allButtonRects)
            {
                if (RectTransformUtility.RectangleContainsScreenPoint(buttonRect, screenPos, null))
                    return true;
            }

            return false;
        }

        private void ResetTouchState()
        {
            _joystickFingerId = -1;
            _lookFingerId = -1;
            MoveInput = Vector2.zero;
            LookDelta = Vector2.zero;
            HideJoystick();
        }

        // ═══════════════════════════════════════════
        //  JOYSTICK VISUALS
        // ═══════════════════════════════════════════

        private void ShowJoystick(Vector2 screenPos)
        {
            _joystickBg.gameObject.SetActive(true);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _controlsRootRect, screenPos, null, out Vector2 localPos);
            _joystickBg.anchoredPosition = localPos;
            _joystickKnob.anchoredPosition = Vector2.zero;
        }

        private void UpdateJoystickVisual()
        {
            Vector2 offset = _joystickCurrent - _joystickOrigin;
            float maxDragScreen = JOYSTICK_MAX_DRAG * _canvas.scaleFactor;
            Vector2 clamped = Vector2.ClampMagnitude(offset, maxDragScreen);
            _joystickKnob.anchoredPosition = clamped / _canvas.scaleFactor;
        }

        private void HideJoystick()
        {
            if (_joystickBg != null)
                _joystickBg.gameObject.SetActive(false);
        }

        // ═══════════════════════════════════════════
        //  UI BUILDING
        // ═══════════════════════════════════════════

        private void BuildUI()
        {
            // --- Canvas ---
            GameObject canvasObj = new GameObject("TouchControlsCanvas");
            canvasObj.transform.SetParent(transform, false);
            _canvas = canvasObj.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 90;

            CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            canvasObj.AddComponent<GraphicRaycaster>();

            // --- Gameplay controls root (hidden during UI overlays) ---
            _controlsRoot = new GameObject("ControlsRoot");
            _controlsRoot.transform.SetParent(canvasObj.transform, false);
            _controlsRootRect = _controlsRoot.AddComponent<RectTransform>();
            StretchFull(_controlsRootRect);
            _controlsRoot.AddComponent<SafeArea>();

            BuildJoystick(_controlsRootRect);
            BuildActionButtons(_controlsRootRect);

            // --- Persistent controls root (visible even during UI overlays) ---
            _persistentRoot = new GameObject("PersistentRoot");
            _persistentRoot.transform.SetParent(canvasObj.transform, false);
            RectTransform persistentRect = _persistentRoot.AddComponent<RectTransform>();
            StretchFull(persistentRect);
            _persistentRoot.AddComponent<SafeArea>();

            BuildTopButtons(persistentRect);

            HideJoystick();
        }

        private void BuildJoystick(RectTransform parent)
        {
            // Background circle (hidden until touch)
            GameObject bgObj = new GameObject("JoystickBg");
            bgObj.transform.SetParent(parent, false);
            _joystickBg = bgObj.AddComponent<RectTransform>();
            _joystickBg.sizeDelta = new Vector2(JOYSTICK_BG_SIZE, JOYSTICK_BG_SIZE);

            Image bgImg = bgObj.AddComponent<Image>();
            bgImg.sprite = _circleSprite;
            bgImg.type = Image.Type.Simple;
            bgImg.color = new Color(1f, 1f, 1f, 0.12f);
            bgImg.raycastTarget = false;

            // Knob
            GameObject knobObj = new GameObject("JoystickKnob");
            knobObj.transform.SetParent(bgObj.transform, false);
            _joystickKnob = knobObj.AddComponent<RectTransform>();
            _joystickKnob.sizeDelta = new Vector2(JOYSTICK_KNOB_SIZE, JOYSTICK_KNOB_SIZE);

            Image knobImg = knobObj.AddComponent<Image>();
            knobImg.sprite = _circleSprite;
            knobImg.type = Image.Type.Simple;
            knobImg.color = new Color(1f, 1f, 1f, 0.35f);
            knobImg.raycastTarget = false;
        }

        private void BuildActionButtons(RectTransform parent)
        {
            // Right-side column: Attack (top), Use (middle), Jump + Crouch (bottom row)
            // Anchored to bottom-right, stacked upward from the bottom
            const float btnStep = ACTION_BTN_SIZE + ACTION_BTN_GAP;

            _crouchBtn = CreateActionButton(parent, "Crouch",
                new Vector2(-ACTION_BTN_MARGIN - ACTION_BTN_SIZE - ACTION_BTN_GAP, ACTION_BTN_MARGIN));

            _jumpBtn = CreateActionButton(parent, "Jump",
                new Vector2(-ACTION_BTN_MARGIN, ACTION_BTN_MARGIN));

            _useBtn = CreateActionButton(parent, "Place",
                new Vector2(-ACTION_BTN_MARGIN, ACTION_BTN_MARGIN + btnStep));

            _attackBtn = CreateActionButton(parent, "Break",
                new Vector2(-ACTION_BTN_MARGIN, ACTION_BTN_MARGIN + btnStep * 2f));
        }

        private TouchButton CreateActionButton(RectTransform parent, string label, Vector2 offset)
        {
            GameObject btnObj = new GameObject($"Btn_{label}");
            btnObj.transform.SetParent(parent, false);

            RectTransform rect = btnObj.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(1, 0);
            rect.anchorMax = new Vector2(1, 0);
            rect.pivot = new Vector2(1, 0);
            rect.sizeDelta = new Vector2(ACTION_BTN_SIZE, ACTION_BTN_SIZE);
            rect.anchoredPosition = offset;

            Image img = btnObj.AddComponent<Image>();
            img.sprite = _roundedRectSprite;
            img.type = Image.Type.Sliced;
            img.color = new Color(1f, 1f, 1f, 0.2f);

            AddLabel(btnObj.transform, label, 24);

            TouchButton btn = btnObj.AddComponent<TouchButton>();
            _allButtonRects.Add(rect);
            return btn;
        }

        private void BuildTopButtons(RectTransform parent)
        {
            float x = TOP_BTN_MARGIN;
            const float y = -TOP_BTN_MARGIN;

            _flyBtn = CreateTopButton(parent, "Fly", ref x, y);
            _noclipBtn = CreateTopButton(parent, "Noclip", ref x, y);
            _debugBtn = CreateTopButton(parent, "Debug", ref x, y);
            _inventoryBtn = CreateTopButton(parent, "Inv", ref x, y);
            _pauseBtn = CreateTopButton(parent, "Pause", ref x, y);
        }

        private TouchButton CreateTopButton(RectTransform parent, string label, ref float x, float y)
        {
            GameObject btnObj = new GameObject($"Btn_{label}");
            btnObj.transform.SetParent(parent, false);

            RectTransform rect = btnObj.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1);
            rect.sizeDelta = new Vector2(TOP_BTN_WIDTH, TOP_BTN_HEIGHT);
            rect.anchoredPosition = new Vector2(x, y);

            x += TOP_BTN_WIDTH + TOP_BTN_SPACING;

            Image img = btnObj.AddComponent<Image>();
            img.sprite = _roundedRectSprite;
            img.type = Image.Type.Sliced;
            img.color = new Color(0.15f, 0.15f, 0.15f, 0.55f);

            AddLabel(btnObj.transform, label, 20);

            TouchButton btn = btnObj.AddComponent<TouchButton>();
            _allButtonRects.Add(rect);
            return btn;
        }

        // ═══════════════════════════════════════════
        //  UI HELPERS
        // ═══════════════════════════════════════════

        private static void AddLabel(Transform parent, string text, int fontSize)
        {
            GameObject textObj = new GameObject("Label");
            textObj.transform.SetParent(parent, false);

            RectTransform rect = textObj.AddComponent<RectTransform>();
            StretchFull(rect);

            TextMeshProUGUI tmp = textObj.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = new Color(1f, 1f, 1f, 0.85f);
            tmp.raycastTarget = false;
        }

        private static void StretchFull(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        // ═══════════════════════════════════════════
        //  PROCEDURAL SPRITES
        // ═══════════════════════════════════════════

        private static Sprite CreateCircleSprite(int radius)
        {
            int size = radius * 2;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color32[] pixels = new Color32[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - radius + 0.5f;
                    float dy = y - radius + 0.5f;
                    float alpha = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy));
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255));
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply();
            tex.filterMode = FilterMode.Bilinear;
            return Sprite.Create(tex, new Rect(0, 0, size, size), Vector2.one * 0.5f);
        }

        private static Sprite CreateRoundedRectSprite(int width, int height, int cornerRadius)
        {
            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            Color32[] pixels = new Color32[width * height];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float dx = 0f, dy = 0f;
                    if (x < cornerRadius) dx = cornerRadius - x;
                    else if (x >= width - cornerRadius) dx = x - (width - cornerRadius - 1);
                    if (y < cornerRadius) dy = cornerRadius - y;
                    else if (y >= height - cornerRadius) dy = y - (height - cornerRadius - 1);

                    float alpha = (dx > 0f && dy > 0f)
                        ? Mathf.Clamp01(cornerRadius - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f)
                        : 1f;
                    pixels[y * width + x] = new Color32(255, 255, 255, (byte)(alpha * 255));
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply();
            tex.filterMode = FilterMode.Bilinear;

            float border = cornerRadius;
            return Sprite.Create(tex, new Rect(0, 0, width, height), Vector2.one * 0.5f, 100f,
                0, SpriteMeshType.FullRect, new Vector4(border, border, border, border));
        }
    }
}
