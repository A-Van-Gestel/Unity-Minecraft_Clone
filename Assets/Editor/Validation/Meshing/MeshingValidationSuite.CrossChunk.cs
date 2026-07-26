using System.Collections.Generic;
using Data;
using Editor.Validation.Meshing.Framework;
using Helpers;
using Jobs.Data;
using Unity.Collections;
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
    /// <item><b>B18/B19/B20</b> (MH-10) — drive a real `NeighborE` (+X) map and assert the job's
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
        // across-seam cell NeighborE[(0, y, z)] via the job's GetVoxelStateFromLocalPos wrap.
        private const int BORDER_CUBE_X = 15;
        private const int BORDER_CUBE_Y = 8;
        private const int BORDER_CUBE_Z = 8;
        private const int NEIGHBOR_CELL_X = 0; // the +X border reads neighbor-local x = 0

        // Standard cube = 4 vertices per face. An isolated border cube exposes all 6 faces (its 5
        // in-chunk neighbors are air) UNLESS the +X neighbor occludes it, in which case 5 faces survive.
        private const int VERTS_ALL_SIX_FACES = 24; // +X neighbor non-occluding (air / transparent)
        private const int VERTS_PLUS_X_CULLED = 20; // +X neighbor opaque-solid → that face culled

        // B37 (MH-12) permutation fixture: one isolated cube per cardinal border, each at its OWN Y so a
        // map delivered to the wrong slot probes a cell that direction never occupied. The tangential
        // coordinate is held at 8 (mid-face) and the four cubes are mutually non-adjacent, so each still
        // exposes all 6 faces before its outward one is culled.
        private const int PERM_TANGENT = 8;
        private const int PERM_WEST_Y = 4;
        private const int PERM_EAST_Y = 6;
        private const int PERM_NORTH_Y = 8;
        private const int PERM_SOUTH_Y = 10;
        private const int PERM_LOW_BORDER = 0; // local x/z = 0 → the -X / -Z faces
        private const int PERM_HIGH_BORDER = VoxelData.ChunkWidth - 1; // local x/z = 15 → the +X / +Z faces

        // Four isolated cubes, each with exactly its outward face culled by a correctly-routed neighbor.
        private const int VERTS_ALL_FOUR_SEAMS_CULLED = 4 * VERTS_PLUS_X_CULLED;

        /// <summary>Registers the cross-chunk border-culling baselines (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddCrossChunkBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("B18: +X border face is drawn when the populated neighbor cell is air (MH-10 consumption)", B18_BorderFaceDrawnWhenNeighborAir));
            scenarios.Add(new Scenario("B19: +X border face is culled when the neighbor cell is opaque-solid (MH-10 consumption)", B19_BorderFaceCulledWhenNeighborSolid));
            scenarios.Add(new Scenario("B20: a transparent (renderNeighborFaces) neighbor does NOT cull the +X border face (MH-10 consumption)", B20_BorderFaceDrawnWhenNeighborTransparent));
            scenarios.Add(new Scenario("B21: border culling holds when the neighbor map is built via the production ChunkData.FillJobVoxelMap path (MH-11 fill-faithful)", B21_BorderCullingViaProductionFill));
            scenarios.Add(new Scenario("B37: all four cardinal neighbor maps reach the slot they belong to — any permutation reds (MH-12 permutation guard)", B37_CardinalNeighborsAreNotPermuted));
            scenarios.Add(new Scenario("B38: all four diagonal neighbor maps reach their slot — fluid corner height drops only when the RIGHT diagonal is populated (MH-12 permutation guard)", B38_DiagonalNeighborsAreNotPermuted));
            scenarios.Add(new Scenario("B39: NeighborMapAssembler maps each compass direction to the right chunk offset, voxel AND light (MH-12; the acquire-site table feeding both meshing and lighting)", B39_NeighborMapAssemblerOffsetsAreCorrect));
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
        /// <b>Note:</b> B18 does <i>not</i> by itself prove the job consults <c>NeighborE</c> — an air
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
            world.SetNeighborEastBlock(NEIGHBOR_CELL_X, BORDER_CUBE_Y, BORDER_CUBE_Z, TestMeshBlockPalette.Air);
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
            world.SetNeighborEastBlock(NEIGHBOR_CELL_X, BORDER_CUBE_Y, BORDER_CUBE_Z, TestMeshBlockPalette.SolidOpaque);
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
            world.SetNeighborEastBlock(NEIGHBOR_CELL_X, BORDER_CUBE_Y, BORDER_CUBE_Z, TestMeshBlockPalette.TransparentCube);
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
            world.SetNeighborEastBlockViaProductionFill(NEIGHBOR_CELL_X, BORDER_CUBE_Y, BORDER_CUBE_Z, TestMeshBlockPalette.SolidOpaque);
            MeshDataJobOutput o = world.Run();

            passed &= MeshAssert.VertexCount("B21 +X border face culled (production fill path)", o, VERTS_PLUS_X_CULLED);
            passed &= MeshAssert.StructuralInvariants("B21 structural", o);
            return passed;
        }

        /// <summary>
        /// B37 (MH-12) — the permutation guard: every cardinal neighbor map must arrive in the slot it
        /// belongs to. B18–B21 populate only the +X map, so they red on any swap that displaces <i>+X</i>
        /// but stay green on a swap among the other three — the residual F6 risk, since the Back/Front/
        /// Left/Right ↔ compass mapping was a hand-written wiring table.
        /// <para>
        /// The fixture puts one isolated opaque cube on each cardinal border, <b>each at a different Y</b>,
        /// and the matching occluder in each neighbor map. Correctly routed, every cube loses exactly its
        /// outward face. Under any permutation, a probe reads a cell that direction never occupied — wrong
        /// Y <i>and</i> wrong border plane (+X reads x=0, -X reads x=15, +Z reads z=0, -Z reads z=15) — so
        /// the face is drawn instead and the count rises above <see cref="VERTS_ALL_FOUR_SEAMS_CULLED"/>.
        /// </para>
        /// </summary>
        private static bool B37_CardinalNeighborsAreNotPermuted()
        {
            bool passed = AssertBorderCullingPaletteAssumptions("B37");
            using MeshingTestWorld world = new MeshingTestWorld();

            // One isolated cube per border, each at its own Y so the four probes are mutually distinct.
            world.SetBlock(PERM_LOW_BORDER, PERM_WEST_Y, PERM_TANGENT, TestMeshBlockPalette.SolidOpaque);
            world.SetBlock(PERM_HIGH_BORDER, PERM_EAST_Y, PERM_TANGENT, TestMeshBlockPalette.SolidOpaque);
            world.SetBlock(PERM_TANGENT, PERM_NORTH_Y, PERM_HIGH_BORDER, TestMeshBlockPalette.SolidOpaque);
            world.SetBlock(PERM_TANGENT, PERM_SOUTH_Y, PERM_LOW_BORDER, TestMeshBlockPalette.SolidOpaque);

            // The across-seam occluder each of those faces must find. The neighbor-local coordinate is the
            // opposite border plane from the face that reads it.
            world.SetNeighborBlock(CardinalNeighbor.West, PERM_HIGH_BORDER, PERM_WEST_Y, PERM_TANGENT,
                TestMeshBlockPalette.SolidOpaque);
            world.SetNeighborBlock(CardinalNeighbor.East, PERM_LOW_BORDER, PERM_EAST_Y, PERM_TANGENT,
                TestMeshBlockPalette.SolidOpaque);
            world.SetNeighborBlock(CardinalNeighbor.North, PERM_TANGENT, PERM_NORTH_Y, PERM_LOW_BORDER,
                TestMeshBlockPalette.SolidOpaque);
            world.SetNeighborBlock(CardinalNeighbor.South, PERM_TANGENT, PERM_SOUTH_Y, PERM_HIGH_BORDER,
                TestMeshBlockPalette.SolidOpaque);

            MeshDataJobOutput o = world.Run();

            passed &= MeshAssert.VertexCount("B37 every cardinal seam face culled by its own neighbor", o,
                VERTS_ALL_FOUR_SEAMS_CULLED);
            passed &= MeshAssert.StructuralInvariants("B37 structural", o);
            return passed;
        }

        // B38 (MH-12, diagonal half): one water source on a chunk corner per leg. Diagonals never reach face
        // culling — they reach GetSmoothedCornerHeight, which admits the diagonal term ONLY when an adjacent
        // cardinal is also fluid (VoxelMeshHelper.cs:1148), so each leg also puts water in one cardinal map.
        private const int DIAG_Y = 8;
        private const byte FLUID_SOURCE_LEVEL = 0; // full height
        private const byte FLUID_LOW_LEVEL = 6; // shorter column, so admitting it lowers the corner average

        /// <summary>
        /// One leg of B38: meshes a corner water source twice — without and with the diagonal neighbor
        /// populated — and returns whether adding the diagonal strictly lowered the summed vertex height.
        /// <para>
        /// Only the one diagonal map under test is ever populated, so a job that routes this slot to any
        /// other diagonal reads a length-0 map, the diagonal term is refused, and the two runs come out
        /// identical — which is exactly the permutation signal. Comparing the two runs (rather than a
        /// hand-computed height) keeps the engine's averaging formula out of the oracle, the A4 discipline.
        /// </para>
        /// </summary>
        /// <param name="diagonal">The diagonal slot under test.</param>
        /// <param name="centerX">Chunk-local X of the corner water source (0 or 15).</param>
        /// <param name="centerZ">Chunk-local Z of the corner water source (0 or 15).</param>
        /// <param name="cardinal">An adjacent cardinal neighbor to fill with fluid, opening the diagonal path.</param>
        /// <param name="cardinalX">Neighbor-local X in that cardinal map.</param>
        /// <param name="cardinalZ">Neighbor-local Z in that cardinal map.</param>
        /// <param name="diagX">Neighbor-local X in the diagonal map.</param>
        /// <param name="diagZ">Neighbor-local Z in the diagonal map.</param>
        /// <param name="label">Assertion label for this leg.</param>
        /// <returns>True when the diagonal demonstrably lowered the corner.</returns>
        private static bool B38Leg(DiagonalNeighbor diagonal, int centerX, int centerZ,
            CardinalNeighbor cardinal, int cardinalX, int cardinalZ, int diagX, int diagZ, string label)
        {
            float withoutDiagonal = SumVertexHeight(diagonal, centerX, centerZ, cardinal, cardinalX, cardinalZ,
                diagX, diagZ, populateDiagonal: false);
            float withDiagonal = SumVertexHeight(diagonal, centerX, centerZ, cardinal, cardinalX, cardinalZ,
                diagX, diagZ, populateDiagonal: true);

            return MeshAssert.IsTrue($"{label} diagonal lowers the fluid corner", withDiagonal < withoutDiagonal,
                $"summed vertex height with the {diagonal} map populated = {withDiagonal:F4}, without = {withoutDiagonal:F4} " +
                "(expected strictly lower — equal means the job never read this diagonal slot)");
        }

        /// <summary>Builds one B38 leg's fixture and returns the summed Y of every emitted vertex.</summary>
        /// <param name="diagonal">The diagonal slot under test.</param>
        /// <param name="centerX">Chunk-local X of the corner water source.</param>
        /// <param name="centerZ">Chunk-local Z of the corner water source.</param>
        /// <param name="cardinal">The adjacent cardinal neighbor that opens the diagonal path.</param>
        /// <param name="cardinalX">Neighbor-local X in that cardinal map.</param>
        /// <param name="cardinalZ">Neighbor-local Z in that cardinal map.</param>
        /// <param name="diagX">Neighbor-local X in the diagonal map.</param>
        /// <param name="diagZ">Neighbor-local Z in the diagonal map.</param>
        /// <param name="populateDiagonal">Whether to place the low-level fluid in the diagonal map.</param>
        /// <returns>The sum of every emitted vertex's Y coordinate.</returns>
        private static float SumVertexHeight(DiagonalNeighbor diagonal, int centerX, int centerZ,
            CardinalNeighbor cardinal, int cardinalX, int cardinalZ, int diagX, int diagZ, bool populateDiagonal)
        {
            using MeshingTestWorld world = new MeshingTestWorld();
            world.SetBlock(centerX, DIAG_Y, centerZ, TestMeshBlockPalette.WaterSource, FLUID_SOURCE_LEVEL);
            world.SetNeighborBlock(cardinal, cardinalX, DIAG_Y, cardinalZ, TestMeshBlockPalette.WaterSource,
                FLUID_SOURCE_LEVEL);

            if (populateDiagonal)
            {
                world.SetNeighborBlock(diagonal, diagX, DIAG_Y, diagZ, TestMeshBlockPalette.WaterSource,
                    FLUID_LOW_LEVEL);
            }

            MeshDataJobOutput o = world.Run();

            float sum = 0f;
            for (int i = 0; i < o.Vertices.Length; i++) sum += o.Vertices[i].y;
            return sum;
        }

        /// <summary>
        /// B38 (MH-12, diagonal half) — every diagonal neighbor map must reach the slot it belongs to.
        /// B37 guards the cardinals through face culling, which the diagonals never reach; they instead drive
        /// <b>fluid corner geometry</b> (<c>GetSmoothedCornerHeight</c> / <c>CalculateSymmetricCornerFlow</c>,
        /// unconditional — not gated on <c>SmoothLightingQuality</c>), so a transposed diagonal shows
        /// up as a wrong fluid surface at a chunk corner rather than a missing face.
        /// <para>
        /// Each leg isolates one diagonal: a water source on the matching chunk corner, water in one adjacent
        /// cardinal map (without it the engine refuses the diagonal term), and — in the second run only —
        /// lower-level water in the diagonal map itself. Admitting that shorter column must strictly lower the
        /// corner's averaged height. Because only the slot under test is ever populated, any misrouting reads
        /// an empty map and the two runs coincide, so every permutation of the four is caught.
        /// </para>
        /// </summary>
        private static bool B38_DiagonalNeighborsAreNotPermuted()
        {
            bool passed = B38Leg(DiagonalNeighbor.NorthEast, PERM_HIGH_BORDER, PERM_HIGH_BORDER,
                CardinalNeighbor.North, PERM_HIGH_BORDER, PERM_LOW_BORDER, PERM_LOW_BORDER, PERM_LOW_BORDER, "B38-NE");
            passed &= B38Leg(DiagonalNeighbor.SouthEast, PERM_HIGH_BORDER, PERM_LOW_BORDER,
                CardinalNeighbor.South, PERM_HIGH_BORDER, PERM_HIGH_BORDER, PERM_LOW_BORDER, PERM_HIGH_BORDER, "B38-SE");
            passed &= B38Leg(DiagonalNeighbor.SouthWest, PERM_LOW_BORDER, PERM_LOW_BORDER,
                CardinalNeighbor.South, PERM_LOW_BORDER, PERM_HIGH_BORDER, PERM_HIGH_BORDER, PERM_HIGH_BORDER, "B38-SW");
            passed &= B38Leg(DiagonalNeighbor.NorthWest, PERM_LOW_BORDER, PERM_HIGH_BORDER,
                CardinalNeighbor.North, PERM_LOW_BORDER, PERM_LOW_BORDER, PERM_HIGH_BORDER, PERM_LOW_BORDER, "B38-NW");
            return passed;
        }

        /// <summary>
        /// A fake <see cref="INeighborMapSource"/> that hands back a one-element map whose single value encodes
        /// the coordinate it was asked for, so a caller can prove which offset landed in which slot without any
        /// buffers, pool or <c>World</c>.
        /// </summary>
        private sealed class MarkerNeighborMapSource : INeighborMapSource
        {
            private readonly List<NativeArray<uint>> _voxel = new List<NativeArray<uint>>();
            private readonly List<NativeArray<ushort>> _light = new List<NativeArray<ushort>>();

            /// <summary>Packs a coordinate delta into the marker value stored in the returned map.</summary>
            /// <param name="dx">Chunk-space X delta from the center.</param>
            /// <param name="dz">Chunk-space Z delta from the center.</param>
            /// <returns>The marker value a map for that delta carries.</returns>
            public static uint Marker(int dx, int dz) => (uint)((dx + 8) * 100 + (dz + 8));

            /// <inheritdoc />
            public NativeArray<uint> AcquireVoxelMap(ChunkCoord coord, bool pooled, Allocator allocator)
            {
                NativeArray<uint> map = new NativeArray<uint>(1, Allocator.Persistent);
                map[0] = Marker(coord.X, coord.Z);
                _voxel.Add(map);
                return map;
            }

            /// <inheritdoc />
            public NativeArray<ushort> AcquireLightMap(ChunkCoord coord, bool pooled, Allocator allocator)
            {
                NativeArray<ushort> map = new NativeArray<ushort>(1, Allocator.Persistent);
                map[0] = (ushort)Marker(coord.X, coord.Z);
                _light.Add(map);
                return map;
            }

            /// <summary>Releases every map handed out.</summary>
            public void Dispose()
            {
                foreach (NativeArray<uint> m in _voxel) m.Dispose();
                foreach (NativeArray<ushort> m in _light) m.Dispose();
            }
        }

        /// <summary>
        /// B39 (MH-12, acquire site) — <see cref="NeighborMapAssembler.Build"/> must send each compass
        /// direction to the right chunk offset, for the light maps as well as the voxel maps.
        /// <para>
        /// This is a <b>second</b> direction→offset table, one layer above the job-field wiring B37/B38 guard,
        /// and it feeds <b>both</b> the meshing and lighting schedules. Neither suite could see it before MP-7's
        /// review round: <c>MeshingTestWorld</c> and <c>LightingTestWorld</c> each build their own
        /// <c>NeighborMapSet</c>, so a transposition here (every N/S seam culling and lighting against the wrong
        /// chunk) left all 348 baselines green. The fake source returns a coordinate-encoding marker per call,
        /// so the assertion reads the delta straight back out of each slot.
        /// </para>
        /// </summary>
        private static bool B39_NeighborMapAssemblerOffsetsAreCorrect()
        {
            ChunkCoord center = new ChunkCoord(3, -5);
            MarkerNeighborMapSource source = new MarkerNeighborMapSource();
            try
            {
                NeighborMapSet set = NeighborMapAssembler.Build(center, source, pooled: false, Allocator.Persistent);

                bool passed = AssertSlot("B39 NeighborN", set.NeighborN, center, 0, 1);
                passed &= AssertSlot("B39 NeighborE", set.NeighborE, center, 1, 0);
                passed &= AssertSlot("B39 NeighborS", set.NeighborS, center, 0, -1);
                passed &= AssertSlot("B39 NeighborW", set.NeighborW, center, -1, 0);
                passed &= AssertSlot("B39 NeighborNE", set.NeighborNE, center, 1, 1);
                passed &= AssertSlot("B39 NeighborSE", set.NeighborSE, center, 1, -1);
                passed &= AssertSlot("B39 NeighborSW", set.NeighborSW, center, -1, -1);
                passed &= AssertSlot("B39 NeighborNW", set.NeighborNW, center, -1, 1);

                passed &= AssertSlot("B39 LightN", set.LightN, center, 0, 1);
                passed &= AssertSlot("B39 LightE", set.LightE, center, 1, 0);
                passed &= AssertSlot("B39 LightS", set.LightS, center, 0, -1);
                passed &= AssertSlot("B39 LightW", set.LightW, center, -1, 0);
                passed &= AssertSlot("B39 LightNE", set.LightNE, center, 1, 1);
                passed &= AssertSlot("B39 LightSE", set.LightSE, center, 1, -1);
                passed &= AssertSlot("B39 LightSW", set.LightSW, center, -1, -1);
                passed &= AssertSlot("B39 LightNW", set.LightNW, center, -1, 1);
                return passed;
            }
            finally
            {
                source.Dispose();
            }
        }

        /// <summary>Asserts one voxel slot carries the marker for the expected neighbor offset.</summary>
        /// <param name="label">Assertion label.</param>
        /// <param name="slot">The map that landed in this slot.</param>
        /// <param name="center">The center chunk the set was built for.</param>
        /// <param name="dx">Expected chunk-space X delta.</param>
        /// <param name="dz">Expected chunk-space Z delta.</param>
        /// <returns>True when the slot holds the expected neighbor's map.</returns>
        private static bool AssertSlot(string label, NativeArray<uint> slot, ChunkCoord center, int dx, int dz)
        {
            uint expected = MarkerNeighborMapSource.Marker(center.X + dx, center.Z + dz);
            return MeshAssert.IsTrue(label, slot.IsCreated && slot.Length == 1 && slot[0] == expected,
                $"slot holds marker {(slot.IsCreated && slot.Length == 1 ? slot[0].ToString() : "<none>")}, " +
                $"expected {expected} (the map for offset ({dx}, {dz}))");
        }

        /// <summary>Asserts one light slot carries the marker for the expected neighbor offset.</summary>
        /// <param name="label">Assertion label.</param>
        /// <param name="slot">The map that landed in this slot.</param>
        /// <param name="center">The center chunk the set was built for.</param>
        /// <param name="dx">Expected chunk-space X delta.</param>
        /// <param name="dz">Expected chunk-space Z delta.</param>
        /// <returns>True when the slot holds the expected neighbor's map.</returns>
        private static bool AssertSlot(string label, NativeArray<ushort> slot, ChunkCoord center, int dx, int dz)
        {
            ushort expected = (ushort)MarkerNeighborMapSource.Marker(center.X + dx, center.Z + dz);
            return MeshAssert.IsTrue(label, slot.IsCreated && slot.Length == 1 && slot[0] == expected,
                $"slot holds marker {(slot.IsCreated && slot.Length == 1 ? slot[0].ToString() : "<none>")}, " +
                $"expected {expected} (the map for offset ({dx}, {dz}))");
        }
    }
}
