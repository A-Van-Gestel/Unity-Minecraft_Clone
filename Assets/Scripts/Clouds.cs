using System.Collections.Generic;
using UnityEngine;

public class Clouds : MonoBehaviour
{
    public int cloudHeight = 100;
    public int cloudDepth = 4;

    [SerializeField]
    private Texture2D _cloudPattern = null;

    [SerializeField]
    private Material _cloudMaterial = null;

    [SerializeField]
    private World _world = null;

    private bool[,] _cloudData; // Array of bools representing where cloud is.
    private int _cloudTexWidth;
    private int _cloudTileSize;
    private Vector3Int _offset;

    private readonly Dictionary<Vector2Int, GameObject> _clouds = new Dictionary<Vector2Int, GameObject>();

    // A flag to ensure we don't try to update before we're ready.
    private bool _isInitialized = false;

    // Awake() for dependencies that don't rely on other scripts' Start()
    private void Awake()
    {
        // Null check is important here for build vs editor asset handling
        if (_cloudPattern == null)
        {
            Debug.LogError("Cloud Pattern Texture is not assigned in the Inspector!");
            enabled = false; // Disable the script if texture is missing.
            return;
        }

        _cloudTexWidth = _cloudPattern.width;
        _cloudTileSize = VoxelData.ChunkWidth;
        _offset = new Vector3Int(-(_cloudTexWidth / 2), 0, -(_cloudTexWidth / 2));
        transform.position = new Vector3(VoxelData.WorldCentre, cloudHeight, VoxelData.WorldCentre);
    }

    // This is our new public initialization method.
    public void Initialize()
    {
        if (_isInitialized) return;

        LoadCloudData();
        CreateClouds();

        // Initialization is done.
        _isInitialized = true;

        // Update clouds to set initial positions.
        UpdateClouds();
    }

    private void LoadCloudData()
    {
        // Ensure the texture is readable. If not, disable clouds to prevent errors.
        if (!_cloudPattern.isReadable)
        {
            Debug.LogError("Cloud Pattern texture is not marked as Read/Write Enabled in its import settings. Cannot load cloud data.");
            enabled = false; // Disable clouds if we can't read the texture.
            return;
        }

        _cloudData = new bool[_cloudTexWidth, _cloudTexWidth];
        Color[] cloudTex = _cloudPattern.GetPixels();

        // Loop through color array and set bools depending on opacity of color.
        for (int x = 0; x < _cloudTexWidth; x++)
        {
            for (int y = 0; y < _cloudTexWidth; y++)
            {
                _cloudData[x, y] = cloudTex[y * _cloudTexWidth + x].a > 0;
            }
        }
    }

    private void CreateClouds()
    {
        if (_world.settings.clouds == CloudStyle.Off)
            return;

        for (int x = 0; x < _cloudTexWidth; x += _cloudTileSize)
        {
            for (int y = 0; y < _cloudTexWidth; y += _cloudTileSize)
            {
                Mesh cloudMesh;
                switch (_world.settings.clouds)
                {
                    case CloudStyle.Fast:
                        cloudMesh = CreateFastCloudMesh(x, y);
                        break;
                    case CloudStyle.Fancy:
                        cloudMesh = CreateFancyCloudMesh(x, y);
                        break;
                    case CloudStyle.Off:
                        cloudMesh = null;
                        break;
                    default:
                        Debug.LogError("Unknown Cloud Style");
                        cloudMesh = null;
                        break;
                }

                // If we don't have a mesh, skip to the next tile.
                if (cloudMesh is null) continue;

                Vector3 position = new Vector3(x, cloudHeight, y);

                // Doesn't seem to be needed --> Center the clouds based around the center of the world
                // position += transform.position - new Vector3(cloudTexWidth / 2f, 0, cloudTexWidth / 2f);
                // position.y = cloudHeight;

                _clouds.Add(CloudTilePosFromVector3(position), CreateCloudTile(cloudMesh, position));
            }
        }
    }

    public void UpdateClouds()
    {
        // Don't run if not initialized or clouds are off.
        if (!_isInitialized || _world.settings.clouds == CloudStyle.Off)
            return;

        for (int x = 0; x < _cloudTexWidth; x += _cloudTileSize)
        {
            for (int y = 0; y < _cloudTexWidth; y += _cloudTileSize)
            {
                Vector3 position = _world.player.transform.position + new Vector3(x, 0, y) + _offset;
                position = new Vector3(RoundToCloud(position.x), cloudHeight, RoundToCloud(position.z));
                Vector2Int cloudPosition = CloudTilePosFromVector3(position);

                // Check to prevent "KeyNotFoundException", though it shouldn't happen now.
                if (_clouds.TryGetValue(cloudPosition, out GameObject cloud))
                {
                    cloud.transform.position = position;
                }
            }
        }
    }

