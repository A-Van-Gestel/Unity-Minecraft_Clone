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
    /// <b>Bit-identity for full cubes</b> is guarded structurally by <see cref="B41_FullCubeSilhouetteIsBinary"/>
    /// (a block without custom bounds occludes all of its cell or none of it, so the partial-occluder
    /// path is unreachable) plus every pre-existing standard-cube baseline staying green — notably B11. There
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
                "B41: a block without custom bounds casts all of its cell or none of it, on every face and orientation (VO-5 bit-identity guard, retargeted by SS-3a)",
                B41_FullCubeSilhouetteIsBinary));
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
        /// For any block without custom bounds, the occlusion query returns <b>all of the cell or none
        /// of it</b> — never a fraction, and identically on every face and orientation. That is what
        /// makes the partial-occluder machinery unreachable for full cubes, and therefore what makes
        /// "no change to full-cube smooth lighting" a structural property rather than a hope.
        /// </para>
        /// <para>
        /// <b>Retargeted by SS-3a onto the live code path.</b> This scenario used to read
        /// <c>AmbientOcclusionOctantCoverage</c>, an octant fill fraction; SS-2 replaced that with a
        /// silhouette rectangle and SS-3a deleted the coverage functions once nothing shaded through
        /// them. The claim is unchanged, and it is now asserted where the mesher actually looks — so it
        /// went on testing a live guarantee instead of quietly guarding dead code.
        /// </para>
        /// <para>
        /// <b>What this adds over the rest of the suite, measured rather than assumed.</b> Neutering the
        /// opacity gate reds this scenario — and ten others with it (B11, B40, B42, B46, B49, B54,
        /// B56–B59), because every air cell then shadows and the change is visible in the output. So
        /// this is <i>not</i> the only guard against that break, and it should not be described as one.
        /// Its distinct value is narrower and real: it reads the <b>primitive directly</b>, so the
        /// failure names the block, plane and metadata byte instead of a shifted light value; and it
        /// sweeps the <b>whole palette</b> against every plane and 37 metadata bytes, where the shading
        /// baselines only ever place <c>SolidOpaque</c>, <c>HalfSlab</c> and <c>Post</c>. A gate or
        /// rotation break confined to a block type no scenario happens to place — <c>TransparentCube</c>,
        /// <c>OrientedOpaque</c>, <c>WaterSource</c> — is caught here and nowhere else.
        /// </para>
        /// <para>
        /// <b>What it does not catch</b> (measured by mutation under VO-5, and still true): removing the
        /// <c>HasCustomBounds</c> short-circuit. The geometry computes the same answer anyway, since a
        /// full cube's authored <c>0..1</c> bounds fill the cell. The binary result is over-determined;
        /// this pins the <i>result</i>, not any one mechanism that produces it.
        /// </para>
        /// </summary>
        /// <returns>True when every full-cube palette block casts its whole cell, and every transparent one casts nothing.</returns>
        private static bool B41_FullCubeSilhouetteIsBinary()
        {
            NativeArray<BlockTypeJobData> palette =
                TestMeshBlockPalette.CreateJobDataNativeArray(Allocator.Temp);

            StringBuilder failures = new StringBuilder();
            for (ushort id = 0; id < TestMeshBlockPalette.Count; id++)
            {
                BlockTypeJobData block = palette[id];
                if (block.HasCustomBounds) continue; // Partial blocks are B42's and B46's subject.

                for (int normalAxis = 0; normalAxis < 3; normalAxis++)
                for (int side = 0; side < 2; side++)
                {
                    bool frontIsPositive = side == 0;

                    // The three planes the mesher asks about: a cell's two walls (a boundary face) and
                    // its midline (a face interior to its own cell, VO-6).
                    foreach (float planeCoord in new[] { frontIsPositive ? 0f : 1f, 0.5f })
                    {
                        // Sweep the whole metadata byte: a full cube must be orientation-independent
                        // here, and a rotation leaking in would show up as a partial rectangle.
                        for (int meta = 0; meta < 256; meta += 7)
                        {
                            bool casts = LightAttenuation.AmbientOcclusionPlaneSilhouette(in block,
                                (byte)meta, normalAxis, planeCoord, frontIsPositive,
                                out float2 rectMin, out float2 rectMax);

                            if (casts != block.IsOpaque)
                            {
                                failures.AppendFormat(
                                    "    block {0} (opaque={1}) axis {2} plane {3} front={4} meta 0x{5:X2}: casts {6}\n",
                                    id, block.IsOpaque, normalAxis, planeCoord, frontIsPositive, meta, casts);
                                continue;
                            }

                            if (!casts) continue;

                            bool whole = Mathf.Approximately(rectMin.x, 0f) && Mathf.Approximately(rectMin.y, 0f)
                                                                            && Mathf.Approximately(rectMax.x, 1f) && Mathf.Approximately(rectMax.y, 1f);
                            if (!whole)
                            {
                                failures.AppendFormat(
                                    "    block {0} axis {1} plane {2} front={3} meta 0x{4:X2}: silhouette [{5} .. {6}], expected the whole cell\n",
                                    id, normalAxis, planeCoord, frontIsPositive, meta, rectMin, rectMax);
                            }
                        }
                    }
                }
            }

            palette.Dispose();

            return MeshAssert.IsTrue("B41 a block without custom bounds casts all of its cell or none",
                failures.Length == 0,
                "A block without custom bounds cast a partial silhouette, or a transparent block cast "
                + "one at all. Either breaks the guarantee that ordinary full-cube terrain never enters "
                + "the partial-occluder path, and with it the claim that its smooth lighting is "
                + "unchanged:\n" + failures);
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

            // Corner-located, not first-quad-located: VO-9b may split this face into sub-quads, and the
            // corner values are the reading that survives any tessellation density (see TopFaceCornerSun).
            byte[] corners = TopFaceCornerSun(o, AO_PROBE_X, AO_PROBE_Y, AO_PROBE_Z);
            if (corners == null) return -1;

            return corners[0] + corners[1] + corners[2] + corners[3];
        }
    }
}
