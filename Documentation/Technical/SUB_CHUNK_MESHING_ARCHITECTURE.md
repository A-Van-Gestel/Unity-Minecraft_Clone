# Sub-Chunk (Section) Meshing Architecture

**Status:** Implemented (Active)
**Target Engine:** Unity 6.2+
**Context:** The engine successfully renders the world using 16x16x16 `ChunkSection` GameObjects instead of monolithic columns.

## 1. Executive Summary

To support increased world height (256+ blocks), eliminate main-thread lag during voxel modifications, and implement robust visibility culling, the rendering architecture will move from **Monolithic Column Meshes** to **Sub-Chunk (Section) Meshes**.

Instead of generating one massive mesh for a 16x16x128 column, we will generate independent meshes for each 16x16x16 `ChunkSection`. This aligns the rendering strategy with the underlying data structure and leverages Unity's native culling systems.

## 2. Problem Analysis

The previous "Monolithic" approach and the attempted "Vertical Passability" optimization faced insurmountable architectural flaws:

1. **Scaling Cost (O(N)):** Modifying a single block at Y=5 required regenerating the mesh for the entire column (Y=0 to Y=127). As height increases to 256 or 512, this cost becomes prohibitive, causing frame spikes.
2. **Ineffective Culling:** Unity's Frustum Culling operates on `Renderers`. A tall chunk column is almost always "in view" (e.g., player looking at the bottom, top is off-screen). Unity is forced to submit geometry for the entire column, including parts behind the player or deep
   underground.
3. **Complex Visibility Logic:** The "Vertical Passability" algorithm attempted to manually calculate occlusion. This was CPU-intensive, complex to maintain, and failed to account for 3D visibility (e.g., viewing a cave from the side).

## 3. Proposed Architecture

### 3.1. The "Sub-Chunk" Concept

* **Logical Unit:** `ChunkSection` (16x16x16 voxels). Already exists in data.
* **Visual Unit:** A distinct `GameObject` (or pooled Renderer) representing exactly one `ChunkSection`.

The `Chunk` class transforms from a Mesh provider into a **Manager**. It manages a list of Section Renderers.

### 3.2. Rendering Strategy

We will use **Individual GameObjects per Section** with `MeshFilter` and `MeshRenderer`.

**Why not `Graphics.DrawMesh`?**

* **Physics:** We need `MeshColliders`. Updating a small collider for a 16x16x16 area is significantly faster than baking a collider for a massive column.
* **Ease of Use:** Unity's built-in systems (Frustum Culling, Sorting, LODs) work best with standard GameObjects.
* **Performance:** In Unity 6, the overhead of GameObjects is low. With **GPU Instancing** enabled on the material, draw calls are batched efficiently.

### 3.3. Culling Strategy (The "Natural" Cull)

We do not need complex flood-fill visibility algorithms on the CPU. We rely on two layers of culling:

1. **Generation Culling (Zero-Vertex Check):**
    * If a section is `IsFullySolid` (completely buried) and surrounded by solid neighbors, the Meshing Job produces **0 vertices**.
    * If a section is `IsEmpty` (air), the Meshing Job produces **0 vertices**.
    * **Action:** If vertex count is 0, we **disable/recycle** the GameObject. No render cost.

2. **Frustum Culling (Unity Native):**
    * Because every 16x16x16 section has its own Bounding Box, Unity automatically stops rendering sections that are:
        * Behind the camera.
        * Above/Below the camera (e.g., surface sections are culled when the player is deep underground).

## 4. Technical Implementation Details

### 4.1. Data Structure Changes

**`Chunk.cs`**
Needs to track visual objects per section.

```csharp
public class Chunk {
    // Replaces the single _meshFilter/_meshRenderer
    private SectionRenderer[] _sectionRenderers; 
    
    // Bitmask or bool array to track which sections need visual updates
    private bool[] _sectionDirtyFlags; 
}
```

**`SectionRenderer` (New Component/Class)**
Wrapper around the Unity objects.

```csharp
public class SectionRenderer {
    public GameObject gameObject;
    public MeshFilter meshFilter;
    public MeshCollider meshCollider;
    
    public void UpdateMesh(MeshData data);
    public void SetVisible(bool visible);
}
```

### 4.2. Mesh Generation Pipeline (Jobs)

To support future Cubic Chunks, the Meshing Job must become granular.

