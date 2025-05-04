using System;
using System.Collections.Generic;
using Data;
using UnityEngine;
using UnityEngine.Rendering;

public class Chunk
{
    public ChunkCoord coord;

    private GameObject chunkObject;
    private MeshRenderer meshRenderer;
    private MeshFilter meshFilter;

    private int vertexIndex = 0;
    private List<Vector3> vertices = new List<Vector3>();
    private List<int> triangles = new List<int>();
    private List<int> transparentTriangles = new List<int>();
    private List<int> waterTriangles = new List<int>();
    private Material[] materials = new Material[3];
    private List<Vector2> uvs = new List<Vector2>();
    private List<Color> colors = new List<Color>();
    private List<Vector3> normals = new List<Vector3>();

    public Vector3 chunkPosition;

    private bool _isActive;

    private ChunkData chunkData;

    private List<VoxelState> activeVoxels = new List<VoxelState>();

    public Chunk(ChunkCoord _coord)
    {
        coord = _coord;
        chunkObject = new GameObject();
        meshFilter = chunkObject.AddComponent<MeshFilter>();
        meshRenderer = chunkObject.AddComponent<MeshRenderer>();

        materials[0] = World.Instance.material;
        materials[1] = World.Instance.transparentMaterial;
        materials[2] = World.Instance.waterMaterial;
        meshRenderer.materials = materials;

        meshRenderer.shadowCastingMode = ShadowCastingMode.TwoSided; // Mostly fixes lines in the shadows between voxels.
        chunkObject.transform.SetParent(World.Instance.transform);
        chunkObject.transform.position = new Vector3(coord.x * VoxelData.ChunkWidth, 0f, coord.z * VoxelData.ChunkWidth);
        chunkObject.name = $"Chunk {coord.x}, {coord.z}";
        chunkPosition = chunkObject.transform.position;

        chunkData = World.Instance.worldData.RequestChunk(new Vector2Int((int)chunkPosition.x, (int)chunkPosition.z), true);
        chunkData.chunk = this;

        // Add active blocks to the active voxel's list.
        for (int y = 0; y < VoxelData.ChunkHeight; y++)
        {
            for (int x = 0; x < VoxelData.ChunkWidth; x++)
            {
                for (int z = 0; z < VoxelData.ChunkWidth; z++)
                {
                    VoxelState voxel = chunkData.map[x, y, z];
                    if (voxel.Properties.isActive)
                        AddActiveVoxel(voxel);
                }
            }
        }

        World.Instance.AddChunkToUpdate(this);

        PlayChunkLoadAnimation();
    }

    public void TickUpdate()
    {
        // Debug.Log(chunkObject.name + " currently has " + activeVoxels.Count + " active blocks.");

        for (int i = activeVoxels.Count - 1; i >= 0; i--)
            if (!BlockBehavior.Active(activeVoxels[i]))
                RemoveActiveVoxel(activeVoxels[i]);
            else
                BlockBehavior.Behave(activeVoxels[i]);
    }


    /// <summary>
    /// Updates chunk on the main thread.
    /// TODO: This function can raise a nullReferenceException.
    /// </summary>
    public void UpdateChunk()
    {
        ClearMeshData();

        for (int y = 0; y < VoxelData.ChunkHeight; y++)
        {
            for (int x = 0; x < VoxelData.ChunkWidth; x++)
            {
                for (int z = 0; z < VoxelData.ChunkWidth; z++)
                {
                    if (chunkData.map[x, y, z].Properties.isSolid)
                        UpdateMeshData(new Vector3(x, y, z));
                }
            }
        }

        World.Instance.chunksToDraw.Enqueue(this);
    }

    public void AddActiveVoxel(VoxelState voxel)
    {
        if (!activeVoxels.Contains(voxel))
            activeVoxels.Add(voxel);
    }

    public void RemoveActiveVoxel(VoxelState voxel)
    {
        for (int i = 0; i < activeVoxels.Count; i++)
        {
            if (activeVoxels[i] == voxel)
            {
                activeVoxels.RemoveAt(i);
                return;
            }
        }
    }

    public int GetActiveVoxelCount()
    {
        return activeVoxels.Count;
    }

    private void ClearMeshData()
    {
        vertexIndex = 0;
        vertices.Clear();
        triangles.Clear();
        transparentTriangles.Clear();
        waterTriangles.Clear();
        uvs.Clear();
        colors.Clear();
        normals.Clear();
    }

