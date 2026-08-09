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

            scenarios.Add(new Scenario(
                "B57: the corner seal stays in its corner — full strength at the corner itself, falling off with distance from it (SS-2a)",
                B57_CornerSealLocality));

            scenarios.Add(new Scenario(
                "B58: a cell the seal treats as occluded does not also feed the light average — under a NON-uniform light field (SS-2a)",
                B58_SealedCornerIgnoresHiddenLight));

            scenarios.Add(new Scenario(
                "B54: a lone full cube's shadow follows Euclidean distance to its silhouette, not a product of two ramps (SS-3)",
                B54_FullCubeShadowFollowsDistance));

            scenarios.Add(new Scenario(
                "B59: a straight wall's shadow is uniform along the wall — no scalloping at the cell seams (SS-3a)",
                B59_StraightWallShadowIsUniform));

            scenarios.Add(new Scenario(
                "B60: a partial occluder that casts nothing does not subdivide the face it stands over (SS-3a)",
                B60_NonCastingPartialDoesNotSubdivide));
        }

        /// <summary>
        /// B60 — <b>the tessellation gate must read the shadow, not the neighborhood.</b> A top slab
        /// hanging above a floor casts nothing on that floor: its volume spans the upper half of its own
        /// cell and never reaches the floor's plane, which
        /// <c>BurstOcclusionUtility.GetPlaneSilhouette</c> already answers correctly. The floor face under
        /// it must therefore stay a single quad.
        /// <para>
        /// The gate asked a cheaper question — <i>is a partial block present in the 3×3</i> — and a block
        /// merely standing nearby is not a shadow. So this face was subdivided 4×4 and shaded at 25
        /// sample points to render a shadow that does not exist: 16 quads and 64 vertices where 1 and 4
        /// carry the same result, on every face any slab or stair hangs over.
        /// </para>
        /// <para>
        /// <b>Both legs are load-bearing, in opposite directions</b> (finding F15). The first leg alone is
        /// satisfied by deleting sub-cell shading outright; the second pins that the very same fixture
        /// still subdivides when the slab is turned the other way up and does reach the plane. Only
        /// together do they say "subdivide where there is a shadow, and only there".
        /// </para>
        /// </summary>
        /// <returns>True when a non-casting partial leaves the face undivided and a casting one splits it.</returns>
        private static bool B60_NonCastingPartialDoesNotSubdivide()
        {
            ushort lit = LightBitMapping.PackLightData(15, 0, 0, 0);

            int topSlabQuads = SlabOverFloorQuads(lit, TOP_SLAB_META, out int darkest);
            int bottomSlabQuads = SlabOverFloorQuads(lit, BOTTOM_SLAB_META, out _);

            bool ok = MeshAssert.IsTrue("B60 a partial occluder that reaches nothing leaves the face undivided",
                topSlabQuads == 1,
                $"The floor face under a top half slab emitted {topSlabQuads} quad(s). That slab's volume "
                + "occupies the upper half of its own cell, so it never reaches this face's plane and the "
                + "silhouette model correctly reports it casts nothing — there is no shadow here to "
                + "resolve.\n"
                + "Subdividing on the mere presence of a partial block in the neighborhood, rather than on "
                + "one having actually cast, makes every face a slab or stair hangs over pay 16 quads and "
                + "64 vertices to reproduce the value a single quad already carried.");

            // F15: "one quad" is also what a face with sub-cell shading removed emits, and "undarkened" is
            // what a face with no light model at all reads. The same fixture with the slab the other way
            // up must still subdivide — that is what makes the leg above a statement about casting.
            ok &= MeshAssert.IsTrue("B60 the same fixture still subdivides when the slab does reach the face",
                bottomSlabQuads > 1,
                $"Turned the other way up — occupying the lower half of its cell, and so standing on this "
                + $"very face — the slab produced {bottomSlabQuads} quad(s). A partial occluder in contact "
                + "with a face must still get the finer grid; its edge sits at the cell midline, which is "
                + "the resolution problem sub-cell shading exists to solve (B49).");

            ok &= MeshAssert.IsTrue("B60 the non-casting slab leaves the face unshadowed",
                darkest == 255,
                $"The floor face under the top slab reads {darkest} at its darkest under a uniformly lit "
                + "sky. A volume that does not reach this plane must not darken it at all; if it does, the "
                + "undivided face above is hiding a real shadow at one sample per corner rather than "
                + "correctly carrying none.");

            return ok;
        }

        /// <summary>
        /// Places one half slab in the cell directly above the probe floor cell and reports how finely the
        /// floor's top face was subdivided.
        /// </summary>
        /// <param name="lit">Packed light value to fill the world with.</param>
        /// <param name="slabMeta">Orientation of the slab, selecting which half of its cell it fills.</param>
        /// <param name="darkest">The darkest sky value emitted on the probe face.</param>
        /// <returns>The quad count on the probe cell's top face.</returns>
        private static int SlabOverFloorQuads(ushort lit, byte slabMeta, out int darkest)
        {
            using MeshingTestWorld world = new MeshingTestWorld();
            BuildFloor(world);
            world.SetBlock(B49_X, B49_Y + 1, B49_Z, TestMeshBlockPalette.HalfSlab, slabMeta);
            world.FillLight(lit);

            // Read inside the using scope: the output's buffers are pooled by the world.
            MeshDataJobOutput output = world.Run(SmoothLightingQuality.High);

            darkest = 255;
            foreach (SubVertexSample s in TopFaceSubVertexField(output, B49_X, B49_Y, B49_Z))
                darkest = Mathf.Min(darkest, s.Sun);

            return CountTopFaceQuads(output, B49_X, B49_Y, B49_Z);
        }

        /// <summary>
        /// <see cref="MetadataSchema.Facing6Roll2"/> orientation placing the half slab in the <b>upper</b>
        /// half of its cell (rotated bounds <c>y ∈ [0.5, 1]</c>) — the occluder that reaches no floor.
        /// </summary>
        private const byte TOP_SLAB_META = 0x10;

        /// <summary>
        /// Orientation leaving the slab in the <b>lower</b> half of its cell (the authored
        /// <c>BottomHalfSlab</c> bounds, unrotated) — the contrast case, which does reach the floor.
        /// </summary>
        private const byte BOTTOM_SLAB_META = 0x00;

        /// <summary>
        /// B59 — <b>a straight wall must cast a straight shadow.</b> Walk along the base of a flat wall
        /// and the shading must not change: the wall's silhouette is the same union of geometry at every
        /// point along it, so nothing about the shading may depend on where the <i>cell boundaries</i>
        /// happen to fall.
        /// <para>
        /// This is the defect SS-3 made visible (SS-3a). Occlusion was summed <b>per cell</b>, and a
        /// straight wall arrives as three separate unit squares in the hoisted 3×3. At a cell seam two of
        /// them touch the sample point (<c>0.25 + 0.25</c>); mid-cell only one touches and the others sit
        /// half a cell away (<c>0.25 + 2×0.0625</c>). Same wall, different sum — measured 128 at the seams
        /// against 159 mid-cell, which reads as a dark dash at every seam.
        /// </para>
        /// <para>
        /// <b>The seams were the correct value, not the artifact.</b> Before sub-cell shading this edge
        /// had only its two corner samples, both 128, and the GPU interpolated a uniform band between
        /// them; the interior samples SS-3 added are what disagreed with the corners. So this scenario
        /// pins uniformity, and the pre-existing corner values are what it must be uniform <i>at</i>.
        /// </para>
        /// </summary>
        /// <returns>True when the wall's shadow is constant along the wall at every depth.</returns>
        private static bool B59_StraightWallShadowIsUniform()
        {
            ushort lit = LightBitMapping.PackLightData(15, 0, 0, 0);

            using MeshingTestWorld world = new MeshingTestWorld();
            for (int dx = -3; dx <= 3; dx++)
            for (int dz = -3; dz <= 0; dz++)
                world.SetBlock(B49_X + dx, B49_Y, B49_Z + dz, TestMeshBlockPalette.SolidOpaque, 0);

            // A straight run of full cubes along +X, one cell beyond the probe cell.
            for (int dx = -3; dx <= 3; dx++)
            for (int dy = 1; dy <= 2; dy++)
                world.SetBlock(B49_X + dx, B49_Y + dy, B49_Z + 1, TestMeshBlockPalette.SolidOpaque, 0);

            world.FillLight(lit);
            MeshDataJobOutput output = world.Run(SmoothLightingQuality.High, fullCubeContactShadows: true);

            List<SubVertexSample> field = TopFaceSubVertexField(output, B49_X, B49_Y, B49_Z);
            if (field.Count == 0)
            {
                Debug.LogError("[FAIL] B59 setup: the probe face emitted no vertices.");
                return false;
            }

            bool ok = true;
            StringBuilder failures = new StringBuilder();
            int rowsChecked = 0;

            // v is depth away from the wall (v = 1 is against it). Every row must be flat in u.
            foreach (float v in new[] { 1f, 0.5f })
            {
                int darkest = 255, lightest = 0, samples = 0;
                foreach (SubVertexSample s in field)
                {
                    if (Mathf.Abs(s.V - v) >= FACE_POSITION_EPSILON) continue;
                    darkest = Mathf.Min(darkest, s.Sun);
                    lightest = Mathf.Max(lightest, s.Sun);
                    samples++;
                }

                if (samples < 3)
                {
                    failures.AppendFormat("    depth v={0}: only {1} sample(s) on this row\n", v, samples);
                    continue;
                }

                rowsChecked++;
                if (lightest - darkest > WALL_UNIFORMITY_TOLERANCE)
                {
                    failures.AppendFormat(
                        "    depth v={0}: spans {1} light units along the wall ({2}..{3}) across {4} samples\n",
                        v, lightest - darkest, darkest, lightest, samples);
                }
            }

            ok &= MeshAssert.IsTrue("B59 a straight wall's shadow does not scallop along the wall",
                failures.Length == 0 && rowsChecked == 2,
                "Shading walked along the base of a flat wall must not change. Where it does, the model "
                + "is reading the wall's decomposition into cells rather than its shape: a sample at a "
                + "cell seam sees two occluders touching it, one mid-cell sees one touching and two half "
                + "a cell away, and the sum differs even though the geometry does not.\n" + failures);

            // Positive control (F15): a row of 255s is trivially uniform. The wall must actually be
            // casting, and the shadow must actually end — otherwise "uniform" means "absent".
            int againstWall = 255, oneCellOut = 0;
            foreach (SubVertexSample s in field)
            {
                if (Mathf.Abs(s.V - 1f) < FACE_POSITION_EPSILON) againstWall = Mathf.Min(againstWall, s.Sun);
                if (Mathf.Abs(s.V) < FACE_POSITION_EPSILON) oneCellOut = Mathf.Max(oneCellOut, s.Sun);
            }

            ok &= MeshAssert.IsTrue("B59 the wall casts, and its shadow ends within a cell",
                againstWall <= 200 && oneCellOut == 255,
                $"Against the wall the face reads {againstWall} (must be materially darkened) and one "
                + $"cell away it reads {oneCellOut} (must be fully lit). Without both, the uniformity "
                + "check above is satisfied by a face carrying no shadow at all.");

            return ok;
        }

        /// <summary>
        /// How far shading may vary walking along a straight wall, in encoded light units — UNorm8
        /// rounding only. The defect this guards spans 31 units at the wall base.
        /// </summary>
        private const int WALL_UNIFORMITY_TOLERANCE = 3;

        /// <summary>
        /// B54 — <b>the metric assertions, and the first ones in this suite</b>. Every other AO scenario
        /// checks <i>values</i>; these check the <b>shape</b> of the field around an isolated block:
        /// its shadow must reach equally far in every direction and never deepen with distance. That is
        /// design finding S2 — the pre-SS model weighted an occluder by a product of two per-axis ramps,
        /// whose isocontours are hyperbolic, so its shadow stretched about twice as far diagonally as
        /// straight out and read as a round blob rather than a block's shadow.
        /// <para>
        /// <b>Rewritten by SS-3a, and the reason matters.</b> This scenario first asserted something
        /// stronger — that shading is a function of distance <i>alone</i>, against the closed form
        /// <c>occ = 0.25·(1 − d)²</c> — and the quadrant model falsified it. Beside a block's face the
        /// block fills two of the point's four quadrants; beside its corner, one. Equal distance,
        /// unequal occlusion, and correctly so: a block touching you along a whole edge blocks more sky
        /// than one touching you at a corner. The old assertion encoded circular isocontours, which was
        /// never the claim S2 makes. So the <i>assertion</i> moved to the property S2 is actually about
        /// — <b>reach</b> and <b>ordering</b>, both functions of the metric alone — rather than having
        /// its tolerance widened to accommodate the new values. Values are pinned by B56/B57/B58/B59.
        /// </para>
        /// <para>
        /// <b>Leg 1 is red on the pre-SS-3 engine by construction</b> — an undivided face emits only its
        /// four corners, where <c>d</c> is 0 or 1 and every model agrees, so there is nowhere to read a
        /// metric at all. That is the phase's prove-red.
        /// </para>
        /// </summary>
        /// <returns>True when the faces around a lone cube are subdivided and match the distance oracle.</returns>
        private static bool B54_FullCubeShadowFollowsDistance()
        {
            ushort lit = LightBitMapping.PackLightData(15, 0, 0, 0);

            using MeshingTestWorld world = new MeshingTestWorld();
            for (int dx = -3; dx <= 3; dx++)
            for (int dz = -3; dz <= 3; dz++)
                world.SetBlock(B49_X + dx, B49_Y, B49_Z + dz, TestMeshBlockPalette.SolidOpaque, 0);

            // One ordinary full cube standing on the floor — no custom bounds anywhere in the fixture,
            // so nothing here trips SS-2's gate and only SS-3's widening can subdivide these faces.
            world.SetBlock(B49_X, B49_Y + 1, B49_Z, TestMeshBlockPalette.SolidOpaque, 0);
            world.FillLight(lit);

            MeshDataJobOutput output = world.Run(SmoothLightingQuality.High, fullCubeContactShadows: true);

            int quads = CountTopFaceQuads(output, B49_X + 1, B49_Y, B49_Z);
            bool ok = MeshAssert.IsTrue("B54 a face a full cube reaches is subdivided",
                quads > 1,
                $"The floor face beside a full cube emitted {quads} quad(s), so its shading still lives "
                + "only at the cell's corners. A shadow whose whole point is to hug the block cannot "
                + "resolve at one sample per cell — it becomes a ramp across the entire neighboring "
                + "block, which is the artifact this phase exists to fix.");

            if (!ok) return false;

            StringBuilder failures = new StringBuilder();
            int litBeyondReach = 0;
            bool darkenedPerpendicular = false;
            bool darkenedDiagonal = false;

            // Sweep the eight floor cells around the cube, so perpendicular and diagonal directions are
            // measured together. Each sample is kept with its distance so the ordering leg below can
            // compare within a direction.
            List<(int dx, int dz, float distance, int sun)> samples = new List<(int, int, float, int)>();

            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dz == 0) continue;

                foreach (SubVertexSample s in TopFaceSubVertexField(output, B49_X + dx, B49_Y, B49_Z + dz))
                {
                    // The cube's silhouette on this floor plane is its own cell's footprint, expressed
                    // in the probed face's parameter frame.
                    float u = s.U + dx, v = s.V + dz;
                    float outsideU = Mathf.Max(Mathf.Max(-u, u - 1f), 0f);
                    float outsideV = Mathf.Max(Mathf.Max(-v, v - 1f), 0f);
                    float distance = Mathf.Sqrt(outsideU * outsideU + outsideV * outsideV);

                    samples.Add((dx, dz, distance, s.Sun));

                    bool darkened = s.Sun < 255;
                    if (distance >= 1f - 0.001f)
                    {
                        if (darkened)
                        {
                            litBeyondReach++;
                            if (failures.Length < 500)
                            {
                                failures.AppendFormat(
                                    "    cell({0},{1}) uv({2:0.00},{3:0.00}) is {4:0.00} cells away and still reads {5}\n",
                                    dx, dz, s.U, s.V, distance, s.Sun);
                            }
                        }
                    }
                    else if (darkened)
                    {
                        bool offAxis = outsideU > 0.001f && outsideV > 0.001f;
                        if (offAxis) darkenedDiagonal = true;
                        else darkenedPerpendicular = true;
                    }
                }
            }

            // Leg 2 — THE anti-blob assertion, and deliberately about the shadow's REACH rather than its
            // values. Finding S2 is that the pre-SS model's isocontours were hyperbolic, so its shadow
            // stretched roughly twice as far diagonally as straight out. Reach is a property of the
            // metric alone: it survives any retuning of the falloff, the share model or the radius.
            ok &= MeshAssert.IsTrue("B54 the shadow reaches equally far in every direction",
                litBeyondReach == 0,
                $"{litBeyondReach} sample(s) further than the shadow's radius from the cube are still "
                + "darkened. The shadow's support must be the block's shape grown uniformly — a model "
                + "that weights an occluder by a product of per-axis ramps bulges diagonally and reads "
                + "as a round blob rather than a block's shadow (finding S2).\n" + failures);

            // F15: the reach leg alone is satisfied by a face carrying no shadow at all, and a shadow
            // present only on the axes would pass a perpendicular-only sweep.
            ok &= MeshAssert.IsTrue("B54 the cube darkens both perpendicular and diagonal neighbors",
                darkenedPerpendicular && darkenedDiagonal,
                $"Within the shadow's radius, darkening was found perpendicular={darkenedPerpendicular}, "
                + "diagonal=" + darkenedDiagonal + ". Both are required: the diagonal samples are the "
                + "only ones that can tell a Euclidean metric from a separable one, and without any "
                + "darkening at all the reach leg above is vacuous.");

            // Leg 3 — within one direction, moving away from the block may never get darker. Also
            // metric-only, and it is what catches a non-monotonic field like the coverage model's
            // (finding S9), which rose where distance said fall.
            StringBuilder ordering = new StringBuilder();
            foreach ((int dx, int dz, float distance, int sun) a in samples)
            foreach ((int dx, int dz, float distance, int sun) b in samples)
            {
                if (a.dx != b.dx || a.dz != b.dz) continue;
                if (a.distance >= b.distance - 0.001f) continue;
                if (a.sun <= b.sun + SHADOW_ORDERING_TOLERANCE) continue;

                if (ordering.Length < 400)
                {
                    ordering.AppendFormat(
                        "    cell({0},{1}): {2:0.00} cells away reads {3}, but {4:0.00} away reads {5}\n",
                        a.dx, a.dz, a.distance, a.sun, b.distance, b.sun);
                }
            }

            ok &= MeshAssert.IsTrue("B54 the shadow never deepens with distance",
                ordering.Length == 0,
                "Within one direction from the block, a point further from it must not be darker than a "
                + "point closer to it.\n" + ordering);

            return ok;
        }

        /// <summary>
        /// Slack when ordering two samples by distance, in encoded light units — UNorm8 rounding plus
        /// the float error of recomputing the distance test-side.
        /// </summary>
        private const int SHADOW_ORDERING_TOLERANCE = 2;

        /// <summary>
        /// B58 — <b>the first meshing scenario to shade under a non-uniform light field, and it exists
        /// because that gap shipped a defect.</b> A sealed corner must not read the light of the cell the
        /// seal declares hidden: if the two walls meeting at a corner hide the diagonal quadrant, they
        /// hide whatever light is in it.
        /// <para>
        /// The engine got this wrong for a subtle reason worth stating. `SS-2` expressed shading as a
        /// light mean times <c>(1 − occlusion)</c>, taking the mean over cells that <i>hold</i> light —
        /// a property of the block. That matches the occluded set exactly while occluders are opaque
        /// (an opaque cell both occludes and holds no usable light), so it looks correct and every
        /// baseline agreed. A <b>sealed diagonal is air</b>: it holds light, so it fed the mean at full
        /// weight while the seal simultaneously counted it as fully occluding — its light credited and
        /// debited at once. At a concave corner the hidden cell is the darkest one around, so real
        /// corners rendered up to <b>twice as dark as they should</b>.
        /// </para>
        /// <para>
        /// <b>Uniform light hides this completely</b>, which is why 436 baselines could not: when every
        /// cell carries the same value, the mean is that value no matter how it is weighted. Every AO
        /// scenario in this suite fills light uniformly (harness gap <c>MH-3</c>), so this is the one
        /// place the two models can be told apart at all.
        /// </para>
        /// </summary>
        /// <returns>True when the sealed corner ignores the hidden cell's light and an open one does not.</returns>
        private static bool B58_SealedCornerIgnoresHiddenLight()
        {
            int sealedBright = SealedCornerSun(nookSky: 15, sealCorner: true);
            int sealedDark = SealedCornerSun(nookSky: 0, sealCorner: true);
            int openBright = SealedCornerSun(nookSky: 15, sealCorner: false);
            int openDark = SealedCornerSun(nookSky: 0, sealCorner: false);

            if (sealedBright < 0 || sealedDark < 0 || openBright < 0 || openDark < 0)
            {
                Debug.LogError("[FAIL] B58 setup: the probe face's corner vertex was not emitted.");
                return false;
            }

            bool ok = MeshAssert.IsTrue("B58 a sealed corner ignores the light of the cell it hides",
                sealedBright == sealedDark,
                $"Darkening the diagonal cell moved the sealed corner from {sealedBright} to "
                + $"{sealedDark}. That cell is hidden behind the two walls meeting at this corner — the "
                + "seal says so, and the occlusion term already charges for it in full. Averaging its "
                + "light in as well counts it twice, and because the hidden cell is the darkest one "
                + "around a real concave corner, corners render far darker than the model claims.\n"
                + "The light mean must be taken over the same visibility weights the occlusion term "
                + "uses, not over a per-block 'holds light' flag — the two agree only while every "
                + "occluder is opaque.");

            // F15: without the walls the diagonal is a legitimate light source, so the corner MUST track
            // it. A model that simply dropped the diagonal would satisfy the leg above and fail here.
            ok &= MeshAssert.IsTrue("B58 an open corner still reads the light around it",
                openBright != openDark,
                $"With no walls raised, the corner reads {openBright} whether the diagonal cell carries "
                + $"full sky or none at all. Smooth lighting must average the light of the cells meeting "
                + "at a corner; a corner that ignores a visible neighbor is not lit, it is flat.");

            return ok;
        }

        /// <summary>
        /// Builds a concave corner from two full cubes — leaving the diagonal cell air — and reads the
        /// probe face's corner vertex under a light field that is uniform except in that diagonal cell.
        /// </summary>
        /// <param name="nookSky">Sky light to write into the diagonal ("nook") cell.</param>
        /// <param name="sealCorner">Whether to raise the two walls that seal the corner.</param>
        /// <returns>The corner vertex's encoded sky light, or -1 when it was not emitted.</returns>
        private static int SealedCornerSun(byte nookSky, bool sealCorner)
        {
            using MeshingTestWorld world = new MeshingTestWorld();
            BuildFloor(world);

            if (sealCorner)
            {
                world.SetBlock(B49_X + 1, B49_Y + 1, B49_Z, TestMeshBlockPalette.SolidOpaque, 0);
                world.SetBlock(B49_X, B49_Y + 1, B49_Z + 1, TestMeshBlockPalette.SolidOpaque, 0);
            }

            world.FillLight(LightBitMapping.PackLightData(15, 0, 0, 0));
            world.SetLight(B49_X + 1, B49_Y + 1, B49_Z + 1, LightBitMapping.PackLightData(nookSky, 0, 0, 0));

            // Read inside the using scope: the output's buffers are pooled by the world.
            return TryReadSubVertex(TopFaceSubVertexField(world.Run(SmoothLightingQuality.High),
                B49_X, B49_Y, B49_Z), 1f, 1f, out int sun)
                ? sun
                : -1;
        }

        /// <summary>
        /// B57 — <b>the corner seal must be local to the corner.</b> Two walls meeting at a right angle
        /// darken the point where they meet more than either does alone: the diagonal quadrant is hidden
        /// behind them, which is the rule <see cref="B56_CornerReduction"/> pins at <c>64</c>. This
        /// scenario asks the question B56 cannot — <i>how far out does that extra darkening reach</i> —
        /// because the whole suite reads face corners and face interiors, and the defect this guards
        /// (SS-2a) lived in the field between them: a dark wedge running diagonally out of every concave
        /// corner across open floor, visible in game while all 435 baselines were green.
        /// <para>
        /// Measured as a <b>four-configuration differential</b> — the same face with both walls, either
        /// wall alone, and neither — so what it reads is exactly the seal: the falloff profile, the
        /// radius, the gate-tripping slab and the light field all appear in every configuration and
        /// cancel. The excess is what the second wall adds <i>beyond</i> the two walls acting
        /// independently, and nothing but the seal produces it.
        /// </para>
        /// <para>
        /// <b>Both legs are load-bearing in opposite directions</b> (finding F15). The locality leg alone
        /// is satisfied by deleting the seal outright, which would lighten every inside corner in the
        /// world from 64 to 127; the corner leg alone is satisfied by the defect. Only together do they
        /// say "keep the corner value, stop it spreading".
        /// </para>
        /// </summary>
        /// <returns>True when the seal is present at the corner and decays away from it.</returns>
        private static bool B57_CornerSealLocality()
        {
            ushort lit = LightBitMapping.PackLightData(15, 0, 0, 0);

            List<SubVertexSample> both = InnerCornerField(lit, wallA: true, wallB: true);
            List<SubVertexSample> onlyA = InnerCornerField(lit, wallA: true, wallB: false);
            List<SubVertexSample> onlyB = InnerCornerField(lit, wallA: false, wallB: true);
            List<SubVertexSample> neither = InnerCornerField(lit, wallA: false, wallB: false);

            // The corner the two walls form is (u, v) = (1, 1); the probes walk away from it.
            if (!TrySealExcess(both, onlyA, onlyB, neither, 1f, 1f, out int atCorner)
                || !TrySealExcess(both, onlyA, onlyB, neither, 1f, 0.5f, out int againstWall)
                || !TrySealExcess(both, onlyA, onlyB, neither, 0.5f, 0.5f, out int outAlongDiagonal))
            {
                Debug.LogError("[FAIL] B57 setup: the probe face did not emit all three sample points. "
                               + "The face must be subdivided in every configuration for the differential "
                               + "to be readable.");
                return false;
            }

            bool ok = MeshAssert.IsTrue("B57 the corner seal is at full strength in the corner itself",
                atCorner >= MIN_CORNER_SEAL,
                $"Where the two walls meet, the second wall adds only {atCorner} light units beyond what "
                + "the walls contribute independently — the seal is missing or weakened there.\n"
                + "A point tucked into a concave corner cannot see the diagonal quadrant at all, whatever "
                + "that cell contains, because the two walls stand between them. Removing the seal to "
                + "cure a spreading artifact lightens every inside corner in the world from 64 to 127; "
                + "B56 pins that corner value and this leg pins the mechanism behind it.");

            ok &= MeshAssert.IsTrue("B57 the corner seal falls off with distance from the corner",
                againstWall - outAlongDiagonal >= MIN_SEAL_FALLOFF,
                $"Half a cell out along the diagonal the seal still adds {outAlongDiagonal} light units, "
                + $"against {againstWall} for a point pressed into the corner along one wall — a fall-off "
                + $"of {againstWall - outAlongDiagonal}, below the {MIN_SEAL_FALLOFF} this guards.\n"
                + "Occlusion attributed to the diagonal cell is only justified while the two walls hide "
                + "it; as the shaded point leaves the corner that cell comes into view, and a seal that "
                + "holds its strength out there darkens open floor a wall's width away from any geometry "
                + "— the diagonal wedge SS-2 shipped and SS-2a corrects.");

            return ok;
        }

        /// <summary>
        /// Reads the extra darkening two perpendicular walls produce at one point beyond their independent
        /// contributions — <c>(A only) + (B only) − (both) − (neither)</c>, in encoded light units.
        /// </summary>
        /// <param name="both">Sub-vertex field with both walls raised.</param>
        /// <param name="onlyA">Field with only the wall on the first tangent axis.</param>
        /// <param name="onlyB">Field with only the wall on the second tangent axis.</param>
        /// <param name="neither">Field with no walls.</param>
        /// <param name="u">Face-parameter coordinate of the probe point.</param>
        /// <param name="v">Second face-parameter coordinate.</param>
        /// <param name="excess">The extra darkening, in light units.</param>
        /// <returns>True when all four configurations emitted a vertex at that point.</returns>
        private static bool TrySealExcess(List<SubVertexSample> both, List<SubVertexSample> onlyA,
            List<SubVertexSample> onlyB, List<SubVertexSample> neither, float u, float v, out int excess)
        {
            excess = 0;

            if (!TryReadSubVertex(both, u, v, out int sunBoth)
                || !TryReadSubVertex(onlyA, u, v, out int sunA)
                || !TryReadSubVertex(onlyB, u, v, out int sunB)
                || !TryReadSubVertex(neither, u, v, out int sunNeither))
            {
                return false;
            }

            excess = sunA + sunB - sunBoth - sunNeither;
            return true;
        }

        /// <summary>Reads one sub-vertex's sunlight by its position within the face.</summary>
        /// <param name="field">The face's sub-vertex field.</param>
        /// <param name="u">Face-parameter coordinate to match.</param>
        /// <param name="v">Second face-parameter coordinate to match.</param>
        /// <param name="sun">The vertex's encoded sky light.</param>
        /// <returns>True when the face emitted a vertex there.</returns>
        private static bool TryReadSubVertex(List<SubVertexSample> field, float u, float v, out int sun)
        {
            foreach (SubVertexSample s in field)
            {
                if (Mathf.Abs(s.U - u) >= FACE_POSITION_EPSILON) continue;
                if (Mathf.Abs(s.V - v) >= FACE_POSITION_EPSILON) continue;

                sun = s.Sun;
                return true;
            }

            sun = -1;
            return false;
        }

        /// <summary>
        /// Builds the concave-corner fixture and returns the probe face's whole sub-vertex field: a floor,
        /// a gate-tripping slab, and either, both or neither of the two walls that form the corner.
        /// <para>
        /// The slab stands on the diagonal <i>opposite</i> the corner and is present in every
        /// configuration — it keeps the face subdivided (so all four fields are sampled at the same
        /// points) and cancels out of the differential.
        /// </para>
        /// </summary>
        /// <param name="lit">Packed light value to fill the world with.</param>
        /// <param name="wallA">Whether to raise the wall on the <c>+u</c> side.</param>
        /// <param name="wallB">Whether to raise the wall on the <c>+v</c> side.</param>
        /// <returns>Every vertex emitted on the probe cell's top face.</returns>
        private static List<SubVertexSample> InnerCornerField(ushort lit, bool wallA, bool wallB)
        {
            using MeshingTestWorld world = new MeshingTestWorld();
            BuildFloor(world);
            world.SetBlock(B49_X - 1, B49_Y + 1, B49_Z - 1, TestMeshBlockPalette.HalfSlab, 0x03);

            if (wallA) world.SetBlock(B49_X + 1, B49_Y + 1, B49_Z, TestMeshBlockPalette.SolidOpaque, 0);
            if (wallB) world.SetBlock(B49_X, B49_Y + 1, B49_Z + 1, TestMeshBlockPalette.SolidOpaque, 0);

            world.FillLight(lit);

            // Read inside the using scope: the output's buffers are pooled by the world. The samples are
            // copied values, so the returned list outlives it.
            return TopFaceSubVertexField(world.Run(SmoothLightingQuality.High), B49_X, B49_Y, B49_Z);
        }

        /// <summary>
        /// Extra darkening the seal must still produce where the two walls actually meet. The model gives
        /// a quarter of the range there (the 127 → 64 step B56 pins); a deleted seal gives zero.
        /// </summary>
        private const int MIN_CORNER_SEAL = 48;

        /// <summary>
        /// How far the seal must decay between a point pressed into the corner and one half a cell out
        /// along the diagonal. The defect holds it flat (a fall-off of 0); the fix roughly quarters it.
        /// </summary>
        private const int MIN_SEAL_FALLOFF = 6;

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
                + "behavior-preserving generalization it is documented to be, and ordinary terrain has "
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

        /// <summary>Fills a 5×5 platform of full cubes centered on the probe cell.</summary>
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
