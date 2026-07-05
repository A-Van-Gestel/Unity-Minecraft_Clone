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
        // nightly menu) ALSO converges across all seeds once Bug 10 is fixed — the INITIAL-WAVE shadow
        // mechanism is not synchronously reproducible (in-range light paths reconcile within the 2 edge
        // rounds). The canopy fuzz's real catch was Bug 10 (K10a/K10b below). HOWEVER, the border-heightmap
        // fuzz (K15a below) reproduces the POST-EDIT form synchronously (found 2026-07-05 at its seed 14):
        // under-bright transparent border voxels persist after border edits with no pending work, because
        // both edge-check rounds were consumed during the generation wave — exactly one forced edge-check
        // round heals the field to the oracle (classifier-proven). Edge-round exhaustion after edits IS
        // the Bug-05 mechanism, now testable without in-build instrumentation.

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
            // Bug 09 still needs a faithful repro (see note above).

            // Bug 15 (found 2026-07-05 by the HF-3 border-heightmap fuzz, its first seed; fixed in code
            // 2026-07-05): a border-column edit's sunlight recalc permanently wiped the cross-chunk
            // surface stamp on opaque seam-face voxels — every cross-seam re-derivation path refused
            // opaque centers. K15b/K15c (distilled repros) flipped green with the fix; the fuzz's stamp
            // seeds (0/9/12/19) are green too. K15a's one remaining red (seed 14) is NOT the stamp bug:
            // it reproduces Bug 05's edge-round exhaustion (see the Bug 05 note above), so the fuzz is
            // registered under Bug 05 and promotes to baseline B62 when THAT mechanism is fixed.
            scenarios.Add(new Scenario(
                "K15a: Border-heightmap fuzz — varied heights at every seam, seam overhangs, and border edits settle on the oracle across randomized seeds (reproduces Bug 05 edge-round exhaustion)",
                KnownBug_BorderHeightFuzz,
                knownBugId: "Bug 05"));
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