    public bool isActive
    {
        get { return _isActive; }
        set
        {
            _isActive = value;
            if (chunkObject != null)
            {
                chunkObject.SetActive(value);
                PlayChunkLoadAnimation();
            }
        }
    }

    public Vector3 GetVoxelPositionInChunkFromGlobalVector3(Vector3 pos)
    {
        int xCheck = Mathf.FloorToInt(pos.x);
        int yCheck = Mathf.FloorToInt(pos.y);
        int zCheck = Mathf.FloorToInt(pos.z);

        xCheck -= Mathf.FloorToInt(chunkPosition.x);
        zCheck -= Mathf.FloorToInt(chunkPosition.z);

        return new Vector3(xCheck, yCheck, zCheck);
    }

    public VoxelState GetVoxelFromGlobalVector3(Vector3 pos)
    {
        int xCheck = Mathf.FloorToInt(pos.x);
        int yCheck = Mathf.FloorToInt(pos.y);
        int zCheck = Mathf.FloorToInt(pos.z);

        xCheck -= Mathf.FloorToInt(chunkPosition.x);
        zCheck -= Mathf.FloorToInt(chunkPosition.z);

        return chunkData.map[xCheck, yCheck, zCheck];
    }

    private VoxelState CheckForVoxel(Vector3 pos)
    {
        int x = Mathf.FloorToInt(pos.x);
        int y = Mathf.FloorToInt(pos.y);
        int z = Mathf.FloorToInt(pos.z);

        if (!chunkData.IsVoxelInChunk(x, y, z))
        {
            return World.Instance.GetVoxelState(pos + chunkPosition);
        }

        return chunkData.map[x, y, z];
    }

    public Vector3 GetHighestVoxel(Vector3 pos)
    {
        int x = Mathf.FloorToInt(pos.x);
        int z = Mathf.FloorToInt(pos.z);

        for (int i = VoxelData.ChunkHeight - 1; i > 0; i--)
        {
            Vector3 currentVoxel = new Vector3(x, i, z);
            if (CheckForVoxel(currentVoxel).Properties.isSolid) return currentVoxel;
        }

        return new Vector3(x, VoxelData.ChunkHeight - 1, z);
    }

    public void EditVoxel(Vector3 pos, byte newID)
    {
        int xCheck = Mathf.FloorToInt(pos.x);
        int yCheck = Mathf.FloorToInt(pos.y);
        int zCheck = Mathf.FloorToInt(pos.z);

        xCheck -= Mathf.FloorToInt(chunkPosition.x);
        zCheck -= Mathf.FloorToInt(chunkPosition.z);

        chunkData.ModifyVoxel(new Vector3Int(xCheck, yCheck, zCheck), newID, World.Instance.player.orientation, true);

        // Update Surrounding Chunks
        UpdateSurroundingVoxels(xCheck, yCheck, zCheck);
    }

    private void UpdateSurroundingVoxels(int x, int y, int z)
    {
        Vector3 thisVoxel = new Vector3(x, y, z);

        for (int p = 0; p < 6; p++) // p = faceIndex
        {
            Vector3 currentVoxel = thisVoxel + VoxelData.FaceChecks[p];

            // Relative position of the voxel within chunk
            int currentVoxelX = (int)currentVoxel.x;
            int currentVoxelY = (int)currentVoxel.y;
            int currentVoxelZ = (int)currentVoxel.z;

            // Absolute position of the voxel (because the UpdateSurroundingVoxels() uses the relative position of a voxel within the chunk, not the global voxel position):
            int currentVoxelWorldPosX = currentVoxelX + (int)chunkPosition.x;
            int currentVoxelWorldPosZ = currentVoxelZ + (int)chunkPosition.z;

            // If surrounding voxel is outside current chunk and still in the world, update that chunk as well
            if (!chunkData.IsVoxelInChunk(currentVoxelX, currentVoxelY, currentVoxelZ)
                && World.Instance.worldData.IsVoxelInWorld(new Vector3(currentVoxelWorldPosX, currentVoxelY, currentVoxelWorldPosZ)))
            {
                Vector3 chunkVector = currentVoxel + chunkPosition;
                Chunk chunk = World.Instance.GetChunkFromVector3(chunkVector);

                // Update current chunk as fast as possible.
                World.Instance.AddChunkToUpdate(chunk, true);
            }
        }
    }

