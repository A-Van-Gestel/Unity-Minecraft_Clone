using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;
using Jobs.BurstData;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Baselines <b>B108-B114</b> — fidelity finding <b>C14</b>: the mixed-channel mirrors of the
    /// suite's white-only blocklight families. Every other blocklight scenario sources light from
    /// <see cref="TestBlockPalette.LampWhite"/> (15,15,15) or <see cref="TestBlockPalette.Torch"/>
    /// (14,14,14), where R == G == B at every voxel and every step — so comparing three identical
    /// actual channels against three identical expected channels cannot distinguish them. The class of
    /// defect that hides is per-channel <b>indexing</b>, not arithmetic: a transposed positional
    /// argument in <c>CrossChunkLightModApplier.ComputeBlocklight</c>'s
    /// <c>ApplyRemovalChannel(oldC, modC, isRemoval, independentC)</c> triple, a mask applied to the
    /// wrong <c>LightRemovalNode</c> channel in <c>PropagateDarknessRGB</c>, or a swapped
    /// <c>r</c>/<c>g</c>/<c>b</c> byte in <c>IPendingLightStore.AddPendingBlocklight</c>.
    /// <para>
    /// Each scenario here duplicates an existing white baseline with
    /// <see cref="TestBlockPalette.TorchMixed"/> (14,8,3) or <see cref="TestBlockPalette.LampMixed"/>
    /// (15,9,3); the white originals are unchanged and keep running alongside. The asymmetric triple is
    /// deliberately stronger than C14's suggested pure-R/pure-G mirror: with three distinct non-zero
    /// channels all six permutations are observable, whereas a pure-red source has G == B == 0 and
    /// cannot see a green-blue transposition at all. It also drives per-channel clamping — blue reaches
    /// 0 while red is still lit, which an equal-channel source can never express.
    /// </para>
    /// <para>
    /// Per C14's acceptance rule, each mirror was shown to detect a transposition at its target site while
    /// its white original stayed green (2026-08-19). Each scenario records the mutation that was <b>actually
    /// applied</b> to it — not a predicted one; four mutations were run, each alone, then reverted. The same
    /// table is in the fidelity doc's C14 entry, and the two records must be kept in step.
    /// </para>
    /// <para>
    /// Structural limit worth recording: <c>LightBitMapping.SetBlocklightRGB</c> /
    /// <c>GetBlocklightR|G|B</c> is NOT provable this way. The harness reads the field back through the
    /// same mapping, so transposing two channels in both the setter and the getter is a genuine no-op,
    /// and transposing one side alone corrupts every baseline — white ones included — so it cannot
    /// demonstrate any single mirror's discriminating power.
    /// </para>
    /// Self-registered via <see cref="AddC14ChannelMirrorBaselineScenarios"/> (the <c>Baselines/</c>
    /// group-partial pattern).
    /// </summary>
    public static partial class LightingValidationSuite
    {
        /// <summary>Superflat floor top shared by the C14 mirror worlds.</summary>
        private const int C14_FLOOR_Y = 10;

        /// <summary>The seam wall's x column for the B113 surface-stamp mirror (west of the x15|16 seam).</summary>
        private const int C14_STAMP_WALL_X = 15;

        /// <summary>Registers the C14 mixed-channel mirror baselines (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddC14ChannelMirrorBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B108: Mixed-channel blocklight falloff and opaque surface stamp hold per channel, with blue clamping to 0 while red is still lit (oracle-independent probe; fidelity C14 mirror of B38/B39)",
                Baseline_MixedChannelFalloffAndStampProbe));
            scenarios.Add(new Scenario(
                "B109: Cross-chunk mixed-channel blocklight persists toward an unloaded neighbor and replays with each channel intact (fidelity C14 mirror of B30)",
                Baseline_MixedChannelPersistReplay));
            scenarios.Add(new Scenario(
                "B110: A broken mixed-channel source's area is re-lit per channel by the cross-border independent source (fidelity C14 mirror of B12)",
                Baseline_MixedChannelCrossBorderRespread));
            scenarios.Add(new Scenario(
                "B111: Mixed-channel blocklight removal survives an in-flight neighbor job with no per-channel ghost (fidelity C14 mirror of B7)",
                Baseline_MixedChannelInFlightRemovalRace));
            scenarios.Add(new Scenario(
                "B112: Pool-recycled chunks re-light a mixed-channel field identically — no single channel left stale by Reset() (fidelity C14 mirror of B33)",
                Baseline_MixedChannelPoolRecycle));
            scenarios.Add(new Scenario(
                "B113: A seam wall's mixed-channel surface stamp re-derives on all three channels from the surviving cross-seam torch (fidelity C14 mirror of B63)",
                Baseline_MixedChannelSeamStampRederives));
            scenarios.Add(new Scenario(
                "B114: Band differential — mixed-channel place/break scripts are bit-identical banded vs full height (fidelity C14 mirror of B75/B76)",
                Baseline_MixedChannelBandDifferential));
        }

        /// <summary>
        /// B108 (A4 probe, fidelity C14 — the mirror of B38 + B39): the mixed-channel twin of the two
        /// hand-derived blocklight probes. A <see cref="TestBlockPalette.TorchMixed"/> emits (14,8,3),
        /// so the -1-per-air-voxel rule is asserted on three channels holding three different values at
        /// every step, and blue reaches 0 four voxels out while red still reads 10 — a per-channel clamp
        /// the equal-channel B38 cannot express. The second half mirrors B39: an opaque face receives a
        /// source-1 stamp on each channel and propagates none of it inward.
        /// <para>
        /// Constants are hand-derived; no oracle call (A4 independence). This also pins the palette's
        /// derived emission — <c>BlockTypeJobData</c> scales color (1.0, 0.6, 0.2) by emission/max — so
        /// a change to that rounding fails here rather than silently shifting every other C14 mirror.
        /// </para>
        /// <para>
        /// Prove-red (demonstrated 2026-08-19): transposing <c>EmissionG</c>/<c>EmissionB</c> in the
        /// <c>BlockTypeJobData</c> constructor reds the very first assert — the torch read <c>(14,3,8)</c>.
        /// White B38/B39 stayed green. Notably every <c>MatchesOracle</c> comparison in the suite ALSO stayed
        /// green, because <see cref="LightingOracle"/> seeds its channels from the same
        /// <c>BlockTypeJobData</c>: only these hand-derived constants can see a defect the oracle shares.
        /// </para>
        /// </summary>
        /// <returns>True when every hand-derived channel triple matches.</returns>
        private static bool Baseline_MixedChannelFalloffAndStampProbe()
        {
            bool passed = ProbeMixedChannelFalloff();
            passed &= ProbeMixedChannelOpaqueStamp();
            return passed;
        }

        /// <summary>The B108 falloff half — mirrors B38 with an asymmetric-channel torch.</summary>
        /// <returns>True when the hand-derived falloff triples match.</returns>
        private static bool ProbeMixedChannelFalloff()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(C14_FLOOR_Y, TestBlockPalette.Stone);
            world.SetBlock(new Vector3Int(24, 12, 24), TestBlockPalette.TorchMixed);
            world.RecalculateHeightmaps();
            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B108: falloff world initial lighting converges");

            // Hand-derived: emission (14,8,3); each air voxel costs 1 per channel, clamped at 0.
            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(24, 12, 24)) == (14, 8, 3),
                "B108: the mixed torch holds its asymmetric emission (14,8,3)",
                $"Expected (14,8,3), got {world.GetBlocklightRGB(new Vector3Int(24, 12, 24))} (palette emission scaling changed, or channels transposed?)");
            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(25, 12, 24)) == (13, 7, 2),
                "B108: one air voxel from the mixed torch = (13,7,2)",
                $"Expected (13,7,2) at x=25, got {world.GetBlocklightRGB(new Vector3Int(25, 12, 24))}");
            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(28, 12, 24)) == (10, 4, 0),
                "B108: four air voxels out, blue has clamped to 0 while red still reads 10 (per-channel clamp)",
                $"Expected (10,4,0) at x=28, got {world.GetBlocklightRGB(new Vector3Int(28, 12, 24))}");
            return passed;
        }

        /// <summary>The B108 surface-stamp half — mirrors B39 with an asymmetric-channel torch.</summary>
        /// <returns>True when the stamp triple matches and the sealed cube center stays dark.</returns>
        private static bool ProbeMixedChannelOpaqueStamp()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(C14_FLOOR_Y, TestBlockPalette.Stone);

            // B39's geometry: solid stone cube (24..28, 11..15, 24..28), torch against its west face.
            world.FillBox(new Vector3Int(24, 11, 24), new Vector3Int(28, 15, 28), TestBlockPalette.Stone);
            world.SetBlock(new Vector3Int(23, 13, 26), TestBlockPalette.TorchMixed);
            world.RecalculateHeightmaps();
            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B108: stamp world initial lighting converges");

            // Hand-derived: the opaque face adjacent to a (14,8,3) source stamps source-1 per channel.
            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(24, 13, 26)) == (13, 7, 2),
                "B108: the opaque face receives a source-1 surface stamp on every channel",
                $"Expected (13,7,2) on the lit face, got {world.GetBlocklightRGB(new Vector3Int(24, 13, 26))}");
            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(26, 13, 26)) == (0, 0, 0),
                "B108: the enclosed cube center stays pitch black on every channel",
                $"Expected (0,0,0) at the cube center, got {world.GetBlocklightRGB(new Vector3Int(26, 13, 26))}");
            return passed;
        }

        /// <summary>
        /// B109 (fidelity C14, family 1 — the mirror of B30): the cross-chunk persist-replay round-trip
        /// driven with an asymmetric source. This is the only path that writes light channels to a store
        /// and reads them back — <c>IPendingLightStore.AddPendingBlocklight</c> takes
        /// <c>byte r, byte g, byte b</c> as three positional arguments, and the persist column and the
        /// replay each carry them onward. A transposition round-trips white light perfectly, which is
        /// exactly why B30 cannot see it: here the replayed spill must read (12,6,1), not any permutation.
        /// <para>
        /// Prove-red (demonstrated 2026-08-19): transposing the mod payload <c>modG</c>/<c>modB</c> in
        /// <c>ComputeBlocklight</c> made the replayed spill read <c>(12,1,6)</c>; the
        /// <c>EmissionG</c>/<c>EmissionB</c> transposition of B108 reds it too. White B30 stayed green under
        /// both. A swap confined to the store side of <c>LightingStateManager.AddPendingBlocklight</c> would
        /// isolate the persist leg specifically, but was not run — the mutations above already discharge the
        /// acceptance rule for this family.
        /// </para>
        /// </summary>
        /// <returns>True when the persisted mixed-channel spill replays intact.</returns>
        private static bool Baseline_MixedChannelPersistReplay()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(C14_FLOOR_Y, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B109: initial lighting converges");

            // (2,1) is in-world but unloaded: the mixed torch's cross-chunk mod must be persisted.
            world.MarkChunkUnloaded(new Vector2Int(2, 1));
            world.PlaceBlock(new Vector3Int(31, 11, 24), TestBlockPalette.TorchMixed);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B109: persist-while-unloaded converges");

            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(33, 11, 24)) == (0, 0, 0),
                "B109: no channel leaks into the unloaded neighbor (persisted, not applied)",
                $"Expected (0,0,0) at x=33 while (2,1) unloaded, got {world.GetBlocklightRGB(new Vector3Int(33, 11, 24))}");

            world.MarkChunkLoaded(new Vector2Int(2, 1), LightingTestWorld.ChunkLoadMode.LoadFromDisk);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B109: post-replay convergence");

            // Hand-derived: source (14,8,3) at x=31, -1 per air voxel per channel — x=33 is two steps out.
            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(33, 11, 24)) == (12, 6, 1),
                "B109: the replayed spill crosses the border with each channel at its own level",
                $"Expected (12,6,1) at x=33 after replay, got {world.GetBlocklightRGB(new Vector3Int(33, 11, 24))} (r/g/b transposed through the pending store?)");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B109: replayed field matches oracle");
            return passed;
        }

        /// <summary>
        /// B110 (fidelity C14, family 3 — the mirror of B12): two overlapping cross-border mixed-channel
        /// sources; breaking one must re-light its area from the survivor and return the field
        /// bit-identically to the single-source baseline. This drives
        /// <c>CrossChunkLightModApplier.ComputeBlocklight</c>'s <c>ApplyRemovalChannel</c> triple and the
        /// Bug-17 veto with three unequal support values, so a veto consulting the wrong channel's
        /// support is observable — under B12's white torches every support value is identical.
        /// <para>
        /// Prove-red (demonstrated 2026-08-19): transposing the mod payload <c>modG</c>/<c>modB</c> across
        /// the <c>ApplyRemovalChannel</c> triple reds this baseline while white B12 stays green.
        /// </para>
        /// </summary>
        /// <returns>True when the post-break field equals the single-source baseline.</returns>
        private static bool Baseline_MixedChannelCrossBorderRespread()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(C14_FLOOR_Y, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B110: initial lighting converges");

            // The surviving source: mixed torch in chunk (2,1).
            world.PlaceBlock(new Vector3Int(36, 11, 24), TestBlockPalette.TorchMixed);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B110: surviving source converges");

            Dictionary<Vector2Int, ushort[]> baseline = world.SnapshotLightField();

            // The doomed source: mixed torch in chunk (1,1), fields overlapping across the border.
            world.PlaceBlock(new Vector3Int(28, 11, 24), TestBlockPalette.TorchMixed);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B110: both sources converge");

            world.BreakBlock(new Vector3Int(28, 11, 24));
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B110: post-break convergence");

            passed &= LightingAssert.FieldsEqual(baseline, world,
                "B110: every channel returns to the single-source baseline");
            return passed;
        }

        /// <summary>
        /// B111 (fidelity C14, family 7 — the mirror of B7): the in-flight blocklight-removal race with
        /// an asymmetric source. A <see cref="TestBlockPalette.LampMixed"/> on chunk (1,1)'s border
        /// column is broken while (2,1)'s job is in flight, so (1,1)'s removal mods are applied to
        /// (2,1)'s live data and then overwritten by the stale merge. Beyond B7's coverage, the darkness
        /// wave here re-enqueues <c>LightRemovalNode</c>s whose per-channel masks carry three different
        /// levels — a mask applied to the wrong channel leaves a ghost on one channel only.
        /// <para>
        /// Prove-red (demonstrated 2026-08-19): transposing the per-channel <b>support</b> arguments
        /// <c>independentG</c>/<c>independentB</c> in <c>ComputeBlocklight</c> — the Bug-17 veto consulting
        /// the wrong channel's support — leaves a surviving blue ghost (<c>B 0/1</c>) across the seam. White
        /// B7 stays green. This mirror is the only C14 one that catches that mutation: the mod-payload
        /// transposition that reds B109/B110/B112/B113 leaves this race green.
        /// </para>
        /// </summary>
        /// <returns>True when no per-channel ghost light survives the race.</returns>
        private static bool Baseline_MixedChannelInFlightRemovalRace()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(C14_FLOOR_Y, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B111: initial lighting converges");

            Dictionary<Vector2Int, ushort[]> baseline = world.SnapshotLightField();

            world.PlaceBlock(new Vector3Int(31, 11, 24), TestBlockPalette.LampMixed);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B111: mixed lamp converges");

            // Race: (2,1)'s job snapshots BEFORE the lamp is broken; (1,1)'s removal mods land on
            // (2,1)'s live data mid-flight and are then overwritten by the stale merge.
            LightingTestWorld.LightingJobFlight inFlight = world.BeginLightingJob(new Vector2Int(2, 1));
            world.BreakBlock(new Vector3Int(31, 11, 24));
            world.RunLightingJob(new Vector2Int(1, 1));
            world.CompleteLightingJob(inFlight);

            passed &= LightingAssert.Converged(world.RunToConvergence(), "B111: post-race convergence");
            passed &= LightingAssert.FieldsEqual(baseline, world,
                "B111: no ghost survives the race on any channel");
            return passed;
        }

        /// <summary>
        /// B112 (fidelity C14, family 6 — the mirror of B33): pool recycle through the real
        /// <c>ChunkData.Reset()</c> over a field that carries three distinct blocklight channels. B33's
        /// world is skylight-only, so it has no channels to leave stale; this mirror adds
        /// <see cref="TestBlockPalette.LampMixed"/> sources to the same 5x5 slab-and-sky-well geometry,
        /// so a single channel array left dirty by a recycle is observable. Stale-after-recycle is a
        /// documented recurring family (finding B4), which is why it earns a colored twin.
        /// <para>
        /// Prove-red (demonstrated 2026-08-19): transposing the mod payload <c>modG</c>/<c>modB</c> in
        /// <c>ComputeBlocklight</c> reds this baseline while white B33 stays green — B33 is skylight-only, so
        /// it has no blocklight channels to disagree about.
        /// </para>
        /// </summary>
        /// <returns>True when the re-lit field equals the pre-recycle snapshot and the oracle.</returns>
        private static bool Baseline_MixedChannelPoolRecycle()
        {
            using LightingTestWorld world = new LightingTestWorld(5);
            const int worldMax = 5 * VoxelData.ChunkWidth - 1;

            void BuildWorld()
            {
                world.FillSuperflatFloor(C14_FLOOR_Y, TestBlockPalette.Stone);
                world.FillBox(new Vector3Int(0, 30, 0), new Vector3Int(worldMax, 30, worldMax), TestBlockPalette.Stone);
                world.SetBlock(new Vector3Int(49, 30, 49), TestBlockPalette.Air);

                // The colored content B33 lacks: mixed lamps under the slab, one on a chunk border
                // column so the recycled state spans a seam.
                world.SetBlock(new Vector3Int(24, 12, 24), TestBlockPalette.LampMixed);
                world.SetBlock(new Vector3Int(31, 12, 40), TestBlockPalette.LampMixed);
                world.RecalculateHeightmaps();
            }

            BuildWorld();
            bool passed = LightingAssert.Converged(world.RunInitialLightingParallel(), "B112: pre-recycle initial lighting converges");

            Dictionary<Vector2Int, ushort[]> baseline = world.SnapshotLightField();
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B112: pre-recycle field matches oracle");

            // Precondition: the world really does carry three different channel levels, so a stale or
            // transposed channel has something to differ from.
            (byte R, byte G, byte B) lamp = world.GetBlocklightRGB(new Vector3Int(25, 12, 24));
            passed &= LightingAssert.IsTrue(lamp.R != lamp.G && lamp.G != lamp.B,
                "B112: the recycled world carries three distinct blocklight channels",
                $"expected three distinct channels beside the mixed lamp, got {lamp}");

            // Recycle every chunk through the REAL ChunkData.Reset(), then rebuild + re-light identically.
            world.RecycleAllChunks();
            BuildWorld(); // Reset() wiped voxels — re-author the identical terrain.

            passed &= LightingAssert.Converged(world.RunInitialLightingParallel(), "B112: post-recycle re-lighting converges");
            passed &= LightingAssert.FieldsEqual(baseline, world, "B112: re-lit field equals the pre-recycle snapshot on every channel");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B112: re-lit field matches oracle");
            return passed;
        }

        /// <summary>
        /// B113 (fidelity C14, family 4 — the mirror of B63): the cross-seam surface stamp re-derivation
        /// with asymmetric sources. B63 asserts only the RED channel of the re-derived stamp because its
        /// white torches make the other two identical; here each channel settles at its own level and
        /// all three are asserted against the oracle separately, so a stamp re-derived from the wrong
        /// channel's cross-seam value is caught.
        /// <para>
        /// Prove-red (demonstrated 2026-08-19): transposing the mod payload <c>modG</c>/<c>modB</c> in
        /// <c>ComputeBlocklight</c> reds this baseline while white B63 stays green — B63 asserts only the RED
        /// channel of the re-derived stamp, so a wrong-channel re-derivation is invisible to it.
        /// </para>
        /// </summary>
        /// <returns>True when all three channels re-derive to their oracle values.</returns>
        private static bool Baseline_MixedChannelSeamStampRederives()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(C14_FLOOR_Y, TestBlockPalette.Stone);
            world.FillBox(new Vector3Int(C14_STAMP_WALL_X, C14_FLOOR_Y + 1, 8),
                new Vector3Int(C14_STAMP_WALL_X, 20, 12), TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B113: initial lighting converges");

            world.PlaceBlock(new Vector3Int(18, 15, 10), TestBlockPalette.TorchMixed);
            world.PlaceBlock(new Vector3Int(13, 15, 10), TestBlockPalette.TorchMixed);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B113: two-torch setup converges");

            Vector3Int probe = new Vector3Int(C14_STAMP_WALL_X, 15, 10);
            (byte r0, byte g0, byte b0) = world.GetBlocklightRGB(probe);
            passed &= LightingAssert.IsTrue(r0 > 0 && g0 > 0 && b0 > 0 && r0 != g0 && g0 != b0,
                "B113: the seam wall carries a stamp whose three channels are distinct and non-zero",
                $"Expected three distinct non-zero channels at {probe}, got ({r0},{g0},{b0})");

            world.BreakBlock(new Vector3Int(13, 15, 10));
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B113: post-break reconciliation converges");

            OracleLightField oracle = LightingOracle.Solve(world);
            (byte r, byte g, byte b) = world.GetBlocklightRGB(probe);
            ushort expectedPacked = oracle.GetLightData(probe);
            byte expectedR = LightBitMapping.GetBlocklightR(expectedPacked);
            byte expectedG = LightBitMapping.GetBlocklightG(expectedPacked);
            byte expectedB = LightBitMapping.GetBlocklightB(expectedPacked);
            passed &= LightingAssert.IsTrue(r == expectedR && g == expectedG && b == expectedB,
                "B113: the wall's stamp re-derives on all three channels from the surviving east torch",
                $"Expected ({expectedR},{expectedG},{expectedB}) at {probe} after breaking the west torch, got ({r},{g},{b})");

            passed &= LightingAssert.MatchesOracle(world, oracle,
                "B113: field matches the borderless oracle after the west torch break");
            return passed;
        }

        /// <summary>
        /// B114 (fidelity C14, family 2 — the mirror of B75/B76): the LI-2 band differential driven with
        /// asymmetric-channel sources. The whole B71-B85 block is white-only, so a band that clips one
        /// channel's gradient is invisible to it: the banded and full-height legs clip identically when
        /// all three channels are equal. Here the gathered rows carry three different gradients, so a
        /// per-channel clip diverges the two legs.
        /// <para>
        /// Prove-red (demonstrated 2026-08-19): transposing G/B in <c>SetBlocklightRGB</c>'s band-gated
        /// store <b>only when the band is engaged</b> diverges the banded leg from the full-height leg and
        /// reds this baseline, while the entire white band block B75–B78 and B83–B85 stays green (with equal
        /// channels the transposition is a no-op, so both legs clip identically).
        /// </para>
        /// </summary>
        /// <returns>True when both mixed-channel scripts are bit-identical banded vs full height.</returns>
        private static bool Baseline_MixedChannelBandDifferential()
        {
            bool ok = BandDifferentialCase("B114: mid-air mixed lamp place+break differential", world =>
            {
                world.FillSuperflatFloor(C14_FLOOR_Y, TestBlockPalette.Stone);
                world.RecalculateHeightmaps();
                int rounds = world.RunInitialLighting();
                world.PlaceBlock(new Vector3Int(24, 30, 24), TestBlockPalette.LampMixed);
                rounds += world.RunToConvergence();
                world.BreakBlock(new Vector3Int(24, 30, 24));
                return rounds + world.RunToConvergence();
            });

            ok &= BandDifferentialCase("B114: cross-seam mixed torch pair, nearer source broken, differential", world =>
            {
                world.FillSuperflatFloor(C14_FLOOR_Y, TestBlockPalette.Stone);
                world.RecalculateHeightmaps();
                int rounds = world.RunInitialLighting();

                // Two mixed torches straddling the (1,1)/(2,1) seam; breaking the west one launches the
                // cross-seam removal + re-spread quadrant on three unequal channels.
                world.PlaceBlock(new Vector3Int(29, 11, 24), TestBlockPalette.TorchMixed);
                world.PlaceBlock(new Vector3Int(34, 11, 24), TestBlockPalette.TorchMixed);
                rounds += world.RunToConvergence();
                world.BreakBlock(new Vector3Int(29, 11, 24));
                return rounds + world.RunToConvergence();
            });

            return ok;
        }
    }
}
