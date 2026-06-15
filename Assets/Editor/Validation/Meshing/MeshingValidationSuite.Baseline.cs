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
