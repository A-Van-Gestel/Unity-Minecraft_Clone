using System.Collections.Generic;
using System.Diagnostics;
using Data;
using UnityEngine;

namespace Benchmarks
{
    /// <summary>The budgeted per-frame pipeline passes a stop reason can be attributed to (FP-2).</summary>
    /// <remarks>
    /// Four, not five: MP-6 retired the draw budget with the stage it bounded. The lighting <i>merge</i>
    /// (<c>ProcessLightingJobs</c>) is deliberately absent — it takes no budget window, so it has no stop
    /// reason to report.
    /// </remarks>
    public enum PipelinePass : byte
    {
        /// <summary>The lighting ready-set scan (quota + ceiling + in-flight cap).</summary>
        LightSchedule = 0,

        /// <summary>The mesh-build queue drain (quota + ceiling + in-flight cap).</summary>
        MeshSchedule = 1,

        /// <summary>Completed-generation-job processing (ceiling only).</summary>
        GenerationProcess = 2,

        /// <summary>Completed-mesh-job processing (ceiling only).</summary>
        MeshProcess = 3,
    }

    /// <summary>
    /// Why a budgeted pass stopped — the admission-bound signal (design §5.1). Five values, because the
    /// three the design first proposed could not express two real break conditions.
    /// </summary>
    public enum PassStopReason : byte
    {
        /// <summary>
        /// The pass did not execute this frame — <b>not</b> a break reason, and deliberately the zero value
        /// so a default-initialized sample cannot masquerade as <see cref="OutOfWork"/> ("ran, nothing left"),
        /// which is a materially different claim. Never tallied: <see cref="PipelineTelemetry.RecordPassStop"/>
        /// ignores it.
        /// </summary>
        NotRun = 0,

        /// <summary>Ran to completion with work served. The pipeline is keeping up.</summary>
        OutOfWork = 1,

        /// <summary>The per-frame rate quota was spent. Unreachable for the two ceiling-only passes.</summary>
        Quota = 2,

        /// <summary>The Stopwatch ms ceiling was spent (hitch guard).</summary>
        Ceiling = 3,

        /// <summary>The OM-1 in-flight job cap was reached — a memory bound, not a throughput budget.</summary>
        InFlightCap = 4,

        /// <summary>
        /// The queue was walked in full and nothing was schedulable — a readiness gate is failing upstream.
        /// Distinct from <see cref="OutOfWork"/> by design: conflating them reports a stalled pipeline as a
        /// healthy one, which is the worst misreading this instrument could produce.
        /// </summary>
        AllDeclined = 5,
    }

    /// <summary>How a chunk's traced lifecycle ended.</summary>
    public enum TraceDisposition : byte
    {
        /// <summary>Still in flight — no terminal event has been stamped yet.</summary>
        Pending = 0,

        /// <summary>Reached the terminal stage: its mesh was applied, and it is on screen.</summary>
        MeshApplied = 1,

        /// <summary>Its completed generation result was discarded — the chunk left the unload boundary mid-flight.</summary>
        DiscardedOutOfRange = 2,

        /// <summary>Its completed disk load was thrown away — the chunk was unloaded or pool-recycled mid-read.</summary>
        LoadStranded = 3,

        /// <summary>Superseded by a fresh request for the same coord before finishing (design §4.1 flush-and-restart).</summary>
        Rerequested = 4,

        /// <summary>
        /// The phase ended while the chunk was still in flight. Not waste — the capture simply stopped first.
        /// Kept distinct so an unfinished chunk is never miscounted as discarded work.
        /// </summary>
        InFlightAtPhaseEnd = 5,
    }

    /// <summary>
    /// One chunk's journey through the pipeline: a Stopwatch timestamp per stage plus how it ended.
    /// Chunk-granular by design — no per-voxel record exists anywhere in this layer.
    /// </summary>
    public struct ChunkTrace
    {
        /// <summary>Chunk index on the X axis.</summary>
        public int X;

        /// <summary>Chunk index on the Z axis.</summary>
        public int Z;

        /// <summary>Timestamp when the chunk was enqueued for generation/load, or 0 if unstamped.</summary>
        public long RequestedTicks;

