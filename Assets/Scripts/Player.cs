using Physics;
using Serialization;
using UnityEditor;
using UnityEngine;

[RequireComponent(typeof(PlayerInteraction))]
[RequireComponent(typeof(VoxelRigidbody))]
public class Player : MonoBehaviour
{
    internal PlayerInteraction PlayerInteraction;
    public VoxelRigidbody VoxelRigidbody { get; private set; }
    private Transform _playerCamera;
    private World _world;
    private InputManager _input;

    [Tooltip("Makes the player not be affected by gravity.")]
    public bool isFlying
    {
        get => VoxelRigidbody.isFlying;
        set => VoxelRigidbody.isFlying = value;
    }

    [Tooltip("Allows the player to fly through blocks.")]
    public bool isNoclipping
    {
        get => VoxelRigidbody.isNoclipping;
        set => VoxelRigidbody.isNoclipping = value;
    }

    public bool isGrounded => VoxelRigidbody.IsGrounded;

    public bool isSprinting
    {
        get => VoxelRigidbody.isSprinting;
        set => VoxelRigidbody.isSprinting = value;
    }

    [Header("Speed modifiers")]
    public float flyingSpeedIncrement = 0.5f;

    [Header("Player properties")]
    public Transform playerBody = null;

    private float _horizontal;
    private float _vertical;
    private float _mouseHorizontal;
    private float _mouseVertical;

    public Vector3 Velocity => VoxelRigidbody.Velocity;
    public float MoveSpeed => VoxelRigidbody.MoveSpeed;

    public byte orientation;


    private void Start()
    {
        PlayerInteraction = GetComponent<PlayerInteraction>();
        VoxelRigidbody = GetComponent<VoxelRigidbody>();
        _playerCamera = Camera.main?.transform;
        _world = World.Instance;
        _input = InputManager.Instance;

        // Scale playerBody to match the width and height settings.
        if (playerBody)
        {
            playerBody.localScale = new Vector3(VoxelRigidbody.collisionWidthX, VoxelRigidbody.collisionHeight / 2f, VoxelRigidbody.collisionDepthZ);
            Vector3 playerBodyLocalPosition = playerBody.localPosition;
            playerBodyLocalPosition = new Vector3(playerBodyLocalPosition.x, VoxelRigidbody.collisionHeight / 2f, playerBodyLocalPosition.z);
            playerBody.localPosition = playerBodyLocalPosition;
        }

        _world.inUI = false;
    }

    private void Update()
    {
        if (_input.ToggleInventoryPressed)
        {
            _world.inUI = !_world.inUI;
        }

        if (!_world.inUI)
        {
            GetPlayerInputs();

            // Rotates the player on the X axis
            transform.Rotate(Vector3.up * (_mouseHorizontal * _world.settings.mouseSensitivityX));

            // Rotates the camera on the Y axis
            float angle = (_playerCamera.localEulerAngles.x - _mouseVertical * _world.settings.mouseSensitivityY + 360) % 360;
            if (angle > 180)
                angle -= 360;

            angle = Mathf.Clamp(angle, -90, 90);
            _playerCamera.localEulerAngles = Vector3.right * angle;

            // Pass movement vectors to Rigidbody
            Vector3 moveDirection = transform.forward * _vertical + transform.right * _horizontal;
            VoxelRigidbody.SetMovementIntent(moveDirection);
        }
        else
        {
            VoxelRigidbody.SetMovementIntent(Vector3.zero);
        }

        // TODO: Merge with lookingDirection from debug script.
        Vector3 xzDirection = transform.forward;
        xzDirection.y = 0;
        if (Vector3.Angle(xzDirection, Vector3.forward) <= 45)
            orientation = 0; // Player is facing north.
        else if (Vector3.Angle(xzDirection, Vector3.right) <= 45)
            orientation = 5; // Player is facing east.
        else if (Vector3.Angle(xzDirection, Vector3.back) <= 45)
            orientation = 1; // Player is facing south.
        else
            orientation = 4; // Player is facing west.
    }

