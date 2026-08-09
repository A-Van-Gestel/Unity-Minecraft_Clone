using System.Collections.Generic;
using Data;
using Unity.Collections;
using Unity.Mathematics;
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
        /// <summary>Index of the half-slab mesh within the flattened arrays.</summary>
        public const int HalfSlabMeshIndex = 0;

        /// <summary>Index of the post mesh within the flattened arrays (SS-0).</summary>
        public const int PostMeshIndex = 1;

        /// <summary>
        /// The Y coordinate of the half slab's large horizontal face. This is the <b>mid-plane</b> face —
        /// it does not lie on a block boundary, which is exactly what <c>MESHING_BUGS.md</c> Bug M01 is about.
        /// </summary>
        public const float HalfSlabTopY = 0.5f;

        /// <summary>
        /// The half slab's volume — the <b>single</b> value both its geometry and its authored
        /// <c>collisionBounds</c> are built from, so the two cannot disagree.
        /// <para>
        /// Finding <b>F13</b> was a fixture whose bounds and geometry diverged silently for an entire
        /// arc, invisible until some phase first asked a shape question. Sharing one value makes that
        /// divergence unrepresentable rather than merely asserted against; baseline <b>B50</b> still
        /// checks it end-to-end, because a future hand-edit could reintroduce two sources.
        /// </para>
        /// </summary>
        public static readonly BlockCollisionBounds HalfSlabBounds = BlockCollisionBounds.BottomHalfSlab;

        /// <summary>
        /// SS-0: a fence-post volume — a quarter-cell square column standing on the cell floor, spanning
        /// the full cell height and touching neither side wall.
        /// <para>
        /// It exists because <b>every other meshing fixture is a full-width box</b> (the palette's only
        /// custom mesh was the half slab, and its builder was parametric on height alone), so the suite
        /// had no shape whose occlusion the corner-blended model cannot express. Its four side faces are
        /// also <i>interior</i> to their own cell, which the half slab only exercises on one face.
        /// </para>
        /// </summary>
        public static readonly BlockCollisionBounds PostBounds = new BlockCollisionBounds
        {
            mode = CollisionBoundsMode.CustomAABB,
            min = new Vector3(0.375f, 0f, 0.375f),
            max = new Vector3(0.625f, 1f, 0.625f),
        };

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

            // Order is load-bearing: the *MeshIndex constants above are positions in this list.
            AppendBoxMesh(meshList, faceList, vertList, triList, HalfSlabBounds.min, HalfSlabBounds.max);
            AppendBoxMesh(meshList, faceList, vertList, triList, PostBounds.min, PostBounds.max);

            meshes = new NativeArray<CustomMeshData>(meshList.ToArray(), allocator);
            faces = new NativeArray<CustomFaceData>(faceList.ToArray(), allocator);
            verts = new NativeArray<CustomVertData>(vertList.ToArray(), allocator);
            tris = new NativeArray<int>(triList.ToArray(), allocator);
        }

        /// <summary>
        /// Appends one axis-aligned box mesh spanning <c>[min, max]</c> in block-local space, emitting all
        /// six canonical faces in order. <c>(0,0,0)→(1,1,1)</c> reproduces a standard cube;
        /// <c>(0,0,0)→(1,0.5,1)</c> gives the half slab whose Top face lands on the block's mid-plane.
        /// <para>
        /// SS-0 widened this from a height-only parameter. A face lying off the cell wall is the
        /// interesting case — the sample-cell, culling and octant confusions of Bugs M01/M02/M03 all live
        /// there — and until now only the slab's Top face could be one.
        /// </para>
        /// </summary>
        /// <param name="meshList">Mesh range list to append to.</param>
        /// <param name="faceList">Face range list to append to.</param>
        /// <param name="vertList">Vertex list to append to.</param>
        /// <param name="triList">Triangle index list to append to.</param>
        /// <param name="min">Minimum corner of the box in block-local space.</param>
        /// <param name="max">Maximum corner of the box in block-local space.</param>
        private static void AppendBoxMesh(List<CustomMeshData> meshList, List<CustomFaceData> faceList,
            List<CustomVertData> vertList, List<int> triList, Vector3 min, Vector3 max)
        {
            meshList.Add(new CustomMeshData { FaceStartIndex = faceList.Count, FaceCount = FaceCount });

            // Each face's four corners in the fixture's BL, TL, BR, TR order — the same per-face vertex
            // ordering the standard cube uses (VoxelData.VoxelTris), stretched to the box's extents.
            AppendFace(faceList, vertList, triList, // 0: Back (-Z)
                new Vector3(min.x, min.y, min.z), new Vector3(min.x, max.y, min.z), new Vector3(max.x, min.y, min.z), new Vector3(max.x, max.y, min.z));
            AppendFace(faceList, vertList, triList, // 1: Front (+Z)
                new Vector3(max.x, min.y, max.z), new Vector3(max.x, max.y, max.z), new Vector3(min.x, min.y, max.z), new Vector3(min.x, max.y, max.z));
            AppendFace(faceList, vertList, triList, // 2: Top (+Y) — the mid-plane face for the half slab
                new Vector3(min.x, max.y, min.z), new Vector3(min.x, max.y, max.z), new Vector3(max.x, max.y, min.z), new Vector3(max.x, max.y, max.z));
            AppendFace(faceList, vertList, triList, // 3: Bottom (-Y)
                new Vector3(max.x, min.y, min.z), new Vector3(max.x, min.y, max.z), new Vector3(min.x, min.y, min.z), new Vector3(min.x, min.y, max.z));
            AppendFace(faceList, vertList, triList, // 4: Left (-X)
                new Vector3(min.x, min.y, max.z), new Vector3(min.x, max.y, max.z), new Vector3(min.x, min.y, min.z), new Vector3(min.x, max.y, min.z));
            AppendFace(faceList, vertList, triList, // 5: Right (+X)
                new Vector3(max.x, min.y, min.z), new Vector3(max.x, max.y, min.z), new Vector3(max.x, min.y, max.z), new Vector3(max.x, max.y, max.z));
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
                // VO-6: mirrors JobDataManagerFactory.FaceCentroid — the mean of the face's verts. Left
                // unset, every face would report a centroid at the cell origin and the mesher would
                // resolve the wrong sampling cell for all of them.
                Centroid = ((float3)bl + (float3)tl + (float3)br + (float3)tr) / 4f,
            });

            vertList.Add(new CustomVertData { Position = bl, UV = s_quadUvs[0] });
            vertList.Add(new CustomVertData { Position = tl, UV = s_quadUvs[1] });
            vertList.Add(new CustomVertData { Position = br, UV = s_quadUvs[2] });
            vertList.Add(new CustomVertData { Position = tr, UV = s_quadUvs[3] });

            triList.AddRange(s_quadTris);
        }
    }
}