        /// <summary>Timestamp when the request was admitted past the in-flight cap and panic gate, or 0.</summary>
        public long AdmittedTicks;

        /// <summary>Timestamp when terrain data became available (generated or deserialized), or 0.</summary>
        public long PopulatedTicks;

        /// <summary>Timestamp when the chunk's lighting last completed, or 0.</summary>
        public long LitTicks;

        /// <summary>Timestamp when the chunk's mesh was applied — the terminal stage — or 0.</summary>
        public long MeshAppliedTicks;

        /// <summary>Lighting jobs this chunk consumed (the edge-check cascade makes this &gt; 1 routinely).</summary>
        public int LightingPasses;

        /// <summary>How this trace ended.</summary>
        public TraceDisposition Disposition;
    }

    /// <summary>
    /// One frame's pipeline pressure: queue depths, panic-gate state, and each budgeted pass's stop reason.
    /// Written into a bounded rolling window; the aggregate tallies it feeds are exact and unbounded.
    /// </summary>
    public struct AdmissionSample
    {
        /// <summary>Generation requests awaiting admission.</summary>
        public int GenerationQueueDepth;

        /// <summary>Generation jobs in flight.</summary>
        public int GenerationInFlight;

        /// <summary>Schedulable lighting backlog (the panic gate's signal).</summary>
        public int LightReadyCount;

        /// <summary>Parked lighting backlog awaiting a promotion event.</summary>
        public int LightWaitingCount;

        /// <summary>Chunks queued for a mesh rebuild.</summary>
        public int MeshQueueDepth;

        /// <summary>Whether the §3.5 panic gate admitted generation this frame.</summary>
        public bool GateOpen;

        /// <summary>Per-pass stop reason, indexed by <see cref="PipelinePass"/>.</summary>
        public PassStopReasonSet StopReasons;
    }

    /// <summary>
    /// A fixed four-slot <see cref="PassStopReason"/> tuple, one per <see cref="PipelinePass"/>. A struct
    /// rather than an array so <see cref="AdmissionSample"/> stays allocation-free by value.
    /// </summary>
    public struct PassStopReasonSet
    {
        /// <summary>Stop reason for <see cref="PipelinePass.LightSchedule"/>.</summary>
        public PassStopReason LightSchedule;

        /// <summary>Stop reason for <see cref="PipelinePass.MeshSchedule"/>.</summary>
        public PassStopReason MeshSchedule;

        /// <summary>Stop reason for <see cref="PipelinePass.GenerationProcess"/>.</summary>
        public PassStopReason GenerationProcess;

        /// <summary>Stop reason for <see cref="PipelinePass.MeshProcess"/>.</summary>
        public PassStopReason MeshProcess;

        /// <summary>Reads or writes the slot for a given pass.</summary>
        /// <param name="pass">The pass to address.</param>
        /// <returns>That pass's recorded stop reason.</returns>
        public PassStopReason this[PipelinePass pass]
        {
            get => pass switch
            {
                PipelinePass.LightSchedule => LightSchedule,
                PipelinePass.MeshSchedule => MeshSchedule,
                PipelinePass.GenerationProcess => GenerationProcess,
                _ => MeshProcess,
            };
            set
            {
                switch (pass)
                {
                    case PipelinePass.LightSchedule: LightSchedule = value; break;
                    case PipelinePass.MeshSchedule: MeshSchedule = value; break;
                    case PipelinePass.GenerationProcess: GenerationProcess = value; break;
                    default: MeshProcess = value; break;
                }
            }
        }
    }

    /// <summary>
    /// The completed record for one benchmark phase: exact tallies, capped latency samples, and the
    /// saturation flags that keep a truncated capture from reading as a complete one.
    /// </summary>
    /// <remarks>
    /// Reference type because it owns the sample lists; one instance per phase is allocated at
    /// <see cref="PipelineTelemetry.EndPhase"/>, never per frame or per chunk.
    /// </remarks>
    public sealed class PipelinePhaseMetrics
    {
        /// <summary>Display name of the phase (matches <c>BenchmarkMetricsCollector</c>'s, e.g. "200 m/s").</summary>
        public string PhaseName;

        /// <summary>Logical group (e.g. "Generation Pass").</summary>
        public string GroupName;

