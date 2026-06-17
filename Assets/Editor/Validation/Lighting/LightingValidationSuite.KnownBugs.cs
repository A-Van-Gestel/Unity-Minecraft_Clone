using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;
using Jobs.BurstData;
using UnityEngine;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Known-bug reproduction scenarios — the test-first encodings of the open bugs in
    /// <c>Documentation/Bugs/LIGHTING_BUGS.md</c>. Each scenario asserts the CORRECT behavior, so it
    /// is EXPECTED TO FAIL until its bug is fixed; the suite runner reports these as warnings, not
    /// regressions. When one starts passing, fix-confirm in-game and archive the bug entry.
    /// </summary>
    public static partial class LightingValidationSuite
    {
        // NOTE on Bug 05: a minimal repro (single diagonal sky well, baseline B8) converges, and the
        // procedural dense-canopy geometry fuzz (LightingValidationSuite.Bug05Canopy.cs, baseline B42 +
        // nightly menu) ALSO converges across all seeds once Bug 10 is fixed — so the Bug-05 shadow
        // mechanism is not synchronously reproducible (in-range light paths reconcile within the 2 edge
        // rounds). The canopy fuzz's real catch was Bug 10 (K10a/K10b below). A faithful Bug-05 repro
        // remains TODO and likely needs in-build instrumentation, not another synchronous layer (cf. B3).

        // NOTE on Bug 09: fifteen synchronous repro attempts exhausted every production scheduling behavior
        // the harness can model (direct-harness single/both-in-flight, frame-simulator ContainsKey/budget/
        // reverse order, multi-frame held flights, fluid-flow contention, seeded iteration-order randomness,
        // and a combined stress test) — all converged to the oracle across every tested seed and ordering.
        // Consolidated 2026-06-14 (see LIGHTING_VALIDATION_HARNESS_FIDELITY.md §5): the deterministic
        // single-instance scenarios folded into two representatives — B15 (direct-harness break+place,
        // single- then both-in-flight) and B16 (fluid break→water→place under a held flight + budget) —
        // backed by B22 (dual-chunk both-in-flight), B26–B29 (50-seed sweeps), and B40 (geometry fuzz).
        // The conclusion stands: the bug is either a genuine async race (Burst job timing, IL2CPP memory
        // ordering) that synchronous .Run() cannot reproduce, or is no longer present in the codebase.

        static partial void AddKnownBugScenarios(List<Scenario> scenarios)
        {
            // Bug 12: unlike Bug 05 / Bug 09, this DOES reproduce deterministically in the synchronous
            // harness (the over-bright "inverse" of Bug 05's shadow patches — a stable-but-wrong field).
            scenarios.Add(new Scenario(
                "K12a: Over-bright cross-seam sunlight loop survives source removal",
                KnownBug_CrossSeamSunlightLoopSurvivesSourceRemoval, "Bug 12"));
        }

        // --- K12a geometry (Bug 12): a roofed E-W corridor straddling the x15|16 chunk seam, lit ONLY by
        // a sky shaft that opens BOTH shared seam columns to the sky. The two seam voxels are therefore
        // mutually equal (each sky 15) — the precondition for a sourceless cross-seam support loop. ---
        private const int K12_SLAB_MIN_Y = 58; // solid rock floor/roof slab, low bound
        private const int K12_SLAB_MAX_Y = 68; // solid rock floor/roof slab, high bound
        private const int K12_CORRIDOR_Y = 63; // the carved 1-wide corridor (inside the slab)
        private const int K12_CORRIDOR_Z = 24; // corridor runs along x at this z (chunk row cz=1)
        private const int K12_CORRIDOR_MIN_X = 10; // corridor spans 6 voxels each side of the seam
        private const int K12_CORRIDOR_MAX_X = 21;
        private const int K12_WEST_SEAM_X = 15; // shared border column owned by chunk (0,1)
        private const int K12_EAST_SEAM_X = 16; // shared border column owned by chunk (1,1)

        /// <summary>
        /// K12a (Bug 12): reproduces the over-bright cross-seam sunlight loop that survives removal of the
        /// source that fed it. Asserts the CORRECT behavior (the corridor must darken once its only sky
        /// source is roofed, matching the borderless oracle), so it is EXPECTED TO FAIL until Bug 12 is
        /// fixed and flips green with zero test edits.
        /// <para>
        /// <b>Geometry.</b> A 1-wide roofed corridor straddles the x15|16 chunk seam, lit by a single sky
        /// shaft that opens <b>both</b> shared seam columns (x15 in chunk (0,1), x16 in chunk (1,1)) to the
        /// sky. After initial convergence the two seam voxels are mutually equal at sky 15 — voxel A (west
        /// seam) and voxel B (east seam) each appear lit "by" the other across the boundary, the precondition
        /// the bug entry describes.
        /// </para>
        /// <para>
        /// <b>Perturbation.</b> Both seam columns are roofed (one <see cref="LightingTestWorld.PlaceBlock"/>
        /// per chunk), removing the dominant cross-seam source. Each edit lands in a different chunk, so both
        /// seam chunks carry a sunlight column recalc in the <b>same</b> wave; the grid is then run
        /// <b>wave-parallel</b> (<see cref="LightingTestWorld.RunWaveToConvergence"/>) so every job snapshots
        /// the same pre-round state — the faithful mirror of production's concurrent multi-job frame, where
        /// cross-chunk mods are computed against schedule-time snapshots.
        /// </para>
        /// <para>
        /// <b>Bug 12.</b> With no genuine source left, neither seam voxel finds a removal initiator: each
        /// reads the other's still-high stale snapshot as legitimate support and re-places the light it just
        /// removed, settling into an over-bright fixed point (seam pinned one level below its pre-roof value,
        /// decaying downstream) instead of going dark. The field is <b>stable</b> ("converged") but does NOT
        /// match the oracle — the static inverse of Bug 05's shadow patches, distinct from Bug 11's
        /// oscillation. The final oracle compare is done quietly (no <c>LogError</c>) because the failure is
        /// expected; the runner announces the reproduction as a warning.
        /// </para>
        /// </summary>
        /// <returns>True only once Bug 12 is fixed (corridor darkens to the oracle); false while it reproduces.</returns>
        private static bool KnownBug_CrossSeamSunlightLoopSurvivesSourceRemoval()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            int width = world.GridSize * VoxelData.ChunkWidth;

            // Solid rock slab; carve a 1-wide E-W corridor straddling the seam, and a sky shaft that opens
            // BOTH shared seam columns to the sky (the only light source).
            world.FillBox(new Vector3Int(0, K12_SLAB_MIN_Y, 0), new Vector3Int(width - 1, K12_SLAB_MAX_Y, width - 1),
                TestBlockPalette.Stone);
            for (int x = K12_CORRIDOR_MIN_X; x <= K12_CORRIDOR_MAX_X; x++)
                world.SetBlock(new Vector3Int(x, K12_CORRIDOR_Y, K12_CORRIDOR_Z), TestBlockPalette.Air);
            foreach (int seamX in new[] { K12_WEST_SEAM_X, K12_EAST_SEAM_X })
                for (int y = K12_CORRIDOR_Y; y < VoxelData.ChunkHeight; y++)
                    world.SetBlock(new Vector3Int(seamX, y, K12_CORRIDOR_Z), TestBlockPalette.Air);

            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "K12a: initial lighting converges");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "K12a: initial field matches the borderless oracle");

            // Precondition: the two seam voxels are mutually equal (sky 15 each, fed from the shared shaft) —
            // each appears lit "by" the other across the boundary, the basis of the sourceless support loop.
            Vector3Int westSeam = new Vector3Int(K12_WEST_SEAM_X, K12_CORRIDOR_Y, K12_CORRIDOR_Z);
            Vector3Int eastSeam = new Vector3Int(K12_EAST_SEAM_X, K12_CORRIDOR_Y, K12_CORRIDOR_Z);
            passed &= LightingAssert.IsTrue(world.GetSkyLight(westSeam) == 15 && world.GetSkyLight(eastSeam) == 15,
                "K12a: both seam voxels are mutually lit to sky 15 before the source is removed",
                $"Expected 15/15, got {world.GetSkyLight(westSeam)}/{world.GetSkyLight(eastSeam)}");

            // Remove the dominant cross-seam source: roof BOTH seam columns. One edit per chunk, so both
            // seam chunks carry a sunlight column recalc into the SAME wave.
            world.PlaceBlock(new Vector3Int(K12_WEST_SEAM_X, K12_CORRIDOR_Y + 1, K12_CORRIDOR_Z), TestBlockPalette.Stone);
            world.PlaceBlock(new Vector3Int(K12_EAST_SEAM_X, K12_CORRIDOR_Y + 1, K12_CORRIDOR_Z), TestBlockPalette.Stone);

            // Wave-parallel reconciliation (production's concurrent multi-job frame). It converges to a
            // STABLE field — Bug 12 is the static defect, not Bug 11's oscillation — but the wrong one.
            passed &= LightingAssert.Converged(world.RunWaveToConvergence(),
                "K12a: post-removal reconciliation reaches a stable field");

            // CORRECT behavior (the spec): with the only source roofed, the corridor must go dark and match
            // the oracle. Bug 12 leaves it over-bright. Compared quietly: this failure is EXPECTED until the
            // bug is fixed, so it must not log a [FAIL]/LogError (those are reserved for baseline regressions).
            OracleLightField oracle = LightingOracle.Solve(world);
            bool matchesOracle = QuietMatchesOracle(world, oracle, out string overBrightSummary);
            if (matchesOracle)
                Debug.Log("[PASS] K12a: corridor darkens to the oracle after the cross-seam source is removed (Bug 12 fixed)");
            else
                Debug.Log($"K12a: cross-seam sunlight loop survives source removal — over-bright field is stable but wrong " +
                          $"(seam x15={world.GetSkyLight(westSeam)}, x16={world.GetSkyLight(eastSeam)}, oracle 0; {overBrightSummary}).");
            passed &= matchesOracle;

            return passed;
        }

        /// <summary>
        /// Full-volume oracle comparison that returns the match result WITHOUT logging a failure — used by
        /// known-bug scenarios whose mismatch is the EXPECTED reproduction (a <c>LogError</c> there would be
        /// indistinguishable from a baseline regression). Builds a short, console-readable summary of the
        /// over-bright extent for the warning log.
        /// </summary>
        /// <param name="world">The test world after convergence.</param>
        /// <param name="oracle">The borderless oracle field for the same voxel contents.</param>
        /// <param name="summary">A short description of the largest discrepancy (mismatch count + worst voxel).</param>
        /// <returns>True when the field matches the oracle exactly.</returns>
        private static bool QuietMatchesOracle(LightingTestWorld world, OracleLightField oracle, out string summary)
        {
            int width = world.GridSize * VoxelData.ChunkWidth;
            int mismatches = 0;
            int worstSkyDelta = 0;
            Vector3Int worstPos = default;

            for (int y = 0; y < VoxelData.ChunkHeight; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    for (int z = 0; z < width; z++)
                    {
                        Vector3Int pos = new Vector3Int(x, y, z);
                        ushort actual = world.GetLightData(pos);
                        ushort expected = oracle.GetLightData(pos);
                        if (actual == expected) continue;

                        mismatches++;
                        int skyDelta = LightBitMapping.GetSkyLight(actual) - LightBitMapping.GetSkyLight(expected);
                        if (skyDelta > worstSkyDelta)
                        {
                            worstSkyDelta = skyDelta;
                            worstPos = pos;
                        }
                    }
                }
            }

            summary = mismatches == 0
                ? "no mismatches"
                : $"{mismatches} voxel(s) differ; worst +{worstSkyDelta} sky at {worstPos}";
            return mismatches == 0;
        }
    }
}
