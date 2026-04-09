using System.Collections.Generic;
using Helpers;
using UnityEngine;

namespace DebugVisualizations
{
    /// <summary>
    /// Visualizes the boundaries, internal grid, and vertical sections of a Chunk for debugging purposes.
    /// <para>
    /// <b>Optimization Strategy:</b><br/>
    /// Since all chunks in the world share the exact same dimensions (16x128x16), generating a unique mesh
    /// for every chunk is incredibly wasteful. This script generates a single <b>static</b> <see cref="Mesh"/>
    /// containing the visualization geometry. All instances of this script simply reference that shared mesh.
    /// </para>
    /// <para>
    /// <b>Rendering Approach:</b><br/>
    /// To support both "Thick" lines (borders/sections) and "Thin" lines (grid/crosses) within a single draw call,
    /// the mesh is divided into 4 SubMeshes with different Topologies:
    /// <list type="bullet">
    /// <item><b>SubMesh 0 (Border):</b> Triangle Topology (Thick geometry)</item>
    /// <item><b>SubMesh 1 (Grid):</b> Line Topology (Thin wireframe)</item>
    /// <item><b>SubMesh 2 (Section Frame):</b> Triangle Topology (Thick geometry)</item>
    /// <item><b>SubMesh 3 (Section Cross):</b> Line Topology (Thin wireframe)</item>
    /// </list>
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

        [Header("Configuration")]
        [Tooltip("The interval in blocks at which to draw the grid lines (default: 4).")]
        public int gridInterval = 4;

        [Tooltip("If true, an internal cross (X) will be drawn within each section layer for better visibility.")]
        public bool renderSectionsInternalCross = true;

        [Header("Thickness Settings")]
        [Tooltip("Thickness of the main chunk border pillars (Rendered as 3D boxes).")]
        public float borderThickness = 0.15f;

        [Tooltip("Thickness of the horizontal section division frames (Rendered as 3D boxes).")]
        public float sectionFrameThickness = 0.05f;

        #endregion

        #region Static Cache

        // --- Static Caching ---
        // We cache the mesh so we don't regenerate it for every single chunk.
        // This reduces memory overhead from ~400 meshes to 1 mesh for the entire world.
        private static Mesh _cachedMesh;

