# Debugging Tools & Diagnostic Methods

This document archives powerful debug methods developed during the implementation of the Voxel Engine.
**Do not keep these in production code (`World.cs`)**. Copy-paste them back in only when diagnosing specific issues.

## 1. Invisible Chunk Diagnosis (`DebugRaycastChunkState`)

**Use Case:** You are standing in the world, and a chunk is invisible (mesh not rendering), but you suspect the data exists.
**Usage:** Add to `World.cs`. Bind a key (e.g., F8) to call `World.Instance.DebugRaycastChunkState()`. Point your crosshair at the empty space.

```csharp
/// <summary>
/// Raycasts for a chunk and logs a comprehensive report on its internal state.
/// Helps diagnose "Invisible Chunk" bugs by comparing Data, Visual, and Queue states.
/// </summary>
public void DebugRaycastChunkState()
{
    Transform cam = Camera.main.transform;
    Ray ray = new Ray(cam.position, cam.forward);
    
    // Raycast against a virtual plane or long distance since the chunk might have no collider
    Vector3 targetPoint = cam.position + cam.forward * 10f;

    ChunkCoord coord = GetChunkCoordFromVector3(targetPoint);
    Vector2Int pos = new Vector2Int(coord.X * VoxelData.ChunkWidth, coord.Z * VoxelData.ChunkWidth);

    StringBuilder sb = new StringBuilder();
    sb.AppendLine($"--- CHUNK REPORT {coord} ---");

    // 1. Check Dictionary & Data
    bool hasData = worldData.Chunks.TryGetValue(pos, out ChunkData data);
    sb.AppendLine($"[Data Layer]");
    sb.AppendLine($"  - In WorldData: {hasData}");
    if (hasData)
    {
        sb.AppendLine($"  - IsPopulated: {data.IsPopulated}");
        sb.AppendLine($"  - NeedsInitialLighting: {data.NeedsInitialLighting}");
        sb.AppendLine($"  - HasLightChanges: {data.HasLightChangesToProcess}");
        
        // Check content
        int totalNonAir = 0;
        int totalSections = 0;
        foreach (var section in data.sections)
        {
            if (section != null)
            {
                totalSections++;
                totalNonAir += section.nonAirCount;
            }
        }
        sb.AppendLine($"  - Sections: {totalSections} allocated");
        sb.AppendLine($"  - Total Non-Air Voxels: {totalNonAir}");
    }

    // 2. Check Object & Mesh
    bool hasObj = _chunkMap.TryGetValue(coord, out Chunk chunk);
    sb.AppendLine($"[Visual Layer]");
    sb.AppendLine($"  - In ChunkMap: {hasObj}");

    if (hasObj)
    {
        sb.AppendLine($"  - isActive: {chunk.isActive}");
        sb.AppendLine($"  - GameObject Active: {(chunk.ChunkGameObject ? chunk.ChunkGameObject.activeSelf.ToString() : "NULL")}");
        sb.AppendLine($"  - Linked Data Match: {(chunk.ChunkData == data ? "YES" : "NO (Desync!)")}");

        // Inspect Renderers (Reflection needed or manual check if fields private)
        // Assuming we can access the GameObject children
        int childCount = chunk.ChunkGameObject.transform.childCount;
        sb.AppendLine($"  - Section Renderers (Children): {childCount}");

        for (int i = 0; i < childCount; i++)
        {
            Transform t = chunk.ChunkGameObject.transform.GetChild(i);
            MeshFilter mf = t.GetComponent<MeshFilter>();
            if (mf && mf.sharedMesh)
            {
                if (mf.sharedMesh.vertexCount == 0 && t.gameObject.activeSelf)
                    sb.AppendLine($"    - Section {i}: 0 Vertices (BUT ACTIVE - Warn)");
                else if (mf.sharedMesh.vertexCount > 0 && !t.gameObject.activeSelf)
                    sb.AppendLine($"    - Section {i}: {mf.sharedMesh.vertexCount} Verts (BUT INACTIVE - Error)");
                else if (mf.sharedMesh.vertexCount > 0)
                    sb.AppendLine($"    - Section {i}: {mf.sharedMesh.vertexCount} Verts (OK)");
            }
            else
            {
                if (t.gameObject.activeSelf) sb.AppendLine($"    - Section {i}: Missing Mesh (Active)");
            }
        }
    }

    // 3. Check Queue State
    bool inMeshQueue = _chunksToBuildMeshSet.Contains(coord);
    bool inLightDict = JobManager.lightingJobs.ContainsKey(coord);
    bool inMeshDict = JobManager.meshJobs.ContainsKey(coord);

    sb.AppendLine($"[System State]");
    sb.AppendLine($"  - In Mesh Queue: {inMeshQueue}");
    sb.AppendLine($"  - In Lighting Job: {inLightDict}");
    sb.AppendLine($"  - In Meshing Job: {inMeshDict}");
    
    Debug.Log(sb.ToString());
}
```

