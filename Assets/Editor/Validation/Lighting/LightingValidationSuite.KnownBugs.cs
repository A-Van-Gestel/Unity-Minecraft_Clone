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
        // NOTE on Bug 05: a minimal repro (single diagonal sky well, baseline B8) converges, and the
        // procedural dense-canopy geometry fuzz (LightingValidationSuite.Bug05Canopy.cs, baseline B42 +
        // nightly menu) ALSO converges across all seeds once Bug 10 is fixed — so the Bug-05 shadow
        // mechanism is not synchronously reproducible (in-range light paths reconcile within the 2 edge
        // rounds). The canopy fuzz's real catch was Bug 10 (K10a/K10b below). A faithful Bug-05 repro
        // remains TODO and likely needs in-build instrumentation, not another synchronous layer (cf. B3).

        // NOTE on Bug 09: fifteen repro attempts total — two direct-harness (B15, B16), three
        // frame-simulator with complete-all (B17, B18, B19), three with multi-frame flight
        // lifetimes (B20, B21, B22), three with fluid-flow contention (B23, B24, B25), three
        // with seeded iteration-order randomness (B26, B27, B28), and one combined stress test
        // (B29) layering ALL harness capabilities simultaneously. All fifteen converge to the
        // oracle across all tested seeds and orderings. Every production scheduling behavior
        // modelable in the synchronous harness has been exhausted — the bug is either a genuine
        // async race condition (Burst job system timing, IL2CPP memory ordering) that synchronous
        // .Run() cannot reproduce, or is no longer present in the current codebase.

        static partial void AddKnownBugScenarios(List<Scenario> scenarios)
        {
            // No open known-bug scenarios. Bug 05 and Bug 09 still need faithful repros (see notes above);
            // Bug 10 was fixed and its repros promoted to baselines B43/B44.
        }
    }
}
