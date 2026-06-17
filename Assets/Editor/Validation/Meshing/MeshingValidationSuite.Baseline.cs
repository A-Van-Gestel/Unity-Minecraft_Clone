using System.Collections.Generic;
using Data;
using Editor.Validation.Meshing.Framework;
using Helpers;
using Jobs.BurstData;
using Unity.Collections;
using UnityEngine;

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
        }

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
                    NativeList<Vector4> uvs = new NativeList<Vector4>(4, Allocator.Temp);
                    NativeList<Color> colors = new NativeList<Color>(4, Allocator.Temp);
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

            passed &= CompareCubeFacesToOracle("B2", o, pos, orientation: VoxelOrientation.North, rotation: 0f);

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
                    passed &= CompareCubeFacesToOracle($"B4 yaw {yaw}", o, pos, orientation, rotation);
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
                if (o.SectionStats[s].VertexCount > 0) emittingSections++;
            passed &= MeshAssert.IsTrue("B9 multi-section coverage", emittingSections == cubes.Length,
                $"sections emitting geometry = {emittingSections} (expected {cubes.Length}, else the tiling check is vacuous)");

            return passed;
        }

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
                    pq.Uvs[i] = o.Uvs[b + i];
                    pq.Colors[i] = o.Colors[b + i];
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
            byte orientation, float rotation)
        {
            Vector3[] expected = new Vector3[4];
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
    }
}
