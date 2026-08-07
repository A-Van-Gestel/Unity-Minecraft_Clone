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
    /// Known-bug reproductions for the meshing engine — scenarios that assert the <b>correct</b> behavior
    /// and are therefore EXPECTED to fail until the documented bug is fixed (see
    /// <c>Documentation/Bugs/MESHING_BUGS.md</c>). A known-bug failure does not mark the suite red; a
    /// known-bug <i>pass</i> is reported as a fix candidate to verify in-game and then promote to a baseline.
    /// <para>
    /// These deliberately log with <c>Debug.Log</c> rather than <c>Debug.LogError</c>: the suite's fast
    /// green/red signal is "zero console errors == all baselines green", and an expected known-bug failure
    /// must not break that.
    /// </para>
    /// </summary>
    public static partial class MeshingValidationSuite
    {
        /// <summary>Registers the known-bug reproduction scenarios (called from <c>Execute</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddKnownBugScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "KM01a: a rotated half slab's mid-plane face takes its smooth light from the cell in front of the surface, not the block-boundary neighbor",
                KM01a_MidPlaneFaceSamplesOwnCell, "M01"));
        }

        /// <summary>
        /// KM01a — reproduces <c>MESHING_BUGS.md</c> Bug M01.
        /// <para>
        /// An isolated half slab at chunk-local (8, 8, 8) is rotated <see cref="MetadataSchema.Facing6Roll2"/>
        /// facing 3 (Bottom) / roll 0 — metadata <c>0x03</c>, one of the four orientations from the reported
        /// screenshot. That rotation maps the mesh's Top face (block-local <c>y = 0.5</c>) onto the plane
        /// <c>z = 0.5</c> with world normal <c>(0, 0, -1)</c>: a <b>mid-plane</b> face, half a block inside its
        /// own cell. The cell physically in front of that surface is the slab's own cell; the block-boundary
        /// neighbor <c>(x, y, z-1)</c> is a full block further away.
        /// </para>
        /// <para>
        /// The assertion is a <b>differential</b>, not a predicted value — deliberately, so it needs no model
        /// of the engine's corner-offset sampling LUT (the A4 trap the MH-3 oracle notes call out): varying
        /// only the light in the cell in front of the surface MUST change that face's emitted vertex light.
        /// Under the bug it does not, because the corner quad is sampled entirely from the ring around
        /// <c>pos + rotatedOffset</c>, which never includes <c>pos</c> itself.
        /// </para>
        /// <para>
        /// Uses <see cref="TestMeshBlockPalette.PartialOpaque"/> (opacity below 15) rather than
        /// <see cref="TestMeshBlockPalette.HalfSlab"/> so this scenario isolates the <i>mesher</i> defect:
        /// with a fully-opaque slab the mesher zeroes its own cell's sample regardless, which is the separate
        /// <c>LIGHTING_BUGS.md</c> Bug 20. Leg C is a positive control proving the probe can observe a change
        /// at all — if leg C fails, the scenario is broken rather than reproducing the bug.
        /// </para>
        /// </summary>
        /// <returns>True once Bug M01 is fixed; false (expected) while it is open.</returns>
        private static bool KM01a_MidPlaneFaceSamplesOwnCell()
        {
            // facing 3 (Bottom) | roll 0 << 3 — the reported screenshot's "Left" slab.
            const byte slabMeta = 0x03;

            // The mid-plane face's world normal after that rotation (verified: M * (0,1,0) = (0,0,-1)).
            Vector3 midPlaneNormal = new Vector3(0f, 0f, -1f);

            ushort uniform = LightBitMapping.PackLightData(8, 0, 0, 0);
            ushort brighter = LightBitMapping.PackLightData(15, 0, 0, 0);

            Color32[] reference = RunSlabProbe(slabMeta, midPlaneNormal, uniform, ownCellLight: null, frontCellLight: null,
                out bool refOk);
            if (!refOk) return false;

            Color32[] ownVaried = RunSlabProbe(slabMeta, midPlaneNormal, uniform, ownCellLight: brighter, frontCellLight: null,
                out bool ownOk);
            if (!ownOk) return false;

            Color32[] frontVaried = RunSlabProbe(slabMeta, midPlaneNormal, uniform, ownCellLight: null, frontCellLight: brighter,
                out bool frontOk);
            if (!frontOk) return false;

            // Positive control (leg C): brightening the block-boundary neighbor must move the face. If this
            // fails the probe itself is broken — the scenario is not reproducing anything.
            if (SameLight(reference, frontVaried))
            {
                Debug.Log("[FAIL] KM01a positive control: brightening the boundary neighbor (0,0,-1) left the "
                          + "mid-plane face unchanged — the probe cannot observe any light change, so this "
                          + "scenario is broken rather than reproducing Bug M01.\n"
                          + DescribeLight("reference", reference) + DescribeLight("front-varied", frontVaried));
                return false;
            }

            // The actual Bug M01 assertion (leg B): the cell in FRONT of the mid-plane surface is the slab's
            // own cell, so brightening it must change that face's light.
            if (SameLight(reference, ownVaried))
            {
                Debug.Log("[FAIL] KM01a reproduces Bug M01: brightening the slab's OWN cell — the cell physically "
                          + "in front of the mid-plane face — left that face's vertex light completely unchanged. "
                          + "The corner quad is being sampled from the ring around the block-boundary neighbor "
                          + "(pos + (0,0,-1)) instead of the cell the surface actually faces.\n"
                          + DescribeLight("reference", reference) + DescribeLight("own-cell-varied", ownVaried));
                return false;
            }

            return true;
        }

        /// <summary>
        /// Meshes a single isolated half slab and returns the four vertex light values of the face whose
        /// world normal is <paramref name="faceNormal"/>.
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
                Debug.Log($"[FAIL] KM01a setup: no emitted quad with normal {faceNormal} "
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
                sb.Append($"v{i}(sun={light[i].r} r={light[i].g} g={light[i].b} b={light[i].a}) ");
            return sb.Append('\n').ToString();
        }
    }
}
