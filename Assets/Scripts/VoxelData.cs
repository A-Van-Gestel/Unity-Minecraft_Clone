using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public static class VoxelData
{
    public static readonly int ChunkWidth = 16;
    public static readonly int ChunkHeight = 128;
    public static readonly int WorldSizeInChunks = 100;

    public static int WorldSizeInVoxels
    {
        get { return WorldSizeInChunks * ChunkWidth; }
    }

    public static readonly int ViewDistanceInChunks = 5;

    public static readonly int TextureAtlasSizeInBlocks = 4;

    public static float NormalizedBlockTextureSize
    {
        get { return 1f / (float)TextureAtlasSizeInBlocks; }
    }

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

    public static readonly Vector3[] FaceChecks = new Vector3[6]
    {
        new Vector3(0.0f, 0.0f, -1.0f), // Back Face
        new Vector3(0.0f, 0.0f, 1.0f), // Front Face
        new Vector3(0.0f, 1.0f, 0.0f), // Top Face
        new Vector3(0.0f, -1.0f, 0.0f), // Bottom Face
        new Vector3(-1.0f, 0.0f, 0.0f), // Left Face
        new Vector3(1.0f, 0.0f, 0.0f), // Right Face
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
}