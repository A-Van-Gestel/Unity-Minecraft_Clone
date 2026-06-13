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

        // NOTE on Bug 09: two repro attempts were made — single-cycle break+place with neighbor
        // in-flight (K09a) and double-cycle with both chunks in-flight (K09b). Both converge to
        // the oracle field — they now live as baselines B15 and B16, guarding the defer/drain
        // mechanism under these interleavings. Bug 09 likely requires production-only timing
        // (frame-budget throttling, fluid re-scheduling contention, or the LightingJobs.ContainsKey
        // guard preventing re-scheduling during rapid edits) that the harness cannot model;
        // a faithful repro remains TODO before that bug's fix can be test-driven.
        static partial void AddKnownBugScenarios(List<Scenario> scenarios)
        {
            // No open known-bug scenarios — Bug 05 and Bug 09 still need faithful repros (see notes above).
        }
    }
}
