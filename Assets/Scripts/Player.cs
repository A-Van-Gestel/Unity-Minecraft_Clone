using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Player : MonoBehaviour
{
    [Tooltip("Makes the player not be affected by gravity.")]
    public bool isFlying = false;

    public bool isGrounded;
    public bool isSprinting;

    private Transform playerCamera;
    private World world;

    public float walkSpeed = 3f;
    public float sprintSpeed = 6f;
    public float flyingAscendSpeed = 5f;
    public float jumpForce = 5.7f;
    public float gravity = -13f;

    [Tooltip("The radius of the player")]
    public float playerWidth = 0.4f;

    [Tooltip("The height of the player")]
    public float playerHeight = 1.8f;

    private float horizontal;
    private float vertical;
    private float verticalFlying;
    private float mouseHorizontal;
    private float mouseVertical;
    public Vector3 velocity;
    private float verticalMomentum = 0;
    private float lastForwardSpeed;
    private bool jumpRequest;

    private Transform highlightBlocksParent;
    public Transform highlightBlock;
    public Transform placeBlock;
    private bool blockPlaceable;
    public bool showHighlightBlocks = true;

    [Tooltip("Distance between each ray-cast check, lower value means better accuracy")]
    public float checkIncrement = 0.05f;

    [Tooltip("Maximum distance the player can place and delete blocks from.")]
    public float reach = 8f;

    public byte selectedBlockIndex = 1;

    private void Start()
    {
        playerCamera = GameObject.Find("Main Camera").transform;
        world = GameObject.Find("World").GetComponent<World>();
        highlightBlocksParent = GameObject.Find("HighlightBlocks").GetComponent<Transform>();

        Cursor.lockState = CursorLockMode.Locked; // Makes cursor invisible and not able to go of screen
    }

    private void FixedUpdate()
    {
        CalculateVelocity();
        if (jumpRequest && !isFlying)
            Jump();

        transform.Translate(velocity, Space.World);
    }

    private void Update()
    {
        GetPlayerInputs();
        PlaceCursorBlocks();

        // Rotates the player on the X axis
        transform.Rotate(Vector3.up * mouseHorizontal * Time.timeScale);

        // Rotates the camera on the Y axis
        float angle = (playerCamera.localEulerAngles.x - mouseVertical * Time.timeScale + 360) % 360;
        if (angle > 180)
            angle -= 360;

        angle = Mathf.Clamp(angle, -85, 85);
        playerCamera.localEulerAngles = Vector3.right * angle;
    }

    private void Jump()
    {
        verticalMomentum = jumpForce;
        isGrounded = false;
        jumpRequest = false;
    }

    private void CalculateVelocity()
    {
        // VERTICAL VELOCITY & GRAVITY
        if (!isFlying)
        {
            // Only start accelerating downwards when falling of a block.
            if (isGrounded && verticalMomentum < 0)
                verticalMomentum = 0f;

            // Affect vertical momentum with gravity.
            if (verticalMomentum > gravity)
                verticalMomentum += Time.fixedDeltaTime * gravity;
        }
        else
        {
            if (verticalFlying != 0)
                verticalMomentum += Time.fixedDeltaTime * verticalFlying * flyingAscendSpeed;
            else
                verticalMomentum = 0;
        }


        // FORWARD & HORIZONTAL VELOCITY
        float moveSpeed = walkSpeed;
        // If we're sprinting, use the sprint multiplier
        if (isSprinting)
            moveSpeed = sprintSpeed;

        // Only change moveSpeed multiplier when on the ground or when flying
        if (isGrounded || isFlying)
            lastForwardSpeed = moveSpeed;
        else
            moveSpeed = lastForwardSpeed;

        Transform playerTransform = transform;
        velocity = (playerTransform.forward * vertical) + (playerTransform.right * horizontal);

        // Normalized movement so that you don't move faster diagonally only when total velocity is higher than 1.0 (allow slower-than-maximum motion)
        if (velocity.magnitude > 1.0f)
            velocity.Normalize();

        velocity = velocity * Time.fixedDeltaTime * moveSpeed;

        // Apply vertical momentum (falling / jumping)
        velocity += Vector3.up * verticalMomentum * Time.fixedDeltaTime;


        // COLLISION
        if ((velocity.z > 0 && Front) || (velocity.z < 0 && Back))
            velocity.z = 0;

        if ((velocity.x > 0 && Right) || (velocity.x < 0 && Left))
            velocity.x = 0;

        if (velocity.y < 0)
            velocity.y = CheckDownSpeed(velocity.y);

        if (velocity.y > 0)
            velocity.y = CheckUpSpeed(velocity.y);
    }

    private void GetPlayerInputs()
    {
        // MOVEMENT & CAMERA
        horizontal = Input.GetAxis("Horizontal");
        vertical = Input.GetAxis("Vertical");
        mouseHorizontal = Input.GetAxis("Mouse X");
        mouseVertical = Input.GetAxis("Mouse Y");

        // SPRINTING
        if (Input.GetButtonDown("Sprint"))
            isSprinting = true;
        if (Input.GetButtonUp("Sprint"))
            isSprinting = false;

        
        // FLYING
        if (Input.GetKeyDown(KeyCode.F1))
            isFlying = !isFlying;
        
        if (!isFlying)
        {
            if (isGrounded && Input.GetButton("Jump"))
                jumpRequest = true;
        }
        else
        {
            float flyingUp = Input.GetAxis("Jump");
            float flyingDown = Input.GetAxis("Crouch");
            verticalFlying = flyingUp - flyingDown;
        }

        // PLACING & DESTROYING BLOCKS
        if (Input.GetKeyDown(KeyCode.F2))
            showHighlightBlocks = !showHighlightBlocks;
        
        if (highlightBlock.gameObject.activeSelf)
        {
            // Destroy block.
            if (Input.GetMouseButtonDown(0))
            {
                Vector3 highlightBlockPosition = highlightBlock.position;
                world.GetChunkFromVector3(highlightBlockPosition).EditVoxel(highlightBlockPosition, 0);
            }

            // Place block.
            if (Input.GetMouseButtonDown(1))
            {
                // Don't place blocks inside the player or other voxels by returning early.
                if (!blockPlaceable) return;
                
                Vector3 placeBlockPosition = placeBlock.position;
                world.GetChunkFromVector3(placeBlockPosition).EditVoxel(placeBlockPosition, selectedBlockIndex);
            }
        }
    }

    private void PlaceCursorBlocks()
    {
        float step = checkIncrement;

        while (step < reach)
        {
            Vector3 pos = playerCamera.position + (playerCamera.forward * step);
            if (world.CheckForVoxel(pos))
            {
                // DESTROY HIGHLIGHT BLOCK
                highlightBlock.position = new Vector3(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y), Mathf.FloorToInt(pos.z));

                // PLACE HIGHLIGHT BLOCK
                // Calculate place block position based on smallest x, y, z value, using highlightBlock position as your origin.
                float xCheck = pos.x % 1;
                if (xCheck > 0.5f)
                    xCheck = xCheck - 1;
                float yCheck = pos.y % 1;
                if (yCheck > 0.5f)
                    yCheck = yCheck - 1;
                float zCheck = pos.z % 1;
                if (zCheck > 0.5f)
                    zCheck = zCheck - 1;

                if (Mathf.Abs(xCheck) < Mathf.Abs(yCheck) && Mathf.Abs(xCheck) < Mathf.Abs(zCheck))
                {
                    // place block on x axis
                    if (xCheck < 0)
                        placeBlock.position = highlightBlock.position + Vector3.right;
                    else
                        placeBlock.position = highlightBlock.position + Vector3.left;
                }
                else if (Mathf.Abs(zCheck) < Mathf.Abs(yCheck) && Mathf.Abs(zCheck) < Mathf.Abs(xCheck))
                {
                    // place block on z axis
                    if (zCheck < 0)
                        placeBlock.position = highlightBlock.position + Vector3.forward;
                    else
                        placeBlock.position = highlightBlock.position + Vector3.back;
                }
                else
                {
                    // place block on y axis by default
                    if (yCheck < 0)
                        placeBlock.position = highlightBlock.position + Vector3.up;
                    else
                        placeBlock.position = highlightBlock.position + Vector3.down;
                }


                // Don't show place block highlight inside the player or when place block is inside solid block or when the placed block would be outside the world.
                Vector3 playerPosition = transform.position;
                Vector3 playerCoord = new Vector3(Mathf.FloorToInt(playerPosition.x), Mathf.FloorToInt(playerPosition.y), Mathf.FloorToInt(playerPosition.z));
                if (playerCoord != placeBlock.position && (playerCoord + new Vector3(0, 1, 0)) != placeBlock.position  // Placed block isn't inside the player
                                                       && world.IsVoxelInWorld(placeBlock.position)  // Placed block is inside the world
                                                       && !world.CheckForVoxel(placeBlock.position))  // Placed block isn't inside a other voxel
                    blockPlaceable = true;
                else
                    blockPlaceable = false;

                // SHOW HIGHLIGHT BLOCKS
                highlightBlocksParent.gameObject.SetActive(showHighlightBlocks);

                highlightBlock.gameObject.SetActive(true);
                placeBlock.gameObject.SetActive(blockPlaceable);

                return;
            }

            step += checkIncrement;
        }

        highlightBlock.gameObject.SetActive(false);
        placeBlock.gameObject.SetActive(false);
    }

    private float CheckDownSpeed(float downSpeed)
    {
        // Check from the center from the player, from the radius on all 4 corners if a solid voxel is below the player, which will stop the player from falling
        if ((world.CheckForVoxel(new Vector3(transform.position.x - playerWidth, transform.position.y + downSpeed, transform.position.z - playerWidth)) && (!Left && !Back)) ||
            (world.CheckForVoxel(new Vector3(transform.position.x + playerWidth, transform.position.y + downSpeed, transform.position.z - playerWidth)) && (!Right && !Back)) ||
            (world.CheckForVoxel(new Vector3(transform.position.x + playerWidth, transform.position.y + downSpeed, transform.position.z + playerWidth)) && (!Right && !Front)) ||
            (world.CheckForVoxel(new Vector3(transform.position.x - playerWidth, transform.position.y + downSpeed, transform.position.z + playerWidth)) && (!Left && !Front)))
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
        if ((world.CheckForVoxel(new Vector3(transform.position.x - playerWidth, transform.position.y + playerHeight + upSpeed, transform.position.z - playerWidth)) && (!Left && !Back)) ||
            (world.CheckForVoxel(new Vector3(transform.position.x + playerWidth, transform.position.y + playerHeight + upSpeed, transform.position.z - playerWidth)) && (!Right && !Back)) ||
            (world.CheckForVoxel(new Vector3(transform.position.x + playerWidth, transform.position.y + playerHeight + upSpeed, transform.position.z + playerWidth)) && (!Right && !Front)) ||
            (world.CheckForVoxel(new Vector3(transform.position.x - playerWidth, transform.position.y + playerHeight + upSpeed, transform.position.z + playerWidth)) && (!Left && !Front)))
        {
            verticalMomentum = 0; // set to 0 so the player falls when their head hits a block while jumping
            return 0;
        }
        else
        {
            return upSpeed;
        }
    }

    public bool Front =>
        // Check from the center from the player, at both feet and head level if a solid voxel is in front of the player, which will stop the player from moving into it.
        world.CheckForVoxel(new Vector3(transform.position.x, transform.position.y, transform.position.z + playerWidth)) ||
        world.CheckForVoxel(new Vector3(transform.position.x, transform.position.y + 1f, transform.position.z + playerWidth));

    public bool Back =>
        // Check from the center from the player, at both feet and head level if a solid voxel is behind of the player, which will stop the player from moving into it.
        world.CheckForVoxel(new Vector3(transform.position.x, transform.position.y, transform.position.z - playerWidth)) ||
        world.CheckForVoxel(new Vector3(transform.position.x, transform.position.y + 1f, transform.position.z - playerWidth));

    public bool Left =>
        // Check from the center from the player, at both feet and head level if a solid voxel is to the left of the player, which will stop the player from moving into it.
        world.CheckForVoxel(new Vector3(transform.position.x - playerWidth, transform.position.y, transform.position.z)) ||
        world.CheckForVoxel(new Vector3(transform.position.x - playerWidth, transform.position.y + 1f, transform.position.z));

    public bool Right =>
        // Check from the center from the player, at both feet and head level if a solid voxel is to the right of the player, which will stop the player from moving into it.
        world.CheckForVoxel(new Vector3(transform.position.x + playerWidth, transform.position.y, transform.position.z)) ||
        world.CheckForVoxel(new Vector3(transform.position.x + playerWidth, transform.position.y + 1f, transform.position.z));
}