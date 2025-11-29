using System.Collections.Generic;
using Helpers;
using UnityEngine;

namespace DebugVisualizations
{
    /// <summary>
    /// Visualizes the boundaries, internal grid, and vertical sections of a Chunk.
    /// <para>
    /// <b>Optimization Note:</b> To maintain high performance with high render distances, this script 
    /// generates a single static <see cref="Mesh"/> that is shared across all chunk instances. 
    /// This reduces memory overhead and CPU cost for generation to near-zero after the first chunk is loaded.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class ChunkBorderVisualizer : MonoBehaviour
    {
        #region Inspector Settings

        [Header("Visual Settings")]
        [Tooltip("The material to use for the red chunk border lines.")]
        public Material borderMaterial;

        [Tooltip("The material to use for the yellow grid lines.")]
        public Material gridMaterial;

        [Tooltip("The material to use for the green section division lines.")]
        public Material sectionMaterial;

        [Tooltip("The interval in blocks at which to draw the grid lines.")]
        public int gridInterval = 4;

        [Tooltip("If true, an internal cross (X) will be drawn within each section layer for better visibility.")]
        public bool renderSectionsInternalCross = true;

        #endregion

        #region Static Cache

        // --- Static Caching ---
        // We cache the mesh so we don't regenerate it for every single chunk (since they are all identical 16x128x16 size).
        // This dramatically reduces memory pressure and instantiation time.
        private static Mesh _cachedMesh;

        // We track the settings used to generate the cached mesh. 
        // If the inspector settings change, we detect it and rebuild the static mesh.
        private static int _cachedGridInterval;
        private static bool _cachedCrossSectionSetting;

        #endregion

        private void Start()
        {
            // Ensure materials are assigned to prevent errors
            if (borderMaterial == null || gridMaterial == null || sectionMaterial == null)
            {
                Debug.LogError("Border, Grid, and Section materials must be assigned in the ChunkBorderVisualizer inspector.", this);
                return;
            }

            // Check if we need to build (or rebuild) the shared mesh.
            // This happens on the very first chunk load, or if the developer changes settings in the Inspector at runtime.
            if (_cachedMesh == null || _cachedGridInterval != gridInterval || _cachedCrossSectionSetting != renderSectionsInternalCross)
            {
                RebuildSharedMesh();
            }

            // Apply the shared mesh. This is a lightweight reference copy.
            GetComponent<MeshFilter>().sharedMesh = _cachedMesh;

            // Assign materials. The order matches the subMesh indices defined in RebuildSharedMesh:
            // 0: Border, 1: Grid, 2: Sections
            GetComponent<MeshRenderer>().sharedMaterials = new Material[] { borderMaterial, gridMaterial, sectionMaterial };
        }

        /// <summary>
        /// Generates the geometry for the chunk visualization. 
        /// This creates a single Mesh with 3 SubMeshes (one for each visualization type).
        /// </summary>
        private void RebuildSharedMesh()
        {
            // Update cache trackers
            _cachedGridInterval = gridInterval;
            _cachedCrossSectionSetting = renderSectionsInternalCross;

            // Clean up old mesh if it existed to prevent memory leaks in Editor
            if (_cachedMesh != null) Destroy(_cachedMesh);

            _cachedMesh = new Mesh();
            _cachedMesh.name = "SharedChunkBorderMesh";

            // Pre-allocate lists to avoid resizing overhead during generation.
            // Vertices are shared across all submeshes to minimize memory footprint.
            List<Vector3> allVertices = new List<Vector3>(2048);

            // Indices are separated by submesh (Material).
            List<int> borderIndices = new List<int>(24); // 12 lines * 2 indices per line
            List<int> gridIndices = new List<int>(512); // Approx estimate
            List<int> sectionIndices = new List<int>(128); // Approx estimate

            // --- 1. Generate Border (SubMesh 0) ---
            GenerateBorder(allVertices, borderIndices);

            // --- 2. Generate Grid (SubMesh 1) ---
            GenerateGrid(allVertices, gridIndices);

            // --- 3. Generate Sections (SubMesh 2) ---
            GenerateSections(allVertices, sectionIndices);

            // --- Apply to Mesh ---
            // Set vertices once
            _cachedMesh.SetVertices(allVertices);

            // Define 3 SubMeshes using Line Topology (lighter than Triangle strips for wireframes)
            _cachedMesh.subMeshCount = 3;
            _cachedMesh.SetIndices(borderIndices, MeshTopology.Lines, 0);
            _cachedMesh.SetIndices(gridIndices, MeshTopology.Lines, 1);
            _cachedMesh.SetIndices(sectionIndices, MeshTopology.Lines, 2);

            // Bounds are static 16x128x16
            _cachedMesh.RecalculateBounds();

            // Optimization: Upload to GPU and mark no longer readable.
            // Since we never modify this mesh after generation, this frees up system memory.
            _cachedMesh.UploadMeshData(false);
        }

        /// <summary>
        /// Generates the outer 12 edges of the chunk column (The Red Box).
        /// </summary>
        private void GenerateBorder(List<Vector3> verts, List<int> indices)
        {
            float w = VoxelData.ChunkWidth;
            float h = VoxelData.ChunkHeight;

            // Helper to add a line segment (2 verts, 2 indices)
            void AddLine(Vector3 start, Vector3 end)
            {
                indices.Add(verts.Count); // Start index
                verts.Add(start);
                indices.Add(verts.Count); // End index
                verts.Add(end);
            }

            // 1. Vertical Pillars (Corners)
            AddLine(new Vector3(0, 0, 0), new Vector3(0, h, 0));
            AddLine(new Vector3(w, 0, 0), new Vector3(w, h, 0));
            AddLine(new Vector3(w, 0, w), new Vector3(w, h, w));
            AddLine(new Vector3(0, 0, w), new Vector3(0, h, w));

            // 2. Bottom Square Cap
            AddLine(new Vector3(0, 0, 0), new Vector3(w, 0, 0)); // Back
            AddLine(new Vector3(w, 0, 0), new Vector3(w, 0, w)); // Right
            AddLine(new Vector3(w, 0, w), new Vector3(0, 0, w)); // Front
            AddLine(new Vector3(0, 0, w), new Vector3(0, 0, 0)); // Left

            // 3. Top Square Cap
            AddLine(new Vector3(0, h, 0), new Vector3(w, h, 0)); // Back
            AddLine(new Vector3(w, h, 0), new Vector3(w, h, w)); // Right
            AddLine(new Vector3(w, h, w), new Vector3(0, h, w)); // Front
            AddLine(new Vector3(0, h, w), new Vector3(0, h, 0)); // Left
        }

        /// <summary>
        /// Generates the internal grid lines based on the gridInterval.
        /// </summary>
        private void GenerateGrid(List<Vector3> verts, List<int> indices)
        {
            if (gridInterval <= 0) return;

            float w = VoxelData.ChunkWidth;
            float h = VoxelData.ChunkHeight;

            void AddLine(Vector3 start, Vector3 end)
            {
                indices.Add(verts.Count);
                verts.Add(start);
                indices.Add(verts.Count);
                verts.Add(end);
            }

            // Lines parallel to the X axis (horizontal)
            // We skip lines that would overlap with the Section dividers to avoid Z-fighting/visual clutter.
            for (float i = gridInterval; i < h; i += gridInterval)
            {
                if (i % ChunkMath.SECTION_SIZE == 0) continue; // Skip overlap with sections

                AddLine(new Vector3(0, i, 0), new Vector3(w, i, 0)); // Back Face
                AddLine(new Vector3(0, i, w), new Vector3(w, i, w)); // Front Face
                AddLine(new Vector3(0, i, 0), new Vector3(0, i, w)); // Left Face
                AddLine(new Vector3(w, i, 0), new Vector3(w, i, w)); // Right Face
            }

            // Lines parallel to the Y axis (vertical)
            for (float i = gridInterval; i < w; i += gridInterval)
            {
                AddLine(new Vector3(i, 0, 0), new Vector3(i, h, 0)); // Back Face
                AddLine(new Vector3(i, 0, w), new Vector3(i, h, w)); // Front Face
                AddLine(new Vector3(0, 0, i), new Vector3(0, h, i)); // Left Face
                AddLine(new Vector3(w, 0, i), new Vector3(w, h, i)); // Right Face
            }

            // Lines parallel to the Z axis (depth caps on Top and Bottom)
            for (float i = gridInterval; i < w; i += gridInterval)
            {
                AddLine(new Vector3(0, 0, i), new Vector3(w, 0, i)); // Bottom Face
                AddLine(new Vector3(0, h, i), new Vector3(w, h, i)); // Top Face
                AddLine(new Vector3(i, 0, 0), new Vector3(i, 0, w)); // Bottom Face
                AddLine(new Vector3(i, h, 0), new Vector3(i, h, w)); // Top Face
            }
        }

        /// <summary>
        /// Generates horizontal frames indicating where ChunkSections start and end.
        /// </summary>
        private void GenerateSections(List<Vector3> verts, List<int> indices)
        {
            float w = VoxelData.ChunkWidth;
            float h = VoxelData.ChunkHeight;
            float sectionSize = ChunkMath.SECTION_SIZE;

            void AddLine(Vector3 start, Vector3 end)
            {
                indices.Add(verts.Count);
                verts.Add(start);
                indices.Add(verts.Count);
                verts.Add(end);
            }

            // Iterate vertically by section height (e.g., 0, 16, 32...)
            for (float y = 0; y <= h; y += sectionSize)
            {
                // Draw a horizontal square frame at height Y
                AddLine(new Vector3(0, y, 0), new Vector3(w, y, 0)); // Back
                AddLine(new Vector3(w, y, 0), new Vector3(w, y, w)); // Right
                AddLine(new Vector3(w, y, w), new Vector3(0, y, w)); // Front
                AddLine(new Vector3(0, y, w), new Vector3(0, y, 0)); // Left

                // Optionally draw an internal cross (X) to make the floor of the section visible from above/below
                if (renderSectionsInternalCross)
                {
                    AddLine(new Vector3(0, y, 0), new Vector3(w, y, w)); // Diagonal 1
                    AddLine(new Vector3(w, y, 0), new Vector3(0, y, w)); // Diagonal 2
                }
            }
        }
    }
}