    private void UpdateMeshData(Vector3 pos)
    {
        int x = Mathf.FloorToInt(pos.x);
        int y = Mathf.FloorToInt(pos.y);
        int z = Mathf.FloorToInt(pos.z);

        VoxelState voxel = chunkData.map[x, y, z];
        BlockType voxelProps = voxel.Properties;
        Vector3 voxelWoldPosition = pos + chunkPosition;

        // Calculate rotation angle based on orientation
        float rotation = 0f;
        switch (voxel.orientation)
        {
            case 0: // Backwards
                rotation = 180f;
                break;
            case 5: // Rightwards (East)
                rotation = 270f;
                break;
            case 1: // Front (Default)
                rotation = 0f;
                break;
            // case 4: // Leftwards (West) - Assuming default handles this
            default: // Leftwards (West)
                rotation = 90f;
                break;
        }

        for (int p = 0; p < 6; p++) // p = World Face Direction Index (0=Back, 1=Front, 2=Top, 3=Bottom, 4=Left, 5=Right)
        {
            // --- Culling and Lighting based on ACTUAL neighbor ---
            VoxelState neighborVoxelAbsolute = chunkData.map[x, y, z].neighbours[p];
            BlockType neighborProps = neighborVoxelAbsolute?.Properties; // Cache neighbor props (handles null)

            bool neighborIsTransparent = neighborProps != null && neighborProps.renderNeighborFaces;
            bool neighborIsWater = neighborProps != null && neighborProps.isWater;
            bool neighborIsSolid = neighborProps != null && neighborProps.isSolid;
            bool isEdgeOfWorld = !World.Instance.worldData.IsVoxelInWorld(voxelWoldPosition + VoxelData.FaceChecks[p]);

            bool shouldDrawFace = false;
            if (isEdgeOfWorld)
            {
                shouldDrawFace = true;
            }
            // Neighbor is null but *inside* the world (e.g., unloaded chunk boundary)
            else if (neighborVoxelAbsolute == null)
            {
                // Treat as air/transparent for drawing purposes.
                shouldDrawFace = true;
            }
            // Neighbor exists within the world
            else
            {
                if (voxelProps.isWater) // Current voxel is Water
                {
                    // Water draws face against anything NOT water
                    shouldDrawFace = !neighborIsWater;
                }
                else if (voxelProps.renderNeighborFaces) // Current voxel is Other Transparent (Glass, Leaves)
                {
                    // Transparent face draws if neighbor is solid OR if neighbor is a DIFFERENT type of transparent?
                    // Draw if the neighbor doesn't fully block the view (is not solid opaque)
                    shouldDrawFace = !neighborIsSolid || neighborIsTransparent;
                }
                else // Current voxel is Solid Opaque
                {
                    // Solid face draws if neighbor does NOT block the view
                    // (i.e., neighbor is transparent like water/glass/leaves OR neighbor is air/non-solid)
                    shouldDrawFace = neighborIsTransparent || !neighborIsSolid;
                }
            }

            // --- Proceed only if face is not culled ---
            if (shouldDrawFace)
            {
                // --- Determine which ORIGINAL face data to use based on rotation ---
                int translatedP = p; // Start with the world face index
                // Apply inverse rotation logic to find which original face index (0-5)
                // corresponds to the world direction 'p' AFTER rotation.
                if (voxel.orientation != 1) // Only apply if not default orientation
                {
                    // Rotated backwards (180 deg)
                    if (voxel.orientation == 0)
                    {
                        if (p == 0) translatedP = 1; // back -> front
                        else if (p == 1) translatedP = 0; // front -> back
                        else if (p == 4) translatedP = 5; // left -> right
                        else if (p == 5) translatedP = 4; // right -> left
                        // Top (2) and Bottom (3) remain the same
                    }
                    // Rotated leftwards (90 deg / West)
                    else if (voxel.orientation == 4) // Assuming 4 is West/Left rot
                    {
                        if (p == 0) translatedP = 4; // back -> left
                        else if (p == 1) translatedP = 5; // front -> right
                        else if (p == 4) translatedP = 1; // left -> front
                        else if (p == 5) translatedP = 0; // right -> back
                        // Top (2) and Bottom (3) remain the same
                    }
                    // Rotated rightwards (270 deg / East)
                    else if (voxel.orientation == 5) // Assuming 5 is East/Right rot
                    {
                        if (p == 0) translatedP = 5; // back -> right
                        else if (p == 1) translatedP = 4; // front -> left
                        else if (p == 4) translatedP = 0; // left -> back
                        else if (p == 5) translatedP = 1; // right -> front
                        // Top (2) and Bottom (3) remain the same
                    }
                }

                // --- Get Mesh Data and Texture ID using the translated face index ---
                FaceMeshData faceData = voxelProps.meshData.faces[translatedP];
                int textureID = voxelProps.GetTextureID(translatedP);

                // --- Calculate Light using the ACTUAL neighbor ---
                // If outside the world, neighbor would be null, so use own voxel globalLightPercent in that case.
                float lightLevel = neighborVoxelAbsolute?.lightAsFloat ?? voxel.lightAsFloat;

                // --- Add Geometry ---
                int faceVertCount = 0;
                for (int i = 0; i < faceData.vertData.Length; i++) // Use faceData here
                {
                    VertData vertData = faceData.GetVertData(i); // Use faceData here

                    // Rotate the vertex position based on the voxel's orientation
                    vertices.Add(pos + vertData.GetRotatedPosition(new Vector3(0, rotation, 0)));
                    // Normal vector always points in the world direction 'p'
                    normals.Add(VoxelData.FaceChecks[p]);
                    // Color encodes light level
                    colors.Add(new Color(0, 0, 0, lightLevel));

                    // Add UVs, potentially transformed or just using the textureID lookup
                    if (voxelProps.isWater)
                        uvs.Add(vertData.uv); // Water might use simple UVs
                    else
                        AddTexture(textureID, vertData.uv); // Use looked-up textureID

                    faceVertCount++;
                }

                // --- Assign triangles to the correct submesh based on CURRENT voxel type ---
                if (voxelProps.isWater)
                {
                    foreach (int triangle in faceData.triangles)
                    {
                        waterTriangles.Add(vertexIndex + triangle);
                    }
                }
                else if (voxelProps.renderNeighborFaces) // Other transparent
                {
                    foreach (int triangle in faceData.triangles)
                    {
                        transparentTriangles.Add(vertexIndex + triangle);
                    }
                }
                else // Solid
                {
                    foreach (int triangle in faceData.triangles)
                    {
                        triangles.Add(vertexIndex + triangle);
                    }
                }

                vertexIndex += faceVertCount;
            } // End if(shouldDrawFace)
        } // End for loop (p)
    }

