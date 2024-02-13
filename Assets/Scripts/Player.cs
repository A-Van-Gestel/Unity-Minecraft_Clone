using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Player : MonoBehaviour
{
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
    private Vector3 velocity;
    private float verticalMomentum = 0;
    private bool jumpRequest;

    private void Start()
    {
        playerCamera = GameObject.Find("Main Camera").transform;
        world = GameObject.Find("World").GetComponent<World>();
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
        if (!isFlying)
        {
            if (verticalMomentum > gravity)
            {
                // Affect vertical momentum with gravity.
                verticalMomentum += Time.fixedDeltaTime * gravity;
            }
        }
        else
        {
            if (verticalFlying != 0)
                verticalMomentum += Time.fixedDeltaTime * verticalFlying * flyingAscendSpeed;
            else
                verticalMomentum = 0;
        }

        float moveSpeed = walkSpeed;
        // If we're sprinting, use the sprint multiplier
        if (isSprinting)
            moveSpeed = sprintSpeed;

        // Normalized movement so that you don't move faster diagonally (causes little movements to stay stuck for a couple of seconds)
        // velocity = ((transform.forward * vertical) + (transform.right * horizontal)).normalized * Time.fixedDeltaTime * moveSpeed;
        velocity = ((transform.forward * vertical) + (transform.right * horizontal)) * Time.fixedDeltaTime * moveSpeed;

        // Apply vertical momentum (falling / jumping)
        velocity += Vector3.up * verticalMomentum * Time.fixedDeltaTime;

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
        horizontal = Input.GetAxis("Horizontal");
        vertical = Input.GetAxis("Vertical");
        mouseHorizontal = Input.GetAxis("Mouse X");
        mouseVertical = Input.GetAxis("Mouse Y");

        if (Input.GetButtonDown("Sprint"))
            isSprinting = true;
        if (Input.GetButtonUp("Sprint"))
            isSprinting = false;

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