    private void GetPlayerInputs()
    {
        // CLOSE GAME ON ESC BUTTON PRESS
        if (_input.EscapePressed)
        {
#if UNITY_EDITOR
            EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // MOVEMENT & CAMERA
        Vector2 move = _input.MoveInput;
        _horizontal = move.x;
        _vertical = move.y;
        Vector2 look = _input.LookInput;
        _mouseHorizontal = look.x;
        _mouseVertical = look.y;

        // SPRINTING
        if (_input.SprintPressed)
            isSprinting = true;
        if (_input.SprintReleased)
            isSprinting = false;

        // --- DEBUG ACTIONS ---
        if (_input.ToggleDebugScreenPressed)
            _world.ToggleDebugScreen();

        if (_input.SaveWorldPressed)
            _world.SaveWorldData();

        if (_input.ToggleChunkBordersPressed)
            _world.settings.showChunkBorders = !_world.settings.showChunkBorders;

        if (_input.CycleVisModePressed)
            _world.CycleVisualizationMode();

        if (_input.DebugCodePressed)
        {
            // Debug method here
            _world.DebugLogFluidSurfaceMath();
        }


        // FLYING
        if (_input.ToggleFlyingPressed)
        {
            isFlying = !isFlying;
            if (!isFlying) isNoclipping = false; // Disable noclip when flight is disabled
        }

        // NOCLIP (GHOST MODE)
        if (_input.ToggleNoclipPressed)
        {
            isNoclipping = !isNoclipping;
            if (isNoclipping) isFlying = true; // Noclip requires flying
        }

        if (!isFlying)
        {
            if (isGrounded && _input.JumpHeld)
                VoxelRigidbody.RequestJump();
        }
        else
        {
            float flyingUp = _input.JumpValue;
            float flyingDown = _input.CrouchValue;
            VoxelRigidbody.SetVerticalFlyingIntent(flyingUp - flyingDown);

            float scroll = _input.ScrollValue;
            if (_input.AltModifierHeld && scroll != 0)
            {
                if (scroll > 0)
                    VoxelRigidbody.IncrementFlyingSpeed(flyingSpeedIncrement);
                else
                    VoxelRigidbody.IncrementFlyingSpeed(-flyingSpeedIncrement);
            }
        }
    }


    // --- SAVE / LOAD LOGIC ---

    #region Save / Load Logic

    public PlayerSaveData GetSaveData()
    {
        // Combine Player Yaw (Body Y) and Camera Pitch (Camera X) into one Vector3 for saving.
        Vector3 combinedRotation = new Vector3(
            _playerCamera.localEulerAngles.x, // Pitch (Camera X)
            transform.localEulerAngles.y, // Yaw (Player Y)
            0f // Roll (Unused)
        );

        PlayerSaveData data = new PlayerSaveData
        {
            position = transform.position,
            rotation = combinedRotation,
            capabilities = new PlayerCapabilityData
            {
                isFlying = isFlying,
                isNoclipping = isNoclipping,
            },
        };

        // Note: Inventory and Cursor data are gathered by the SaveSystem
        // from the Toolbar/UI scripts and injected into this data structure
        // before serialization.

        return data;
    }

    public void LoadSaveData(PlayerSaveData data)
    {
        // 1. Position + small Y offset to prevent clipping trough world
        transform.position = data.position + new Vector3(0, 0.1f, 0);

        // 2. Rotation
        Vector3 savedRot = data.rotation;

        // Apply Yaw to Body (Y axis)
        transform.rotation = Quaternion.Euler(0, savedRot.y, 0);

        // Apply Pitch to Camera (X axis)
        // Ensure camera ref is valid (Load can happen before Start if called manually)
        if (_playerCamera == null) _playerCamera = Camera.main?.transform;

        if (_playerCamera != null)
        {
            _playerCamera.localEulerAngles = new Vector3(savedRot.x, 0, 0);
        }

        // 3. Capabilities
        isFlying = data.capabilities.isFlying;
        isNoclipping = data.capabilities.isNoclipping;
    }

    #endregion
}
