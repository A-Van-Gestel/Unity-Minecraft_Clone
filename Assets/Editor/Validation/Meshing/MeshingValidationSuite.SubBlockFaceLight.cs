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
    /// VO-6 baselines: a custom mesh's face takes its smooth light from the cell that face actually
    /// looks into, not from the block-boundary neighbor.
    /// <para>
    /// Promoted 2026-08-08 from the known-bug scenarios <c>KM01a</c>/<c>KM01b</c>, which reproduced
    /// <c>MESHING_BUGS.md</c> Bug M01 (archived as <c>_FIXED_BUGS.md</c> Meshing #01) and flipped green
    /// when VO-6 landed. They are the permanent regression guard for that fix.
    /// </para>
    /// <para>
    /// <b>Both signs are guarded on purpose.</b> The sampling cell is derived by stepping off the face's
    /// centroid along its normal, and a <i>half-cell</i> step — the value the VO-6 plan originally
    /// specified — lands a mid-plane face exactly on a cell boundary, where rounding resolves toward the
    /// own cell for a negative normal and toward the neighbor for a positive one. Verified by mutation:
    /// with the half-cell step <b>B44 passes and B45 fails</b>. Never reduce this pair to one scenario;
    /// a single repro of a sign-symmetric defect only ever tests one sign.
    /// </para>
    /// </summary>
    public static partial class MeshingValidationSuite
    {
        /// <summary>Registers the VO-6 sub-block face-light baselines (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddSubBlockFaceLightBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B44: a ROTATED half slab's mid-plane face samples the cell in front of the surface, not the block-boundary neighbor (VO-6)",
                B44_RotatedMidPlaneFaceSamplesOwnCell));
            scenarios.Add(new Scenario(
                "B45: an UNROTATED half slab's mid-plane face (+Y normal) does the same — the positive-normal half (VO-6)",
                B45_UnrotatedMidPlaneFaceSamplesOwnCell));
        }

        /// <summary>
        /// B44 — promoted from <c>KM01a</c> (Bug M01, fixed by VO-6 August 2026).
        /// <para>
        /// An isolated half slab at chunk-local (8, 8, 8) is rotated <see cref="MetadataSchema.Facing6Roll2"/>
        /// facing 3 (Bottom) / roll 0 — metadata <c>0x03</c>, one of the four orientations from the reported
        /// screenshot. That rotation maps the mesh's Top face (block-local <c>y = 0.5</c>) onto the plane
        /// <c>z = 0.5</c> with world normal <c>(0, 0, -1)</c>: a <b>mid-plane</b> face, half a block inside its
        /// own cell. The cell physically in front of that surface is the slab's own cell; the block-boundary
        /// neighbor <c>(x, y, z-1)</c> is a full block further away.
        /// </para>
        /// </summary>
        /// <returns>True when the mid-plane face samples the cell it faces.</returns>
        private static bool B44_RotatedMidPlaneFaceSamplesOwnCell()
        {
            // facing 3 (Bottom) | roll 0 << 3 — the reported screenshot's "Left" slab.
            const byte slabMeta = 0x03;

            // The mid-plane face's world normal after that rotation (verified: M * (0,1,0) = (0,0,-1)).
            Vector3 midPlaneNormal = new Vector3(0f, 0f, -1f);

            return AssertMidPlaneFaceSamplesOwnCell("B44", slabMeta, midPlaneNormal);
        }

        /// <summary>
        /// B45 — promoted from <c>KM01b</c>, the positive-normal half of Bug M01.
        /// <para>
        /// An <b>unrotated</b> half slab (metadata <c>0x00</c>, the identity orientation) has its Top face
        /// at block-local <c>y = 0.5</c> with world normal <c>(0, +1, 0)</c> — a mid-plane face exactly
        /// like B44's, but facing the opposite way along its axis. The cell in front of that surface is
        /// the slab's own cell (its open upper half). See the class remarks for why both signs are
        /// guarded separately.
        /// </para>
        /// </summary>
        /// <returns>True when the mid-plane face samples the cell it faces.</returns>
        private static bool B45_UnrotatedMidPlaneFaceSamplesOwnCell()
        {
            // Identity orientation: facing 0 (South) | roll 0. The mesh's Top face stays the world +Y face.
            const byte slabMeta = 0x00;

            Vector3 midPlaneNormal = new Vector3(0f, 1f, 0f);

            return AssertMidPlaneFaceSamplesOwnCell("B45", slabMeta, midPlaneNormal);
        }

        /// <summary>
        /// The shared body of <see cref="B44_RotatedMidPlaneFaceSamplesOwnCell"/> and
        /// <see cref="B45_UnrotatedMidPlaneFaceSamplesOwnCell"/>: the same legs against a mid-plane face
        /// in a given orientation. Shared so the two orientations cannot drift apart in what they assert
        /// — the whole point of B45 is that it differs from B44 <i>only</i> in the face's sign.
        /// <para>
        /// The assertions are <b>differentials</b>, not predicted values, so they need no model of the
        /// engine's corner-offset sampling LUT (the A4 trap the MH-3 oracle notes call out): varying the
        /// light in the cell in front of the surface MUST change that face's emitted vertex light, and
        /// varying the block-boundary neighbor a full cell further away must NOT.
        /// </para>
        /// <para>
        /// <b>The positive control is deliberately not "the boundary neighbor moves the face".</b> That
        /// was the original KM01a leg C, documented as proof the probe worked — but that coupling <i>was</i>
        /// the defect, so the control failed exactly when the fix succeeded and briefly masked the flip.
        /// The control here varies the whole uniform field, which proves the probe observes light at all
        /// without asserting anything about which cell it reads. A positive control must not be
        /// satisfiable by the behavior under test.
        /// </para>
        /// </summary>
        /// <param name="label">Scenario id used in the failure output.</param>
        /// <param name="slabMeta">The slab's <see cref="MetadataSchema.Facing6Roll2"/> metadata byte.</param>
        /// <param name="midPlaneNormal">World normal of the mid-plane face to probe.</param>
        /// <returns>True when the face samples the cell it faces, for this orientation.</returns>
        private static bool AssertMidPlaneFaceSamplesOwnCell(string label, byte slabMeta, Vector3 midPlaneNormal)
        {
            ushort dim = LightBitMapping.PackLightData(8, 0, 0, 0);
            ushort bright = LightBitMapping.PackLightData(15, 0, 0, 0);

            Color32[] reference = RunSlabProbe(slabMeta, midPlaneNormal, dim, null, null, out bool refOk);
            if (!refOk) return false;

            Color32[] brighterField = RunSlabProbe(slabMeta, midPlaneNormal, bright, null, null, out bool fieldOk);
            if (!fieldOk) return false;

            Color32[] ownVaried = RunSlabProbe(slabMeta, midPlaneNormal, dim, bright, null, out bool ownOk);
            if (!ownOk) return false;

            Color32[] frontVaried = RunSlabProbe(slabMeta, midPlaneNormal, dim, null, bright, out bool frontOk);
            if (!frontOk) return false;

            bool ok = MeshAssert.IsTrue($"{label} positive control (the probe can observe light)",
                !SameLight(reference, brighterField),
                "Raising the entire light field from 8 to 15 left the mid-plane face unchanged — the probe "
                + "cannot observe light at all, so the legs below would pass vacuously.\n"
                + DescribeLight("field 8", reference) + DescribeLight("field 15", brighterField));

            ok &= MeshAssert.IsTrue($"{label} the own cell drives the mid-plane face",
                !SameLight(reference, ownVaried),
                $"Brightening the slab's OWN cell — the cell physically in front of the mid-plane face "
                + $"{midPlaneNormal} — left that face's vertex light unchanged. The corner quad is being "
                + "sampled around the block-boundary neighbor instead of the cell the surface faces "
                + "(the Bug M01 regression).\n"
                + DescribeLight("reference", reference) + DescribeLight("own-cell-varied", ownVaried));

            ok &= MeshAssert.IsTrue($"{label} the boundary neighbor does not reach the mid-plane face",
                SameLight(reference, frontVaried),
                $"Brightening the block-boundary neighbor at {midPlaneNormal} — a full cell beyond the "
                + "mid-plane surface, outside the ring that face samples — still changed its vertex light. "
                + "This is the leg a half-cell sampling step gets away with on one sign.\n"
                + DescribeLight("reference", reference) + DescribeLight("front-varied", frontVaried));

            return ok;
        }

        /// <summary>
        /// Meshes a single isolated half slab and returns the four vertex light values of the face whose
        /// world normal is <paramref name="faceNormal"/>.
        /// <para>
        /// Uses <see cref="TestMeshBlockPalette.PartialOpaque"/> (opacity below 15) rather than
        /// <see cref="TestMeshBlockPalette.HalfSlab"/> so these scenarios isolate the <i>mesher</i>
        /// question: with a fully-opaque slab the AO path zeroes its own cell's sample regardless, which
        /// is the separate concern VO-3/VO-5 own.
        /// </para>
        /// </summary>
        /// <param name="meta">The slab's <see cref="MetadataSchema.Facing6Roll2"/> metadata byte.</param>
        /// <param name="faceNormal">World normal identifying the face to probe.</param>
        /// <param name="uniform">Packed light written to every cell before the overrides below.</param>
        /// <param name="ownCellLight">When set, overrides the light in the slab's own cell.</param>
        /// <param name="frontCellLight">When set, overrides the light in the cell at <c>pos + faceNormal</c>.</param>
        /// <param name="ok">False when the probe face could not be located (setup failure, already logged).</param>
        /// <returns>The face's four vertex light values, or null when <paramref name="ok"/> is false.</returns>
        private static Color32[] RunSlabProbe(byte meta, Vector3 faceNormal, ushort uniform,
            ushort? ownCellLight, ushort? frontCellLight, out bool ok)
        {
            using MeshingTestWorld world = new MeshingTestWorld();
            world.SetBlock(PROBE_X, PROBE_Y, PROBE_Z, TestMeshBlockPalette.PartialOpaque, meta);
            world.FillLight(uniform);

            if (ownCellLight.HasValue)
                world.SetLight(PROBE_X, PROBE_Y, PROBE_Z, ownCellLight.Value);

            if (frontCellLight.HasValue)
            {
                world.SetLight(PROBE_X + (int)faceNormal.x, PROBE_Y + (int)faceNormal.y, PROBE_Z + (int)faceNormal.z,
                    frontCellLight.Value);
            }

            MeshDataJobOutput o = world.Run(SmoothLightingQuality.High);

            int quadCount = o.Vertices.Length / 4;
            int start = FindQuadByNormal(o.Normals, quadCount, faceNormal);
            if (start < 0)
            {
                Debug.LogError($"[FAIL] {faceNormal} probe setup: no emitted quad with that normal "
                               + $"(the slab emitted {quadCount} quads / {o.Vertices.Length} verts). "
                               + "The custom-mesh fixture or its rotation is wrong, not the light sampling.");
                ok = false;
                return null;
            }

            Color32[] light = new Color32[4];
            for (int i = 0; i < 4; i++) light[i] = o.LightData[start + i];
            ok = true;
            return light;
        }

        /// <summary>Chunk-local X of the probe slab (interior, so empty neighbor maps never influence culling).</summary>
        private const int PROBE_X = 8;

        /// <summary>Chunk-local Y of the probe slab.</summary>
        private const int PROBE_Y = 8;

        /// <summary>Chunk-local Z of the probe slab.</summary>
        private const int PROBE_Z = 8;

        /// <summary>Returns true when two four-vertex light sets are identical on every channel.</summary>
        /// <param name="a">First light set.</param>
        /// <param name="b">Second light set.</param>
        private static bool SameLight(Color32[] a, Color32[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].r != b[i].r || a[i].g != b[i].g || a[i].b != b[i].b || a[i].a != b[i].a)
                    return false;
            }

            return true;
        }

        /// <summary>Formats a four-vertex light set for console diagnostics.</summary>
        /// <param name="label">Label for the run this set came from.</param>
        /// <param name="light">The four vertex light values.</param>
        private static string DescribeLight(string label, Color32[] light)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("  ").Append(label).Append(": ");
            for (int i = 0; i < light.Length; i++)
                sb.Append($"v{i}(sky={light[i].r} r={light[i].g} g={light[i].b} b={light[i].a}) ");
            return sb.Append('\n').ToString();
        }
    }
}
