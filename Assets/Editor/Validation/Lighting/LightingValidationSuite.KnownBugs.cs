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

        // NOTE on Bug 09: fourteen repro attempts total — two direct-harness (B15, B16), three
        // frame-simulator with complete-all (B17, B18, B19), three with multi-frame flight
        // lifetimes (B20, B21, B22), three with fluid-flow contention (B23, B24, B25), and three
        // with seeded iteration-order randomness (B26, B27, B28). All fourteen converge to the
        // oracle field across all tested orderings and seeds. Every production scheduling behavior
        // modelable in the synchronous harness has been exhausted. The bug is likely a genuine async
        // race condition (Burst job system timing, IL2CPP memory ordering) or has been fixed by the
        // Bug 07/08 cross-chunk mod delivery fixes. A faithful repro remains TODO.

        static partial void AddKnownBugScenarios(List<Scenario> scenarios)
        {
            // No open known-bug scenarios — Bug 05 and Bug 09 still need faithful repros (see notes above).
        }
    }
}
