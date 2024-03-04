using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

public class Clouds : MonoBehaviour
{
    public int cloudHeight = 100;

    [SerializeField] private Texture2D cloudPattern = null;
    [SerializeField] private Material cloudMaterial = null;
    [SerializeField] private World world = null;
    
    private bool[,] cloudData; // Array of bools representing where cloud is.

    private int cloudTexWidth;

    private int cloudTileSize;
    private Vector3Int offset;

    private Dictionary<Vector2Int, GameObject> clouds = new Dictionary<Vector2Int, GameObject>();

    private void Start()
    {
        cloudTexWidth = cloudPattern.width;
        cloudTileSize = VoxelData.ChunkWidth;
        offset = new Vector3Int(-(cloudTexWidth / 2), 0, -(cloudTexWidth / 2));

        transform.position = new Vector3(VoxelData.WorldCentre, cloudHeight, VoxelData.WorldCentre);

        LoadCloudData();
        CreateClouds();
        UpdateClouds();
    }

    private void LoadCloudData()
    {
        cloudData = new bool[cloudTexWidth, cloudTexWidth];
        Color[] cloudTex = cloudPattern.GetPixels();

        // Loop through color array and set bools depending on opacity of color.
        for (int x = 0; x < cloudTexWidth; x++)
        {
            for (int y = 0; y < cloudTexWidth; y++)
            {
                cloudData[x, y] = (cloudTex[y * cloudTexWidth + x].a > 0);
            }
        }
    }

    private void CreateClouds()
    {
        for (int x = 0; x < cloudTexWidth; x += cloudTileSize)
        {
            for (int y = 0; y < cloudTexWidth; y += cloudTileSize)
            {
                Vector3 position = new Vector3(x, cloudHeight, y);
                clouds.Add(CloudTilePosFromVector3(position), CreateCloudTile(CreateCloudMesh(x, y), position));
            }
        }
    }

    public void UpdateClouds()
    {
        for (int x = 0; x < cloudTexWidth; x += cloudTileSize)
        {
            for (int y = 0; y < cloudTexWidth; y += cloudTileSize)
            {
                Vector3 position = world.player.position + new Vector3(x, 0, y) + offset;
                position = new Vector3(RoundToCloud(position.x), cloudHeight, RoundToCloud(position.z));
                Vector2Int cloudPosition = CloudTilePosFromVector3(position);

                clouds[cloudPosition].transform.position = position;
            }
        }
    }

    private int RoundToCloud(float value)
    {
        return Mathf.FloorToInt(value / cloudTileSize) * cloudTileSize;
    }

    private Mesh CreateCloudMesh(int x, int z)
    {
        List<Vector3> vetices = new List<Vector3>();
        List<int> triangles = new List<int>();
        List<Vector3> normals = new List<Vector3>();
        int vertCount = 0;

        for (int xIncrement = 0; xIncrement < cloudTileSize; xIncrement++)
        {
            for (int zIncrement = 0; zIncrement < cloudTileSize; zIncrement++)
            {
                int xVal = x + xIncrement;
                int zVal = z + zIncrement;

                if (cloudData[xVal, zVal])
                {
                    // Add 4 vertices for cloud face.
                    vetices.Add(new Vector3(xIncrement, 0, zIncrement));
                    vetices.Add(new Vector3(xIncrement, 0, zIncrement + 1));
                    vetices.Add(new Vector3(xIncrement + 1, 0, zIncrement + 1));
                    vetices.Add(new Vector3(xIncrement + 1, 0, zIncrement));

                    // We know what direction our faces are facing, so we just add them directly.
                    for (int i = 0; i < 4; i++)
                        normals.Add(Vector3.down);

                    // As we are looking at them from the bottom, we need to add our triangles anti clockwise.
                    // Add first triangle.
                    triangles.Add(vertCount + 1);
                    triangles.Add(vertCount);
                    triangles.Add(vertCount + 2);
                    // Add second triangle.
                    triangles.Add(vertCount + 2);
                    triangles.Add(vertCount);
                    triangles.Add(vertCount + 3);
                    // Increment vertCount
                    vertCount += 4;
                }
            }
        }

        Mesh mesh = new Mesh();
        mesh.vertices = vetices.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.normals = normals.ToArray();

        return mesh;
    }

    private GameObject CreateCloudTile(Mesh mesh, Vector3 position)
    {
        GameObject newCloudTile = new GameObject();
        newCloudTile.transform.position = position;
        newCloudTile.transform.parent = transform;
        newCloudTile.name = $"Cloud {position.x}, {position.z}";
        MeshFilter mF = newCloudTile.AddComponent<MeshFilter>();
        MeshRenderer mR = newCloudTile.AddComponent<MeshRenderer>();

        mR.material = cloudMaterial;
        mF.mesh = mesh;

        return newCloudTile;
    }

    private Vector2Int CloudTilePosFromVector3(Vector3 pos)
    {
        return new Vector2Int(CloudTileCoordFromFloat(pos.x), CloudTileCoordFromFloat(pos.z));
    }

    private int CloudTileCoordFromFloat(float value)
    {
        float a = value / (float)cloudTexWidth;  // Gets the position using cloudTexture width as units.
        a -= Mathf.FloorToInt(a);  // Subtract whole nums to get a 0-1 value representing position in cloud texture.
        int b = Mathf.FloorToInt((float)cloudTexWidth * a);  // Multiply cloud texture width by a to get position in texture globally.

        return b;
    }
}