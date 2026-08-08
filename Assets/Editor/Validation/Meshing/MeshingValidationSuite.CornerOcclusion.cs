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
    /// VO-8 baselines: ambient occlusion asks its coverage question per <b>corner</b> (the octant of a
    /// sample cell touching that corner's vertex), not per face — plus <b>B47</b>, which guards the
    /// follow-up fix for <c>MESHING_BUGS.md</c> Bug M03: the octant's <i>normal</i> axis is resolved from
    /// the face's own plane, so a face interior to its cell is not shadowed by the block emitting it.
    /// <para>
    /// VO-5 already produced an in-between shade for partial blocks, but a single per-face answer applies
    /// evenly to all four corners of a face — so a vertical slab standing on a block dimmed the whole
    /// surface uniformly instead of shading only the half its solid part stands on. These scenarios pin
    /// the <i>directional</i> property that fixes.
    /// </para>
    /// <para>
    /// Assertions are structural — "two corners darkened and two not", "the four rolls disagree" — never
    /// predicted light constants, so they need no model of the engine's corner averaging or UNorm8
    /// encoding (the A4 trap the MH-3 oracle notes call out).
    /// </para>
    /// </summary>
    public static partial class MeshingValidationSuite
    {
        /// <summary>Registers the VO-8 per-corner occlusion baselines (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddCornerOcclusionBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B46: a vertical slab shades only the corners its solid half stands on, and rotating it moves which corners (VO-8)",
                B46_PartialBlockShadesPerCorner));
            scenarios.Add(new Scenario(
                "B47: a partial block recessed into a floor does not occlude its own mid-plane face (Bug M03)",
                B47_RecessedSlabDoesNotOccludeItself));
        }

        /// <summary>Chunk-local position of the recessed slab used by B47.</summary>
        private const int KM03_X = 8;

        /// <summary>Chunk-local Y of the recessed slab; the surrounding floor occupies the same layer.</summary>
        private const int KM03_Y = 8;

        /// <summary>Chunk-local Z of the recessed slab.</summary>
        private const int KM03_Z = 8;

        /// <summary>
        /// B47 — promoted from <c>KM03a</c> (<c>MESHING_BUGS.md</c> Bug M03, fixed August 2026,
        /// confirmed in game 2026-08-08).
        /// <para>
        /// A bottom half slab sits in a floor of full blocks, so its mid-plane top face is recessed half a
        /// block below the surrounding surface with open sky above. The light field is a uniform sky 15,
        /// so anything dark here is the ambient-occlusion term alone — this scenario cannot be confounded
        /// by light propagation.
        /// </para>
        /// <para>
        /// Under the bug the face rendered <b>fully black</b>: VO-6 makes a mid-plane face sample the
        /// block's own cell, and the octant selected for that sample was the half <i>behind</i> the
        /// surface — the slab's own solid material — so the block reported that it occluded its own face,
        /// and the same error evaluated the ambient ring through the floor's solid material.
        /// </para>
        /// <para>
        /// Uses <see cref="TestMeshBlockPalette.HalfSlab"/> (opacity 15) deliberately, not
        /// <see cref="TestMeshBlockPalette.PartialOpaque"/>: the AO coverage query is gated on
        /// <c>IsOpaque</c>, so a sub-15 slab returns zero coverage and can never self-occlude. Only a
        /// fully-opaque partial block — which is what the production <c>Stone Half Slab</c> is — can
        /// regress this. <b>Do not "simplify" the fixture to the shared PartialOpaque slab</b>; it would
        /// leave this scenario permanently green regardless of the engine.
        /// </para>
        /// </summary>
        /// <returns>True while a recessed partial block lights its own mid-plane face.</returns>
        private static bool B47_RecessedSlabDoesNotOccludeItself()
        {
            ushort lit = LightBitMapping.PackLightData(15, 0, 0, 0);
            ushort dimmed = LightBitMapping.PackLightData(8, 0, 0, 0);

            // Control: the same probe on an ISOLATED slab must respond to the light field, proving the
            // fixture and face lookup work independently of the embedded case this scenario is about.
            byte[] loneLit = RecessedSlabTopFace(false, lit, null);
            byte[] loneDim = RecessedSlabTopFace(false, dimmed, null);
            if (loneLit == null || loneDim == null) return false;

            bool ok = MeshAssert.IsTrue("B47 control: the probe can observe light",
                !SameCorners(loneLit, loneDim),
                "An isolated slab's mid-plane face did not change when the light field went 8 -> 15, so "
                + "the probe cannot observe light at all and the legs below would pass vacuously.\n"
                + Describe("field 8", loneDim) + Describe("field 15", loneLit));

            byte[] embedded = RecessedSlabTopFace(true, lit, null);
            if (embedded == null) return false;

            // Leg 1 — the visible symptom.
            ok &= MeshAssert.IsTrue("B47 a recessed slab's own face is not fully black",
                !(embedded[0] == 0 && embedded[1] == 0 && embedded[2] == 0 && embedded[3] == 0),
                "A half slab recessed into a floor rendered its mid-plane top face FULLY BLACK under a "
                + "uniform sky-15 field. The face has open sky above it through its own cell's upper "
                + "half, so it cannot be unlit.\n"
                + Describe("embedded", embedded) + Describe("isolated (for scale)", loneLit));

            // Leg 2 — the mechanism. If the block occludes its own face, its own cell's light is
            // multiplied by zero and changing that light cannot move the face.
            byte[] ownBright = RecessedSlabTopFace(true, dimmed, lit);
            byte[] ownDim = RecessedSlabTopFace(true, dimmed, null);
            if (ownBright == null || ownDim == null) return false;

            ok &= MeshAssert.IsTrue("B47 the recessed slab's own cell drives its own face",
                !SameCorners(ownBright, ownDim),
                "Brightening the recessed slab's OWN cell left its mid-plane face unchanged, so that "
                + "cell's contribution is being multiplied by zero — the block is occluding its own "
                + "surface.\n" + Describe("own cell dim", ownDim) + Describe("own cell lit", ownBright));

            return ok;
        }

        /// <summary>
        /// Meshes a bottom half slab, optionally surrounded by a floor at the same level, and returns the
        /// four vertex sunlight values of its mid-plane top face.
        /// </summary>
        /// <param name="embedded">When true, fills the surrounding cells with full blocks so the slab's
        /// top face is recessed below the surface.</param>
        /// <param name="uniform">Packed light written to every cell.</param>
        /// <param name="ownCellLight">When set, overrides the light in the slab's own cell.</param>
        /// <returns>The four corner sun values, or null when the face was not emitted (already logged).</returns>
        private static byte[] RecessedSlabTopFace(bool embedded, ushort uniform, ushort? ownCellLight)
        {
            using MeshingTestWorld world = new MeshingTestWorld();
            world.SetBlock(KM03_X, KM03_Y, KM03_Z, TestMeshBlockPalette.HalfSlab, 0x00);

            if (embedded)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    for (int dz = -2; dz <= 2; dz++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        world.SetBlock(KM03_X + dx, KM03_Y, KM03_Z + dz, TestMeshBlockPalette.SolidOpaque, 0);
                    }
                }
            }

            world.FillLight(uniform);
            if (ownCellLight.HasValue)
                world.SetLight(KM03_X, KM03_Y, KM03_Z, ownCellLight.Value);

            MeshDataJobOutput o = world.Run(SmoothLightingQuality.High);

            for (int quad = 0; quad < o.Vertices.Length / 4; quad++)
            {
                Vector3 normal = o.Normals[quad * 4];
                Vector3 vertex = o.Vertices[quad * 4];
                bool isMidPlaneTop = normal.y > 0.5f
                                     && Mathf.Abs(vertex.y - (KM03_Y + 0.5f)) < 0.01f
                                     && vertex.x >= KM03_X - 0.01f && vertex.x <= KM03_X + 1.01f
                                     && vertex.z >= KM03_Z - 0.01f && vertex.z <= KM03_Z + 1.01f;
                if (!isMidPlaneTop) continue;

                byte[] corners = new byte[4];
                for (int i = 0; i < 4; i++) corners[i] = o.LightData[quad * 4 + i].r;
                return corners;
            }

            Debug.LogError($"[FAIL] B47 setup: the slab's mid-plane top face (y = {KM03_Y + 0.5f}) was not "
                           + $"emitted (embedded = {embedded}). Either it is being culled or the probe is "
                           + "looking for the wrong quad — the scenario is broken, not the engine.");
            return null;
        }

        /// <summary>Chunk-local position of the floor block whose +Y face these scenarios read.</summary>
        private const int VO8_FLOOR_X = 8;

        /// <summary>Chunk-local Y of the floor block; the occluder stands at Y + 1.</summary>
        private const int VO8_FLOOR_Y = 8;

        /// <summary>Chunk-local Z of the floor block.</summary>
        private const int VO8_FLOOR_Z = 8;

        /// <summary>The four vertical orientations of a half slab — facing 3, rolls 0-3.</summary>
        private static readonly byte[] s_vo8VerticalRolls = { 0x03, 0x0B, 0x13, 0x1B };

        /// <summary>
        /// B46 — the VO-8 behavior: a partial block occludes the corners its volume actually stands next
        /// to, and leaves the others alone.
        /// <para>
        /// A floor block's <c>+Y</c> face is shaded while a half slab stands in the cell above it. The
        /// slab covers half that cell, so it must darken the two corners on its solid side and leave the
        /// two on its open side untouched — the wall-like gradient, with the in-between shading coming
        /// from the corner average rather than from a fractional coverage.
        /// </para>
        /// <list type="bullet">
        /// <item><b>Controls.</b> Nothing above leaves all four corners at full light; a <i>bottom</i>
        /// slab (solid underside, filling every octant against the floor) darkens all four equally. Those
        /// two bracket the effect and prove the probe can observe both extremes — without them, a
        /// scenario that always reported "two dark, two light" could pass on a broken engine.</item>
        /// <item><b>The gradient.</b> A vertical slab must produce exactly two darkened corners and two
        /// undarkened ones. Four equal corners means the query is still per-face (the VO-5 behavior).</item>
        /// <item><b>Directionality.</b> The four vertical rolls must produce four <i>pairwise different</i>
        /// corner patterns. This is the leg that matters: an implementation that picks the wrong octant —
        /// or the same octant for every corner — still yields "two dark, two light" for some rotation and
        /// would sail past the leg above. Only "rotating the slab moves which corners darken" pins that
        /// the coverage is being asked about the right place.</item>
        /// </list>
        /// </summary>
        /// <returns>True when occlusion is per-corner and orientation-dependent.</returns>
        private static bool B46_PartialBlockShadesPerCorner()
        {
            byte[] open = FloorTopCorners(TestMeshBlockPalette.Air, 0);
            byte[] bottomSlab = FloorTopCorners(TestMeshBlockPalette.HalfSlab, AO_SLAB_BOTTOM);
            if (open == null || bottomSlab == null) return false;

            bool ok = MeshAssert.IsTrue("B46 control: an open cell leaves every corner unshaded",
                AllEqual(open),
                "With nothing above the floor its top face should be uniformly lit; it is not, so the "
                + "probe is picking up something other than the occluder.\n" + Describe("open", open));

            ok &= MeshAssert.IsTrue("B46 control: a bottom slab shades every corner equally",
                AllEqual(bottomSlab) && bottomSlab[0] < open[0],
                "A bottom half slab's underside fills the octant against every corner of the floor's top "
                + "face, so it must darken all four equally. If it does not darken at all the probe "
                + "cannot see occlusion; if it darkens unevenly the octant selection is wrong.\n"
                + Describe("open", open) + Describe("bottom slab", bottomSlab));

            StringBuilder patterns = new StringBuilder();
            byte[][] byRoll = new byte[s_vo8VerticalRolls.Length][];
            for (int i = 0; i < s_vo8VerticalRolls.Length; i++)
            {
                byRoll[i] = FloorTopCorners(TestMeshBlockPalette.HalfSlab, s_vo8VerticalRolls[i]);
                if (byRoll[i] == null) return false;
                patterns.Append(Describe($"meta 0x{s_vo8VerticalRolls[i]:X2}", byRoll[i]));
            }

            foreach (byte[] corners in byRoll)
            {
                ok &= MeshAssert.IsTrue("B46 a vertical slab shades exactly half the corners",
                    ShadedCornerCount(corners, open[0]) == 2,
                    "A vertical half slab occupies half the cell above, so exactly two of the floor's four "
                    + "top corners should be darkened. Four equal corners means the coverage question is "
                    + "still being asked per face rather than per corner.\n" + patterns);
            }

            for (int a = 0; a < byRoll.Length; a++)
            {
                for (int b = a + 1; b < byRoll.Length; b++)
                {
                    ok &= MeshAssert.IsTrue("B46 rotating the slab moves which corners are shaded",
                        !SameCorners(byRoll[a], byRoll[b]),
                        $"Rolls 0x{s_vo8VerticalRolls[a]:X2} and 0x{s_vo8VerticalRolls[b]:X2} put the slab's "
                        + "solid half against different corners, so they must darken different ones. "
                        + "Identical patterns mean the octant is not being selected from the corner — the "
                        + "shading would be orientation-blind even though its magnitude looks right.\n"
                        + patterns);
                }
            }

            return ok;
        }

        /// <summary>
        /// Meshes a floor block with <paramref name="aboveId"/> standing on it and returns the four
        /// vertex sunlight values of the floor's <c>+Y</c> face.
        /// </summary>
        /// <param name="aboveId">Block placed in the cell above, or <see cref="TestMeshBlockPalette.Air"/> for none.</param>
        /// <param name="aboveMeta">That block's metadata byte, selecting its orientation.</param>
        /// <returns>The four corner sun values, or null when the face was not emitted (already logged).</returns>
        private static byte[] FloorTopCorners(ushort aboveId, byte aboveMeta)
        {
            using MeshingTestWorld world = new MeshingTestWorld();
            world.SetBlock(VO8_FLOOR_X, VO8_FLOOR_Y, VO8_FLOOR_Z, TestMeshBlockPalette.SolidOpaque, 0);
            if (aboveId != TestMeshBlockPalette.Air)
                world.SetBlock(VO8_FLOOR_X, VO8_FLOOR_Y + 1, VO8_FLOOR_Z, aboveId, aboveMeta);

            // Uniform full sunlight isolates the AO term: any per-corner variation is occlusion.
            world.FillLight(LightBitMapping.PackLightData(15, 0, 0, 0));

            MeshDataJobOutput o = world.Run(SmoothLightingQuality.High);

            for (int quad = 0; quad < o.Vertices.Length / 4; quad++)
            {
                Vector3 normal = o.Normals[quad * 4];
                Vector3 vertex = o.Vertices[quad * 4];
                bool isFloorTop = normal.y > 0.5f
                                  && Mathf.Abs(vertex.y - (VO8_FLOOR_Y + 1)) < 0.01f
                                  && vertex.x >= VO8_FLOOR_X - 0.01f && vertex.x <= VO8_FLOOR_X + 1.01f
                                  && vertex.z >= VO8_FLOOR_Z - 0.01f && vertex.z <= VO8_FLOOR_Z + 1.01f;
                if (!isFloorTop) continue;

                byte[] corners = new byte[4];
                for (int i = 0; i < 4; i++) corners[i] = o.LightData[quad * 4 + i].r;
                return corners;
            }

            Debug.LogError($"[FAIL] B46 setup: the floor's +Y face was not emitted with block {aboveId} "
                           + $"(meta 0x{aboveMeta:X2}) above it. Either the fixture culls it or the probe "
                           + "is looking for the wrong quad — the scenario is broken, not the engine.");
            return null;
        }

        /// <summary>Returns true when all four corner values are identical.</summary>
        /// <param name="corners">The four corner values.</param>
        private static bool AllEqual(byte[] corners)
        {
            for (int i = 1; i < corners.Length; i++)
                if (corners[i] != corners[0])
                    return false;

            return true;
        }

        /// <summary>Counts corners darker than the unoccluded reference value.</summary>
        /// <param name="corners">The four corner values.</param>
        /// <param name="unoccluded">The value a corner takes with nothing occluding it.</param>
        private static int ShadedCornerCount(byte[] corners, byte unoccluded)
        {
            int count = 0;
            foreach (byte corner in corners)
                if (corner < unoccluded)
                    count++;

            return count;
        }

        /// <summary>Returns true when two corner sets are identical.</summary>
        /// <param name="a">First corner set.</param>
        /// <param name="b">Second corner set.</param>
        private static bool SameCorners(byte[] a, byte[] b)
        {
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;

            return true;
        }

        /// <summary>Formats a corner set for console diagnostics.</summary>
        /// <param name="label">Label for the configuration it came from.</param>
        /// <param name="corners">The four corner values.</param>
        private static string Describe(string label, byte[] corners)
        {
            return $"    {label}: {corners[0]},{corners[1]},{corners[2]},{corners[3]}\n";
        }
    }
}
