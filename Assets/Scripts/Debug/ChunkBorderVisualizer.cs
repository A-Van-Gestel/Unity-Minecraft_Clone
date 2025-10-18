using System.Collections.Generic;
using UnityEngine;

public class ChunkBorderVisualizer : MonoBehaviour
{
    [Header("Visual Settings")]
    [Tooltip("The material to use for the red chunk border lines.")]
    public Material borderMaterial; // New public field for the border material

    [Tooltip("The material to use for the yellow grid lines.")]
    public Material gridMaterial; // New public field for the grid material

    [Tooltip("The interval in blocks at which to draw the grid lines.")]
    public int gridInterval = 4;

    private LineRenderer _borderLineRenderer;

    void Start()
    {
        // Ensure materials are assigned to prevent errors
        if (borderMaterial == null || gridMaterial == null)
        {
            Debug.LogError("Border and Grid materials must be assigned in the ChunkBorderVisualizer inspector.", this);
            return;
        }

        // Set up the main border renderer on this GameObject
        _borderLineRenderer = gameObject.AddComponent<LineRenderer>();
        SetupBorderLineRenderer();
        DrawBorders();

        // Set up the grid renderer on a child GameObject
        GameObject gridObject = new GameObject("Grid");
        gridObject.transform.SetParent(transform, false); // false = don't use world position
        SetupGridMesh(gridObject);
        DrawGrid(gridObject);
    }

    #region Border Methods

    private void SetupBorderLineRenderer()
    {
        // Directly assign the material from the public field.
        _borderLineRenderer.material = borderMaterial;

        _borderLineRenderer.startWidth = 0.15f;
        _borderLineRenderer.endWidth = 0.15f;
        _borderLineRenderer.positionCount = 16;
        _borderLineRenderer.useWorldSpace = false;
        _borderLineRenderer.loop = false;
    }

    private void DrawBorders()
    {
        float w = VoxelData.ChunkWidth;
        float h = VoxelData.ChunkHeight;

        var p = new Vector3[16];

        // This sequence draws all 12 edges of a cube with one non-looping line strip
        p[0] = new Vector3(0, 0, 0);
        p[1] = new Vector3(w, 0, 0);
        p[2] = new Vector3(w, 0, w);
        p[3] = new Vector3(0, 0, w);
        p[4] = new Vector3(0, 0, 0); // Close bottom loop
        p[5] = new Vector3(0, h, 0); // Go up from corner 1
        p[6] = new Vector3(w, h, 0); // Start top loop
        p[7] = new Vector3(w, 0, 0); // Go down to corner 2
        p[8] = new Vector3(w, h, 0); // Go back up
        p[9] = new Vector3(w, h, w);
        p[10] = new Vector3(w, 0, w); // Go down to corner 3
        p[11] = new Vector3(w, h, w); // Go back up
        p[12] = new Vector3(0, h, w);
        p[13] = new Vector3(0, 0, w); // Go down to corner 4
        p[14] = new Vector3(0, h, w); // Go back up
        p[15] = new Vector3(0, h, 0); // Close top loop

        _borderLineRenderer.SetPositions(p);
    }

    #endregion

    #region Grid Methods

    private void SetupGridMesh(GameObject gridObject)
    {
        gridObject.AddComponent<MeshFilter>();
        MeshRenderer mr = gridObject.AddComponent<MeshRenderer>();

        // Directly assign the material for the grid.
        mr.material = gridMaterial;
    }

    private void DrawGrid(GameObject gridObject)
    {
        if (gridInterval <= 0) return;

        var vertices = new List<Vector3>();
        var indices = new List<int>();
        int currentIndex = 0;

        float w = VoxelData.ChunkWidth;
        float h = VoxelData.ChunkHeight;

        // Helper function to add a line segment to our lists
        void AddLine(Vector3 start, Vector3 end)
        {
            vertices.Add(start);
            vertices.Add(end);
            indices.Add(currentIndex++);
            indices.Add(currentIndex++);
        }

        // --- Generate the unique set of grid lines ---

        // Lines parallel to the Y axis (vertical)
        for (float i = gridInterval; i < w; i += gridInterval)
        {
            AddLine(new Vector3(i, 0, 0), new Vector3(i, h, 0)); // On back face
            AddLine(new Vector3(i, 0, w), new Vector3(i, h, w)); // On front face
            AddLine(new Vector3(0, 0, i), new Vector3(0, h, i)); // On left face
            AddLine(new Vector3(w, 0, i), new Vector3(w, h, i)); // On right face
        }

        // Lines parallel to the X axis (horizontal)
        for (float i = gridInterval; i < h; i += gridInterval)
        {
            AddLine(new Vector3(0, i, 0), new Vector3(w, i, 0)); // On back face
            AddLine(new Vector3(0, i, w), new Vector3(w, i, w)); // On front face
        }

        for (float i = gridInterval; i < w; i += gridInterval)
        {
            AddLine(new Vector3(0, 0, i), new Vector3(w, 0, i)); // On bottom face
            AddLine(new Vector3(0, h, i), new Vector3(w, h, i)); // On top face
        }

        // Lines parallel to the Z axis (depth)
        for (float i = gridInterval; i < h; i += gridInterval)
        {
            AddLine(new Vector3(0, i, 0), new Vector3(0, i, w)); // On left face
            AddLine(new Vector3(w, i, 0), new Vector3(w, i, w)); // On right face
        }

        for (float i = gridInterval; i < w; i += gridInterval)
        {
            AddLine(new Vector3(i, 0, 0), new Vector3(i, 0, w)); // On bottom face
            AddLine(new Vector3(i, h, 0), new Vector3(i, h, w)); // On top face
        }

        // --- Create the mesh ---
        Mesh mesh = new Mesh();
        mesh.SetVertices(vertices);
        // Set the indices for the Line topology, where each pair of indices defines one line
        mesh.SetIndices(indices.ToArray(), MeshTopology.Lines, 0);

        gridObject.GetComponent<MeshFilter>().mesh = mesh;
    }

    #endregion
}