using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Data;
using Editor.Validation.Meshing.Framework;
using Helpers;
using Jobs.Data;
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
    /// <item><b>B31–B33</b> (MP-6 §8.1) — the production <see cref="MeshCompletionDriver"/> itself, driven
    /// through the real skeleton behind a recording <see cref="IMeshCompletionHost"/>: the MP-6 apply →
    /// load-animation mapping (B31), the merge-fault path (B32), and the <c>_curJob</c> scratch lifecycle
    /// (B33). B27 pins the skeleton; these pin what the driver does inside it.</item>
    /// </list>
    /// Pure — no <see cref="MeshingTestWorld"/>, no world coupling: the collaborators are faked, so this pins
    /// the SKELETON's and the DRIVER's contracts. It does not (and cannot, world-free) prove that
    /// <c>ProcessMeshJobs</c> calls the skeleton, nor that the animation actually plays on a real
    /// <c>GameObject</c> (the one-shot latch lives on <c>Chunk</c>, which the runner's <c>World.Instance</c>
    /// isolation guard forbids standing up); the build, the in-game smoke, and the MP-1
    /// <c>MeshMergeAttempts</c> counter cover that. Because the skeleton is shared, B27 doubles as a
    /// post-rename regression pin for the LIGHTING pass — a mutation there reds both B27 and the lighting
    /// suite's B65.
    /// <para><b>Expected console noise:</b> B32 exercises the real driver's stage-2 fault hook, so one
    /// deliberate <c>[MESHING] Merging the completed mesh …</c> error is logged during a healthy run (the
    /// CP-6 / NS-1 injected-fault precedent).</para>
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
            scenarios.Add(new Scenario("B31: MeshCompletionDriver triggers the load animation exactly for merges that applied — a gone chunk discards without animating, and still releases (MP-6)", B31_DriverAppliesThenAnimates));
            scenarios.Add(new Scenario("B32: a faulting apply never animates, still releases its buffers, and does not abort the pass (MP-6) [logs one deliberate [MESHING] error]", B32_DriverMergeFaultNeverAnimates));
            scenarios.Add(new Scenario("B33: MeshCompletionDriver's per-job scratch — each release gets its OWN job, and the scratch is cleared after (MP-6 §8.1)", B33_DriverScratchLifecycle));
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
        /// B31 — the MP-6 apply → load-animation mapping, on the production
        /// <see cref="MeshCompletionDriver"/> driven through the real <see cref="JobCompletionPass"/>.
        /// Three jobs: two whose chunk is live, one whose chunk is gone. The animation must fire once per
        /// APPLIED mesh, strictly after that apply, and never for the discarded one — while the discarded job
        /// still releases its buffers (the MR-6 single-release-site invariant, evidence-only before this).
        /// Prove-red: hoist <c>TriggerLoadAnimation</c> out of the <c>if</c> in
        /// <see cref="MeshCompletionDriver.MergeJob"/> → an <c>Animate(2)</c> appears for the gone chunk.
        /// </summary>
        private static bool B31_DriverAppliesThenAnimates()
        {
            RecordingMeshCompletionHost host = new RecordingMeshCompletionHost(goneChunks: new[] { 2 });
            MeshCompletionDriver driver = new MeshCompletionDriver(host);
            List<ChunkCoord> enrolled = new List<ChunkCoord>();

            ChunkCoord[] candidates = { Key(1), Key(2), Key(3) };
            JobCompletionPass.RunMergeLoop(candidates, driver, enrolled);
            JobCompletionPass.RunRemoveAndPromote(enrolled, driver);

            // The whole contract as one order-sensitive string: the animation is bound to the apply that
            // earned it (same key, immediately after), the gone chunk applies-false → no animation but a
            // release anyway, and removal is strictly after the merge loop.
            const string expected =
                "IsComplete(1) Complete(1) Apply(1,101) Animate(1) Release(101) " +
                "IsComplete(2) Complete(2) Apply(2,102) Release(102) " +
                "IsComplete(3) Complete(3) Apply(3,103) Animate(3) Release(103) " +
                "Remove(1) Remove(2) Remove(3)";
            string actual = host.OpLog;

            bool ok = MeshAssert.IsTrue(
                "B31.1 apply → animate mapping (gone chunk discards without animating, still releases)",
                actual == expected,
                actual == expected
                    ? $"op log matches the contract: {actual}"
                    : $"op log diverged from the contract.\n      expected: {expected}\n      actual:   {actual}");

            // Positive control: the host CAN observe an animation (so "no Animate(2)" above is not vacuous),
            // and the counts line up with the applies that succeeded.
            ok &= MeshAssert.IsTrue(
                "B31.2 animation count equals the successful-apply count",
                host.AnimateCount == 2 && host.ApplyCount == 3,
                host.AnimateCount == 2 && host.ApplyCount == 3
                    ? "3 applies attempted, 2 succeeded, 2 animations — the probe is live, not silent"
                    : $"applies={host.ApplyCount.ToString()}, animations={host.AnimateCount.ToString()}, expected 3 / 2");

            return ok;
        }

        /// <summary>
        /// B32 — a merge whose apply throws must not animate, must still release its buffers, and must not
        /// abort the pass. Logs one deliberate <c>[MESHING]</c> error (the real driver's stage-2 fault hook).
        /// Prove-red: move <c>TriggerLoadAnimation</c> into a <c>finally</c> around the apply → the faulted
        /// job animates a chunk whose mesh never landed.
        /// </summary>
        private static bool B32_DriverMergeFaultNeverAnimates()
        {
            RecordingMeshCompletionHost host = new RecordingMeshCompletionHost(applyFaultChunk: 1);
            MeshCompletionDriver driver = new MeshCompletionDriver(host);
            List<ChunkCoord> enrolled = new List<ChunkCoord>();

            ChunkCoord[] candidates = { Key(1), Key(2) };
            JobCompletionPass.RunMergeLoop(candidates, driver, enrolled);
            JobCompletionPass.RunRemoveAndPromote(enrolled, driver);

            const string expected =
                "IsComplete(1) Complete(1) Apply(1,101) Release(101) " +
                "IsComplete(2) Complete(2) Apply(2,102) Animate(2) Release(102) " +
                "Remove(1) Remove(2)";
            string actual = host.OpLog;

            bool ok = MeshAssert.IsTrue(
                "B32.1 a faulting apply releases but never animates, and the pass continues",
                actual == expected,
                actual == expected
                    ? $"op log matches the contract: {actual}"
                    : $"op log diverged from the contract.\n      expected: {expected}\n      actual:   {actual}");

            ok &= MeshAssert.IsTrue(
                "B32.2 the faulted job is still enrolled (never stranded with released buffers)",
                enrolled.Count == 2,
                enrolled.Count == 2
                    ? "both jobs enrolled — the faulted one is removed from the registry like any other"
                    : $"enrolled {enrolled.Count.ToString()} job(s), expected 2");

            return ok;
        }

        /// <summary>
        /// B33 — the <c>_curJob</c> scratch lifecycle. Each job's release must receive that job's OWN data
        /// (no carry-over between candidates), and the scratch must be cleared afterwards so a hook running
        /// out of sequence operates on <c>default</c> rather than silently double-returning the previous
        /// job's pooled buffers. This is the code-review finding of 2026-07-25, which had to be accepted on
        /// reasoning because no baseline could observe it. Prove-red: delete <c>_curJob = default;</c> from
        /// <see cref="MeshCompletionDriver.ReleaseJob"/> → the out-of-sequence release reports job 3's epoch.
        /// </summary>
        private static bool B33_DriverScratchLifecycle()
        {
            RecordingMeshCompletionHost host = new RecordingMeshCompletionHost();
            MeshCompletionDriver driver = new MeshCompletionDriver(host);
            List<ChunkCoord> enrolled = new List<ChunkCoord>();

            JobCompletionPass.RunMergeLoop(new[] { Key(1), Key(2), Key(3) }, driver, enrolled);

            // Each release carries its own job's epoch — a scratch that leaked across candidates would repeat
            // an earlier epoch here.
            bool perJob = host.ReleasedEpochs.Count == 3
                          && host.ReleasedEpochs[0] == 101
                          && host.ReleasedEpochs[1] == 102
                          && host.ReleasedEpochs[2] == 103;
            bool ok = MeshAssert.IsTrue(
                "B33.1 every release receives its own job's data",
                perJob,
                perJob
                    ? "released epochs 101, 102, 103 — one per candidate, in order"
                    : $"released epochs [{string.Join(", ", host.ReleasedEpochs)}], expected [101, 102, 103]");

            // Out-of-sequence release: the driver's documented postcondition is that the scratch is dead after
            // ReleaseJob, so this must hand back a zeroed job — NOT job 3's already-returned buffers.
            driver.ReleaseJob(Key(3));
            bool scratchCleared = host.ReleasedEpochs.Count == 4 && host.ReleasedEpochs[3] == 0;
            ok &= MeshAssert.IsTrue(
                "B33.2 the scratch is cleared after a release (an out-of-sequence hook cannot double-return)",
                scratchCleared,
                scratchCleared
                    ? "a release outside a Complete → Merge → Release sequence handed back default(MeshingJobData)"
                    : $"expected a 4th release with epoch 0, got [{string.Join(", ", host.ReleasedEpochs)}]");

            return ok;
        }

        /// <summary>Builds the scenario's chunk key from a small integer tag.</summary>
        /// <param name="tag">The per-job tag; also the low digit of the job's <c>TargetEpoch</c>.</param>
        /// <returns>A chunk coordinate the fake host recognizes.</returns>
        private static ChunkCoord Key(int tag) => new ChunkCoord(tag, 0);

        /// <summary>
        /// A recording <see cref="IMeshCompletionHost"/>: it logs every collaborator call the
        /// <see cref="MeshCompletionDriver"/> makes, in order, and can be scripted to report a chunk gone or
        /// to throw from the apply. Jobs are tagged by <see cref="MeshingJobData.TargetEpoch"/>
        /// (<c>100 + key.X</c>) — a plain blittable field, so no native buffers, no <c>Chunk</c>, and no
        /// <c>World</c> are needed anywhere in this fixture.
        /// </summary>
        private sealed class RecordingMeshCompletionHost : IMeshCompletionHost
        {
            private readonly StringBuilder _ops = new StringBuilder();
            private readonly HashSet<int> _goneChunks = new HashSet<int>();
            private readonly int _applyFaultChunk;

            /// <summary>Initializes a scripted recording host.</summary>
            /// <param name="goneChunks">Key tags whose <see cref="TryApplyMesh"/> reports the chunk gone.</param>
            /// <param name="applyFaultChunk">Key tag whose <see cref="TryApplyMesh"/> throws (-1 = none).</param>
            public RecordingMeshCompletionHost(IEnumerable<int> goneChunks = null, int applyFaultChunk = -1)
            {
                if (goneChunks != null)
                    foreach (int tag in goneChunks)
                        _goneChunks.Add(tag);

                _applyFaultChunk = applyFaultChunk;
            }

            /// <summary>The space-separated collaborator log, in invocation order.</summary>
            public string OpLog => _ops.ToString().TrimEnd();

            /// <summary>The <c>TargetEpoch</c> of every job handed to <see cref="ReleaseJobData"/>, in order.</summary>
            public List<int> ReleasedEpochs { get; } = new List<int>();

            /// <summary>How many applies were attempted (successful or not).</summary>
            public int ApplyCount { get; private set; }

            /// <summary>How many load animations were triggered.</summary>
            public int AnimateCount { get; private set; }

            /// <inheritdoc />
            public bool IsJobComplete(ChunkCoord key)
            {
                Record("IsComplete", key.X);
                return true;
            }

            /// <inheritdoc />
            public MeshingJobData CompleteJob(ChunkCoord key)
            {
                Record("Complete", key.X);
                return new MeshingJobData { TargetEpoch = EpochFor(key) };
            }

            /// <inheritdoc />
            public bool TryApplyMesh(ChunkCoord key, in MeshingJobData job)
            {
                ApplyCount++;
                _ops.Append("Apply(").Append(key.X).Append(',').Append(job.TargetEpoch).Append(") ");

                if (key.X == _applyFaultChunk)
                    throw new InvalidOperationException($"apply fault on {key.X.ToString()}");

                return !_goneChunks.Contains(key.X);
            }

            /// <inheritdoc />
            public void TriggerLoadAnimation(ChunkCoord key)
            {
                AnimateCount++;
                Record("Animate", key.X);
            }

            /// <inheritdoc />
            public void ReleaseJobData(in MeshingJobData job)
            {
                ReleasedEpochs.Add(job.TargetEpoch);
                Record("Release", job.TargetEpoch);
            }

            /// <inheritdoc />
            public void RemoveJob(ChunkCoord key) => Record("Remove", key.X);

            private static int EpochFor(ChunkCoord key) => 100 + key.X;

            private void Record(string op, int value) => _ops.Append(op).Append('(').Append(value).Append(") ");
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
