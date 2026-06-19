using Data;
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

    private static readonly VertexAttributeDescriptor[] s_layout =
    {
        new VertexAttributeDescriptor(VertexAttribute.Position),
        new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4, stream: 1),
        new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4, stream: 2),
        new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, stream: 3),
        new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.UNorm8, 4, stream: 3),
    };

    /// <summary>
    /// MR-4: a section's geometry is always confined to its 16×16×16 cell (fluid surface heights and
    /// cross meshes stay inside block bounds), so its post-processed section-space vertices lie in
    /// [0, SECTION_SIZE]³. A constant <see cref="Bounds"/> replaces the per-update
    /// <see cref="Mesh.RecalculateBounds"/> vertex scan in <see cref="UpdateMeshNative"/>.
    /// </summary>
    private static readonly Bounds s_sectionBounds = new Bounds(
        new Vector3(ChunkMath.SECTION_SIZE * 0.5f, ChunkMath.SECTION_SIZE * 0.5f, ChunkMath.SECTION_SIZE * 0.5f),
        new Vector3(ChunkMath.SECTION_SIZE, ChunkMath.SECTION_SIZE, ChunkMath.SECTION_SIZE));

    // --- MR-3: cached material combinations ---
    // There are only 8 possible submesh-presence combinations (bit0=opaque, bit1=transparent,
    // bit2=fluid). Each cached array holds the present submeshes' materials in opaque → transparent →
    // fluid order, so UpdateMeshNative never allocates a Material[] in the hot apply path. Index 0
    // (the empty combination) is the empty array — the empty-section path returns before it is read.
    private static readonly Material[][] s_materialCombinations = new Material[8][];
    private static Material s_cachedOpaque;
    private static Material s_cachedTransparent;
    private static Material s_cachedLiquid;

    /// <summary>
    /// Bumped whenever <see cref="s_materialCombinations"/> is rebuilt (the source materials changed
    /// identity). A renderer reassigns <c>sharedMaterials</c> when either its bitmask or this version
    /// differs from its last update, so a global material swap still propagates.
    /// </summary>
    private static int s_materialCacheVersion;

    /// <summary>The submesh-presence bitmask assigned on this section's last update; -1 = none yet (MR-3).</summary>
    private int _lastMaterialMask = -1;

    /// <summary>The <see cref="s_materialCacheVersion"/> observed at this section's last material assignment (MR-3).</summary>
    private int _lastMaterialCacheVersion = -1;

    public SectionRenderer(Transform parent, int sectionIndex)
    {
#if UNITY_EDITOR
        GameObject = new GameObject($"Section_{sectionIndex}");
#else
        GameObject = new GameObject();
#endif
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
        NativeArray<Vector3> verts, NativeArray<Vector4> uvs, NativeArray<Color> colors,
        NativeArray<NormalLightVertex> stream3, int vertexStart, int vertexCount,
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
        _mesh.SetVertexBufferData(stream3, vertexStart, 0, vertexCount, 3, flags);

        // --- 2. Setup Index Buffer ---
        int totalIndices = opaqueCount + transparentCount + fluidCount;
        _mesh.SetIndexBufferParams(totalIndices, IndexFormat.UInt32);

        // The submesh-presence bitmask (bit0=opaque, bit1=transparent, bit2=fluid) fully determines
        // both the submesh order and the material set, so it is the cache key for MR-3.
        int materialMask = (opaqueCount > 0 ? 1 : 0) | (transparentCount > 0 ? 2 : 0) | (fluidCount > 0 ? 4 : 0);

        // Use a temporary NativeArray to store descriptors for atomic assignment.
        NativeArray<SubMeshDescriptor> descriptors = new NativeArray<SubMeshDescriptor>(3, Allocator.Temp);

        int activeSubMeshCount = 0;
        int currentIndexOffset = 0;

        // Add submeshes in order (opaque → transparent → fluid), matching the cached material order.
        AddSubMesh(opaqueTris, opaqueStart, opaqueCount);
        AddSubMesh(transparentTris, transparentStart, transparentCount);
        AddSubMesh(fluidTris, fluidStart, fluidCount);

        // --- 3. Apply Submeshes Atomically ---
        // This overwrites all previous submesh data in one go, preventing overlap warnings.
        _mesh.SetSubMeshes(descriptors, 0, activeSubMeshCount, flags);

        descriptors.Dispose();

        // --- 4. Update Materials (MR-3) ---
        // Pick the cached material combination by bitmask and only touch sharedMaterials when the
        // combination actually changed since this section's last update — assigning sharedMaterials
        // forces a renderer-state update, so skipping it on the (very common) unchanged case removes
        // GC churn and redundant work from the hot apply path. The cache version guards the rare case
        // where the global source materials themselves changed identity.
        EnsureMaterialCacheCurrent();
        if (_lastMaterialMask != materialMask || _lastMaterialCacheVersion != s_materialCacheVersion)
        {
            _meshRenderer.sharedMaterials = s_materialCombinations[materialMask];
            _lastMaterialMask = materialMask;
            _lastMaterialCacheVersion = s_materialCacheVersion;
        }

        // --- 5. Finalize (MR-4) ---
        // Section geometry is confined to its 16³ cell, so assign the constant section-cell bounds
        // instead of scanning every vertex with RecalculateBounds().
        _mesh.bounds = s_sectionBounds;
        return;

        // Local helper to reduce code duplication for adding submeshes
        void AddSubMesh(NativeArray<int> indices, int start, int count)
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

            // Advance counters
            currentIndexOffset += count;
            activeSubMeshCount++;
        }
    }

    /// <summary>
    /// Rebuilds <see cref="s_materialCombinations"/> from the current <see cref="World.Instance"/>
    /// materials if they have changed identity (or on first use). The 8 arrays each hold the present
    /// submeshes' materials in opaque → transparent → fluid order. Main-thread only (called from the
    /// mesh apply path), so the static cache needs no synchronization.
    /// </summary>
    private static void EnsureMaterialCacheCurrent()
    {
        Material opaque = World.Instance.OpaqueMaterial;
        Material transparent = World.Instance.TransparentMaterial;
        Material liquid = World.Instance.LiquidMaterial;

        // ReferenceEquals (identity), not ==: Unity's overloaded == reports a destroyed material as
        // "null", which would mask a genuine material-instance swap. The [7] null check forces the
        // initial build (when all three cached references are still null).
        if (s_materialCombinations[7] != null
            && ReferenceEquals(opaque, s_cachedOpaque)
            && ReferenceEquals(transparent, s_cachedTransparent)
            && ReferenceEquals(liquid, s_cachedLiquid))
        {
            return;
        }

        s_cachedOpaque = opaque;
        s_cachedTransparent = transparent;
        s_cachedLiquid = liquid;

        for (int mask = 0; mask < s_materialCombinations.Length; mask++)
        {
            int count = ((mask & 1) != 0 ? 1 : 0) + ((mask & 2) != 0 ? 1 : 0) + ((mask & 4) != 0 ? 1 : 0);
            Material[] combo = new Material[count];
            int idx = 0;
            if ((mask & 1) != 0) combo[idx++] = opaque;
            if ((mask & 2) != 0) combo[idx++] = transparent;
            if ((mask & 4) != 0) combo[idx] = liquid;
            s_materialCombinations[mask] = combo;
        }

        s_materialCacheVersion++;
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

        // MR-3: a recycled section must reassign sharedMaterials on its first update (the renderer's
        // material state is no longer tracked once cleared). Reset to the "none assigned yet" default.
        _lastMaterialMask = -1;
        _lastMaterialCacheVersion = -1;
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
