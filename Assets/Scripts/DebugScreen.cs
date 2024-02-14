using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class DebugScreen : MonoBehaviour
{
    [Tooltip("How many times per second the frame rate counter will be updated.")]
    public float frameRateUpdateRate = 0.5f;
    
    private World world;
    private TextMeshProUGUI text;

    private float frameRate;
    private float timer;

    private int halfWorldSizeInVoxels;
    private int halfWorldSizeInChunks;
    
    void Start()
    {
        world = GameObject.Find("World").GetComponent<World>();
        text = GetComponent<TextMeshProUGUI>();

        halfWorldSizeInVoxels = VoxelData.WorldSizeInVoxels / 2;
        halfWorldSizeInChunks = VoxelData.WorldSizeInChunks / 2;
    }

    void Update()
    {
        Vector3 playerPosition = world.player.transform.position;
        
        string debugText = "b3agz' Code a Game Like Minecraft in Unity";
        debugText += "\n";
        debugText += $"{frameRate} fps";
        debugText += "\n\n";
        debugText += $"XYZ : {(Mathf.FloorToInt(playerPosition.x) - halfWorldSizeInVoxels)} / {Mathf.FloorToInt(playerPosition.y)} / {(Mathf.FloorToInt(playerPosition.z) - halfWorldSizeInVoxels)} | ";
        debugText += $"Eye Level : {Mathf.FloorToInt(playerPosition.y) + 1.65f}";
        debugText += "\n";
        debugText += $"Chunk: {world.playerChunkCoord.x - halfWorldSizeInChunks} / {world.playerChunkCoord.z - halfWorldSizeInChunks}";

        text.text = debugText;

        if (timer > frameRateUpdateRate)
        {
            frameRate = (int)(1f / Time.unscaledDeltaTime);
            timer = 0;
        }
        else
        {
            timer += Time.deltaTime;
        }
    }
}
