using System.Collections.Generic;
using Data;
using Unity.Collections;
using UnityEngine;

namespace Editor.Validation.Meshing.Framework
{
    /// <summary>
    /// Synthetic custom-mesh fixtures for the meshing suite, flattened into exactly the four parallel
    /// arrays <see cref="Jobs.MeshGenerationJob"/> consumes (<see cref="CustomMeshData"/> /
    /// <see cref="CustomFaceData"/> / <see cref="CustomVertData"/> / triangle indices).
    /// <para>
    /// Mirrors the production flattening in <c>JobDataManagerFactory.Create</c> step 2 — mesh → faces →
    /// verts/tris with running start indices — but authors the geometry in code instead of reading a
    /// <c>VoxelMeshData</c> ScriptableObject, so the fixture is deterministic under asset edits (the same
    /// reason <see cref="TestMeshBlockPalette"/> uses test-local block IDs).
    /// </para>
    /// <para>
    /// <b>Face order is load-bearing.</b> The schema-aware custom-mesh path indexes
    /// <c>BurstVoxelData.FaceChecks[p]</c> with the face's own array position, so face <c>p</c> MUST be the
    /// canonical direction <c>p</c>: 0 = Back (−Z), 1 = Front (+Z), 2 = Top (+Y), 3 = Bottom (−Y),
    /// 4 = Left (−X), 5 = Right (+X).
    /// </para>
    /// </summary>
    public static class TestCustomMeshLibrary
    {
        /// <summary>Index of the half-slab mesh within the flattened arrays (the only fixture mesh today).</summary>
        public const int HalfSlabMeshIndex = 0;

        /// <summary>
        /// The Y coordinate of the half slab's large horizontal face. This is the <b>mid-plane</b> face —
        /// it does not lie on a block boundary, which is exactly what <c>MESHING_BUGS.md</c> Bug M01 is about.
        /// </summary>
        public const float HalfSlabTopY = 0.5f;

        /// <summary>Number of faces every fixture mesh defines (all six canonical directions).</summary>
        public const int FaceCount = 6;

        /// <summary>Quad triangle indices, face-local — matches the standard cube's non-flipped winding.</summary>
        private static readonly int[] s_quadTris = { 0, 1, 2, 2, 1, 3 };

        /// <summary>Quad UVs in the fixture's vertex order (BL, TL, BR, TR), mirroring <c>VoxelData.VoxelUvs</c>.</summary>
        private static readonly Vector2[] s_quadUvs =
        {
            new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 0f), new Vector2(1f, 1f),
        };

        /// <summary>
        /// Builds the flattened custom-mesh arrays for every fixture mesh, as persistent
        /// <see cref="NativeArray{T}"/>s ready to assign to the job. Caller owns disposal.
        /// </summary>
        /// <param name="allocator">Allocator for the returned arrays.</param>
        /// <param name="meshes">Per-mesh face ranges.</param>
        /// <param name="faces">Per-face vert/tri ranges.</param>
        /// <param name="verts">Flattened vertex positions + UVs.</param>
        /// <param name="tris">Flattened face-local triangle indices.</param>
        public static void Build(Allocator allocator,
            out NativeArray<CustomMeshData> meshes,
            out NativeArray<CustomFaceData> faces,
            out NativeArray<CustomVertData> verts,
            out NativeArray<int> tris)
        {
            List<CustomMeshData> meshList = new List<CustomMeshData>();
            List<CustomFaceData> faceList = new List<CustomFaceData>();
            List<CustomVertData> vertList = new List<CustomVertData>();
            List<int> triList = new List<int>();

            AppendBoxMesh(meshList, faceList, vertList, triList, HalfSlabTopY);

            meshes = new NativeArray<CustomMeshData>(meshList.ToArray(), allocator);
            faces = new NativeArray<CustomFaceData>(faceList.ToArray(), allocator);
            verts = new NativeArray<CustomVertData>(vertList.ToArray(), allocator);
            tris = new NativeArray<int>(triList.ToArray(), allocator);
        }