## 2. Stuck Generation Diagnosis (`DebugAnalyzeStuckChunks`)

**Use Case:** The `_chunksToBuildMesh` queue is not emptying. Chunks stay at the edge of view and never load.
**Usage:** Add to `World.cs`. Call when queue count > 0 for extended periods.

```csharp
public void DebugAnalyzeStuckChunks()
{
    Debug.Log($"--- Analyzing {_chunksToBuildMesh.Count} Stuck Chunks ---");
    foreach (var chunk in _chunksToBuildMesh)
    {
        if (chunk == null) continue;
        
        ChunkCoord coord = chunk.Coord;
        Debug.Log($"Checking Stuck Chunk {coord} (Active: {chunk.isActive})...");

        // Check neighbors
        foreach (int faceIndex in VoxelData.HorizontalFaceChecksIndices)
        {
            Vector3Int offset = VoxelData.FaceChecks[faceIndex];
            ChunkCoord neighborCoord = new ChunkCoord(coord.X + offset.x, coord.Z + offset.z);
            Vector2Int neighborPos = new Vector2Int(neighborCoord.X * VoxelData.ChunkWidth, neighborCoord.Z * VoxelData.ChunkWidth);

            if (worldData.Chunks.TryGetValue(neighborPos, out ChunkData nData))
            {
                bool isLit = !nData.HasLightChangesToProcess && !nData.NeedsInitialLighting;
                bool jobRunning = JobManager.lightingJobs.ContainsKey(neighborCoord);
                bool hasObject = nData.Chunk != null;
                bool isActive = nData.Chunk != null && nData.Chunk.isActive;
                
                // Logic check: Is the neighbor holding us back?
                if (!isLit || jobRunning)
                {
                    Debug.LogWarning($"   -> Waiting for Neighbor {neighborCoord} | Lit: {isLit} | Job: {jobRunning} | HasObj: {hasObject} | Active: {isActive} | PendingFlags: Init={nData.NeedsInitialLighting}, Change={nData.HasLightChangesToProcess}");
                }
            }
            else
            {
                Debug.Log($"   -> Neighbor {neighborCoord} is NOT LOADED.");
            }
        }
    }
    Debug.Log("--- Analysis Complete ---");
}
```

### 3. Mesh Queue Sync Validator & Repair

**Use Case:** Suspected desync between `_chunksToBuildMesh` (List) and `_chunksToBuildMeshSet` (HashSet).
**Usage:** Call `DebugLogMeshQueueState` to check health. If errors are found, call `DebugCleanMeshQueue` to force a re-sync during runtime.

```csharp
/// <summary>
/// DEBUG: Logs detailed information about the current state of _chunksToBuildMesh
/// Call this from your DebugScreen or via a keyboard shortcut
/// </summary>
public void DebugLogMeshQueueState()
{
    Debug.Log($"[Mesh Queue Diagnostic] List Count: {_chunksToBuildMesh.Count} | HashSet Count: {_chunksToBuildMeshSet.Count}");
    
    if (_chunksToBuildMesh.Count != _chunksToBuildMeshSet.Count)
    {
        Debug.LogError("[Mesh Queue Diagnostic] DESYNC DETECTED!");
    }

    int inactiveCount = 0;
    foreach (var c in _chunksToBuildMesh)
    {
        if (c == null || !c.isActive) inactiveCount++;
    }

    if (inactiveCount > 0)
    {
        Debug.LogWarning($"[Mesh Queue Diagnostic] Found {inactiveCount} inactive/null chunks in queue.");
    }
}

/// <summary>
/// Emergency cleanup method. Rebuilds the HashSet from the List and purges nulls.
/// </summary>
public void DebugCleanMeshQueue()
{
    // Remove dead references from the List
    int removed = _chunksToBuildMesh.RemoveAll(c => c == null || !c.isActive);
    
    // Rebuild the HashSet to match the List exactly
    _chunksToBuildMeshSet.Clear();
    foreach (var c in _chunksToBuildMesh)
    {
        _chunksToBuildMeshSet.Add(c.Coord);
    }

    Debug.Log($"[Mesh Queue Diagnostic] Queue cleaned (Removed {removed}) and re-synced.");
}
```

## 4. Sunlight Cross-Section Diagnosis (`DebugLogSunlightCrossSection`)

**Use Case:** Debugging "shadow wall" artifacts or incorrect light propagation across chunk boundaries. It provides a visual 11x11 cross-section of sunlight values and heightmap data around the targeted block, centered on the nearest chunk boundary.  
**Usage:** Add to `World.cs`. Call `_world.DebugLogSunlightCrossSection()`, typically tied to a debug key like `F8` in `Player.cs`. Aim your crosshair closely at a block experiencing lighting artifacts.

**Example Output:**

