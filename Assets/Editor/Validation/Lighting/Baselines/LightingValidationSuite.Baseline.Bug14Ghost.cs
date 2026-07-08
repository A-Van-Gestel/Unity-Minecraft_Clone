using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Baseline regression scenarios <b>B60/B61</b> guarding the <b>Bug 14</b> fix — stale-snapshot
    /// cross-chunk sunlight ghost light (fixed July 2026 by recording the darkness-wave seam pull-back
    /// as <see cref="Jobs.PullBackClaim"/>s and re-verifying them against live neighbor data at merge
    /// time; see <c>Documentation/Bugs/_FIXED_BUGS.md</c>). B61 was promoted from known-bug repro K14a
    /// after in-game confirmation on the fluid-stress opaque-floor run.
    /// <list type="bullet">
    /// <item><b>B60</b> — the claim data contract: the column-recalc shadow-caster path seeds darkness
    /// nodes cross-border, and pull-backs during such halo-node waves must not be recorded as claims
    /// (pre-guard this crashed <c>ProcessLightingJobs</c> in-game).</item>
    /// <item><b>B61</b> — the ghost repro itself: the pinned schedule under which the dynamic slab
    /// stamp settled ~57k voxels over-bright pre-fix must settle on the borderless oracle.</item>
    /// </list>
    /// Self-registered via the <see cref="AddBug14GhostBaselineScenarios"/> hook called from
    /// <c>AddBaselineScenarios</c> (the <c>Baselines/</c> group-partial pattern); shares the slab
    /// builders and stamp runner with the B56–B59 family.
    /// </summary>
    public static partial class LightingValidationSuite
    {
        // The sweep parameters of the first observed ghost case (seed 1 of the B59 space: budget =
        // 1 + seed % 4, cadence = seed % 3, shuffled). Pinned as constants so the repro cannot drift.
        private const int BUG14_SEED = 1;
        private const int BUG14_BUDGET = 2;
        private const int BUG14_STAMP_CADENCE = 1;

        /// <summary>
        /// Registers the Bug-14 fix baselines (B60 contract, B61 promoted repro; called from
        /// <c>AddBaselineScenarios</c>).
        /// </summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddBug14GhostBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B60: A border-column edit whose sunlight recalc seeds a cross-border shadow-caster darkness node settles on the oracle — halo pull-backs are not recorded as claims (Bug 14 fix contract)",
                Baseline_BorderShadowCasterHaloNode));
            scenarios.Add(new Scenario(
                "B61: The dynamically-stamped slab's settled field matches the borderless oracle under a shuffled/budgeted schedule (grid-3, seed 1) — no stale ghost light survives (Bug 14 guard)",
                Baseline_StaleGhostLightCleared));
        }

        /// <summary>
        /// B60: guards the <see cref="Jobs.PullBackClaim"/> center-only contract on the one production
        /// path that seeds darkness nodes OUTSIDE the center chunk — the column-recalc shadow-caster
        /// check (<c>RecalculateSunlightForColumn</c> wakes the highest block's horizontal neighbors,
        /// including cross-border ones). A pull-back during such a halo node's wave must NOT be recorded
        /// as a claim: pre-guard, the merge-time verifier indexed the chunk with the out-of-bounds
        /// position and the resulting exception aborted the whole <c>ProcessLightingJobs</c> pass
        /// (in-game: ObjectDisposedException spam from already-released jobs left in the dictionary).
        /// Geometry: a 1-wide overhang in the west chunk right at the seam leaves the voxel under it
        /// partially lit (lateral spill, sky 14) — the shadow-caster branch's precondition — then a
        /// border-column edit in the east chunk triggers the recalc whose shadow caster neighbors it.
        /// The cross-border mod emission is asserted as the liveness proof that the halo-node path
        /// actually ran. (The crash itself is not scenario-provable here: at this position the harness
        /// <c>ChunkData</c> tolerates an out-of-bounds read as a wrong-voxel read, which the verifier's
        /// superseded check then skips — verified by a deliberate both-guards-off sabotage run staying
        /// green. The guards themselves are the crash fix; this baseline pins the path's convergence.)
        /// </summary>
        private static bool Baseline_BorderShadowCasterHaloNode()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);

            // 1-wide overhang in chunk (0,1) touching the x15|16 seam; the voxel under it is lit
            // laterally to 14 — partially lit, exactly what the shadow-caster branch looks for.
            world.SetBlock(new Vector3Int(15, 50, 24), TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B60: initial lighting converges");

            byte underOverhang = world.GetSkyLight(new Vector3Int(15, 49, 24));
            passed &= LightingAssert.IsTrue(underOverhang > 0 && underOverhang < 15,
                "B60: the voxel under the seam overhang is partially lit (the shadow-caster branch's precondition)",
                $"Expected 0 < sky < 15 at (15,49,24), got {underOverhang}");

            // Border-column edit in the east chunk: the recalc's shadow-caster check seeds a darkness
            // node AT the cross-border neighbor (local x = -1); its wave's pull-back exercises the
            // halo-node path the claim contract excludes. The halo writes surface as cross-chunk mods —
            // asserting they exist proves the path fired (liveness), not just that nothing broke.
            world.PlaceBlock(new Vector3Int(16, 49, 24), TestBlockPalette.Stone);
            LightingTestWorld.LightingRunResult editResult = world.RunLightingJob(new Vector2Int(1, 1));
            passed &= LightingAssert.IsTrue(editResult.ModsEmitted > 0,
                "B60: the border shadow-caster wave emitted cross-chunk mods (the halo-node path fired)",
                $"Expected ModsEmitted > 0 from the edit job, got {editResult.ModsEmitted}");

            passed &= LightingAssert.Converged(world.RunToConvergence(), "B60: post-edit reconciliation converges");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B60: field matches the borderless oracle after the border shadow-caster edit");
            return passed;
        }

        /// <summary>
        /// B61 (promoted from known-bug repro K14a after the July 2026 Bug 14 fix + in-game
        /// confirmation): the deterministic seed-1 ghost case. Settles a slab-less grid-3 world, stamps
        /// the full-grid slab chunk-by-chunk under the pinned seed-1 schedule, and asserts the settled
        /// field matches the borderless oracle — pre-fix it settled ~57k voxels over-bright (worst +14
        /// sky), the stale pull-back ghost. Termination itself is guarded by B58/B59.
        /// </summary>
        private static bool Baseline_StaleGhostLightCleared()
        {
            using LightingTestWorld world = BuildBug13SlabWorld(BUG13_FULL_GRID,
                BUG13_FULL_SLAB_MIN, BUG13_FULL_SLAB_MAX, includeSlab: false);
            world.RunInitialLighting();

            LightingFrameSimulator sim = new LightingFrameSimulator(world, BUG14_SEED);
            return RunBug13StampAndSettle(world, sim, BUG13_FULL_SLAB_MIN, BUG13_FULL_SLAB_MAX,
                BUG14_BUDGET, LightingFrameSimulator.CompletionOrder.Shuffled, BUG14_STAMP_CADENCE,
                BUG13_SIM_MAX_FRAMES, "B61 (grid-3 slab stamp, seed 1)", logPass: true, assertOracle: true,
                expectedRed: false);
        }
    }
}
