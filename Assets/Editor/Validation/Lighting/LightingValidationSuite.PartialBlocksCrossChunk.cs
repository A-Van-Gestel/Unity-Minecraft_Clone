using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Cross-chunk directional occlusion for <b>partial blocks</b> — the <c>VO-4</c> half of
    /// <c>Documentation/Bugs/LIGHTING_BUGS.md</c> Bug 20, per
    /// <c>Documentation/Design/VOXEL_OCCLUSION_REFACTOR.md</c>.
    /// <para>
    /// <b>Why this phase existed.</b> <c>VO-3</c> made the in-chunk BFS deliver light through a partial
    /// block's open half but left the cross-chunk removal veto whole-block: its support scan skipped any
    /// <c>IsOpaque</c> neighbor, and a half slab is authored <c>opacity = 15</c>. So the BFS fed a seam
    /// voxel through a slab while the veto computed zero support for it — the removal initiator cleared it,
    /// the BFS re-lit it, and the pair cycled. That is the Bug 13 period-2 live-lock shape, reachable
    /// through a slab. <c>VO-4</c> closed it by making both the source guard and the entry cost per-face.
    /// </para>
    /// <para>
    /// <b>B106 tests the production decision function directly</b> rather than end-to-end. B49's own note
    /// records why: a full cross-chunk-removal scenario over a seam forms an orphaned light loop, so no
    /// removal mod ever reaches the target and the thing under test is masked. The support function is
    /// public and pure, so it is asserted with controlled inputs — still real production code.
    /// <b>B105</b> supplies the end-to-end half as a convergence guard.
    /// </para>
    /// </summary>
    public static partial class LightingValidationSuite
    {
        /// <summary>Facing6Roll2 metadata for a slab stood upright, solid half against the cell's +Z side.</summary>
        private const byte VO4_SLAB_VERTICAL = 0x03;

        /// <summary>Facing6Roll2 metadata for an unrotated slab, solid half resting on the cell floor.</summary>
        private const byte VO4_SLAB_HORIZONTAL = 0x00;

        /// <summary>Sky level written into the single lit neighbor of each support probe.</summary>
        private const byte VO4_NEIGHBOR_SKY = 12;

        /// <summary>
        /// Registers the VO-4 cross-chunk partial-block scenarios (called from the baseline runner).
        /// </summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddPartialBlockCrossChunkScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B106: the cross-chunk removal veto credits a partial block as a light source, per face",
                B106_SupportCreditsPartialBlockNeighbor));
            scenarios.Add(new Scenario(
                "B105: a seam-straddling gradient fed through partial blocks settles on the borderless oracle",
                B105_PartialBlockSeamGradientMatchesOracle));
            scenarios.Add(new Scenario(
                "B107: sealing a partial-block light shaft darkens the column beneath it",
                B107_SealedPartialBlockShaftDarkens));
        }

        /// <summary>
        /// B106 — the permanent guard for the VO-4 half of Bug 20, promoted from repro <c>K20b</c> on
        /// 2026-08-08 after the fix was confirmed in game (no flicker at a slab seam). The cross-chunk
        /// sunlight removal veto's support scan (<c>CrossChunkLightModApplier.InChunkSunlightSupport</c>)
        /// must answer the same question the BFS answers: <i>can this neighbor deliver light to me through
        /// the face between us?</i> Between VO-3 and VO-4 it could not — the scan skipped every
        /// <c>IsOpaque</c> neighbor, and a half slab is authored opaque, so a voxel the BFS legitimately
        /// fed through a slab read support 0 and a removal cleared what the BFS immediately restored.
        /// <para>
        /// Four legs, and the directional pairs are the point — this is not "slabs always count":
        /// </para>
        /// <list type="bullet">
        /// <item><b>Leg A — source side, open face:</b> the slab sits west of the probe and delivers through
        /// its +X face, which a vertical slab covers only half, so light passes and support is
        /// <c>Attenuate(sky, air)</c>. This is the leg that was red before the fix.</item>
        /// <item><b>Leg B — source side, covered face:</b> the same slab in the same rotation, now behind the
        /// probe in −Z so it would deliver through its +Z face, which that rotation covers <i>fully</i> —
        /// support stays 0. It passed before the fix too, for the wrong reason (the slab was skipped
        /// wholesale), and is kept as the tripwire against "credit every partial block in every
        /// direction", which would over-estimate support and veto legitimate removals.</item>
        /// <item><b>Leg C — full-cube guard:</b> B49's rule restated locally — a fully-opaque neighbor
        /// storing sky 15 is a surface stamp, never support. Guards against the directional change
        /// weakening the whole-block source guard.</item>
        /// <item><b>Leg D — target side:</b> the probe itself is the slab. Entry cost is directional too, so
        /// light arriving on the open face is charged air and on the covered face the authored opacity.</item>
        /// </list>
        /// </summary>
        /// <returns>True when the support scan answers per face on both the source and target sides.</returns>
        private static bool B106_SupportCreditsPartialBlockNeighbor()
        {
            using LightingTestWorld world = new LightingTestWorld(1);

            // Leg A — the slab's OPEN side. Neighbor at probe + FaceChecks[4] (-X), so it delivers
            // through its own +X face; a vertical slab's ±X faces are half covered, so light passes.
            Vector3Int openProbe = new Vector3Int(8, 64, 8);
            byte openSupport = SupportFromSlabNeighbor(world, openProbe, faceIndex: 4, VO4_SLAB_VERTICAL);
            const byte expectedOpen = VO4_NEIGHBOR_SKY - 1; // air entry cost into the probe

            bool passed = LightingAssert.IsTrue(openSupport == expectedOpen,
                "B106: a partial block delivering through an UNCOVERED face counts as support",
                $"expected {expectedOpen} (= sky {VO4_NEIGHBOR_SKY} attenuated by the air probe), got {openSupport}. "
                + "The veto's support scan is still whole-block: it skips the slab because it is authored "
                + "opacity 15, while the BFS since VO-3 does deliver light through the slab's open half. "
                + "That disagreement is the live-lock — the removal clears what the BFS re-lights.");

            // Leg B — the same slab, same rotation, SOLID side. Neighbor at probe + FaceChecks[0] (-Z)
            // delivers through its +Z face, which this rotation covers fully.
            Vector3Int solidProbe = new Vector3Int(8, 70, 8);
            byte solidSupport = SupportFromSlabNeighbor(world, solidProbe, faceIndex: 0, VO4_SLAB_VERTICAL);

            passed &= LightingAssert.IsTrue(solidSupport == 0,
                "B106: the same slab delivering through its COVERED face contributes no support",
                $"expected 0 support through the slab's solid half, got {solidSupport}. Crediting a partial "
                + "block in every direction would over-estimate support and veto legitimate removals — "
                + "the over-bright failure mode this scan exists to avoid.");

            // Leg C — tripwire: a full opaque cube is a surface stamp, never a source (B49's rule).
            Vector3Int cubeProbe = new Vector3Int(8, 76, 8);
            Vector3Int cubeNeighbor = cubeProbe + VoxelData.FaceChecks[4];
            world.SetBlock(cubeNeighbor, TestBlockPalette.Stone);
            world.SetSkyLightAt(cubeNeighbor, 15);
            byte cubeSupport = world.InChunkSunlightSupportAt(cubeProbe, 0);

            passed &= LightingAssert.IsTrue(cubeSupport == 0,
                "B106: a fully-opaque neighbor storing sky 15 still contributes no support",
                $"expected 0 from an opaque sky-15 neighbor, got {cubeSupport} — the directional change "
                + "must not weaken the whole-block source guard for full cubes.");

            // Leg D — the TARGET side of the same question. A slab receiving light pays its opacity only
            // on the faces its volume covers, so the entry cost is directional too. Charging the authored
            // 15 in every direction reads support 0 through the open half and clears light the BFS feeds.
            byte openEntry = SupportIntoSlabTarget(world, new Vector3Int(8, 82, 8), faceIndex: 0);
            byte covedEntry = SupportIntoSlabTarget(world, new Vector3Int(8, 88, 8), faceIndex: 1);

            passed &= LightingAssert.IsTrue(openEntry == expectedOpen && covedEntry == 0,
                "B106: entering a partial block costs air on an open face and its opacity on a covered one",
                $"expected open-face support {expectedOpen} and covered-face support 0, got {openEntry} and "
                + $"{covedEntry}. A vertical slab covers only its +Z face, so light arriving from −Z enters "
                + "through empty space and must not be charged the slab's authored opacity 15.");

            return passed;
        }

        /// <summary>
        /// Places a lit half slab in one face-neighbor of a probe and returns the in-chunk sunlight
        /// support the production veto computes for that probe (entering air, so the entry cost is the
        /// flat air step and the only variable is whether the slab is credited).
        /// </summary>
        /// <param name="world">The harness world to build into.</param>
        /// <param name="probe">The world-space voxel whose neighbors are scanned.</param>
        /// <param name="faceIndex">Index into <c>VoxelData.FaceChecks</c> locating the slab neighbor.</param>
        /// <param name="slabMeta">The slab's Facing6Roll2 metadata (its orientation).</param>
        /// <returns>The strongest support the scan finds for the probe.</returns>
        private static byte SupportFromSlabNeighbor(LightingTestWorld world, Vector3Int probe, int faceIndex, byte slabMeta)
        {
            Vector3Int neighbor = probe + VoxelData.FaceChecks[faceIndex];
            world.SetBlock(neighbor, TestBlockPalette.HalfSlab, slabMeta);
            world.SetSkyLightAt(neighbor, VO4_NEIGHBOR_SKY);
            return world.InChunkSunlightSupportAt(probe, 0);
        }

        /// <summary>
        /// Makes the probe voxel itself a vertical half slab, lights one of its face-neighbors, and
        /// returns the support the production veto computes for it — exercising the <b>entry</b> cost
        /// rather than the source guard.
        /// </summary>
        /// <param name="world">The harness world to build into.</param>
        /// <param name="probe">The world-space voxel that becomes the slab.</param>
        /// <param name="faceIndex">Index into <c>VoxelData.FaceChecks</c> locating the lit neighbor, and
        /// therefore the slab's entry face.</param>
        /// <returns>The strongest support the scan finds for the slab.</returns>
        private static byte SupportIntoSlabTarget(LightingTestWorld world, Vector3Int probe, int faceIndex)
        {
            world.SetBlock(probe, TestBlockPalette.HalfSlab, VO4_SLAB_VERTICAL);
            Vector3Int neighbor = probe + VoxelData.FaceChecks[faceIndex];
            world.SetSkyLightAt(neighbor, VO4_NEIGHBOR_SKY);
            return world.DirectionalInChunkSunlightSupportAt(probe);
        }

        /// <summary>
        /// B105 — the settled-field guard for partial blocks across chunk seams, and the suite's
        /// <b>first oracle comparison containing a partial block at all</b> (B101–B104 are probe-based by
        /// design, per F7). A ceiling of <i>horizontal</i> half slabs (solid side down, so they block)
        /// spans the center chunk and overhangs its neighbors, pierced by a line of <i>vertical</i> half
        /// slabs sitting exactly on a chunk seam: light punches down through their open halves and
        /// spreads sideways under the ceiling into both chunks. VO-4 must not disturb this field.
        /// <para>
        /// Authoring this scenario is what exposed the oracle's own column-seeding defect — its downward
        /// sky walk charged only each cell's <i>entry</i> cost through the top face, so a horizontal
        /// slab's open mid-plane top let the column pass straight through the solid half beneath it. The
        /// engine was right and the spec was wrong; <see cref="LightingOracle"/> now tests the bottom-face
        /// exit as well. Full-cube controls matched throughout, which is how the fixture was cleared
        /// before the oracle was touched.
        /// </para>
        /// </summary>
        /// <returns>True when the settled field matches the borderless oracle.</returns>
        private static bool B105_PartialBlockSeamGradientMatchesOracle()
        {
            using LightingTestWorld world = BuildVo4SeamWorld();
            world.RunInitialLighting();

            return LightingAssert.IsTrue(
                LightingAssert.MatchesOracleQuiet(world, LightingOracle.Solve(world), out string summary),
                "B105: the settled seam gradient under a partial-block ceiling matches the borderless oracle",
                summary);
        }

        /// <summary>
        /// B107 — the permanent guard for <c>_FIXED_BUGS.md</c> Bug 21, promoted from repro <c>K21a</c> on
        /// 2026-08-08. A vertical half slab admits an <b>undimmed</b> sky column (VO-3, baseline B104), so
        /// sealing that shaft must darken the column beneath it. Before the fix it did not — the column
        /// stayed at 15 forever, stable and 4 levels above the oracle.
        /// <para>
        /// The minimal form is deliberately <b>single-chunk</b>: the defect was first seen in a 3×3 seam
        /// world, but it reproduces with no chunk boundary anywhere, so it is not a cross-chunk defect and
        /// was not filed against VO-4.
        /// </para>
        /// <para>
        /// <b>Why this baseline is load-bearing.</b> Sealing without an opacity change needs in-place
        /// rotation or a same-opacity direct overwrite, and neither is player-reachable today (a normal seal
        /// is break → place, which changes opacity at both steps and always worked). So the stuck-column
        /// half is <b>latent</b> until in-place rotation ships — nothing in game can catch a regression
        /// here, and this scenario is the only thing that will. Do not delete it as "untested in practice".
        /// The wrong heightmap underneath it was never latent.
        /// </para>
        /// <para>
        /// <b>Mechanism</b> (classified, not guessed). The pre-fix heightmap test was
        /// <c>IsLightObstructing</c> = <c>Opacity &gt; 0</c>, so a half slab — authored opacity 15 — put the
        /// heightmap at itself; sealing it left the heightmap unchanged, so
        /// <c>RecalculateSunlightForColumn</c> (the authority for sky removal) never re-ran.
        /// <c>PropagateDarkness</c> could not finish the job either: it unwinds light by following exact
        /// <c>neighbor == old − cost</c> chains, which a flat 15 column does not have. The controls prove
        /// the diagnosis and are kept: a <b>Glass</b> shaft (full cube at opacity 0, equally undimmed column,
        /// but <i>not</i> light-obstructing, so its heightmap moved) always darkened correctly, as did an
        /// attenuating <b>Water</b> shaft — so a red slab leg cannot be mistaken for a broken fixture.
        /// </para>
        /// </summary>
        /// <returns>True when sealed partial-block shafts darken and opened ones re-light.</returns>
        private static bool B107_SealedPartialBlockShaftDarkens()
        {
            bool passed = SealedShaftDarkens("B107 control: a Glass shaft (full cube, undimmed column)",
                TestBlockPalette.Glass, meta: 0, TestBlockPalette.Stone, sealMeta: 0);
            passed &= SealedShaftDarkens("B107 control: a Water shaft (attenuating column)",
                TestBlockPalette.Water, meta: 0, TestBlockPalette.Stone, sealMeta: 0);
            passed &= SealedShaftDarkens("B107: a VERTICAL HALF SLAB shaft sealed with an opaque cube (Bug 21)",
                TestBlockPalette.HalfSlab, VO4_SLAB_VERTICAL, TestBlockPalette.Stone, sealMeta: 0);

            // Sealed by ROTATION alone — same block, same opacity 15, only the shape moves. This is the
            // case an opacity-valued trigger cannot see at all, so it is the sharpest form of the bug.
            passed &= SealedShaftDarkens("B107: the same slab sealed by ROTATION alone (no opacity change)",
                TestBlockPalette.HalfSlab, VO4_SLAB_VERTICAL, TestBlockPalette.HalfSlab, VO4_SLAB_HORIZONTAL);

            // Reverse direction: OPENING a shaft by standing a flat slab upright must light the column.
            // Guards against "fix Bug 21 by making every partial block obstruct", which would darken
            // correctly and then never re-light — trading a stuck-lit column for a stuck-dark one.
            passed &= OpenedShaftLights();

            return passed;
        }

        /// <summary>
        /// The reverse of <see cref="SealedShaftDarkens"/>: rotating a flat slab upright turns its cell into
        /// a full-height channel, so the column beneath it must go from shadowed to fully lit.
        /// </summary>
        /// <returns>True when opening the shaft lights the column to the oracle.</returns>
        private static bool OpenedShaftLights()
        {
            using LightingTestWorld world = new LightingTestWorld(1);
            world.FillSuperflatFloor(VO4_FLOOR_Y, TestBlockPalette.Stone);
            for (int x = 4; x <= 11; x++)
            for (int z = 4; z <= 11; z++)
                world.SetBlock(new Vector3Int(x, VO4_ROOM_CEILING_Y, z), TestBlockPalette.Stone);

            Vector3Int shaft = new Vector3Int(8, VO4_ROOM_CEILING_Y, 8);
            world.SetBlock(shaft, TestBlockPalette.HalfSlab, VO4_SLAB_HORIZONTAL);
            world.RecalculateHeightmaps();
            world.RunInitialLighting();

            Vector3Int probe = new Vector3Int(8, VO4_ROOM_CEILING_Y - 3, 8);
            byte before = world.GetSkyLight(probe);

            LightingFrameSimulator sim = new LightingFrameSimulator(world);
            world.PlaceBlock(shaft, TestBlockPalette.HalfSlab, VO4_SLAB_VERTICAL);
            sim.RunToConvergence(VO4_MAX_FRAMES, int.MaxValue, LightingFrameSimulator.CompletionOrder.Fifo);

            byte after = world.GetSkyLight(probe);
            return LightingAssert.IsTrue(
                LightingAssert.MatchesOracleQuiet(world, LightingOracle.Solve(world), out string summary)
                && after > before,
                "B107 reverse: standing a flat slab upright lights the column beneath it",
                $"{summary}. Probe {probe} went {before} -> {after}. If it did not brighten, the sky-column "
                + "obstruction test has been made unconditionally true for partial blocks — which fixes the "
                + "stuck-lit column by creating a stuck-dark one.");
        }

        /// <summary>
        /// Builds a single-chunk room with an opaque ceiling pierced by one shaft block, settles it,
        /// seals the shaft with Stone through the player-edit path, and asserts the field then matches
        /// the borderless oracle.
        /// </summary>
        /// <param name="label">Console label for this leg.</param>
        /// <param name="shaftBlock">The block forming the light shaft.</param>
        /// <param name="meta">The shaft block's metadata (its orientation).</param>
        /// <param name="sealBlock">The block the shaft is sealed with.</param>
        /// <param name="sealMeta">The sealing block's metadata.</param>
        /// <returns>True when the sealed field matches the oracle.</returns>
        private static bool SealedShaftDarkens(string label, ushort shaftBlock, byte meta, ushort sealBlock, byte sealMeta)
        {
            using LightingTestWorld world = new LightingTestWorld(1);
            world.FillSuperflatFloor(VO4_FLOOR_Y, TestBlockPalette.Stone);
            for (int x = 4; x <= 11; x++)
            for (int z = 4; z <= 11; z++)
                world.SetBlock(new Vector3Int(x, VO4_ROOM_CEILING_Y, z), TestBlockPalette.Stone);

            Vector3Int shaft = new Vector3Int(8, VO4_ROOM_CEILING_Y, 8);
            world.SetBlock(shaft, shaftBlock, meta);
            world.RecalculateHeightmaps();
            world.RunInitialLighting();

            LightingFrameSimulator sim = new LightingFrameSimulator(world);
            world.PlaceBlock(shaft, sealBlock, sealMeta);
            sim.RunToConvergence(VO4_MAX_FRAMES, int.MaxValue, LightingFrameSimulator.CompletionOrder.Fifo);

            Vector3Int probe = new Vector3Int(8, VO4_ROOM_CEILING_Y - 3, 8);
            return LightingAssert.IsTrue(
                LightingAssert.MatchesOracleQuiet(world, LightingOracle.Solve(world), out string summary),
                label,
                $"{summary}. Probe {probe} reads {world.GetSkyLight(probe)}. A column left at 15 after its "
                + "shaft is sealed is Bug 21: the slab is light-obstructing, so the heightmap never moves "
                + "and the authoritative column recalculation never re-runs, while the darkness wave has "
                + "no decrement chain to follow through a flat column.");
        }

        /// <summary>Frame budget for B105's settle — generous, so a red means "did not terminate", not "was slow".</summary>
        private const int VO4_MAX_FRAMES = 500;

        /// <summary>Y level of the partial-block ceiling, well clear of the floor so the gradient has room.</summary>
        private const int VO4_CEILING_Y = 100;

        /// <summary>Y level of B107's single-chunk room ceiling.</summary>
        private const int VO4_ROOM_CEILING_Y = 60;

        /// <summary>Inclusive top Y of the superflat floor beneath the ceiling.</summary>
        private const int VO4_FLOOR_Y = 10;

        /// <summary>
        /// Builds B105's world: a superflat floor, and a half-slab ceiling over the center chunk whose
        /// slabs are horizontal (blocking) except along the seam line returned by
        /// <see cref="Vo4ShaftPositions"/>, which are vertical (open). Returned un-lit.
        /// </summary>
        /// <returns>A freshly-built, un-lit test world.</returns>
        private static LightingTestWorld BuildVo4SeamWorld()
        {
            LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(VO4_FLOOR_Y, TestBlockPalette.Stone);

            // Ceiling over the center chunk, overhanging one voxel into each neighbor so the gradient
            // beneath it genuinely straddles the seams rather than stopping at them.
            const int min = VoxelData.ChunkWidth - 1;
            const int max = 2 * VoxelData.ChunkWidth;
            for (int x = min; x <= max; x++)
            for (int z = min; z <= max; z++)
                world.SetBlock(new Vector3Int(x, VO4_CEILING_Y, z), TestBlockPalette.HalfSlab, VO4_SLAB_HORIZONTAL);

            foreach (Vector3Int shaft in Vo4ShaftPositions())
                world.SetBlock(shaft, TestBlockPalette.HalfSlab, VO4_SLAB_VERTICAL);

            world.RecalculateHeightmaps();
            return world;
        }

        /// <summary>
        /// The ceiling cells that are vertical (open) slabs: the column sitting exactly on the center
        /// chunk's west seam, so the light they admit spreads under the ceiling into both chunks.
        /// </summary>
        /// <returns>World-space positions of the light shafts.</returns>
        private static IEnumerable<Vector3Int> Vo4ShaftPositions()
        {
            const int seamX = VoxelData.ChunkWidth; // local x = 0 of the center chunk
            for (int z = VoxelData.ChunkWidth + 4; z <= VoxelData.ChunkWidth + 11; z++)
                yield return new Vector3Int(seamX, VO4_CEILING_Y, z);
        }
    }
}