        /// <summary>Wall-clock duration. Per-phase rates MUST divide by this, never by the nominal phase time.</summary>
        public float DurationSeconds;

        /// <summary>Frames sampled during the phase.</summary>
        public int FrameCount;

        /// <summary>Frames during which the panic gate was closed.</summary>
        public int GateClosedFrames;

        /// <summary>Chunks that entered the trace table.</summary>
        public int TracesStarted;

        /// <summary>Per-disposition trace counts, indexed by <see cref="TraceDisposition"/>.</summary>
        public readonly int[] DispositionCounts = new int[PipelineTelemetry.DispositionCount];

        /// <summary>Exact per-pass, per-reason frame tallies — <c>[pass][reason]</c>. Never truncated.</summary>
        public readonly int[,] StopReasonCounts =
            new int[PipelineTelemetry.PassCount, PipelineTelemetry.StopReasonCount];

        /// <summary>Enqueue → populated latencies in Stopwatch ticks (capped; see <see cref="SamplesSaturated"/>).</summary>
        public readonly List<long> RequestToPopulatedTicks = new List<long>();

        /// <summary>Populated → lit latencies in Stopwatch ticks.</summary>
        public readonly List<long> PopulatedToLitTicks = new List<long>();

        /// <summary>Lit → mesh-applied latencies in Stopwatch ticks.</summary>
        public readonly List<long> LitToMeshAppliedTicks = new List<long>();

        /// <summary>End-to-end enqueue → mesh-applied latencies in Stopwatch ticks.</summary>
        public readonly List<long> RequestToMeshAppliedTicks = new List<long>();

        /// <summary>The bounded rolling window of recent per-frame samples (oldest entries overwritten).</summary>
        public readonly List<AdmissionSample> RecentFrames = new List<AdmissionSample>();

        /// <summary>
        /// True when the trace table hit its capacity and later chunks went untraced. Every number derived
        /// from traces is then a prefix of the phase, and the report must say so rather than imply totality.
        /// </summary>
        public bool TracesSaturated;

        /// <summary>True when a latency sample list hit its cap — percentiles cover only the samples kept.</summary>
        public bool SamplesSaturated;

        /// <summary>
        /// True when the per-frame rolling window wrapped. Not a data-loss flag for the tallies (those stay
        /// exact); only <see cref="RecentFrames"/> is a window rather than the whole phase.
        /// </summary>
        public bool FrameWindowWrapped;

        /// <summary>Whether any capture buffer overflowed — the report's saturation banner condition.</summary>
        public bool AnySaturation => TracesSaturated || SamplesSaturated;
    }

    /// <summary>
    /// Opt-in, allocation-free-when-disabled telemetry for the chunk pipeline's <b>internals</b> — the layer
    /// that answers whether sluggish chunk appearance at speed is admission-, throughput-, ordering- or
    /// readiness-bound. Per-chunk stage latency is its load-bearing output; frame health stays with
    /// <see cref="PerformanceMonitor"/> and <c>BenchmarkMetricsCollector</c>, which this reports alongside.
    /// <para>
    /// Modeled directly on <see cref="WorldFrameProfiler"/>: a static, opt-in accumulator that production
    /// code calls through cheap guarded hooks, driven by a controller that flips <see cref="Enabled"/> for
    /// the duration of a capture. Every public method early-returns on a single bool read when disabled.
    /// </para>
    /// <para>
    /// <b>Side table, not fields on <c>ChunkData</c>:</b> the engine gains no fields and therefore no
    /// pool-reset obligation for a diagnostic that is off in every shipping session. See the design doc
    /// <c>Documentation/Design/FLIGHT_PROFILE_CAPTURE.md</c> §4.
    /// </para>
    /// </summary>
    public static class PipelineTelemetry
    {
        /// <summary>Number of <see cref="PipelinePass"/> values.</summary>
        public const int PassCount = 4;

        /// <summary>Number of <see cref="PassStopReason"/> values, including the untallied <c>NotRun</c>.</summary>
        public const int StopReasonCount = 6;

        /// <summary>Number of <see cref="TraceDisposition"/> values.</summary>
        public const int DispositionCount = 6;