    public void CreateMesh()
    {
        Mesh mesh = new Mesh();
        mesh.vertices = vertices.ToArray();

        mesh.subMeshCount = 3;
        mesh.SetTriangles(triangles.ToArray(), 0);
        mesh.SetTriangles(transparentTriangles.ToArray(), 1);
        mesh.SetTriangles(waterTriangles.ToArray(), 2);

        mesh.uv = uvs.ToArray();
        mesh.colors = colors.ToArray();
        mesh.normals = normals.ToArray();

        meshFilter.mesh = mesh;
    }

    private void AddTexture(int textureID, Vector2 uv)
    {
        float y = Mathf.FloorToInt((float)textureID / VoxelData.TextureAtlasSizeInBlocks);
        float x = textureID - (y * VoxelData.TextureAtlasSizeInBlocks);

        x *= VoxelData.NormalizedBlockTextureSize;
        y *= VoxelData.NormalizedBlockTextureSize;

        // To start reading the atlas from the top left
        y = 1f - y - VoxelData.NormalizedBlockTextureSize;

        x += VoxelData.NormalizedBlockTextureSize * uv.x;
        y += VoxelData.NormalizedBlockTextureSize * uv.y;

        uvs.Add(new Vector2(x, y));
    }


    #region Bonus Stuff

    private void PlayChunkLoadAnimation()
    {
        if (World.Instance.settings.enableChunkLoadAnimations && chunkObject.GetComponent<ChunkLoadAnimation>() == null)
            chunkObject.AddComponent<ChunkLoadAnimation>();
    }

    #endregion
}

public class ChunkCoord : IEquatable<ChunkCoord>
{
    public readonly int x;
    public readonly int z;

    public ChunkCoord()
    {
        x = 0;
        z = 0;
    }

    public ChunkCoord(int _x, int _z)
    {
        x = _x;
        z = _z;
    }

    public ChunkCoord(Vector3 pos)
    {
        int xCheck = Mathf.FloorToInt(pos.x);
        int zCheck = Mathf.FloorToInt(pos.z);

        x = xCheck / VoxelData.ChunkWidth;
        z = zCheck / VoxelData.ChunkWidth;
    }

    public override int GetHashCode()
    {
        // Multiply x & y by different constant to differentiate between situations like x=12 & z=13 and x=13 & z=12.
        return 31 * x + 17 * z;
    }

    public override bool Equals(object obj)
    {
        return obj is ChunkCoord coord && Equals(coord);
    }

    public bool Equals(ChunkCoord other)
    {
        if (other == null)
            return false;

        return other.x == x && other.z == z;
    }
}