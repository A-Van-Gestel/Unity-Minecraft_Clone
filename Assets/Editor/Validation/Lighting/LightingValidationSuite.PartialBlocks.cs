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
    /// VO-2 — directional occlusion baselines for <b>partial blocks</b> (half slabs), the behaviour
    /// <c>Documentation/Design/VOXEL_OCCLUSION_REFACTOR.md</c> VO-3 implements and
    /// <c>Documentation/Bugs/LIGHTING_BUGS.md</c> Bug 20 describes.
    /// <para>
    /// <b>Split across the two scenario channels, per the suite taxonomy.</b> The scenario that asserts
    /// behaviour the engine does not yet have — daylight passing a vertical slab's open half — is a
    /// <b>known-bug</b> scenario (<c>K20a</c>, tagged Bug 20): it is expected to fail, does not mark the
    /// suite red, and flips to a cyan "fix candidate" the moment VO-3 lands. The other three are ordinary
    /// <b>baselines</b> (B101–B103) that pass today and must keep passing through VO-3 — they are the
    /// tripwires against "fix Bug 20 by making slabs transparent".
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
        /// Registers the VO-2 partial-block occlusion scenarios (called from the suite runner): three
        /// baselines that must stay green, plus the <c>K20a</c> known-bug repro in the other channel.
        /// </summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddPartialBlockOcclusionScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("B101: an UNROTATED half slab capping a shaft blocks daylight through its solid underside", B101_FloorSlabBlocksDaylight));
            scenarios.Add(new Scenario("B102: control — a full opaque cube capping a shaft blocks daylight", B102_FullCubeBlocksDaylight));
            scenarios.Add(new Scenario("B103: control — an uncapped shaft is fully lit", B103_OpenShaftIsLit));
            scenarios.Add(new Scenario("K20a: a VERTICAL half slab capping a shaft lets daylight past its open half", K20a_VerticalSlabPassesDaylight, "20"));
        }

        /// <summary>
        /// K20a — <b>the motivating case.</b> A two-deep shaft in a superflat floor, capped by a half slab
        /// rotated upright (<see cref="SLAB_VERTICAL_META"/>). That orientation puts the slab's solid half
        /// against the cell's +Z side, leaving its ±Y faces only half covered — so neither the top nor the
        /// bottom face fully occludes, and daylight must reach the voxel below the slab.
        /// <para>
        /// Asserted as reach / no-reach rather than an exact level, so it does not restate the cost formula
        /// and stays valid whichever attenuation VO-3 settles on.
        /// </para>
        /// </summary>
        /// <returns>True when light reaches below the vertical slab.</returns>
        private static bool K20a_VerticalSlabPassesDaylight()
        {
            byte below = ShaftLightBelowCap(TestBlockPalette.HalfSlab, SLAB_VERTICAL_META, out byte capCell);
            return LightingAssert.IsTrue(below > 0,
                "K20a: daylight passes a vertical half slab's open half",
                $"expected sky > 0 below the slab, got {below} (slab cell itself = {capCell}). "
                + "Until VO-3 the slab is treated as a full blocker in every direction, so this reads 0 — "
                + "that is the documented LIGHTING_BUGS.md Bug 20 failure, not a regression.");
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
                + "broken, so B101, B102 and K20a prove nothing.");
        }

        /// <summary>
        /// Builds the shared fixture: a superflat opaque floor with a two-deep shaft carved into it, capped
        /// at the top by <paramref name="capBlock"/>, lit to convergence. Returns the sky light in the
        /// voxel directly beneath the cap.
        /// </summary>
        /// <param name="capBlock">Block placed at the top of the shaft (use Air to leave it open).</param>
        /// <param name="meta">Raw metadata byte for the cap block — selects a partial block's orientation.</param>
        /// <param name="capCellLight">Sky light stored in the cap voxel itself, for diagnostics.</param>
        /// <returns>Sky light in the voxel below the cap.</returns>
        private static byte ShaftLightBelowCap(ushort capBlock, byte meta, out byte capCellLight)
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(FLOOR_TOP_Y, TestBlockPalette.Stone);

            // A two-deep shaft: the cap sits at the floor surface, the probe one voxel below it.
            Vector3Int capPos = new Vector3Int(24, FLOOR_TOP_Y, 24);
            Vector3Int probePos = new Vector3Int(24, FLOOR_TOP_Y - 1, 24);
            world.SetBlock(capPos, TestBlockPalette.Air);
            world.SetBlock(probePos, TestBlockPalette.Air);
            if (capBlock != TestBlockPalette.Air)
                world.SetBlock(capPos, capBlock, meta);

            world.RecalculateHeightmaps();
            world.RunInitialLighting();

            ChunkData data = world.GetChunkData(new Vector2Int(1, 1));
            capCellLight = LightBitMapping.GetSkyLight(data.GetLightData(8, capPos.y, 8));
            return LightBitMapping.GetSkyLight(data.GetLightData(8, probePos.y, 8));
        }
    }
}
