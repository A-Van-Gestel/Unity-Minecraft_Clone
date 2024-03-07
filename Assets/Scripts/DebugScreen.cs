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
    private Player player;
    private Transform playerCamera;
    private TextMeshProUGUI text;

    private float frameRate;
    private float frameRateTimer;

    private float groundVoxelStateUpdateRate = 0.2f;
    private float groundVoxelStateTimer;
    private VoxelState groundVoxelState;

    private int halfWorldSizeInVoxels;
    private int halfWorldSizeInChunks;

    void Start()
    {
        world = GameObject.Find("World").GetComponent<World>();
        player = GameObject.Find("Player").GetComponent<Player>();
        playerCamera = GameObject.Find("Main Camera").GetComponent<Transform>();
        text = GetComponent<TextMeshProUGUI>();

        halfWorldSizeInVoxels = VoxelData.WorldSizeInVoxels / 2;
        halfWorldSizeInChunks = VoxelData.WorldSizeInChunks / 2;
    }

    void Update()
    {
        Vector3 playerPosition = world.player.transform.position;
        Vector2 lookingDirection = GetLookingAngles();

        string debugText = "b3agz' Code a Game Like Minecraft in Unity";
        debugText += "\n";
        debugText += $"{frameRate} fps";
        debugText += "\n\n";
        // debugText += $"XYZ : {(Mathf.FloorToInt(playerPosition.x) - halfWorldSizeInVoxels)} / {Mathf.FloorToInt(playerPosition.y)} / {(Mathf.FloorToInt(playerPosition.z) - halfWorldSizeInVoxels)} | ";
        debugText += $"XYZ: {(Mathf.FloorToInt(playerPosition.x))} / {Mathf.FloorToInt(playerPosition.y)} / {(Mathf.FloorToInt(playerPosition.z))} | ";
        debugText += $"Eye Level: {(playerPosition.y + 1.65f):f2}";
        debugText += "\n";

        debugText += $"Looking Angle H / V: {lookingDirection.x:f2} / {lookingDirection.y:f2} | Direction: {GetHorizontalDirection(lookingDirection.x)}";
        debugText += "\n";

        // debugText += $"Chunk: {world.playerChunkCoord.x - halfWorldSizeInChunks} / {world.playerChunkCoord.z - halfWorldSizeInChunks}";
        debugText += $"Chunk: {world.playerChunkCoord.x} / {world.playerChunkCoord.z}";
        debugText += "\n\n";
        debugText += "PLAYER:\n";
        debugText += $"isGrounded: {player.isGrounded}\nisFlying: {player.isFlying}\nshowHighlightBlocks {player.showHighlightBlocks}";
        debugText += "\n";
        debugText += $"SPEED: Current: {player.moveSpeed:f1} | Flying: {player.flyingSpeed:f1}";
        debugText += "\n";
        debugText += $"Velocity XYZ: {player.velocity.x:F4} / {player.velocity.y:F4} / {player.velocity.z:F4}";
        debugText += "\n\n";
        debugText += "LIGHTING:\n";

        string groundLightLevel = groundVoxelState != null ? groundVoxelState.light.ToString() : "NULL";
        debugText += $"groundLightLevel: {groundLightLevel}";

        text.text = debugText;

        // FRAMERATE
        if (frameRateTimer > frameRateUpdateRate)
        {
            frameRate = (int)(1f / Time.unscaledDeltaTime);
            frameRateTimer = 0;
        }
        else
        {
            frameRateTimer += Time.deltaTime;
        }

        // GROUND VOXEL STATE (LIGHT LEVEL)
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

    private Vector2 GetLookingAngles()
    {
        float hAngle = player.transform.eulerAngles.y;
        float vAngleRaw = playerCamera.transform.eulerAngles.x;
        float vAngle;

        if (vAngleRaw <= 360 && vAngleRaw >= 270)
            vAngle = 360 - vAngleRaw;
        else
            vAngle = vAngleRaw * -1;


        return new Vector2(hAngle, vAngle);
    }

    private static string GetHorizontalDirection(float hAngle)
    {
        return hAngle switch
        {
            >= 45 and <= 135 => "East",
            >= 135 and <= 225 => "South",
            >= 225 and <= 315 => "West",
            _ => "North"
        };
    }
}