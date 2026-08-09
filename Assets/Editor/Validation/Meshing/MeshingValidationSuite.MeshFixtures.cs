using System.Collections.Generic;
using System.Text;
using Data;
using Editor.Validation.Meshing.Framework;
using Jobs.BurstData;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Meshing
{
    /// <summary>
    /// SS-0 fixture-integrity baselines: every custom-mesh fixture's authored volume agrees with the
    /// geometry actually flattened for it, and the post fixture really is a shape the corner-blended
    /// occlusion model cannot express.
    /// </summary>
    public static partial class MeshingValidationSuite
    {
        /// <summary>Registers the SS-0 fixture baselines (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddMeshFixtureBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B50: custom-mesh fixtures' authored bounds match their flattened geometry, and the post's occlusion is non-monotonic across its cell (SS-0)",
                B50_CustomMeshFixtureIntegrity));
        }

        /// <summary>
        /// B50 — the two properties every later <c>SS-*</c> baseline is built on.
        /// <list type="number">
        /// <item><b>Bounds match geometry.</b> For each custom-mesh palette block, the authored
        /// <see cref="BlockTypeJobData.BoundsMin"/>/<see cref="BlockTypeJobData.BoundsMax"/> must equal
        /// the extents of the vertices actually flattened for its mesh. <b>This is finding F13 made
        /// checkable:</b> the meshing palette once carried a slab in geometry and a full cube in shape,
        /// and nothing could see it until VO-5 first asked a shape question. SS-0 removed the second
        /// source of truth (both sides now read one <see cref="BlockCollisionBounds"/> value), so this
        /// leg guards against a future hand-edit reintroducing one — it is not the primary defense.</item>
        /// <item><b>The post is a fixture the current model cannot express.</b> Sweeping the mesher's
        /// own occlusion query across the cell, the post's coverage is <b>non-monotonic</b> — an interior
        /// sample strictly exceeds both endpoints — which no blend of two corner values can reproduce at
        /// any tessellation density. Two positive controls sit beside it so the reading cannot be
        /// sweep-machinery noise: a full cube's sweep is constant, and a vertical half slab's is
        /// monotonic.</item>
        /// </list>
        /// <para>
        /// <b>Recorded finding (SS-0, 2026-08-09).</b> The design predicted the post would separate from
        /// the slab by being "non-linear where the slab is linear". Measured, that is not what happens:
        /// the slab's sweep departs from an endpoint-linear fit by <c>0.083</c> and the post's by only
        /// <c>0.038</c> — the slab is the <i>more</i> non-linear of the two. What actually distinguishes
        /// the post is the <i>shape</i> of the departure, and the cause is that
        /// <c>GetRegionCoverage</c> normalizes by the query region's own volume: near a cell edge the
        /// region is clipped and shrinks, which inflates the fraction and produces a rise where distance
        /// to the occluder says there should be a fall. Monotonicity, not linearity, is therefore the
        /// discriminating property, and it is what this leg asserts.
        /// </para>
        /// </summary>
        /// <returns>True when both fixture properties hold.</returns>
        private static bool B50_CustomMeshFixtureIntegrity()
        {
            bool ok = B50_BoundsMatchGeometry();
            ok &= B50_PostOcclusionIsNonMonotonic();
            return ok;
        }

        /// <summary>
        /// Leg 1 — every custom-mesh block's authored volume equals the extents of its flattened mesh.
        /// </summary>
        /// <returns>True when every custom-mesh fixture agrees with its geometry.</returns>
        private static bool B50_BoundsMatchGeometry()
        {
            TestCustomMeshLibrary.Build(Allocator.Temp,
                out NativeArray<CustomMeshData> meshes, out NativeArray<CustomFaceData> faces,
                out NativeArray<CustomVertData> verts, out NativeArray<int> tris);
            NativeArray<BlockTypeJobData> palette =
                TestMeshBlockPalette.CreateJobDataNativeArray(Allocator.Temp);

            StringBuilder failures = new StringBuilder();
            int checkedBlocks = 0;

            for (ushort id = 0; id < TestMeshBlockPalette.Count; id++)
            {
                BlockTypeJobData block = palette[id];
                if (block.RenderShape != RenderShape.CustomMesh || block.CustomMeshIndex < 0) continue;

                checkedBlocks++;
                MeshVertexExtents(in meshes, in faces, in verts, block.CustomMeshIndex,
                    out float3 geomMin, out float3 geomMax);

                if (math.all(math.abs(geomMin - block.BoundsMin) <= FIXTURE_BOUNDS_EPSILON)
                    && math.all(math.abs(geomMax - block.BoundsMax) <= FIXTURE_BOUNDS_EPSILON))
                {
                    continue;
                }

                failures.AppendFormat(
                    "    block {0} (mesh {1}): authored [{2} .. {3}], geometry [{4} .. {5}]\n",
                    id, block.CustomMeshIndex, block.BoundsMin, block.BoundsMax, geomMin, geomMax);
            }

            meshes.Dispose();
            faces.Dispose();
            verts.Dispose();
            tris.Dispose();
            palette.Dispose();

            // Without this the leg passes vacuously the day someone drops the custom-mesh fixtures.
            if (!MeshAssert.IsTrue("B50 the fixture sweep found custom-mesh blocks to check",
                    checkedBlocks >= 3,
                    $"Only {checkedBlocks} custom-mesh blocks were found in the palette. This leg asserts "
                    + "nothing if the fixtures it sweeps are gone."))
            {
                return false;
            }

            return MeshAssert.IsTrue("B50 custom-mesh fixtures' bounds match their geometry",
                failures.Length == 0,
                "A fixture's authored collisionBounds disagrees with the mesh flattened for it, so it is "
                + "one shape to the mesher and another to every shape query. That is finding F13, and it "
                + "stayed invisible for a whole arc last time.\n" + failures);
        }

        /// <summary>
        /// Leg 2 — the post's occlusion of a neighboring face is non-monotonic across the cell, while a
        /// full cube's is constant and a vertical slab's is monotonic.
        /// </summary>
        /// <returns>True when the post separates from both controls.</returns>
        private static bool B50_PostOcclusionIsNonMonotonic()
        {
            NativeArray<BlockTypeJobData> palette =
                TestMeshBlockPalette.CreateJobDataNativeArray(Allocator.Temp);

            // Sweep along Z for the slab (its solid half is on Z under meta 0x03) and along X for the
            // post (symmetric, so either axis serves). Axis 1 (Y) is the shaded face's normal.
            float[] cube = SweepCoverage(palette[TestMeshBlockPalette.SolidOpaque], 0x00, sweepAxis: 2);
            float[] slab = SweepCoverage(palette[TestMeshBlockPalette.HalfSlab], 0x03, sweepAxis: 2);
            float[] post = SweepCoverage(palette[TestMeshBlockPalette.Post], 0x00, sweepAxis: 0);
            palette.Dispose();

            bool ok = MeshAssert.IsTrue("B50 control: a full cube's coverage sweep is constant",
                IsConstant(cube),
                "A full cube fills every query region, so its sweep must not vary. It does — the sweep "
                + $"machinery itself is wrong, and leg 2's reading means nothing.\n{Format(cube)}");

            ok &= MeshAssert.IsTrue("B50 control: a vertical slab's coverage sweep is monotonic",
                IsMonotonic(slab),
                "A half slab's occlusion must rise steadily across the cell toward its solid half. A "
                + "non-monotonic reading here would mean the post's is not the property under test.\n"
                + Format(slab));

            ok &= MeshAssert.IsTrue("B50 the post's coverage sweep is NOT monotonic",
                !IsMonotonic(post),
                "The post fixture exists so the suite has an occluder whose contribution across a cell "
                + "cannot be reproduced by interpolating two corner values. If its sweep is monotonic, a "
                + "corner blend can express it and every later SS-* baseline built on this fixture is "
                + $"weaker than it reads.\n{Format(post)}");

            return ok;
        }

        /// <summary>
        /// Samples the mesher's occlusion query at points across a cell, mirroring how
        /// <c>MeshGenerationJob</c> builds its region for a <c>+Y</c> face below the sampled cell: the
        /// region spans the cell's low half on the normal axis, a one-cell-wide window clipped to the
        /// cell on the swept tangent axis, and the whole cell on the other tangent.
        /// </summary>
        /// <param name="block">The occluding block's job data.</param>
        /// <param name="meta">Its metadata byte (selects the volume's rotation).</param>
        /// <param name="sweepAxis">Tangent axis to sweep along (0 = X, 2 = Z).</param>
        /// <returns>Coverage at each sample point, in sweep order.</returns>
        private static float[] SweepCoverage(BlockTypeJobData block, byte meta, int sweepAxis)
        {
            float[] result = new float[SWEEP_SAMPLES];
            int otherAxis = sweepAxis == 0 ? 2 : 0;

            for (int i = 0; i < SWEEP_SAMPLES; i++)
            {
                float p = i / (float)(SWEEP_SAMPLES - 1);

                float3 regionMin = new float3(0f, 0f, 0f);
                float3 regionMax = new float3(1f, 0.5f, 1f);
                regionMin[sweepAxis] = math.saturate(p - 0.5f);
                regionMax[sweepAxis] = math.saturate(p + 0.5f);
                regionMin[otherAxis] = 0f;
                regionMax[otherAxis] = 1f;

                result[i] = LightAttenuation.AmbientOcclusionRegionCoverage(in block, meta, regionMin, regionMax);
            }

            return result;
        }

        /// <summary>Reads the min/max corner of every vertex belonging to one flattened mesh.</summary>
        /// <param name="meshes">Per-mesh face ranges.</param>
        /// <param name="faces">Per-face vert ranges.</param>
        /// <param name="verts">Flattened vertex positions.</param>
        /// <param name="meshIndex">Which mesh to measure.</param>
        /// <param name="min">The mesh's minimum corner.</param>
        /// <param name="max">The mesh's maximum corner.</param>
        private static void MeshVertexExtents(in NativeArray<CustomMeshData> meshes,
            in NativeArray<CustomFaceData> faces, in NativeArray<CustomVertData> verts, int meshIndex,
            out float3 min, out float3 max)
        {
            min = new float3(float.MaxValue);
            max = new float3(float.MinValue);

            CustomMeshData mesh = meshes[meshIndex];
            for (int f = 0; f < mesh.FaceCount; f++)
            {
                CustomFaceData face = faces[mesh.FaceStartIndex + f];
                for (int v = 0; v < face.VertCount; v++)
                {
                    float3 p = verts[face.VertStartIndex + v].Position;
                    min = math.min(min, p);
                    max = math.max(max, p);
                }
            }
        }

        /// <summary>True when every sample equals the first, within the fixture tolerance.</summary>
        /// <param name="values">The swept coverage values.</param>
        private static bool IsConstant(float[] values)
        {
            for (int i = 1; i < values.Length; i++)
            {
                if (Mathf.Abs(values[i] - values[0]) > SWEEP_EPSILON) return false;
            }

            return true;
        }

        /// <summary>True when the sweep never reverses direction beyond the fixture tolerance.</summary>
        /// <param name="values">The swept coverage values.</param>
        private static bool IsMonotonic(float[] values)
        {
            bool nonDecreasing = true;
            bool nonIncreasing = true;

            for (int i = 1; i < values.Length; i++)
            {
                if (values[i] < values[i - 1] - SWEEP_EPSILON) nonDecreasing = false;
                if (values[i] > values[i - 1] + SWEEP_EPSILON) nonIncreasing = false;
            }

            return nonDecreasing || nonIncreasing;
        }

        /// <summary>Formats a coverage sweep for a failure message.</summary>
        /// <param name="values">The swept coverage values.</param>
        private static string Format(float[] values)
        {
            StringBuilder sb = new StringBuilder("    sweep:");
            foreach (float v in values) sb.AppendFormat(" {0:F3}", v);
            return sb.ToString();
        }

        /// <summary>Number of points sampled across a cell by <see cref="SweepCoverage"/>.</summary>
        private const int SWEEP_SAMPLES = 9;

        /// <summary>
        /// Slack for the monotonicity and constancy tests. Well below the post's measured 0.038 swing,
        /// so a genuine reversal is never absorbed by it.
        /// </summary>
        private const float SWEEP_EPSILON = 1e-4f;

        /// <summary>Slack for comparing authored bounds against flattened vertex extents.</summary>
        private const float FIXTURE_BOUNDS_EPSILON = 1e-5f;
    }
}
