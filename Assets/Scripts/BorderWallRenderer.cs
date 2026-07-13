using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Renders the per-world gameplay border (TF-14 Phase 2) as a translucent, animated wall at the
/// world-border AABB faces. Four camera-following quads slide along their edge to track the player
/// and are culled beyond the terrain draw distance; the shader animates and distance-fades them.
/// Purely visual — reads <see cref="World.BorderRadius"/> and never touches the voxel pipeline
/// (generation, lighting, meshing, storage). Hidden entirely when the border is disabled (radius 0).
/// </summary>
public class BorderWallRenderer : MonoBehaviour
{
    [SerializeField]
    private World _world = null;

    [SerializeField]
    private Material _borderMaterial = null;

    [Tooltip("Extra voxels of wall drawn past the terrain view distance on each side, so the fence is visible slightly before terrain culls in.")]
    [SerializeField]
    private int _drawMargin = 16;

    [Tooltip("Nudges the wall this many voxels off the voxel boundary so it doesn't Z-fight the terrain faces at ±radius. Negative moves it inward (in front of the boundary terrain); positive pushes it outward/deeper.")]
    [SerializeField]
    private float _depthOffset = -0.001f;

    private MeshRenderer _meshRenderer;
    private Mesh _mesh;

    // Reused per-frame buffers (cleared, never reallocated) to stay GC-free in LateUpdate.
    private readonly List<Vector3> _vertices = new List<Vector3>(16);
    private readonly List<Vector2> _uvs = new List<Vector2>(16);
    private readonly List<int> _triangles = new List<int>(24);

    private bool _isInitialized;

    /// <summary>
    /// Builds the mesh + renderer and anchors the object at the world origin (its mesh is authored in
    /// world coordinates). Safe to call more than once; call after the world and player exist.
    /// </summary>
    public void Initialize()
    {
        if (_isInitialized) return;

        if (_borderMaterial == null)
        {
            Debug.LogError("[BorderWallRenderer] Border Material is not assigned in the Inspector. Disabling.");
            enabled = false;
            return;
        }

        // Mesh vertices are authored in world space, so the transform must be identity.
        transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        transform.localScale = Vector3.one;

        MeshFilter meshFilter = GetComponent<MeshFilter>();
        if (meshFilter == null) meshFilter = gameObject.AddComponent<MeshFilter>();
        _meshRenderer = GetComponent<MeshRenderer>();
        if (_meshRenderer == null) _meshRenderer = gameObject.AddComponent<MeshRenderer>();

        _mesh = new Mesh { name = "BorderWall" };
        _mesh.MarkDynamic(); // rebuilt every frame while visible
        meshFilter.mesh = _mesh;

        _meshRenderer.sharedMaterial = _borderMaterial;
        _meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _meshRenderer.receiveShadows = false;
        _meshRenderer.enabled = false;

        _isInitialized = true;
    }

    private void LateUpdate()
    {
        if (!_isInitialized) return;

        // No border, or player not ready yet → hide.
        if (_world == null || _world.player == null || _world.BorderRadius <= 0)
        {
            if (_meshRenderer.enabled) _meshRenderer.enabled = false;
            return;
        }

        RebuildMesh();
    }

    /// <summary>
    /// Rebuilds the four face quads around the player's current position, clamped to the border
    /// extent so corners join cleanly and each face is dropped when it is beyond the draw distance.
    /// </summary>
    private void RebuildMesh()
    {
        _vertices.Clear();
        _uvs.Clear();
        _triangles.Clear();

        float r = _world.BorderRadius;
        const float h = VoxelData.ChunkHeight;
        float drawWidth = _world.settings.viewDistance * VoxelData.ChunkWidth + _drawMargin;
        Vector3 p = _world.player.transform.position;

        // Offset the faces off the exact voxel boundary (and extend the parallel span to match) so the
        // wall doesn't Z-fight the terrain faces at ±radius, while corners still meet at (±ext, ±ext).
        float ext = r + _depthOffset;

        // +X / -X faces slide along Z; +Z / -Z faces slide along X.
        AddFace(true, ext, p.x, p.z, ext, h, drawWidth);
        AddFace(true, -ext, p.x, p.z, ext, h, drawWidth);
        AddFace(false, ext, p.z, p.x, ext, h, drawWidth);
        AddFace(false, -ext, p.z, p.x, ext, h, drawWidth);

        _mesh.Clear();
        _mesh.SetVertices(_vertices);
        _mesh.SetUVs(0, _uvs);
        _mesh.SetTriangles(_triangles, 0);

        _meshRenderer.enabled = _vertices.Count > 0;
    }

    /// <summary>
    /// Adds one border face if the player is within draw distance of it. <paramref name="axisIsX"/>
    /// selects the perpendicular axis: true for the ±X faces (which slide along Z), false for the ±Z
    /// faces (which slide along X).
    /// </summary>
    /// <param name="plane">World coordinate of the face on its perpendicular axis (±radius).</param>
    /// <param name="playerPerp">Player position on the perpendicular axis.</param>
    /// <param name="playerParallel">Player position on the parallel (slide) axis.</param>
    /// <param name="parallelLimit">Half-extent along the slide axis (border extent incl. the depth offset); clamps corners.</param>
    /// <param name="h">Wall height.</param>
    /// <param name="drawWidth">Half-window drawn along the edge, and the per-face cull distance.</param>
    private void AddFace(bool axisIsX, float plane, float playerPerp, float playerParallel, float parallelLimit, float h, float drawWidth)
    {
        if (Mathf.Abs(plane - playerPerp) > drawWidth) return; // face beyond draw distance

        float min = Mathf.Max(-parallelLimit, playerParallel - drawWidth);
        float max = Mathf.Min(parallelLimit, playerParallel + drawWidth);
        if (max - min < 0.01f) return; // degenerate window

        int i = _vertices.Count;
        if (axisIsX)
        {
            _vertices.Add(new Vector3(plane, 0, min));
            _vertices.Add(new Vector3(plane, 0, max));
            _vertices.Add(new Vector3(plane, h, max));
            _vertices.Add(new Vector3(plane, h, min));
        }
        else
        {
            _vertices.Add(new Vector3(min, 0, plane));
            _vertices.Add(new Vector3(max, 0, plane));
            _vertices.Add(new Vector3(max, h, plane));
            _vertices.Add(new Vector3(min, h, plane));
        }

        // uv.x = world distance along the edge (world-anchored bands), uv.y = world height.
        _uvs.Add(new Vector2(min, 0));
        _uvs.Add(new Vector2(max, 0));
        _uvs.Add(new Vector2(max, h));
        _uvs.Add(new Vector2(min, h));

        _triangles.Add(i);
        _triangles.Add(i + 1);
        _triangles.Add(i + 2);
        _triangles.Add(i);
        _triangles.Add(i + 2);
        _triangles.Add(i + 3);
    }
}
