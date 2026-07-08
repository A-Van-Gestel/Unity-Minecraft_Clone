using System.Collections.Generic;
using Data;
using Editor.Validation.Meshing.Framework;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Meshing
{
    /// <summary>
    /// Cross-chunk border-face-culling baselines (findings <b>MH-10</b> consumption + <b>MH-11</b>
    /// fill-faithful — see
    /// Documentation/Architecture/Testing Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md §3 Phase 5).
    /// Until now the suite left every neighbor voxel map empty (<see cref="MeshingTestWorld"/> placed
    /// blocks in the interior), so the meshing job's border-face culling — the meshing-side consumer of
    /// all neighbor data — had <b>zero</b> coverage. That is the seam the halo-padded substrate
    /// (LI-1 → P-2) and TG-4 Phase 4 rewrite, so it is a substrate prerequisite, not an optional extra.
    /// <list type="bullet">
    /// <item><b>B18/B19/B20</b> (MH-10) — drive a real `NeighborRight` (+X) map and assert the job's
    /// <c>ShouldDrawFace</c> verdict via a face-count delta: air neighbor → drawn, opaque neighbor →
    /// culled (one face fewer), transparent (renderNeighborFaces) neighbor → drawn.</item>
    /// <item><b>B21</b> (MH-11) — repeats B19's occlusion but builds the neighbor map through the
    /// <b>production</b> <c>ChunkData.FillJobVoxelMap</c> path (the exact fill a slab/halo substrate
    /// rewrites), so a border-plane under-copy/mis-index flips it red.</item>
    /// </list>
    /// The expected face counts are derived from the <c>ShouldDrawFace</c> contract by hand (NOT by calling
    /// the job's predicate), guarded by <see cref="AssertBorderCullingPaletteAssumptions"/> so a palette
    /// edit fails loudly here instead of silently invalidating the magic constants — the A4-avoidance
    /// discipline B3 established. Self-registers via the <see cref="AddCrossChunkBaselineScenarios"/> hook
    /// called from <c>AddBaselineScenarios</c>.
    /// </summary>
    public static partial class MeshingValidationSuite
    {
        // A single isolated opaque cube on this chunk's +X border (local x = 15); its +X face reads the
        // across-seam cell NeighborRight[(0, y, z)] via the job's GetVoxelStateFromLocalPos wrap.
        private const int BORDER_CUBE_X = 15;
        private const int BORDER_CUBE_Y = 8;
        private const int BORDER_CUBE_Z = 8;
        private const int NEIGHBOR_CELL_X = 0; // the +X border reads neighbor-local x = 0

        // Standard cube = 4 vertices per face. An isolated border cube exposes all 6 faces (its 5
        // in-chunk neighbors are air) UNLESS the +X neighbor occludes it, in which case 5 faces survive.
        private const int VERTS_ALL_SIX_FACES = 24; // +X neighbor non-occluding (air / transparent)
        private const int VERTS_PLUS_X_CULLED = 20; // +X neighbor opaque-solid → that face culled

        /// <summary>Registers the cross-chunk border-culling baselines (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddCrossChunkBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("B18: +X border face is drawn when the populated neighbor cell is air (MH-10 consumption)", B18_BorderFaceDrawnWhenNeighborAir));
            scenarios.Add(new Scenario("B19: +X border face is culled when the neighbor cell is opaque-solid (MH-10 consumption)", B19_BorderFaceCulledWhenNeighborSolid));
            scenarios.Add(new Scenario("B20: a transparent (renderNeighborFaces) neighbor does NOT cull the +X border face (MH-10 consumption)", B20_BorderFaceDrawnWhenNeighborTransparent));
            scenarios.Add(new Scenario("B21: border culling holds when the neighbor map is built via the production ChunkData.FillJobVoxelMap path (MH-11 fill-faithful)", B21_BorderCullingViaProductionFill));
        }

        /// <summary>
        /// Pins the palette properties the derived face counts depend on, so a <see cref="TestMeshBlockPalette"/>
        /// edit fails loudly here rather than silently invalidating B18–B21's magic constants (the B3 guard
        /// pattern). The counts follow directly from <c>MeshGenerationJob.ShouldDrawFace</c>: an opaque
        /// non-render-neighbor block occludes; a non-solid or renderNeighborFaces block does not.
        /// </summary>
        /// <param name="label">Scenario label for the assertion message.</param>
        /// <returns>True when every assumed palette property holds.</returns>
        private static bool AssertBorderCullingPaletteAssumptions(string label)
        {
            BlockTypeJobData[] p = TestMeshBlockPalette.CreateJobDataArray();
            BlockTypeJobData solid = p[TestMeshBlockPalette.SolidOpaque];
            BlockTypeJobData trans = p[TestMeshBlockPalette.TransparentCube];
            bool ok = solid.IsSolid && solid.IsOpaque && !solid.RenderNeighborFaces
                      && trans.IsSolid && trans.RenderNeighborFaces
                      && !p[TestMeshBlockPalette.Air].IsSolid;
            return MeshAssert.IsTrue($"{label} palette assumptions", ok,
                "SolidOpaque must be solid+opaque+non-render-neighbor, TransparentCube solid+render-neighbor, Air non-solid for the derived face counts to hold");
        }

        /// <summary>
        /// B18 (MH-10) — an opaque cube on the +X border with the across-seam neighbor cell populated as
        /// <b>air</b> draws all six faces (24 verts). This is the <b>positive-control reference count</b> for
        /// B19's culled-face delta, and pins that an air (non-occluding) neighbor does <b>not</b> cull (an
        /// inverted predicate where air culls would drop it to 20 and red this).
        /// <para>
        /// <b>Note:</b> B18 does <i>not</i> by itself prove the job consults <c>NeighborRight</c> — an air
        /// neighbor (0), the all-zero map, and the legacy empty-array (null → draw) path all yield 24, so this
        /// count is the same whether or not the map is read. <b>B19/B21</b> (opaque neighbor → 20) are the
        /// baselines that actually exercise the consumption path; the prove-red severing the neighbor reds
        /// only B19/B21, never B18.
        /// </para>
        /// </summary>
        private static bool B18_BorderFaceDrawnWhenNeighborAir()
        {
            bool passed = AssertBorderCullingPaletteAssumptions("B18");
            using MeshingTestWorld world = new MeshingTestWorld();
            world.SetBlock(BORDER_CUBE_X, BORDER_CUBE_Y, BORDER_CUBE_Z, TestMeshBlockPalette.SolidOpaque);
            world.SetNeighborRightBlock(NEIGHBOR_CELL_X, BORDER_CUBE_Y, BORDER_CUBE_Z, TestMeshBlockPalette.Air);
            MeshDataJobOutput o = world.Run();

            passed &= MeshAssert.VertexCount("B18 all six border-cube faces drawn", o, VERTS_ALL_SIX_FACES);
            passed &= MeshAssert.StructuralInvariants("B18 structural", o);
            return passed;
        }

        /// <summary>
        /// B19 (MH-10) — the core culling assertion: the same border cube with an <b>opaque-solid</b> neighbor
        /// across the +X seam emits exactly one face fewer than B18 (the +X face is culled). This is the
        /// meshing-side consumer of cross-chunk neighbor data that had no coverage before MH-10.
        /// </summary>
        private static bool B19_BorderFaceCulledWhenNeighborSolid()
        {
            bool passed = AssertBorderCullingPaletteAssumptions("B19");
            using MeshingTestWorld world = new MeshingTestWorld();
            world.SetBlock(BORDER_CUBE_X, BORDER_CUBE_Y, BORDER_CUBE_Z, TestMeshBlockPalette.SolidOpaque);
            world.SetNeighborRightBlock(NEIGHBOR_CELL_X, BORDER_CUBE_Y, BORDER_CUBE_Z, TestMeshBlockPalette.SolidOpaque);
            MeshDataJobOutput o = world.Run();

            passed &= MeshAssert.VertexCount("B19 +X border face culled by opaque neighbor", o, VERTS_PLUS_X_CULLED);
            passed &= MeshAssert.StructuralInvariants("B19 structural", o);
            return passed;
        }

        /// <summary>
        /// B20 (MH-10) — pins the transparent-neighbor predicate: a <c>renderNeighborFaces</c> neighbor
        /// (glass/leaves-like) across the +X seam does <b>not</b> cull the border face, so all six survive
        /// (same count as B18's air case). Guards against a substrate change silently flipping the
        /// opaque-vs-transparent culling rule.
        /// </summary>
        private static bool B20_BorderFaceDrawnWhenNeighborTransparent()
        {
            bool passed = AssertBorderCullingPaletteAssumptions("B20");
            using MeshingTestWorld world = new MeshingTestWorld();
            world.SetBlock(BORDER_CUBE_X, BORDER_CUBE_Y, BORDER_CUBE_Z, TestMeshBlockPalette.SolidOpaque);
            world.SetNeighborRightBlock(NEIGHBOR_CELL_X, BORDER_CUBE_Y, BORDER_CUBE_Z, TestMeshBlockPalette.TransparentCube);
            MeshDataJobOutput o = world.Run();

            passed &= MeshAssert.VertexCount("B20 transparent neighbor does not cull the +X face", o, VERTS_ALL_SIX_FACES);
            passed &= MeshAssert.StructuralInvariants("B20 structural", o);
            return passed;
        }

        /// <summary>
        /// B21 (MH-11) — the fill-faithful guard: identical occlusion to B19, but the +X neighbor map is
        /// produced through the <b>production</b> <c>ChunkData.FillJobVoxelMap</c> path (the exact fill a
        /// border-slab/halo substrate — P-1/P-2 — rewrites) rather than a direct flat-array write. If that
        /// fill ever under-copies or mis-indexes the border plane, the +X face is no longer culled and this
        /// reds — the actual substrate guard the meshing suite was missing.
        /// </summary>
        private static bool B21_BorderCullingViaProductionFill()
        {
            bool passed = AssertBorderCullingPaletteAssumptions("B21");
            using MeshingTestWorld world = new MeshingTestWorld();
            world.SetBlock(BORDER_CUBE_X, BORDER_CUBE_Y, BORDER_CUBE_Z, TestMeshBlockPalette.SolidOpaque);
            world.SetNeighborRightBlockViaProductionFill(NEIGHBOR_CELL_X, BORDER_CUBE_Y, BORDER_CUBE_Z, TestMeshBlockPalette.SolidOpaque);
            MeshDataJobOutput o = world.Run();

            passed &= MeshAssert.VertexCount("B21 +X border face culled (production fill path)", o, VERTS_PLUS_X_CULLED);
            passed &= MeshAssert.StructuralInvariants("B21 structural", o);
            return passed;
        }
    }
}
