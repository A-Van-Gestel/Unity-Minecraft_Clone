using System.Collections.Generic;
using System.Text;
using Data;
using Data.Enums;
using Editor.Validation.Meshing.Framework;
using Jobs.BurstData;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Meshing
{
    /// <summary>
    /// VO-9b baselines: a face a partial occluder can reach is subdivided so its shading can carry
    /// sub-cell detail, the subdivision is gated to those faces, and it renders the same field an
    /// ordinary face would until a term that varies within the cell is added.
    /// </summary>
    public static partial class MeshingValidationSuite
    {
        /// <summary>Registers the VO-9b sub-cell shading baselines (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddSubCellShadingBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B49: only faces a partial occluder can reach are subdivided, and a subdivided face still renders its own corner field (VO-9b)",
                B49_SubCellContactShadow));
        }

        /// <summary>Chunk-local X of the floor cell the occluder stands on.</summary>
        private const int B49_X = 8;

        /// <summary>Chunk-local Y of the floor layer.</summary>
        private const int B49_Y = 8;

        /// <summary>Chunk-local Z of the floor cell the occluder stands on.</summary>
        private const int B49_Z = 8;

        /// <summary>
        /// B49 — the VO-9b substrate: a face a partial occluder can reach is subdivided, and subdividing
        /// it does not change how it looks.
        /// <para>
        /// <b>Read that second clause carefully — it is the point of this scenario, not a weakness in
        /// it.</b> VO-9b re-evaluates only the <i>direct</i> cell's coverage per sub-vertex; the ring
        /// samples that carry shadows from neighbouring geometry stay a blend of the face's corner
        /// values. For an axis-aligned half slab the direct coverage varies linearly across the cell, and
        /// a blend of the two corner values already <i>is</i> that linear ramp — so the subdivided face
        /// reproduces the undivided one exactly. The substrate is in place; it is a shape whose coverage
        /// is non-linear (a post, a stair) or a silhouette-based falloff term that would make it visible.
        /// </para>
        /// <list type="bullet">
        /// <item><b>The gate holds.</b> A floor face with nothing above it must still emit exactly one
        /// quad. Tessellation leaking into ordinary terrain would multiply the vertex count of every
        /// chunk in the world.</item>
        /// <item><b>The face is subdivided</b> when a partial occluder can reach it.</item>
        /// <item><b>The interior still matches the undivided field.</b> Every sub-vertex must equal the
        /// bilinear blend of the face's own corners. <b>This is the regression guard for a shipped bug:</b>
        /// an earlier VO-9b re-sampled the ring per sub-vertex, which pulled every wall shadow into a hard
        /// band against the wall and lightened face interiors drastically (an inner corner's centre went
        /// 144 to 255). Faces still agreed with their neighbours along the shared seam <i>line</i>, so a
        /// seam-only check stayed green while the artifact was plainly visible in game — which is why this
        /// leg samples the interior, not the edge.</item>
        /// </list>
        /// </summary>
        /// <returns>True when the substrate is gated, active, and visually inert.</returns>
        private static bool B49_SubCellContactShadow()
        {
            ushort lit = LightBitMapping.PackLightData(15, 0, 0, 0);

            // --- Leg 2 first: the gate. An undisturbed floor face is still a single quad.
            using (MeshingTestWorld plain = new MeshingTestWorld())
            {
                BuildFloor(plain);
                plain.FillLight(lit);
                MeshDataJobOutput o = plain.Run(SmoothLightingQuality.High);
                int quads = CountTopFaceQuads(o, B49_X, B49_Y, B49_Z);

                if (!MeshAssert.IsTrue("B49 gate: a face with no partial occluder is not tessellated",
                        quads == 1,
                        $"The floor's top face emitted {quads} quads with nothing but full cubes around "
                        + "it. Sub-cell shading must be gated on a partial occluder actually being able "
                        + "to reach the face — otherwise every face in the world pays for it."))
                {
                    return false;
                }
            }

            // --- Legs 1 and 3: place the slab and read the face it stands on.
            using MeshingTestWorld world = new MeshingTestWorld();
            BuildFloor(world);
            world.SetBlock(B49_X, B49_Y + 1, B49_Z, TestMeshBlockPalette.HalfSlab, 0x03);
            world.FillLight(lit);
            MeshDataJobOutput output = world.Run(SmoothLightingQuality.High);

            int shadedQuads = CountTopFaceQuads(output, B49_X, B49_Y, B49_Z);
            bool ok = MeshAssert.IsTrue("B49 a face a partial occluder reaches is tessellated",
                shadedQuads > 1,
                $"The floor's top face under a vertical slab emitted {shadedQuads} quad(s), so it still "
                + "carries one shading value per cell corner and the contact shadow cannot resolve.");

            if (!ok) return false;

            byte[] corners = TopFaceCornerSun(output, B49_X, B49_Y, B49_Z);
            if (corners == null)
            {
                Debug.LogError("[FAIL] B49 setup: the floor's top face corners were not all emitted.");
                return false;
            }

            // Leg 3a — with an occluder in the direct cell the interior may legitimately depart from the
            // corner field, because that is the one term VO-9b re-evaluates per sub-vertex. The bound is
            // a gross-departure guard, not a pin: measured drift here is a few units, while the shipped
            // ring-resampling defect drifted by over a hundred.
            ok &= AssertInteriorNearCornerField(output, corners, "open floor, slab overhead",
                DIRECT_TERM_DRIFT_ALLOWANCE);

            using MeshingTestWorld walled = new MeshingTestWorld();
            BuildFloor(walled);
            for (int d = -2; d <= 2; d++)
            {
                walled.SetBlock(B49_X + 1, B49_Y + 1, B49_Z + d, TestMeshBlockPalette.SolidOpaque, 0);
                walled.SetBlock(B49_X + d, B49_Y + 1, B49_Z + 1, TestMeshBlockPalette.SolidOpaque, 0);
            }

            walled.SetBlock(B49_X - 1, B49_Y + 1, B49_Z - 1, TestMeshBlockPalette.HalfSlab, 0x03);
            walled.FillLight(lit);
            MeshDataJobOutput walledOut = walled.Run(SmoothLightingQuality.High);

            byte[] walledCorners = TopFaceCornerSun(walledOut, B49_X, B49_Y, B49_Z);
            if (walledCorners == null)
            {
                Debug.LogError("[FAIL] B49 setup: the walled probe cell's top face corners were not emitted.");
                return false;
            }

            // Leg 3b — THE precise regression guard. Here the probe cell's own direct cell is empty (the
            // walls and the gate-tripping slab are all ring cells), so VO-9b has nothing to vary and the
            // subdivided face must reproduce its corner field exactly. Any drift is the ring being
            // re-sampled per sub-vertex, which is what shipped and what the owner caught in game.
            ok &= AssertInteriorNearCornerField(walledOut, walledCorners,
                "inner corner between two walls, empty cell overhead", ROUNDING_ALLOWANCE);

            return ok;
        }

        /// <summary>Slack that absorbs the per-vertex UNorm8 rounding of the encode.</summary>
        private const float ROUNDING_ALLOWANCE = 1.5f;

        /// <summary>
        /// How far a sub-vertex may sit off the corner field when the direct cell holds an occluder.
        /// Generous on purpose: this leg guards against a <i>gross</i> departure (the ring-resampling
        /// defect moved an inner corner's centre by over 100), not against the direct term's own
        /// legitimate sub-cell variation, which measures a few units for an axis-aligned slab.
        /// </summary>
        private const float DIRECT_TERM_DRIFT_ALLOWANCE = 32f;

        /// <summary>
        /// Asserts that every emitted vertex on the probe cell's top face sits within
        /// <paramref name="allowance"/> of the bilinear blend of that face's four corner values.
        /// </summary>
        /// <param name="o">The meshing job output to read.</param>
        /// <param name="corners">The face's four corner sun values, in <c>l0..l3</c> order.</param>
        /// <param name="label">Configuration name used in the failure text.</param>
        /// <param name="allowance">Permitted departure from the corner field, in encoded light units.</param>
        /// <returns>True when every sub-vertex is within the allowance.</returns>
        private static bool AssertInteriorNearCornerField(MeshDataJobOutput o, byte[] corners, string label,
            float allowance)
        {
            StringBuilder drift = new StringBuilder();

            foreach (SubVertexSample s in TopFaceSubVertexField(o, B49_X, B49_Y, B49_Z))
            {
                float expected = corners[0] * (1f - s.U) * (1f - s.V) + corners[1] * (1f - s.U) * s.V
                                                                      + corners[2] * s.U * (1f - s.V)
                                                                      + corners[3] * s.U * s.V;

                if (Mathf.Abs(s.Sun - expected) <= allowance) continue;

                drift.AppendFormat("    ({0:F2}, {1:F2}): reads {2}, corner field gives {3:F1}\n",
                    s.U, s.V, s.Sun, expected);
            }

            return MeshAssert.IsTrue($"B49 the subdivided face stays on its own corner field ({label})",
                drift.Length == 0,
                $"A sub-vertex drifted more than {allowance} off the bilinear field of the face's corners, "
                + "so a subdivided face no longer renders what an ordinary face would. That is how the "
                + "shipped ring-resampling defect looked: shadows from neighbouring blocks collapsed into "
                + "a hard band and face interiors lightened, while the seams themselves still matched.\n"
                + $"    corners: {corners[0]}, {corners[1]}, {corners[2]}, {corners[3]}\n" + drift);
        }

        /// <summary>One emitted vertex on a probed face, in the face's own parameter space.</summary>
        public struct SubVertexSample
        {
            /// <summary>First face-parameter coordinate, in <c>[0, 1]</c>.</summary>
            public float U;

            /// <summary>Second face-parameter coordinate, in <c>[0, 1]</c>.</summary>
            public float V;

            /// <summary>The vertex's encoded sky light.</summary>
            public byte Sun;
        }

        /// <summary>
        /// SS-0: returns every emitted vertex lying on one cell's <c>+Y</c> face, keyed by its position
        /// within that face rather than by which quad carried it.
        /// <para>
        /// <b>The reading is tessellation-independent by construction</b>, which is the whole point: a
        /// face is one quad or <c>N×N</c> sub-quads depending on what stands near it, so any probe that
        /// indexes by quad order asserts something different at each density. B42 and B46 broke on
        /// exactly that when VO-9b landed; <see cref="TopFaceCornerSun"/> is the corner-located answer
        /// and this is its whole-field counterpart, for scenarios that need the interior too.
        /// </para>
        /// <para>
        /// The <c>(u, v)</c> convention matches <c>VoxelMeshHelper.GetCornerUV</c> for a <c>+Y</c> face
        /// (<c>u = x</c>, <c>v = z</c>), so a sample's parameters index the <c>l0..l3</c> corner order
        /// directly and a caller can compare against a bilinear corner field without remapping.
        /// </para>
        /// </summary>
        /// <param name="o">The meshing job output to read.</param>
        /// <param name="cellX">Chunk-local X of the cell.</param>
        /// <param name="cellY">Chunk-local Y of that cell (the face lies at <c>cellY + 1</c>).</param>
        /// <param name="cellZ">Chunk-local Z of the cell.</param>
        /// <returns>Every vertex on that face, in emission order; empty when the face is not emitted.</returns>
        private static List<SubVertexSample> TopFaceSubVertexField(MeshDataJobOutput o,
            int cellX, int cellY, int cellZ)
        {
            List<SubVertexSample> samples = new List<SubVertexSample>();
            float plane = cellY + 1;

            for (int v = 0; v < o.Vertices.Length; v++)
            {
                if (o.Normals[v].y < 0.99f) continue;

                Vector3 p = o.Vertices[v];
                if (Mathf.Abs(p.y - plane) > FACE_POSITION_EPSILON) continue;
                if (p.x < cellX - FACE_POSITION_EPSILON || p.x > cellX + 1f + FACE_POSITION_EPSILON) continue;
                if (p.z < cellZ - FACE_POSITION_EPSILON || p.z > cellZ + 1f + FACE_POSITION_EPSILON) continue;

                samples.Add(new SubVertexSample
                {
                    U = p.x - cellX,
                    V = p.z - cellZ,
                    Sun = o.LightData[v].r,
                });
            }

            return samples;
        }

        /// <summary>Positional tolerance when matching an emitted vertex to a face (SS-0).</summary>
        private const float FACE_POSITION_EPSILON = 0.01f;

        /// <summary>Fills a 5×5 platform of full cubes centred on the probe cell.</summary>
        /// <param name="world">The fixture to build into.</param>
        private static void BuildFloor(MeshingTestWorld world)
        {
            for (int dx = -2; dx <= 2; dx++)
            for (int dz = -2; dz <= 2; dz++)
                world.SetBlock(B49_X + dx, B49_Y, B49_Z + dz, TestMeshBlockPalette.SolidOpaque, 0);
        }

        /// <summary>Counts the emitted quads lying wholly on one cell's <c>+Y</c> face.</summary>
        /// <param name="o">The meshing job output to search.</param>
        /// <param name="cellX">Chunk-local X of the cell.</param>
        /// <param name="cellY">Chunk-local Y of the cell (its top face lies at <c>cellY + 1</c>).</param>
        /// <param name="cellZ">Chunk-local Z of the cell.</param>
        private static int CountTopFaceQuads(MeshDataJobOutput o, int cellX, int cellY, int cellZ)
        {
            float plane = cellY + 1;
            int count = 0;

            for (int quad = 0; quad < o.Vertices.Length / 4; quad++)
            {
                if (o.Normals[quad * 4].y < 0.99f) continue;

                bool onFace = true;
                for (int v = 0; v < 4; v++)
                {
                    Vector3 p = o.Vertices[quad * 4 + v];
                    onFace &= Mathf.Abs(p.y - plane) < 0.01f
                              && p.x >= cellX - 0.01f && p.x <= cellX + 1.01f
                              && p.z >= cellZ - 0.01f && p.z <= cellZ + 1.01f;
                }

                if (onFace) count++;
            }

            return count;
        }
    }
}
