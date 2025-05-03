using UnityEngine;

public static class VoxelData
{
    public const int ChunkWidth = 16;
    public const int ChunkHeight = 128;
    public const int WorldSizeInChunks = 100;
    
    // World Generation Constants
    public const int SolidGroundHeight = 42;
    public const int SeaLevel = 45;  // Minecraft = 62

    // Lighting Values
    public static float MinLightLevel = 0.15f;
    public static float MaxLightLevel = 1.0f;
    
    // Light is handled as float (0-1) byt Minecraft stores light as a byte (0-15),
    // so we need to know how much of that float a single light level represents.
    public const float UnitOfLight = 1f / 16f;

    /// Ticks per second.
    public static float TickLength = 1f;
    
    // Block Behavior
    public const float GrassSpreadChance = 0.02f;

    public static int Seed = 0;

    public const int WorldSizeInVoxels = WorldSizeInChunks * ChunkWidth;

    public const int WorldCentre = WorldSizeInVoxels / 2;

    public const int TextureAtlasSizeInBlocks = 16;

    public const float NormalizedBlockTextureSize = 1f / TextureAtlasSizeInBlocks;

    public static readonly Vector3[] VoxelVerts = new Vector3[8]
    {
        new Vector3(0.0f, 0.0f, 0.0f),
        new Vector3(1.0f, 0.0f, 0.0f),
        new Vector3(1.0f, 1.0f, 0.0f),
        new Vector3(0.0f, 1.0f, 0.0f),
        new Vector3(0.0f, 0.0f, 1.0f),
        new Vector3(1.0f, 0.0f, 1.0f),
        new Vector3(1.0f, 1.0f, 1.0f),
        new Vector3(0.0f, 1.0f, 1.0f),
    };

    public static readonly Vector3Int[] FaceChecks = new Vector3Int[6]
    {
        new Vector3Int(0, 0, -1), // Back Face
        new Vector3Int(0, 0, 1), // Front Face
        new Vector3Int(0, 1, 0), // Top Face
        new Vector3Int(0, -1, 0), // Bottom Face
        new Vector3Int(-1, 0, 0), // Left Face
        new Vector3Int(1, 0, 0), // Right Face
    };

    public static readonly int[] revFaceChecksIndex = new int[6]
    {
        1, // Front Face
        0, // Back Face
        3, // Bottom Face
        2, // Top Face
        5, // Right Face
        4, // Left Face
    };

    public static readonly int[,] VoxelTris = new int[6, 4]
    {
        // Vertex Index order (with duplicates)
        // Back, Front, Top, Bottom, Left, Right
        //  0      1     2     2       1     3
        { 0, 3, 1, 2 }, // Back Face
        { 5, 6, 4, 7 }, // Front Face
        { 3, 7, 2, 6 }, // Top Face
        { 1, 5, 0, 4 }, // Bottom Face
        { 4, 7, 0, 3 }, // Left Face
        { 1, 2, 5, 6 }, // Right Face
    };

    public static readonly Vector2[] VoxelUvs = new Vector2[4]
    {
        new Vector2(0.0f, 0.0f), // Bottom left
        new Vector2(0.0f, 1.0f), // Top Left
        new Vector2(1.0f, 0.0f), // Bottom Right
        new Vector2(1.0f, 1.0f), // Top Right
    };

    public static int CalculateSeed(string seedText)
    {
        if (string.IsNullOrEmpty(seedText) || seedText.Length <= 1)  // TextMeshPro empty string has Length of 1 -_-
        {
            int randomSeed = new System.Random().Next(int.MinValue, int.MaxValue);
            Debug.Log($"VoxelData.CalculateSeed | Using Random seed: {randomSeed}");
            seedText = randomSeed.ToString();
        }

        return Mathf.Abs(seedText.GetHashCode()) / 10000;
    }
}