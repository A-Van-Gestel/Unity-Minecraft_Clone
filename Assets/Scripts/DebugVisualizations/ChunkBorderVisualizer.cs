using System.Collections.Generic;
using Helpers;
using UnityEngine;
using UnityEngine.Rendering;

namespace DebugVisualizations
{
    public class ChunkBorderVisualizer : MonoBehaviour
    {
        [Header("Visual Settings")]
        [Tooltip("The material to use for the red chunk border lines.")]
        public Material borderMaterial;

        [Tooltip("The material to use for the yellow grid lines.")]
        public Material gridMaterial;

        [Tooltip("The material to use for the green section division lines.")]
        public Material sectionMaterial; // New field for section visualization

        [Tooltip("The interval in blocks at which to draw the grid lines.")]
        public int gridInterval = 4;
        
        [Tooltip("If true, an internal cross sections of sections will be rendered.")]
        public bool renderSectionsInternalCross = true;

        private LineRenderer _borderLineRenderer;

        private void Start()
        {
            // Ensure materials are assigned to prevent errors
            if (borderMaterial == null || gridMaterial == null || sectionMaterial == null)
            {
                Debug.LogError("Border, Grid, and Section materials must be assigned in the ChunkBorderVisualizer inspector.", this);
                return;
            }

            // 1. Main Border (Red)
            _borderLineRenderer = gameObject.AddComponent<LineRenderer>();
            SetupBorderLineRenderer();
            DrawBorders();

            // 2. Grid (Yellow)
            GameObject gridObject = new GameObject("Grid");
            gridObject.transform.SetParent(transform, false);
            SetupMeshRenderer(gridObject, gridMaterial);
            DrawGrid(gridObject);

            // 3. Sections (Green)
            GameObject sectionObject = new GameObject("Sections");
            sectionObject.transform.SetParent(transform, false);
            SetupMeshRenderer(sectionObject, sectionMaterial);
            DrawSections(sectionObject);
        }

        #region Border Methods

        private void SetupBorderLineRenderer()
        {
            _borderLineRenderer.material = borderMaterial;
            _borderLineRenderer.startWidth = 0.15f;
            _borderLineRenderer.endWidth = 0.15f;
            _borderLineRenderer.positionCount = 16;
            _borderLineRenderer.useWorldSpace = false;
            _borderLineRenderer.loop = false;
        }

        private void DrawBorders()
        {
            const int w = VoxelData.ChunkWidth;
            const int h = VoxelData.ChunkHeight;

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

        #region Mesh Generation Methods

        private void SetupMeshRenderer(GameObject obj, Material mat)
        {
            obj.AddComponent<MeshFilter>();
            MeshRenderer mr = obj.AddComponent<MeshRenderer>();
            mr.material = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        private void DrawGrid(GameObject gridObject)
        {
            if (gridInterval <= 0) return;

            var vertices = new List<Vector3>();
            var indices = new List<int>();
            int currentIndex = 0;

            const int w = VoxelData.ChunkWidth;
            const int h = VoxelData.ChunkHeight;

            // Helper function to add a line segment
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
                AddLine(new Vector3(i, 0, 0), new Vector3(i, h, 0)); // Back
                AddLine(new Vector3(i, 0, w), new Vector3(i, h, w)); // Front
                AddLine(new Vector3(0, 0, i), new Vector3(0, h, i)); // Left
                AddLine(new Vector3(w, 0, i), new Vector3(w, h, i)); // Right
            }

            // Lines parallel to the X axis (horizontal)
            for (float i = gridInterval; i < h; i += gridInterval)
            {
                // Skip lines that overlap with section borders to avoid Z-fighting, 
                // assuming gridInterval fits into SectionSize (e.g. 4 fits into 16).
                if (i % ChunkMath.SECTION_SIZE == 0) continue;

                AddLine(new Vector3(0, i, 0), new Vector3(w, i, 0)); // Back
                AddLine(new Vector3(0, i, w), new Vector3(w, i, w)); // Front
            }

            for (float i = gridInterval; i < w; i += gridInterval)
            {
                AddLine(new Vector3(0, 0, i), new Vector3(w, 0, i)); // Bottom
                AddLine(new Vector3(0, h, i), new Vector3(w, h, i)); // Top
            }

            // Lines parallel to the Z axis (depth)
            for (float i = gridInterval; i < h; i += gridInterval)
            {
                if (i % ChunkMath.SECTION_SIZE == 0) continue;

                AddLine(new Vector3(0, i, 0), new Vector3(0, i, w)); // Left
                AddLine(new Vector3(w, i, 0), new Vector3(w, i, w)); // Right
            }

            for (float i = gridInterval; i < w; i += gridInterval)
            {
                AddLine(new Vector3(i, 0, 0), new Vector3(i, 0, w)); // Bottom
                AddLine(new Vector3(i, h, 0), new Vector3(i, h, w)); // Top
            }

            ApplyMesh(gridObject, vertices, indices);
        }

        private void DrawSections(GameObject sectionObject)
        {
            var vertices = new List<Vector3>();
            var indices = new List<int>();
            int currentIndex = 0;

            const int w = VoxelData.ChunkWidth;
            const int h = VoxelData.ChunkHeight;
            const int sectionSize = ChunkMath.SECTION_SIZE;

            void AddLine(Vector3 start, Vector3 end)
            {
                vertices.Add(start);
                vertices.Add(end);
                indices.Add(currentIndex++);
                indices.Add(currentIndex++);
            }

            // Draw horizontal frames at every section interval (0, 16, 32, ..., 128)
            // We skip 0 and 128 if we want to avoid overlapping the red border, 
            // but drawing them ensures the section visualizer is complete on its own.
            for (int y = 0; y <= h; y += sectionSize)
            {
                // Draw a horizontal square at height Y
                AddLine(new Vector3(0, y, 0), new Vector3(w, y, 0)); // Back edge
                AddLine(new Vector3(w, y, 0), new Vector3(w, y, w)); // Right edge
                AddLine(new Vector3(w, y, w), new Vector3(0, y, w)); // Front edge
                AddLine(new Vector3(0, y, w), new Vector3(0, y, 0)); // Left edge

                // Draw internal cross for better section visibility (Optional)
                if (renderSectionsInternalCross)
                {
                    AddLine(new Vector3(0, y, 0), new Vector3(w, y, w)); 
                    AddLine(new Vector3(w, y, 0), new Vector3(0, y, w));
                }
            }

            ApplyMesh(sectionObject, vertices, indices);
        }

        private void ApplyMesh(GameObject obj, List<Vector3> verts, List<int> indices)
        {
            Mesh mesh = new Mesh();
            mesh.SetVertices(verts);
            mesh.SetIndices(indices.ToArray(), MeshTopology.Lines, 0);
            obj.GetComponent<MeshFilter>().mesh = mesh;
        }

        #endregion
    }
}
