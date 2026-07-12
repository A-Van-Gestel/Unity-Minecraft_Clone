using System.Collections.Generic;
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

        // NOTE on Bug 16 (found + FIXED 2026-07-11, in-game confirmed same day, archived in
        // _FIXED_BUGS.md): the runaway RGB blocklight removal loop OOM. Root cause: removal nodes
        // carried non-removed channels + the darkness-phase CheckEdgeVoxelRGB pull-back restored
        // border cells to constants → infinite per-channel removal cycle inside one job. Fixed by
        // masking re-enqueued removal nodes to the channels actually zeroed; the BFS work cap became
        // a permanent fail-safe. Repro K16a promoted to baseline B87; B86 guards the simple form.
        //
        // NOTE on Bug 17 (found 2026-07-11 as Bug 16's post-fix residue; FIXED July 2026, oracle-only
        // confirmed, archived in _FIXED_BUGS.md): the same interrupted-cycling recipe left a small
        // SOURCELESS over-bright red island straddling the z31|32 seam — a stale in-flight job re-instated
        // pre-break red (merge + cross-chunk uplift) that ordinary re-spread then fanned out, and nothing
        // removed it because RGB blocklight had no removal veto. Attribution (instrumented) pinned the
        // planter to the stale in-flight job, NOT the darkness pull-back. Fixed by mirroring the sky
        // Bug 11/13 independent-support veto to RGB in CrossChunkLightModApplier.ComputeBlocklight
        // (per-channel): it breaks the stale-snapshot cross-seam removal oscillation so removals complete
        // and the field converges to the oracle. Repro K17a promoted to baseline B88.

        static partial void AddKnownBugScenarios(List<Scenario> scenarios)
        {
            // Bug 09 still needs a faithful repro (see note above).

            // Fidelity finding C10 (predicted latent bug — Bug 12's RGB twin): the RGB sourceless cross-seam
            // loop probe. Registered test-first as a known-bug scenario until its verdict is known — RED
            // confirms the predicted bug (document + fix); GREEN means the Bug 17 veto already collapses the
            // loop and it promotes to a baseline. Lives in LightingValidationSuite.C10RgbLoop.cs.
            AddC10RgbLoopScenarios(scenarios);
        }

        /// <summary>Hook for the C10 RGB sourceless-loop probe (implemented in LightingValidationSuite.C10RgbLoop.cs).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddC10RgbLoopScenarios(List<Scenario> scenarios);
    }
}
