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

        // NOTE on Bug 09: eleven repro attempts total — two direct-harness (B15, B16), three
        // frame-simulator with complete-all (B17, B18, B19), three with multi-frame flight
        // lifetimes (B20, B21, B22), and three with fluid-flow contention (B23, B24, B25).
        // All converge to the oracle field. The fluid scenarios model water flowing back into the
        // broken lamp position (opacity change + BFS injection) while the removal job is in-flight,
        // combined with held flights, budget pressure, and repeated break+fluid+place cycles.
        // The only remaining unmodeled production behavior is Dictionary iteration randomness
        // in ProcessLightingJobs — the simulator uses deterministic row-major ordering.
        // A faithful repro remains TODO.

        static partial void AddKnownBugScenarios(List<Scenario> scenarios)
        {
            // No open known-bug scenarios — Bug 05 and Bug 09 still need faithful repros (see notes above).
        }
    }
}
