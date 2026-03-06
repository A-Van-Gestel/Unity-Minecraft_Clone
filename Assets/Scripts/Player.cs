using Serialization;
using UnityEditor;
using UnityEngine;

[RequireComponent(typeof(PlayerInteraction))]
public class Player : MonoBehaviour
{
    internal PlayerInteraction PlayerInteraction;
    private Transform _playerCamera;
    private World _world;

    [Tooltip("Makes the player not be affected by gravity.")]
    public bool isFlying = false;

    [Tooltip("Allows the player to fly through blocks.")]
    public bool isNoclipping = false;

    public bool isGrounded;
    public bool isSprinting;

    [Header("Speed modifiers")]
    public float walkSpeed = 3f;

    public float sprintSpeed = 6f;
    public float flyingSpeed = 3f;
    public float flyingSpeedIncrement = 0.5f;
    public float flyingAscendSpeed = 5f;

    [Header("Gravity modifiers")]
    public float jumpForce = 5.7f;

    public float gravity = -13f;

    [Header("Player properties")]
    public Transform playerBody = null;

    [Tooltip("The radius of the player")]
    public float playerWidth = 0.4f;

    [Tooltip("The height of the player")]
    public float playerHeight = 1.8f;

    private float _horizontal;
    private float _vertical;
    private float _verticalFlying;
    private float _mouseHorizontal;
    private float _mouseVertical;
    internal Vector3 Velocity; // TODO: Should be private, and be accessed publicly using readOnly property. External modifications should be done in a separate method.
    private float _verticalMomentum = 0;
    internal float MoveSpeed; // TODO: Should be private, and be accessed publicly using readOnly property. External modifications should be done in a separate method.
    private float _lastMoveSpeed;
    private bool _jumpRequest;

    public byte orientation;

    [Header("Debug Keybindings")]
    [Tooltip("Key to toggle the debug screen. (Defaults to F3)")]
    public KeyCode toggleDebugScreenKey = KeyCode.F3;

    [Tooltip("Key to toggle flying. (Defaults to F1)")]
    public KeyCode toggleFlyingKey = KeyCode.F1;

    [Tooltip("Key to toggle noclip. (Defaults to F6)")]
    public KeyCode toggleNoclipKey = KeyCode.F6;

    [Tooltip("Key to toggle block highlight. (Defaults to F2)")]
    public KeyCode toggleBlockHighlightKey = KeyCode.F2;

    [Tooltip("Key to save the World on demand. (Defaults to F4)")]
    public KeyCode saveWorldKey = KeyCode.F4;

    [Tooltip("Key to toggle Chunk Border visualization. (Defaults to F5)")]
    public KeyCode toggleChunkBordersKey = KeyCode.F5;

    [Tooltip("Key to cycle through the internal VoxelData visualization modes. (Defaults to F7)")]
    public KeyCode cycleVisModeKey = KeyCode.F7;

    [Tooltip("Key to print debug info regarding. (Defaults to F8)")]
    public KeyCode debugCodeKey = KeyCode.F8;


    private void Start()
    {
        PlayerInteraction = GetComponent<PlayerInteraction>();
        _playerCamera = Camera.main?.transform;
        _world = World.Instance;

        // Scale playerBody to match the width and height settings.
        if (playerBody)
        {
            playerBody.localScale = new Vector3(playerWidth * 2f, playerHeight / 2f, playerWidth * 2f);
            Vector3 playerBodyLocalPosition = playerBody.localPosition;
            playerBodyLocalPosition = new Vector3(playerBodyLocalPosition.x, playerHeight / 2f, playerBodyLocalPosition.z);
            playerBody.localPosition = playerBodyLocalPosition;
        }

        _world.inUI = false;
    }

