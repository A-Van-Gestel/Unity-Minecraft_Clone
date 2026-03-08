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
