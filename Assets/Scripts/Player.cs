using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Player : MonoBehaviour
{
    public bool isGrounded;
    public bool isSprinting;

    private Transform cam;
    private World world;

    public float walkSpeed = 3f;
    public float sprintSpeed = 6f;
    public float jumpForce = 5.7f;
    public float gravity = -13f;

    [Tooltip("The radius of the player")]
    public float playerWidth = 0.4f;

    [Tooltip("The height of the player")]
    public float playerHeight = 1.8f;

    private float horizontal;
    private float vertical;
    private float mouseHorizontal;
    private float mouseVertical;
    private Vector3 velocity;
    private float verticalMomentum = 0;
    private bool jumpRequest;

    private void Start()
    {
        cam = GameObject.Find("Main Camera").transform;
        world = GameObject.Find("World").GetComponent<World>();
    }

    private void FixedUpdate()
    {
        CalculateVelocity();
        if (jumpRequest)
            Jump();

        // Rotates the player on the X axis
        transform.Rotate(Vector3.up * mouseHorizontal);

        // Rotates the camera on the Y axis
        cam.Rotate(Vector3.right * -mouseVertical);

        transform.Translate(velocity, Space.World);
    }

    private void Update()
    {
        GetPlayerInputs();
    }

    private void Jump()
    {
        verticalMomentum = jumpForce;
        isGrounded = false;
        jumpRequest = false;
    }

    private void CalculateVelocity()
    {
        // Affect vertical momentum with gravity.
        if (verticalMomentum > gravity)
            verticalMomentum += Time.fixedDeltaTime * gravity;

        // If we're sprinting, use the sprint multiplier
        if (isSprinting)
            velocity = ((transform.forward * vertical) + (transform.right * horizontal)) * Time.fixedDeltaTime * sprintSpeed;
        else
            velocity = ((transform.forward * vertical) + (transform.right * horizontal)) * Time.fixedDeltaTime * walkSpeed;

        // Apply vertical momentum (falling / jumping)
        velocity += Vector3.up * verticalMomentum * Time.fixedDeltaTime;

        if ((velocity.z > 0 && front) || (velocity.z < 0 && back))
            velocity.z = 0;

        if ((velocity.x > 0 && right) || (velocity.x < 0 && left))
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

        if (isGrounded && Input.GetButton("Jump"))
            jumpRequest = true;
    }

    private float CheckDownSpeed(float downSpeed)
    {
        // Check from the center from the player, from the radius on all 4 corners if a solid voxel is below the player, which will stop the player from falling
        if ((world.CheckForVoxel(transform.position.x - playerWidth, transform.position.y + downSpeed, transform.position.z - playerWidth) && (!left && !back)) ||
            (world.CheckForVoxel(transform.position.x + playerWidth, transform.position.y + downSpeed, transform.position.z - playerWidth) && (!right && !back)) ||
            (world.CheckForVoxel(transform.position.x + playerWidth, transform.position.y + downSpeed, transform.position.z + playerWidth) && (!right && !front)) ||
            (world.CheckForVoxel(transform.position.x - playerWidth, transform.position.y + downSpeed, transform.position.z + playerWidth) && (!left && !front)))
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
        if ((world.CheckForVoxel(transform.position.x - playerWidth, transform.position.y + playerHeight + upSpeed, transform.position.z - playerWidth) && (!left && !back)) ||
            (world.CheckForVoxel(transform.position.x + playerWidth, transform.position.y + playerHeight + upSpeed, transform.position.z - playerWidth) && (!right && !back)) ||
            (world.CheckForVoxel(transform.position.x + playerWidth, transform.position.y + playerHeight + upSpeed, transform.position.z + playerWidth) && (!right && !front)) ||
            (world.CheckForVoxel(transform.position.x - playerWidth, transform.position.y + playerHeight + upSpeed, transform.position.z + playerWidth) && (!left && !front)))
        {
            verticalMomentum = 0; // set to 0 so the player falls when their head hits a block while jumping
            return 0;
        }
        else
        {
            return upSpeed;
        }
    }

    public bool front
    {
        get
        {
            // Check from the center from the player, at both feet and head level if a solid voxel is in front of the player, which will stop the player from moving into it.
            if (world.CheckForVoxel(transform.position.x, transform.position.y, transform.position.z + playerWidth) ||
                world.CheckForVoxel(transform.position.x, transform.position.y + 1f, transform.position.z + playerWidth))
                return true;
            else
                return false;
        }
    }

    public bool back
    {
        get
        {
            // Check from the center from the player, at both feet and head level if a solid voxel is behind of the player, which will stop the player from moving into it.
            if (world.CheckForVoxel(transform.position.x, transform.position.y, transform.position.z - playerWidth) ||
                world.CheckForVoxel(transform.position.x, transform.position.y + 1f, transform.position.z - playerWidth))
                return true;
            else
                return false;
        }
    }

    public bool left
    {
        get
        {
            // Check from the center from the player, at both feet and head level if a solid voxel is to the left of the player, which will stop the player from moving into it.
            if (world.CheckForVoxel(transform.position.x - playerWidth, transform.position.y, transform.position.z) ||
                world.CheckForVoxel(transform.position.x - playerWidth, transform.position.y + 1f, transform.position.z))
                return true;
            else
                return false;
        }
    }

    public bool right
    {
        get
        {
            // Check from the center from the player, at both feet and head level if a solid voxel is to the right of the player, which will stop the player from moving into it.
            if (world.CheckForVoxel(transform.position.x + playerWidth, transform.position.y, transform.position.z) ||
                world.CheckForVoxel(transform.position.x + playerWidth, transform.position.y + 1f, transform.position.z))
                return true;
            else
                return false;
        }
    }
}