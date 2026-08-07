using System.Collections.Generic;
using System.Text;
using Data;
using Editor.Validation.Lighting.Framework;
using Jobs.BurstData;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Directional occlusion baselines for <b>partial blocks</b> (half slabs) — the lighting half of
    /// <c>Documentation/Bugs/LIGHTING_BUGS.md</c> Bug 20, implemented by <c>VO-3</c> of
    /// <c>Documentation/Design/VOXEL_OCCLUSION_REFACTOR.md</c>.
    /// <para>
    /// All four are baselines today. <b>B104</b> was authored by VO-2 as known-bug repro <c>K20a</c>
    /// (expected-red, so a not-yet-implemented behaviour could not mark the suite red) and promoted here
    /// on 2026-08-07 once VO-3 landed and it was confirmed in game. The other three passed from the start
    /// and are the tripwires against "fix Bug 20 by making slabs transparent" — <b>B101</b> is the one that
    /// catches it, verified red under a deliberate sabotage.
    /// </para>
    /// <para>
    /// <b>Why these assert behaviour, not the oracle (F7).</b> <see cref="LightingOracle"/> calls the
    /// same <see cref="LightAttenuation"/> the engine does, so an oracle comparison cannot arbitrate
    /// whether the directional model is right — both sides would move together. Every assertion here is
    /// therefore a direct probe of "did light reach this voxel", derived from the geometry rather than
    /// from a re-statement of the cost formula.
    /// </para>
    /// </summary>
    public static partial class LightingValidationSuite
    {
        /// <summary>Facing6Roll2 metadata for the unrotated slab (facing South, roll 0 — the identity matrix).</summary>
        private const byte SLAB_FLOOR_META = 0x00;

        /// <summary>Facing6Roll2 metadata for a slab stood upright against the +Z half of its cell (facing Bottom, roll 0).</summary>
        private const byte SLAB_VERTICAL_META = 0x03;

        /// <summary>Inclusive top Y of the superflat floor these scenarios build on.</summary>
        private const int FLOOR_TOP_Y = 10;

        /// <summary>
        /// Registers the partial-block directional-occlusion baselines (called from the suite runner).
        /// </summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddPartialBlockOcclusionScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("B101: an UNROTATED half slab capping a shaft blocks daylight through its solid underside", B101_FloorSlabBlocksDaylight));
            scenarios.Add(new Scenario("B102: control — a full opaque cube capping a shaft blocks daylight", B102_FullCubeBlocksDaylight));
            scenarios.Add(new Scenario("B103: control — an uncapped shaft is fully lit", B103_OpenShaftIsLit));
            scenarios.Add(new Scenario("B104: a VERTICAL half slab's open half carries the sky column undimmed", B104_VerticalSlabPassesDaylight));
        }

        /// <summary>
        /// B104 — <b>the motivating case</b>, promoted from repro <c>K20a</c> after in-game confirmation
        /// (2026-08-07: "it's now indeed 15 all the way down"). A shaft in a superflat floor, capped by a half slab rotated
        /// upright (<see cref="SLAB_VERTICAL_META"/>). That orientation puts the slab's solid half against
        /// the cell's +Z side, leaving its ±Y faces only half covered — so the open half is a full-height
        /// vertical channel and the sky column must pass through it <b>undimmed</b>.
        /// <para>
        /// Asserted as a <b>differential against an uncapped shaft</b>, column for column: it pins the
        /// degree of the effect without restating the cost formula. An earlier revision asserted only
        /// "sky &gt; 0" below the slab; that was too weak — it passed while the column actually decayed
        /// 15/14/13/… per block, a defect only found in game. Do not weaken it back.
        /// </para>
        /// </summary>
        /// <returns>True when the column below a vertical slab matches an uncapped shaft exactly.</returns>
        private static bool B104_VerticalSlabPassesDaylight()
        {
            byte[] open = ShaftColumn(TestBlockPalette.Air, meta: 0);
            byte[] slabbed = ShaftColumn(TestBlockPalette.HalfSlab, SLAB_VERTICAL_META);

            StringBuilder diff = new StringBuilder();
            for (int i = 0; i < open.Length; i++)
            {
                if (open[i] != slabbed[i])
                    diff.Append($" y={FLOOR_TOP_Y - 1 - i}: open={open[i]} slabbed={slabbed[i]};");
            }

            return LightingAssert.IsTrue(diff.Length == 0,
                "B104: a vertical half slab's open half carries the sky column undimmed",
                $"the column under a vertical slab must equal an uncapped shaft's, but differs at —{diff}. "
                + "A decaying column (15/14/13/…) means the sky-column rule is still whole-block: the open "
                + "half of a vertical slab is an unobstructed vertical channel and must not attenuate.");
        }

        /// <summary>
        /// B101 — the other half of the same model: unrotated, the slab rests on the cell floor, so its −Y
        /// face is fully covered and DOES occlude. A slab floor must keep darkening the space beneath it —
        /// this is the tripwire against "fix Bug 20 by making slabs transparent", which would trade one
        /// visible wrongness for another.
        /// </summary>
        /// <returns>True when no light reaches below the floor slab.</returns>
        private static bool B101_FloorSlabBlocksDaylight()
        {
            byte below = ShaftLightBelowCap(TestBlockPalette.HalfSlab, SLAB_FLOOR_META, out _);
            return LightingAssert.IsTrue(below == 0,
                "B101: an unrotated half slab still blocks daylight below it",
                $"expected sky 0 below the floor slab, got {below}. A slab whose solid half faces down "
                + "must fully occlude — if this reds after VO-3, the directional model made slabs "
                + "transparent instead of directional.");
        }

        /// <summary>
        /// B102 — full-block control. A plain opaque cube caps the shaft; it must block daylight both
        /// before and after VO-3. This is the "no behaviour change for full cubes" guard in its smallest
        /// form: full cubes have coverage 1 on every face, so the directional model must reduce to today's.
        /// </summary>
        /// <returns>True when no light reaches below the cube.</returns>
        private static bool B102_FullCubeBlocksDaylight()
        {
            byte below = ShaftLightBelowCap(TestBlockPalette.Stone, meta: 0, out _);
            return LightingAssert.IsTrue(below == 0,
                "B102 control: a full opaque cube blocks daylight",
                $"expected sky 0 below the cube, got {below}");
        }

        /// <summary>
        /// B103 — positive control for the whole fixture. With the shaft left open, the probe voxel must be
        /// brightly lit. Without this, B101/B102 would pass vacuously if the harness simply never lit
        /// anything (a mis-built floor, a missing heightmap recalc, an un-run lighting pass).
        /// </summary>
        /// <returns>True when the open shaft's probe voxel is lit.</returns>
        private static bool B103_OpenShaftIsLit()
        {
            byte below = ShaftLightBelowCap(TestBlockPalette.Air, meta: 0, out _);
            return LightingAssert.IsTrue(below > 0,
                "B103 control: an uncapped shaft is lit",
                $"expected sky > 0 at the bottom of an open shaft, got {below} — the fixture itself is "
                + "broken, so B101, B102 and B104 prove nothing.");
        }

        /// <summary>
        /// Depth of the shaft below the cap. Deep enough that a per-block decay is visible as a gradient
        /// rather than a single value — the shape of defect B104's original "sky &gt; 0" assertion missed.
        /// </summary>
        private const int SHAFT_DEPTH = 6;

        /// <summary>
        /// Returns the whole sky-light column beneath the cap, topmost voxel first, for the
        /// uncapped-versus-capped differential B104 asserts.
        /// </summary>
        /// <param name="capBlock">Block placed at the top of the shaft (use Air to leave it open).</param>
        /// <param name="meta">Raw metadata byte for the cap block — selects a partial block's orientation.</param>
        /// <returns>Sky light per voxel, from just under the cap downwards.</returns>
        private static byte[] ShaftColumn(ushort capBlock, byte meta)
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            BuildShaft(world, capBlock, meta);

            ChunkData data = world.GetChunkData(new Vector2Int(1, 1));
            byte[] column = new byte[SHAFT_DEPTH];
            for (int i = 0; i < SHAFT_DEPTH; i++)
                column[i] = LightBitMapping.GetSkyLight(data.GetLightData(8, FLOOR_TOP_Y - 1 - i, 8));
            return column;
        }

        /// <summary>
        /// Carves the shaft, places the cap, and lights the world to convergence — the geometry every
        /// scenario in this file shares.
        /// </summary>
        /// <param name="world">The harness world to build into.</param>
        /// <param name="capBlock">Block placed at the top of the shaft (use Air to leave it open).</param>
        /// <param name="meta">Raw metadata byte for the cap block.</param>
        private static void BuildShaft(LightingTestWorld world, ushort capBlock, byte meta)
        {
            world.FillSuperflatFloor(FLOOR_TOP_Y, TestBlockPalette.Stone);

            // The cap sits at the floor surface with the shaft carved out beneath it.
            Vector3Int capPos = new Vector3Int(24, FLOOR_TOP_Y, 24);
            world.SetBlock(capPos, TestBlockPalette.Air);
            for (int i = 1; i <= SHAFT_DEPTH; i++)
                world.SetBlock(new Vector3Int(24, FLOOR_TOP_Y - i, 24), TestBlockPalette.Air);

            if (capBlock != TestBlockPalette.Air)
                world.SetBlock(capPos, capBlock, meta);

            world.RecalculateHeightmaps();
            world.RunInitialLighting();
        }

        /// <summary>
        /// Builds the shared fixture and returns the sky light in the voxel directly beneath the cap.
        /// </summary>
        /// <param name="capBlock">Block placed at the top of the shaft (use Air to leave it open).</param>
        /// <param name="meta">Raw metadata byte for the cap block — selects a partial block's orientation.</param>
        /// <param name="capCellLight">Sky light stored in the cap voxel itself, for diagnostics.</param>
        /// <returns>Sky light in the voxel below the cap.</returns>
        private static byte ShaftLightBelowCap(ushort capBlock, byte meta, out byte capCellLight)
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            BuildShaft(world, capBlock, meta);

            ChunkData data = world.GetChunkData(new Vector2Int(1, 1));
            capCellLight = LightBitMapping.GetSkyLight(data.GetLightData(8, FLOOR_TOP_Y, 8));
            return LightBitMapping.GetSkyLight(data.GetLightData(8, FLOOR_TOP_Y - 1, 8));
        }
    }
}
