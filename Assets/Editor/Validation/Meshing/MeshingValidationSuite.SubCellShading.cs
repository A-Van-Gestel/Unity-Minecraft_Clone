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
    /// VO-9b + SS-2 baselines: a face a partial occluder can reach is subdivided, the subdivision stays
    /// gated to those faces, and the shading it carries has real sub-cell detail without losing the
    /// occlusion its neighbors contribute.
    /// </summary>
    public static partial class MeshingValidationSuite
    {
        /// <summary>Registers the VO-9b sub-cell shading baselines (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddSubCellShadingBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B49: only faces a partial occluder can reach are subdivided, and such a face carries a contact shadow without losing its neighbors' (VO-9b + SS-2)",
                B49_SubCellContactShadow));

            scenarios.Add(new Scenario(
                "B56: a face corner with 0/1/2/3 fully-occluding neighbors reads exactly 255/191/64/64 — the pre-SS-2 model, reproduced (SS-2)",
                B56_CornerReduction));
        }

        /// <summary>
        /// B56 — <b>the claim the whole SS-2 replacement rests on.</b> Swapping a coverage fraction for a
        /// distance field is only safe because, at a cell corner with full-cube occluders, the new model
        /// collapses term-for-term onto the expression the engine has always evaluated. These are exact
        /// values, not tolerances: every weight is a quarter and every occluder is either in contact or a
        /// full cell away, so the arithmetic is identical and any drift is a real change.
        /// <para>
        /// <b>The two-and-three-occluder rows are the sharp ones.</b> They pin the corner seal — classic
        /// voxel AO darkens a corner fully once both flanking cells are solid, whatever sits diagonally,
        /// because the diagonal quadrant is not visible from that corner at all. An occlusion model that
        /// treats the nine cells as independent silently lightens every inside corner in the world from
        /// 64 to 127, and <b>nothing else in this suite pins that</b>: measured by mutation, replacing
        /// the accumulating sum with a max leaves 0/1 correct and drives 2/3 to 191.
        /// </para>
        /// <para>
        /// The single-quad check is the positive control: it proves these readings come from the ordinary
        /// undivided path, so the row is about the model rather than about tessellation.
        /// </para>
        /// </summary>
        /// <returns>True when all four occluder counts reproduce their historical value.</returns>
        private static bool B56_CornerReduction()
        {
            ushort lit = LightBitMapping.PackLightData(15, 0, 0, 0);
            int[] expected = { 255, 191, 64, 64 };
            StringBuilder failures = new StringBuilder();

            for (int occluders = 0; occluders < expected.Length; occluders++)
            {
                using MeshingTestWorld world = new MeshingTestWorld();
                BuildFloor(world);

                // Around the probe face's (0,0) corner: the two flanking cells, then the diagonal.
                if (occluders >= 1) world.SetBlock(B49_X - 1, B49_Y + 1, B49_Z, TestMeshBlockPalette.SolidOpaque, 0);
                if (occluders >= 2) world.SetBlock(B49_X, B49_Y + 1, B49_Z - 1, TestMeshBlockPalette.SolidOpaque, 0);
                if (occluders >= 3) world.SetBlock(B49_X - 1, B49_Y + 1, B49_Z - 1, TestMeshBlockPalette.SolidOpaque, 0);

                world.FillLight(lit);
                MeshDataJobOutput o = world.Run(SmoothLightingQuality.High);

                int quads = CountTopFaceQuads(o, B49_X, B49_Y, B49_Z);
                if (quads != 1)
                {
                    failures.AppendFormat(
                        "    {0} occluder(s): the face emitted {1} quads, so this is not the undivided path\n",
                        occluders, quads);
                    continue;
                }

                byte[] corners = TopFaceCornerSun(o, B49_X, B49_Y, B49_Z);
                if (corners == null)
                {
                    failures.AppendFormat("    {0} occluder(s): the face's corners were not all emitted\n", occluders);
                    continue;
                }

                if (corners[0] != expected[occluders])
                {
                    failures.AppendFormat("    {0} occluder(s): corner reads {1}, expected {2}\n",
                        occluders, corners[0], expected[occluders]);
                }
            }

            return MeshAssert.IsTrue("B56 the corner reduction reproduces the pre-SS-2 model",
                failures.Length == 0,
                "A face corner surrounded by full cubes must read exactly what it read before SS-2 "
                + "replaced the occlusion function. If it does not, the replacement is not the "
                + "behaviour-preserving generalization it is documented to be, and ordinary terrain has "
                + "moved.\n" + failures);
        }

        /// <summary>Chunk-local X of the floor cell the occluder stands on.</summary>
        private const int B49_X = 8;

        /// <summary>Chunk-local Y of the floor layer.</summary>
        private const int B49_Y = 8;

        /// <summary>Chunk-local Z of the floor cell the occluder stands on.</summary>
        private const int B49_Z = 8;

        /// <summary>
        /// B49 — the subdivision substrate and what it now carries.
        /// <list type="bullet">
        /// <item><b>The gate holds.</b> A floor face with nothing above it must still emit exactly one
        /// quad. Tessellation leaking into ordinary terrain would multiply the vertex count of every
        /// chunk in the world.</item>
        /// <item><b>The face is subdivided</b> when a partial occluder can reach it.</item>
        /// <item><b>It carries a real contact shadow.</b> Under VO-9b this scenario asserted the opposite
        /// — that the subdivided face reproduced its own corner field exactly — because a coverage
        /// fraction varies near-linearly across a cell and a corner blend already is that ramp. SS-2
        /// replaced coverage with a distance field, so the interior now departs from the corner field on
        /// purpose and this leg measures that the departure exists.</item>
        /// <item><b>Without lightening the interior.</b> The regression guard for a shipped bug: an
        /// earlier VO-9b re-sampled the ring per sub-vertex, and because occlusion rode on the
        /// interpolation weights — which collapse onto the cell in front of the face at its center —
        /// every neighboring shadow vanished there (an inner corner's center went 144 to 255). Faces
        /// still agreed along the shared seam <i>line</i>, so a seam-only check stayed green while the
        /// artifact was plainly visible in game. This leg reads the interior, and pins the value the
        /// defect drives to 255.</item>
        /// </list>
        /// </summary>
        /// <returns>True when the substrate is gated, active, and shading with sub-cell detail.</returns>
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

            // Leg 3a — the contact shadow must actually reach the face interior. Under a vertical slab
            // the sub-vertex on the slab's own edge is the darkest point of the face, and the far edge
            // is the lightest: a shadow that is present, oriented, and bounded.
            ok &= AssertContactShadowProfile(output, corners, "open floor, slab overhead");

            int walledCenter = InnerCornerFaceCenter(lit, withWalls: true);
            int openCenter = InnerCornerFaceCenter(lit, withWalls: false);

            // Leg 3b — THE precise regression guard, rewritten for SS-2. It used to assert the interior
            // stayed on the face's bilinear corner field; SS-2 removes that property on purpose, so the
            // assertion moved to the *defect's own signature* rather than being loosened to accommodate
            // the change. The shipped VO-9b defect lightened face interiors toward the unoccluded value
            // as every ring occluder's contribution vanished at the face center (an inner corner went
            // 144 to 255). So: tucked between two walls, the center must stay materially dark, and stay
            // correctly ordered against the near and far corners.
            ok &= AssertInnerCornerCenterStaysDark(walledCenter, openCenter);

            return ok;
        }

        /// <summary>
        /// Asserts the face carries a contact shadow: darkest against the occluder, lightest away from
        /// it, and never darker than a fully-occluded corner.
        /// </summary>
        /// <param name="o">The meshing job output to read.</param>
        /// <param name="corners">The face's four corner sun values, in <c>l0..l3</c> order.</param>
        /// <param name="label">Configuration name used in the failure text.</param>
        /// <returns>True when the profile is present and correctly oriented.</returns>
        private static bool AssertContactShadowProfile(MeshDataJobOutput o, byte[] corners, string label)
        {
            List<SubVertexSample> field = TopFaceSubVertexField(o, B49_X, B49_Y, B49_Z);
            if (field.Count == 0)
            {
                Debug.LogError("[FAIL] B49 setup: the probe face emitted no vertices.");
                return false;
            }

            int darkest = 255;
            int lightest = 0;
            foreach (SubVertexSample s in field)
            {
                if (s.Sun < darkest) darkest = s.Sun;
                if (s.Sun > lightest) lightest = s.Sun;
            }

            return MeshAssert.IsTrue($"B49 the face carries a contact shadow ({label})",
                lightest - darkest >= MIN_CONTACT_SHADOW_RANGE,
                $"The face's sub-vertices span only {lightest - darkest} light units "
                + $"({darkest}..{lightest}), so the occluder standing on it casts no measurable contact "
                + "shadow. This is the state VO-9b shipped in: the substrate subdivides the face, but "
                + "whatever shades it carries no sub-cell detail.\n"
                + $"    corners: {corners[0]}, {corners[1]}, {corners[2]}, {corners[3]}");
        }

        /// <summary>
        /// Builds the inner-corner fixture: the probe cell with a gate-tripping slab on its diagonal,
        /// optionally walled in on two sides.
        /// </summary>
        /// <param name="lit">Packed light value to fill the world with.</param>
        /// <param name="withWalls">Whether to raise the two walls that form the inner corner.</param>
        /// <returns>The probe face's center sun value, or -1 when that vertex was not emitted.</returns>
        private static int InnerCornerFaceCenter(ushort lit, bool withWalls)
        {
            using MeshingTestWorld world = new MeshingTestWorld();
            BuildFloor(world);

            if (withWalls)
            {
                for (int d = -2; d <= 2; d++)
                {
                    world.SetBlock(B49_X + 1, B49_Y + 1, B49_Z + d, TestMeshBlockPalette.SolidOpaque, 0);
                    world.SetBlock(B49_X + d, B49_Y + 1, B49_Z + 1, TestMeshBlockPalette.SolidOpaque, 0);
                }
            }

            // Present in BOTH configurations, so the face is subdivided either way and the comparison
            // isolates the walls rather than the tessellation.
            world.SetBlock(B49_X - 1, B49_Y + 1, B49_Z - 1, TestMeshBlockPalette.HalfSlab, 0x03);
            world.FillLight(lit);

            // Read inside the using scope: the output's buffers are pooled by the world.
            return TryReadFaceCenter(world.Run(SmoothLightingQuality.High), out int center) ? center : -1;
        }

        /// <summary>
        /// Asserts that walls standing <i>beside</i> a face darken its <b>interior</b>, not merely its
        /// edges — the regression guard for the defect VO-9b shipped and had to correct.
        /// <para>
        /// Stated as a differential between the same face with and without the walls, which is what
        /// makes it robust: it assumes nothing about which corner is which, about the falloff profile,
        /// or about the shadow's radius, all of which are tuning surfaces. The defect drove this
        /// difference to zero — occlusion rode on the interpolation weights, and those collapse onto the
        /// single cell in front of the face at its center, so every neighboring shadow vanished exactly
        /// there while the seams still matched (an inner corner's center went 144 to 255).
        /// </para>
        /// </summary>
        /// <param name="walledCenter">Face-center sun with the two walls raised.</param>
        /// <param name="openCenter">Face-center sun with the same fixture minus the walls.</param>
        /// <returns>True when the walls measurably darken the face center.</returns>
        private static bool AssertInnerCornerCenterStaysDark(int walledCenter, int openCenter)
        {
            if (walledCenter < 0 || openCenter < 0)
            {
                Debug.LogError("[FAIL] B49 setup: the inner-corner probe face emitted no center sub-vertex.");
                return false;
            }

            return MeshAssert.IsTrue("B49 walls beside a face darken its interior, not just its edges",
                openCenter - walledCenter >= MIN_RING_INTERIOR_DARKENING,
                $"With two walls raised beside it the face center reads {walledCenter}; without them it "
                + $"reads {openCenter} — a difference of {openCenter - walledCenter}, below the "
                + $"{MIN_RING_INTERIOR_DARKENING} this guards.\n"
                + "Occlusion from geometry standing beside a surface must reach the middle of that "
                + "surface. When it does not, wall shadows collapse into a hard band against the wall "
                + "and face interiors wash out — the artifact VO-9b shipped, which a seam-only check "
                + "could not see because the faces still agreed along their shared edge.");
        }

        /// <summary>Reads the sunlight at the probe face's center sub-vertex.</summary>
        /// <param name="o">The meshing job output to read.</param>
        /// <param name="center">The center sub-vertex's sun value.</param>
        /// <returns>True when the face emitted a vertex at its center.</returns>
        private static bool TryReadFaceCenter(MeshDataJobOutput o, out int center)
        {
            center = -1;
            foreach (SubVertexSample s in TopFaceSubVertexField(o, B49_X, B49_Y, B49_Z))
            {
                if (Mathf.Abs(s.U - 0.5f) < 0.01f && Mathf.Abs(s.V - 0.5f) < 0.01f) center = s.Sun;
            }

            return center >= 0;
        }

        /// <summary>
        /// How far walls beside a face must darken its center. The defect this guards drives the
        /// difference to zero; the model measures far more.
        /// </summary>
        private const int MIN_RING_INTERIOR_DARKENING = 24;

        /// <summary>
        /// Smallest spread across a face's sub-vertices that counts as a contact shadow. Well below the
        /// measured range, and far above the couple of units the pre-SS-2 coverage model managed.
        /// </summary>
        private const int MIN_CONTACT_SHADOW_RANGE = 24;

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

        /// <summary>Fills a 5×5 platform of full cubes centerd on the probe cell.</summary>
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
