# Advanced Visibility Culling (Graph Connectivity)

- **Status:** Planned (Prerequisites Complete)
- **Current Implementation:** Standard Frustum Culling + Empty Section Skipping
- **Target:** Advanced Occlusion Culling via Graph Traversal
- **Context:** Solving the "Underground Overdraw" problem where caves render while the player is on the surface.

## 1. The Problem: Frustum vs. Occlusion

Unity's built-in **Frustum Culling** only checks if an object's bounding box is inside the camera's view cone. It does **not** check if the object is hidden behind other opaque objects.

In a voxel game, massive amounts of geometry (caves) exist directly below the surface. Since the camera often looks slightly downward, these caves are "in the frustum." Without Occlusion Culling, the engine renders the entire underground world, causing significant GPU overhead.

## 2. The Solution: Graph-Based Traversal

We cannot use Unity's baked Occlusion Culling because our world is procedural and destructible. We must implement a dynamic **Graph Traversal** algorithm.

### Core Concept

Instead of asking "Is this chunk in the camera view?", we ask: **"Can the air flow from the camera's position to this chunk?"**

If a cave system is completely sealed off by solid blocks (or just not connected to the current hollow area the player is standing in), the traversal algorithm will never reach it. If the algorithm doesn't reach it, we disable the renderer.

## 3. Data Structure: The Connectivity Mask

To make this efficient, we cannot Flood Fill the entire world every frame. Instead, we pre-calculate a **Connectivity Graph** for every `ChunkSection` during the `MeshGenerationJob`.

We reduce the complex 4096-voxel section into a simple question:
*"If I enter this section from the Top face, can I exit via the East face?"*

### 3.1. The Data Type (`ConnectivityMask`)

We track connections between all 6 faces (Up, Down, North, South, East, West).
There are 6 Entry points and 6 Exit points.
Data requirement: 6 bitmasks (one for each entry face), each containing 6 bits (one for each exit).
Total: 36 bits. Fits in a single `ulong`.

```csharp
public struct SectionConnectivity
{
    // A bitmask representing reachability.
    // If we enter from Face A, which other Faces can we reach?
    // Usage: (VisibilityMask >> (EntryFaceIndex * 6)) & (1 << ExitFaceIndex)
    public ulong VisibilityMask; 
    
    // Quick flag: Is this section completely empty (Air)? 
    // If so, all faces connect to all faces (Mask = ~0).
    public bool IsEmpty;
}
```

### 3.2. Generation Logic (Inside `MeshGenerationJob`)

When generating a mesh, we run a localized **Union-Find** or **Flood Fill** on the 16x16x16 voxel grid.

1. Identify all Air voxels touching the **Top** boundary. Group them into a "Set".
2. Identify all Air voxels touching **Bottom**, **North**, etc.
3. Flood fill through the air voxels.
4. If the "Top Set" merges with the "Bottom Set", we set the `Top->Bottom` bit in the mask to 1.

## 4. Runtime Logic: The Render Loop (BFS)

Every frame (or every time the camera moves a significant distance), we run a Breadth-First Search.

**Manager:** `VisibilityManager.cs`

1. **Disable All:** Mark all `SectionRenderers` as invisible (logically, don't actually toggle GameObjects yet to avoid flicker).
2. **Start Node:** Find the `ChunkSection` containing the Camera. Mark it Visible.
3. **Queue:** Push the Camera Section to a `Queue`.
4. **Loop:**
    * Pop Section `S`.
    * For each neighbor `N` (Up, Down, N, E, S, W):
        * **Check 1 (Frustum):** Is `N` inside the Camera Frustum? If No, Skip.
        * **Check 2 (Backtracking):** Have we already visited `N`? If Yes, Skip.
        * **Check 3 (Connectivity):**
            * We are trying to exit `S` via `Face F`.
            * Does `S.Connectivity.Mask` say that `EntryFace` connects to `Face F`?
            * *Note: For the Camera Section, we assume we can reach all faces.*
    * If all checks pass:
        * Mark `N` Visible.
        * Record which face we entered `N` from (for the next iteration's connectivity check).
        * Enqueue `N`.

5. **Apply:** Iterate all chunks.
    * If `Section.Visible == true` AND `GameObject.activeSelf == false` -> Enable.
    * If `Section.Visible == false` AND `GameObject.activeSelf == true` -> Disable.

## 5. Implementation Roadmap

### Phase 0: Prerequisites (Completed)

- [x] **Section Rendering:** Switched from Monolithic Chunks to `SectionRenderer` (`Chunk.cs`).
- [x] **Data Tracking:** Implemented `IsEmpty` and `IsFullySolid` tracking in `ChunkSection` and `SectionJobData`.
- [x] **Generation Optimization:** Implemented Empty Section skipping in `MeshGenerationJob`.

### Phase 1: Data Generation (The Hard Part)

- [ ] **Data Structure:** Create `SectionConnectivity` struct in `JobData.cs`.
- [ ] **Storage:** Add a `NativeArray<SectionConnectivity>` to `ChunkData`.
- [ ] **Job Implementation:** Create a Burst-compiled `ConnectivityCalculatorJob` (or integrate into `MeshGenerationJob`).
    * **Algorithm:** Run a 16x16x16 Flood Fill to determine face-to-face reachability.
    * **Output:** Compress results into a `ulong` mask (6 entry faces * 6 exit faces).

### Phase 2: The Visibility Manager

- [ ] **Manager Class:** Create `VisibilityManager` (MonoBehaviour).
- [ ] **BFS Job:** Implement the BFS traversal loop using `NativeQueue` and `Burst`.
    * **Input:** `NativeHashMap<ChunkCoord, ConnectivityData>` (Global lookup of all loaded chunks).
    * **Input:** Camera Frustum Planes.
    * **Output:** A `NativeList<ChunkSectionID>` (or coordinate + index) of sections to enable.

### Phase 3: Integration

- [ ] **World Hook:** Update `World.cs` to feed data to the `VisibilityManager` every frame.
- [ ] **Renderer Update:** Update `SectionRenderer` to accept visibility toggles (enabling/disabling the GameObject based on the BFS result).

## 6. Edge Cases & Optimizations

* **The "Transparent Block" Issue:** Glass allows visibility but might block movement. The connectivity check must treat Transparent blocks as "Air" for visibility purposes.
* **Latency:** If the BFS is too slow to run every frame, run it every 5 frames or in a background job. The visual artifact would be "chunks popping in" when turning corners rapidly.
* **Spatial Hashing:** To feed the Job System, we need a fast way to look up neighbor connectivity data. A flat `NativeHashMap` of `(ChunkCoord -> Data)` is best.

---

## Recommendation

Do **not** try to hack this into the existing `MeshGenerationJob` as a quick fix. This requires a dedicated system. The current "Empty Section" optimization is a great first step. The architecture above is the correct next step to solve the specific "Surface walking over caves"
performance issue.