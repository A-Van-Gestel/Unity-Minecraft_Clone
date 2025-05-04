using System;
using System.Collections.Generic;
using Data;
using Helpers;
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
        get => _isActive;
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
        Vector3 voxelWorldPosition = pos + chunkPosition;

        // Calculate rotation angle based on orientation
        float rotation = VoxelHelper.GetRotationAngle(voxel.orientation);

        for (int p = 0; p < 6; p++) // p = World Face Direction Index (0=Back, 1=Front, 2=Top, 3=Bottom, 4=Left, 5=Right)
        {
            // --- Determine Original Face Index (translatedP) ---
            // This calculation determines which *original* face of the block
            // ends up pointing in the *world* direction 'p' after rotation.
            int translatedP = VoxelHelper.GetTranslatedFaceIndex(p, voxel.orientation);

            // --- Culling Decision based on the NEIGHBOR matching the ORIGINAL face direction ---
            VoxelState neighborVoxel = voxel.neighbours[p];
            BlockType neighborProps = neighborVoxel?.Properties; // Cache absolute neighbor props

            bool isEdgeOfWorld = !World.Instance.worldData.IsVoxelInWorld(voxelWorldPosition + VoxelData.FaceChecks[p]);

            bool shouldDrawFace = false;
            // Rule 1: Always draw if facing the edge of the loaded world.
            if (isEdgeOfWorld)
            {
                shouldDrawFace = true;
            }
            // Rule 2: Neighbor is null but *inside* the world (e.g., unloaded chunk boundary)
            else if (neighborVoxel == null)
            {
                // Treat as air / transparent for drawing purposes.
                shouldDrawFace = true;
            }
            // Neighbor exists within the world
            else
            {
                bool neighborIsTransparent = neighborProps.renderNeighborFaces;
                bool neighborIsSolid = neighborProps.isSolid;
                bool neighborIsWater = neighborProps.isWater;

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
                    // TODO: This prevents drawing glass against glass -> Shader should draw backfaces
                    // if (neighborIsTransparent && neighborProps == voxelProps) shouldDrawFace = false;
                }
                else // Current voxel is Solid Opaque
                {
                    // Solid face draws if neighbor does NOT block the view
                    // (i.e., neighbor is transparent like water/glass/leaves OR neighbor is air/non-solid)
                    shouldDrawFace = neighborIsTransparent || !neighborIsSolid;
                }
            }

            // --- Add Geometry ONLY if this face should be drawn ---
            if (shouldDrawFace)
            {
                // --- Get Mesh Data using the 'translatedP' index ---
                FaceMeshData faceData = voxelProps.meshData.faces[translatedP];
                int textureID = voxelProps.GetTextureID(translatedP);

                // --- Calculate Light using the ACTUAL neighbor's space ('neighborVoxel') ---
                // If outside the world, neighbor would be null, so use own voxel globalLightPercent in that case.
                float lightLevel = neighborVoxel?.lightAsFloat ?? voxel.lightAsFloat;

                // --- Add Geometry ---
                int faceVertCount = faceData.vertData.Length;
                for (int i = 0; i < faceData.vertData.Length; i++)
                {
                    VertData vertData = faceData.GetVertData(i);

                    // Rotate the vertex position based on the voxel's orientation
                    vertices.Add(pos + vertData.GetRotatedPosition(new Vector3(0, rotation, 0)));
                    // Normal vector always points in the world direction 'p'
                    normals.Add(VoxelData.FaceChecks[p]);
                    // Color encodes light level
                    colors.Add(new Color(0, 0, 0, lightLevel));

                    // Add UVs, potentially transformed or just using the textureID lookup
                    if (voxelProps.isWater)
                        uvs.Add(vertData.uv);
                    else
                        AddTexture(textureID, vertData.uv); // Use looked-up textureID
                }

                // --- Assign triangles to the correct submesh based on CURRENT voxel type ---
                if (voxelProps.isWater)
                {
                    foreach (int triangle in faceData.triangles)
                    {
                        waterTriangles.Add(vertexIndex + triangle);
                    }
                }
                else if (voxelProps.renderNeighborFaces) // Other transparent blocks
                {
                    foreach (int triangle in faceData.triangles)
                    {
                        transparentTriangles.Add(vertexIndex + triangle);
                    }
                }
                else // Solid Opaque blocks
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