using System.Collections;
using System.Collections.Generic;
using Data;
using TMPro;
using UnityEngine;

public class DebugScreen : MonoBehaviour
{
    [Tooltip("How many times per second the frame rate counter will be updated.")]
    public float frameRateUpdateRate = 0.5f;

    private World world;
    private Transform playerCamera;
    private TextMeshProUGUI text;

    private float frameRate;
    private float frameRateTimer;

    private float groundVoxelStateUpdateRate = 0.2f;
    private float groundVoxelStateTimer;
    private VoxelState groundVoxelState;

    private float currentChunkUpdateRate = 0.2f;
    private float currentChunkTimer;
    private Chunk currentChunk;

    void Start()
    {
        world = World.Instance;
        playerCamera = GameObject.Find("Main Camera").GetComponent<Transform>();
        text = GetComponent<TextMeshProUGUI>();
    }

    void Update()
    {
        Vector3 playerPosition = world.player.transform.position;
        Vector2 lookingDirection = GetLookingAngles();

        string debugText = "b3agz' Code a Game Like Minecraft in Unity";
        debugText += "\n";
        debugText += $"{frameRate} fps";
        debugText += "\n\n";
        debugText += $"XYZ: {(Mathf.FloorToInt(playerPosition.x))} / {Mathf.FloorToInt(playerPosition.y)} / {(Mathf.FloorToInt(playerPosition.z))} | ";
        debugText += $"Eye Level: {(playerPosition.y + 1.65f):f2}";
        debugText += "\n";

        debugText += $"Looking Angle H / V: {lookingDirection.x:f2} / {lookingDirection.y:f2} | Direction: {GetHorizontalDirection(lookingDirection.x)}";
        debugText += "\n";

        debugText += $"Chunk: {world.playerChunkCoord.x} / {world.playerChunkCoord.z}";
        debugText += "\n\n";
        debugText += "PLAYER:\n";
        debugText += $"isGrounded: {world.player.isGrounded}\nisFlying: {world.player.isFlying}\nshowHighlightBlocks {world.player.showHighlightBlocks}";
        debugText += "\n";
        debugText += $"SPEED: Current: {world.player.moveSpeed:f1} | Flying: {world.player.flyingSpeed:f1}";
        debugText += "\n";
        debugText += $"Velocity XYZ: {world.player.velocity.x:F4} / {world.player.velocity.y:F4} / {world.player.velocity.z:F4}";
        debugText += "\n\n";
        debugText += "LIGHTING:\n";

        string groundLightLevel = groundVoxelState != null ? groundVoxelState?.light.ToString() : "NULL";
        debugText += $"groundLightLevel: {groundLightLevel}";
        
        
        debugText += "\n\n";
        debugText += "CHUNK:\n";

        string activeBlockBehaviorVoxels = currentChunk != null ? currentChunk?.GetActiveVoxelCount().ToString() : "NULL";
        debugText += $"activeBlockBehaviorVoxels: {activeBlockBehaviorVoxels}";

        text.text = debugText;

        // FRAMERATE
        FrameRate();

        // GROUND VOXEL STATE (LIGHT LEVEL)
        GroundVoxelState(playerPosition);

        // CURRENT CHUNK
        CurrentChunk(playerPosition);
    }

    private void FrameRate()
    {
        if (frameRateTimer > frameRateUpdateRate)
        {
            frameRate = (int)(1f / Time.unscaledDeltaTime);
            frameRateTimer = 0;
        }
        else
        {
            frameRateTimer += Time.deltaTime;
        }
    }

    private void GroundVoxelState(Vector3 playerPosition)
    {
        if (groundVoxelStateTimer > groundVoxelStateUpdateRate)
        {
            groundVoxelState = world.GetVoxelState(playerPosition - new Vector3(0, -1, 0));
            groundVoxelStateTimer = 0;
        }
        else
        {
            groundVoxelStateTimer += Time.deltaTime;
        }
    }

    private void CurrentChunk(Vector3 playerPosition)
    {
        if (currentChunkTimer > currentChunkUpdateRate)
        {
            currentChunk = world.GetChunkFromVector3(playerPosition);
            currentChunkTimer = 0;
        }
        else
        {
            currentChunkTimer += Time.deltaTime;
        }
    }

    private Vector2 GetLookingAngles()
    {
        float hAngle = world.player.transform.eulerAngles.y;
        float vAngleRaw = playerCamera.transform.eulerAngles.x;
        float vAngle;

        if (vAngleRaw is <= 360 and >= 270)
            vAngle = 360 - vAngleRaw;
        else
            vAngle = vAngleRaw * -1;


        return new Vector2(hAngle, vAngle);
    }

    private static string GetHorizontalDirection(float hAngle)
    {
        return hAngle switch
        {
            >= 22.5f and < 67.5f => "North-east",
            >= 67.5f and < 112.5f => "East",
            >= 112.5f and < 157.5f => "South-east",
            >= 157.5f and < 202.5f => "South",
            >= 202.5f and < 247.5f => "South-west",
            >= 247.5f and < 292.5f => "West",
            >= 292.5f and < 337.5f => "North-west",
            _ => "North"
        };
    }
}