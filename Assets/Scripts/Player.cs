using Data;
using MyBox;
using UnityEngine;

public class Player : MonoBehaviour
{
    [Tooltip("Makes the player not be affected by gravity.")]
    public bool isFlying = false;

    [Tooltip("Allows the player to fly through blocks.")]
    public bool isNoclipping = false;

    public bool isGrounded;
    public bool isSprinting;

    private Transform _playerCamera;
    private World _world;

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

    [Header("Block Destroy & Placement properties")]
    public bool showHighlightBlocks = true;

    private Transform _highlightBlocksParent;
    public Transform highlightBlock;
    public Transform placeBlock;

    /// <summary>
    /// Is current placeable block not inside the player, other solid block, outside the world and current itemSlot is not empty.
    /// </summary>
    private bool _blockPlaceable;

    [Tooltip("Distance between each ray-cast check, lower value means better accuracy")]
    public float checkIncrement = 0.05f;

    [Tooltip("Maximum distance the player can place and delete blocks from.")]
    public float reach = 8f;

    public Toolbar toolbar;

    // A struct to hold the results of our voxel raycast.
    public struct VoxelRaycastResult
    {
        public bool DidHit;
        public Vector3Int HitPosition;
        public Vector3Int PlacePosition;
    }


    private void Start()
    {
        _playerCamera = GameObject.Find("Main Camera").transform;
        _world = World.Instance;
        _highlightBlocksParent = GameObject.Find("HighlightBlocks").GetComponent<Transform>();

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
            PlaceCursorBlocks();

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
            Application.Quit();

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


        // FLYING
        if (Input.GetKeyDown(KeyCode.F1))
        {
            isFlying = !isFlying;
            if (!isFlying) isNoclipping = false; // Disable noclip when flight is disabled
        }


        // NOCLIP (GHOST MODE)
        if (Input.GetKeyDown(KeyCode.F6))
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

        // PLACING & DESTROYING BLOCKS
        if (Input.GetKeyDown(KeyCode.F2))
            showHighlightBlocks = !showHighlightBlocks;

        // TOGGLE CHUNK BORDERS
        if (Input.GetKeyDown(KeyCode.F5))
            _world.settings.showChunkBorders = !_world.settings.showChunkBorders;

        if (highlightBlock.gameObject.activeSelf)
        {
            // Destroy block.
            if (Input.GetMouseButtonDown(0))
            {
                _world.AddModification(new VoxelMod(highlightBlock.position.ToVector3Int(), blockId: 0)
                {
                    ImmediateUpdate = true,
                });
            }

            // Place block.
            if (Input.GetMouseButtonDown(1))
            {
                // Don't place blocks inside the player or other voxels or when current itemSlot is empty by returning early.
                if (!_blockPlaceable) return;

                UIItemSlot itemSlot = toolbar.slots[toolbar.slotIndex];
                _world.AddModification(new VoxelMod(placeBlock.position.ToVector3Int(), blockId: itemSlot.ItemSlot.Stack.ID)
                {
                    Orientation = orientation,
                    ImmediateUpdate = true,
                });
                itemSlot.ItemSlot.Take(1);
            }
        }
    }

    /// <summary>
    /// Centralized method to cast a ray from the player's camera to find a voxel.
    /// </summary>
    /// <returns>A VoxelRaycastResult struct containing information about the hit.</returns>
    public VoxelRaycastResult RaycastForVoxel()
    {
        float step = checkIncrement;

        while (step < reach)
        {
            Vector3 pos = _playerCamera.position + _playerCamera.forward * step;

            if (_world.CheckForVoxel(pos))
            {
                VoxelRaycastResult result = new VoxelRaycastResult { DidHit = true };

                // DESTROY HIGHLIGHT BLOCK
                result.HitPosition = new Vector3Int(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y), Mathf.FloorToInt(pos.z));

                // PLACE HIGHLIGHT BLOCK
                // Calculate place block position based on smallest x, y, z value, using HitPosition position as your origin.
                float xCheck = pos.x % 1;
                if (xCheck > 0.5f)
                    xCheck -= 1;
                float yCheck = pos.y % 1;
                if (yCheck > 0.5f)
                    yCheck -= 1;
                float zCheck = pos.z % 1;
                if (zCheck > 0.5f)
                    zCheck -= 1;

                if (Mathf.Abs(xCheck) < Mathf.Abs(yCheck) && Mathf.Abs(xCheck) < Mathf.Abs(zCheck))
                {
                    // place block on x-axis
                    if (xCheck < 0)
                        result.PlacePosition = result.HitPosition + Vector3Int.right;
                    else
                        result.PlacePosition = result.HitPosition + Vector3Int.left;
                }
                else if (Mathf.Abs(zCheck) < Mathf.Abs(yCheck) && Mathf.Abs(zCheck) < Mathf.Abs(xCheck))
                {
                    // place block on z axis
                    if (zCheck < 0)
                        result.PlacePosition = result.HitPosition + Vector3Int.forward;
                    else
                        result.PlacePosition = result.HitPosition + Vector3Int.back;
                }
                else
                {
                    // place block on y-axis by default
                    if (yCheck < 0)
                        result.PlacePosition = result.HitPosition + Vector3Int.up;
                    else
                        result.PlacePosition = result.HitPosition + Vector3Int.down;
                }

                return result;
            }

            step += checkIncrement;
        }

        // If we get here, we didn't hit anything.
        return new VoxelRaycastResult { DidHit = false };
    }

    private void PlaceCursorBlocks()
    {
        VoxelRaycastResult result = RaycastForVoxel();

        if (result.DidHit)
        {
            highlightBlock.position = result.HitPosition;
            placeBlock.position = result.PlacePosition;

            // Check if the placement position is valid.
            Vector3 playerPosition = transform.position;
            Vector3Int playerCoord = new Vector3Int(Mathf.FloorToInt(playerPosition.x), Mathf.FloorToInt(playerPosition.y), Mathf.FloorToInt(playerPosition.z));

            _blockPlaceable =
                result.PlacePosition != playerCoord && // Not inside player's feet
                result.PlacePosition != playerCoord + Vector3Int.up && // Not inside player's head
                _world.worldData.IsVoxelInWorld(result.PlacePosition) &&
                !_world.CheckForVoxel(result.PlacePosition) &&
                toolbar.slots[toolbar.slotIndex].ItemSlot.HasItem;

            // Set highlight objects active state
            _highlightBlocksParent.gameObject.SetActive(showHighlightBlocks);
            highlightBlock.gameObject.SetActive(true);
            placeBlock.gameObject.SetActive(_blockPlaceable);
        }
        else
        {
            // If we didn't hit a block, hide the highlights.
            highlightBlock.gameObject.SetActive(false);
            placeBlock.gameObject.SetActive(false);
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
}