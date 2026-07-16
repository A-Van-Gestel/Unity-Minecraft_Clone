using System.Collections.Generic;
using Helpers;
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

    [Tooltip("Inflates the cloud hull outward along vertex normals by this many units, off the voxel grid, so no cloud face (top, bottom, or sides) Z-fights terrain — without opening seams between tiles. Increase if Z-fighting persists at distance.")]
    [SerializeField]
    private float _depthOffset = 0.0035f;

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
    }

    // This is our new public initialization method.
    public void Initialize()
    {
        if (_isInitialized) return;

        AnchorRoot();

        LoadCloudData();
        CreateClouds();

        // Initialization is done.
        _isInitialized = true;

        // Update clouds to set initial positions.
        UpdateClouds();
    }

    /// <summary>
    /// Places the cloudscape's root. Deliberately not in Awake: DefaultSpawnPosition is voxel space, so it converts
    /// through the origin, and Awake runs before World has anchored it. (The tiles themselves don't depend on this —
    /// UpdateClouds positions each one in Unity space directly.)
    /// </summary>
    private void AnchorRoot()
    {
        transform.position = WorldOrigin.VoxelToUnity(
            new Vector3(VoxelData.DefaultSpawnPosition, cloudHeight, VoxelData.DefaultSpawnPosition));
    }

    /// <summary>
    /// Re-anchors the cloudscape after a floating-origin shift (WS-4b). The root is world-anchored, so it re-derives;
    /// the tiles are re-placed immediately rather than waiting for the next chunk crossing to drive UpdateClouds.
    /// </summary>
    public void Reanchor()
    {
        if (!_isInitialized) return;

        AnchorRoot();
        UpdateClouds();
    }

    /// <summary>
    /// Destroys all existing cloud tiles and re-creates them with the current cloud style setting.
    /// Called when the cloud style is changed at runtime (e.g. from the pause menu settings).
    /// </summary>
    public void Reinitialize()
    {
        foreach (GameObject cloud in _clouds.Values)
        {
            if (cloud != null) Destroy(cloud);
        }

        _clouds.Clear();
        _isInitialized = false;
        Initialize();
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

                // x/y are already pattern-space (0.._cloudTexWidth), so they ARE the key — no wrap, and in
                // particular no origin conversion: unlike UpdateClouds, this loop never holds a Unity-space value.
                _clouds.Add(new Vector2Int(x, y), CreateCloudTile(cloudMesh, position));
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
                // Unity space: the tile grid follows the player, so it re-anchors across a shift for free.
                Vector3 position = _world.player.transform.position + new Vector3(x, 0, y) + _offset;
                position = new Vector3(RoundToCloud(position.x), cloudHeight, RoundToCloud(position.z));

                // The pattern, unlike the tiles, is anchored to the WORLD — so the lookup converts to voxel space.
                // Without this the whole cloudscape would jump to a different part of the pattern on a re-anchor.
                Vector2Int cloudPosition = CloudTileKeyFromVoxel(WorldOrigin.UnityToVoxelCell(position));

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
                    // Push the single down-facing quad outward (below the plane) along its normal, off the
                    // voxel grid, to avoid Z-fighting. Fast tiles have no side faces, so no seams to worry about.
                    Vector3 o = Vector3.down * _depthOffset;

                    // Add 4 vertices for cloud face.
                    vertices.Add(new Vector3(xIncrement, 0, zIncrement) + o);
                    vertices.Add(new Vector3(xIncrement, 0, zIncrement + 1) + o);
                    vertices.Add(new Vector3(xIncrement + 1, 0, zIncrement + 1) + o);
                    vertices.Add(new Vector3(xIncrement + 1, 0, zIncrement) + o);

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
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.SetNormals(normals);

        return mesh;
    }

    private Mesh CreateFancyCloudMesh(int x, int z)
    {
        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        List<Vector3> normals = new List<Vector3>();
        int vertCount = 0;

        // Integer cube corner of face p's i-th vertex, in local tile coordinates (VoxelVerts are 0/1).
        Vector3Int CornerOf(int xi, int zi, int p, int i)
        {
            Vector3 vv = VoxelData.VoxelVerts[VoxelData.VoxelTris[p * 4 + i]];
            return new Vector3Int(xi + Mathf.RoundToInt(vv.x), Mathf.RoundToInt(vv.y), zi + Mathf.RoundToInt(vv.z));
        }

        // Pass 1: collect the exposed hull faces and, per shared corner, accumulate the summed normal of
        // every face meeting there. Displacing each corner along that summed normal inflates the whole hull
        // outward as one watertight shell — every face (top, bottom, sides) lifts off the voxel grid to avoid
        // Z-fighting, while faces sharing an edge move together so no seams open between cells or tiles.
        List<(int xi, int zi, int p)> faces = new List<(int xi, int zi, int p)>();
        Dictionary<Vector3Int, Vector3> cornerNormals = new Dictionary<Vector3Int, Vector3>();

        for (int xIncrement = 0; xIncrement < _cloudTileSize; xIncrement++)
        {
            for (int zIncrement = 0; zIncrement < _cloudTileSize; zIncrement++)
            {
                int xVal = x + xIncrement;
                int zVal = z + zIncrement;

                if (!_cloudData[xVal, zVal])
                    continue;

                // Loop though neighbor points using faceCheck array.
                for (int p = 0; p < 6; p++)
                {
                    // Only faces whose neighbor has no cloud are on the hull; internal faces are skipped
                    // (and so carry no offset).
                    if (CheckCloudData(new Vector3Int(xVal, 0, zVal) + VoxelData.FaceChecks[p]))
                        continue;

                    faces.Add((xIncrement, zIncrement, p));

                    Vector3 faceNormal = VoxelData.FaceChecks[p];
                    for (int i = 0; i < 4; i++)
                    {
                        Vector3Int corner = CornerOf(xIncrement, zIncrement, p, i);
                        cornerNormals[corner] = (cornerNormals.TryGetValue(corner, out Vector3 acc) ? acc : Vector3.zero) + faceNormal;
                    }
                }
            }
        }

        // Pass 2: emit each hull face with its corners displaced along the (normalized) accumulated normal.
        foreach ((int xi, int zi, int p) in faces)
        {
            for (int i = 0; i < 4; i++)
            {
                Vector3Int corner = CornerOf(xi, zi, p, i);
                Vector3 dir = cornerNormals[corner];
                if (dir != Vector3.zero) dir.Normalize();
                vertices.Add(corner + dir * _depthOffset);
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

        Mesh mesh = new Mesh();
        // Assign via the List-accepting mesh API (vertices before triangles, since triangles
        // index into the vertex buffer and Unity validates on assignment). Avoids the three
        // temporary managed arrays that .ToArray() would allocate per tile (MR-9).
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.SetNormals(normals);

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
#if UNITY_EDITOR
        newCloudTile.name = $"Cloud {position.x}, {position.z}";
#endif
        MeshFilter mF = newCloudTile.AddComponent<MeshFilter>();
        MeshRenderer mR = newCloudTile.AddComponent<MeshRenderer>();

        mR.material = _cloudMaterial;
        mF.mesh = mesh;

        return newCloudTile;
    }

    /// <summary>
    /// Wraps an absolute voxel-space cell onto the repeating cloud pattern, yielding the tile's dictionary key.
    /// </summary>
    /// <param name="voxelCell">The absolute voxel-space cell the tile sits over.</param>
    /// <returns>The pattern-space tile key, both components in <c>[0, _cloudTexWidth)</c>.</returns>
    private Vector2Int CloudTileKeyFromVoxel(Vector3Int voxelCell)
    {
        return new Vector2Int(WrapToPattern(voxelCell.x), WrapToPattern(voxelCell.z));
    }

    /// <summary>
    /// Positive-modulo wrap of a voxel coordinate onto the pattern width.
    /// </summary>
    /// <param name="value">An absolute voxel coordinate on one axis.</param>
    /// <returns>The wrapped coordinate in <c>[0, _cloudTexWidth)</c>.</returns>
    // Integer modulo, not the old float `frac` idiom: exact at any distance from the origin, where dividing a large
    // world coordinate by the pattern width would quantize the result and stripe the pattern.
    private int WrapToPattern(int value)
    {
        int wrapped = value % _cloudTexWidth;
        return wrapped < 0 ? wrapped + _cloudTexWidth : wrapped;
    }
}

public enum CloudStyle
{
    Off,
    Fast,
    Fancy,
}
