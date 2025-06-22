using System.Collections.Generic;
using UnityEngine;
using Random = System.Random;

public static class VoxelData
{
    public const int ChunkWidth = 16;
    public const int ChunkHeight = 128;
    public const int WorldSizeInChunks = 100;

    // World Generation Constants
    public const int SolidGroundHeight = 42;
    public const int SeaLevel = 45; // Minecraft = 62

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

    // Should be accessed like this: VoxelTris[face * 4 + vert].
    public static readonly int[] VoxelTris = new int[24]
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

    public static readonly Vector2[] VoxelUvs = new Vector2[4]
    {
        new Vector2(0.0f, 0.0f), // Bottom left
        new Vector2(0.0f, 1.0f), // Top Left
        new Vector2(1.0f, 0.0f), // Bottom Right
        new Vector2(1.0f, 1.0f), // Top Right
    };

    public static int CalculateSeed(string seedText)
    {
        if (string.IsNullOrEmpty(seedText) || seedText.Length <= 1) // TextMeshPro empty string has Length of 1 -_-
        {
            int randomSeed = new Random().Next(int.MinValue, int.MaxValue);
            Debug.Log($"VoxelData.CalculateSeed | Using Random seed: {randomSeed}");
            seedText = randomSeed.ToString();
        }

        return Mathf.Abs(seedText.GetHashCode()) / 10000;
    }

    // --- Constants for Bit Packing ---
    // Using Hex for clarity with bit positions
    private const ushort ID_MASK = 0x00FF; // Bits 0-7   (00000000 11111111) (8-bits, Values 0-256)
    private const ushort LIGHT_MASK = 0x0F00; // Bits 8-11 (00001111 00000000) (4-bits, Values 0-15)
    private const ushort ORIENTATION_MASK = 0x3000; // Bits 12-13 (00110000 00000000) (2-bits, Values 0-3)
    // Bits 14-15 are reserved (0xC000)

    private const int ID_SHIFT = 0;
    private const int LIGHT_SHIFT = 8;
    private const int ORIENTATION_SHIFT = 12;

    // Maps the 2-bit stored value back to the orientation int used elsewhere
    private static readonly byte[] OrientationMap =
    {
        1, // Index 0 maps to Orientation 1 (Front)
        0, // Index 1 maps to Orientation 0 (Back)
        4, // Index 2 maps to Orientation 4 (Left)
        5 // Index 3 maps to Orientation 5 (Right)
    };

    // Inverse map for packing
    private static readonly Dictionary<byte, byte> OrientationToIndexMap = new Dictionary<byte, byte>()
    {
        { 1, 0 }, { 0, 1 }, { 4, 2 }, { 5, 3 }
    };

    // --- Packing ---
    // Creates the initial packed value
    public static ushort PackVoxelData(byte id, byte lightLevel, byte orientation)
    {
        ushort packedData = 0;
        packedData |= (ushort)((id & 0xFF) << ID_SHIFT); // ID: Ensure only 8 bits
        packedData |= (ushort)((lightLevel & 0xF) << LIGHT_SHIFT); // Light Level: Ensure only 4 bits

        // Pack Orientation (Find index in map)
        if (!OrientationToIndexMap.TryGetValue(orientation, out byte orientationIndex))
        {
            orientationIndex = 0; // Default to Front if invalid orientation provided
            Debug.LogWarning($"Invalid orientation {orientation} provided for packing. Defaulting to Front (1).");
        }
        packedData |= (ushort)((orientationIndex & 0x3) << ORIENTATION_SHIFT); // Orientation: Ensure only 2 bits

        return packedData;
    }

    // --- Unpacking ---
    public static byte GetId(ushort packedData)
    {
        return (byte)((packedData & ID_MASK) >> ID_SHIFT);
    }

    public static byte GetLight(ushort packedData)
    {
        return (byte)((packedData & LIGHT_MASK) >> LIGHT_SHIFT);
    }

    public static byte GetOrientation(ushort packedData)
    {
        byte orientationIndex = (byte)((packedData & ORIENTATION_MASK) >> ORIENTATION_SHIFT);
        // Safely get orientation from map, default to Front (index 0) if somehow out of bounds
        return (orientationIndex < OrientationMap.Length) ? OrientationMap[orientationIndex] : OrientationMap[0];
    }

    // --- Setters (Return new packed value) ---
    public static ushort SetId(ushort packedData, byte id)
    {
        return (ushort)((packedData & ~ID_MASK) | (id & 0xFF)); // Clear old, OR in new
    }

    public static ushort SetLight(ushort packedData, byte lightLevel)
    {
        return (ushort)((packedData & ~LIGHT_MASK) | ((lightLevel & 0xF) << LIGHT_SHIFT)); // Clear old, OR in new (masked+shifted)
    }

    public static ushort SetOrientation(ushort packedData, byte orientation)
    {
        if (!OrientationToIndexMap.TryGetValue(orientation, out byte orientationIndex))
        {
            orientationIndex = 0; // Default to Front
            Debug.LogWarning($"Invalid orientation {orientation} provided for setting. Defaulting to Front (1).");
        }

        return (ushort)((packedData & ~ORIENTATION_MASK) | ((orientationIndex & 0x3) << ORIENTATION_SHIFT)); // Clear old, OR in new (masked+shifted)
    }
}