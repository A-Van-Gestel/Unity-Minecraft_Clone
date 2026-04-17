using System;
using Helpers;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

public class SectionRenderer
{
    public readonly GameObject GameObject;
    private readonly MeshRenderer _meshRenderer;
    private readonly Mesh _mesh;

    // Define the vertex layout once to ensure consistency and avoid allocation.
    private static readonly VertexAttributeDescriptor[] s_layout =
    {
        new VertexAttributeDescriptor(VertexAttribute.Position),
        new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4, stream: 1),
        new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4, stream: 2),
        new VertexAttributeDescriptor(VertexAttribute.Normal, stream: 3),
    };

    public SectionRenderer(Transform parent, int sectionIndex)
    {
        GameObject = new GameObject($"Section_{sectionIndex}");
        GameObject.transform.SetParent(parent);
        GameObject.transform.localPosition = new Vector3(0, sectionIndex * ChunkMath.SECTION_SIZE, 0);
        GameObject.transform.localRotation = Quaternion.identity;

        MeshFilter meshFilter = GameObject.AddComponent<MeshFilter>();
        _meshRenderer = GameObject.AddComponent<MeshRenderer>();
        _meshRenderer.shadowCastingMode = ShadowCastingMode.TwoSided;

        _mesh = new Mesh();
        // Mark dynamic to hint to the driver that this mesh changes frequently.
        _mesh.MarkDynamic();
        meshFilter.mesh = _mesh;
    }

    /// <summary>
    /// Updates the mesh using the Advanced Mesh API.
    /// <para>
    /// Uses <see cref="Mesh.SetSubMeshes{T}(NativeArray{T}, int, int, MeshUpdateFlags)"/> for atomic updates.
    /// This is critical to prevent Unity from validating new submesh descriptors against stale index buffer data.
    /// </para>
    /// </summary>
    public void UpdateMeshNative(
        NativeArray<Vector3> verts, NativeArray<Vector4> uvs, NativeArray<Color> colors, NativeArray<Vector3> normals, int vertexStart, int vertexCount,
        NativeArray<int> opaqueTris, int opaqueStart, int opaqueCount,
        NativeArray<int> transparentTris, int transparentStart, int transparentCount,
        NativeArray<int> fluidTris, int fluidStart, int fluidCount)
    {
        // Optimization: Disable game object if the mesh is empty to save render calls.
        if (vertexCount == 0)
        {
            if (GameObject.activeSelf) GameObject.SetActive(false);
            return;
        }

        if (!GameObject.activeSelf) GameObject.SetActive(true);

        // Define flags to skip expensive validation checks during bulk updates for performance.
        const MeshUpdateFlags flags = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds;

        // --- 1. Setup Vertex Buffer ---
        _mesh.SetVertexBufferParams(vertexCount, s_layout);
        _mesh.SetVertexBufferData(verts, vertexStart, 0, vertexCount, 0, flags);
        _mesh.SetVertexBufferData(uvs, vertexStart, 0, vertexCount, 1, flags);
        _mesh.SetVertexBufferData(colors, vertexStart, 0, vertexCount, 2, flags);
        _mesh.SetVertexBufferData(normals, vertexStart, 0, vertexCount, 3, flags);

        // --- 2. Setup Index Buffer ---
        int totalIndices = opaqueCount + transparentCount + fluidCount;
        _mesh.SetIndexBufferParams(totalIndices, IndexFormat.UInt32);

        // Use a temporary NativeArray to store descriptors for atomic assignment.
        NativeArray<SubMeshDescriptor> descriptors = new NativeArray<SubMeshDescriptor>(3, Allocator.Temp);
        Material[] materialBuffer = new Material[3];

        int activeSubMeshCount = 0;
        int currentIndexOffset = 0;

        // Add submeshes in order
        AddSubMesh(opaqueTris, opaqueStart, opaqueCount, World.Instance.OpaqueMaterial);
        AddSubMesh(transparentTris, transparentStart, transparentCount, World.Instance.TransparentMaterial);
        AddSubMesh(fluidTris, fluidStart, fluidCount, World.Instance.LiquidMaterial);

        // --- 3. Apply Submeshes Atomically ---
        // This overwrites all previous submesh data in one go, preventing overlap warnings.
        _mesh.SetSubMeshes(descriptors, 0, activeSubMeshCount, flags);

        descriptors.Dispose();

        // --- 4. Update Materials ---
        // Resize material array to exact count if needed to avoid null materials on the renderer.
        if (activeSubMeshCount < materialBuffer.Length)
        {
            Array.Resize(ref materialBuffer, activeSubMeshCount);
        }

        _meshRenderer.sharedMaterials = materialBuffer;

        // --- 5. Finalize ---
        _mesh.RecalculateBounds();
        return;

        // Local helper to reduce code duplication for adding submeshes
        void AddSubMesh(NativeArray<int> indices, int start, int count, Material material)
        {
            if (count <= 0) return;

            // Upload indices to the mesh buffer
            _mesh.SetIndexBufferData(indices, start, currentIndexOffset, count, flags);

            // Create descriptor
            descriptors[activeSubMeshCount] = new SubMeshDescriptor(currentIndexOffset, count)
            {
                firstVertex = 0,
                vertexCount = vertexCount,
            };

            // Assign material to buffer
            materialBuffer[activeSubMeshCount] = material;

            // Advance counters
            currentIndexOffset += count;
            activeSubMeshCount++;
        }
    }

    #region Lifecycle

    /// <summary>
    /// Clears the mesh data and disables the object for pooling.
    /// Does NOT destroy the mesh or object, preserving memory allocation.
    /// </summary>
    public void Clear()
    {
        if (_mesh != null) _mesh.Clear();
        if (GameObject != null) GameObject.SetActive(false);
    }

    /// <summary>
    /// Destroys the mesh destroys the GameObject, fully removing this section from the scene.
    /// </summary>
    public void Destroy()
    {
        if (_mesh != null) Object.Destroy(_mesh);
        if (GameObject != null) Object.Destroy(GameObject);
    }

    #endregion
}
