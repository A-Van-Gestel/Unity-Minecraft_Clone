using System.Collections.Generic;
using Helpers;
using UnityEngine;
using UnityEngine.Pool;

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

    // Cloud tiles are deliberately larger than terrain chunks: coverage now scales with render distance,
    // so fewer, bigger tiles keep the GameObject count bounded (identical pattern tiles share one mesh).
    // Must divide the pattern texture width.
    private const int CLOUD_TILE_SIZE = 64;

    // Coverage radius in chunks = viewDistance * this. Clouds sit high above the terrain, so extending
    // them past the render distance keeps the sky filled to the horizon instead of ending mid-view.
    private const int VIEW_DISTANCE_MULTIPLIER = 2;

    // Floor so very low render distances still get a believable sky instead of a small patch overhead.
    private const int MIN_COVERAGE_RADIUS_CHUNKS = 8;

    private bool[,] _cloudData; // Array of bools representing where cloud is.
    private int _cloudTexWidth;
    private int _cloudTileSize;

    // Shared mesh per pattern tile (key = pattern-space tile origin). A null value marks a tile the
    // pattern leaves empty, so repeat lookups skip both the mesh build and the GameObject.
    private readonly Dictionary<Vector2Int, Mesh> _tileMeshes = new Dictionary<Vector2Int, Mesh>();

    // Live tile instances keyed by WORLD tile index (voxel cell / tile size) — unlike the old
    // pattern-space keying, the same pattern tile can appear multiple times once the coverage
    // radius exceeds one pattern period.
    private readonly Dictionary<Vector2Int, MeshFilter> _clouds = new Dictionary<Vector2Int, MeshFilter>();

    // Inactive tile GameObjects, reused as the covered area moves with the player.
    private readonly Stack<MeshFilter> _tilePool = new Stack<MeshFilter>();

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
        _cloudTileSize = Mathf.Min(CLOUD_TILE_SIZE, _cloudTexWidth);

        if (_cloudTexWidth % _cloudTileSize != 0)
        {
            Debug.LogError($"Cloud Pattern width ({_cloudTexWidth}) must be divisible by the cloud tile size ({_cloudTileSize}).");
            enabled = false;
        }
    }

    /// <summary>
    /// Loads the cloud pattern and places the initial tile set around the player.
    /// Called by <see cref="World"/> once the world (and its origin) is ready.
    /// </summary>
    public void Initialize()
    {
        if (_isInitialized) return;

        AnchorRoot();

        LoadCloudData();
        if (_cloudData == null) return; // Unreadable pattern texture — LoadCloudData already disabled us.

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
    /// Destroys all existing cloud tiles and shared meshes, then re-creates them with the current cloud
    /// style setting. Called when the cloud style is changed at runtime (e.g. from the pause menu settings).
    /// </summary>
    public void Reinitialize()
    {
        foreach (MeshFilter tile in _clouds.Values)
        {
            if (tile != null) Destroy(tile.gameObject);
        }

        _clouds.Clear();

        while (_tilePool.Count > 0)
        {
            MeshFilter tile = _tilePool.Pop();
            if (tile != null) Destroy(tile.gameObject);
        }

        // The meshes are shared assets owned by this component, not by the tiles — destroy them explicitly.
        foreach (Mesh mesh in _tileMeshes.Values)
        {
            if (mesh != null) Destroy(mesh);
        }

        _tileMeshes.Clear();
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

    /// <summary>
    /// Streams cloud tiles to cover the current coverage radius around the player: releases tiles that
    /// fell out of range, then places (pooled) instances for every in-range world tile whose pattern
    /// cell has geometry. In-range tiles are always re-positioned, so a floating-origin shift is
    /// absorbed by the same pass.
    /// </summary>
    public void UpdateClouds()
    {
        // Don't run if not initialized or clouds are off.
        if (!_isInitialized || _world.settings.clouds == CloudStyle.Off)
            return;

        // The pattern is anchored to the WORLD, so tile indexing runs in voxel space — otherwise the
        // whole cloudscape would jump to a different part of the pattern on an origin re-anchor.
        Vector3Int playerVoxelCell = WorldOrigin.UnityToVoxelCell(_world.player.transform.position);
        int centerTileX = ChunkMath.FloorDiv(playerVoxelCell.x, _cloudTileSize);
        int centerTileZ = ChunkMath.FloorDiv(playerVoxelCell.z, _cloudTileSize);
        int radiusTiles = Mathf.CeilToInt(CoverageRadiusInBlocks() / (float)_cloudTileSize);

        // Release out-of-range tiles first so their GameObjects can be reused by this same pass.
        List<Vector2Int> stale = ListPool<Vector2Int>.Get();
        foreach (KeyValuePair<Vector2Int, MeshFilter> pair in _clouds)
        {
            if (Mathf.Abs(pair.Key.x - centerTileX) > radiusTiles || Mathf.Abs(pair.Key.y - centerTileZ) > radiusTiles)
                stale.Add(pair.Key);
        }

        foreach (Vector2Int key in stale)
            ReleaseTile(key);

        ListPool<Vector2Int>.Release(stale);

        for (int tileX = centerTileX - radiusTiles; tileX <= centerTileX + radiusTiles; tileX++)
        {
            for (int tileZ = centerTileZ - radiusTiles; tileZ <= centerTileZ + radiusTiles; tileZ++)
            {
                Vector2Int worldTile = new Vector2Int(tileX, tileZ);
                Vector3 unityPos = WorldOrigin.VoxelToUnity(
                    new Vector3(tileX * _cloudTileSize, cloudHeight, tileZ * _cloudTileSize));

                if (_clouds.TryGetValue(worldTile, out MeshFilter existing))
                {
                    existing.transform.position = unityPos;
                    continue;
                }

                Mesh mesh = GetTileMesh(worldTile);
                if (mesh == null) continue; // Pattern leaves this tile empty — nothing to render.

                _clouds.Add(worldTile, AcquireTile(mesh, unityPos, worldTile));
            }
        }
    }

    /// <summary>
    /// The distance (in blocks) clouds extend from the player: double the render distance, floored at
    /// <see cref="MIN_COVERAGE_RADIUS_CHUNKS"/> chunks, so the cloudscape always reaches past the fog line.
    /// </summary>
    /// <returns>The cloud coverage radius in blocks.</returns>
    private int CoverageRadiusInBlocks()
    {
        int radiusChunks = Mathf.Max(
            _world.settings.viewDistance * VIEW_DISTANCE_MULTIPLIER, MIN_COVERAGE_RADIUS_CHUNKS);
        return radiusChunks * VoxelData.ChunkWidth;
    }

    /// <summary>
    /// Returns the shared mesh for the pattern tile under the given world tile, building and caching it
    /// on first use. Returns null for pattern tiles with no cloud pixels.
    /// </summary>
    /// <param name="worldTile">World-space tile index (voxel cell / tile size).</param>
    /// <returns>The shared tile mesh, or null when the pattern tile is empty (or clouds are off).</returns>
    private Mesh GetTileMesh(Vector2Int worldTile)
    {
        Vector2Int patternKey = new Vector2Int(
            WrapToPattern(worldTile.x * _cloudTileSize),
            WrapToPattern(worldTile.y * _cloudTileSize));

        if (_tileMeshes.TryGetValue(patternKey, out Mesh mesh))
            return mesh;

        switch (_world.settings.clouds)
        {
            case CloudStyle.Fast:
                mesh = CreateFastCloudMesh(patternKey.x, patternKey.y);
                break;
            case CloudStyle.Fancy:
                mesh = CreateFancyCloudMesh(patternKey.x, patternKey.y);
                break;
            case CloudStyle.Off:
                mesh = null;
                break;
            default:
                Debug.LogError("Unknown Cloud Style");
                mesh = null;
                break;
        }

        // Cache empty tiles as null so neither the mesh build nor the GameObject is repeated for them.
        if (mesh != null && mesh.vertexCount == 0)
        {
            Destroy(mesh);
            mesh = null;
        }

        _tileMeshes.Add(patternKey, mesh);
        return mesh;
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

    /// <summary>
    /// Takes a tile from the pool (or creates one) and configures it with the given shared mesh and position.
    /// </summary>
    /// <param name="mesh">Shared pattern-tile mesh to render.</param>
    /// <param name="unityPos">Unity-space position of the tile's minimum corner.</param>
    /// <param name="worldTile">World tile index, used only for the editor-facing name.</param>
    /// <returns>The tile's MeshFilter (its GameObject is the tile instance).</returns>
    private MeshFilter AcquireTile(Mesh mesh, Vector3 unityPos, Vector2Int worldTile)
    {
        MeshFilter tile;
        if (_tilePool.Count > 0)
        {
            tile = _tilePool.Pop();
            tile.gameObject.SetActive(true);
        }
        else
        {
            GameObject newCloudTile = new GameObject();
            newCloudTile.transform.parent = transform;
            tile = newCloudTile.AddComponent<MeshFilter>();

            // sharedMaterial: every tile renders with the one cloud material — a per-tile
            // .material copy would break batching and leak instances.
            MeshRenderer mR = newCloudTile.AddComponent<MeshRenderer>();
            mR.sharedMaterial = _cloudMaterial;
        }

        tile.transform.position = unityPos;
        tile.sharedMesh = mesh;
#if UNITY_EDITOR
        tile.gameObject.name = $"Cloud {worldTile.x}, {worldTile.y}";
#endif
        return tile;
    }

    /// <summary>
    /// Deactivates the tile at the given world tile key and returns it to the pool.
    /// </summary>
    /// <param name="worldTile">World tile index of the tile to release.</param>
    private void ReleaseTile(Vector2Int worldTile)
    {
        MeshFilter tile = _clouds[worldTile];
        _clouds.Remove(worldTile);

        if (tile == null) return;

        tile.gameObject.SetActive(false);
        _tilePool.Push(tile);
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