    private void FixedUpdate()
    {
        // Prevent gravity / movement while the world is still loading to avoid
        // the player falling through not-yet-meshed terrain. (FIX-I)
        if (!_world.IsWorldLoaded) return;

        if (!_world.inUI)
        {
            CalculateVelocity();
            if (_jumpRequest && !isFlying)
                Jump();

            transform.Translate(Velocity, Space.World);
        }
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.I))
        {
            _world.inUI = !_world.inUI;
        }

        if (!_world.inUI)
        {
            GetPlayerInputs();

            // Rotates the player on the X axis
            transform.Rotate(Vector3.up * (_mouseHorizontal * Time.timeScale * _world.settings.mouseSensitivityX));

            // Rotates the camera on the Y axis
            float angle = (_playerCamera.localEulerAngles.x - _mouseVertical * Time.timeScale * _world.settings.mouseSensitivityY + 360) % 360;
            if (angle > 180)
                angle -= 360;

            angle = Mathf.Clamp(angle, -90, 90);
            _playerCamera.localEulerAngles = Vector3.right * angle;
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

    private void Jump()
    {
        _verticalMomentum = jumpForce;
        isGrounded = false;
        _jumpRequest = false;
    }

    private void CalculateVelocity()
    {
        // VERTICAL VELOCITY & GRAVITY
        if (!isFlying)
        {
            // Only start accelerating downwards when falling of a block.
            if (isGrounded && _verticalMomentum < 0)
                _verticalMomentum = 0f;

            // Affect vertical momentum with gravity.
            if (_verticalMomentum > gravity)
                _verticalMomentum += Time.fixedDeltaTime * gravity;
        }
        else
        {
            if (_verticalFlying != 0)
                _verticalMomentum += Time.fixedDeltaTime * _verticalFlying * flyingAscendSpeed;
            else
                _verticalMomentum = 0;
        }


        // FORWARD & HORIZONTAL VELOCITY
        MoveSpeed = walkSpeed;
        // If we're sprinting, use the sprint multiplier
        if (isSprinting)
            MoveSpeed = sprintSpeed;

        // Only change moveSpeed multiplier when on the ground or when flying
        if (isGrounded && !isFlying)
            _lastMoveSpeed = MoveSpeed;
        else if (isFlying)
        {
            _lastMoveSpeed = flyingSpeed;
            MoveSpeed = _lastMoveSpeed;
        }
        else
            MoveSpeed = _lastMoveSpeed;

        Transform playerTransform = transform;
        Velocity = playerTransform.forward * _vertical + playerTransform.right * _horizontal;

        // Normalized movement so that you don't move faster diagonally only when total velocity is higher than 1.0 (allow slower-than-maximum motion)
        if (Velocity.magnitude > 1.0f)
            Velocity.Normalize();

        Velocity *= Time.fixedDeltaTime * MoveSpeed;

        // Apply vertical momentum (falling / jumping)
        Velocity += Vector3.up * (_verticalMomentum * Time.fixedDeltaTime);


        // COLLISION (Only apply if not Noclipping)
        if (!isNoclipping)
        {
            if ((Velocity.z > 0 && Front) || (Velocity.z < 0 && Back))
                Velocity.z = 0;

            if ((Velocity.x > 0 && Right) || (Velocity.x < 0 && Left))
                Velocity.x = 0;

            if (Velocity.y < 0)
                Velocity.y = CheckDownSpeed(Velocity.y);

            if (Velocity.y > 0)
                Velocity.y = CheckUpSpeed(Velocity.y);
        }
    }

    private void GetPlayerInputs()
    {
        // CLOSE GAME ON ESC BUTTON PRESS
        if (Input.GetKeyDown(KeyCode.Escape))
        {
#if UNITY_EDITOR
            EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // MOVEMENT & CAMERA
        _horizontal = Input.GetAxis("Horizontal");
        _vertical = Input.GetAxis("Vertical");
        _mouseHorizontal = Input.GetAxis("Mouse X");
        _mouseVertical = Input.GetAxis("Mouse Y");

        // SPRINTING
        if (Input.GetButtonDown("Sprint"))
            isSprinting = true;
        if (Input.GetButtonUp("Sprint"))
            isSprinting = false;

        // --- DEBUG ACTIONS ---
        if (Input.GetKeyDown(toggleDebugScreenKey))
            _world.ToggleDebugScreen();

        if (Input.GetKeyDown(saveWorldKey))
            _world.SaveWorldData();

        if (Input.GetKeyDown(toggleChunkBordersKey))
            _world.settings.showChunkBorders = !_world.settings.showChunkBorders;

        if (Input.GetKeyDown(cycleVisModeKey))
            _world.CycleVisualizationMode();

        if (Input.GetKeyDown(debugCodeKey))
        {
            // Debug method here
        }


        // FLYING
        if (Input.GetKeyDown(toggleFlyingKey))
        {
            isFlying = !isFlying;
            if (!isFlying) isNoclipping = false; // Disable noclip when flight is disabled
        }

        // NOCLIP (GHOST MODE)
        if (Input.GetKeyDown(toggleNoclipKey))
        {
            isNoclipping = !isNoclipping;
            if (isNoclipping) isFlying = true; // Noclip requires flying
        }

        if (!isFlying)
        {
            if (isGrounded && Input.GetButton("Jump"))
                _jumpRequest = true;
        }
        else
        {
            float flyingUp = Input.GetAxis("Jump");
            float flyingDown = Input.GetAxis("Crouch");
            _verticalFlying = flyingUp - flyingDown;

            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Input.GetKey(KeyCode.LeftAlt) && scroll != 0)
            {
                if (scroll > 0)
                    flyingSpeed += flyingSpeedIncrement;
                else
                    flyingSpeed -= flyingSpeedIncrement;

                if (flyingSpeed <= 0)
                    flyingSpeed = 1f;
            }
        }
    }

    private float CheckDownSpeed(float downSpeed)
    {
        // Check from the center from the player, from the radius on all 4 corners if a solid voxel is below the player, which will stop the player from falling
        if ((_world.CheckForCollision(new Vector3(transform.position.x - playerWidth, transform.position.y + downSpeed, transform.position.z - playerWidth)) && !Left && !Back) ||
            (_world.CheckForCollision(new Vector3(transform.position.x + playerWidth, transform.position.y + downSpeed, transform.position.z - playerWidth)) && !Right && !Back) ||
            (_world.CheckForCollision(new Vector3(transform.position.x + playerWidth, transform.position.y + downSpeed, transform.position.z + playerWidth)) && !Right && !Front) ||
            (_world.CheckForCollision(new Vector3(transform.position.x - playerWidth, transform.position.y + downSpeed, transform.position.z + playerWidth)) && !Left && !Front))
        {
            isGrounded = true;
            return 0;
        }
        else
        {
            isGrounded = false;
            return downSpeed;
        }
    }

    private float CheckUpSpeed(float upSpeed)
    {
        // Check from the center from the player, from the radius on all 4 corners if a solid voxel is above the player, which will stop the player from jumping.
        if ((_world.CheckForCollision(new Vector3(transform.position.x - playerWidth, transform.position.y + playerHeight + upSpeed, transform.position.z - playerWidth)) && !Left && !Back) ||
            (_world.CheckForCollision(new Vector3(transform.position.x + playerWidth, transform.position.y + playerHeight + upSpeed, transform.position.z - playerWidth)) && !Right && !Back) ||
            (_world.CheckForCollision(new Vector3(transform.position.x + playerWidth, transform.position.y + playerHeight + upSpeed, transform.position.z + playerWidth)) && !Right && !Front) ||
            (_world.CheckForCollision(new Vector3(transform.position.x - playerWidth, transform.position.y + playerHeight + upSpeed, transform.position.z + playerWidth)) && !Left && !Front))
        {
            _verticalMomentum = 0; // set to 0 so the player falls when their head hits a block while jumping
            return 0;
        }
        else
        {
            return upSpeed;
        }
    }

    // ReSharper disable ArrangeAccessorOwnerBody
    public bool Front =>
        // Check from the center from the player, at both feet and head level if a solid voxel is in front of the player, which will stop the player from moving into it.
        _world.CheckForCollision(new Vector3(transform.position.x, transform.position.y, transform.position.z + playerWidth)) ||
        _world.CheckForCollision(new Vector3(transform.position.x, transform.position.y + 1f, transform.position.z + playerWidth));

    public bool Back =>
        // Check from the center from the player, at both feet and head level if a solid voxel is behind of the player, which will stop the player from moving into it.
        _world.CheckForCollision(new Vector3(transform.position.x, transform.position.y, transform.position.z - playerWidth)) ||
        _world.CheckForCollision(new Vector3(transform.position.x, transform.position.y + 1f, transform.position.z - playerWidth));

    public bool Left =>
        // Check from the center from the player, at both feet and head level if a solid voxel is to the left of the player, which will stop the player from moving into it.
        _world.CheckForCollision(new Vector3(transform.position.x - playerWidth, transform.position.y, transform.position.z)) ||
        _world.CheckForCollision(new Vector3(transform.position.x - playerWidth, transform.position.y + 1f, transform.position.z));

    public bool Right =>
        // Check from the center from the player, at both feet and head level if a solid voxel is to the right of the player, which will stop the player from moving into it.
        _world.CheckForCollision(new Vector3(transform.position.x + playerWidth, transform.position.y, transform.position.z)) ||
        _world.CheckForCollision(new Vector3(transform.position.x + playerWidth, transform.position.y + 1f, transform.position.z));
    // ReSharper restore ArrangeAccessorOwnerBody

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

        var data = new PlayerSaveData
        {
            position = transform.position,
            rotation = combinedRotation,
            capabilities = new PlayerCapabilityData
            {
                isFlying = isFlying,
                isNoclipping = isNoclipping
            }
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
