using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Editor.Validation.Meshing.Framework;
using Helpers;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Meshing
{
    /// <summary>
    /// Completion-pass baselines (MP-4 — see
    /// Documentation/Design/MESHING_PIPELINE_ORCHESTRATION_REFACTOR.md §MP-4 / §4.2). The mesh pass's
    /// HF-2 fault isolation and release-inside / remove-after ordering used to be hand-written inside
    /// <c>WorldJobManager.ProcessMeshJobs</c>, so no scenario could replay it (finding <b>F5</b> — the mesh-side
    /// analog of lighting fidelity <b>B7</b>, which took an in-game <c>ObjectDisposedException</c> cascade to
    /// discover). MP-4 routed that pass through the shared <see cref="JobCompletionPass"/> skeleton; this
    /// baseline replays the skeleton directly with a recording fake driver.
    /// <list type="bullet">
    /// <item><b>B27</b> — skeleton-order replay: multi-job fault isolation (stage-1 carries over without
    /// releasing, stage-2 still releases + enrolls), remove-strictly-after-merge ordering, the P-4 window
    /// break, and the rotating <c>startIndex</c> visit order.</item>
    /// </list>
    /// Pure — no <see cref="MeshingTestWorld"/>, no world coupling: the driver is a fake, so this pins the
    /// SKELETON's contract. It does not (and cannot, world-free) prove that <c>ProcessMeshJobs</c> calls the
    /// skeleton; the build, the in-game smoke, and the MP-1 <c>MeshMergeAttempts</c> counter cover that.
    /// Because the skeleton is shared, this doubles as a post-rename regression pin for the LIGHTING pass —
    /// a mutation here reds both B27 and the lighting suite's B65.
    /// </summary>
    public static partial class MeshingValidationSuite
    {
        // Leg 2's time ceiling. Sized for margin, not realism: the pass must survive an arming-to-first-check
        // stall in a managed editor process (GC, preemption) without breaking before candidate 1.
        private const float WINDOW_BUDGET_MS = 50f;

        /// <summary>Registers the MP-4 completion-pass baselines (called from <c>Execute</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddCompletionScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("B27: JobCompletionPass replays the mesh pass skeleton — stage-1/stage-2 fault isolation, release-inside/remove-after, window break, rotating start (MP-4)", B27_CompletionPassOrder));
        }

        /// <summary>
        /// B27 — drives the production <see cref="JobCompletionPass"/> with a recording fake driver across four
        /// legs. Prove-red: move <c>driver.ReleaseJob</c> out of the merge <c>finally</c> in
        /// <see cref="JobCompletionPass.RunMergeLoop{TKey}"/> → leg 1 reds (the stage-2-faulted job never
        /// releases its buffers — the fidelity-B7 stranded-container cascade). The lighting suite's B65 reds
        /// with it; that shared coupling is the point.
        /// </summary>
        private static bool B27_CompletionPassOrder()
        {
            bool ok = true;

            // Leg 1 — fault isolation + ordering over four candidates: 1 clean, 2 not complete (carried over),
            // 3 stage-1 fault, 4 stage-2 fault.
            {
                RecordingCompletionDriver driver = new RecordingCompletionDriver(
                    incomplete: new[] { 2 }, stage1FaultKey: 3, stage2FaultKey: 4);
                List<int> enrolled = new List<int>();

                JobCompletionPass.RunMergeLoop(new[] { 1, 2, 3, 4 }, driver, enrolled);
                JobCompletionPass.RunRemoveAndPromote(enrolled, driver);

                // The exact contract, stated independently of the implementation:
                //  - a not-complete candidate is only probed (carried over, nothing released, not enrolled);
                //  - a stage-1 fault does NOT release (the job may still own its buffers) and is NOT enrolled;
                //  - a stage-2 fault still releases and IS enrolled (so it can never strand disposed containers);
                //  - every RemoveAndPromote runs strictly AFTER the whole merge loop.
                const string expected =
                    "IsComplete(1) Complete(1) Merge(1) Release(1) " +
                    "IsComplete(2) " +
                    "IsComplete(3) Complete(3) CompleteFault(3) " +
                    "IsComplete(4) Complete(4) Merge(4) MergeFault(4) Release(4) " +
                    "Remove(1) Remove(4)";
                string actual = driver.OpLog;

                ok &= MeshAssert.IsTrue(
                    "B27.1 skeleton op order (fault isolation + release-inside / remove-after)",
                    actual == expected,
                    actual == expected
                        ? $"op log matches the contract: {actual}"
                        : $"op log diverged from the contract.\n      expected: {expected}\n      actual:   {actual}");

                ok &= MeshAssert.IsTrue(
                    "B27.2 enrollment = merged-or-stage-2-faulted only",
                    enrolled.Count == 2 && enrolled[0] == 1 && enrolled[1] == 4,
                    enrolled.Count == 2 && enrolled[0] == 1 && enrolled[1] == 4
                        ? "enrolled [1, 4] — the incomplete (2) and stage-1-faulted (3) jobs stay in the registry"
                        : $"enrolled [{string.Join(", ", enrolled)}], expected [1, 4]");
            }

            // Leg 2 — P-4 window break: the ceiling is consumed inside job 1's merge, so the pass must break
            // BETWEEN jobs and leave the remainder untouched (not enrolled ⇒ not removed ⇒ retried next frame).
            // The budget is deliberately generous (WINDOW_BUDGET_MS): the skeleton also tests window.Expired
            // before the FIRST candidate, so an arming-to-first-check stall longer than the budget (a GC pause
            // or thread preemption in the managed editor process) would break the pass with nothing enrolled and
            // red this baseline spuriously. The driver spins to the deadline, so a wide budget costs one
            // bounded busy-wait and buys a large safety margin — it does not weaken the assertion.
            {
                long budgetTicks = PipelinePassBudget.TicksForMs(WINDOW_BUDGET_MS);
                long start = Stopwatch.GetTimestamp();
                PipelinePassBudget.Window window = new PipelinePassBudget.Window(start, budgetTicks);

                // Deterministic (no sleep, no flake): spin until the budget is provably spent.
                RecordingCompletionDriver driver = new RecordingCompletionDriver(
                    spinOnKey: 1, spinUntilTimestamp: start + budgetTicks);
                List<int> enrolled = new List<int>();

                JobCompletionPass.RunMergeLoop(new[] { 1, 2, 3 }, driver, enrolled, window);
                JobCompletionPass.RunRemoveAndPromote(enrolled, driver);

                // Separate the precondition from the conclusion so a pre-emptive break (job 1 never reached)
                // reports as its own explicit failure rather than masquerading as "the break logic is wrong".
                bool reachedFirst = driver.OpLog.Contains("Merge(1)");
                bool remainderUntouched = !driver.OpLog.Contains("IsComplete(2)")
                                          && !driver.OpLog.Contains("IsComplete(3)");
                bool brokeAfterFirst = reachedFirst && remainderUntouched
                                                    && enrolled.Count == 1 && enrolled[0] == 1;

                ok &= MeshAssert.IsTrue(
                    "B27.3 expired window breaks between jobs, remainder stays enrolled in the registry",
                    brokeAfterFirst,
                    brokeAfterFirst
                        ? "job 1 completed then the window expired; jobs 2/3 were never probed and never removed"
                        : !reachedFirst
                            ? $"job 1 was never merged — the window expired before the first candidate, so this run " +
                              $"proves nothing about the break (raise WINDOW_BUDGET_MS if this recurs). ops: {driver.OpLog}"
                            : $"expected only job 1 processed; enrolled [{string.Join(", ", enrolled)}], ops: {driver.OpLog}");
            }

            // Leg 3 — rotating start (P-4 §3.4 fairness): startIndex 2 over 4 candidates visits 12,13,10,11.
            {
                RecordingCompletionDriver driver = new RecordingCompletionDriver();
                List<int> enrolled = new List<int>();

                JobCompletionPass.RunMergeLoop(new[] { 10, 11, 12, 13 }, driver, enrolled, default, 2);

                bool rotated = enrolled.Count == 4 && enrolled[0] == 12 && enrolled[1] == 13
                               && enrolled[2] == 10 && enrolled[3] == 11;
                ok &= MeshAssert.IsTrue(
                    "B27.4 startIndex rotates the visit order and wraps",
                    rotated,
                    rotated
                        ? "visited 12, 13, 10, 11 — every candidate served exactly once from the rotated start"
                        : $"expected [12, 13, 10, 11], got [{string.Join(", ", enrolled)}]");
            }

            // Leg 4 — empty candidate set with a non-zero start index must not divide by zero.
            {
                RecordingCompletionDriver driver = new RecordingCompletionDriver();
                List<int> enrolled = new List<int> { 99 }; // must be cleared even on the empty path
                bool threw = false;
                try
                {
                    JobCompletionPass.RunMergeLoop(Array.Empty<int>(), driver, enrolled, default, 5);
                }
                catch (Exception)
                {
                    threw = true;
                }

                ok &= MeshAssert.IsTrue(
                    "B27.5 empty candidate list with a stale cursor is a safe no-op",
                    !threw && enrolled.Count == 0 && driver.OpLog.Length == 0,
                    !threw && enrolled.Count == 0 && driver.OpLog.Length == 0
                        ? "no exception, enrolled cleared, driver untouched"
                        : $"threw={threw}, enrolled={enrolled.Count}, ops: {driver.OpLog}");
            }

            return ok;
        }

        /// <summary>
        /// A fake <see cref="IJobCompletionDriver{TKey}"/> that records every hook the skeleton invokes, in
        /// order, and can be scripted to report a candidate incomplete, throw from stage 1
        /// (<see cref="CompleteJob"/>), throw from stage 2 (<see cref="MergeJob"/>), or burn a time budget
        /// inside a merge. Keyed by <c>int</c> — the skeleton is generic over the key, so the baseline does not
        /// need <c>ChunkCoord</c> or any world state.
        /// </summary>
        private sealed class RecordingCompletionDriver : IJobCompletionDriver<int>
        {
            private readonly StringBuilder _ops = new StringBuilder();
            private readonly HashSet<int> _incomplete = new HashSet<int>();
            private readonly int _stage1FaultKey;
            private readonly int _stage2FaultKey;
            private readonly int _spinOnKey;
            private readonly long _spinUntilTimestamp;

            /// <summary>Initializes a scripted recording driver.</summary>
            /// <param name="incomplete">Keys whose <see cref="IsComplete"/> reports false (carried over).</param>
            /// <param name="stage1FaultKey">Key whose <see cref="CompleteJob"/> throws (-1 = none).</param>
            /// <param name="stage2FaultKey">Key whose <see cref="MergeJob"/> throws (-1 = none).</param>
            /// <param name="spinOnKey">Key whose merge busy-waits to consume a time budget (-1 = none).</param>
            /// <param name="spinUntilTimestamp">The <see cref="Stopwatch"/> timestamp to spin until.</param>
            public RecordingCompletionDriver(
                IEnumerable<int> incomplete = null,
                int stage1FaultKey = -1,
                int stage2FaultKey = -1,
                int spinOnKey = -1,
                long spinUntilTimestamp = 0)
            {
                if (incomplete != null)
                    foreach (int key in incomplete)
                        _incomplete.Add(key);

                _stage1FaultKey = stage1FaultKey;
                _stage2FaultKey = stage2FaultKey;
                _spinOnKey = spinOnKey;
                _spinUntilTimestamp = spinUntilTimestamp;
            }

            /// <summary>The space-separated hook log, in invocation order.</summary>
            public string OpLog => _ops.ToString().TrimEnd();

            /// <inheritdoc />
            public bool IsComplete(int key)
            {
                Record("IsComplete", key);
                return !_incomplete.Contains(key);
            }

            /// <inheritdoc />
            public void CompleteJob(int key)
            {
                Record("Complete", key);
                if (key == _stage1FaultKey) throw new InvalidOperationException($"stage-1 fault on {key}");
            }

            /// <inheritdoc />
            public void OnCompleteFault(int key, Exception e) => Record("CompleteFault", key);

            /// <inheritdoc />
            public void MergeJob(int key)
            {
                Record("Merge", key);

                if (key == _spinOnKey)
                    while (Stopwatch.GetTimestamp() < _spinUntilTimestamp)
                    {
                    }

                if (key == _stage2FaultKey) throw new InvalidOperationException($"stage-2 fault on {key}");
            }

            /// <inheritdoc />
            public void OnMergeFault(int key, Exception e) => Record("MergeFault", key);

            /// <inheritdoc />
            public void ReleaseJob(int key) => Record("Release", key);

            /// <inheritdoc />
            public void RemoveAndPromote(int key) => Record("Remove", key);

            private void Record(string op, int key) => _ops.Append(op).Append('(').Append(key).Append(") ");
        }
    }
}
