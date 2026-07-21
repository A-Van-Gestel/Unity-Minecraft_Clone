using System.Collections.Generic;
using Helpers;
using Scenario = Editor.Validation.Framework.Scenario;
using Result = Helpers.ChunkUnloadDecision.Result;

namespace Editor.Validation.ChunkUnload
{
    /// <summary>
    /// Baseline (regression) scenarios for the <see cref="ChunkUnloadDecision"/> suite (CP-5 + P-4 rec 3).
    /// Each pins one arm or precedence rule of <c>World.UnloadChunks</c>' deferral policy; B8 is the
    /// exhaustive 16-row truth table with hand-written expected arms (an independent specification, not a
    /// re-derived oracle). The stranding baselines (B4/B5/B7) are the now-testable witness for the §9.6
    /// deadlock guard; B3/B9 pin the P-4 rec 3 persist-and-unload arm that drains the pinned trail.
    /// <para>Note: the 4th fact is <b>in-range</b> strand. The "out-of-range neighbors do not strand" half
    /// of P-4 rec 3 lives in <c>World.UnloadChunks</c>' fact gathering (a distance test on each neighbor),
    /// not in this pure function, so it is proven by the in-game soak, not here.</para>
    /// </summary>
    public static partial class ChunkUnloadDecisionValidationSuite
    {
        static partial void AddBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("B1: In-range chunk is kept (never a candidate)", B1_KeepInRange));
            scenarios.Add(new Scenario("B2: Beyond + running job defers on the job", B2_DeferJobRunning));
            scenarios.Add(new Scenario("B3: Beyond + pending light (no job/strand) persists-and-unloads (P-4 rec 3)", B3_PersistLightPending));
            scenarios.Add(new Scenario("B4: Beyond + in-range strand (no job) defers on strand (§9.6)", B4_DeferWouldStrand));
            scenarios.Add(new Scenario("B5: Beyond + fully unpinned unloads (§9.6 fall-through)", B5_Unload));
            scenarios.Add(new Scenario("B6: Job pin precedes strand + light", B6_JobPrecedesRest));
            scenarios.Add(new Scenario("B7: In-range strand precedes light-persist (§9.6 protects the neighbor)", B7_StrandPrecedesLight));
            scenarios.Add(new Scenario("B8: Exhaustive 16-row truth table", B8_ExhaustiveTruthTable));
            scenarios.Add(new Scenario("B9: Light-pending arm is a persist-unload, never a defer", B9_LightPendingNeverDefers));
        }

        /// <summary>
        /// B1 — A chunk within the unload distance is kept regardless of any other flag; it is not an
        /// unload candidate this pass.
        /// <para><b>Prove-red:</b> drop the <c>if (!BeyondUnloadDistance) return KeepInRange</c> guard.</para>
        /// </summary>
        private static bool B1_KeepInRange()
        {
            bool passed = CheckArm("B1 in-range, unpinned", Result.KeepInRange, Eval(false, false, false, false));
            passed &= CheckArm("B1 in-range, all pins set", Result.KeepInRange, Eval(false, true, true, true));
            return passed;
        }

        /// <summary>
        /// B2 — A beyond-range chunk with a running generation/mesh/lighting job defers on the job.
        /// <para><b>Prove-red:</b> remove the <c>if (JobRunning) return DeferJobRunning</c> arm.</para>
        /// </summary>
        private static bool B2_DeferJobRunning() =>
            CheckArm("B2 beyond + job", Result.DeferJobRunning, Eval(true, true, false, false));

        /// <summary>
        /// B3 — A beyond-range chunk pinned only by its own pending lighting (no job, no in-range strand)
        /// is persisted-and-unloaded rather than deferred forever — the core P-4 rec 3 behavior that drains
        /// the pinned trail whose lighting can never complete.
        /// <para><b>Prove-red:</b> remove the <c>if (ProcessingLight) return UnloadPersistLightPending</c> arm
        /// (it falls through to Unload).</para>
        /// </summary>
        private static bool B3_PersistLightPending() =>
            CheckArm("B3 beyond + light, no strand", Result.UnloadPersistLightPending, Eval(true, false, true, false));

        /// <summary>
        /// B4 — A beyond-range chunk with no job and no pending light, but whose unload would strand an
        /// in-range neighbor, defers on the strand rule (§9.6). Inverting the strand term flips this to Unload.
        /// <para><b>Prove-red:</b> invert the strand test to <c>if (!WouldStrandInRangeNeighbor)</c> in Evaluate.</para>
        /// </summary>
        private static bool B4_DeferWouldStrand() =>
            CheckArm("B4 beyond + in-range strand", Result.DeferWouldStrand, Eval(true, false, false, true));

        /// <summary>
        /// B5 — A beyond-range chunk with no pin at all unloads.
        /// <para><b>Prove-red:</b> invert the strand test to <c>if (!WouldStrandInRangeNeighbor)</c> in Evaluate.</para>
        /// </summary>
        private static bool B5_Unload() =>
            CheckArm("B5 beyond + unpinned", Result.Unload, Eval(true, false, false, false));

        /// <summary>
        /// B6 — The job pin takes precedence over both the strand and lighting pins.
        /// <para><b>Prove-red:</b> move the <c>JobRunning</c> arm below the strand/light arms.</para>
        /// </summary>
        private static bool B6_JobPrecedesRest() =>
            CheckArm("B6 beyond + job + light + strand", Result.DeferJobRunning, Eval(true, true, true, true));

        /// <summary>
        /// B7 — With no job, the in-range strand pin takes precedence over the light-persist arm: a chunk an
        /// in-range neighbor genuinely needs must DEFER, not shed its lighting to persistence. This precedence
        /// is what keeps P-4 rec 3 from reintroducing the §9.6 deadlock.
        /// <para><b>Prove-red:</b> move the <c>WouldStrandInRangeNeighbor</c> arm below the <c>ProcessingLight</c> arm.</para>
        /// </summary>
        private static bool B7_StrandPrecedesLight() =>
            CheckArm("B7 beyond + light + in-range strand", Result.DeferWouldStrand, Eval(true, false, true, true));

        /// <summary>
        /// B8 — The complete 4-bool → arm mapping, one hand-written expected row per combination. Any
        /// change to the arm set or precedence reds a specific row here.
        /// <para><b>Prove-red:</b> any single-arm or precedence mutation reds its matching row(s).</para>
        /// </summary>
        private static bool B8_ExhaustiveTruthTable()
        {
            // (beyond, job, light, inRangeStrand) -> expected arm. Written by hand as the policy specification.
            (bool beyond, bool job, bool light, bool strand, Result expected)[] table =
            {
                (false, false, false, false, Result.KeepInRange),
                (false, false, false, true, Result.KeepInRange),
                (false, false, true, false, Result.KeepInRange),
                (false, false, true, true, Result.KeepInRange),
                (false, true, false, false, Result.KeepInRange),
                (false, true, false, true, Result.KeepInRange),
                (false, true, true, false, Result.KeepInRange),
                (false, true, true, true, Result.KeepInRange),
                (true, false, false, false, Result.Unload),
                (true, false, false, true, Result.DeferWouldStrand),
                (true, false, true, false, Result.UnloadPersistLightPending),
                (true, false, true, true, Result.DeferWouldStrand),
                (true, true, false, false, Result.DeferJobRunning),
                (true, true, false, true, Result.DeferJobRunning),
                (true, true, true, false, Result.DeferJobRunning),
                (true, true, true, true, Result.DeferJobRunning),
            };

            bool passed = true;
            foreach ((bool beyond, bool job, bool light, bool strand, Result expected) in table)
            {
                string label = $"B8 ({(beyond ? 1 : 0)}{(job ? 1 : 0)}{(light ? 1 : 0)}{(strand ? 1 : 0)})";
                passed &= CheckArm(label, expected, Eval(beyond, job, light, strand));
            }

            return passed;
        }

        /// <summary>
        /// B9 — Across every beyond-range, no-job, no-strand case, a light-pending chunk resolves to the
        /// persist-and-unload arm (never a Defer*). This is the anti-regression for P-4 rec 3's whole point:
        /// the pinned trail must drain, not accumulate.
        /// <para><b>Prove-red:</b> change the <c>ProcessingLight</c> arm to return any <c>Defer*</c> value.</para>
        /// </summary>
        private static bool B9_LightPendingNeverDefers()
        {
            Result r = Eval(true, false, true, false);
            bool passed = Check("B9 light-pending resolves to persist-unload", r == Result.UnloadPersistLightPending);
            passed &= Check("B9 light-pending is not a defer",
                r != Result.DeferJobRunning && r != Result.DeferLightPending && r != Result.DeferWouldStrand);
            return passed;
        }
    }
}
