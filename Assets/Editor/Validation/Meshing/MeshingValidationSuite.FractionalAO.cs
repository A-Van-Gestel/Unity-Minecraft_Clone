using System.Collections.Generic;
using System.Text;
using Data;
using Data.Enums;
using Editor.Validation.Meshing.Framework;
using Jobs.BurstData;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Meshing
{
    /// <summary>
    /// VO-5 baselines: ambient occlusion weighted by how much of a face a partial block actually
    /// covers, instead of the pre-VO-5 all-or-nothing <c>IsOpaque</c> test.
    /// <para>
    /// These assert a <b>strict ordering</b> rather than predicted constants — the A4 trap the MH-3
    /// oracle notes call out. The engine's corner-averaging and UNorm8 encoding would have to be
    /// re-modeled here to predict a value, and that model would then be a second implementation free
    /// to drift. An ordering needs no model and still fails the moment the weighting breaks.
    /// </para>
    /// <para>
    /// <b>Bit-identity for full cubes</b> is guarded structurally by <see cref="B41_FullCubeCoverageIsBinary"/>
    /// (coverage can only ever be 0 or 1 for a block without custom bounds, so the weighting branch is
    /// unreachable) plus every pre-existing standard-cube baseline staying green — notably B11. There
    /// is deliberately no "run it both ways and compare" scenario: the pre-VO-5 path no longer exists,
    /// having been a temporary sign-off toggle, and keeping it alive purely for a test would have put a
    /// dead branch in the engine's hottest loop.
    /// </para>
    /// </summary>
    public static partial class MeshingValidationSuite
    {
        /// <summary>Registers the VO-5 fractional-AO baselines (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddFractionalAoBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B41: a full cube's face coverage is exactly 0 or 1 on every face and orientation (VO-5 bit-identity guard)",
                B41_FullCubeCoverageIsBinary));
            scenarios.Add(new Scenario(
                "B42: a partial block darkens a probe face less than a full cube and more than air, per orientation (VO-5)",
                B42_PartialBlockAoOrdering));
            scenarios.Add(new Scenario(
                "B43: BurstVoxelData.OppositeFace agrees with VoxelData.RevFaceChecksIndices on all 6 faces",
                B43_OppositeFaceMatchesManagedTable));
        }

        /// <summary>Chunk-local coordinates of the probe block whose +Y face the AO scenarios read.</summary>
        private const int AO_PROBE_X = 8;

        /// <summary>Chunk-local Y of the AO probe block.</summary>
        private const int AO_PROBE_Y = 8;

        /// <summary>Chunk-local Z of the AO probe block.</summary>
        private const int AO_PROBE_Z = 8;

        /// <summary>A bottom half slab: solid underside, so it occludes a surface beneath it completely.</summary>
        private const byte AO_SLAB_BOTTOM = 0x00;

        /// <summary>A vertical half slab: covers half the cell's cross-section on the horizontal faces.</summary>
        private const byte AO_SLAB_VERTICAL = 0x03;

        /// <summary>
        /// A top half slab (facing 0, roll 2): its volume sits in the upper half of the cell, so it
        /// touches neither the cell's bottom face nor the surface below it.
        /// </summary>
        private const byte AO_SLAB_TOP = 0x10;

        /// <summary>
        /// B41 — the bit-identity guard, asserted on the mechanism rather than on output.
        /// <para>
        /// For any full-cube block <see cref="LightAttenuation.AmbientOcclusionOctantCoverage"/> returns
        /// exactly 1 (opaque) or exactly 0 (not opaque) — never a fraction, and identically on all eight
        /// octants. That is what makes the weighting branch in <c>SampleNeighborLight</c> unreachable for
        /// full cubes, and therefore what makes "no change to full-cube smooth lighting" a structural
        /// property instead of a hope.
        /// <b>Sweeping all eight octants is what carries this claim across VO-8</b>: the query became
        /// per-corner, so a full cube being uniform now means uniform over octants, not over faces.
        /// </para>
        /// <para>
        /// <b>What this does and does not catch</b> (measured by mutation, 2026-08-08, rather than
        /// assumed). It catches a change to the <b>opacity gate</b> — returning a fraction for a
        /// non-opaque block turns this red, and B11 red with it. It did <b>not</b> catch removing the
        /// <c>HasCustomBounds</c> short-circuit from the coverage entry point when that was tried under
        /// VO-5: the geometry computes the same answer anyway, since a full cube's authored <c>0..1</c>
        /// bounds fill every octant. The binary result is over-determined; this scenario pins the
        /// <i>result</i>, not any one mechanism that produces it.
        /// </para>
        /// </summary>
        /// <returns>True when every full-cube palette block reports binary coverage on every octant.</returns>
        private static bool B41_FullCubeCoverageIsBinary()
        {
            NativeArray<BlockTypeJobData> palette =
                TestMeshBlockPalette.CreateJobDataNativeArray(Allocator.Temp);

            StringBuilder failures = new StringBuilder();
            for (ushort id = 0; id < TestMeshBlockPalette.Count; id++)
            {
                BlockTypeJobData block = palette[id];
                if (block.HasCustomBounds) continue; // Partial blocks are B42's and B46's subject.

                float expected = block.IsOpaque ? 1f : 0f;
                for (int octant = 0; octant < 8; octant++)
                {
                    bool3 lowHalf = new bool3((octant & 1) != 0, (octant & 2) != 0, (octant & 4) != 0);

                    // Sweep the whole metadata byte: a full cube must be orientation-independent here,
                    // and a rotation leaking in would show up as a fractional coverage on some meta.
                    for (int meta = 0; meta < 256; meta += 7)
                    {
                        float actual = LightAttenuation.AmbientOcclusionOctantCoverage(block, (byte)meta, lowHalf);
                        if (!Mathf.Approximately(actual, expected))
                        {
                            failures.AppendFormat(
                                "    block {0} (opaque={1}) octant {2} meta 0x{3:X2}: coverage {4}, expected {5}\n",
                                id, block.IsOpaque, lowHalf, meta, actual, expected);
                        }
                    }
                }
            }

            palette.Dispose();

            return MeshAssert.IsTrue("B41 full-cube coverage is binary", failures.Length == 0,
                "A block without custom bounds returned a fractional AO coverage, so the weighting "
                + "branch in SampleNeighborLight is now reachable for full cubes and their smooth "
                + "lighting is no longer guaranteed unchanged:\n" + failures);
        }

        /// <summary>
        /// B42 — the VO-5 behavior itself: a partial block occludes in proportion to the face fraction
        /// its volume covers, and which fraction that is depends on its orientation.
        /// <para>
        /// A solid cube's <c>+Y</c> face is shaded while an occluder sits in the AO ring above it. The
        /// assertions are ordering-only (see the class remarks). Three orientations of one slab, which
        /// differ <i>only</i> by metadata, must produce three different results:
        /// </para>
        /// <list type="bullet">
        /// <item>a <b>bottom</b> slab rests on the shaded plane — coverage 1, so it must darken exactly
        /// as much as a full cube;</item>
        /// <item>a <b>vertical</b> slab covers half the cross-section — strictly between air and cube;</item>
        /// <item>a <b>top</b> slab floats clear of the surface — coverage 0, so it must not darken at all.</item>
        /// </list>
        /// <para>
        /// The air-vs-cube leg is a positive control: if a full cube does not darken the probe, the
        /// probe cannot observe occlusion and every other leg would pass vacuously. The top-slab leg is
        /// what discriminates D5's chosen rule (ask the sampled cell for the face turned toward the
        /// shaded surface) from the rejected alternative (average coverage over all six faces), which
        /// would score all three orientations identically at 0.5.
        /// </para>
        /// </summary>
        /// <returns>True when the ordering holds for every orientation.</returns>
        private static bool B42_PartialBlockAoOrdering()
        {
            int air = ProbeTopFaceSun(TestMeshBlockPalette.Air, 0);
            int cube = ProbeTopFaceSun(TestMeshBlockPalette.SolidOpaque, 0);
            int slabBottom = ProbeTopFaceSun(TestMeshBlockPalette.HalfSlab, AO_SLAB_BOTTOM);
            int slabVertical = ProbeTopFaceSun(TestMeshBlockPalette.HalfSlab, AO_SLAB_VERTICAL);
            int slabTop = ProbeTopFaceSun(TestMeshBlockPalette.HalfSlab, AO_SLAB_TOP);

            string readings = string.Format(
                "    air={0} topSlab={1} verticalSlab={2} bottomSlab={3} fullCube={4}",
                air, slabTop, slabVertical, slabBottom, cube);

            if (air < 0 || cube < 0 || slabBottom < 0 || slabVertical < 0 || slabTop < 0)
            {
                return MeshAssert.IsTrue("B42 probe located the shaded face", false,
                    "At least one configuration emitted no +Y face for the probe block, so the "
                    + "scenario is broken rather than measuring occlusion.\n" + readings);
            }

            bool ok = MeshAssert.IsTrue("B42 positive control (a full cube darkens the probe)",
                cube < air,
                "A full cube in the AO ring did not darken the probe face, so this probe cannot "
                + "observe occlusion at all and every ordering leg below would pass vacuously.\n"
                + readings);

            ok &= MeshAssert.IsTrue("B42 vertical slab darkens less than a full cube",
                slabVertical > cube,
                "A vertical half slab covers only half the cell cross-section, so it must darken the "
                + "probe strictly less than a full cube does.\n" + readings);

            ok &= MeshAssert.IsTrue("B42 vertical slab darkens more than air",
                slabVertical < air,
                "A vertical half slab still covers half the face, so it must darken the probe "
                + "strictly more than empty space does.\n" + readings);

            ok &= MeshAssert.IsTrue("B42 bottom slab occludes like a full cube",
                slabBottom == cube,
                "A bottom half slab's underside covers the shaded plane completely (coverage 1), so it "
                + "must be indistinguishable from a full cube here. A difference means coverage is "
                + "being read from the wrong face.\n" + readings);

            ok &= MeshAssert.IsTrue("B42 top slab does not occlude the surface below it",
                slabTop == air,
                "A top half slab's volume does not reach the cell's bottom face, so it cannot occlude "
                + "a surface beneath it. Darkening here means the AO sample is asking the block about "
                + "the wrong face — the orientation-blind failure mode D5 rejected.\n" + readings);

            return ok;
        }

        /// <summary>
        /// B43 — drift guard for <see cref="BurstVoxelData.OppositeFace"/>, which exists only because
        /// <c>VoxelData.RevFaceChecksIndices</c> is a managed array a Burst job cannot read. The two
        /// must agree, or the AO path silently samples the wrong face while every full-cube test stays
        /// green (full cubes cover all six faces, so a wrong index is invisible on them).
        /// </summary>
        /// <returns>True when the Burst helper matches the managed table on all six faces.</returns>
        private static bool B43_OppositeFaceMatchesManagedTable()
        {
            StringBuilder failures = new StringBuilder();
            for (int face = 0; face < VoxelData.RevFaceChecksIndices.Length; face++)
            {
                int burst = BurstVoxelData.OppositeFace(face);
                int managed = VoxelData.RevFaceChecksIndices[face];
                if (burst != managed)
                    failures.AppendFormat("    face {0}: Burst says {1}, managed table says {2}\n", face, burst, managed);
            }

            return MeshAssert.IsTrue("B43 OppositeFace matches RevFaceChecksIndices", failures.Length == 0,
                "The Burst-safe opposite-face helper has drifted from the managed table it mirrors:\n"
                + failures);
        }

        /// <summary>
        /// Meshes a lone opaque cube with <paramref name="ringId"/> placed in the ambient-occlusion ring
        /// above it, and returns the sum of the four vertex sunlight values on the cube's <c>+Y</c> face.
        /// Lower means more darkening; 1020 is fully lit.
        /// <para>
        /// The occluder goes in the ring at <c>(+1, +1, 0)</c> rather than directly overhead, because a
        /// block directly overhead culls the probe face instead of shading it.
        /// </para>
        /// </summary>
        /// <param name="ringId">The occluder's block ID, or <see cref="TestMeshBlockPalette.Air"/> for none.</param>
        /// <param name="ringMeta">The occluder's metadata byte, selecting its orientation.</param>
        /// <returns>The summed vertex sunlight, or -1 when the probe face was not emitted.</returns>
        private static int ProbeTopFaceSun(ushort ringId, byte ringMeta)
        {
            using MeshingTestWorld world = new MeshingTestWorld();
            world.SetBlock(AO_PROBE_X, AO_PROBE_Y, AO_PROBE_Z, TestMeshBlockPalette.SolidOpaque, 0);
            if (ringId != TestMeshBlockPalette.Air)
                world.SetBlock(AO_PROBE_X + 1, AO_PROBE_Y + 1, AO_PROBE_Z, ringId, ringMeta);

            // Uniform full sunlight everywhere isolates the AO term: any variation the probe sees is
            // occlusion weighting, never a light gradient.
            world.FillLight(LightBitMapping.PackLightData(15, 0, 0, 0));

            MeshDataJobOutput o = world.Run(SmoothLightingQuality.High);

            for (int quad = 0; quad < o.Vertices.Length / 4; quad++)
            {
                Vector3 normal = o.Normals[quad * 4];
                Vector3 vertex = o.Vertices[quad * 4];
                bool isProbeTopFace = normal.y > 0.5f
                                      && Mathf.Abs(vertex.y - (AO_PROBE_Y + 1)) < 0.01f
                                      && vertex.x >= AO_PROBE_X - 0.01f && vertex.x <= AO_PROBE_X + 1.01f
                                      && vertex.z >= AO_PROBE_Z - 0.01f && vertex.z <= AO_PROBE_Z + 1.01f;
                if (!isProbeTopFace) continue;

                int sum = 0;
                for (int i = 0; i < 4; i++) sum += o.LightData[quad * 4 + i].r;
                return sum;
            }

            return -1;
        }
    }
}
