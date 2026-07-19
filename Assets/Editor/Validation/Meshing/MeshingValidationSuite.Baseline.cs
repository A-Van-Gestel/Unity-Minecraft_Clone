using System.Collections.Generic;
using Data;
using Data.Enums;
using Editor.Validation.Meshing.Framework;
using Helpers;
using Jobs.BurstData;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Meshing
{
    /// <summary>
    /// Baseline (regression) scenarios for the meshing suite. These must stay green. Several are the
    /// regression guards for performance finding <b>MR-1</b> (hoisting the per-vertex
    /// <c>Quaternion.Euler</c> out of <see cref="VoxelMeshHelper.GenerateStandardCubeFace"/>):
    /// B1 pins the rotated-vertex math bit-for-bit in isolation, B4 pins it end-to-end through the
    /// real <see cref="Jobs.MeshGenerationJob"/>.
    /// </summary>
    public static partial class MeshingValidationSuite
    {
        private static readonly float[] s_rotations = { 0f, 90f, 180f, 270f };

        static partial void AddBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("B1: standard cube face rotation matches oracle (all faces × 0/90/180/270°)", B1_FaceRotationMatchesOracle));
            scenarios.Add(new Scenario("B2: single opaque cube emits 6 oracle-correct faces", B2_SingleOpaqueCube));
            scenarios.Add(new Scenario("B3: fully enclosed cube emits no geometry", B3_FullyEnclosedCube));
            scenarios.Add(new Scenario("B4: oriented cube geometry matches oracle (4 yaws, end-to-end)", B4_OrientedCubeEndToEnd));
            scenarios.Add(new Scenario("B5: meshing is deterministic across runs", B5_Determinism));
            scenarios.Add(new Scenario("B6: transparent cube routes faces to the transparent submesh", B6_TransparentRouting));
            scenarios.Add(new Scenario("B7: fluid blocks route to the fluid submesh, deterministic + structurally valid", B7_FluidRoutingAndDeterminism));
            scenarios.Add(new Scenario("B8: air-surrounded fluid keeps a zero shore mask even after wall-adjacent fluids (MR-7 neighbor-buffer guard)", B8_FluidNeighborBufferIsolation));
            scenarios.Add(new Scenario("B9: multi-section scene tiles SectionStats ranges contiguously (MH-9 per-section partition guard)", B9_MultiSectionStatsTiling));
            scenarios.Add(new Scenario("B10: post-process rewrites to section-space + interleaves stream 3, chained==separate (MH-5 / MR-5 guard)", B10_PostProcessSectionSpaceAndInterleave));
            scenarios.Add(new Scenario("B11: smooth lighting encodes uniform corner light to the right UNorm8 values (MH-3 / MR-2 guard)", B11_SmoothLightingUniformCornerValues));
            scenarios.Add(new Scenario("B17: a pooled output reused across scenes equals a fresh buffer (MH-2 / MR-6 stale-reuse guard)", B17_PooledOutputStaleDataGuard));
            scenarios.Add(new Scenario("B22: cross-mesh UV ZW carries sway weight (top/bottom split) + deterministic per-voxel phase; cubes stay ZW=0 (FL-1 guard)", B22_CrossMeshSwayChannels));
            scenarios.Add(new Scenario("B23: sway-flagged cube writes authored swayStrength + phase to UV ZW on every vert; zero-strength blocks stay ZW=0 (FL-2 guard)", B23_CubeSwayChannels));

            // --- Cross-chunk border-face-culling family (B18–B21, MH-10/MH-11) lives in its own partial
            // file (MeshingValidationSuite.CrossChunk.cs) and self-registers here. ---
            AddCrossChunkBaselineScenarios(scenarios);
        }

        /// <summary>Hook for the cross-chunk border-culling baselines (implemented in MeshingValidationSuite.CrossChunk.cs).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddCrossChunkBaselineScenarios(List<Scenario> scenarios);

        /// <summary>
        /// B1 — Direct, isolated differential test of <see cref="VoxelMeshHelper.GenerateStandardCubeFace"/>:
        /// for every face and every supported Y-rotation, the four emitted vertices and the normal must
        /// equal <see cref="MeshOracle"/>'s (Quaternion-based) expectation. This is the tightest guard
        /// for MR-1 — it exercises the exact function the optimization rewrites, with no surrounding job.
        /// </summary>
        private static bool B1_FaceRotationMatchesOracle()
        {
            if (!BurstVoxelData.VoxelVerts.Data.IsCreated) BurstVoxelData.Initialize();

            Vector3Int pos = new Vector3Int(5, 5, 5);
            Color32 white = new Color32(255, 255, 255, 255);
            Vector3[] expected = new Vector3[4];
            bool allPassed = true;

            for (int face = 0; face < 6; face++)
            {
                foreach (float rotation in s_rotations)
                {
                    NativeList<Vector3> verts = new NativeList<Vector3>(4, Allocator.Temp);
                    NativeList<int> tris = new NativeList<int>(6, Allocator.Temp);
                    NativeList<int> transTris = new NativeList<int>(0, Allocator.Temp);
                    NativeList<half4> uvs = new NativeList<half4>(4, Allocator.Temp);
                    NativeList<Color32> colors = new NativeList<Color32>(4, Allocator.Temp);
                    NativeList<Vector3> normals = new NativeList<Vector3>(4, Allocator.Temp);
                    NativeList<Color32> lightData = new NativeList<Color32>(4, Allocator.Temp);
                    int vertexIndex = 0;

                    VoxelMeshHelper.GenerateStandardCubeFace(
                        face, textureID: 0, in pos, rotation, uvQuarterTurnsCW: 0,
                        white, white, white, white,
                        ref vertexIndex,
                        ref verts, ref tris, ref transTris,
                        ref uvs, ref colors, ref normals, ref lightData, isTransparent: false);

                    MeshOracle.ExpectedStandardCubeFace(face, rotation, pos, expected, out Vector3 normal);
                    bool passed = MeshAssert.QuadMatchesOracle(
                        $"B1 face {face} @ {rotation}°", verts, normals, 0, expected, normal);
                    allPassed &= passed;

                    verts.Dispose();
                    tris.Dispose();
                    transTris.Dispose();
                    uvs.Dispose();
                    colors.Dispose();
                    normals.Dispose();
                    lightData.Dispose();
                }
            }

            return allPassed;
        }

        /// <summary>
        /// B2 — A single unrotated opaque cube in an otherwise-air chunk emits exactly six faces
        /// (24 vertices), satisfies the structural invariants, and every face's geometry matches the
        /// oracle. Doubles as the framework end-to-end smoke test.
        /// </summary>
        private static bool B2_SingleOpaqueCube()
        {
            using MeshingTestWorld world = new MeshingTestWorld();
            Vector3Int pos = new Vector3Int(8, 8, 8);
            world.SetBlock(pos.x, pos.y, pos.z, TestMeshBlockPalette.SolidOpaque);
            MeshDataJobOutput o = world.Run();

            bool passed = MeshAssert.VertexCount("B2 vertex count", o, 24);
            passed &= MeshAssert.StructuralInvariants("B2 structural", o);
            if (o.Vertices.Length != 24) return false; // per-face compare would be meaningless

            BlockTypeJobData solidData = TestMeshBlockPalette.CreateJobDataArray()[TestMeshBlockPalette.SolidOpaque];
            passed &= CompareCubeFacesToOracle("B2", o, pos, orientation: VoxelOrientation.North, rotation: 0f, in solidData);

            // MH-1: every emitted vertex must lie inside the block's section cell — the premise behind
            // MR-4's proposed constant section bounds.
            SectionCellBounds(pos, out Vector3 min, out Vector3 max);
            passed &= MeshAssert.BoundsWithin("B2 bounds", o, min, max);
            return passed;
        }

        /// <summary>
        /// B3 — A cube fully surrounded by opaque cubes is completely occluded and emits no geometry.
        /// Guards that a meshing optimization doesn't start emitting hidden interior faces.
        /// </summary>
        private static bool B3_FullyEnclosedCube()
        {
            using MeshingTestWorld world = new MeshingTestWorld();
            Vector3Int c = new Vector3Int(8, 8, 8);
            world.SetBlock(c.x, c.y, c.z, TestMeshBlockPalette.SolidOpaque);
            world.SetBlock(c.x + 1, c.y, c.z, TestMeshBlockPalette.SolidOpaque);
            world.SetBlock(c.x - 1, c.y, c.z, TestMeshBlockPalette.SolidOpaque);
            world.SetBlock(c.x, c.y + 1, c.z, TestMeshBlockPalette.SolidOpaque);
            world.SetBlock(c.x, c.y - 1, c.z, TestMeshBlockPalette.SolidOpaque);
            world.SetBlock(c.x, c.y, c.z + 1, TestMeshBlockPalette.SolidOpaque);
            world.SetBlock(c.x, c.y, c.z - 1, TestMeshBlockPalette.SolidOpaque);
            MeshDataJobOutput o = world.Run();

            // The expected count is derived: 6 shell cubes, each exposing 5 faces (the 6th is culled
            // by the opaque center), plus the center cube exposing 0 → 6 × 5 × 4 = 120 vertices. That
            // derivation only holds while the palette's SolidOpaque really is an opaque, solid,
            // non-render-neighbor cube and Air is non-solid. Assert those assumptions so a palette
            // edit fails loudly here instead of silently invalidating the magic constant.
            BlockTypeJobData[] palette = TestMeshBlockPalette.CreateJobDataArray();
            BlockTypeJobData solid = palette[TestMeshBlockPalette.SolidOpaque];
            bool assumptionsHold = solid.IsSolid && solid.IsOpaque && !solid.RenderNeighborFaces
                                   && !palette[TestMeshBlockPalette.Air].IsSolid;
            bool passed = MeshAssert.IsTrue("B3 palette assumptions", assumptionsHold,
                "SolidOpaque must be solid + opaque + non-render-neighbor and Air non-solid for the derived count to hold");

            const int shellCubes = 6;
            const int facesPerShellCube = 5; // 6 faces minus the one culled by the opaque center
            const int vertsPerFace = 4;
            passed &= MeshAssert.VertexCount("B3 enclosed-cube occlusion", o, shellCubes * facesPerShellCube * vertsPerFace);
            return passed;
        }

        /// <summary>
        /// B4 — End-to-end MR-1 guard: a <see cref="MetadataSchema.HorizontalOnly"/> cube meshed
        /// through the real job for each of the four yaws must produce geometry identical to the
        /// oracle's rotated faces. This is the path the per-vertex quaternion rotation actually runs in.
        /// </summary>
        private static bool B4_OrientedCubeEndToEnd()
        {
            using MeshingTestWorld world = new MeshingTestWorld();
            Vector3Int pos = new Vector3Int(8, 8, 8);
            BlockTypeJobData orientedData = TestMeshBlockPalette.CreateJobDataArray()[TestMeshBlockPalette.OrientedOpaque];
            bool allPassed = true;

            for (byte yaw = 0; yaw < 4; yaw++)
            {
                world.Clear();
                world.SetBlock(pos.x, pos.y, pos.z, TestMeshBlockPalette.OrientedOpaque, meta: yaw);
                MeshDataJobOutput o = world.Run();

                bool passed = MeshAssert.VertexCount($"B4 yaw {yaw} vertex count", o, 24);
                passed &= MeshAssert.StructuralInvariants($"B4 yaw {yaw} structural", o);

                // MH-1: a rotated cube's vertices must still fall inside its section cell.
                SectionCellBounds(pos, out Vector3 min, out Vector3 max);
                passed &= MeshAssert.BoundsWithin($"B4 yaw {yaw} bounds", o, min, max);

                if (o.Vertices.Length == 24)
                {
                    byte orientation = MeshOracle.LegacyOrientationForYaw(yaw);
                    float rotation = MeshOracle.RotationForYaw(yaw);
                    passed &= CompareCubeFacesToOracle($"B4 yaw {yaw}", o, pos, orientation, rotation, in orientedData);
                }
                else
                {
                    passed = false;
                }

                allPassed &= passed;
            }

            return allPassed;
        }

        /// <summary>
        /// B5 — Meshing the same voxel data twice produces byte-identical output. Guards against any
        /// optimization introducing nondeterminism (e.g. uninitialized hoisted buffers).
        /// </summary>
        private static bool B5_Determinism()
        {
            using MeshingTestWorld worldA = new MeshingTestWorld();
            using MeshingTestWorld worldB = new MeshingTestWorld();

            // A small structured mix: an oriented cube, a plain cube, and a transparent cube.
            foreach (MeshingTestWorld w in new[] { worldA, worldB })
            {
                w.SetBlock(4, 6, 4, TestMeshBlockPalette.SolidOpaque);
                w.SetBlock(6, 6, 4, TestMeshBlockPalette.OrientedOpaque, meta: 2); // West
                w.SetBlock(8, 6, 4, TestMeshBlockPalette.TransparentCube);
            }

            MeshDataJobOutput a = worldA.Run();
            MeshDataJobOutput b = worldB.Run();
            return MeshAssert.OutputsEqual("B5 determinism", a, b);
        }

        /// <summary>
        /// B6 — A transparent cube (renderNeighborFaces) routes all its faces to the transparent
        /// submesh, leaving the opaque triangle list empty. Guards the transparent culling/routing path.
        /// </summary>
        private static bool B6_TransparentRouting()
        {
            using MeshingTestWorld world = new MeshingTestWorld();
            world.SetBlock(8, 8, 8, TestMeshBlockPalette.TransparentCube);
            MeshDataJobOutput o = world.Run();

            bool passed = MeshAssert.VertexCount("B6 vertex count", o, 24);
            passed &= MeshAssert.IsTrue("B6 opaque submesh empty", o.Triangles.Length == 0,
                $"opaque triangles = {o.Triangles.Length} (expected 0)");
            passed &= MeshAssert.IsTrue("B6 transparent submesh filled", o.TransparentTriangles.Length == 36,
                $"transparent triangles = {o.TransparentTriangles.Length} (expected 36)");
            passed &= MeshAssert.StructuralInvariants("B6 structural", o);
            return passed;
        }

        /// <summary>
        /// B7 — A pure-fluid scene (a small water pool, one block stacked to exercise the
        /// <c>hasFluidAbove</c> path) emits geometry, routes <b>all</b> of it to the fluid submesh
        /// (opaque + transparent triangle lists stay empty), satisfies the structural invariants, and
        /// is bit-identical across two runs. This is the fluid analog of B6/B5 and the broad regression
        /// guard for the fluid meshing path that finding MR-7 optimizes.
        /// </summary>
        private static bool B7_FluidRoutingAndDeterminism()
        {
            using MeshingTestWorld worldA = new MeshingTestWorld();
            using MeshingTestWorld worldB = new MeshingTestWorld();

            foreach (MeshingTestWorld w in new[] { worldA, worldB })
            {
                // 3×3 water pool at y=6, air all around it, plus one stacked source so a block has the
                // same fluid directly above it (top-face suppression path).
                for (int dx = 0; dx < 3; dx++)
                for (int dz = 0; dz < 3; dz++)
                    w.SetBlock(6 + dx, 6, 6 + dz, TestMeshBlockPalette.WaterSource);
                w.SetBlock(7, 7, 7, TestMeshBlockPalette.WaterSource);
            }

            MeshDataJobOutput a = worldA.Run();
            MeshDataJobOutput b = worldB.Run();

            bool passed = MeshAssert.IsTrue("B7 fluid submesh filled", a.FluidTriangles.Length > 0,
                $"fluid triangles = {a.FluidTriangles.Length} (expected > 0)");
            passed &= MeshAssert.IsTrue("B7 opaque submesh empty", a.Triangles.Length == 0,
                $"opaque triangles = {a.Triangles.Length} (expected 0 for a pure-fluid scene)");
            passed &= MeshAssert.IsTrue("B7 transparent submesh empty", a.TransparentTriangles.Length == 0,
                $"transparent triangles = {a.TransparentTriangles.Length} (expected 0 for a pure-fluid scene)");
            passed &= MeshAssert.StructuralInvariants("B7 structural", a);
            passed &= MeshAssert.OutputsEqual("B7 determinism", a, b);
            return passed;
        }

        /// <summary>
        /// B8 — Fluid neighbor-buffer isolation guard, written test-first for finding <b>MR-7</b> (hoist
        /// the per-fluid-voxel <c>Allocator.Temp</c> neighbor arrays out of the voxel loop).
        /// <para>
        /// The fluid mesher writes a neighbor slot only when that neighbor exists; air slots are left at
        /// their default. With a fresh per-voxel buffer that default is correct, but a future
        /// optimization that reuses one buffer across voxels <i>without resetting it</i> would leak a
        /// prior fluid voxel's neighbors into a later air-surrounded one. This scenario makes that leak
        /// observable in three parts:
        /// <list type="number">
        /// <item><b>Reference</b> — an isolated, fully air-surrounded source. Its shore mask
        /// (<c>Color.g</c>) must be 0, and its full quad set becomes the golden reference.</item>
        /// <item><b>Differential</b> — the same probe meshed <i>after</i> several fluid sources each
        /// fully encased in solid (priming all 14 neighbor slots with solid state). The probe stays
        /// air-surrounded, so its <i>entire</i> quad set must be byte-identical to the reference — this
        /// catches a leak into <i>any</i> slot (cardinal, diagonal, above/below), not just the shore
        /// mask. An explicit assertion pins the load-bearing invariant that every primer is meshed
        /// before the probe (lower in-section flattened index).</item>
        /// <item><b>Positive control</b> — a probe ringed by four solid walls <i>must</i> report a
        /// non-zero shore mask (15/255). Without this, a silent break in the extraction/packing would
        /// let parts 1–2 pass vacuously, leaving a dead tripwire.</item>
        /// </list>
        /// </para>
        /// </summary>
        private static bool B8_FluidNeighborBufferIsolation()
        {
            Vector3Int probe = new Vector3Int(8, 8, 8);
            Vector3Int[] primers = { new Vector3Int(2, 4, 2), new Vector3Int(2, 4, 5), new Vector3Int(5, 4, 2) };

            // Load-bearing invariant: each primer must be meshed BEFORE the probe (same section, lower
            // flattened index), else a reused-but-not-reset buffer can't carry primer state into the
            // probe and the differential below would be a false green. Voxel iteration walks the
            // in-section flattened index, so assert ordering explicitly instead of trusting coordinates.
            int probeIndex = ChunkMath.GetFlattenedIndexInChunk(probe.x, probe.y, probe.z);
            int probeSection = probe.y / ChunkMath.SECTION_SIZE;
            bool ordered = true;
            foreach (Vector3Int p in primers)
                ordered &= p.y / ChunkMath.SECTION_SIZE == probeSection
                           && ChunkMath.GetFlattenedIndexInChunk(p.x, p.y, p.z) < probeIndex;
            bool passed = MeshAssert.IsTrue("B8 primers precede probe", ordered,
                "every primer must share the probe's section and have a lower flattened index, else the buffer-reuse leak cannot reach the probe");

            // (1) Reference: isolated air-surrounded source → zero shore mask + golden quad set.
            List<ProbeQuad> reference;
            using (MeshingTestWorld world = new MeshingTestWorld())
            {
                world.SetBlock(probe.x, probe.y, probe.z, TestMeshBlockPalette.WaterSource);
                MeshDataJobOutput o = world.Run();
                reference = CollectProbeQuads(o, probe);
            }

            passed &= MeshAssert.IsTrue("B8 reference emits geometry", reference.Count > 0,
                $"isolated probe emitted {reference.Count} quads (expected > 0)");
            passed &= MeshAssert.IsTrue("B8 reference shore mask",
                TryTopFaceShoreMask(reference, out float refMask) && Mathf.Abs(refMask) <= MeshAssert.VertexEpsilon,
                $"air-surrounded fluid shore mask = {(reference.Count > 0 ? refMask : float.NaN):F4} (expected 0)");

            // (2) Differential: same probe, preceded by solid-encased fluid primers that prime ALL 14
            //     neighbor slots. The probe stays air-surrounded, so its full quad set must match the
            //     reference exactly — a leak into any slot would change its geometry and trip this.
            using (MeshingTestWorld world = new MeshingTestWorld())
            {
                foreach (Vector3Int p in primers) PlaceEncasedFluid(world, p);
                world.SetBlock(probe.x, probe.y, probe.z, TestMeshBlockPalette.WaterSource);
                MeshDataJobOutput o = world.Run();
                List<ProbeQuad> primed = CollectProbeQuads(o, probe);
                passed &= ProbeQuadsEqual("B8 primed probe matches isolated reference", reference, primed);
            }

            // (3) Positive control: a cardinal-walled probe MUST report a non-zero shore mask, proving
            //     the extraction path can actually observe a wall-neighbor leak.
            using (MeshingTestWorld world = new MeshingTestWorld())
            {
                PlaceCardinalWalledFluid(world, probe);
                MeshDataJobOutput o = world.Run();
                List<ProbeQuad> walled = CollectProbeQuads(o, probe);
                const float fourCardinalWalls = (1f + 2f + 4f + 8f) / 255f; // wallN|S|E|W, no diagonals
                passed &= MeshAssert.IsTrue("B8 positive control non-zero mask",
                    TryTopFaceShoreMask(walled, out float wallMask) && Mathf.Abs(wallMask - fourCardinalWalls) <= MeshAssert.VertexEpsilon,
                    $"wall-boxed probe shore mask = {(walled.Count > 0 ? wallMask : float.NaN):F4} (expected {fourCardinalWalls:F4})");
            }

            return passed;
        }

        /// <summary>
        /// B9 — Multi-section <c>SectionStats</c> tiling guard (finding <b>MH-9</b>). Three isolated
        /// opaque cubes are placed one per section (y=8/24/40 → sections 0/1/2) so more than one section
        /// emits geometry. <see cref="MeshAssert.StructuralInvariants"/> now asserts the per-section
        /// vertex/triangle ranges are contiguous, non-overlapping, and sum to each stream's length — a
        /// refactor that mis-partitions sections (the kind MR-5/MR-6 per-section work risks) passes the
        /// global length/index checks but fails here.
        /// <para>
        /// B2/B4 touch only one section, so their tiling check is satisfied by a single range; this
        /// scenario adds a positive control asserting that at least two sections actually emitted
        /// geometry, so the contiguity check cannot pass vacuously.
        /// </para>
        /// </summary>
        private static bool B9_MultiSectionStatsTiling()
        {
            using MeshingTestWorld world = new MeshingTestWorld();

            // One isolated cube per section: 16 blocks apart vertically so each is fully air-surrounded
            // and emits its own 24 vertices. y=8 → section 0, y=24 → section 1, y=40 → section 2.
            Vector3Int[] cubes = { new Vector3Int(8, 8, 8), new Vector3Int(8, 24, 8), new Vector3Int(8, 40, 8) };
            foreach (Vector3Int c in cubes)
                world.SetBlock(c.x, c.y, c.z, TestMeshBlockPalette.SolidOpaque);
            MeshDataJobOutput o = world.Run();

            bool passed = MeshAssert.VertexCount("B9 vertex count", o, cubes.Length * 24);
            passed &= MeshAssert.StructuralInvariants("B9 structural (incl. section tiling)", o);

            // Positive control: the tiling check is only meaningful with >= 2 emitting sections. Verify
            // the scene actually produced that, so a future fixture change can't silently make B9 vacuous.
            int emittingSections = 0;
            for (int s = 0; s < o.SectionStats.Length; s++)
                if (o.SectionStats[s].VertexCount > 0)
                    emittingSections++;
            passed &= MeshAssert.IsTrue("B9 multi-section coverage", emittingSections == cubes.Length,
                $"sections emitting geometry = {emittingSections} (expected {cubes.Length}, else the tiling check is vacuous)");

            return passed;
        }

        /// <summary>
        /// B10 — Post-process stage guard (finding <b>MH-5</b>; the prerequisite for <b>MR-5</b> and half of
        /// MR-2). The job suite otherwise stops at the chunk-space <see cref="Jobs.MeshGenerationJob"/> output;
        /// this runs the real <see cref="Jobs.MeshPostProcessJob"/> after it and asserts the three properties an
        /// output-preserving move of that stage (MR-5) must keep:
        /// <list type="number">
        /// <item><b>Section-space coords</b> — every post-processed vertex equals its chunk-space original
        /// with <c>y</c> shifted down by its section's <c>index × SECTION_SIZE</c> (x/z unchanged).</item>
        /// <item><b>InterleavedStream3</b> — equals the element-wise interleave of <c>Normals</c> +
        /// <c>LightData</c> (the GPU-upload vertex stream MR-2 restructures, empty in the gen-only suite).</item>
        /// <item><b>Chained == separate</b> — running the post job chained on the gen handle off-thread
        /// (the MR-5 shape) produces byte-identical output to the production blocking
        /// <c>Schedule().Complete()</c> path. MR-7/B8-style differential.</item>
        /// </list>
        /// Fixture: two isolated opaque cubes in <i>non-zero</i> sections (y=24 → section 1, y=40 → section 2)
        /// so the y-offset transform is non-identity and per-section index relativization spans two sections.
        /// Two positive controls keep it from passing vacuously: (i) the gen-only run's
        /// <c>InterleavedStream3</c> is empty while the post run's is full (so the post stage is what fills it),
        /// and (ii) at least one emitting section has index ≥ 1 (so section-space genuinely differs from
        /// chunk-space rather than the offset being a no-op).
        /// </summary>
        private static bool B10_PostProcessSectionSpaceAndInterleave()
        {
            // Two cubes, one per non-zero section, fully air-surrounded so each emits its own 24 verts.
            Vector3Int[] cubes = { new Vector3Int(8, 24, 8), new Vector3Int(8, 40, 8) }; // sections 1 and 2

            // (Reference) Gen-only run: capture chunk-space vertices + confirm InterleavedStream3 is empty.
            Vector3[] chunkVerts;
            bool passed;
            using (MeshingTestWorld refWorld = new MeshingTestWorld())
            {
                foreach (Vector3Int c in cubes) refWorld.SetBlock(c.x, c.y, c.z, TestMeshBlockPalette.SolidOpaque);
                MeshDataJobOutput refOut = refWorld.Run(); // PostProcessMode.Off

                passed = MeshAssert.VertexCount("B10 reference vertex count", refOut, cubes.Length * 24);
                // Positive control (i): the gen-only stage must NOT populate InterleavedStream3 — proving the
                // post-process is solely responsible for it, so the interleave assertion below isn't vacuous.
                passed &= MeshAssert.IsTrue("B10 gen-only InterleavedStream3 empty", refOut.InterleavedStream3.Length == 0,
                    $"InterleavedStream3 length = {refOut.InterleavedStream3.Length} (expected 0 before post-process)");

                chunkVerts = new Vector3[refOut.Vertices.Length];
                for (int i = 0; i < chunkVerts.Length; i++) chunkVerts[i] = refOut.Vertices[i];
            }

            // Two worlds so both post-processed outputs stay live for the chained-vs-separate comparison.
            using MeshingTestWorld separateWorld = new MeshingTestWorld();
            using MeshingTestWorld chainedWorld = new MeshingTestWorld();
            foreach (Vector3Int c in cubes)
            {
                separateWorld.SetBlock(c.x, c.y, c.z, TestMeshBlockPalette.SolidOpaque);
                chainedWorld.SetBlock(c.x, c.y, c.z, TestMeshBlockPalette.SolidOpaque);
            }

            MeshDataJobOutput separate = separateWorld.Run(postProcess: PostProcessMode.Separate);
            MeshDataJobOutput chained = chainedWorld.Run(postProcess: PostProcessMode.Chained);

            // (a) Section-space coordinate rewrite.
            passed &= MeshAssert.SectionSpaceVertices("B10 section-space coords", separate, chunkVerts, ChunkMath.SECTION_SIZE);

            // The post-process relativizes per-section triangle indices in place; independently assert the
            // post-processed output is still structurally valid (indices in range & %3, SectionStats tile each
            // stream). chained==separate alone can't catch a deterministic relativization bug — both paths run
            // the same code — so this is the only guard that the rewritten indices remain well-formed.
            passed &= MeshAssert.StructuralInvariants("B10 post-process structural", separate);

            // Positive control (ii): the rewrite must be non-identity — at least one emitting section sits
            // above section 0, so its y-offset is non-zero and section-space ≠ chunk-space for those verts.
            int highestEmittingSection = -1;
            for (int s = 0; s < separate.SectionStats.Length; s++)
                if (separate.SectionStats[s].VertexCount > 0)
                    highestEmittingSection = s;
            passed &= MeshAssert.IsTrue("B10 non-identity offset", highestEmittingSection >= 1,
                $"highest emitting section = {highestEmittingSection} (expected ≥ 1 so the section-space y-offset is non-zero)");

            // (b) InterleavedStream3 == interleave(Normals, LightData), and is now non-empty.
            passed &= MeshAssert.IsTrue("B10 post InterleavedStream3 filled", separate.InterleavedStream3.Length == separate.Vertices.Length,
                $"InterleavedStream3 length = {separate.InterleavedStream3.Length} (expected {separate.Vertices.Length})");
            passed &= MeshAssert.InterleavedMatches("B10 interleave", separate);

            // (c) Chained (MR-5 shape) == separate (production shape), across every stream incl. stream 3.
            passed &= MeshAssert.OutputsEqual("B10 chained==separate base streams", separate, chained);
            passed &= MeshAssert.InterleavedStreamsEqual("B10 chained==separate interleaved", separate, chained);

            return passed;
        }

        /// <summary>
        /// B11 — Smooth-lighting <i>value</i> guard (finding <b>MH-3</b>; completes the prerequisite set for
        /// <b>MR-2</b>, whose acceptance criterion is "the smooth-lighting encoding in TexCoord1 must be
        /// preserved exactly"). The suite otherwise runs <c>SmoothLightingQuality.Off</c> over a zeroed light
        /// map, so <c>LightData</c> values are meaningless. This populates the in-chunk light map with a
        /// <i>spatially uniform</i> field and meshes an isolated opaque cube under
        /// <see cref="SmoothLightingQuality.High"/>, then pins every emitted vertex's corner light against a
        /// hand-derived oracle.
        /// <para>
        /// The oracle (<see cref="MeshOracle.ExpectedUniformCornerLight"/>) is the limiting case of the
        /// engine's 4-sample corner averaging: when every sampled neighbor holds the same level, the result
        /// is <c>17 × level</c> per channel regardless of <i>which</i> neighbors are sampled — so it is
        /// derived without copying the engine's <c>CornerOffsets</c> sampling LUT (the A4 shared-assumption
        /// trap). Two configs are checked: full sunlight (→ 255 sun) and an intermediate, multi-channel
        /// blocklight (R=7→119, G=3→51) that proves the averaging + UNorm8 rounding + channel order rather
        /// than passing vacuously on an all-zero or saturated read.
        /// </para>
        /// <para>
        /// Positive control: the two configs must produce <i>different</i> <c>LightData</c>, proving the
        /// populated light map demonstrably drives the output (a path that ignored the map could not yield
        /// both). <b>Out of scope (future MH-3 extension):</b> distinct-per-corner values and AO darkening —
        /// see <see cref="MeshOracle.ExpectedUniformCornerLight"/>.
        /// </para>
        /// </summary>
        private static bool B11_SmoothLightingUniformCornerValues()
        {
            Vector3Int pos = new Vector3Int(8, 8, 8); // interior, so every sampled neighbor is in-chunk

            // Config A — uniform full sunlight. Every corner averages 4× sky=15 → 17×15 = 255.
            // `engineA` captures the ACTUAL emitted light (vert 0) for the cross-config positive control below.
            Color32 engineA;
            using (MeshingTestWorld world = new MeshingTestWorld())
            {
                world.SetBlock(pos.x, pos.y, pos.z, TestMeshBlockPalette.SolidOpaque);
                world.FillLight(LightBitMapping.PackLightData(sky: 15, blockR: 0, blockG: 0, blockB: 0));
                MeshDataJobOutput o = world.Run(SmoothLightingQuality.High);

                bool ok = MeshAssert.VertexCount("B11-A vertex count", o, 24);
                ok &= MeshAssert.StructuralInvariants("B11-A structural", o);
                Color32 expectedA = MeshOracle.ExpectedUniformCornerLight(15, 0, 0, 0); // (255,0,0,0)
                ok &= MeshAssert.LightDataMatches("B11-A full sunlight", o, expectedA);
                if (!ok) return false;
                engineA = o.LightData[0];
            }

            // Config B — uniform intermediate blocklight (no sky). R=7→(28·17+2)/4=119, G=3→51, B=0.
            Color32 engineB;
            using (MeshingTestWorld world = new MeshingTestWorld())
            {
                world.SetBlock(pos.x, pos.y, pos.z, TestMeshBlockPalette.SolidOpaque);
                world.FillLight(LightBitMapping.PackLightData(sky: 0, blockR: 7, blockG: 3, blockB: 0));
                MeshDataJobOutput o = world.Run(SmoothLightingQuality.High);

                bool ok = MeshAssert.VertexCount("B11-B vertex count", o, 24);
                ok &= MeshAssert.StructuralInvariants("B11-B structural", o);
                Color32 expectedB = MeshOracle.ExpectedUniformCornerLight(0, 7, 3, 0); // (0,119,51,0)
                ok &= MeshAssert.LightDataMatches("B11-B intermediate blocklight", o, expectedB);
                if (!ok) return false;
                engineB = o.LightData[0];
            }

            // Positive control: the two configs' ACTUAL emitted light must differ, proving the populated map
            // drives the output — a path that ignored the light map (or hardcoded a constant) would emit the
            // same value for both. Comparing engine output (not the oracle constants, which trivially differ).
            bool differ = !(engineA.r == engineB.r && engineA.g == engineB.g && engineA.b == engineB.b && engineA.a == engineB.a);
            return MeshAssert.IsTrue("B11 configs drive distinct light", differ,
                $"engine A {FmtColor(engineA)} vs B {FmtColor(engineB)} (must differ, else the map isn't driving LightData)");
        }

        /// <summary>
        /// B17 (MH-2) — guards the MR-6 output-pooling reset. A pooled <see cref="MeshDataJobOutput"/>
        /// reused across two <i>different</i> scenes must produce byte-identical output to a fresh buffer
        /// running the second scene. The hazard: <see cref="Jobs.MeshGenerationJob"/> <b>appends</b> to
        /// its output lists (and writes triangle indices from a job-local vertex counter that starts at
        /// 0) and never clears them, so a reused buffer that was not cleared on return would carry the
        /// first scene's vertices into the second — silent corruption.
        /// <para>
        /// This drives the <b>real</b> <see cref="MeshOutputPool"/> reset path: rent → run scene A →
        /// <c>Return</c> (which clears) → rent the same instance back → run scene B, then compare against
        /// a fresh-buffer scene B via <see cref="MeshAssert.OutputsEqual"/> (the same differential B5/B7
        /// use). Without the <c>ClearForReuse</c> in <c>MeshOutputPool.Return</c> this is expected to fail
        /// (scene A's verts leak in), so it is a genuine regression guard, not a tautology.
        /// </para>
        /// <para>
        /// Positive control: scene A and scene B emit a <i>different</i> vertex count, so the reused
        /// buffer genuinely held foreign data before reuse — a no-op reset could not pass both the
        /// positive control and the equality check.
        /// </para>
        /// </summary>
        private static bool B17_PooledOutputStaleDataGuard()
        {
            MeshOutputPool pool = new MeshOutputPool();
            try
            {
                // Scene A primes the pooled buffer with real geometry (a structured opaque/oriented/
                // transparent mix — the densest of the two scenes).
                MeshDataJobOutput pooled = pool.Rent();
                using (MeshingTestWorld worldA = new MeshingTestWorld())
                {
                    BuildSceneA(worldA);
                    worldA.Run(reuseOutput: pooled);
                }

                int sceneAVerts = pooled.Vertices.Length;

                // Return (clears, retains capacity) then rent the same instance straight back — the MR-6
                // reset is the only thing between scene A's data and scene B's run.
                pool.Return(pooled);
                MeshDataJobOutput reused = pool.Rent();

                using (MeshingTestWorld worldB = new MeshingTestWorld())
                {
                    BuildSceneB(worldB);
                    worldB.Run(reuseOutput: reused);
                }

                // Fresh-buffer scene B is the equality oracle.
                using MeshingTestWorld worldFresh = new MeshingTestWorld();
                BuildSceneB(worldFresh);
                MeshDataJobOutput fresh = worldFresh.Run();

                bool passed = MeshAssert.IsTrue("B17 reused buffer emitted scene B", reused.Vertices.Length > 0,
                    $"reused verts = {reused.Vertices.Length} (expected > 0)");
                passed &= MeshAssert.IsTrue("B17 scenes differ (positive control)", sceneAVerts != fresh.Vertices.Length,
                    $"scene A verts {sceneAVerts} vs scene B verts {fresh.Vertices.Length} (must differ so the buffer truly held foreign data)");
                passed &= MeshAssert.OutputsEqual("B17 reused==fresh", reused, fresh);

                pool.Return(reused); // return before the pool is disposed
                return passed;
            }
            finally
            {
                pool.Dispose();
            }
        }

        /// <summary>Scene A for B17: a spaced opaque/oriented/transparent mix (the B5 structured set).</summary>
        private static void BuildSceneA(MeshingTestWorld world)
        {
            world.SetBlock(4, 6, 4, TestMeshBlockPalette.SolidOpaque);
            world.SetBlock(6, 6, 4, TestMeshBlockPalette.OrientedOpaque, meta: 2); // West
            world.SetBlock(8, 6, 4, TestMeshBlockPalette.TransparentCube);
        }

        /// <summary>
        /// Scene B for B17: a different, smaller set (one opaque + one transparent cube) so its vertex
        /// count differs from scene A — making the stale-reuse positive control non-vacuous.
        /// </summary>
        private static void BuildSceneB(MeshingTestWorld world)
        {
            world.SetBlock(8, 8, 8, TestMeshBlockPalette.SolidOpaque);
            world.SetBlock(10, 8, 8, TestMeshBlockPalette.TransparentCube);
        }

        /// <summary>Formats a <see cref="Color32"/> as <c>(r,g,b,a)</c> for assertion diagnostics.</summary>
        private static string FmtColor(Color32 c) => $"({c.r},{c.g},{c.b},{c.a})";

        /// <summary>One emitted quad's per-vertex streams, copied out of a (soon-disposed) job output.</summary>
        private sealed class ProbeQuad
        {
            public readonly Vector3[] Verts = new Vector3[4];
            public readonly Vector3[] Normals = new Vector3[4];
            public readonly Vector4[] Uvs = new Vector4[4];
            public readonly Color[] Colors = new Color[4];
            public readonly Color32[] Light = new Color32[4];
        }

        /// <summary>Places a fluid source ringed by solid opaque blocks on its four horizontal sides.</summary>
        private static void PlaceCardinalWalledFluid(MeshingTestWorld world, Vector3Int at)
        {
            world.SetBlock(at.x, at.y, at.z, TestMeshBlockPalette.WaterSource);
            world.SetBlock(at.x + 1, at.y, at.z, TestMeshBlockPalette.SolidOpaque);
            world.SetBlock(at.x - 1, at.y, at.z, TestMeshBlockPalette.SolidOpaque);
            world.SetBlock(at.x, at.y, at.z + 1, TestMeshBlockPalette.SolidOpaque);
            world.SetBlock(at.x, at.y, at.z - 1, TestMeshBlockPalette.SolidOpaque);
        }

        /// <summary>
        /// Places a fluid source fully encased in solid opaque blocks (the entire 3×3×3 shell around it).
        /// This drives solid state into <b>every</b> one of the fluid mesher's 14 neighbor slots —
        /// cardinals, horizontal diagonals, and the above/below set — so a leak into any slot is primed.
        /// </summary>
        private static void PlaceEncasedFluid(MeshingTestWorld world, Vector3Int at)
        {
            world.SetBlock(at.x, at.y, at.z, TestMeshBlockPalette.WaterSource);
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dy == 0 && dz == 0) continue;
                world.SetBlock(at.x + dx, at.y + dy, at.z + dz, TestMeshBlockPalette.SolidOpaque);
            }
        }

        /// <summary>
        /// Copies out every quad whose four vertices lie within the probe block's unit cell
        /// [x,x+1] × [y,y+1] × [z,z+1] (all three axes, so it cannot confuse a face one block away), in
        /// emission order. The data is copied because the owning <see cref="MeshingTestWorld"/> disposes
        /// its output when its <c>using</c> block exits.
        /// </summary>
        private static List<ProbeQuad> CollectProbeQuads(MeshDataJobOutput o, Vector3Int probe)
        {
            List<ProbeQuad> quads = new List<ProbeQuad>();
            int quadCount = o.Vertices.Length / 4;
            const float e = MeshAssert.VertexEpsilon;

            for (int q = 0; q < quadCount; q++)
            {
                int b = q * 4;
                bool inCell = true;
                for (int i = 0; i < 4 && inCell; i++)
                {
                    Vector3 v = o.Vertices[b + i];
                    inCell = v.x >= probe.x - e && v.x <= probe.x + 1f + e
                                                && v.y >= probe.y - e && v.y <= probe.y + 1f + e
                                                && v.z >= probe.z - e && v.z <= probe.z + 1f + e;
                }

                if (!inCell) continue;

                ProbeQuad pq = new ProbeQuad();
                for (int i = 0; i < 4; i++)
                {
                    pq.Verts[i] = o.Vertices[b + i];
                    pq.Normals[i] = o.Normals[b + i];
                    pq.Uvs[i] = (float4)o.Uvs[b + i]; // MR-2: half4 → float4 → Vector4
                    pq.Colors[i] = o.Colors[b + i]; // MR-2: Color32 → Color (implicit)
                    pq.Light[i] = o.LightData[b + i];
                }

                quads.Add(pq);
            }

            return quads;
        }

        /// <summary>Returns the shore mask (<c>Color.g</c>) of the probe's upward-facing quad, if present.</summary>
        private static bool TryTopFaceShoreMask(List<ProbeQuad> quads, out float shoreMask)
        {
            foreach (ProbeQuad q in quads)
            {
                if (Vector3.Distance(q.Normals[0], Vector3.up) <= MeshAssert.VertexEpsilon)
                {
                    shoreMask = q.Colors[0].g;
                    return true;
                }
            }

            shoreMask = 0f;
            return false;
        }

        /// <summary>
        /// Asserts two probe-quad sets are element-for-element identical across every per-vertex stream
        /// (positions/normals within <see cref="MeshAssert.VertexEpsilon"/>; UVs, colors, and packed
        /// light exact). The probe is meshed by the same code in both worlds, so a mismatch means an
        /// external block leaked into its neighbor buffer.
        /// </summary>
        private static bool ProbeQuadsEqual(string label, List<ProbeQuad> a, List<ProbeQuad> b)
        {
            if (a.Count != b.Count)
            {
                Debug.LogError($"[FAIL] {label}: quad count {a.Count} vs {b.Count}.");
                return false;
            }

            const float e = MeshAssert.VertexEpsilon;
            for (int q = 0; q < a.Count; q++)
            {
                ProbeQuad x = a[q], y = b[q];
                for (int i = 0; i < 4; i++)
                {
                    if (Vector3.Distance(x.Verts[i], y.Verts[i]) > e ||
                        Vector3.Distance(x.Normals[i], y.Normals[i]) > e ||
                        x.Uvs[i] != y.Uvs[i] ||
                        x.Colors[i] != y.Colors[i] ||
                        !x.Light[i].Equals(y.Light[i]))
                    {
                        Debug.LogError($"[FAIL] {label}: quad {q} vertex {i} differs " +
                                       $"(pos {x.Verts[i]} vs {y.Verts[i]}, color {x.Colors[i]} vs {y.Colors[i]}).");
                        return false;
                    }
                }
            }

            Debug.Log($"[PASS] {label}: {a.Count} probe quads identical.");
            return true;
        }

        /// <summary>
        /// The axis-aligned bounds of the 16×16×16 section cell containing <paramref name="pos"/>, in
        /// chunk-local block units — the box MR-4's proposed constant section bounds would assign. X/Z
        /// span the full chunk width; Y spans the one section the block sits in.
        /// </summary>
        private static void SectionCellBounds(Vector3Int pos, out Vector3 min, out Vector3 max)
        {
            int section = pos.y / ChunkMath.SECTION_SIZE;
            min = new Vector3(0f, section * ChunkMath.SECTION_SIZE, 0f);
            max = new Vector3(VoxelData.ChunkWidth, (section + 1) * ChunkMath.SECTION_SIZE, VoxelData.ChunkWidth);
        }

        /// <summary>
        /// Compares all six faces of a single standard cube in the output against the oracle. For each
        /// world face it locates the emitted quad by matching normals rather than assuming emission
        /// order, so a future reorder of the face loop cannot silently misalign the comparison (an
        /// isolated cube emits 6 quads with 6 distinct axis normals, so the match is unique).
        /// </summary>
        private static bool CompareCubeFacesToOracle(string label, MeshDataJobOutput o, Vector3Int pos,
            byte orientation, float rotation, in BlockTypeJobData blockData)
        {
            Vector3[] expected = new Vector3[4];
            Vector4[] expectedUVs = new Vector4[4];
            bool allPassed = true;
            int quadCount = o.Vertices.Length / 4;

            for (int worldFace = 0; worldFace < 6; worldFace++)
            {
                int faceIndex = MeshOracle.TranslatedFace(worldFace, orientation);
                MeshOracle.ExpectedStandardCubeFace(faceIndex, rotation, pos, expected, out Vector3 normal);

                int matchStart = FindQuadByNormal(o.Normals, quadCount, normal);
                if (matchStart < 0)
                {
                    Debug.LogError($"[FAIL] {label} face {worldFace} (geom {faceIndex}): no emitted quad with normal {normal}.");
                    allPassed = false;
                    continue;
                }

                allPassed &= MeshAssert.QuadMatchesOracle(
                    $"{label} face {worldFace} (geom {faceIndex})", o.Vertices, o.Normals, matchStart, expected, normal);

                // MH-4: the same matched quad must carry the atlas UVs of the texture this geometry face
                // is assigned. The palette gives each face a distinct texture (Back=0 … Right=5), so a
                // face-translation or atlas-math regression shows up here as wrong UVs.
                int textureID = MeshOracle.ExpectedTextureIDForFace(blockData, faceIndex);
                MeshOracle.ExpectedFaceUVs(textureID, expectedUVs);
                allPassed &= MeshAssert.UVsMatch(
                    $"{label} face {worldFace} (geom {faceIndex}) UVs (tex {textureID})", o.Uvs, matchStart, expectedUVs);
            }

            return allPassed;
        }

        /// <summary>
        /// Returns the start vertex index of the emitted quad whose (shared) normal matches
        /// <paramref name="normal"/>, or -1 if none. Each standard-cube quad's 4 vertices carry one
        /// normal; an isolated cube emits 6 quads with 6 distinct axis normals, so the match is unique.
        /// </summary>
        private static int FindQuadByNormal(NativeList<Vector3> normals, int quadCount, Vector3 normal)
        {
            for (int q = 0; q < quadCount; q++)
            {
                if (Vector3.Distance(normals[q * 4], normal) <= MeshAssert.VertexEpsilon)
                    return q * 4;
            }

            return -1;
        }

        /// <summary>
        /// B22 — FL-1 sway-channel guard. Two cross-flora voxels must emit UV ZW sway data:
        /// Z (weight) exactly 1 on every top (y = pos.y + 1) vertex and exactly 0 on every bottom
        /// vertex, W (phase) identical across one voxel's 16 verts, inside [0, 1], different between
        /// the two cells (anti-constant-hash), and bit-identical across two runs (determinism).
        /// A standard opaque cube in a separate scene must keep ZW = 0 on all verts, proving the
        /// sway overload never leaks into non-flora emission paths.
        /// </summary>
        private static bool B22_CrossMeshSwayChannels()
        {
            using MeshingTestWorld world = new MeshingTestWorld();
            Vector3Int posA = new Vector3Int(8, 8, 8);
            Vector3Int posB = new Vector3Int(4, 8, 4);
            world.SetBlock(posA.x, posA.y, posA.z, TestMeshBlockPalette.CrossFlora);
            world.SetBlock(posB.x, posB.y, posB.z, TestMeshBlockPalette.CrossFlora);
            MeshDataJobOutput o = world.Run();

            bool passed = MeshAssert.VertexCount("B22 vertex count (2 cross voxels)", o, 32);
            passed &= MeshAssert.StructuralInvariants("B22 structural", o);
            if (o.Vertices.Length != 32) return false;

            passed &= MeshAssert.IsTrue("B22 cross routes all triangles to the transparent submesh",
                o.Triangles.Length == 0 && o.FluidTriangles.Length == 0 && o.TransparentTriangles.Length == 48,
                $"opaque={o.Triangles.Length} fluid={o.FluidTriangles.Length} transparent={o.TransparentTriangles.Length} (expected 0/0/48)");

            passed &= CheckCrossSwayChannels("B22 voxel A", o, posA, out float phaseA);
            passed &= CheckCrossSwayChannels("B22 voxel B", o, posB, out float phaseB);

            // Anti-constant guard: a hash that collapsed to one value would make every tuft sway in
            // lockstep — and would false-green the per-voxel determinism checks above.
            passed &= MeshAssert.IsTrue("B22 phase differs between cells", phaseA != phaseB,
                $"phaseA={phaseA:G6} phaseB={phaseB:G6}");

            // Determinism: a second run over the same map must reproduce the UV stream bit-identically
            // (the phase hash must depend on nothing but the voxel cell).
            half4[] firstUvs = o.Uvs.AsArray().ToArray();
            MeshDataJobOutput o2 = world.Run();
            bool uvsIdentical = o2.Uvs.Length == firstUvs.Length;
            if (uvsIdentical)
            {
                for (int i = 0; i < firstUvs.Length; i++)
                {
                    if (!firstUvs[i].Equals(o2.Uvs[i]))
                    {
                        uvsIdentical = false;
                        break;
                    }
                }
            }

            passed &= MeshAssert.IsTrue("B22 UV stream deterministic across runs", uvsIdentical,
                $"run1 count={firstUvs.Length} run2 count={o2.Uvs.Length}");

            // Non-flora guard: a plain opaque cube keeps ZW = 0 on every vertex — the sway overload
            // must never leak into the standard-cube emission path.
            using MeshingTestWorld cubeWorld = new MeshingTestWorld();
            cubeWorld.SetBlock(8, 8, 8, TestMeshBlockPalette.SolidOpaque);
            MeshDataJobOutput oc = cubeWorld.Run();
            bool cubeZeroZw = true;
            for (int i = 0; i < oc.Uvs.Length; i++)
            {
                if (oc.Uvs[i].z != 0f || oc.Uvs[i].w != 0f)
                {
                    cubeZeroZw = false;
                    break;
                }
            }

            passed &= MeshAssert.IsTrue("B22 standard cube keeps UV ZW = 0", cubeZeroZw,
                $"checked {oc.Uvs.Length} verts");

            return passed;
        }

        /// <summary>
        /// Verifies one cross-flora voxel's 16 verts: weight (UV Z) is exactly 1 on top verts and 0 on
        /// bottom verts, and phase (UV W) is a single value shared by all 16, inside [0, 1] (half
        /// rounding may land a phase just below 1 exactly on 1 — functionally equivalent, sin is 2π-periodic).
        /// </summary>
        /// <param name="label">Assertion label prefix.</param>
        /// <param name="o">The meshing output containing the voxel's verts.</param>
        /// <param name="pos">The voxel's chunk-local cell.</param>
        /// <param name="phase">The voxel's shared phase value (NaN when verts are missing/mismatched).</param>
        private static bool CheckCrossSwayChannels(string label, MeshDataJobOutput o, Vector3Int pos, out float phase)
        {
            phase = float.NaN;
            int vertsSeen = 0;
            bool weightsOk = true, phaseUniform = true;

            for (int i = 0; i < o.Vertices.Length; i++)
            {
                Vector3 v = o.Vertices[i];
                // Cross verts sit on the cell's corners/edges: x/z in {pos, pos+1}, y in {pos, pos+1}.
                if (v.x < pos.x || v.x > pos.x + 1 || v.z < pos.z || v.z > pos.z + 1 ||
                    v.y < pos.y || v.y > pos.y + 1)
                    continue;

                vertsSeen++;
                float weight = o.Uvs[i].z;
                bool isTop = Mathf.Approximately(v.y, pos.y + 1);
                if (weight != (isTop ? 1f : 0f)) weightsOk = false;

                float w = o.Uvs[i].w;
                if (float.IsNaN(phase)) phase = w;
                else if (w != phase) phaseUniform = false;
            }

            bool passed = MeshAssert.IsTrue($"{label}: 16 verts found in cell", vertsSeen == 16, $"found {vertsSeen}");
            passed &= MeshAssert.IsTrue($"{label}: weight 1 on top verts, 0 on bottom verts", weightsOk, "UV Z split mismatch");
            passed &= MeshAssert.IsTrue($"{label}: phase uniform across the voxel", phaseUniform, $"first phase={phase:G6}");
            passed &= MeshAssert.IsTrue($"{label}: phase within [0, 1]", phase >= 0f && phase <= 1f, $"phase={phase:G6}");
            return passed;
        }

        /// <summary>
        /// B23 — FL-2 cube-shimmer guard. A sway-flagged transparent cube (leaf-like) must carry its
        /// authored <c>swayStrength</c> in UV Z and one deterministic per-voxel phase in UV W on
        /// <b>every</b> emitted vertex (uniform — cubes shimmer whole, unlike FL-1's rooted crosses);
        /// two cells get distinct phases; a zero-strength transparent cube in the same scene keeps
        /// ZW = 0, proving the post-pass keys off the authored strength, not the submesh.
        /// </summary>
        private static bool B23_CubeSwayChannels()
        {
            using MeshingTestWorld world = new MeshingTestWorld();
            Vector3Int posA = new Vector3Int(8, 8, 8);
            Vector3Int posB = new Vector3Int(4, 8, 4);
            Vector3Int posPlain = new Vector3Int(12, 8, 12); // zero-strength control, same scene
            world.SetBlock(posA.x, posA.y, posA.z, TestMeshBlockPalette.SwayingLeafCube);
            world.SetBlock(posB.x, posB.y, posB.z, TestMeshBlockPalette.SwayingLeafCube);
            world.SetBlock(posPlain.x, posPlain.y, posPlain.z, TestMeshBlockPalette.TransparentCube);
            MeshDataJobOutput o = world.Run();

            // 3 isolated cubes × 24 verts (renderNeighborFaces blocks don't cull against air).
            bool passed = MeshAssert.VertexCount("B23 vertex count (3 cubes)", o, 72);
            passed &= MeshAssert.StructuralInvariants("B23 structural", o);
            if (o.Vertices.Length != 72) return false;

            passed &= CheckCubeSwayChannels("B23 sway cube A", o, posA, TestMeshBlockPalette.SwayStrength, out float phaseA);
            passed &= CheckCubeSwayChannels("B23 sway cube B", o, posB, TestMeshBlockPalette.SwayStrength, out float phaseB);
            passed &= CheckCubeSwayChannels("B23 plain transparent cube", o, posPlain, expectedWeight: 0f, out float phasePlain);

            passed &= MeshAssert.IsTrue("B23 phase differs between cells", phaseA != phaseB,
                $"phaseA={phaseA:G6} phaseB={phaseB:G6}");
            passed &= MeshAssert.IsTrue("B23 zero-strength cube keeps phase channel 0", phasePlain == 0f,
                $"phase={phasePlain:G6}");

            // Determinism: a second run must reproduce the UV stream bit-identically.
            half4[] firstUvs = o.Uvs.AsArray().ToArray();
            MeshDataJobOutput o2 = world.Run();
            bool uvsIdentical = o2.Uvs.Length == firstUvs.Length;
            if (uvsIdentical)
            {
                for (int i = 0; i < firstUvs.Length; i++)
                {
                    if (!firstUvs[i].Equals(o2.Uvs[i]))
                    {
                        uvsIdentical = false;
                        break;
                    }
                }
            }

            passed &= MeshAssert.IsTrue("B23 UV stream deterministic across runs", uvsIdentical,
                $"run1 count={firstUvs.Length} run2 count={o2.Uvs.Length}");
            return passed;
        }

        /// <summary>
        /// Verifies one cube voxel's 24 verts carry <paramref name="expectedWeight"/> in UV Z on every
        /// vertex and a single shared UV W phase in [0, 1] (identical across the voxel).
        /// </summary>
        /// <param name="label">Assertion label prefix.</param>
        /// <param name="o">The meshing output containing the voxel's verts.</param>
        /// <param name="pos">The voxel's chunk-local cell.</param>
        /// <param name="expectedWeight">The UV Z value every vert must carry (half-rounded exact).</param>
        /// <param name="phase">The voxel's shared phase value (NaN when verts are missing/mismatched).</param>
        private static bool CheckCubeSwayChannels(string label, MeshDataJobOutput o, Vector3Int pos,
            float expectedWeight, out float phase)
        {
            phase = float.NaN;
            int vertsSeen = 0;
            bool weightsOk = true, phaseUniform = true;
            float expectedHalf = (half)expectedWeight;

            for (int i = 0; i < o.Vertices.Length; i++)
            {
                Vector3 v = o.Vertices[i];
                if (v.x < pos.x || v.x > pos.x + 1 || v.z < pos.z || v.z > pos.z + 1 ||
                    v.y < pos.y || v.y > pos.y + 1)
                    continue;

                vertsSeen++;
                if (o.Uvs[i].z != expectedHalf) weightsOk = false;

                float w = o.Uvs[i].w;
                if (float.IsNaN(phase)) phase = w;
                else if (w != phase) phaseUniform = false;
            }

            bool passed = MeshAssert.IsTrue($"{label}: 24 verts found in cell", vertsSeen == 24, $"found {vertsSeen}");
            passed &= MeshAssert.IsTrue($"{label}: UV Z == {expectedWeight:G4} on every vert", weightsOk, "UV Z mismatch");
            passed &= MeshAssert.IsTrue($"{label}: phase uniform across the voxel", phaseUniform, $"first phase={phase:G6}");
            passed &= MeshAssert.IsTrue($"{label}: phase within [0, 1]", phase >= 0f && phase <= 1f, $"phase={phase:G6}");
            return passed;
        }
    }
}