        /// <summary>
        /// Appends one axis-aligned box mesh spanning the full X/Z cell and <c>y ∈ [0, topY]</c>, emitting all
        /// six canonical faces in order. A <paramref name="topY"/> of 1 reproduces a standard cube; 0.5 gives
        /// the half slab whose Top face lands on the block's mid-plane.
        /// </summary>
        /// <param name="meshList">Mesh range list to append to.</param>
        /// <param name="faceList">Face range list to append to.</param>
        /// <param name="vertList">Vertex list to append to.</param>
        /// <param name="triList">Triangle index list to append to.</param>
        /// <param name="topY">Height of the box's top face in block-local space.</param>
        private static void AppendBoxMesh(List<CustomMeshData> meshList, List<CustomFaceData> faceList,
            List<CustomVertData> vertList, List<int> triList, float topY)
        {
            meshList.Add(new CustomMeshData { FaceStartIndex = faceList.Count, FaceCount = FaceCount });

            // Each face's four corners in the fixture's BL, TL, BR, TR order — the same per-face vertex
            // ordering the standard cube uses (VoxelData.VoxelTris), with the top edge pulled down to topY.
            AppendFace(faceList, vertList, triList, // 0: Back (-Z)
                new Vector3(0f, 0f, 0f), new Vector3(0f, topY, 0f), new Vector3(1f, 0f, 0f), new Vector3(1f, topY, 0f));
            AppendFace(faceList, vertList, triList, // 1: Front (+Z)
                new Vector3(1f, 0f, 1f), new Vector3(1f, topY, 1f), new Vector3(0f, 0f, 1f), new Vector3(0f, topY, 1f));
            AppendFace(faceList, vertList, triList, // 2: Top (+Y) — the mid-plane face when topY < 1
                new Vector3(0f, topY, 0f), new Vector3(0f, topY, 1f), new Vector3(1f, topY, 0f), new Vector3(1f, topY, 1f));
            AppendFace(faceList, vertList, triList, // 3: Bottom (-Y)
                new Vector3(1f, 0f, 0f), new Vector3(1f, 0f, 1f), new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 1f));
            AppendFace(faceList, vertList, triList, // 4: Left (-X)
                new Vector3(0f, 0f, 1f), new Vector3(0f, topY, 1f), new Vector3(0f, 0f, 0f), new Vector3(0f, topY, 0f));
            AppendFace(faceList, vertList, triList, // 5: Right (+X)
                new Vector3(1f, 0f, 0f), new Vector3(1f, topY, 0f), new Vector3(1f, 0f, 1f), new Vector3(1f, topY, 1f));
        }

        /// <summary>Appends one quad face (4 verts, 2 triangles) and its range record.</summary>
        /// <param name="faceList">Face range list to append to.</param>
        /// <param name="vertList">Vertex list to append to.</param>
        /// <param name="triList">Triangle index list to append to.</param>
        /// <param name="bl">Bottom-left corner in block-local space.</param>
        /// <param name="tl">Top-left corner.</param>
        /// <param name="br">Bottom-right corner.</param>
        /// <param name="tr">Top-right corner.</param>
        private static void AppendFace(List<CustomFaceData> faceList, List<CustomVertData> vertList,
            List<int> triList, Vector3 bl, Vector3 tl, Vector3 br, Vector3 tr)
        {
            faceList.Add(new CustomFaceData
            {
                VertStartIndex = vertList.Count,
                VertCount = 4,
                TriStartIndex = triList.Count,
                TriCount = s_quadTris.Length,
            });

            vertList.Add(new CustomVertData { Position = bl, UV = s_quadUvs[0] });
            vertList.Add(new CustomVertData { Position = tl, UV = s_quadUvs[1] });
            vertList.Add(new CustomVertData { Position = br, UV = s_quadUvs[2] });
            vertList.Add(new CustomVertData { Position = tr, UV = s_quadUvs[3] });

            triList.AddRange(s_quadTris);
        }
    }
}