    private int RoundToCloud(float value)
    {
        return Mathf.FloorToInt(value / _cloudTileSize) * _cloudTileSize;
    }

    private Mesh CreateFastCloudMesh(int x, int z)
    {
        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        List<Vector3> normals = new List<Vector3>();
        int vertCount = 0;

        for (int xIncrement = 0; xIncrement < _cloudTileSize; xIncrement++)
        {
            for (int zIncrement = 0; zIncrement < _cloudTileSize; zIncrement++)
            {
                int xVal = x + xIncrement;
                int zVal = z + zIncrement;

                if (_cloudData[xVal, zVal])
                {
                    // Add 4 vertices for cloud face.
                    vertices.Add(new Vector3(xIncrement, 0, zIncrement));
                    vertices.Add(new Vector3(xIncrement, 0, zIncrement + 1));
                    vertices.Add(new Vector3(xIncrement + 1, 0, zIncrement + 1));
                    vertices.Add(new Vector3(xIncrement + 1, 0, zIncrement));

                    // We know what direction our faces are facing, so we just add them directly.
                    for (int i = 0; i < 4; i++)
                        normals.Add(Vector3.down);

                    // As we are looking at them from the bottom, we need to add our triangles anti-clockwise.
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
        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.normals = normals.ToArray();

        return mesh;
    }

    private Mesh CreateFancyCloudMesh(int x, int z)
    {
        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        List<Vector3> normals = new List<Vector3>();
        int vertCount = 0;

        for (int xIncrement = 0; xIncrement < _cloudTileSize; xIncrement++)
        {
            for (int zIncrement = 0; zIncrement < _cloudTileSize; zIncrement++)
            {
                int xVal = x + xIncrement;
                int zVal = z + zIncrement;

                if (_cloudData[xVal, zVal])
                {
                    // Loop though neighbour points using faceCheck array.
                    for (int p = 0; p < 6; p++)
                    {
                        // If the current neighbour has no cloud, draw this face.
                        if (!CheckCloudData(new Vector3Int(xVal, 0, zVal) + VoxelData.FaceChecks[p]))
                        {
                            // Add our 4 vertices for this face.
                            for (int i = 0; i < 4; i++)
                            {
                                Vector3 vert = new Vector3Int(xIncrement, 0, zIncrement);
                                int vertIndex = VoxelData.VoxelTris[p * 4 + i];
                                vert += VoxelData.VoxelVerts[vertIndex];
                                vertices.Add(vert);
                            }

                            for (int i = 0; i < 4; i++)
                                normals.Add(VoxelData.FaceChecks[p]);

                            triangles.Add(vertCount);
                            triangles.Add(vertCount + 1);
                            triangles.Add(vertCount + 2);
                            triangles.Add(vertCount + 2);
                            triangles.Add(vertCount + 1);
                            triangles.Add(vertCount + 3);

                            vertCount += 4;
                        }
                    }
                }
            }
        }

        Mesh mesh = new Mesh();
        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.normals = normals.ToArray();

        return mesh;
    }

    // Returns true or false depending on if there is cloud at the given point.
    private bool CheckCloudData(Vector3Int point)
    {
        // Because clouds are 2D, if y is above or below 0, return false.
        if (point.y != 0)
            return false;

        int x = point.x;
        int z = point.z;

        // If the x or z value is outside the cloudData range, wrap it around.
        if (point.x < 0) x = _cloudTexWidth - 1;
        if (point.x > _cloudTexWidth - 1) x = 0;
        if (point.z < 0) z = _cloudTexWidth - 1;
        if (point.z > _cloudTexWidth - 1) z = 0;

        return _cloudData[x, z];
    }

    private GameObject CreateCloudTile(Mesh mesh, Vector3 position)
    {
        GameObject newCloudTile = new GameObject();
        newCloudTile.transform.position = position;
        newCloudTile.transform.parent = transform;
        newCloudTile.name = $"Cloud {position.x}, {position.z}";
        MeshFilter mF = newCloudTile.AddComponent<MeshFilter>();
        MeshRenderer mR = newCloudTile.AddComponent<MeshRenderer>();

        mR.material = _cloudMaterial;
        mF.mesh = mesh;

        return newCloudTile;
    }

    private Vector2Int CloudTilePosFromVector3(Vector3 pos)
    {
        return new Vector2Int(CloudTileCoordFromFloat(pos.x), CloudTileCoordFromFloat(pos.z));
    }

    private int CloudTileCoordFromFloat(float value)
    {
        float a = value / _cloudTexWidth; // Gets the position using cloudTexture width as units.
        a -= Mathf.FloorToInt(a); // Subtract whole nums to get a 0-1 value representing position in cloud texture.
        int b = Mathf.FloorToInt(_cloudTexWidth * a); // Multiply cloud texture width by 'a' to get position in texture globally.

        return b;
    }
}

public enum CloudStyle
{
    Off,
    Fast,
    Fancy,
}