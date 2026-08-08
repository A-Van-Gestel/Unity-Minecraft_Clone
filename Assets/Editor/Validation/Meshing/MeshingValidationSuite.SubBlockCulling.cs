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
    /// B48: a custom mesh's face is culled by the cell that face actually looks into, not by the
    /// block-boundary neighbor.
    /// <para>
    /// Promoted 2026-08-08 from the known-bug scenario <c>KM02</c>, which reproduced
    /// <c>MESHING_BUGS.md</c> Bug M02 and flipped green when the cull check moved onto
    /// <c>MeshGenerationJob.ResolveFaceSampleCell</c>. It is the permanent regression guard for that fix.
    /// </para>
    /// <para>
    /// This is the <i>culling</i> twin of the sub-block face-light baselines B44/B45 next door: same
    /// wrong-cell confusion, same derivation, different decision. Both families must move together if
    /// the sampling rule ever changes.
    /// </para>
    /// </summary>
    public static partial class MeshingValidationSuite
    {
        /// <summary>Registers the sub-block culling baseline (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddSubBlockCullingBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B48: a custom mesh's mid-plane face survives a solid block at the block-boundary neighbor, while its boundary faces still cull (M02)",
                B48_MidPlaneFaceSurvivesBoundaryNeighbor));
        }

        /// <summary>Chunk-local X of the probe slab (interior, so empty neighbor maps never influence culling).</summary>
        private const int B48_X = 8;

        /// <summary>Chunk-local Y of the probe slab.</summary>
        private const int B48_Y = 8;

        /// <summary>Chunk-local Z of the probe slab.</summary>
        private const int B48_Z = 8;

        /// <summary>Identity orientation: facing 0 (South) / roll 0. The mesh's Top face stays the world +Y face.</summary>
        private const byte B48_META_IDENTITY = 0x00;

        /// <summary>
        /// Facing 3 (Bottom) / roll 0 — the orientation <c>MESHING_BUGS.md</c> Bug M02 reports. It maps the
        /// mesh's Top face onto the plane <c>z = 0.5</c> with world normal <c>(0, 0, -1)</c>, leaving the
        /// slab's solid half at <c>z ∈ [0.5, 1]</c>.
        /// </summary>
        private const byte B48_META_FACING_BOTTOM = 0x03;

        /// <summary>Position tolerance when matching an emitted quad against a queried plane.</summary>
        private const float B48_TOLERANCE = 0.01f;

        /// <summary>
        /// B48 — promoted from <c>KM02</c> (Bug M02, fixed August 2026). A half slab's mid-plane face is
        /// half a block <i>inside</i> its own cell, with the cell's open half between it and the
        /// block-boundary neighbor, so a full block a whole cell away cannot occlude it.
        /// <para>
        /// Under the bug both custom-mesh paths decided visibility with
        /// <c>ShouldDrawFace(voxelProps, GetVoxelStateFromLocalPos(pos + rotatedOffset))</c> regardless of
        /// where the face sat inside the cell. The documented numbers: the slab at meta <c>0x03</c> emits
        /// its mid-plane face at <c>z = 8.50</c> when isolated, and emitted nothing once a solid block was
        /// placed at <c>(8, 8, 7)</c> — a player-facing hole, since the surface is visible through the
        /// slab's open half from a grazing angle.
        /// </para>
        /// <para>
        /// <b>The scenario is deliberately two-sided.</b> Legs 1–2 assert that a mid-plane face is NOT
        /// culled by the boundary neighbor (the legs that were red before the fix); legs 3–5 assert that
        /// <i>boundary</i> faces still ARE culled by theirs — the guard that stops this being "fixed" by
        /// weakening the cull test into always drawing. Legs 4 and 5 use the fully walled-in
        /// configuration rather than the isolated one — the suite gap of exactly that shape is what let
        /// Bug M03 ship with 429 baselines green and the artifact plainly visible on screen.
        /// </para>
        /// <para>
        /// Both signs of the mid-plane normal are covered (leg 1 negative, leg 2 positive) for the reason
        /// recorded on B44/B45: a single repro of a sign-symmetric defect only ever tests one sign.
        /// </para>
        /// <para>
        /// Uses <see cref="TestMeshBlockPalette.HalfSlab"/>, whose <c>isSolid</c> /
        /// <c>renderNeighborFaces</c> / <c>opacity</c> mirror the production <c>Stone Half Slab</c> — the
        /// culling decision reads exactly those fields, so a fixture with corrected authoring would not
        /// reproduce the bug at all.
        /// </para>
        /// </summary>
        /// <returns>True when mid-plane faces ignore the boundary neighbor and boundary faces do not.</returns>
        private static bool B48_MidPlaneFaceSurvivesBoundaryNeighbor()
        {
            // The slab's cell, in chunk-local space. Every query below pins one face plane exactly and
            // requires the other two axes to stay inside this footprint, which makes each query unique
            // without depending on where the rotation puts the slab's solid half.
            const float lo = 8f;
            const float hi = 9f;
            const float mid = 8.5f;

            SlabFaceQuery midPlaneTop = new SlabFaceQuery("mid-plane +Y face (y = 8.50)",
                Vector3.up, new Vector3(lo, mid, lo), new Vector3(hi, mid, hi));
            SlabFaceQuery midPlaneBack = new SlabFaceQuery("mid-plane -Z face (z = 8.50)",
                Vector3.back, new Vector3(lo, lo, mid), new Vector3(hi, hi, mid));

            // --- Control: the probe finds a mid-plane face on an isolated slab, in both orientations.
            // This leg is satisfied identically before and after the fix, so it cannot be satisfied BY
            // the behavior under test (the F15 lesson from KM01a's original leg C).
            bool[] loneRotated = RunSlabCullProbe(B48_META_FACING_BOTTOM, null,
                new[] { midPlaneBack }, out string loneRotatedDump);
            bool[] loneIdentity = RunSlabCullProbe(B48_META_IDENTITY, null,
                new[] { midPlaneTop }, out string loneIdentityDump);

            bool ok = MeshAssert.IsTrue("B48 control: an isolated slab emits its mid-plane face (meta 0x03)",
                loneRotated[0],
                "The rotated slab did not emit a face on the plane z = 8.50 with normal (0,0,-1) even when "
                + "completely isolated. The fixture, its rotation, or the query is wrong — the scenario is "
                + "broken, not the engine.\n" + loneRotatedDump);

            ok &= MeshAssert.IsTrue("B48 control: an isolated slab emits its mid-plane face (meta 0x00)",
                loneIdentity[0],
                "The unrotated slab did not emit a face on the plane y = 8.50 with normal (0,+1,0) even "
                + "when completely isolated. The fixture or the query is wrong — the scenario is broken, "
                + "not the engine.\n" + loneIdentityDump);

            if (!ok) return false; // The legs below would report on a fixture that is already broken.

            // --- Leg 1: the documented case. Negative-normal mid-plane face, solid block at (8, 8, 7).
            bool[] blockedRotated = RunSlabCullProbe(B48_META_FACING_BOTTOM,
                new[] { new Vector3Int(0, 0, -1) }, new[] { midPlaneBack }, out string blockedRotatedDump);

            ok &= MeshAssert.IsTrue("B48 a solid block a full cell away does not cull the mid-plane face (meta 0x03)",
                blockedRotated[0],
                "Placing a solid block at (8, 8, 7) deleted the slab's mid-plane face at z = 8.50 — a full "
                + "cell beyond it, with the slab's own open half in between. The surface is visible through "
                + "that open half from a grazing angle, so this renders in game as a hole. Face visibility "
                + "is being decided against pos + rotatedOffset instead of the cell the face actually "
                + "looks into (ResolveFaceSampleCell).\n" + blockedRotatedDump);

            // --- Leg 2: the same defect on the opposite sign of the face normal.
            bool[] blockedIdentity = RunSlabCullProbe(B48_META_IDENTITY,
                new[] { new Vector3Int(0, 1, 0) }, new[] { midPlaneTop }, out string blockedIdentityDump);

            ok &= MeshAssert.IsTrue("B48 a solid block a full cell away does not cull the mid-plane face (meta 0x00)",
                blockedIdentity[0],
                "Placing a solid block at (8, 9, 8) deleted the slab's mid-plane face at y = 8.50. Same "
                + "defect as the leg above, on the positive-normal half — guarded separately because a "
                + "single repro of a sign-symmetric defect only ever tests one sign.\n" + blockedIdentityDump);

            // --- Leg 3: the counter-assertion. A BOUNDARY face must still be culled by its own neighbor.
            SlabFaceQuery boundaryBottom = new SlabFaceQuery("boundary -Y face (y = 8.00)",
                Vector3.down, new Vector3(lo, lo, lo), new Vector3(hi, lo, hi));

            bool[] flooredIdentity = RunSlabCullProbe(B48_META_IDENTITY,
                new[] { new Vector3Int(0, -1, 0) }, new[] { boundaryBottom, midPlaneTop },
                out string flooredDump);

            ok &= MeshAssert.IsTrue("B48 a boundary face is still culled by its block-boundary neighbor",
                !flooredIdentity[0],
                "The slab's -Y face sits ON the cell boundary at y = 8.00, so the solid block at (8, 7, 8) "
                + "is flush against it and must cull it. It was emitted anyway — the cull test has been "
                + "weakened into drawing everything, which would trade this bug for hidden geometry "
                + "everywhere.\n" + flooredDump);

            ok &= MeshAssert.IsTrue("B48 flooring the slab leaves its mid-plane face alone",
                flooredIdentity[1],
                "A solid block below the slab removed its mid-plane top face, which faces the other way "
                + "entirely.\n" + flooredDump);

            // --- Legs 4 and 5: the embedded configuration, in both orientations. Walled in on all six
            // sides, exactly one face may survive — the mid-plane one — and every boundary face must go.
            ok &= AssertWalledSlabEmitsOnlyItsMidPlaneFace(B48_META_IDENTITY, midPlaneTop, lo, hi);
            ok &= AssertWalledSlabEmitsOnlyItsMidPlaneFace(B48_META_FACING_BOTTOM, midPlaneBack, lo, hi);

            return ok;
        }

        /// <summary>
        /// Walls a slab in on all six sides and asserts that the only face it still emits is its
        /// mid-plane one: every face lying on a cell boundary is flush against a solid neighbor and must
        /// be culled, while the mid-plane face keeps the cell's open half in front of it.
        /// <para>
        /// This is the against-a-wall configuration from Bug M02's reproduction steps, and it is the one
        /// that also pins the <i>rotated</i> frame: for meta <c>0x03</c> the five boundary planes are
        /// reached only if the cull test rotates with the geometry.
        /// </para>
        /// </summary>
        /// <param name="meta">The slab's <see cref="MetadataSchema.Facing6Roll2"/> metadata byte.</param>
        /// <param name="midPlane">The mid-plane face expected to survive in this orientation.</param>
        /// <param name="lo">Low coordinate of the slab's cell on every axis.</param>
        /// <param name="hi">High coordinate of the slab's cell on every axis.</param>
        /// <returns>True when exactly the mid-plane face survives.</returns>
        private static bool AssertWalledSlabEmitsOnlyItsMidPlaneFace(byte meta, SlabFaceQuery midPlane,
            float lo, float hi)
        {
            SlabFaceQuery[] queries =
            {
                midPlane,
                new SlabFaceQuery("boundary -Y face (y = 8.00)", Vector3.down,
                    new Vector3(lo, lo, lo), new Vector3(hi, lo, hi)),
                new SlabFaceQuery("boundary +Y face (y = 9.00)", Vector3.up,
                    new Vector3(lo, hi, lo), new Vector3(hi, hi, hi)),
                new SlabFaceQuery("boundary -Z face (z = 8.00)", Vector3.back,
                    new Vector3(lo, lo, lo), new Vector3(hi, hi, lo)),
                new SlabFaceQuery("boundary +Z face (z = 9.00)", Vector3.forward,
                    new Vector3(lo, lo, hi), new Vector3(hi, hi, hi)),
                new SlabFaceQuery("boundary -X face (x = 8.00)", Vector3.left,
                    new Vector3(lo, lo, lo), new Vector3(lo, hi, hi)),
                new SlabFaceQuery("boundary +X face (x = 9.00)", Vector3.right,
                    new Vector3(hi, lo, lo), new Vector3(hi, hi, hi)),
            };

            Vector3Int[] wall =
            {
                new Vector3Int(0, -1, 0), new Vector3Int(0, 1, 0),
                new Vector3Int(0, 0, -1), new Vector3Int(0, 0, 1),
                new Vector3Int(-1, 0, 0), new Vector3Int(1, 0, 0),
            };

            bool[] present = RunSlabCullProbe(meta, wall, queries, out string dump);

            bool ok = MeshAssert.IsTrue($"B48 a walled-in slab (meta 0x{meta:X2}) still emits its {midPlane.Name}",
                present[0],
                "The slab is surrounded on all six sides, but its mid-plane face keeps the cell's own open "
                + "half in front of it — nothing outside the cell can reach it. This is the configuration "
                + "Bug M02's reproduction steps describe (a slab against a wall, viewed along the wall).\n"
                + dump);

            for (int i = 1; i < queries.Length; i++)
            {
                ok &= MeshAssert.IsTrue(
                    $"B48 a walled-in slab (meta 0x{meta:X2}) culls its {queries[i].Name}",
                    !present[i],
                    "That face lies on a cell boundary with a solid block flush against it, so it must be "
                    + "culled. Emitting it means the visibility test stopped asking the neighbor that is "
                    + "actually there — for a rotated slab, that the cull direction no longer rotates with "
                    + "the geometry.\n" + dump);
            }

            return ok;
        }

        /// <summary>
        /// Meshes a half slab at the probe position with optional solid neighbors, and reports which of
        /// the queried faces were emitted.
        /// </summary>
        /// <param name="meta">The slab's <see cref="MetadataSchema.Facing6Roll2"/> metadata byte.</param>
        /// <param name="solidNeighbors">Offsets from the probe position to fill with
        /// <see cref="TestMeshBlockPalette.SolidOpaque"/>, or null for an isolated slab.</param>
        /// <param name="queries">The faces to look for.</param>
        /// <param name="dump">A listing of every emitted quad, for failure diagnostics.</param>
        /// <returns>One flag per query, in the same order.</returns>
        private static bool[] RunSlabCullProbe(byte meta, Vector3Int[] solidNeighbors,
            SlabFaceQuery[] queries, out string dump)
        {
            using MeshingTestWorld world = new MeshingTestWorld();
            world.SetBlock(B48_X, B48_Y, B48_Z, TestMeshBlockPalette.HalfSlab, meta);

            if (solidNeighbors != null)
            {
                foreach (Vector3Int offset in solidNeighbors)
                {
                    world.SetBlock(B48_X + offset.x, B48_Y + offset.y, B48_Z + offset.z,
                        TestMeshBlockPalette.SolidOpaque);
                }
            }

            world.FillLight(LightBitMapping.PackLightData(15, 0, 0, 0));
            MeshDataJobOutput o = world.Run(SmoothLightingQuality.High);

            bool[] present = new bool[queries.Length];
            for (int i = 0; i < queries.Length; i++) present[i] = HasQuadOnPlane(o, queries[i]);

            dump = DescribeQuads(meta, solidNeighbors, o);
            return present;
        }

        /// <summary>
        /// Returns true when some emitted quad carries the queried normal and has all four of its
        /// vertices inside the queried box. Each query pins one face plane exactly and bounds the other
        /// two axes to the slab's own cell, so a neighbor block's face cannot satisfy it: a neighbor
        /// facing the slab shares the plane but carries the opposite normal, and a neighbor beside the
        /// slab has vertices outside the cell footprint.
        /// </summary>
        /// <param name="o">The meshing job output to search.</param>
        /// <param name="query">The face to look for.</param>
        private static bool HasQuadOnPlane(MeshDataJobOutput o, SlabFaceQuery query)
        {
            int quadCount = o.Vertices.Length / 4;

            for (int q = 0; q < quadCount; q++)
            {
                if (Vector3.Distance(o.Normals[q * 4], query.Normal) > B48_TOLERANCE) continue;

                bool inside = true;
                for (int v = 0; v < 4; v++)
                {
                    Vector3 p = o.Vertices[q * 4 + v];
                    inside &= p.x >= query.Min.x - B48_TOLERANCE && p.x <= query.Max.x + B48_TOLERANCE
                                                                  && p.y >= query.Min.y - B48_TOLERANCE && p.y <= query.Max.y + B48_TOLERANCE
                                                                  && p.z >= query.Min.z - B48_TOLERANCE && p.z <= query.Max.z + B48_TOLERANCE;
                }

                if (inside) return true;
            }

            return false;
        }

        /// <summary>Formats every emitted quad (normal + corner span) for failure diagnostics.</summary>
        /// <param name="meta">The slab metadata the run used.</param>
        /// <param name="solidNeighbors">The solid neighbor offsets the run used, or null.</param>
        /// <param name="o">The meshing job output to describe.</param>
        private static string DescribeQuads(byte meta, Vector3Int[] solidNeighbors, MeshDataJobOutput o)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append($"  fixture: slab meta 0x{meta:X2} at ({B48_X},{B48_Y},{B48_Z}), solid at ");
            if (solidNeighbors == null || solidNeighbors.Length == 0) sb.Append("nothing (isolated)");
            else
            {
                foreach (Vector3Int offset in solidNeighbors)
                    sb.Append($"({B48_X + offset.x},{B48_Y + offset.y},{B48_Z + offset.z}) ");
            }

            sb.Append('\n');

            int quadCount = o.Vertices.Length / 4;
            sb.Append($"  emitted {quadCount} quads / {o.Vertices.Length} verts:\n");
            for (int q = 0; q < quadCount; q++)
            {
                Vector3 min = o.Vertices[q * 4];
                Vector3 max = min;
                for (int v = 1; v < 4; v++)
                {
                    Vector3 p = o.Vertices[q * 4 + v];
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }

                sb.Append($"    n={o.Normals[q * 4]} span {min} .. {max}\n");
            }

            return sb.ToString();
        }

        /// <summary>
        /// One face lookup: a normal plus the axis-aligned box every one of the quad's four vertices
        /// must lie in. The box pins the face's own plane exactly and bounds the other two axes to the
        /// slab's cell.
        /// </summary>
        private readonly struct SlabFaceQuery
        {
            /// <summary>Human-readable face name, used in the assertion text.</summary>
            public readonly string Name;

            /// <summary>The world normal the emitted quad must carry.</summary>
            public readonly Vector3 Normal;

            /// <summary>Low corner of the box every vertex must lie in.</summary>
            public readonly Vector3 Min;

            /// <summary>High corner of the box every vertex must lie in.</summary>
            public readonly Vector3 Max;

            /// <summary>Initializes a face lookup.</summary>
            /// <param name="name">Human-readable face name.</param>
            /// <param name="normal">The world normal the quad must carry.</param>
            /// <param name="min">Low corner of the containing box.</param>
            /// <param name="max">High corner of the containing box.</param>
            public SlabFaceQuery(string name, Vector3 normal, Vector3 min, Vector3 max)
            {
                Name = name;
                Normal = normal;
                Min = min;
                Max = max;
            }
        }
    }
}
