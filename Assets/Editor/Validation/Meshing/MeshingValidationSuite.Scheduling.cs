using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text;
using Data;
using Editor.Validation.Meshing.Framework;
using Helpers;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Meshing
{
    /// <summary>
    /// Orchestration-layer baselines (MP-2 — see
    /// Documentation/Design/MESHING_PIPELINE_ORCHESTRATION_REFACTOR.md §2.3/§4.1). Until now the
    /// scheduling loop had zero coverage: the meshing suite started at the job's inputs and the queue
    /// suite ended at the queue's API, so the gate composition (<c>ScheduleMeshing</c>) and the drain
    /// policy (<c>World.Update</c> step "schedule new mesh jobs") could regress unseen.
    /// <list type="bullet">
    /// <item><b>B24</b> — decision census: every input combination of the pure
    /// <see cref="MeshingScheduleDecision"/> maps to the documented result (in-flight → center-light →
    /// neighbor precedence, plus the lighting-disabled bypass), asserted against an independently-stated
    /// contract oracle so inverting a gate term in <c>Evaluate</c> reds it.</item>
    /// <item><b>B25</b> — drain policy: the real <see cref="MeshDrainPolicy.Drain"/> loop (the exact one
    /// <c>World.Update</c> runs) replayed over a real <see cref="MeshBuildQueue"/> with a scripted
    /// <see cref="IMeshDrainHost"/> — pins the quota stop, the in-flight-cap re-check, the time-window
    /// stop, the budgets-off leg, null/inactive purge, remove-on-schedule vs leave-on-decline, and
    /// immediate-ahead-of-normal order. The budget <i>math</i> (<see cref="PipelinePassBudget"/>) is
    /// owned by the Pipeline Backpressure suite; this pins the loop that consumes it.</item>
    /// </list>
    /// Both are baselines (must stay green). Self-registers via the <see cref="AddSchedulingScenarios"/>
    /// hook called from <c>Execute</c>. No <see cref="MeshingTestWorld"/> / world coupling — the decision
    /// is pure and the queue only reads <c>Coord</c>/<c>IsActive</c>.
    /// </summary>
    public static partial class MeshingValidationSuite
    {
        /// <summary>Registers the MP-2 orchestration baselines (called from <c>Execute</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddSchedulingScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("B24: MeshingScheduleDecision census — all 32 input combinations map to the documented result (MP-2 gate composition)", B24_ScheduleDecisionCensus));
            scenarios.Add(new Scenario("B25: MeshDrainPolicy replays the drain's quota/cap/window stops, purge, remove-vs-leave, and priority order (MP-2 drain policy)", B25_DrainPolicy));
            scenarios.Add(new Scenario("B26: an in-flight request stays queued (MP-3 F1 fix) — DequeuesChunk leaves AlreadyInFlight queued, and the drain reschedules it after the flight completes", B26_InFlightRequestStaysQueued));
        }

        /// <summary>
        /// B24 — sweeps all 2⁵ input combinations of <see cref="MeshingScheduleDecision.Evaluate"/> and
        /// asserts each equals <see cref="ExpectedDecision"/>, an independent restatement of the
        /// <c>ScheduleMeshing</c> gate contract. Prove-red: invert the center-gate term in <c>Evaluate</c>
        /// (e.g. drop the <c>lightingEnabled &amp;&amp;</c> guard) → the lighting-disabled rows diverge and B24 reds.
        /// </summary>
        private static bool B24_ScheduleDecisionCensus()
        {
            StringBuilder mismatches = new StringBuilder();
            int checkedCount = 0;

            for (int mask = 0; mask < 32; mask++)
            {
                bool inFlight = (mask & 1) != 0;
                bool lightingEnabled = (mask & 2) != 0;
                bool hasWork = (mask & 4) != 0;
                bool needsInit = (mask & 8) != 0;
                bool neighborsReady = (mask & 16) != 0;

                MeshingScheduleDecision.Result expected =
                    ExpectedDecision(inFlight, lightingEnabled, hasWork, needsInit, neighborsReady);
                MeshingScheduleDecision.Result actual = MeshingScheduleDecision.Evaluate(
                    inFlight, lightingEnabled, hasWork, needsInit, neighborsReady);
                checkedCount++;

                if (actual != expected)
                    mismatches.AppendLine(
                        $"    inFlight={inFlight}, lightingEnabled={lightingEnabled}, hasWork={hasWork}, " +
                        $"needsInit={needsInit}, neighborsReady={neighborsReady}: expected {expected}, got {actual}");
            }

            return MeshAssert.IsTrue(
                "B24: MeshingScheduleDecision census",
                mismatches.Length == 0,
                mismatches.Length == 0
                    ? $"all {checkedCount} input combinations match the contract oracle (in-flight → center-light → neighbor precedence, lighting-disabled bypass)"
                    : $"{checkedCount} combinations checked; divergences from the contract:\n{mismatches}");
        }

        /// <summary>
        /// Independent restatement of the <c>ScheduleMeshing</c> gate CONTRACT — the census oracle. This is
        /// a separate copy of the spec, NOT a call to <see cref="MeshingScheduleDecision.Evaluate"/>, so a
        /// mutation to the production decision diverges from it (the prove-red mechanism). Precedence:
        /// in-flight beats the center-light gate beats the neighbor gate; the center-light gate is bypassed
        /// when lighting is disabled.
        /// </summary>
        private static MeshingScheduleDecision.Result ExpectedDecision(
            bool inFlight, bool lightingEnabled, bool hasWork, bool needsInit, bool neighborsReady)
        {
            if (inFlight) return MeshingScheduleDecision.Result.AlreadyInFlight;
            if (lightingEnabled && (hasWork || needsInit)) return MeshingScheduleDecision.Result.CenterNotLightReady;
            if (!neighborsReady) return MeshingScheduleDecision.Result.NeighborsNotReady;
            return MeshingScheduleDecision.Result.Schedule;
        }

        /// <summary>
        /// B25 — drives the production <see cref="MeshDrainPolicy.Drain"/> over a real
        /// <see cref="MeshBuildQueue"/> across seven policy legs. Prove-red: change the leave-on-decline arm
        /// in <c>MeshDrainPolicy</c> to remove on <c>false</c> → leg 6 reds (the declined chunk is dropped
        /// instead of retried). Each leg is a fresh queue so ordering assertions are unambiguous.
        /// </summary>
        private static bool B25_DrainPolicy()
        {
            bool ok = true;

            ChunkCoord a = new ChunkCoord(0, 0);
            ChunkCoord b = new ChunkCoord(1, 0);
            ChunkCoord c = new ChunkCoord(2, 0);
            ChunkCoord d = new ChunkCoord(3, 0);
            ChunkCoord e = new ChunkCoord(4, 0);

            // Leg 1 — budgets-off (window = default, quota = raw cap): drains every ready chunk.
            {
                MeshBuildQueue q = BuildQueue((a, true), (b, true), (c, true));
                ScriptedDrainHost host = new ScriptedDrainHost(0, a, b, c);
                int scheduled = MeshDrainPolicy.Drain(q, 10, default, 10, host).Scheduled;
                ok &= MeshAssert.IsTrue("B25.1 budgets-off drains all ready chunks",
                    scheduled == 3 && q.Count == 0 && host.Scheduled.Count == 3,
                    $"scheduled {scheduled} (want 3), queue left {q.Count} (want 0)");
            }

            // Leg 2 — quota stop: only the first `quota` chunks schedule; the rest stay queued in place.
            {
                MeshBuildQueue q = BuildQueue((a, true), (b, true), (c, true), (d, true), (e, true));
                ScriptedDrainHost host = new ScriptedDrainHost(0, a, b, c, d, e);
                int scheduled = MeshDrainPolicy.Drain(q, 2, default, 10, host).Scheduled;
                ok &= MeshAssert.IsTrue("B25.2 quota stop schedules exactly `quota` chunks",
                    scheduled == 2 && q.Count == 3 && host.Scheduled.Count == 2,
                    $"scheduled {scheduled} (want 2), queue left {q.Count} (want 3)");
            }

            // Leg 3 — in-flight-cap re-check: entering under the cap, one schedule grows the live count to
            // the cap, so the NEXT iteration stops — even though the quota is far from spent.
            {
                MeshBuildQueue q = BuildQueue((a, true), (b, true), (c, true), (d, true), (e, true));
                ScriptedDrainHost host = new ScriptedDrainHost(2, a, b, c, d, e); // starts 2 in flight
                int scheduled = MeshDrainPolicy.Drain(q, 10, default, 3, host).Scheduled;
                ok &= MeshAssert.IsTrue("B25.3 in-flight cap re-check stops mid-quota once the live count hits the cap",
                    scheduled == 1 && q.Count == 4,
                    $"scheduled {scheduled} (want 1: cap 3 − 2 already in flight), queue left {q.Count} (want 4)");
            }

            // Leg 4 — time-window stop: an already-expired window breaks before any schedule.
            {
                MeshBuildQueue q = BuildQueue((a, true), (b, true), (c, true), (d, true), (e, true));
                ScriptedDrainHost host = new ScriptedDrainHost(0, a, b, c, d, e);
                int scheduled = MeshDrainPolicy.Drain(q, 10, ExpiredWindow(), 10, host).Scheduled;
                ok &= MeshAssert.IsTrue("B25.4 expired time-window stops the drain with nothing scheduled",
                    scheduled == 0 && q.Count == 5,
                    $"scheduled {scheduled} (want 0), queue left {q.Count} (want 5)");
            }

            // Leg 5 — inactive purge: an inactive chunk is removed WITHOUT scheduling; active ones schedule.
            {
                MeshBuildQueue q = BuildQueue((a, true), (b, false), (c, true)); // b inactive
                ScriptedDrainHost host = new ScriptedDrainHost(0, a, b, c);
                int scheduled = MeshDrainPolicy.Drain(q, 10, default, 10, host).Scheduled;
                ok &= MeshAssert.IsTrue("B25.5 inactive chunk is purged, not scheduled",
                    scheduled == 2 && q.Count == 0
                                   && host.Scheduled.Contains(a) && host.Scheduled.Contains(c) && !host.Scheduled.Contains(b),
                    $"scheduled {scheduled} (want 2: a,c), b scheduled? {host.Scheduled.Contains(b)} (want false)");
            }

            // Leg 6 — leave-on-decline (the F1/MP-3 seam): a declined chunk (deps not ready) stays queued;
            // the drain moves on to the next. THE PROVE-RED ANCHOR.
            {
                MeshBuildQueue q = BuildQueue((a, true), (b, true)); // both active; a will decline, b will schedule
                ScriptedDrainHost host = new ScriptedDrainHost(0, b); // only b is schedulable
                int scheduled = MeshDrainPolicy.Drain(q, 10, default, 10, host).Scheduled;
                ok &= MeshAssert.IsTrue("B25.6 declined chunk is left queued (retry next frame), scheduled chunk is removed",
                    scheduled == 1 && q.Count == 1 && q.Contains(a) && !q.Contains(b),
                    $"scheduled {scheduled} (want 1: b), a still queued? {q.Contains(a)} (want true), b still queued? {q.Contains(b)} (want false)");
            }

            // Leg 7 — priority order: an immediate re-request drains ahead of an earlier normal request.
            {
                MeshBuildQueue q = new MeshBuildQueue();
                q.TryEnqueue(MakeSchedulingChunk(a, true), false); // normal
                q.TryEnqueue(MakeSchedulingChunk(b, true), true); // immediate → jumps to head
                ScriptedDrainHost host = new ScriptedDrainHost(0, a, b);
                int scheduled = MeshDrainPolicy.Drain(q, 1, default, 10, host).Scheduled; // budget one schedule
                ok &= MeshAssert.IsTrue("B25.7 immediate request drains ahead of an earlier normal request",
                    scheduled == 1 && host.Scheduled.Count == 1 && host.Scheduled[0].Equals(b)
                    && q.Contains(a) && !q.Contains(b),
                    $"first scheduled = {(host.Scheduled.Count > 0 ? Fmt(host.Scheduled[0]) : "none")} (want {Fmt(b)}), normal still queued? {q.Contains(a)} (want true)");
            }

            return ok;
        }

        /// <summary>
        /// B26 — the MP-3 F1 fix: a rebuild request arriving while a chunk's mesh job is in flight must stay
        /// queued (rescheduled after the flight completes), not be dropped against the job's stale schedule-time
        /// snapshot. Two parts, both over the exact production code paths:
        /// <list type="bullet">
        /// <item>the pure <see cref="MeshingScheduleDecision.DequeuesChunk"/> mapping — <c>AlreadyInFlight</c>
        /// leaves the chunk queued (false); only <c>Schedule</c> dequeues (true). <b>The revert guard:</b>
        /// restoring the pre-MP-3 mapping (in-flight also dequeues) reds this immediately, and because
        /// <c>ScheduleMeshing</c> reads the same function it regresses in lockstep.</item>
        /// <item>an end-to-end drain scenario driving the real <see cref="MeshDrainPolicy.Drain"/> through a host
        /// whose verdict is <c>DequeuesChunk(Evaluate(...))</c>: while in flight the chunk survives the drain;
        /// once the flight completes the same chunk drains and is removed.</item>
        /// </list>
        /// </summary>
        private static bool B26_InFlightRequestStaysQueued()
        {
            bool ok = true;

            // Part 1 — the shared dequeue mapping (production ScheduleMeshing reads the SAME function).
            ok &= MeshAssert.IsTrue("B26.1 in-flight decision leaves the chunk queued (not dequeued) — the MP-3 fix",
                !MeshingScheduleDecision.DequeuesChunk(MeshingScheduleDecision.Result.AlreadyInFlight),
                "DequeuesChunk(AlreadyInFlight) must be false so the drain leaves the request queued to retry");
            ok &= MeshAssert.IsTrue("B26.2 only a Schedule decision dequeues; every non-Schedule result leaves the chunk queued",
                MeshingScheduleDecision.DequeuesChunk(MeshingScheduleDecision.Result.Schedule)
                && !MeshingScheduleDecision.DequeuesChunk(MeshingScheduleDecision.Result.CenterNotLightReady)
                && !MeshingScheduleDecision.DequeuesChunk(MeshingScheduleDecision.Result.NeighborsNotReady),
                "Schedule must dequeue; in-flight + both decline results must leave the chunk queued");

            // Part 2 — end-to-end: the drain leaves an in-flight chunk queued this frame, schedules it the next.
            ChunkCoord x = new ChunkCoord(0, 0);
            MeshBuildQueue q = BuildQueue((x, true));
            InFlightDrainHost host = new InFlightDrainHost { InFlight = true };

            // Frame 1 — X's mesh job is in flight: the request must survive the drain (F1 pre-fix would drop it).
            int f1 = MeshDrainPolicy.Drain(q, 10, default, 10, host).Scheduled;
            ok &= MeshAssert.IsTrue("B26.3 request during flight stays queued (drain schedules nothing, chunk retained)",
                f1 == 0 && q.Count == 1 && q.Contains(x),
                $"frame 1 scheduled {f1} (want 0), queue left {q.Count} (want 1: X retained for retry)");

            // Frame 2 — the flight completed: the retained request now schedules and is dequeued.
            host.InFlight = false;
            int f2 = MeshDrainPolicy.Drain(q, 10, default, 10, host).Scheduled;
            ok &= MeshAssert.IsTrue("B26.4 after the flight completes the retained request schedules and is dequeued",
                f2 == 1 && q.Count == 0 && host.Scheduled.Contains(x),
                $"frame 2 scheduled {f2} (want 1), queue left {q.Count} (want 0), X scheduled? {host.Scheduled.Contains(x)}");

            return ok;
        }

        /// <summary>Builds a queue from <c>(coord, active)</c> pairs, enqueued as normal requests in order.</summary>
        private static MeshBuildQueue BuildQueue(params (ChunkCoord coord, bool active)[] entries)
        {
            MeshBuildQueue q = new MeshBuildQueue();
            foreach ((ChunkCoord coord, bool active) in entries)
                q.TryEnqueue(MakeSchedulingChunk(coord, active), false);
            return q;
        }

        /// <summary>
        /// Mints a bare <see cref="Chunk"/> carrying only <paramref name="coord"/> and its active flag,
        /// bypassing the constructor (which needs a live <c>World.Instance</c> + GameObject). The queue and
        /// drain only read <c>Coord</c>/<c>IsActive</c>; the <c>IsActive</c> setter's GameObject branch is a
        /// no-op here because <c>ChunkGameObject</c> is null on an uninitialized instance (the
        /// <c>MeshBuildQueueValidationSuite.MakeChunk</c> precedent, extended with the active flag).
        /// </summary>
        private static Chunk MakeSchedulingChunk(ChunkCoord coord, bool active)
        {
            Chunk chunk = (Chunk)FormatterServices.GetUninitializedObject(typeof(Chunk));
            chunk.Coord = coord;
            chunk.IsActive = active;
            return chunk;
        }

        /// <summary>An already-expired budget window: constructed with a start far in the past and a 1-tick
        /// budget so <see cref="PipelinePassBudget.Window.Expired"/> is true on the first read (deterministic,
        /// no sleep). Fully-qualifies <c>Stopwatch</c> to avoid the <c>UnityEngine.Debug</c> ambiguity.</summary>
        private static PipelinePassBudget.Window ExpiredWindow() =>
            new PipelinePassBudget.Window(System.Diagnostics.Stopwatch.GetTimestamp() - 1_000_000_000L, 1L);

        /// <summary>Formats a coordinate as <c>(x,z)</c> for scheduling-leg log output.</summary>
        private static string Fmt(ChunkCoord coord) => $"({coord.X},{coord.Z})";

        /// <summary>
        /// A scripted <see cref="IMeshDrainHost"/> for B25: schedules only the coords it was told are ready
        /// (others decline, modeling unmet neighbor/lighting deps), records what it scheduled, and grows its
        /// live in-flight count on each real schedule so the drain's per-iteration cap re-check is exercised
        /// faithfully.
        /// </summary>
        private sealed class ScriptedDrainHost : IMeshDrainHost
        {
            private readonly HashSet<ChunkCoord> _schedulable;

            /// <summary>Coords this host accepted, in schedule order.</summary>
            public readonly List<ChunkCoord> Scheduled = new List<ChunkCoord>();

            /// <summary>Live in-flight count read by the drain's cap check; grows on each real schedule.</summary>
            public int InFlightCount { get; private set; }

            public ScriptedDrainHost(int initialInFlight, params ChunkCoord[] schedulable)
            {
                InFlightCount = initialInFlight;
                _schedulable = new HashSet<ChunkCoord>(schedulable);
            }

            public bool TrySchedule(Chunk chunk)
            {
                if (!_schedulable.Contains(chunk.Coord))
                    return false; // deps not ready — decline (drain leaves it queued)

                Scheduled.Add(chunk.Coord);
                InFlightCount++; // a real schedule grows the in-flight set (production: MeshJobs gains an entry)
                return true;
            }
        }

        /// <summary>
        /// A B26 host modeling a single chunk with a toggleable in-flight state, deciding via the SAME
        /// production functions <c>ScheduleMeshing</c> uses — <c>DequeuesChunk(Evaluate(jobInFlight: InFlight, …))</c>
        /// with lighting disabled and neighbors ready, so only the in-flight gate is in play. While in flight it
        /// declines (the drain leaves the chunk queued — the MP-3 retry); once the flight completes it schedules.
        /// </summary>
        private sealed class InFlightDrainHost : IMeshDrainHost
        {
            /// <summary>Whether the chunk's mesh job is currently in flight.</summary>
            public bool InFlight;

            /// <summary>Coords this host scheduled, in order.</summary>
            public readonly List<ChunkCoord> Scheduled = new List<ChunkCoord>();

            /// <summary>Grows on each real schedule so the drain's per-iteration cap check sees a live count.</summary>
            public int InFlightCount { get; private set; }

            public bool TrySchedule(Chunk chunk)
            {
                MeshingScheduleDecision.Result decision = MeshingScheduleDecision.Evaluate(
                    jobInFlight: InFlight, lightingEnabled: false,
                    centerHasLightWork: false, centerNeedsInitialLighting: false,
                    neighborsMeshReady: true);

                if (!MeshingScheduleDecision.DequeuesChunk(decision))
                    return false; // in-flight (or unmet dep) — leave queued, the MP-3 retry

                Scheduled.Add(chunk.Coord);
                InFlightCount++;
                return true;
            }
        }
    }
}
