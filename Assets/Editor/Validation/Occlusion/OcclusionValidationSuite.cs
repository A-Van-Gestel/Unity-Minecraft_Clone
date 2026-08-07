using System.Collections.Generic;
using System.Text;
using Data;
using Editor.Validation.Framework;
using Helpers;
using Jobs.BurstData;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.Occlusion
{
    /// <summary>
    /// Validation suite for per-face block-shape coverage (<see cref="BurstOcclusionUtility"/>) — the
    /// foundation the <c>VO-*</c> arc builds directional light occlusion on (see
    /// <c>Documentation/Design/VOXEL_OCCLUSION_REFACTOR.md</c>).
    /// <para>
    /// All scenarios are baselines (must stay green). The known-bug channel is registered for parity with
    /// the other suites but is unused today.
    /// </para>
    /// <para>
    /// Fixtures are synthetic <see cref="BlockType"/> instances built in-file rather than read from
    /// <c>BlockDatabase.asset</c>, so the suite stays deterministic under database edits — the same
    /// convention the lighting/meshing palettes use.
    /// </para>
    /// </summary>
    public static class OcclusionValidationSuite
    {
        /// <summary>Face indices in <c>VoxelData.FaceChecks</c> order, for readable expectations.</summary>
        private const int BACK = 0, FRONT = 1, TOP = 2, BOTTOM = 3, LEFT = 4, RIGHT = 5;

        /// <summary>Tolerance for comparing coverage fractions and bounds components.</summary>
        private const float EPSILON = 1e-4f;

        /// <summary>Runs every registered scenario and prints a categorized summary.</summary>
        [MenuItem("Minecraft Clone/Dev/Validate Occlusion")]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the occlusion scenarios, returning the categorized result (the headless/CI entry point).
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result.</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar.</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>
            {
                new Scenario("B1: an unrotated half slab covers its bottom face fully, its top face not at all, and its four sides by half", B1_IdentityOrientation),
                new Scenario("B2: a vertical half slab leaves the face its open half points at completely uncovered (the VO motivating case)", B2_VerticalOrientation),
                new Scenario("B3: a full-block type covers every face completely, for every orientation", B3_FullBlockControl),
                new Scenario("B4: across all 24 Facing6Roll2 orientations a half slab always has exactly one full face, one empty face, and they are opposite", B4_AllOrientationsStructure),
                new Scenario("B5: the managed collision path and the Burst occlusion core report the same volume for all 24 orientations", B5_ManagedAndBurstAgree),
            };

            return ValidationSuiteRunner.Execute("Voxel Occlusion", scenarios, KnownBugChannel.Bug, logToConsole, showProgress);
        }

        /// <summary>
        /// B1 — the authored (unrotated) half slab. <see cref="MetadataSchema.Facing6Roll2"/> facing 0
        /// (South) with roll 0 is the identity matrix, so metadata <c>0x00</c> leaves the authored bounds
        /// <c>(0,0,0)→(1,0.5,1)</c> untouched: the slab sits on the cell floor.
        /// <para>Prove-red: drop the <c>touches</c> test in <see cref="BurstOcclusionUtility.GetFaceCoverage"/>
        /// and the Top face returns 0.5 instead of 0.</para>
        /// </summary>
        /// <returns>True when all six faces match.</returns>
        private static bool B1_IdentityOrientation()
        {
            BlockType slab = MakeHalfSlab();
            float[] expected = new float[6];
            expected[BACK] = 0.5f;
            expected[FRONT] = 0.5f;
            expected[TOP] = 0f; // the mid-plane face — the volume stops at y=0.5
            expected[BOTTOM] = 1f; // resting on the cell floor
            expected[LEFT] = 0.5f;
            expected[RIGHT] = 0.5f;
            return AssertCoverage("B1 identity (meta 0x00)", slab, meta: 0x00, expected);
        }

        /// <summary>
        /// B2 — the reported screenshot's "Left" slab: facing 3 (Bottom), roll 0, metadata <c>0x03</c>.
        /// That rotation stands the slab up against the +Z half of its cell, so its −Z face is
        /// completely uncovered — the case that motivates the whole VO arc, because no scalar opacity
        /// value can express "blocks through one face, passes through the opposite one".
        /// <para>Prove-red: force <c>GetFaceCoverage</c> to ignore the rotation and the −Z face reports
        /// 0.5 (the identity value) instead of 0.</para>
        /// </summary>
        /// <returns>True when all six faces match.</returns>
        private static bool B2_VerticalOrientation()
        {
            BlockType slab = MakeHalfSlab();
            float[] expected = new float[6];
            expected[BACK] = 0f; // open half — light must pass
            expected[FRONT] = 1f; // solid half
            expected[TOP] = 0.5f;
            expected[BOTTOM] = 0.5f;
            expected[LEFT] = 0.5f;
            expected[RIGHT] = 0.5f;
            return AssertCoverage("B2 vertical (meta 0x03)", slab, meta: 0x03, expected);
        }

        /// <summary>
        /// B3 — control: a block without custom bounds must report full coverage on every face for every
        /// orientation, which is what keeps full cubes bit-identical when callers adopt this utility.
        /// Doubles as the positive control for B1/B2 — without it, an implementation that returned 1
        /// everywhere would look correct on the "full" rows alone.
        /// </summary>
        /// <returns>True when every face of every orientation reports 1.</returns>
        private static bool B3_FullBlockControl()
        {
            BlockType cube = MakeBlock("TestFullCube", BlockCollisionBounds.FullBlock);
            BlockTypeJobData data = new BlockTypeJobData(cube);
            StringBuilder failures = new StringBuilder();

            for (int meta = 0; meta < 24; meta++)
            {
                byte raw = BurstVoxelMetadataUtility.EncodeFacing6Roll2((byte)(meta % 6), (byte)(meta / 6));
                for (int face = 0; face < 6; face++)
                {
                    float actual = BurstOcclusionUtility.GetBlockFaceCoverage(in data, raw, face);
                    if (math.abs(actual - 1f) > EPSILON)
                        failures.AppendLine($"    meta 0x{raw:X2} face {face}: expected 1, got {actual:F4}");
                }
            }

            return Report("B3 full-block control", failures);
        }

        /// <summary>
        /// B4 — structural invariant that holds for any half slab under any 90° rotation, asserted without
        /// naming which face lands where: exactly one face is fully covered, exactly one is completely
        /// uncovered, those two are opposite, and the remaining four are half covered. This catches a
        /// rotation bug that permutes faces consistently (which per-orientation expectations could miss)
        /// and needs no hand-derived table for the other 22 orientations.
        /// </summary>
        /// <returns>True when the invariant holds for all 24 orientations.</returns>
        private static bool B4_AllOrientationsStructure()
        {
            BlockType slab = MakeHalfSlab();
            BlockTypeJobData data = new BlockTypeJobData(slab);
            StringBuilder failures = new StringBuilder();

            for (int facing = 0; facing < 6; facing++)
            {
                for (int roll = 0; roll < 4; roll++)
                {
                    byte raw = BurstVoxelMetadataUtility.EncodeFacing6Roll2((byte)facing, (byte)roll);
                    int fullFace = -1, emptyFace = -1, halfCount = 0;

                    for (int face = 0; face < 6; face++)
                    {
                        float c = BurstOcclusionUtility.GetBlockFaceCoverage(in data, raw, face);
                        if (math.abs(c - 1f) <= EPSILON) fullFace = fullFace < 0 ? face : -2;
                        else if (math.abs(c) <= EPSILON) emptyFace = emptyFace < 0 ? face : -2;
                        else if (math.abs(c - 0.5f) <= EPSILON) halfCount++;
                        else failures.AppendLine($"    meta 0x{raw:X2} face {face}: coverage {c:F4} is none of 0 / 0.5 / 1");
                    }

                    if (fullFace < 0 || emptyFace < 0)
                        failures.AppendLine($"    meta 0x{raw:X2}: expected exactly one full and one empty face, got full={fullFace} empty={emptyFace}");
                    else if (OppositeFace(fullFace) != emptyFace)
                        failures.AppendLine($"    meta 0x{raw:X2}: full face {fullFace} and empty face {emptyFace} are not opposite");

                    if (halfCount != 4)
                        failures.AppendLine($"    meta 0x{raw:X2}: expected 4 half-covered faces, got {halfCount}");
                }
            }

            return Report("B4 all-orientation structure", failures);
        }

        /// <summary>
        /// B5 — the VO-1 consolidation guard. <see cref="BlockCollisionBoundsUtility.GetBounds"/> (the
        /// managed physics / placement / interaction-ray path) now delegates its rotation to
        /// <see cref="BurstOcclusionUtility.RotateLocalBounds"/>; this asserts the two really do describe
        /// the same volume, so the shared core cannot silently drift from what <c>NS-4</c> guards.
        /// <para>Prove-red: transpose the matrix inside <c>RotateLocalBounds</c> — this reds (and so do the
        /// <c>NS-4</c> baselines), while B1/B2's authored expectations also move.</para>
        /// </summary>
        /// <returns>True when both paths agree for all 24 orientations.</returns>
        private static bool B5_ManagedAndBurstAgree()
        {
            BlockType slab = MakeHalfSlab();
            StringBuilder failures = new StringBuilder();
            Vector3 origin = new Vector3(3f, 7f, -2f); // arbitrary non-zero cell, to catch re-spacing errors

            for (int facing = 0; facing < 6; facing++)
            {
                for (int roll = 0; roll < 4; roll++)
                {
                    byte raw = BurstVoxelMetadataUtility.EncodeFacing6Roll2((byte)facing, (byte)roll);

                    Bounds managed = BlockCollisionBoundsUtility.GetBounds(slab, raw, origin);

                    float3x3 rot = BurstCustomMeshRotationUtility.GetRotationMatrix(
                        slab.metadataSchema, raw, slab.defaultMetadata);
                    BurstOcclusionUtility.RotateLocalBounds(slab.collisionBounds.min, slab.collisionBounds.max,
                        in rot, out float3 rMin, out float3 rMax);

                    Vector3 expectedMin = new Vector3(rMin.x, rMin.y, rMin.z) + origin;
                    Vector3 expectedMax = new Vector3(rMax.x, rMax.y, rMax.z) + origin;

                    if (Vector3.Distance(managed.min, expectedMin) > EPSILON
                        || Vector3.Distance(managed.max, expectedMax) > EPSILON)
                    {
                        failures.AppendLine($"    meta 0x{raw:X2}: managed [{managed.min:F3} .. {managed.max:F3}] "
                                            + $"vs core [{expectedMin:F3} .. {expectedMax:F3}]");
                    }
                }
            }

            return Report("B5 managed/Burst agreement", failures);
        }

        /// <summary>Asserts a block's coverage on all six faces against an expected table.</summary>
        /// <param name="label">Scenario label for console output.</param>
        /// <param name="blockType">The block fixture.</param>
        /// <param name="meta">The raw metadata byte selecting the orientation.</param>
        /// <param name="expected">Expected coverage per face, indexed in <c>FaceChecks</c> order.</param>
        /// <returns>True when every face matches.</returns>
        private static bool AssertCoverage(string label, BlockType blockType, byte meta, float[] expected)
        {
            BlockTypeJobData data = new BlockTypeJobData(blockType);
            StringBuilder failures = new StringBuilder();

            for (int face = 0; face < 6; face++)
            {
                float actual = BurstOcclusionUtility.GetBlockFaceCoverage(in data, meta, face);
                if (math.abs(actual - expected[face]) > EPSILON)
                    failures.AppendLine($"    face {face} ({FaceName(face)}): expected {expected[face]:F4}, got {actual:F4}");
            }

            return Report(label, failures);
        }

        /// <summary>Logs a scenario's outcome and returns whether it passed.</summary>
        /// <param name="label">Scenario label.</param>
        /// <param name="failures">Accumulated failure lines; empty means pass.</param>
        /// <returns>True when there were no failures.</returns>
        private static bool Report(string label, StringBuilder failures)
        {
            if (failures.Length == 0)
            {
                Debug.Log($"[PASS] {label}");
                return true;
            }

            Debug.LogError($"[FAIL] {label}:\n{failures}");
            return false;
        }

        /// <summary>Returns the face index opposite the given one, in <c>FaceChecks</c> order.</summary>
        /// <param name="face">The face index.</param>
        /// <returns>Its opposite.</returns>
        private static int OppositeFace(int face) => face switch
        {
            BACK => FRONT,
            FRONT => BACK,
            TOP => BOTTOM,
            BOTTOM => TOP,
            LEFT => RIGHT,
            _ => LEFT,
        };

        /// <summary>Returns a readable name for a face index.</summary>
        /// <param name="face">The face index.</param>
        /// <returns>The face's name.</returns>
        private static string FaceName(int face) => face switch
        {
            BACK => "Back -Z",
            FRONT => "Front +Z",
            TOP => "Top +Y",
            BOTTOM => "Bottom -Y",
            LEFT => "Left -X",
            _ => "Right +X",
        };

        /// <summary>
        /// The suite's primary fixture: a bottom half slab on the <see cref="MetadataSchema.Facing6Roll2"/>
        /// schema, matching the production <c>Stone Half Slab</c>'s authored bounds (verified by the VO-0
        /// probe: <c>min=(0,0,0)</c>, <c>max=(1,0.5,1)</c>).
        /// </summary>
        /// <returns>The fixture block type.</returns>
        private static BlockType MakeHalfSlab()
        {
            BlockType block = MakeBlock("TestHalfSlab", BlockCollisionBounds.BottomHalfSlab);
            block.renderShape = RenderShape.CustomMesh;
            return block;
        }

        /// <summary>Builds a minimal solid block fixture with the given collision volume.</summary>
        /// <param name="name">Block name.</param>
        /// <param name="bounds">The authored collision bounds.</param>
        /// <returns>The fixture block type.</returns>
        private static BlockType MakeBlock(string name, BlockCollisionBounds bounds)
        {
            return new BlockType
            {
                blockName = name,
                isSolid = true,
                opacity = 15,
                collisionBounds = bounds,
                metadataSchema = MetadataSchema.Facing6Roll2,
                defaultMetadata = 0,
                renderShape = RenderShape.Cube,
            };
        }
    }
}