        // Track settings to detect changes in the Inspector and rebuild the mesh if necessary.
        private static int _cachedGridInterval;
        private static bool _cachedCrossSectionSetting;
        private static float _cachedBorderThick;
        private static float _cachedSectionThick;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            _cachedMesh = null;
            _cachedGridInterval = 0;
            _cachedCrossSectionSetting = false;
            _cachedBorderThick = 0f;
            _cachedSectionThick = 0f;
        }

        #endregion

        private void Start()
        {
            // Fail-safe: Ensure materials are assigned
            if (borderMaterial == null || gridMaterial == null || sectionMaterial == null)
            {
                Debug.LogError("Border, Grid, and Section materials must be assigned in the ChunkBorderVisualizer inspector.", this);
                enabled = false;
                return;
            }

            // Check if we need to build (or rebuild) the shared mesh.
            // This happens on the very first chunk load, or if the developer changes settings in the Inspector at runtime.
            if (_cachedMesh == null ||
                _cachedGridInterval != gridInterval ||
                _cachedCrossSectionSetting != renderSectionsInternalCross ||
                !Mathf.Approximately(_cachedBorderThick, borderThickness) ||
                !Mathf.Approximately(_cachedSectionThick, sectionFrameThickness))
            {
                RebuildSharedMesh();
            }

            // Apply the shared mesh. This is a lightweight reference copy.
            GetComponent<MeshFilter>().sharedMesh = _cachedMesh;

            // Assign materials.
            // We have 4 SubMeshes, so we need an array of 4 Materials.
            // SubMesh 2 (Section Frame) and SubMesh 3 (Section Cross) share the same material.
            GetComponent<MeshRenderer>().sharedMaterials = new[]
            {
                borderMaterial,
                gridMaterial,
                sectionMaterial,
                sectionMaterial,
            };
        }

        /// <summary>
        /// Generates the geometry for the chunk visualization.
        /// This creates a single Mesh with 4 SubMeshes, mixing Triangle and Line topologies.
        /// </summary>
        private void RebuildSharedMesh()
        {
            // Update cache trackers to prevent unnecessary rebuilds
            _cachedGridInterval = gridInterval;
            _cachedCrossSectionSetting = renderSectionsInternalCross;
            _cachedBorderThick = borderThickness;
            _cachedSectionThick = sectionFrameThickness;

            // Clean up old mesh if it existed to prevent memory leaks in Editor
            if (_cachedMesh != null) Destroy(_cachedMesh);

            _cachedMesh = new Mesh();
            _cachedMesh.name = "SharedChunkBorderMesh";

            // Pre-allocate a vertex list.
            // Vertices are shared across all submeshes to minimize memory footprint.
            List<Vector3> allVertices = new List<Vector3>(4096);

            // INDICES for the 4 SubMeshes:
            List<int> borderIndices = new List<int>(); // Triangles (Thick Geometry)
            List<int> gridIndices = new List<int>(); // Lines (Thin Wireframe)
            List<int> sectionFrameIndices = new List<int>(); // Triangles (Thick Geometry)
            List<int> sectionCrossIndices = new List<int>(); // Lines (Thin Wireframe)

            // --- 1. Generate Border (Geometry) ---
            // Generates the outer red box using thick rectangular prisms.
            GenerateBorderGeometry(allVertices, borderIndices, borderThickness);

            // --- 2. Generate Grid (Lines) ---
            // Generates the internal yellow grid using lightweight lines.
            GenerateGridLines(allVertices, gridIndices);

            // --- 3. Generate Sections (Mixed) ---
            // Generates green horizontal frames (Thick Geometry)
            GenerateSectionGeometry(allVertices, sectionFrameIndices, sectionFrameThickness);
            // Generates optional internal crosses (Thin Lines)
            GenerateSectionCrossLines(allVertices, sectionCrossIndices);

            // --- Apply Data to Mesh ---
            // 1. Set vertices once for the whole mesh.
            _cachedMesh.SetVertices(allVertices);

            // 2. Define SubMeshes.
            _cachedMesh.subMeshCount = 4;

            // Note: We deliberately mix Topologies here.
            // SubMeshes 0 & 2 use Triangles (for thickness).
            // SubMeshes 1 & 3 use Lines (for performance).
            _cachedMesh.SetIndices(borderIndices, MeshTopology.Triangles, 0);
            _cachedMesh.SetIndices(gridIndices, MeshTopology.Lines, 1);
            _cachedMesh.SetIndices(sectionFrameIndices, MeshTopology.Triangles, 2);
            _cachedMesh.SetIndices(sectionCrossIndices, MeshTopology.Lines, 3);

            _cachedMesh.RecalculateBounds();

            // Optimization: Upload to GPU and mark no longer readable.
            // Since we never modify this mesh after generation, this frees up system memory.
            _cachedMesh.UploadMeshData(false);
        }

        #region Geometry Generators (Thick Lines using Triangles)

        /// <summary>
        /// Generates the 12 edges of the chunk using elongated cubes (geometry).
        /// </summary>
        /// <param name="verts">The list of vertices to append to.</param>
        /// <param name="indices">The list of triangle indices to append to.</param>
        /// <param name="thickness">The thickness of the border pillars.</param>
        private void GenerateBorderGeometry(List<Vector3> verts, List<int> indices, float thickness)
        {
            const float w = VoxelData.ChunkWidth;
            const float h = VoxelData.ChunkHeight;

            // 1. Vertical Pillars (Corners)
            // Centered at the corner, extending up.
            AddBox(verts, indices, new Vector3(0, h / 2f, 0), new Vector3(thickness, h, thickness)); // Back-Left
            AddBox(verts, indices, new Vector3(w, h / 2f, 0), new Vector3(thickness, h, thickness)); // Back-Right
            AddBox(verts, indices, new Vector3(w, h / 2f, w), new Vector3(thickness, h, thickness)); // Front-Right
            AddBox(verts, indices, new Vector3(0, h / 2f, w), new Vector3(thickness, h, thickness)); // Front-Left

            // 2. Bottom Frame (Horizontal bars between pillars)
            AddBox(verts, indices, new Vector3(w / 2f, 0, 0), new Vector3(w, thickness, thickness)); // Back Edge
            AddBox(verts, indices, new Vector3(w / 2f, 0, w), new Vector3(w, thickness, thickness)); // Front Edge
            AddBox(verts, indices, new Vector3(0, 0, w / 2f), new Vector3(thickness, thickness, w)); // Left Edge
            AddBox(verts, indices, new Vector3(w, 0, w / 2f), new Vector3(thickness, thickness, w)); // Right Edge

            // 3. Top Frame
            AddBox(verts, indices, new Vector3(w / 2f, h, 0), new Vector3(w, thickness, thickness));
            AddBox(verts, indices, new Vector3(w / 2f, h, w), new Vector3(w, thickness, thickness));
            AddBox(verts, indices, new Vector3(0, h, w / 2f), new Vector3(thickness, thickness, w));
            AddBox(verts, indices, new Vector3(w, h, w / 2f), new Vector3(thickness, thickness, w));
        }

        /// <summary>
        /// Generates horizontal frames indicating where ChunkSections start and end using geometry.
        /// </summary>
        /// <param name="verts">The list of vertices to append to.</param>
        /// <param name="indices">The list of triangle indices to append to.</param>
        /// <param name="thickness">The thickness of the section frame lines.</param>
        private void GenerateSectionGeometry(List<Vector3> verts, List<int> indices, float thickness)
        {
            const float w = VoxelData.ChunkWidth;
            const float h = VoxelData.ChunkHeight;
            const float sectionSize = ChunkMath.SECTION_SIZE;

            // Iterate vertically sections
            // We skip 0 and h (128) because the red border already covers those areas.
            for (float y = sectionSize; y < h; y += sectionSize)
            {
                // Draw horizontal frame at height Y
                AddBox(verts, indices, new Vector3(w / 2f, y, 0), new Vector3(w, thickness, thickness)); // Back
                AddBox(verts, indices, new Vector3(w / 2f, y, w), new Vector3(w, thickness, thickness)); // Front
                AddBox(verts, indices, new Vector3(0, y, w / 2f), new Vector3(thickness, thickness, w)); // Left
                AddBox(verts, indices, new Vector3(w, y, w / 2f), new Vector3(thickness, thickness, w)); // Right
            }
        }

        /// <summary>
        /// Adds a 3D Box (Cube) to the vertex/index lists.
        /// </summary>
        /// <param name="verts">The list of vertices to append to.</param>
        /// <param name="indices">The list of triangle indices to append to.</param>
        /// <param name="center">Center position of the box relative to the chunk origin.</param>
        /// <param name="size">Total size (width, height, depth) of the box.</param>
        private void AddBox(List<Vector3> verts, List<int> indices, Vector3 center, Vector3 size)
        {
            int startIndex = verts.Count;
            Vector3 ext = size * 0.5f; // Extents

            // Generate 8 corners
            Vector3[] p = new Vector3[8];
            p[0] = center + new Vector3(-ext.x, -ext.y, -ext.z); // 0: - - -
            p[1] = center + new Vector3(ext.x, -ext.y, -ext.z); // 1: + - -
            p[2] = center + new Vector3(ext.x, -ext.y, ext.z); // 2: + - +
            p[3] = center + new Vector3(-ext.x, -ext.y, ext.z); // 3: - - +
            p[4] = center + new Vector3(-ext.x, ext.y, -ext.z); // 4: - + -
            p[5] = center + new Vector3(ext.x, ext.y, -ext.z); // 5: + + -
            p[6] = center + new Vector3(ext.x, ext.y, ext.z); // 6: + + +
            p[7] = center + new Vector3(-ext.x, ext.y, ext.z); // 7: - + +

            verts.AddRange(p);

            // Add triangles (12 tris, 36 indices) for the 6 faces
            AddQuad(indices, startIndex, 3, 2, 1, 0); // Bottom
            AddQuad(indices, startIndex, 4, 5, 6, 7); // Top
            AddQuad(indices, startIndex, 0, 1, 5, 4); // Front
            AddQuad(indices, startIndex, 2, 3, 7, 6); // Back
            AddQuad(indices, startIndex, 3, 0, 4, 7); // Left
            AddQuad(indices, startIndex, 1, 2, 6, 5); // Right
        }

        /// <summary>
        /// Adds two triangles representing a quad to the indices list.
        /// </summary>
        /// <param name="indices">The list of indices to append to.</param>
        /// <param name="offset">The vertex index offset to apply to local indices.</param>
        /// <param name="a">The first local vertex index.</param>
        /// <param name="b">The second local vertex index.</param>
        /// <param name="c">The third local vertex index.</param>
        /// <param name="d">The fourth local vertex index.</param>
        private static void AddQuad(List<int> indices, int offset, int a, int b, int c, int d)
        {
            // Triangle 1
            indices.Add(offset + a);
            indices.Add(offset + b);
            indices.Add(offset + c);

            // Triangle 2
            indices.Add(offset + c);
            indices.Add(offset + d);
            indices.Add(offset + a);
        }

        #endregion

        #region Line Generators (Thin Lines)

        /// <summary>
        /// Generates the internal grid using single-pixel lines.
        /// </summary>
        /// <param name="verts">The list of vertices to append to.</param>
        /// <param name="indices">The list of line indices to append to.</param>
        private void GenerateGridLines(List<Vector3> verts, List<int> indices)
        {
            if (gridInterval <= 0) return;
            const float w = VoxelData.ChunkWidth;
            const float h = VoxelData.ChunkHeight;

            // X-Axis Lines
            for (float i = gridInterval; i < h; i += gridInterval)
            {
                if (i % ChunkMath.SECTION_SIZE == 0) continue; // Skip overlap with section frames
                AddLine(new Vector3(0, i, 0), new Vector3(w, i, 0));
                AddLine(new Vector3(0, i, w), new Vector3(w, i, w));
                AddLine(new Vector3(0, i, 0), new Vector3(0, i, w));
                AddLine(new Vector3(w, i, 0), new Vector3(w, i, w));
            }

            // Y-Axis Lines
            for (float i = gridInterval; i < w; i += gridInterval)
            {
                AddLine(new Vector3(i, 0, 0), new Vector3(i, h, 0));
                AddLine(new Vector3(i, 0, w), new Vector3(i, h, w));
                AddLine(new Vector3(0, 0, i), new Vector3(0, h, i));
                AddLine(new Vector3(w, 0, i), new Vector3(w, h, i));
            }

            // Z-Axis Lines
            for (float i = gridInterval; i < w; i += gridInterval)
            {
                AddLine(new Vector3(0, 0, i), new Vector3(w, 0, i));
                AddLine(new Vector3(0, h, i), new Vector3(w, h, i));
                AddLine(new Vector3(i, 0, 0), new Vector3(i, 0, w));
                AddLine(new Vector3(i, h, 0), new Vector3(i, h, w));
            }

            return;

            // Helper for adding a line segment
            void AddLine(Vector3 start, Vector3 end)
            {
                indices.Add(verts.Count);
                verts.Add(start);
                indices.Add(verts.Count);
                verts.Add(end);
            }
        }

        /// <summary>
        /// Generates optional cross lines (X) inside sections using single-pixel lines.
        /// </summary>
        /// <param name="verts">The list of vertices to append to.</param>
        /// <param name="indices">The list of line indices to append to.</param>
        private void GenerateSectionCrossLines(List<Vector3> verts, List<int> indices)
        {
            if (!renderSectionsInternalCross) return;

            const float w = VoxelData.ChunkWidth;
            const float h = VoxelData.ChunkHeight;
            const float sectionSize = ChunkMath.SECTION_SIZE;

            for (float y = 0; y <= h; y += sectionSize)
            {
                AddLine(new Vector3(0, y, 0), new Vector3(w, y, w)); // Diagonal 1
                AddLine(new Vector3(w, y, 0), new Vector3(0, y, w)); // Diagonal 2
            }

            return;

            void AddLine(Vector3 start, Vector3 end)
            {
                indices.Add(verts.Count);
                verts.Add(start);
                indices.Add(verts.Count);
                verts.Add(end);
            }
        }

        #endregion
    }
}
