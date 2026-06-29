using UnityEngine;
using Random = System.Random;

public static class VoxelData
{
    public const int ChunkWidth = 16;
    public const int ChunkHeight = 128;
    public const int WorldSizeInChunks = 100;

    // Lighting Values
    public const float MinLightLevel = 0.15f;
    public const float MaxLightLevel = 1.0f;

    // Light is handled as float (0-1) byt Minecraft stores light as a byte (0-15),
    // so we need to know how much of that float a single light level represents.
    public const float UnitOfLight = 1f / 16f;

    /// Ticks per second.
    public static float TickLength = 1f;

    // Block Behavior
    public const float GrassSpreadChance = 0.02f;

    public static int Seed = 0;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void DomainReset()
    {
        TickLength = 1f;
        Seed = 0;
    }

    public const int WorldSizeInVoxels = WorldSizeInChunks * ChunkWidth;

    public const int WorldCentre = WorldSizeInVoxels / 2;

    public const int TextureAtlasSizeInBlocks = 16;

    public const float NormalizedBlockTextureSize = 1f / TextureAtlasSizeInBlocks;

    public static readonly Vector3[] VoxelVerts =
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

    public static readonly Vector3Int[] FaceChecks =
    {
        new Vector3Int(0, 0, -1), // Back Face
        new Vector3Int(0, 0, 1), // Front Face
        new Vector3Int(0, 1, 0), // Top Face
        new Vector3Int(0, -1, 0), // Bottom Face
        new Vector3Int(-1, 0, 0), // Left Face
        new Vector3Int(1, 0, 0), // Right Face
    };

    /// <summary>
    /// Checks for all faces (front, back, top, bottom, right, left).
    /// </summary>
    public static readonly int[] RevFaceChecksIndices =
    {
        1, // Front Face
        0, // Back Face
        3, // Bottom Face
        2, // Top Face
        5, // Right Face
        4, // Left Face
    };

    /// <summary>
    /// Checks for only the horizontal faces (front, back, right, left).
    /// </summary>
    public static readonly int[] HorizontalFaceChecksIndices =
    {
        1, // Front Face
        0, // Back Face
        5, // Right Face
        4, // Left Face
    };

    /// <summary>
    /// Checks for only the vertical faces (bottom, top).
    /// </summary>
    public static readonly int[] VerticalFaceChecksIndices =
    {
        3, // Bottom Face
        2, // Top Face
    };

    /// <summary>
    /// The four horizontal diagonal offsets (NE, NW, SE, SW).
    /// These are NOT indices into FaceChecks — they are standalone Vector3Int offsets.
    /// Used for stability checks that must account for all 8 neighbors, e.g. mesh or lighting readiness.
    /// </summary>
    public static readonly Vector3Int[] DiagonalNeighborOffsets =
    {
        new Vector3Int(1, 0, 1), // Front-Right (NE)
        new Vector3Int(-1, 0, 1), // Front-Left  (NW)
        new Vector3Int(1, 0, -1), // Back-Right  (SE)
        new Vector3Int(-1, 0, -1), // Back-Left   (SW)
    };

    /// <summary>
    /// All 8 horizontal neighbor offsets (4 cardinal + 4 diagonal).
    /// Used by <see cref="World.AreNeighborsDataReady"/> to validate that the full
    /// neighborhood is populated before scheduling lighting jobs.
    /// </summary>
    public static readonly Vector3Int[] AllNeighborOffsets =
    {
        new Vector3Int(0, 0, -1), // South  (Back)
        new Vector3Int(0, 0, 1), // North  (Front)
        new Vector3Int(-1, 0, 0), // West   (Left)
        new Vector3Int(1, 0, 0), // East   (Right)
        new Vector3Int(1, 0, 1), // NE     (Front-Right)
        new Vector3Int(-1, 0, 1), // NW     (Front-Left)
        new Vector3Int(1, 0, -1), // SE     (Back-Right)
        new Vector3Int(-1, 0, -1), // SW     (Back-Left)
    };

    // Should be accessed like this: VoxelTris[face * 4 + vert].
    public static readonly int[] VoxelTris =
    {
        // Vertex Index order (with duplicates)
        // Back, Front, Top, Bottom, Left, Right
        //  0      1     2     2       1     3
        0, 3, 1, 2, // Back Face
        5, 6, 4, 7, // Front Face
        3, 7, 2, 6, // Top Face
        1, 5, 0, 4, // Bottom Face
        4, 7, 0, 3, // Left Face
        1, 2, 5, 6, // Right Face
    };

    public static readonly Vector2[] VoxelUvs =
    {
        new Vector2(0.0f, 0.0f), // Bottom left
        new Vector2(0.0f, 1.0f), // Top Left
        new Vector2(1.0f, 0.0f), // Bottom Right
        new Vector2(1.0f, 1.0f), // Top Right
    };

    public static int CalculateSeed(string seedText)
    {
        // Trim ZERO WIDTH SPACE (U+8203) from the string that TextMeshPro always adds -_-.
        seedText = seedText.Trim((char)8203);

        // 1. Handle null or empty strings by generating a random seed.
        if (string.IsNullOrEmpty(seedText))
        {
            int randomSeed = new Random().Next(int.MinValue, int.MaxValue);
            int hashCode = randomSeed.ToString().GetHashCode();
            int safeHashCode = Mathf.Abs(hashCode) / 10000; // TODO: This is a hack to make the make the world generation not shit itself.
            Debug.Log($"VoxelData.CalculateSeed | Using Random seed: {randomSeed} | Actual seed: {safeHashCode}");
            return safeHashCode;
        }

        // 2. Try to parse the string as an integer.
        if (int.TryParse(seedText, out int parsedSeed))
        {
            Debug.Log($"VoxelData.CalculateSeed | Using integer seed: {parsedSeed}");
            return parsedSeed; // Return the parsed integer directly.
        }
        // 3. Seed was string (e.g., "hello world").

        {
            int hashCode = seedText.GetHashCode();
            int safeHashCode = Mathf.Abs(hashCode) / 10000; // TODO: This is a hack to make the make the world generation not shit itself.
            Debug.Log($"VoxelData.CalculateSeed | Using hash of string \"{seedText}\" | Actual seed: {safeHashCode}");
            return safeHashCode;
        }
    }
}