        // Sizing floor/ceiling for the derived trace capacity. The floor keeps a tiny-LoadDistance session
        // from saturating instantly; the ceiling bounds worst-case memory (~48 B/trace + dictionary
        // overhead, so ~4 MB at the cap) on a capture that would otherwise try to trace a whole world.
        private const int MIN_TRACE_CAPACITY = 4096;
        private const int MAX_TRACE_CAPACITY = 65536;

        // Headroom over the geometric estimate, covering flush-and-restart revisits (§4.1): the loading
        // pass re-enters the same coords by design, and each revisit starts a fresh trace.
        private const float REVISIT_HEADROOM = 1.5f;

        // Latency samples are the percentile input, so they are capped independently of the trace table —
        // a phase can start far more traces than it completes, and only completions produce samples.
        private const int MAX_LATENCY_SAMPLES = 32768;

        // Starting capacity for each latency series. Well below MAX_LATENCY_SAMPLES on purpose: pre-sizing
        // all four series to the cap would reserve ~1 MB per phase up front for phases that never fill it.
        private const int INITIAL_SAMPLE_CAPACITY = 4096;

        // The per-frame detail window. Tallies are exact regardless; this only bounds how many individual
        // frames can be inspected after the fact.
        private const int FRAME_WINDOW_CAPACITY = 4096;

        /// <summary>
        /// When <c>false</c> (default) every method is a no-op guarded by a single bool read, so production
        /// frames pay nothing. Only a benchmark capture flips this on.
        /// </summary>
        public static bool Enabled;

        private static readonly Dictionary<ChunkCoord, ChunkTrace> s_traces =
            new Dictionary<ChunkCoord, ChunkTrace>(MIN_TRACE_CAPACITY);

        private static readonly List<PipelinePhaseMetrics> s_completedPhases = new List<PipelinePhaseMetrics>(16);

        private static PipelinePhaseMetrics s_activePhase;
        private static float s_phaseStartTime;
        private static int s_traceCapacity = MIN_TRACE_CAPACITY;
        private static int s_frameWindowCursor;
        private static AdmissionSample s_pendingFrame;

        /// <summary>Completed phase records, in capture order.</summary>
        public static IReadOnlyList<PipelinePhaseMetrics> CompletedPhases => s_completedPhases;

        /// <summary>Whether a phase is currently recording.</summary>
        public static bool IsPhaseActive => s_activePhase != null;

        /// <summary>Converts Stopwatch ticks to milliseconds.</summary>
        /// <param name="ticks">A tick count or tick delta.</param>
        /// <returns>The equivalent milliseconds.</returns>
        public static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        /// <summary>
        /// Clears all static state on play-mode entry so a capture left <see cref="Enabled"/> (or holding a
        /// previous session's phases) never leaks into the next when domain reload is disabled. Mirrors the
        /// <c>DomainReset</c> convention used by <see cref="WorldFrameProfiler"/> and
        /// <see cref="PerformanceMonitor"/>.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            Enabled = false;
            s_traces.Clear();
            s_completedPhases.Clear();
            s_activePhase = null;
            s_phaseStartTime = 0f;
            s_traceCapacity = MIN_TRACE_CAPACITY;
            s_frameWindowCursor = 0;
            s_pendingFrame = default;
        }

        /// <summary>
        /// Estimates how many chunk traces a phase will start, from the same region geometry the benchmark
        /// rig derives its waypoints from (design §8 Q1). A phase touches the resident load square plus one
        /// square-width swath per chunk of travel, times headroom for the revisits flush-and-restart traces
        /// separately.
        /// </summary>
        /// <param name="loadDistance">The active <c>Settings.LoadDistance</c>, in chunks.</param>
        /// <param name="speedMetersPerSecond">The phase's flight speed.</param>
        /// <param name="phaseSeconds">The phase's nominal duration.</param>
        /// <returns>A trace capacity, clamped to the supported range.</returns>
        public static int EstimateTraceCapacity(int loadDistance, float speedMetersPerSecond, float phaseSeconds)
        {
            int squareWidth = Mathf.Max(1, loadDistance * 2 + 1);
            long resident = (long)squareWidth * squareWidth;

            int travelChunks = Mathf.CeilToInt(
                Mathf.Max(0f, speedMetersPerSecond) * Mathf.Max(0f, phaseSeconds) / VoxelData.ChunkWidth);
            long swath = (long)squareWidth * travelChunks;

            long estimate = (long)((resident + swath) * REVISIT_HEADROOM);
            return ClampCapacity(estimate);
        }

