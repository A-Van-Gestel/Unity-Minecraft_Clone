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

        static partial void AddKnownBugScenarios(List<Scenario> scenarios)
        {
            // Bug 05 and Bug 09 still need faithful repros (see notes above) — though Bug 15 below is a
            // strong candidate mechanism for Bug 05 (identical healing profile; dense-biome decoration
            // mods run through the same border-column edit path).

            // Bug 15 (found 2026-07-05 by the HF-3 border-heightmap fuzz, its first seed): a border-column
            // edit's sunlight recalc permanently wipes the cross-chunk surface stamp on opaque seam-face
            // voxels; no wake ever re-derives it. Promote to baseline B62 after the fix + in-game confirm.
            scenarios.Add(new Scenario(
                "K15a: Border-heightmap fuzz — varied heights at every seam, seam overhangs, and border edits settle on the oracle across randomized seeds (reproduces Bug 15)",
                KnownBug_BorderHeightFuzz,
                knownBugId: "Bug 15"));
            scenarios.Add(new Scenario(
                "K15b: A seam cliff face's cross-seam sunlight surface stamp survives a same-column border edit (reproduces Bug 15, distilled)",
                KnownBug_SeamFaceStampWipedByColumnRecalc,
                knownBugId: "Bug 15"));
            scenarios.Add(new Scenario(
                "K15c: A seam wall's blocklight surface stamp re-derives from a surviving cross-seam torch after a nearer source breaks (reproduces Bug 15, RGB path)",
                KnownBug_SeamWallBlocklightStampWipedByDarknessWave,
                knownBugId: "Bug 15"));
        }
    }
}