**Current:** `MeshGenerationJob` processes 0..Height.
**New:** `MeshGenerationJob` processes a **Single Section** (or a batch of sections independently).

**Job Input:**

* Target Section Voxel Map (16x16x16)
* Neighbor Data (Immediate 1-block border from all 6 directions).
    * *Note:* We no longer need full neighbor chunk maps, just the boundary slices. However, passing full neighbor maps is easier for memory management in the short term.

**Job Output:**

* `MeshDataJobOutput` struct containing vertices/tris.

### 4.3. Modern Mesh API Optimization (Unity 2021+)

To avoid Main Thread spikes when applying the mesh, we will use the Advanced Mesh API. This bypasses the conversion from `NativeList` -> `C# Array` -> `C++ Internal`.

```csharp
// OLD WAY (Slow, allocates memory)
mesh.vertices = jobOutput.Vertices.ToArray();
mesh.triangles = jobOutput.Triangles.ToArray();

// NEW WAY (Fast, Zero Allocation)
mesh.SetVertexBufferParams(vertexCount, ...layout...);
mesh.SetVertexBufferData(jobOutput.Vertices.AsArray(), 0, 0, vertexCount);

mesh.SetIndexBufferParams(indexCount, IndexFormat.UInt32);
mesh.SetIndexBufferData(jobOutput.Triangles.AsArray(), 0, 0, indexCount);

mesh.subMeshCount = 1;
mesh.SetSubMesh(0, new SubMeshDescriptor(0, indexCount));
```

### 4.4. Voxel Modification Workflow

1. **Input:** Player breaks block at `(x, y, z)`.
2. **Data Update:** `Chunk.ModifyVoxel` updates `uint[]` and lighting.
3. **Re-Mesh:**
    * The `World` detects the chunk is dirty (`chunksToBuildMesh`).
    * Schedules `MeshGenerationJob`.
    * **Optimization:** The job iterates all sections but skips `IsEmpty` sections immediately. It produces a single native buffer (`MeshDataJobOutput`) containing offsets for every section.
    * **Apply:** `Chunk.ApplyMeshData` slices this buffer and updates only the necessary `SectionRenderers`.

## 5. Performance Considerations & Limitations

### 5.1. Draw Calls

* **Risk:** Increasing GameObject count from ~400 (Chunks) to ~6,400 (Sections).
* **Mitigation:**
    1. **Empty Sections:** ~50% of sections (High air, Deep underground) have 0 vertices and will exist only as data, not GameObjects.
    2. **GPU Instancing:** All sections share the same Material. Unity will batch them efficiently.
    3. **Static Batching:** Not applicable for dynamic chunks, but Instancing is sufficient.

### 5.2. Memory

* **Risk:** Mesh overhead.
* **Mitigation:** `Mesh` objects in Unity have a small header overhead. However, dividing one large vertex buffer into 16 smaller ones does not significantly increase total VRAM usage (vertex count remains roughly the same).

## 6. Path to Cubic Chunks (Future)

This architecture is the prerequisite for Infinite Height / Cubic Chunks.
Once implemented, the `Chunk` (Column) class effectively becomes a legacy wrapper.

**Migration to Cubic:**

1. Remove `Chunk` array `[x, z]`.
2. Store `ChunkSection` in a spatial hash `Dictionary<Vector3Int, ChunkSection>`.
3. Load/Unload sections based on distance from player sphere, not cylinder.
4. Job system remains identical (it is already section-based).

## 7. Implementation Status (Completed)

- [x] **Data:** `ChunkSection` tracks `IsFullySolid` / `IsEmpty`.
- [x] **Jobs:** `MeshGenerationJob` generates `MeshSectionStats` for granular rendering.
- [x] **Manager:** `Chunk.cs` manages an array of `SectionRenderer` objects.
- [x] **API:** `SectionRenderer` uses `Mesh.SetVertexBufferData` and `Mesh.SetSubMeshes` for zero-allocation updates.
- [x] **Orchestrator:** `WorldJobManager` handles dependencies before scheduling meshing.

---

## 8. Advanced Visibility Culling (Next Steps)

The Sub-Chunk architecture lays the foundation for **Graph-Based Visibility Culling** (stopping the rendering of caves when the player is on the surface).

For the detailed design and implementation plan of this feature, please refer to:
**[Documentation/Technical/VISIBILITY_CULLING_ARCHITECTURE.md](VISIBILITY_CULLING_ARCHITECTURE.md)**