        /// <summary>
        /// Clamps a capacity to the supported range. Hand-rolled rather than <c>Math.Clamp</c>, which is not
        /// available under this project's .NET Framework API compatibility level, and <c>Mathf.Clamp</c>
        /// has no <see cref="long"/> overload.
        /// </summary>
        /// <param name="value">The raw estimate.</param>
        /// <returns>The clamped capacity.</returns>
        private static int ClampCapacity(long value)
        {
            if (value < MIN_TRACE_CAPACITY) return MIN_TRACE_CAPACITY;
            return value > MAX_TRACE_CAPACITY ? MAX_TRACE_CAPACITY : (int)value;
        }

        /// <summary>
        /// Starts recording a phase. Any active phase is ended first (the <c>BeginPhase</c> contract
        /// <c>BenchmarkMetricsCollector</c> already uses, so the two stay in lockstep).
        /// </summary>
        /// <param name="phaseName">Display name, matching the frame-health collector's.</param>
        /// <param name="groupName">Logical group (e.g. "Generation Pass").</param>
        /// <param name="expectedTraceCapacity">Capacity hint from <see cref="EstimateTraceCapacity"/>.</param>
        public static void BeginPhase(string phaseName, string groupName, int expectedTraceCapacity)
        {
            if (!Enabled) return;

            if (s_activePhase != null) EndPhase();

            s_traceCapacity = ClampCapacity(expectedTraceCapacity);
            s_traces.Clear();
            s_frameWindowCursor = 0;
            s_pendingFrame = default;

            s_activePhase = new PipelinePhaseMetrics
            {
                PhaseName = phaseName,
                GroupName = groupName,
            };

            // Pre-size the capture buffers (design §6: no incremental growth during a capture). Done here
            // rather than in field initializers so the capacity constants can stay private to this class.
            s_activePhase.RecentFrames.Capacity = FRAME_WINDOW_CAPACITY;
            s_activePhase.RequestToPopulatedTicks.Capacity = INITIAL_SAMPLE_CAPACITY;
            s_activePhase.PopulatedToLitTicks.Capacity = INITIAL_SAMPLE_CAPACITY;
            s_activePhase.LitToMeshAppliedTicks.Capacity = INITIAL_SAMPLE_CAPACITY;
            s_activePhase.RequestToMeshAppliedTicks.Capacity = INITIAL_SAMPLE_CAPACITY;

            s_phaseStartTime = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// Ends the active phase and appends its record to <see cref="CompletedPhases"/>. Traces still in
        /// flight are finalized as <see cref="TraceDisposition.InFlightAtPhaseEnd"/> — counted, never
        /// silently dropped and never miscounted as waste.
        /// </summary>
        public static void EndPhase()
        {
            if (s_activePhase == null) return;

            foreach (KeyValuePair<ChunkCoord, ChunkTrace> entry in s_traces)
            {
                ChunkTrace trace = entry.Value;
                if (trace.Disposition == TraceDisposition.Pending)
                    trace.Disposition = TraceDisposition.InFlightAtPhaseEnd;

                CloseTrace(trace);
            }

            s_traces.Clear();

            s_activePhase.DurationSeconds = Time.realtimeSinceStartup - s_phaseStartTime;
            s_completedPhases.Add(s_activePhase);
            s_activePhase = null;
        }

        #region Stage stamps (FP-1 hook targets)

        /// <summary>
        /// Stamps a chunk being enqueued for generation or disk load — the chain's first hop. A coord that is
        /// already traced is <b>flushed and restarted</b> (§4.1): the loading pass revisits coords by design,
        /// so overwriting in place would silently discard the earlier visit's samples. The flush count is
        /// also the design's re-request (wave-front churn) metric.
        /// </summary>
        /// <param name="coord">The chunk being requested.</param>
        public static void StampRequested(ChunkCoord coord)
        {
            if (!Enabled || s_activePhase == null) return;

            if (s_traces.TryGetValue(coord, out ChunkTrace existing))
            {
                if (existing.Disposition == TraceDisposition.Pending)
                    existing.Disposition = TraceDisposition.Rerequested;

                CloseTrace(existing);
            }
            else if (s_traces.Count >= s_traceCapacity)
            {
                // Saturated: stop admitting NEW coords, but keep servicing coords already traced so their
                // in-flight journeys still complete. Silent truncation is the one outcome forbidden here.
                s_activePhase.TracesSaturated = true;
                return;
            }

            // Indexer, never Add: a duplicate key is routine here (the revisit above), and Add would throw
            // inside a hook that must be non-throwing by construction.
            s_traces[coord] = new ChunkTrace
            {
                X = coord.X,
                Z = coord.Z,
                RequestedTicks = Stopwatch.GetTimestamp(),
                Disposition = TraceDisposition.Pending,
            };

            s_activePhase.TracesStarted++;
        }

        /// <summary>Stamps a request being admitted past the in-flight cap and panic gate.</summary>
        /// <param name="coord">The admitted chunk.</param>
        public static void StampAdmitted(ChunkCoord coord)
        {
            if (!Enabled || s_activePhase == null) return;
            if (!s_traces.TryGetValue(coord, out ChunkTrace trace)) return;

            trace.AdmittedTicks = Stopwatch.GetTimestamp();
            s_traces[coord] = trace;
        }

        /// <summary>Stamps terrain data becoming available (generated or deserialized).</summary>
        /// <param name="coord">The populated chunk.</param>
        public static void StampPopulated(ChunkCoord coord)
        {
            if (!Enabled || s_activePhase == null) return;
            if (!s_traces.TryGetValue(coord, out ChunkTrace trace)) return;

            trace.PopulatedTicks = Stopwatch.GetTimestamp();
            s_traces[coord] = trace;
        }

        /// <summary>
        /// Stamps a completed lighting pass. Overwrites rather than keeping the first: the edge-check cascade
        /// runs several passes per chunk, and the gap that matters to meshing is the one before the LAST.
        /// </summary>
        /// <param name="coord">The lit chunk.</param>
        public static void StampLit(ChunkCoord coord)
        {
            if (!Enabled || s_activePhase == null) return;
            if (!s_traces.TryGetValue(coord, out ChunkTrace trace)) return;

            trace.LitTicks = Stopwatch.GetTimestamp();
            trace.LightingPasses++;
            s_traces[coord] = trace;
        }

        /// <summary>
        /// Stamps the terminal stage — the mesh was applied and the chunk is on screen. Post-MP-6 this is
        /// also the instant the load animation fires, so there is no separate "visible" hop to stamp.
        /// </summary>
        /// <param name="coord">The chunk whose mesh was applied.</param>
        public static void StampMeshApplied(ChunkCoord coord)
        {
            if (!Enabled || s_activePhase == null) return;
            if (!s_traces.TryGetValue(coord, out ChunkTrace trace)) return;

            trace.MeshAppliedTicks = Stopwatch.GetTimestamp();
            trace.Disposition = TraceDisposition.MeshApplied;

            CloseTrace(trace);
            s_traces.Remove(coord);
        }

        /// <summary>
        /// Stamps a non-terminal ending — a discarded generation result or a stranded disk load — and closes
        /// the trace.
        /// </summary>
        /// <param name="coord">The chunk whose work was thrown away.</param>
        /// <param name="disposition">Which waste path ended it.</param>
        public static void StampDisposition(ChunkCoord coord, TraceDisposition disposition)
        {
            if (!Enabled || s_activePhase == null) return;
            if (!s_traces.TryGetValue(coord, out ChunkTrace trace)) return;

            trace.Disposition = disposition;
            CloseTrace(trace);
            s_traces.Remove(coord);
        }

        #endregion

        #region Per-frame admission pressure (FP-2 hook targets)

        /// <summary>
        /// Records why a budgeted pass stopped this frame. Tallied exactly (never windowed) and folded into
        /// the frame's sample.
        /// </summary>
        /// <param name="pass">The pass that stopped.</param>
        /// <param name="reason">Why it stopped.</param>
        public static void RecordPassStop(PipelinePass pass, PassStopReason reason)
        {
            if (!Enabled || s_activePhase == null) return;

            // NotRun is the sample window's default, not an outcome — tallying it would invent frames in
            // which a pass "stopped" for a reason that is really an absence of data.
            if (reason == PassStopReason.NotRun) return;

            s_activePhase.StopReasonCounts[(int)pass, (int)reason]++;
            s_pendingFrame.StopReasons[pass] = reason;
        }

        /// <summary>
        /// Closes the frame: records queue depths and panic-gate state, then commits the frame's sample to
        /// the rolling window. Called once per <c>World.Update</c>, after every budgeted pass has run.
        /// </summary>
        /// <param name="generationQueueDepth">Requests awaiting admission.</param>
        /// <param name="generationInFlight">Generation jobs in flight.</param>
        /// <param name="lightReadyCount">Schedulable lighting backlog (the gate's signal).</param>
        /// <param name="lightWaitingCount">Parked lighting backlog.</param>
        /// <param name="meshQueueDepth">Chunks queued for a mesh rebuild.</param>
        /// <param name="gateOpen">Whether the panic gate admitted generation this frame.</param>
        public static void RecordFrame(int generationQueueDepth, int generationInFlight, int lightReadyCount,
            int lightWaitingCount, int meshQueueDepth, bool gateOpen)
        {
            if (!Enabled || s_activePhase == null) return;

            s_pendingFrame.GenerationQueueDepth = generationQueueDepth;
            s_pendingFrame.GenerationInFlight = generationInFlight;
            s_pendingFrame.LightReadyCount = lightReadyCount;
            s_pendingFrame.LightWaitingCount = lightWaitingCount;
            s_pendingFrame.MeshQueueDepth = meshQueueDepth;
            s_pendingFrame.GateOpen = gateOpen;

            s_activePhase.FrameCount++;
            if (!gateOpen) s_activePhase.GateClosedFrames++;

            List<AdmissionSample> window = s_activePhase.RecentFrames;
            if (window.Count < FRAME_WINDOW_CAPACITY)
            {
                window.Add(s_pendingFrame);
            }
            else
            {
                window[s_frameWindowCursor] = s_pendingFrame;
                s_frameWindowCursor = (s_frameWindowCursor + 1) % FRAME_WINDOW_CAPACITY;
                s_activePhase.FrameWindowWrapped = true;
            }

            s_pendingFrame = default;
        }

        #endregion

        /// <summary>
        /// Folds a closed trace into the active phase: counts its disposition and, when it completed,
        /// contributes its hop latencies to the percentile sample lists.
        /// </summary>
        /// <param name="trace">The trace being closed.</param>
        private static void CloseTrace(ChunkTrace trace)
        {
            s_activePhase.DispositionCounts[(int)trace.Disposition]++;

            if (trace.Disposition != TraceDisposition.MeshApplied) return;

            // Only fully-stamped chains yield latencies. A chunk can reach MeshApplied without a Lit stamp
            // when lighting is disabled, so each hop is contributed independently rather than all-or-nothing.
            AddSample(s_activePhase.RequestToPopulatedTicks, trace.RequestedTicks, trace.PopulatedTicks);
            AddSample(s_activePhase.PopulatedToLitTicks, trace.PopulatedTicks, trace.LitTicks);
            AddSample(s_activePhase.LitToMeshAppliedTicks, trace.LitTicks, trace.MeshAppliedTicks);
            AddSample(s_activePhase.RequestToMeshAppliedTicks, trace.RequestedTicks, trace.MeshAppliedTicks);
        }

        /// <summary>
        /// Appends one hop latency, skipping unstamped endpoints and flagging saturation instead of
        /// silently dropping past the cap.
        /// </summary>
        /// <param name="samples">The destination sample list.</param>
        /// <param name="fromTicks">Hop start timestamp, or 0 when unstamped.</param>
        /// <param name="toTicks">Hop end timestamp, or 0 when unstamped.</param>
        private static void AddSample(List<long> samples, long fromTicks, long toTicks)
        {
            if (fromTicks <= 0 || toTicks < fromTicks) return;

            if (samples.Count >= MAX_LATENCY_SAMPLES)
            {
                s_activePhase.SamplesSaturated = true;
                return;
            }

            samples.Add(toTicks - fromTicks);
        }
    }
}