```text
<color=cyan>--- SUNLIGHT CROSS-SECTION ---</color>
Hit: (560, 53, 1438) | Local: (0, 14)
Scanning along: X axis | Border at X=560
Chunk border between [559] and [560]

  Y  |  555  556  557  558  559| 560  561  562  563  564  565
---------------------------------------------------------------
  58 | 15.  15.  15.  15.  15. |15.  15.  15.  15.  15.  15. 
  57 | 15.  15.  15.  15.  15. |15.  15.  15.  15.  15.  15. 
  56 | 15.  15.  15.  15.  15. |15.  15.  15.  15.  15.  15. 
  55 | 15.  15.  15.  15.  15. |15.  15.  15.  15.  15.  15. 
  54 | 15.  15.  15.  15.  15. |15.  15.  15.  15.  15.  15. 
> 53<| 15~  15~  15~  15~  15~ |15~  15~  15~  15~  15~  15~ 
  52 | 13~  13~  13~  13~  13~ |13~  13~  13~  13~  13~  13~ 
  51 | 11~  11~  11~  11~  11~ |11~  11~  11~  11~  11~  11~ 
  50 |  9~   9~   9~   9~   9~ | 9~   9~   9~   9~   9~   9~ 
  49 |  8#   8#   8#   8#   8# | 8#   8#   8#   8#   8#   8# 
  48 |  0#   0#   0#   0#   0# | 0#   0#   0#   0#   0#   0# 

Legend: . = Air, ~ = Transparent (water), # = Opaque solid
Border line is between columns 559 and 560

HMap |   53   53   53   53   53|  53   53   53   53   53   53
Water block opacity: 2
```

**Code:**

