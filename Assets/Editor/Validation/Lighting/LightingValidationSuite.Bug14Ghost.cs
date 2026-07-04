using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Known-bug scenario for <b>Bug 14</b> — stale-snapshot cross-chunk sunlight <b>ghost light</b>:
    /// under interleaved schedules, a chunk's job re-lights its field from a neighbor's schedule-time
    /// snapshot (the seam pull-back in <c>PropagateDarkness</c> plus cross-chunk uplift mods) after that
    /// neighbor has already darkened, and the resulting over-bright region settles with no removal
    /// initiator left to clear it — a stable-but-wrong field, the terminating sibling of Bug 13's
    /// live-lock (both were exposed by the same AS-1 slab repro; Bug 13's fix removed the
    /// non-termination exit, this defect owns the over-bright exit).
    /// <para>
    /// <b>K14a</b> replays the first failing sweep case found by K13d: the grid-3 full-grid slab stamped
    /// chunk-by-chunk under seed 1 (per-frame budget 2, one frame between stamps, shuffled completion).
    /// The pipeline terminates (guarded by K13d), but ~57k voxels settle over-bright vs the borderless
    /// oracle (worst +14 sky) — and the field is genuinely stable: sequential convergence afterwards
    /// finds no pending work. Expected red until Bug 14 is fixed.
    /// </para>
    /// </summary>
    public static partial class LightingValidationSuite
    {
        // The K13d sweep parameters of the first observed ghost case (seed 1): budget = 1 + seed % 4,
        // cadence = seed % 3, shuffled completion. Pinned as constants so the repro cannot drift.
        private const int BUG14_SEED = 1;
        private const int BUG14_BUDGET = 2;
        private const int BUG14_STAMP_CADENCE = 1;

        /// <summary>Registers the Bug-14 stale-ghost-light scenario.</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddBug14GhostKnownBugScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "K14a: The dynamically-stamped slab's settled field matches the borderless oracle under a shuffled/budgeted schedule (grid-3, seed 1) — no stale ghost light survives (Bug 14)",
                KnownBug_Bug14StaleGhostLight, "Bug 14"));
        }

        /// <summary>
        /// K14a: the deterministic seed-1 ghost case. Settles a slab-less grid-3 world, stamps the
        /// full-grid slab chunk-by-chunk under the pinned seed-1 schedule, and asserts the settled field
        /// matches the borderless oracle (termination itself is Bug 13's property, guarded by K13c/K13d).
        /// </summary>
        private static bool KnownBug_Bug14StaleGhostLight()
        {
            using LightingTestWorld world = BuildBug13SlabWorld(BUG13_FULL_GRID,
                BUG13_FULL_SLAB_MIN, BUG13_FULL_SLAB_MAX, includeSlab: false);
            world.RunInitialLighting();

            LightingFrameSimulator sim = new LightingFrameSimulator(world, BUG14_SEED);
            return RunBug13StampAndSettle(world, sim, BUG13_FULL_SLAB_MIN, BUG13_FULL_SLAB_MAX,
                BUG14_BUDGET, LightingFrameSimulator.CompletionOrder.Shuffled, BUG14_STAMP_CADENCE,
                BUG13_SIM_MAX_FRAMES, "K14a (grid-3 slab stamp, seed 1)", logPass: true, assertOracle: true);
        }
    }
}
