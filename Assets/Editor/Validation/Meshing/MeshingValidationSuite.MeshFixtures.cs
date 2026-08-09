using System.Collections.Generic;
using System.Text;
using Data;
using Editor.Validation.Meshing.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Meshing
{
    /// <summary>
    /// SS-0 fixture-integrity baselines: every custom-mesh fixture's authored volume agrees with the
    /// geometry actually flattened for it, and the post fixture really is a shape the corner-blended
    /// occlusion model cannot express.
    /// </summary>
    public static partial class MeshingValidationSuite
    {
        /// <summary>Registers the SS-0 fixture baselines (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddMeshFixtureBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B50: custom-mesh fixtures' authored bounds match their flattened geometry (SS-0 / finding F13)",
                B50_CustomMeshFixtureIntegrity));
        }

        /// <summary>
        /// B50 — the property every later <c>SS-*</c> baseline is built on. For each custom-mesh palette
        /// block, the authored <see cref="BlockTypeJobData.BoundsMin"/>/<see cref="BlockTypeJobData.BoundsMax"/>
        /// must equal the extents of the vertices actually flattened for its mesh.
        /// <para>
        /// <b>This is finding F13 made checkable:</b> the meshing palette once carried a slab in geometry
        /// and a full cube in shape, and nothing could see it until VO-5 first asked a shape question.
        /// SS-0 removed the second source of truth (both sides now read one
        /// <see cref="BlockCollisionBounds"/> value), so this guards against a future hand-edit
        /// reintroducing one — it is not the primary defense.
        /// </para>
        /// <para>
        /// <b>A second leg was removed by SS-3a.</b> It swept the mesher's occlusion query across a cell
        /// and asserted the post fixture's coverage was <i>non-monotonic</i> — the evidence for finding
        /// S9, that a coverage fraction is not a distance field at all (<c>GetRegionCoverage</c>
        /// normalized by the query region's own volume, so a region clipped at a cell edge shrank and
        /// inflated the fraction into a rise where distance said fall). That finding is what justified
        /// abandoning coverage; once nothing shaded through it the function was deleted, and a leg
        /// re-proving a property of deleted code every run guarded nothing. The finding is recorded in
        /// <c>SILHOUETTE_CONTACT_SHADOWS.md</c> §S9. The post fixture's <i>shape</i> is still asserted,
        /// on the live path, by Occlusion <c>B6</c>.
        /// </para>
        /// </summary>
        /// <returns>True when every custom-mesh fixture's bounds match its geometry.</returns>
        private static bool B50_CustomMeshFixtureIntegrity()
        {
            return B50_BoundsMatchGeometry();
        }

        /// <summary>
        /// Leg 1 — every custom-mesh block's authored volume equals the extents of its flattened mesh.
        /// </summary>
        /// <returns>True when every custom-mesh fixture agrees with its geometry.</returns>
        private static bool B50_BoundsMatchGeometry()
        {
            TestCustomMeshLibrary.Build(Allocator.Temp,
                out NativeArray<CustomMeshData> meshes, out NativeArray<CustomFaceData> faces,
                out NativeArray<CustomVertData> verts, out NativeArray<int> tris);
            NativeArray<BlockTypeJobData> palette =
                TestMeshBlockPalette.CreateJobDataNativeArray(Allocator.Temp);

            StringBuilder failures = new StringBuilder();
            int checkedBlocks = 0;

            for (ushort id = 0; id < TestMeshBlockPalette.Count; id++)
            {
                BlockTypeJobData block = palette[id];
                if (block.RenderShape != RenderShape.CustomMesh || block.CustomMeshIndex < 0) continue;

                checkedBlocks++;
                MeshVertexExtents(in meshes, in faces, in verts, block.CustomMeshIndex,
                    out float3 geomMin, out float3 geomMax);

                if (math.all(math.abs(geomMin - block.BoundsMin) <= FIXTURE_BOUNDS_EPSILON)
                    && math.all(math.abs(geomMax - block.BoundsMax) <= FIXTURE_BOUNDS_EPSILON))
                {
                    continue;
                }

                failures.AppendFormat(
                    "    block {0} (mesh {1}): authored [{2} .. {3}], geometry [{4} .. {5}]\n",
                    id, block.CustomMeshIndex, block.BoundsMin, block.BoundsMax, geomMin, geomMax);
            }

            meshes.Dispose();
            faces.Dispose();
            verts.Dispose();
            tris.Dispose();
            palette.Dispose();

            // Without this the leg passes vacuously the day someone drops the custom-mesh fixtures.
            if (!MeshAssert.IsTrue("B50 the fixture sweep found custom-mesh blocks to check",
                    checkedBlocks >= 3,
                    $"Only {checkedBlocks} custom-mesh blocks were found in the palette. This leg asserts "
                    + "nothing if the fixtures it sweeps are gone."))
            {
                return false;
            }

            return MeshAssert.IsTrue("B50 custom-mesh fixtures' bounds match their geometry",
                failures.Length == 0,
                "A fixture's authored collisionBounds disagrees with the mesh flattened for it, so it is "
                + "one shape to the mesher and another to every shape query. That is finding F13, and it "
                + "stayed invisible for a whole arc last time.\n" + failures);
        }


        /// <summary>Reads the min/max corner of every vertex belonging to one flattened mesh.</summary>
        /// <param name="meshes">Per-mesh face ranges.</param>
        /// <param name="faces">Per-face vert ranges.</param>
        /// <param name="verts">Flattened vertex positions.</param>
        /// <param name="meshIndex">Which mesh to measure.</param>
        /// <param name="min">The mesh's minimum corner.</param>
        /// <param name="max">The mesh's maximum corner.</param>
        private static void MeshVertexExtents(in NativeArray<CustomMeshData> meshes,
            in NativeArray<CustomFaceData> faces, in NativeArray<CustomVertData> verts, int meshIndex,
            out float3 min, out float3 max)
        {
            min = new float3(float.MaxValue);
            max = new float3(float.MinValue);

            CustomMeshData mesh = meshes[meshIndex];
            for (int f = 0; f < mesh.FaceCount; f++)
            {
                CustomFaceData face = faces[mesh.FaceStartIndex + f];
                for (int v = 0; v < face.VertCount; v++)
                {
                    float3 p = verts[face.VertStartIndex + v].Position;
                    min = math.min(min, p);
                    max = math.max(max, p);
                }
            }
        }


        /// <summary>Slack for comparing authored bounds against flattened vertex extents.</summary>
        private const float FIXTURE_BOUNDS_EPSILON = 1e-5f;
    }
}