```csharp
/// <summary>
/// A diagnostic tool for analyzing the sunlight values spanning across chunk boundaries.
/// Raycasts to the nearest targeted block, identifies the cross section perpendicular to
/// the nearest chunk border, and prints a cross-section of sunlight values spanning
/// 5 voxels on each side of the border across 11 Y levels (5 above, the hit level, 5 below).
/// </summary>
public void DebugLogSunlightCrossSection()
{
    VoxelRaycastResult result = player.PlayerInteraction.RaycastForVoxel(overrideInteractWithFluids: true);
    if (!result.DidHit)
    {
        Debug.Log("[SunlightDiag] No voxel hit. Aim at a block and try again.");
        return;
    }

    Vector3Int hitPos = result.HitPosition;

    // Determine which chunk border is nearest (X or Z axis)
    int localX = ((hitPos.x % VoxelData.ChunkWidth) + VoxelData.ChunkWidth) % VoxelData.ChunkWidth;
    int localZ = ((hitPos.z % VoxelData.ChunkWidth) + VoxelData.ChunkWidth) % VoxelData.ChunkWidth;

    // Distance to nearest X border (0 or 15)
    int distToXBorder = Mathf.Min(localX, VoxelData.ChunkWidth - 1 - localX);
    // Distance to nearest Z border (0 or 15)
    int distToZBorder = Mathf.Min(localZ, VoxelData.ChunkWidth - 1 - localZ);

    bool scanAlongX; // true = cross-section walks along X axis, false = along Z axis
    int borderWorldCoord; // The world-space coordinate of the border

    if (distToXBorder <= distToZBorder)
    {
        scanAlongX = true;
        // Find the actual chunk border X coordinate
        int chunkOriginX = Mathf.FloorToInt((float)hitPos.x / VoxelData.ChunkWidth) * VoxelData.ChunkWidth;
        borderWorldCoord = localX < VoxelData.ChunkWidth / 2
            ? chunkOriginX          // Border is at the left edge
            : chunkOriginX + VoxelData.ChunkWidth; // Border is at the right edge
    }
    else
    {
        scanAlongX = false;
        int chunkOriginZ = Mathf.FloorToInt((float)hitPos.z / VoxelData.ChunkWidth) * VoxelData.ChunkWidth;
        borderWorldCoord = localZ < VoxelData.ChunkWidth / 2
            ? chunkOriginZ
            : chunkOriginZ + VoxelData.ChunkWidth;
    }

    const int HALF_RANGE = 5;
    const int Y_RANGE = 5;

    System.Text.StringBuilder sb = new System.Text.StringBuilder();
    sb.AppendLine($"<color=cyan>--- SUNLIGHT CROSS-SECTION ---</color>");
    sb.AppendLine($"Hit: ({hitPos.x}, {hitPos.y}, {hitPos.z}) | Local: ({localX}, {localZ})");
    sb.AppendLine($"Scanning along: {(scanAlongX ? "X" : "Z")} axis | Border at {(scanAlongX ? "X" : "Z")}={borderWorldCoord}");
    sb.AppendLine($"Chunk border between [{borderWorldCoord - 1}] and [{borderWorldCoord}]");
    sb.AppendLine();

    // Header row: coordinate labels
    sb.Append("  Y  |");
    for (int offset = -HALF_RANGE; offset <= HALF_RANGE; offset++)
    {
        int coord = borderWorldCoord + offset;
        string marker = offset == 0 ? "|" : " ";
        sb.Append($"{marker}{coord,4}");
    }
    sb.AppendLine();
    sb.AppendLine(new string('-', 8 + (HALF_RANGE * 2 + 1) * 5));

    // Data rows: Y levels from above to below
    for (int yOff = Y_RANGE; yOff >= -Y_RANGE; yOff--)
    {
        int y = hitPos.y + yOff;
        if (y < 0 || y >= VoxelData.ChunkHeight) continue;

        string yLabel = yOff == 0 ? $">{y,3}<" : $" {y,3} ";
        sb.Append($"{yLabel}|");

        for (int offset = -HALF_RANGE; offset <= HALF_RANGE; offset++)
        {
            int worldCoord = borderWorldCoord + offset;
            int wx = scanAlongX ? worldCoord : hitPos.x;
            int wz = scanAlongX ? hitPos.z : worldCoord;

            Vector3 worldPos = new Vector3(wx, y, wz);
            string marker = offset == 0 ? "|" : " ";

            if (!worldData.IsVoxelInWorld(worldPos))
            {
                sb.Append($"{marker}  - ");
                continue;
            }

            VoxelState? state = worldData.GetVoxelState(worldPos);
            if (!state.HasValue)
            {
                sb.Append($"{marker}  ? ");
                continue;
            }

            byte sl = state.Value.Sunlight;
            ushort id = state.Value.id;
            string blockChar;

            if (id == BlockIDs.Air)
                blockChar = ".";
            else if (blockTypes[id].IsOpaque)
                blockChar = "#";
            else
                blockChar = "~"; // Water or other transparent

            sb.Append($"{marker}{sl,2}{blockChar} ");
        }

        sb.AppendLine();
    }

    sb.AppendLine();
    sb.AppendLine("Legend: . = Air, ~ = Transparent (water), # = Opaque solid");
    sb.AppendLine($"Border line is between columns {borderWorldCoord - 1} and {borderWorldCoord}");

    // --- HEIGHTMAP & OPACITY DATA ---
    sb.AppendLine();
    sb.Append("HMap |");
    for (int offset = -HALF_RANGE; offset <= HALF_RANGE; offset++)
    {
        int worldCoord = borderWorldCoord + offset;
        int wx = scanAlongX ? worldCoord : hitPos.x;
        int wz = scanAlongX ? hitPos.z : worldCoord;

        // Find which chunk this column belongs to and get its heightmap
        int chunkX = Mathf.FloorToInt((float)wx / VoxelData.ChunkWidth) * VoxelData.ChunkWidth;
        int chunkZ = Mathf.FloorToInt((float)wz / VoxelData.ChunkWidth) * VoxelData.ChunkWidth;
        Vector2Int chunkPos = new Vector2Int(chunkX, chunkZ);
        string marker = offset == 0 ? "|" : " ";

        if (worldData.Chunks.TryGetValue(chunkPos, out ChunkData cd))
        {
            int lx = ((wx - chunkX) % VoxelData.ChunkWidth + VoxelData.ChunkWidth) % VoxelData.ChunkWidth;
            int lz = ((wz - chunkZ) % VoxelData.ChunkWidth + VoxelData.ChunkWidth) % VoxelData.ChunkWidth;
            int hmIdx = lx + VoxelData.ChunkWidth * lz;
            ushort hm = cd.heightMap[hmIdx];
            sb.Append($"{marker}{hm,4}");
        }
        else
        {
            sb.Append($"{marker}   ?");
        }
    }
    sb.AppendLine();

    // Show water opacity for reference
    ushort waterId = BlockIDs.Water;
    if (waterId < blockTypes.Length)
    {
        sb.AppendLine($"Water block opacity: {blockTypes[waterId].opacity}");
    }

    Debug.Log(sb.ToString());
}
```

## 5. Fluid Surface Math Diagnosis (`DebugLogFluidSurfaceMath` & Helpers)

**Use Case:** Debugging incorrect fluid surface smoothing, side face culling, geometry gaps, or dynamic flow vector generation in `VoxelMeshHelper.cs`.  
**Usage:** Add to `World.cs`. Call `_world.DebugLogFluidSurfaceMath()`, typically tied to a debug key like `F8` in `Player.cs`. Aim your crosshair closely at a fluid block experiencing meshing or rendering artifacts.

*(Note: Requires the helper method `GetDebugSmoothHeight` to be present alongside it).*

