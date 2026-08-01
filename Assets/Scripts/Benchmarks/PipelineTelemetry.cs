using System;
using System.Collections.Generic;
using System.Diagnostics;
using Data;
using Helpers;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Benchmarks
{
    /// <summary>How a chunk's traced lifecycle ended.</summary>
    public enum TraceDisposition : byte
    {
        /// <summary>Still in flight — no terminal event has been stamped yet.</summary>
        Pending = 0,

        /// <summary>Reached the terminal stage: its mesh was applied, and it is on screen.</summary>
        MeshApplied = 1,

        /// <summary>Its completed generation result was discarded — the chunk left the unload boundary mid-flight.</summary>
        DiscardedOutOfRange = 2,

        /// <summary>
        /// <b>Retired (FP-7d) — never produced.</b> Intended to mark a completed disk load thrown away when
        /// the chunk was unloaded or pool-recycled mid-read. Unreachable as intended: the only site that
        /// removes a chunk from <c>WorldData</c> is <c>World.UnloadChunks</c>, which stamps
        /// <see cref="UnloadedBeforeMeshApplied"/> and closes the trace <i>first</i>, so the post-await guard
        /// could only ever find a live trace on the pool-ABA arm — where the trace belongs to the successor
        /// placeholder, not the load that was stranded. Kept (rather than renumbered away) so disposition
        /// tables in reports across the FP-7 boundary still line up column-for-column.
        /// </summary>
        LoadStranded = 3,

        /// <summary>Superseded by a fresh request for the same coord before finishing (design §4.1 flush-and-restart).</summary>
        Rerequested = 4,

        /// <summary>
        /// The phase ended while the chunk was still in flight. Not waste — the capture simply stopped first.
        /// Kept distinct so an unfinished chunk is never miscounted as discarded work.
        /// </summary>
        InFlightAtPhaseEnd = 5,

        /// <summary>
        /// Unloaded mid-flight: the chunk left the unload boundary before its mesh was ever applied, so every
        /// stage it did complete was thrown away. <b>This is waste, and it is the ordering-bound signal</b> —
        /// work the pipeline finished for a chunk the player had already flown past. Distinct from
        /// <see cref="InFlightAtPhaseEnd"/> (which is explicitly *not* waste) because folding the two would
        /// let genuine churn hide behind "the capture just stopped first".
        /// </summary>
        UnloadedBeforeMeshApplied = 6,

        /// <summary>
        /// Requested, then unloaded <b>before it was ever admitted</b> — no stage ran and no work was
        /// performed. <b>Not waste</b>, and deliberately excluded from the waste fraction's denominator too
        /// (FP-7a): that fraction means "of the work the pipeline completed, how much was thrown away", and a
        /// request that never entered the pipeline is not in that population at either end. Split out from
        /// <see cref="UnloadedBeforeMeshApplied"/> because folding the two inflates the ordering-bound signal
        /// with chunks the pipeline never touched — which is most severe exactly where the panic gate holds
        /// admissions back, i.e. the regime the capture exists to weigh.
        /// </summary>
        AbandonedBeforeAdmission = 7,
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
        /// Per-pass: true when that pass reported a stop reason more than once in a single frame, which the
        /// §7.1 v2 participation denominator assumes never happens. Sticky, and set in <b>every</b> build —
        /// the skew it records (that pass voting with double weight) leaves no other trace in the report, and
        /// a release capture is exactly where the runtime ordering that causes it could first differ.
        /// </summary>
        public readonly bool[] PassDoubleRecorded = new bool[PipelineTelemetry.PassCount];

        /// <summary>
        /// Whether a §7.1 regime verdict is meaningful for this phase at all. <c>false</c> for phases that
        /// are not measurements — the drain/save/unload transition being the one that exists today (FP-9a).
        /// <para>
        /// Distinct from a sample-size problem and not fixable by one: FP-8's transition carried ~1 332
        /// eligible observations, comfortably over
        /// <see cref="PipelineRegimeVerdict.MinRegimeObservations"/>, and still printed
        /// <c>AdmissionBound</c> for a phase whose entire job is to drain queues and unload. A pass being
        /// quota-limited while deliberately flushing is not a regime; it is the point.
        /// </para>
        /// </summary>
        public bool RegimeBearing = true;

        /// <summary>Whether any pass double-reported — the report's integrity banner condition.</summary>
        public bool AnyPassDoubleRecorded
        {
            get
            {
                foreach (bool doubled in PassDoubleRecorded)
                    if (doubled)
                        return true;

                return false;
            }
        }

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
        public const int DispositionCount = 8;

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

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        // Diagnostic latches for the two RecordPassStop asserts — readonly (never reassigned, so UDR0002
        // does not apply) but their CONTENTS are session state, so DomainReset clears them like s_traces.
        private static readonly bool[,] s_capabilityWarned = new bool[PassCount, StopReasonCount];
        private static readonly bool[] s_doubleRecordWarned = new bool[PassCount];
#endif

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
        /// Discards every record from any previous capture, so a run reports only its own phases. Must be
        /// called at the start of each benchmark run, before <see cref="Enabled"/> is set — and deliberately
        /// is <b>not</b> guarded by <see cref="Enabled"/>, since at that moment the layer is still off.
        /// <para>
        /// Play-mode entry alone is not a sufficient reset point (FP-5): a second run inside one process
        /// otherwise appends to the first run's <see cref="CompletedPhases"/>, and the report — which reads
        /// that list wholesale — presents the earlier run's phases as its own. The frame-health collector is
        /// rebuilt per run, so without this the two recorders disagree about where a run begins.
        /// </para>
        /// </summary>
        /// <remarks>Also leaves the layer <see cref="Enabled"/><c> == false</c>; the caller enables it
        /// explicitly once the run is ready to record.</remarks>
        public static void BeginRun() => DomainReset();

        /// <summary>
        /// Clears all static state on play-mode entry so a capture left <see cref="Enabled"/> (or holding a
        /// previous session's phases) never leaks into the next when domain reload is disabled. Mirrors the
        /// <c>DomainReset</c> convention used by <see cref="WorldFrameProfiler"/> and
        /// <see cref="PerformanceMonitor"/>. Also the body of <see cref="BeginRun"/>.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            // The full reset lives HERE, not in a shared helper both entry points call: UDR0002 requires
            // every mutable static to be assigned lexically inside the [RuntimeInitializeOnLoadMethod], and
            // delegating outward trips it on s_activePhase. So BeginRun calls this, not the reverse — do not
            // "tidy" the body into a helper without re-checking the analyzer.
            Enabled = false;
            s_traces.Clear();
            s_completedPhases.Clear();
            s_activePhase = null;
            s_phaseStartTime = 0f;
            s_traceCapacity = MIN_TRACE_CAPACITY;
            s_frameWindowCursor = 0;
            s_pendingFrame = default;

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            // Re-arm the diagnostic latches: a divergence already reported in a previous run must be
            // reported again in the next, or a repeat capture would run silently on a known-bad matrix.
            Array.Clear(s_capabilityWarned, 0, s_capabilityWarned.Length);
            Array.Clear(s_doubleRecordWarned, 0, s_doubleRecordWarned.Length);
#endif
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
        /// <param name="regimeBearing">
        /// <c>false</c> for a phase that is not a measurement (the drain/save/unload transition), so the
        /// report withholds a regime verdict instead of describing a deliberate flush as a pipeline state.
        /// Optional so every existing call site keeps the measurement default.
        /// </param>
        public static void BeginPhase(string phaseName, string groupName, int expectedTraceCapacity,
            bool regimeBearing = true)
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
                RegimeBearing = regimeBearing,
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
                // Admission is the discriminator between the two ways a coord can already be traced.
                //
                // NOT yet admitted: CheckViewDistance clears and REBUILDS the whole request queue on every
                // boundary crossing, so an un-admitted request is re-enqueued on each crossing. That is the
                // same logical request, not a new one — restarting it would measure latency from the LAST
                // crossing instead of the first request, biasing every latency downward precisely as the
                // crossing rate rises with speed. That is the regime under investigation, so the bias would
                // corrupt the capture's central question. Keep the original stamp; count nothing.
                if (existing.AdmittedTicks == 0) return;

                // Admitted but never finished: the journey entered the pipeline and died without a terminal
                // hook (unloaded mid-flight). THIS is the §4.1 flush-and-restart case, and the count of these
                // is the design's re-request / wave-front-churn metric.
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
        /// <remarks>
        /// FP-11a's tour-coverage marking runs <i>above</i> the <see cref="Enabled"/> guard on purpose:
        /// coverage must accrue across the whole run, including the gaps between phases, whereas the trace
        /// table only records inside an active phase.
        /// </remarks>
        public static void StampPopulated(ChunkCoord coord)
        {
            BenchmarkTourCoverage.MarkPopulated(coord);

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

        /// <summary>
        /// Stamps a chunk leaving memory, choosing between the two unload endings from the trace itself:
        /// a journey that was never admitted ended before any stage ran
        /// (<see cref="TraceDisposition.AbandonedBeforeAdmission"/>), while an admitted one had work thrown
        /// away (<see cref="TraceDisposition.UnloadedBeforeMeshApplied"/> — the ordering-bound signal).
        /// <para>
        /// The discrimination lives here rather than at the call site because <c>AdmittedTicks</c> is trace
        /// state the engine cannot see (FP-7a). Passing the wrong one of the two is what let requests the
        /// panic gate never admitted count as completed-then-discarded work.
        /// </para>
        /// </summary>
        /// <param name="coord">The chunk being unloaded.</param>
        public static void StampUnloaded(ChunkCoord coord)
        {
            if (!Enabled || s_activePhase == null) return;
            if (!s_traces.TryGetValue(coord, out ChunkTrace trace)) return;

            trace.Disposition = trace.AdmittedTicks == 0
                ? TraceDisposition.AbandonedBeforeAdmission
                : TraceDisposition.UnloadedBeforeMeshApplied;

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

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            // §7.1 v2 weights each reason by which passes can express it, so a stale capability declaration
            // silently mis-weights every verdict. FP-7b was exactly that failure — a pass documented as
            // ceiling-only had carried a quota stop for two captures. Assert the declaration against what
            // production actually records, so the next divergence surfaces on the frame it happens.
            //
            // Latched per (pass, reason): this runs once per pass per frame, so an unlatched error would
            // emit thousands of lines into the very log the capture is read from, burying the signal it is
            // trying to raise. Qualified `UnityEngine.Debug` — the file imports System.Diagnostics for
            // Stopwatch, so the bare name is ambiguous.
            if (!PipelineRegimeVerdict.CanEmit(pass, reason) && !s_capabilityWarned[(int)pass, (int)reason])
            {
                s_capabilityWarned[(int)pass, (int)reason] = true;
                Debug.LogError($"[PipelineTelemetry] {pass} recorded {reason}, which PipelineRegimeVerdict." +
                               "CanEmit says it cannot emit — the §7.1 v2 capability matrix is stale and " +
                               "every verdict weighted by it is wrong until it is corrected. (Reported once.)");
            }

            // Immediate console signal for the double record flagged below. The flag itself is set in every
            // build; only this log is development-only.
            if (s_pendingFrame.StopReasons[pass] != PassStopReason.NotRun
                && !s_doubleRecordWarned[(int)pass])
            {
                s_doubleRecordWarned[(int)pass] = true;
                Debug.LogError($"[PipelineTelemetry] {pass} recorded a stop reason twice in one " +
                               $"frame ({s_pendingFrame.StopReasons[pass]} then {reason}). The §7.1 v2 " +
                               "participation denominator assumes one report per pass per frame; this " +
                               "pass now votes with double weight and the verdict is skewed. (Reported once.)");
            }
#endif

            // The v2 denominator is each pass's MEASURED participation, summed straight from this matrix, so
            // it is only a frame count while a pass reports at most once per frame. Every current call site
            // obeys that, but only by enable-timing: ForceCompleteDataJobsCoroutine drives
            // ProcessGenerationJobs in a tight while-loop, and it is merely the case today that telemetry is
            // still off during startup.
            //
            // The symptom is NOT an out-of-range share — participation sums the same cells the numerator
            // draws from, so shares stay <= 1 by construction either way. It is that the offending pass
            // votes with DOUBLE WEIGHT in every reason it is eligible for, skewing the plurality with
            // nothing else in the report to show for it. Recorded as a sticky flag rather than a log so the
            // warning reaches the ARTIFACT in every build (the TracesSaturated pattern) — a release capture
            // is precisely where the runtime ordering could first differ, and precisely where no console is
            // being watched.
            if (s_pendingFrame.StopReasons[pass] != PassStopReason.NotRun)
                s_activePhase.PassDoubleRecorded[(int)pass] = true;

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
