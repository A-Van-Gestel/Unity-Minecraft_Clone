using System.Collections.Generic;

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
        // NOTE on Bug 05: a minimal repro was attempted (5×5 grid, full slab with a single diagonal
        // sky well, wave-parallel initial lighting via RunInitialLightingParallel) but the engine
        // converges to the oracle field — it now lives as baseline B8. Bug 05 likely requires
        // denser multi-pocket canopy patterns or mod-loss timing the minimal case doesn't hit;
        // a faithful repro remains TODO before that bug's fix can be test-driven.

        // NOTE on Bug 09: eight repro attempts total — two direct-harness (B15, B16), three
        // frame-simulator with complete-all (B17, B18, B19), and three frame-simulator with
        // multi-frame flight lifetimes (B20, B21, B22). All converge to the oracle field.
        // The multi-frame scenarios model held flights via completion predicates: chunk A's
        // removal job stays in-flight across 2–3 frames while chunk B snapshots stale pre-removal
        // light, potentially stabilizing before chunk A's re-emission is even scheduled.
        // Bug 09 likely requires either fluid-flow contention (continuous voxel edits from water
        // re-filling the broken position between frames) or Dictionary iteration randomness in
        // ProcessLightingJobs that the simulator's deterministic ordering cannot reproduce.
        // A faithful repro remains TODO.

        static partial void AddKnownBugScenarios(List<Scenario> scenarios)
        {
            // No open known-bug scenarios — Bug 05 and Bug 09 still need faithful repros (see notes above).
        }
    }
}