```csharp
/// <summary>
/// A diagnostic tool for analyzing fluid meshing derivatives.
/// Raycasts to the target fluid voxel and prints its complete smoothing context
/// and per-face side culling simulation to the console.
/// </summary>
public void DebugLogFluidSurfaceMath()
{
    VoxelRaycastResult result = player.PlayerInteraction.RaycastForVoxel(overrideInteractWithFluids: true);
    if (!result.DidHit) return;

    VoxelState? centerState = worldData.GetVoxelState(result.HitPosition);
    if (!centerState.HasValue || centerState.Value.Properties.fluidType == FluidType.None) return;

    ushort id = centerState.Value.id;
    byte level = centerState.Value.FluidLevel;

    // --- Horizontal neighbours ---
    VoxelState? n = worldData.GetVoxelState(result.HitPosition + new Vector3Int(0, 0, 1));
    VoxelState? s = worldData.GetVoxelState(result.HitPosition + new Vector3Int(0, 0, -1));
    VoxelState? e = worldData.GetVoxelState(result.HitPosition + new Vector3Int(1, 0, 0));
    VoxelState? w = worldData.GetVoxelState(result.HitPosition + new Vector3Int(-1, 0, 0));
    VoxelState? ne = worldData.GetVoxelState(result.HitPosition + new Vector3Int(1, 0, 1));
    VoxelState? nw = worldData.GetVoxelState(result.HitPosition + new Vector3Int(-1, 0, 1));
    VoxelState? se = worldData.GetVoxelState(result.HitPosition + new Vector3Int(1, 0, -1));
    VoxelState? sw = worldData.GetVoxelState(result.HitPosition + new Vector3Int(-1, 0, -1));

    // --- Vertical neighbours ---
    VoxelState? above = worldData.GetVoxelState(result.HitPosition + new Vector3Int(0, 1, 0));
    VoxelState? above_N = worldData.GetVoxelState(result.HitPosition + new Vector3Int(0, 1, 1));
    VoxelState? above_S = worldData.GetVoxelState(result.HitPosition + new Vector3Int(0, 1, -1));
    VoxelState? above_E = worldData.GetVoxelState(result.HitPosition + new Vector3Int(1, 1, 0));
    VoxelState? above_W = worldData.GetVoxelState(result.HitPosition + new Vector3Int(-1, 1, 0));

    bool hasFluidAbove = above.HasValue && above.Value.id == id;

    // --- Pre-compute corner smooth heights (reused for both surface and side-face reports) ---
    float[] templates = FluidVertexTemplates.WaterVertexTemplates.ToArray();
    float templateHeight = templates[level];

    float neCorner = GetDebugSmoothHeight(level, n, e, ne, id);
    float nwCorner = GetDebugSmoothHeight(level, n, w, nw, id);
    float seCorner = GetDebugSmoothHeight(level, s, e, se, id);
    float swCorner = GetDebugSmoothHeight(level, s, w, sw, id);

    // --- Surface Report (original) ---
    Debug.Log($"<color=cyan>--- FLUID SURFACE MATH REPORT ---</color>\n" +
                $"<b>Global Pos:</b> {result.HitPosition} | <b>Level:</b> {level} | " +
                $"<b>FluidAbove:</b> {hasFluidAbove} | <b>TemplateHeight:</b> {templateHeight:F4}\n\n" +
                $"[Corner Smooth Values]\n" +
                $"  <b>Top-Right (NE):</b> {neCorner}\n" +
                $"  <b>Top-Left  (NW):</b> {nwCorner}\n" +
                $"  <b>Bottom-Right (SE):</b> {seCorner}\n" +
                $"  <b>Bottom-Left  (SW):</b> {swCorner}");

    // --- Side Face Diagnostic ---
    // Each entry: (label, sideNeighbor, aboveNeighbor, cornerA_value, cornerA_name, cornerB_value, cornerB_name)
    var faces = new (string label, VoxelState? neighbor, VoxelState? neighborAbove, float ca, string caName, float cb, string cbName)[]
    {
        ("North (+Z)", n, above_N, neCorner, "NE", nwCorner, "NW"),
        ("South (-Z)", s, above_S, seCorner, "SE", swCorner, "SW"),
        ("East  (+X)", e, above_E, neCorner, "NE", seCorner, "SE"),
        ("West  (-X)", w, above_W, nwCorner, "NW", swCorner, "SW"),
    };

    StringBuilder sb = new StringBuilder();
    sb.AppendLine("<color=yellow>--- FLUID SIDE FACE DIAGNOSTIC ---</color>");

    foreach (var face in faces)
    {
        bool neighborIsSameFluid = face.neighbor.HasValue && face.neighbor.Value.id == id;
        bool neighborHasFluidAbove = face.neighborAbove.HasValue && face.neighborAbove.Value.id == id;

        byte neighborLevel = neighborIsSameFluid ? face.neighbor.Value.FluidLevel : (byte)0;
        float neighborTemplate = neighborIsSameFluid ? templates[neighborLevel] : 0f;

        // Simulate the culling logic from VoxelMeshHelper (replicates colleague's patch)
        string cullResult;
        if (neighborIsSameFluid)
        {
            bool isFullHeight = hasFluidAbove || templateHeight >= 1.0f;
            if (!isFullHeight)
                cullResult = "<color=red>CULLED</color> — same fluid, isFullHeight=false (smooth cull)";
            else if (neighborTemplate >= 1.0f)
                cullResult = "<color=red>CULLED</color> — same fluid, neighbor templateHeight >= 1.0";
            else if (neighborHasFluidAbove)
                cullResult = "<color=red>CULLED</color> — same fluid, neighbor has fluid above";
            else
                cullResult = "<color=green>DRAWN</color> — same fluid, gap fill required";
        }
        else if (!face.neighbor.HasValue)
        {
            cullResult = "<color=green>DRAWN</color> — no neighbor (air/void)";
        }
        else if (face.neighbor.Value.Properties.fluidType == FluidType.None
                    && !face.neighbor.Value.Properties.IsTransparentForMesh)
        {
            cullResult = "<color=red>CULLED</color> — opaque solid neighbor";
        }
        else
        {
            cullResult = "<color=green>DRAWN</color> — transparent or non-fluid neighbor";
        }

        // The smoothed top-Y for this face's top edge (average of its two relevant corners)
        float smoothedEdgeTopY = (face.ca + face.cb) * 0.5f;
        bool hasGeometryGap = smoothedEdgeTopY < templateHeight - 0.001f;

        sb.AppendLine($"  <b>[{face.label}]</b>");
        sb.AppendLine($"    Neighbor: id={(face.neighbor.HasValue ? face.neighbor.Value.id.ToString() : "none")} | " +
                        $"SameFluid: {neighborIsSameFluid} | " +
                        $"NeighborLevel: {(neighborIsSameFluid ? neighborLevel.ToString() : "n/a")} | " +
                        $"NeighborTemplate: {neighborTemplate:F4} | " +
                        $"NeighborHasFluidAbove: {neighborHasFluidAbove}");
        sb.AppendLine($"    TopY → SmoothedEdge: {smoothedEdgeTopY:F4} " +
                        $"(corners {face.caName}:{face.ca:F4} + {face.cbName}:{face.cb:F4}) | " +
                        $"Template: {templateHeight:F4} | " +
                        $"GeometryGap: {(hasGeometryGap ? "<color=magenta>YES</color>" : "no")}");
        sb.AppendLine($"    Cull Decision: {cullResult}");
    }

    // ── Per-Corner Flow Vector Report ─────────────────────────────────────────
    // Mirrors CalculateSymmetricCornerFlow: IsSolidWall + accessibility guard + GetEffectiveFluidHeight.
    sb.AppendLine("\n<color=lime>--- FLOW VECTORS (Per-Corner, Symmetric) ---</color>");
    sb.AppendLine("  Uses IsSolidWall + accessibility guard (non-fluid blocks need ≥1 fluid grid-neighbor).");
    sb.AppendLine("  Flow = normalized gradient × speed curve.\n");

    ushort fluidId = id;
    static bool IsFluid(VoxelState? vs, ushort fId) => vs.HasValue && vs.Value.id == fId;

    // Debug equivalent of GetEffectiveFluidHeight.
    static float DebugEffHeight(VoxelState? vs, ushort fId, float[] tmpl)
    {
        if (!vs.HasValue) return 0f;
        bool isSolid = vs.Value.Properties.isSolid;
        bool isTransparent = vs.Value.Properties.IsTransparentForMesh;
        if (isSolid && !isTransparent) return 2.0f;
        if (vs.Value.Properties.fluidType == FluidType.None && !isSolid) return -1.0f;
        if (vs.Value.id == fId) return tmpl[vs.Value.FluidLevel];
        return 0f;
    }

    static (Vector2 flow, string detail) DebugCornerFlow(
        VoxelState? b00, VoxelState? b10, VoxelState? b01, VoxelState? b11,
        ushort fId, float[] tmpl)
    {
        // Wall check
        static bool W(VoxelState? v) => v.HasValue && v.Value.Properties.isSolid &&
                                        v.Value.Properties.fluidType == FluidType.None;

        bool w00 = W(b00);
        bool w10 = W(b10);
        bool w01 = W(b01);
        bool w11 = W(b11);

        // Fluid check
        bool f00 = IsFluid(b00, fId);
        bool f10 = IsFluid(b10, fId);
        bool f01 = IsFluid(b01, fId);
        bool f11 = IsFluid(b11, fId);

        // Accessibility guard
        if (!w00 && !f00 && !f10 && !f01) w00 = true;
        if (!w10 && !f10 && !f00 && !f11) w10 = true;
        if (!w01 && !f01 && !f00 && !f11) w01 = true;
        if (!w11 && !f11 && !f10 && !f01) w11 = true;

        float h00 = w00 ? 0f : DebugEffHeight(b00, fId, tmpl);
        float h10 = w10 ? 0f : DebugEffHeight(b10, fId, tmpl);
        float h01 = w01 ? 0f : DebugEffHeight(b01, fId, tmpl);
        float h11 = w11 ? 0f : DebugEffHeight(b11, fId, tmpl);

        float dx = 0f;
        int dxc = 0;
        if (!w01 && !w11)
        {
            dx += h11 - h01;
            dxc++;
        }

        if (!w00 && !w10)
        {
            dx += h10 - h00;
            dxc++;
        }

        if (dxc > 0) dx /= dxc;

        float dz = 0f;
        int dzc = 0;
        if (!w10 && !w11)
        {
            dz += h11 - h10;
            dzc++;
        }

        if (!w00 && !w01)
        {
            dz += h01 - h00;
            dzc++;
        }

        if (dzc > 0) dz /= dzc;

        string Blk(VoxelState? v, bool isW, bool isF, float h) =>
            isF ? $"fluid(h={h:F3})" :
            isW ? "wall(skip)" :
            (v.HasValue ? $"id={v.Value.id}(h={h:F3})" : "void(skip)");

        string detail = $"b00={Blk(b00, w00, f00, h00)} b10={Blk(b10, w10, f10, h10)} " +
                        $"b01={Blk(b01, w01, f01, h01)} b11={Blk(b11, w11, f11, h11)}" +
                        $"\n               dx={dx:F4}(pairs:{dxc}) dz={dz:F4}(pairs:{dzc})";

        Vector2 flow = new Vector2(dx, dz);
        float sqrMag = flow.sqrMagnitude;
        if (sqrMag < 0.0001f) return (Vector2.zero, detail + " → zero");

        float mag = Mathf.Sqrt(sqrMag);
        Vector2 dir = flow / mag;
        float speed = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, 0.25f, mag)) +
                        Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.8f, 1.2f, mag)) * 0.5f;

        return (dir * speed, detail + $" → dir={dir} speed={speed:F3}");
    }

    var (fBL, dBL) = DebugCornerFlow(sw, s, w, centerState, fluidId, templates);
    var (fTL, dTL) = DebugCornerFlow(w, centerState, nw, n, fluidId, templates);
    var (fBR, dBR) = DebugCornerFlow(s, se, centerState, e, fluidId, templates);
    var (fTR, dTR) = DebugCornerFlow(centerState, e, n, ne, fluidId, templates);

    sb.AppendLine($"  BL (x=0 z=0)  flow={fBL}\n               {dBL}");
    sb.AppendLine($"  TL (x=0 z=1)  flow={fTL}\n               {dTL}");
    sb.AppendLine($"  BR (x=1 z=0)  flow={fBR}\n               {dBR}");
    sb.AppendLine($"  TR (x=1 z=1)  flow={fTR}\n               {dTR}");

    // ── Shore Gradient / Push Report ─────────────────────────────────────────
    // The shore gradient is computed per-pixel in the shader from an 8-neighbor wall mask.
    // The push direction is still computed per-corner via the symmetric 4-block neighborhood.
    sb.AppendLine("\n<color=lime>--- SHORE DATA ---</color>");
    sb.AppendLine("  Wall mask: 8-neighbor flags (N/S/E/W + diagonals) packed into color.g.");
    sb.AppendLine("  Push: per-corner normalized displacement direction (symmetric 4-block neighborhood).\n");

    // ── Per-voxel wall mask (drives the per-pixel shore gradient in the shader) ──
    static bool IsWall(VoxelState? s) =>
        s.HasValue && s.Value.Properties.isSolid &&
        s.Value.Properties.fluidType == FluidType.None;

    bool wallN = IsWall(n);
    bool wallS = IsWall(s);
    bool wallE = IsWall(e);
    bool wallW = IsWall(w);
    bool diagNE = !wallN && !wallE && IsWall(ne);
    bool diagNW = !wallN && !wallW && IsWall(nw);
    bool diagSE = !wallS && !wallE && IsWall(se);
    bool diagSW = !wallS && !wallW && IsWall(sw);

    int mask = (wallN ? 1 : 0) | (wallS ? 2 : 0) | (wallE ? 4 : 0) | (wallW ? 8 : 0) |
                (diagNE ? 16 : 0) | (diagNW ? 32 : 0) | (diagSE ? 64 : 0) | (diagSW ? 128 : 0);

    sb.AppendLine($"  Wall Mask: 0x{mask:X2} (N={wallN} S={wallS} E={wallE} W={wallW}" +
                    $" NE={diagNE} NW={diagNW} SE={diagSE} SW={diagSW})");
    sb.AppendLine($"  Packed color.g = {mask / 255f:F4}\n");

    // ── Per-corner push directions (drive the shore push displacement effect) ──
    // Local helper: replicates CalculateSymmetricCornerShorePush for the main thread.
    static Vector2 DebugCornerShorePush(
        VoxelState? b00, VoxelState? b10, VoxelState? b01, VoxelState? b11)
    {
        bool s00 = IsWall(b00); // SW
        bool s10 = IsWall(b10); // SE
        bool s01 = IsWall(b01); // NW
        bool s11 = IsWall(b11); // NE

        // Accessibility guard: promote enclosed non-fluid blocks to wall status.
        if (!s00 && s10 && s01 && b00.HasValue && b00.Value.Properties.fluidType == FluidType.None) s00 = true;
        if (!s10 && s00 && s11 && b10.HasValue && b10.Value.Properties.fluidType == FluidType.None) s10 = true;
        if (!s01 && s00 && s11 && b01.HasValue && b01.Value.Properties.fluidType == FluidType.None) s01 = true;
        if (!s11 && s10 && s01 && b11.HasValue && b11.Value.Properties.fluidType == FluidType.None) s11 = true;

        float x_push = 0f;
        float z_push = 0f;

        if (s00 && s01) x_push -= 1f;
        if (s10 && s11) x_push += 1f;
        if (s00 && s10) z_push -= 1f;
        if (s01 && s11) z_push += 1f;

        if (x_push == 0f && z_push == 0f)
        {
            if (s00)
            {
                x_push -= 1f;
                z_push -= 1f;
            }
            else if (s10)
            {
                x_push += 1f;
                z_push -= 1f;
            }
            else if (s01)
            {
                x_push -= 1f;
                z_push += 1f;
            }
            else if (s11)
            {
                x_push += 1f;
                z_push += 1f;
            }
        }

        float len = Mathf.Sqrt(x_push * x_push + z_push * z_push);
        return len > 0.001f ? new Vector2(x_push / len, z_push / len) : Vector2.zero;
    }

    Vector2 p_bl = DebugCornerShorePush(sw, s, w, centerState);
    Vector2 p_tl = DebugCornerShorePush(w, centerState, nw, n);
    Vector2 p_br = DebugCornerShorePush(s, se, centerState, e);
    Vector2 p_tr = DebugCornerShorePush(centerState, e, n, ne);

    sb.AppendLine($"  BL (SW / x=0 z=0)  push={p_bl}");
    sb.AppendLine($"  TL (NW / x=0 z=1)  push={p_tl}");
    sb.AppendLine($"  BR (SE / x=1 z=0)  push={p_br}");
    sb.AppendLine($"  TR (NE / x=1 z=1)  push={p_tr}");

    // ── Seam check: NE corner of THIS voxel vs NW corner of the East neighbor ──
    // These represent the SAME physical world vertex and must have identical push vectors.
    VoxelState? eastVoxel = worldData.GetVoxelState(result.HitPosition + new Vector3Int(1, 0, 0));
    VoxelState? eastNorth = worldData.GetVoxelState(result.HitPosition + new Vector3Int(1, 0, 1));
    Vector2 p_eastNW = DebugCornerShorePush(
        centerState, // East's SW
        eastVoxel, // East's SE
        n, // East's NW
        eastNorth // East's NE
    );

    bool pushMatch = Vector2.Distance(p_tr, p_eastNW) < 0.005f;
    string seamResult = pushMatch
        ? "<color=green>✓ MATCH — shared vertex push is identical.</color>"
        : "<color=red>✗ MISMATCH — shared vertex push differs.</color>";

    sb.AppendLine($"\n  [Seam Check: NE of center vs NW of East neighbor]");
    sb.AppendLine($"    Center NE   → push={p_tr}");
    sb.AppendLine($"    East   NW   → push={p_eastNW}");
    sb.AppendLine($"    Result: {seamResult}");

    Debug.Log(sb.ToString());
}

private float GetDebugSmoothHeight(byte centerLevel, VoxelState? n1, VoxelState? n2, VoxelState? nDiag, ushort fluidId)
{
    float[] templates = FluidVertexTemplates.WaterVertexTemplates.ToArray(); // Assume Water for debug
    float totalHeight = templates[centerLevel];
    int count = 1;

    bool n1IsFluid = n1.HasValue && n1.Value.id == fluidId;
    bool n2IsFluid = n2.HasValue && n2.Value.id == fluidId;

    if (n1IsFluid)
    {
        totalHeight += templates[n1.Value.FluidLevel];
        count++;
    }

    if (n2IsFluid)
    {
        totalHeight += templates[n2.Value.FluidLevel];
        count++;
    }

    bool nDiagIsFluid = nDiag.HasValue && nDiag.Value.id == fluidId;
    if ((n1IsFluid || n2IsFluid) && nDiagIsFluid)
    {
        totalHeight += templates[nDiag.Value.FluidLevel];
        count++;
    }

    return totalHeight / count;
}
```
