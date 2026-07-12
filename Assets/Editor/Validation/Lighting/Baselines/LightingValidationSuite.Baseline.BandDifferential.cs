using System;
using System.Collections.Generic;
using Data;
using Editor.Validation.Lighting.Framework;
using Helpers;
using UnityEngine;
using Random = System.Random;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// The LI-2 band differential gate: every scenario here runs an identical world-and-edit script
    /// TWICE — once at full height (<see cref="LightingBandGatherMode.FullHeight"/>, the pre-LI-2
    /// reference) and once through the derived Y-band (<see cref="LightingBandGatherMode.Derived"/>,
    /// the production banded path) — and requires the two converged light fields to be
    /// <b>bit-identical</b> with the <b>same total round count</b> (rounds are the cheap proxy for the
    /// mod-stream/stability equality the "bit-identical light output" acceptance criterion demands).
    /// B78 and B85 are the gate's prove-reds: a sabotaged derivation (headroom-stripped band top /
    /// raised band bottom) must make the same differential FAIL, proving the gate has teeth before its
    /// green is trusted. The bottom-band scenarios (B83+) additionally assert ENGAGEMENT — at least one
    /// banded-leg job must actually derive <c>bandMinY &gt; 0</c>, so a derivation that never engages
    /// cannot vacuously pass. Self-registered via the
    /// <see cref="AddBandDifferentialBaselineScenarios"/> hook (the <c>Baselines/</c> group-partial
    /// pattern).
    /// </summary>
    public static partial class LightingValidationSuite
    {
        /// <summary>Registers the LI-2 band differential baselines (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddBandDifferentialBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B75: Band differential — steady-state edits (torch place, place+break, surface edits) are bit-identical banded vs full height (LI-2)",
                Baseline_BandDifferentialSteadyStateEdits));
            scenarios.Add(new Scenario(
                "B76: Band differential — band-top and seam stress (tall pillar lamp, cross-seam slab darkening + removal, border lamp) are bit-identical (LI-2)",
                Baseline_BandDifferentialBandTopAndSeams));
            scenarios.Add(new Scenario(
                "B77: Band differential fuzz — randomized floors and edits converge bit-identical banded vs full height across seeds (LI-2)",
                Baseline_BandDifferentialFuzz));
            scenarios.Add(new Scenario(
                "B78: Band differential prove-red — a headroom-stripped band MUST be caught by the differential, then the honest band passes (LI-2)",
                Baseline_BandDifferentialProveRed));
            scenarios.Add(new Scenario(
                "B83: Bottom-band differential — buried lamp place+break, dark cave carve, and surface edits over a deep floor are bit-identical with the bottom band ENGAGED (LI-2 bottom band)",
                Baseline_BandBottomDifferentialSteadyState));
            scenarios.Add(new Scenario(
                "B84: Bottom-band differential fuzz — randomized deep floors with buried and surface edits converge bit-identical across seeds (LI-2 bottom band)",
                Baseline_BandBottomDifferentialFuzz));
            scenarios.Add(new Scenario(
                "B85: Bottom-band differential prove-red — a raised band floor MUST be caught by the differential, then the honest bottom passes (LI-2 bottom band)",
                Baseline_BandBottomDifferentialProveRed));
        }

        /// <summary>Grid size shared by every band differential world (3×3 — full cross-seam coverage).</summary>
        private const int BAND_DIFF_GRID = 3;

        /// <summary>
        /// Runs one world-and-edit script under the given band mode and returns its converged light
        /// field, the script's accumulated convergence rounds, and the run's highest derived band
        /// bottom (the bottom-band engagement signal).
        /// </summary>
        /// <param name="mode">The band mode for every job the script schedules.</param>
        /// <param name="script">Builds, edits, and converges the world; returns total rounds.</param>
        /// <param name="sabotage">Optional prove-red band-top sabotage (Derived mode only).</param>
        /// <param name="bottomSabotage">Optional prove-red band-bottom sabotage (Derived mode only).</param>
        /// <returns>The snapshotted light field, the round count, and the max derived bandMinY.</returns>
        private static (ushort[] field, int rounds, int maxBandMinY) RunBandScript(
            LightingBandGatherMode mode, Func<LightingTestWorld, int> script,
            Func<int, int> sabotage = null, Func<int, int> bottomSabotage = null)
        {
            using LightingTestWorld world = new LightingTestWorld(BAND_DIFF_GRID);
            world.BandGatherMode = mode;
            world.BandHeightSabotageHook = sabotage;
            world.BandMinYSabotageHook = bottomSabotage;
            int rounds = script(world);
            return (SnapshotBandLightField(world), rounds, world.MaxDerivedBandMinY);
        }

        /// <summary>Snapshots the full 3×3 grid's light field (every voxel's packed ushort) for the
        /// banded-vs-full comparison.</summary>
        /// <param name="world">The converged world.</param>
        /// <returns>The flat field, chunk-major.</returns>
        private static ushort[] SnapshotBandLightField(LightingTestWorld world)
        {
            ushort[] field = new ushort[BAND_DIFF_GRID * BAND_DIFF_GRID * LightingTestWorld.ChunkBufferLength];
            int i = 0;
            for (int cx = 0; cx < BAND_DIFF_GRID; cx++)
            {
                for (int cz = 0; cz < BAND_DIFF_GRID; cz++)
                {
                    ChunkData data = world.GetChunkData(new Vector2Int(cx, cz));
                    for (int y = 0; y < VoxelData.ChunkHeight; y++)
                    for (int z = 0; z < VoxelData.ChunkWidth; z++)
                    for (int x = 0; x < VoxelData.ChunkWidth; x++)
                        field[i++] = data.GetLightData(x, y, z);
                }
            }

            return field;
        }

        /// <summary>
        /// The differential core: runs <paramref name="script"/> under both band modes and compares.
        /// Quiet — no console output; callers assert on the result (the prove-red EXPECTS a mismatch).
        /// </summary>
        /// <param name="script">The world-and-edit script (must be deterministic).</param>
        /// <param name="detail">Human-readable comparison summary for failure reports.</param>
        /// <param name="sabotage">Optional band sabotage applied to the Derived run only.</param>
        /// <returns>True when the fields are bit-identical and round counts match.</returns>
        private static bool BandDifferentialMatches(
            Func<LightingTestWorld, int> script, out string detail, Func<int, int> sabotage = null)
        {
            return BandDifferentialMatches(script, out detail, out _, sabotage);
        }

        /// <summary>
        /// The differential core with bottom-band support: runs <paramref name="script"/> under both
        /// band modes and compares, also reporting the banded leg's highest derived band bottom (the
        /// engagement signal bottom-band scenarios assert on).
        /// </summary>
        /// <param name="script">The world-and-edit script (must be deterministic).</param>
        /// <param name="detail">Human-readable comparison summary for failure reports.</param>
        /// <param name="bandedMaxBandMinY">The banded run's highest derived <c>bandMinY</c>.</param>
        /// <param name="sabotage">Optional band-top sabotage applied to the Derived run only.</param>
        /// <param name="bottomSabotage">Optional band-bottom sabotage applied to the Derived run only.</param>
        /// <returns>True when the fields are bit-identical and round counts match.</returns>
        private static bool BandDifferentialMatches(
            Func<LightingTestWorld, int> script, out string detail, out int bandedMaxBandMinY,
            Func<int, int> sabotage = null, Func<int, int> bottomSabotage = null)
        {
            (ushort[] fullField, int fullRounds, _) = RunBandScript(LightingBandGatherMode.FullHeight, script);
            (ushort[] bandField, int bandRounds, int maxBandMinY) = RunBandScript(LightingBandGatherMode.Derived, script, sabotage, bottomSabotage);
            bandedMaxBandMinY = maxBandMinY;

            int mismatches = 0;
            int firstIndex = -1;
            for (int i = 0; i < fullField.Length; i++)
            {
                if (fullField[i] == bandField[i]) continue;
                if (firstIndex < 0) firstIndex = i;
                mismatches++;
            }

            string first = "";
            if (firstIndex >= 0)
            {
                const int perChunk = LightingTestWorld.ChunkBufferLength;
                int chunkIndex = firstIndex / perChunk;
                int local = firstIndex % perChunk;
                int y = local / (VoxelData.ChunkWidth * VoxelData.ChunkWidth);
                int rem = local % (VoxelData.ChunkWidth * VoxelData.ChunkWidth);
                int z = rem / VoxelData.ChunkWidth;
                int x = rem % VoxelData.ChunkWidth;
                first = $"; first at chunk({chunkIndex / BAND_DIFF_GRID},{chunkIndex % BAND_DIFF_GRID}) " +
                        $"local({x},{y},{z}): full=0x{fullField[firstIndex]:X4} banded=0x{bandField[firstIndex]:X4}";
            }

            detail = $"mismatches={mismatches}, rounds full={fullRounds} banded={bandRounds}{first}";
            return mismatches == 0 && fullRounds == bandRounds;
        }

        /// <summary>Asserts one differential case matches, with suite-style logging.</summary>
        /// <param name="caseName">The sub-case name for the log line.</param>
        /// <param name="script">The deterministic world-and-edit script.</param>
        /// <returns>True when banded and full-height runs are bit-identical.</returns>
        private static bool BandDifferentialCase(string caseName, Func<LightingTestWorld, int> script)
        {
            bool matched = BandDifferentialMatches(script, out string detail);
            return LightingAssert.IsTrue(matched, caseName, $"banded run diverged from full-height run: {detail}");
        }

        /// <summary>
        /// B75: the bread-and-butter steady-state relights the band optimizes — a mid-air torch, a
        /// torch placed then broken, and surface block edits (column-recalc path) — each bit-identical
        /// banded vs full height.
        /// </summary>
        /// <returns>True when every case matches.</returns>
        private static bool Baseline_BandDifferentialSteadyStateEdits()
        {
            bool ok = BandDifferentialCase("B75: mid-air torch differential", world =>
            {
                world.FillSuperflatFloor(10, TestBlockPalette.Stone);
                world.RecalculateHeightmaps();
                int rounds = world.RunInitialLighting();
                world.PlaceBlock(new Vector3Int(24, 30, 24), TestBlockPalette.LampWhite);
                return rounds + world.RunToConvergence();
            });

            ok &= BandDifferentialCase("B75: torch place+break differential", world =>
            {
                world.FillSuperflatFloor(10, TestBlockPalette.Stone);
                world.RecalculateHeightmaps();
                int rounds = world.RunInitialLighting();
                world.PlaceBlock(new Vector3Int(24, 30, 24), TestBlockPalette.LampWhite);
                rounds += world.RunToConvergence();
                world.BreakBlock(new Vector3Int(24, 30, 24));
                return rounds + world.RunToConvergence();
            });

            ok &= BandDifferentialCase("B75: surface edits differential (column recalc)", world =>
            {
                world.FillSuperflatFloor(10, TestBlockPalette.Stone);
                world.RecalculateHeightmaps();
                int rounds = world.RunInitialLighting();
                world.PlaceBlock(new Vector3Int(20, 11, 20), TestBlockPalette.Stone);
                world.BreakBlock(new Vector3Int(28, 10, 28));
                return rounds + world.RunToConvergence();
            });

            return ok;
        }

        /// <summary>
        /// B76: the band's hardest geometry — content pushed toward the band top and waves crossing
        /// chunk seams (the C3 darkening quadrant): a tall pillar with a lamp on top, a cross-seam
        /// shadow slab placed (darkening) then broken (re-light), and a lamp on a border column.
        /// </summary>
        /// <returns>True when every case matches.</returns>
        private static bool Baseline_BandDifferentialBandTopAndSeams()
        {
            bool ok = BandDifferentialCase("B76: tall pillar + lamp near band top differential", world =>
            {
                world.FillSuperflatFloor(10, TestBlockPalette.Stone);
                world.RecalculateHeightmaps();
                int rounds = world.RunInitialLighting();
                for (int y = 11; y < 60; y++)
                    world.PlaceBlock(new Vector3Int(24, y, 24), TestBlockPalette.Stone);
                rounds += world.RunToConvergence();
                world.PlaceBlock(new Vector3Int(24, 60, 24), TestBlockPalette.LampWhite);
                return rounds + world.RunToConvergence();
            });

            ok &= BandDifferentialCase("B76: cross-seam shadow slab place (darkening) + break (re-light) differential", world =>
            {
                world.FillSuperflatFloor(10, TestBlockPalette.Stone);
                world.RecalculateHeightmaps();
                int rounds = world.RunInitialLighting();

                // A slab straddling the (1,1)/(2,1) seam at y=60: placing it launches cross-seam
                // darkening waves below (the C3 quadrant); breaking its center re-lights them.
                for (int x = 28; x <= 35; x++)
                for (int z = 22; z <= 26; z++)
                    world.PlaceBlock(new Vector3Int(x, 60, z), TestBlockPalette.Stone);
                rounds += world.RunToConvergence();

                for (int z = 22; z <= 26; z++)
                    world.BreakBlock(new Vector3Int(31, 60, z));
                return rounds + world.RunToConvergence();
            });

            ok &= BandDifferentialCase("B76: border-column lamp differential", world =>
            {
                world.FillSuperflatFloor(10, TestBlockPalette.Stone);
                world.RecalculateHeightmaps();
                int rounds = world.RunInitialLighting();
                world.PlaceBlock(new Vector3Int(31, 40, 24), TestBlockPalette.LampWhite);
                return rounds + world.RunToConvergence();
            });

            return ok;
        }

        /// <summary>
        /// B77: randomized floors and edit scripts across seeds — the wide-net counterpart to B75/B76's
        /// crafted cases. Each seed's script is deterministic (seeded <see cref="System.Random"/>
        /// constructed inside the script), so both band modes replay the identical sequence.
        /// </summary>
        /// <returns>True when every seed matches.</returns>
        private static bool Baseline_BandDifferentialFuzz()
        {
            const int seeds = 12;
            const int editsPerSeed = 6;

            bool ok = true;
            for (int seed = 0; seed < seeds; seed++)
            {
                int capturedSeed = seed;
                bool matched = BandDifferentialMatches(world =>
                {
                    Random rnd = new Random(capturedSeed * 7919 + 17);
                    int floorY = 8 + rnd.Next(24);
                    world.FillSuperflatFloor(floorY, TestBlockPalette.Stone);
                    world.RecalculateHeightmaps();
                    int rounds = world.RunInitialLighting();

                    for (int edit = 0; edit < editsPerSeed; edit++)
                    {
                        int x = rnd.Next(BAND_DIFF_GRID * VoxelData.ChunkWidth);
                        int z = rnd.Next(BAND_DIFF_GRID * VoxelData.ChunkWidth);
                        int action = rnd.Next(3);
                        if (action == 0)
                            world.PlaceBlock(new Vector3Int(x, floorY + 1 + rnd.Next(40), z), TestBlockPalette.LampWhite);
                        else if (action == 1)
                            world.PlaceBlock(new Vector3Int(x, floorY + 1 + rnd.Next(40), z), TestBlockPalette.Stone);
                        else
                            world.BreakBlock(new Vector3Int(x, floorY, z));

                        // Converge every second edit so waves interleave rather than batch.
                        if (edit % 2 == 1) rounds += world.RunToConvergence();
                    }

                    return rounds + world.RunToConvergence();
                }, out string detail);

                if (!matched)
                {
                    ok = LightingAssert.IsTrue(false,
                        $"B77: band differential fuzz seed {seed}",
                        $"banded run diverged from full-height run: {detail}");
                }
            }

            if (ok) Debug.Log($"[PASS] B77: band differential fuzz — {seeds} seeds bit-identical");
            return ok;
        }

        /// <summary>
        /// B78: the gate's prove-red. With the derivation's headroom stripped (and a little more, so a
        /// mid-air torch's node clears the sabotaged band), the torch's light lands entirely in skipped
        /// rows — the differential MUST report a divergence. The honest derivation on the identical
        /// script must then pass. A differential that cannot fail the sabotage proves nothing.
        /// </summary>
        /// <returns>True when the sabotage is caught AND the honest band matches.</returns>
        private static bool Baseline_BandDifferentialProveRed()
        {
            Func<LightingTestWorld, int> script = world =>
            {
                world.FillSuperflatFloor(10, TestBlockPalette.Stone);
                world.RecalculateHeightmaps();
                int rounds = world.RunInitialLighting();
                world.PlaceBlock(new Vector3Int(24, 30, 24), TestBlockPalette.LampWhite);
                return rounds + world.RunToConvergence();
            };

            // Strip the headroom and the node-coverage margin: the honest band for the lamp job is
            // max(ceiling 16, node 30+1) + 16 = 47; the sabotage drops it below the lamp itself.
            Func<int, int> sabotage = honest => honest >= ChunkMath.CHUNK_HEIGHT
                ? honest
                : Math.Max(1, honest - LightingBandDecision.BandHeadroomVoxels - 8);

            bool sabotageCaught = !BandDifferentialMatches(script, out string sabotageDetail, sabotage);
            bool ok = LightingAssert.IsTrue(sabotageCaught,
                "B78: headroom-stripped band is CAUGHT by the differential (prove-red)",
                $"sabotaged band produced a bit-identical field — the differential has no teeth ({sabotageDetail})");

            bool honestMatches = BandDifferentialMatches(script, out string honestDetail);
            ok &= LightingAssert.IsTrue(honestMatches,
                "B78: honest band passes the identical script",
                $"honest derivation diverged: {honestDetail}");

            return ok;
        }

        /// <summary>
        /// Asserts one bottom-band differential case matches AND (when expected) that the banded leg
        /// actually engaged the bottom band — a bandMinY that stays 0 makes the bit-identity comparison
        /// vacuous for the bottom, so engagement is part of the assertion.
        /// </summary>
        /// <param name="caseName">The sub-case name for the log line.</param>
        /// <param name="script">The deterministic world-and-edit script.</param>
        /// <param name="expectEngaged">Whether at least one banded-leg job must derive bandMinY &gt; 0.</param>
        /// <returns>True when the runs are bit-identical (and engaged, when expected).</returns>
        private static bool BandBottomDifferentialCase(
            string caseName, Func<LightingTestWorld, int> script, bool expectEngaged = true)
        {
            bool matched = BandDifferentialMatches(script, out string detail, out int engagedMinY);
            bool ok = LightingAssert.IsTrue(matched, caseName,
                $"banded run diverged from full-height run: {detail}");

            if (expectEngaged)
            {
                ok &= LightingAssert.IsTrue(engagedMinY > 0,
                    caseName + " — bottom band ENGAGED",
                    "no job in the banded leg derived bandMinY > 0; the bottom-band differential was vacuous");
            }

            return ok;
        }

        /// <summary>
        /// B83: the bottom band's bread-and-butter cases over a deep (3-section) floor — a buried lamp
        /// placed then broken inside solid stone, a dark cave pocket carved underground, and surface
        /// edits (column-recalc coexistence; the recalc job itself legitimately runs bottom 0). The
        /// first two must run with the bottom band engaged.
        /// </summary>
        /// <returns>True when every case matches (and engages where expected).</returns>
        private static bool Baseline_BandBottomDifferentialSteadyState()
        {
            bool ok = BandBottomDifferentialCase("B83: buried lamp place+break differential", world =>
            {
                world.FillSuperflatFloor(47, TestBlockPalette.Stone);
                world.RecalculateHeightmaps();
                int rounds = world.RunInitialLighting();
                world.PlaceBlock(new Vector3Int(24, 20, 24), TestBlockPalette.LampWhite);
                rounds += world.RunToConvergence();
                world.BreakBlock(new Vector3Int(24, 20, 24));
                return rounds + world.RunToConvergence();
            });

            ok &= BandBottomDifferentialCase("B83: dark cave pocket carve differential", world =>
            {
                world.FillSuperflatFloor(47, TestBlockPalette.Stone);
                world.RecalculateHeightmaps();
                int rounds = world.RunInitialLighting();

                // A 3×2×3 pocket straddling the (1,1)/(2,1) seam at y=20 — pure darkness (no light
                // enters), so every wave is a no-op OVER a non-zero band floor.
                for (int x = 30; x <= 32; x++)
                for (int z = 23; z <= 25; z++)
                for (int y = 20; y <= 21; y++)
                    world.BreakBlock(new Vector3Int(x, y, z));

                return rounds + world.RunToConvergence();
            });

            ok &= BandBottomDifferentialCase("B83: surface edits over a deep floor differential (column-recalc coexistence)", world =>
            {
                world.FillSuperflatFloor(47, TestBlockPalette.Stone);
                world.RecalculateHeightmaps();
                int rounds = world.RunInitialLighting();
                world.PlaceBlock(new Vector3Int(20, 48, 20), TestBlockPalette.Stone);
                world.BreakBlock(new Vector3Int(28, 47, 28));
                rounds += world.RunToConvergence();
                world.PlaceBlock(new Vector3Int(24, 60, 24), TestBlockPalette.LampWhite);
                return rounds + world.RunToConvergence();
            }, expectEngaged: false);

            return ok;
        }

        /// <summary>
        /// B84: randomized deep floors with a guaranteed buried edit per seed plus surface/mid-air
        /// edits — the wide-net counterpart to B83's crafted cases. Each seed's script is deterministic,
        /// so both band modes replay the identical sequence.
        /// </summary>
        /// <returns>True when every seed matches.</returns>
        private static bool Baseline_BandBottomDifferentialFuzz()
        {
            const int seeds = 8;
            const int editsPerSeed = 6;

            bool ok = true;
            for (int seed = 0; seed < seeds; seed++)
            {
                int capturedSeed = seed;
                bool matched = BandDifferentialMatches(world =>
                {
                    Random rnd = new Random(capturedSeed * 6271 + 31);
                    int floorY = 40 + rnd.Next(8); // deep floor: 2 full dark sections minimum
                    world.FillSuperflatFloor(floorY, TestBlockPalette.Stone);
                    world.RecalculateHeightmaps();
                    int rounds = world.RunInitialLighting();

                    for (int edit = 0; edit < editsPerSeed; edit++)
                    {
                        int x = rnd.Next(BAND_DIFF_GRID * VoxelData.ChunkWidth);
                        int z = rnd.Next(BAND_DIFF_GRID * VoxelData.ChunkWidth);
                        int action = edit == 0 ? 0 : rnd.Next(4); // edit 0 is always a buried lamp
                        if (action == 0)
                            world.PlaceBlock(new Vector3Int(x, 4 + rnd.Next(floorY - 8), z), TestBlockPalette.LampWhite);
                        else if (action == 1)
                            world.BreakBlock(new Vector3Int(x, 4 + rnd.Next(floorY - 8), z));
                        else if (action == 2)
                            world.PlaceBlock(new Vector3Int(x, floorY + 1 + rnd.Next(30), z), TestBlockPalette.LampWhite);
                        else
                            world.BreakBlock(new Vector3Int(x, floorY, z));

                        // Converge every second edit so waves interleave rather than batch.
                        if (edit % 2 == 1) rounds += world.RunToConvergence();
                    }

                    return rounds + world.RunToConvergence();
                }, out string detail, out _);

                if (!matched)
                {
                    ok = LightingAssert.IsTrue(false,
                        $"B84: bottom-band differential fuzz seed {seed}",
                        $"banded run diverged from full-height run: {detail}");
                }
            }

            if (ok) Debug.Log($"[PASS] B84: bottom-band differential fuzz — {seeds} seeds bit-identical");
            return ok;
        }

        /// <summary>
        /// B85: the bottom band's prove-red. With the derived floor raised past the buried lamp, the
        /// lamp lands in skipped rows — the emission-sync scan never stamps it and the seeding pass
        /// reads virtual air — so the differential MUST report a divergence. The honest derivation on
        /// the identical script must then pass. A differential that cannot fail the sabotage proves
        /// nothing.
        /// </summary>
        /// <returns>True when the sabotage is caught AND the honest bottom matches.</returns>
        private static bool Baseline_BandBottomDifferentialProveRed()
        {
            Func<LightingTestWorld, int> script = world =>
            {
                world.FillSuperflatFloor(47, TestBlockPalette.Stone);
                world.RecalculateHeightmaps();
                int rounds = world.RunInitialLighting();
                world.PlaceBlock(new Vector3Int(24, 20, 24), TestBlockPalette.LampWhite);
                return rounds + world.RunToConvergence();
            };

            // Raise the floor past the lamp: the honest bottom for the lamp job is
            // min(node 20 − 16, lamp-section floor 16, heightmap 47 − 16) = 4; +headroom+4 lifts it to
            // 24 > 20, so the lamp sits in skipped rows. Full-height jobs (bottom 0) pass through.
            Func<int, int> bottomSabotage = honest => honest <= 0
                ? honest
                : honest + LightingBandDecision.BandHeadroomVoxels + 4;

            bool sabotageCaught = !BandDifferentialMatches(script, out string sabotageDetail, out _,
                sabotage: null, bottomSabotage: bottomSabotage);
            bool ok = LightingAssert.IsTrue(sabotageCaught,
                "B85: raised band floor is CAUGHT by the differential (prove-red)",
                $"sabotaged bottom produced a bit-identical field — the differential has no teeth ({sabotageDetail})");

            bool honestMatches = BandDifferentialMatches(script, out string honestDetail);
            ok &= LightingAssert.IsTrue(honestMatches,
                "B85: honest bottom passes the identical script",
                $"honest derivation diverged: {honestDetail}");

            return ok;
        }
    }
}
