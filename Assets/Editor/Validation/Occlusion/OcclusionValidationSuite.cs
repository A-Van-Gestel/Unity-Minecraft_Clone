using System.Collections.Generic;
using System.Text;
using Data;
using Editor.Dev;
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
        [MenuItem("Minecraft Clone/Dev/Validate Occlusion", priority = DevMenuPriority.Validation)]
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
                new Scenario("B6: a face silhouette is the shape's own rectangle — full cube, slab, post and every roll — and its area still reproduces GetFaceCoverage exactly (SS-1)", B6_FaceSilhouette),
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

        /// <summary>
        /// B6 — the <c>SS-1</c> primitive. <see cref="BurstOcclusionUtility.GetFaceSilhouette"/> must
        /// report <i>where</i> a volume sits on a face, not merely how much of it is filled, because a
        /// contact shadow measures distance to that rectangle.
        /// <list type="bullet">
        /// <item><b>Shape-derived, with no per-shape code.</b> A full cube projects the unit square on
        /// every face; a bottom slab's mid-plane face is not reached at all; a vertical slab covers half
        /// its top face; a <b>post</b> projects a small central square on <c>±Y</c> and reaches no side
        /// wall. The post is the case that matters — it is the only fixture whose silhouette is neither
        /// the whole face nor a clean half.</item>
        /// <item><b>Rotation is real.</b> Rolling a vertical slab through its four rolls must move the
        /// silhouette to four <i>distinct</i> halves of the top face. Asserted structurally rather than
        /// against a hand-derived table, like B4 — and this is the leg that earns its keep: <c>VO-1</c>'s
        /// prove-red established that a transposed rotation leaves symmetric cases green and is caught
        /// only by an asymmetric one (finding <b>F10</b>).</item>
        /// <item><b>It is a strict generalization.</b> The rectangle's area must equal
        /// <see cref="BurstOcclusionUtility.GetFaceCoverage"/> <b>exactly</b>, for every fixture, face and
        /// orientation. That is what licenses leaving <c>GetFaceCoverage</c> in place rather than
        /// re-expressing it through the new primitive: the two cannot drift without this going red, and
        /// consolidating instead would put a last-ulp change into the light-transport path for the sake
        /// of one multiply.</item>
        /// </list>
        /// </summary>
        /// <returns>True when every silhouette matches and every area reproduces the coverage.</returns>
        private static bool B6_FaceSilhouette()
        {
            StringBuilder failures = new StringBuilder();

            BlockTypeJobData cube = new BlockTypeJobData(MakeBlock("TestFullCube", BlockCollisionBounds.FullBlock));
            BlockTypeJobData slab = new BlockTypeJobData(MakeHalfSlab());
            BlockTypeJobData post = new BlockTypeJobData(MakePost());

            // A full cube covers every face completely, in every orientation — the degeneration check
            // that keeps ordinary terrain out of the rotation path.
            for (int meta = 0; meta < 24; meta++)
            {
                byte raw = BurstVoxelMetadataUtility.EncodeFacing6Roll2((byte)(meta % 6), (byte)(meta / 6));
                for (int face = 0; face < 6; face++)
                {
                    AssertSilhouette(failures, $"full cube meta 0x{raw:X2}", cube, raw, face,
                        expectTouches: true, new float2(0f, 0f), new float2(1f, 1f));
                }
            }

            // Bottom slab, identity: resting on the cell floor, so its mid-plane top face is unreached.
            AssertSilhouette(failures, "bottom slab", slab, 0x00, BOTTOM, true, new float2(0f, 0f), new float2(1f, 1f));
            AssertSilhouette(failures, "bottom slab", slab, 0x00, TOP, false, float2.zero, float2.zero);
            AssertSilhouette(failures, "bottom slab", slab, 0x00, BACK, true, new float2(0f, 0f), new float2(1f, 0.5f));

            // Vertical slab 0x03: solid half against +Z, so -Z is unreached and the top face is half covered.
            AssertSilhouette(failures, "vertical slab", slab, 0x03, BACK, false, float2.zero, float2.zero);
            AssertSilhouette(failures, "vertical slab", slab, 0x03, FRONT, true, new float2(0f, 0f), new float2(1f, 1f));
            AssertSilhouette(failures, "vertical slab", slab, 0x03, TOP, true, new float2(0f, 0.5f), new float2(1f, 1f));

            // The post: a central column touching floor and ceiling but neither side wall.
            AssertSilhouette(failures, "post", post, 0x00, TOP, true, new float2(0.375f, 0.375f), new float2(0.625f, 0.625f));
            AssertSilhouette(failures, "post", post, 0x00, BOTTOM, true, new float2(0.375f, 0.375f), new float2(0.625f, 0.625f));
            foreach (int side in new[] { BACK, FRONT, LEFT, RIGHT })
            {
                AssertSilhouette(failures, "post", post, 0x00, side, false, float2.zero, float2.zero);
            }

            AssertRollsMoveSilhouette(failures, slab);
            AssertAreaReproducesCoverage(failures, cube, "full cube");
            AssertAreaReproducesCoverage(failures, slab, "half slab");
            AssertAreaReproducesCoverage(failures, post, "post");

            return Report("B6 face silhouette", failures);
        }

        /// <summary>
        /// Asserts one face's silhouette against an expected rectangle (or against "does not touch").
        /// </summary>
        /// <param name="failures">Failure accumulator.</param>
        /// <param name="label">Fixture label for the failure text.</param>
        /// <param name="block">The block fixture's job data.</param>
        /// <param name="meta">Raw metadata byte selecting the orientation.</param>
        /// <param name="face">Face index, in <c>FaceChecks</c> order.</param>
        /// <param name="expectTouches">Whether the volume should reach that face's plane.</param>
        /// <param name="expectMin">Expected rectangle minimum, when it touches.</param>
        /// <param name="expectMax">Expected rectangle maximum, when it touches.</param>
        private static void AssertSilhouette(StringBuilder failures, string label, BlockTypeJobData block,
            byte meta, int face, bool expectTouches, float2 expectMin, float2 expectMax)
        {
            bool touches = LightAttenuation.AmbientOcclusionFaceSilhouette(in block, meta, face,
                out float2 rectMin, out float2 rectMax);

            if (touches != expectTouches)
            {
                failures.AppendLine($"    {label} face {face} ({FaceName(face)}): touches={touches}, expected {expectTouches}");
                return;
            }

            if (!expectTouches) return;

            if (math.any(math.abs(rectMin - expectMin) > EPSILON) || math.any(math.abs(rectMax - expectMax) > EPSILON))
            {
                failures.AppendLine($"    {label} face {face} ({FaceName(face)}): silhouette [{rectMin} .. {rectMax}], "
                                    + $"expected [{expectMin} .. {expectMax}]");
            }
        }

        /// <summary>
        /// Asserts that the four rolls of a vertical slab put its top-face silhouette on four distinct
        /// halves of that face — the asymmetric case a transposed rotation cannot survive (F10).
        /// </summary>
        /// <param name="failures">Failure accumulator.</param>
        /// <param name="slab">The half-slab fixture's job data.</param>
        private static void AssertRollsMoveSilhouette(StringBuilder failures, BlockTypeJobData slab)
        {
            List<float4> seen = new List<float4>();

            for (int roll = 0; roll < 4; roll++)
            {
                byte raw = BurstVoxelMetadataUtility.EncodeFacing6Roll2(3, (byte)roll); // facing 3 = Bottom
                if (!LightAttenuation.AmbientOcclusionFaceSilhouette(in slab, raw, TOP,
                        out float2 rectMin, out float2 rectMax))
                {
                    failures.AppendLine($"    vertical slab roll {roll}: top face reports no silhouette at all");
                    continue;
                }

                float area = (rectMax.x - rectMin.x) * (rectMax.y - rectMin.y);
                if (math.abs(area - 0.5f) > EPSILON)
                {
                    failures.AppendLine($"    vertical slab roll {roll}: top silhouette area {area:F4}, expected 0.5");
                }

                float4 rect = new float4(rectMin, rectMax);
                foreach (float4 previous in seen)
                {
                    if (math.all(math.abs(rect - previous) <= EPSILON))
                    {
                        failures.AppendLine($"    vertical slab roll {roll}: top silhouette [{rectMin} .. {rectMax}] "
                                            + "repeats an earlier roll's — rolling the block does not move it");
                    }
                }

                seen.Add(rect);
            }
        }

        /// <summary>
        /// Asserts the silhouette's area reproduces <see cref="BurstOcclusionUtility.GetFaceCoverage"/>
        /// exactly, across every face and orientation — the drift guard that lets the two coexist.
        /// </summary>
        /// <param name="failures">Failure accumulator.</param>
        /// <param name="block">The block fixture's job data.</param>
        /// <param name="label">Fixture label for the failure text.</param>
        private static void AssertAreaReproducesCoverage(StringBuilder failures, BlockTypeJobData block, string label)
        {
            for (int meta = 0; meta < 24; meta++)
            {
                byte raw = BurstVoxelMetadataUtility.EncodeFacing6Roll2((byte)(meta % 6), (byte)(meta / 6));
                for (int face = 0; face < 6; face++)
                {
                    float coverage = BurstOcclusionUtility.GetBlockFaceCoverage(in block, raw, face);
                    float area = LightAttenuation.AmbientOcclusionFaceSilhouette(in block, raw, face,
                        out float2 rectMin, out float2 rectMax)
                        ? (rectMax.x - rectMin.x) * (rectMax.y - rectMin.y)
                        : 0f;

                    // Bitwise, not approximate: the point is that the two computations cannot drift.
                    if (area != coverage)
                    {
                        failures.AppendLine($"    {label} meta 0x{raw:X2} face {face} ({FaceName(face)}): "
                                            + $"silhouette area {area:R} != coverage {coverage:R}");
                    }
                }
            }
        }

        /// <summary>
        /// The <c>SS-0</c> post fixture — a quarter-cell column touching floor and ceiling but no side
        /// wall. Its bounds come from <see cref="Meshing.Framework.TestCustomMeshLibrary.PostBounds"/>
        /// rather than a local literal, so this suite and the meshing suite cannot disagree about what
        /// "the post" is.
        /// </summary>
        /// <returns>The fixture block type.</returns>
        private static BlockType MakePost()
        {
            BlockType block = MakeBlock("TestPost", Meshing.Framework.TestCustomMeshLibrary.PostBounds);
            block.renderShape = RenderShape.CustomMesh;
            return block;
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
