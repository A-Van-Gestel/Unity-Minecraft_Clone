using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

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
        // nightly menu) ALSO converges across all seeds once Bug 10 is fixed — the INITIAL-WAVE shadow
        // mechanism is not synchronously reproducible (in-range light paths reconcile within the 2 edge
        // rounds). The canopy fuzz's real catch was Bug 10 (K10a/K10b below). HOWEVER, the border-heightmap
        // fuzz (now baseline B64) reproduced the POST-EDIT form synchronously (found 2026-07-05 at seed 14):
        // under-bright transparent border voxels persisted after border edits with no pending work, because
        // both edge-check rounds were consumed during the generation wave — exactly one forced edge-check
        // round heals the field to the oracle (classifier-proven). FIXED July 2026: ChunkData.ModifyVoxel
        // re-grants a bounded edge-check round on a border-column opacity edit, so the post-edit
        // stabilization re-runs the reconciling border check (archived _FIXED_BUGS.md Lighting #20;
        // confirmed in-game — no dense-canopy border shadow patches).

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

        // NOTE on Bugs 13/14: both reproduced synchronously via the AS-1 slab repro and fixed July 2026
        // — Bug 13 (live-lock; extended Bug-11 veto with live third-party cross-chunk support) promoted
        // to baselines B56–B59, Bug 14 (stale pull-back ghost light; merge-time PullBackClaim
        // verification) promoted to baselines B60/B61. Entries archived in _FIXED_BUGS.md.

        // NOTE on Bug 15: found 2026-07-05 by the HF-3 border-heightmap fuzz's first seed (cross-chunk
        // surface stamps on opaque seam faces wiped by border-column edits — every cross-seam
        // re-derivation path refused opaque centers) and fixed the same day (opaque-center stamps in
        // CheckEdgeVoxel/RGB + claim-verified dimmer/zeroed-halo pull-back). In-game confirmed via the
        // F3/F7 stored-light views; distilled repros K15b/K15c promoted to baselines B62/B63
        // (Baselines/LightingValidationSuite.Baseline.Bug15Stamp.cs). Entry archived in _FIXED_BUGS.md.

        // NOTE on Bug 16 (found 2026-07-11): the runaway RGB blocklight removal loop OOM. Simple
        // shapes do NOT reproduce — a single clean break of an overlapping two-color cross-seam
        // blend converges in every execution model (sequential, wave-parallel, held stale flight,
        // rapid cycling, dry or underwater; those attempts are consolidated into baseline B86 as the
        // simple-form guard + fix tripwire). The runaway needs the NON-MONOTONE mixed-channel
        // plateau state built by INTERRUPTED reconciliation (under-budgeted waves + pre-edit held
        // snapshots + water attenuation), after which a single job's blocklight removal phase enters
        // a true infinite cycle (proven by the near-cap node dumps: identical (pos, oldRGB) triplets
        // repeating at a two-seam corner — per-channel re-zeroing sustained by the darkness-phase
        // CheckEdgeVoxelRGB pull-back re-lighting from never-zeroed lit halo cells). K16a below is
        // that recipe, deterministic (3/3 reproductions during bisection).

        static partial void AddKnownBugScenarios(List<Scenario> scenarios)
        {
            // Bug 09 still needs a faithful repro (see note above).

            scenarios.Add(new Scenario(
                "K16a: Interrupted underwater break/re-place cycling of a seam lamp must still converge to the oracle — no runaway RGB removal loop (Bug 16)",
                KnownBug_RgbSeamBlendWaterCyclingRunaway, "Bug 16"));
        }

        /// <summary>
        /// K16a (Bug 16): the deterministic runaway-removal recipe. The B86 world plus a water volume
        /// (opacity-2 attenuation builds mixed-channel plateaus), cycled three times through: hold the
        /// green chunk's job on a pre-edit snapshot → break the red lamp → run the red chunk's removal
        /// pass → water re-flows into the hole → complete the stale green flight → one under-budgeted
        /// wave → re-place the lamp → one more under-budgeted wave. The interrupted reconciliation
        /// leaves non-monotone mixed R/G plateau values at the seam corners; the final break then
        /// drives a blocklight removal phase into an infinite per-channel re-zero ↔ seam pull-back
        /// cycle inside a single job (Bug 16's OOM).
        /// <para>
        /// EXPECTED RED until Bug 16 is fixed — and only survivable because of the temporary
        /// <c>NeighborhoodLightingJob.MAX_BFS_NODES_PER_PASS</c> diagnostic cap, which aborts the
        /// runaway pass (console: "[LightingJob DIAG] Bug-16 BFS work cap exceeded") and leaves a
        /// corrupted field this scenario's oracle compare reports. WITHOUT that cap this scenario
        /// OOM-crashes the editor (observed twice, 2026-07-11) — do not remove the cap before the fix.
        /// </para>
        /// </summary>
        private static bool KnownBug_RgbSeamBlendWaterCyclingRunaway()
        {
            const int cycles = 3;
            Vector3Int redPos = new Vector3Int(BUG16_RED_LAMP_X, BUG16_LAMP_Y, BUG16_LAMP_Z);

            using LightingTestWorld world = BuildBug16RgbSeamBlendWorld(withWater: true);
            bool passed = SetUpBug16InitialBlend(world, "K16a");

            for (int i = 0; i < cycles; i++)
            {
                LightingTestWorld.LightingJobFlight greenFlight = world.BeginLightingJob(new Vector2Int(1, 1));
                world.BreakBlock(redPos);
                world.RunLightingJob(new Vector2Int(0, 1)); // removal pass against the held green snapshot
                world.PlaceBlock(redPos, TestBlockPalette.Water); // fluid re-flow into the hole mid-reconciliation
                world.CompleteLightingJob(greenFlight); // stale pre-break merge + deferred-mod drain
                world.RunWaveToConvergence(1); // deliberately under-budgeted: next edit lands mid-reconciliation
                world.PlaceBlock(redPos, TestBlockPalette.LampRed);
                world.RunWaveToConvergence(1);
            }

            world.BreakBlock(redPos);
            world.PlaceBlock(redPos, TestBlockPalette.Water);

            passed &= LightingAssert.Converged(world.RunWaveToConvergence(),
                "K16a: post-cycling reconciliation reaches a stable field without runaway removal work");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "K16a: the settled field matches the borderless oracle (red gone, green intact)");
            return passed;
        }
    }
}
