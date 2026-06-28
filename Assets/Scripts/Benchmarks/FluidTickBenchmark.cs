#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Data;
using Serialization;
using UnityEngine;
using UnityEngine.InputSystem;
using Debug = UnityEngine.Debug;

namespace Benchmarks
{
    /// <summary>
    /// Isolated micro-benchmark for the <b>block-behavior tick</b> (grass + fluid), the workload TG-4 re-architects.
    /// It drives the <b>production</b> path — <see cref="Chunk.TickUpdate"/> → <see cref="BlockBehavior.Behave"/>/
    /// <see cref="BlockBehavior.Active"/> over the real per-family <see cref="ChunkData"/> buckets, then
    /// <c>World.ApplyModifications</c> — over hand-seeded interior chunks (see <see cref="FluidBenchmarkScenarios"/>),
    /// with no rendering, meshing, or lighting noise. Its purpose is the <b>TG-4 vs TG-5 profile gate</b>: quantify the
    /// main-thread cost per active voxel and how it scales, so the choice to jobify (Phase 2+) or take the lighter
    /// TG-5 finisher rests on data.
    /// <para>
    /// <b>World seam:</b> the benchmark owns its world — it creates its own inert <see cref="World"/> (see
    /// <see cref="CreateInertWorld"/>) and <b>refuses to run if a live <c>World.Instance</c> already exists</b>, so it
    /// can never register synthetic chunks into a real game world. It must therefore run only in its dedicated scene
    /// (no <c>World</c> present). The inert world's <c>Awake</c> wires <c>Instance</c>, the <c>ChunkPool</c>, and the
    /// job-safe block tables, but its <c>Update</c> stays inert (never <c>StartWorld</c>-ed, so <c>_isWorldLoaded</c>
    /// is false) — so this harness fully controls the tick cadence with zero interference. Seeded chunks are
    /// registered via <c>World.RegisterSyntheticChunk</c> and torn down via <c>World.ClearSyntheticChunks</c>.
    /// </para>
    /// <para>
    /// Modeled on <see cref="LightingJobBenchmark"/>: a discarded warm-up absorbs JIT/first-touch cost, then each
    /// scenario runs <c>_benchmarkRuns</c> times (rebuilt fresh each run) and the per-tick wall time is averaged. The
    /// report (system info + a results table) is logged and optionally written under
    /// <c>Application.persistentDataPath/Benchmarks/</c>. Per-family attribution is available separately via the
    /// <c>Chunk.TickUpdate.Grass/Fluid</c> and <c>World.ApplyModifications</c> profiler markers.
    /// </para>
    /// </summary>
    public class FluidTickBenchmark : MonoBehaviour
    {
        #region Serialized Fields

        [Header("Benchmark Configuration")]
        [Tooltip("Whether the benchmark is enabled and allowed to run.")]
        [SerializeField]
        private bool _benchmarkEnabled = true;

        [Tooltip("Number of measured runs per scenario. Each run yields one avg-ms/tick sample; the report aggregates " +
                 "their mean/min/median/stddev. More runs = finer resolution (the whole suite is sub-second); ~30–100 " +
                 "is the sweet spot before returns diminish.")]
        [SerializeField]
        [Min(1)]
        private int _benchmarkRuns = 30;

        [Tooltip("Discarded warm-up ticks run before measurement each run, to absorb Burst/JIT and first-touch cost.")]
        [SerializeField]
        [Min(0)]
        private int _warmupTicks = 2;

        [Tooltip("TG-4 Phase 4b A/B: add the managed-border leg (Enable Fluid Border Burst OFF = the Phase-3/4a " +
                 "hybrid) alongside the halo leg(s), to measure the serial main-thread gather cost vs the managed " +
                 "border. Requires Enable Fluid Burst Tick. Off = halo leg(s) only.")]
        [SerializeField]
        private bool _sweepBorderBurst = true;

        [Tooltip("TG-4 Phase 4b Y-band A/B: split the halo leg into full-height vs Y-band (Enable Fluid Band Gather " +
                 "OFF then ON) to measure the band's per-tick copy reduction. On = the band's primary A/B (halo-full " +
                 "vs halo-band); Off = a single halo leg at the world's current band flag.")]
        [SerializeField]
        private bool _sweepBandGather = true;

        [Header("Execution Settings")]
        [Tooltip("If checked, the benchmark runs automatically when the scene starts.")]
        [SerializeField]
        private bool _runOnStart = true;

        [Tooltip("If checked, the report (rich-text stripped) is written to a timestamped file under " +
                 "Application.persistentDataPath/Benchmarks/. The path is logged after the run.")]
        [SerializeField]
        private bool _writeReportToFile = true;

        [Header("Keybinding")]
        [Tooltip("Press this key to manually trigger the benchmark.")]
        [SerializeField]
        private Key _triggerKey = Key.F;

        #endregion

        #region Private Fields

        private bool _isBenchmarking;
        private World _ownWorld;

        // TG-4 Phase 4b A/B: World._enableFluidBorderBurst / _enableFluidBandGather are private [SerializeField]s; the
        // benchmark flips them per leg to compare the managed-border hybrid vs the full halo vs the Y-band halo.
        // Reflection (not a public setter) keeps these flags dev-only — this file is already gated to
        // UNITY_EDITOR || DEVELOPMENT_BUILD.
        private static readonly FieldInfo s_borderBurstField =
            typeof(World).GetField("_enableFluidBorderBurst", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo s_bandGatherField =
            typeof(World).GetField("_enableFluidBandGather", BindingFlags.Instance | BindingFlags.NonPublic);

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            if (!_benchmarkEnabled) return;

            // Isolation guard (Option A): this benchmark owns its world. If a live World already exists, the scene is
            // a real game world — registering synthetic chunks into it would corrupt it — so refuse rather than touch
            // it. The benchmark is meant to run only in its dedicated scene, where no World is present.
            if (World.Instance != null)
            {
                Debug.LogError("FluidTickBenchmark must run in its own dedicated scene with no live World present. " +
                               "A World.Instance already exists — refusing to run so synthetic chunks cannot corrupt it.", this);
                enabled = false;
                return;
            }

            // Stand up our own inert World. The trick: create the GameObject inactive so AddComponent<World> DEFERS
            // Awake, force the component disabled so the World.Start → StartWorld full bootstrap never fires, then
            // activate — Awake now runs (loads the shared block database, Instance + ChunkPool + job-safe block
            // tables) while Start/Update stay dormant. A fully-wired World.Instance with zero tick-loop
            // interference, all owned and torn down by this benchmark.
            _ownWorld = CreateInertWorld();
        }

        private void Start()
        {
            if (!_benchmarkEnabled)
            {
                enabled = false;
                return;
            }

            if (World.Instance == null)
            {
                Debug.LogError("FluidTickBenchmark: no World.Instance (inert-world bootstrap failed — is " +
                               "Resources/Data/BlockDatabase present?).", this);
                enabled = false;
                return;
            }

            // Awake does not create worldData/settings (StartWorld does, and we never call it). Provide quiet
            // defaults so the production tick + ModifyVoxel path runs without the full world bootstrap, but never
            // clobber values a live/configured world already set.
            World.Instance.worldData ??= new WorldData("FluidTickBenchmark", 0);
            World.Instance.settings ??= new Settings { enableLighting = false, enableChunkLoadAnimations = false };

            if (_runOnStart)
                TriggerBenchmark();
        }

        private void Update()
        {
            if (Keyboard.current != null && Keyboard.current[_triggerKey].wasPressedThisFrame)
                TriggerBenchmark();
        }

        private void OnDestroy()
        {
            // Tear down the inert World we created (if any). World.OnDestroy disposes its own (mostly null) state safely.
            if (_ownWorld != null)
                Destroy(_ownWorld.gameObject);
        }

        /// <summary>
        /// Creates a disabled <see cref="World"/> whose <c>Awake</c> loads the shared block database — enough to
        /// initialize <c>Instance</c>, the chunk pool, and the block tables, but never the full <c>StartWorld</c> boot.
        /// </summary>
        /// <returns>The inert world component, or <c>null</c> if the block database could not be loaded.</returns>
        private World CreateInertWorld()
        {
            BlockDatabase db = Resources.Load<BlockDatabase>("Data/BlockDatabase");
            if (db == null)
            {
                Debug.LogError("FluidTickBenchmark: could not load BlockDatabase from Resources/Data/BlockDatabase.", this);
                return null;
            }

            GameObject go = new GameObject("FluidBench_InertWorld");
            go.SetActive(false); // defer Awake (the db pre-check above guards against the asset being missing)
            World world = go.AddComponent<World>();
            world.enabled = false; // Start()/StartWorld() must never run
            go.SetActive(true); // Awake runs now (loads the block database, Instance + ChunkPool + block tables)

            // ApplyModifications routes a mod whose target chunk isn't registered/populated through ModManager (a
            // dependency StartWorld normally builds). Interior scenarios never hit that branch, but wire a volatile
            // ModManager so a stray out-of-chunk mod degrades to a queued pending-mod rather than a null-deref.
            world.ModManager = new ModificationManager("FluidTickBenchmark", useVolatilePath: true);
            return world;
        }

        #endregion

        #region Orchestration

        /// <summary>Starts the full benchmark if one is not already running.</summary>
        private void TriggerBenchmark()
        {
            if (_isBenchmarking)
            {
                Debug.LogWarning("Fluid tick benchmark is already in progress.");
                return;
            }

            StartCoroutine(RunAllScenarios());
        }

        /// <summary>Runs every scenario in sequence, then generates the report.</summary>
        private IEnumerator RunAllScenarios()
        {
            _isBenchmarking = true;
            Debug.Log("--- Starting Fluid Tick Benchmark ---");

            Stopwatch totalStopwatch = Stopwatch.StartNew();
            List<ScenarioResult> results = new List<ScenarioResult>();

            // Build the A/B leg list once (managed-border hybrid, full halo, Y-band halo) per the sweep flags, so the
            // rows for each scenario sit adjacent for an at-a-glance delta. Flags are restored after the whole suite.
            bool originalBorderBurst = World.Instance.EnableFluidBorderBurst;
            bool originalBandGather = World.Instance.EnableFluidBandGather;
            List<BenchLeg> legs = BuildLegs();

            foreach (FluidScenario scenario in FluidBenchmarkScenarios.All())
            {
                foreach (BenchLeg leg in legs)
                {
                    ScenarioResult result = default;
                    yield return RunScenario(scenario, leg, r => result = r);
                    results.Add(result);
                }

                yield return null; // let a frame breathe between scenarios
            }

            // Restore both world flags after the sweep.
            SetBorderBurst(originalBorderBurst);
            SetBandGather(originalBandGather);

            totalStopwatch.Stop();
            Debug.Log("--- All Fluid Tick Runs Complete. Generating Report... ---");
            GenerateReport(results, totalStopwatch.Elapsed);
            _isBenchmarking = false;
        }

        /// <summary>
        /// Runs one scenario across <see cref="_benchmarkRuns"/> rebuilt runs (plus a discarded warm-up run) and
        /// reports the per-run avg-ms/tick distribution (mean/min/median/stddev), the single worst tick, and the peak
        /// active-voxel count. Each run's avg-ms/tick is one independent sample (ticks within a run are correlated —
        /// the fluid spreads then settles — so the run, not the tick, is the iid unit the statistics aggregate over).
        /// </summary>
        /// <param name="scenario">The scenario to measure.</param>
        /// <param name="leg">
        /// The A/B leg to measure under (its border-burst + band-gather flags + label). Set on <c>World</c> before the
        /// runs so the production <c>Chunk.TickUpdate</c> fluid path takes that exact configuration.
        /// </param>
        /// <param name="onComplete">Callback receiving the aggregated result.</param>
        private IEnumerator RunScenario(FluidScenario scenario, BenchLeg leg, Action<ScenarioResult> onComplete)
        {
            SetBorderBurst(leg.BorderBurst);
            SetBandGather(leg.BandGather);
            Debug.Log($"--- Running: {scenario.Name} [{leg.Label}] ({scenario.ChunkCount} chunk(s), {scenario.Ticks} ticks × {_benchmarkRuns} runs) ---");

            // Discarded warm-up run (absorbs JIT/first-touch; not measured). Runs the configurable _warmupTicks.
            {
                List<Chunk> warmup = BuildChunks(scenario);
                for (int t = 0; t < _warmupTicks; t++) StepAll(warmup);
                TeardownChunks(warmup);
            }

            List<double> runAvgsMs = new List<double>(_benchmarkRuns); // one avg-ms/tick sample per run (iid unit)
            double maxMsPerTick = 0; // single worst tick across all runs (outlier)
            int peakActive = 0;

            for (int run = 0; run < _benchmarkRuns; run++)
            {
                List<Chunk> chunks = BuildChunks(scenario);
                // Track the true PEAK active-voxel count over the whole run, not just the seeded start: fluid flow
                // grows the active set as the front spreads (neighbor re-activation in ApplyModifications), so the
                // start count understates it — and µs/voxel divides by this denominator.
                peakActive = Mathf.Max(peakActive, CountActive(chunks));

                double runTotalMs = 0;
                Stopwatch sw = new Stopwatch();
                for (int t = 0; t < scenario.Ticks; t++)
                {
                    sw.Restart();
                    StepAll(chunks);
                    sw.Stop();

                    double tickMs = sw.Elapsed.TotalMilliseconds;
                    runTotalMs += tickMs;
                    if (tickMs > maxMsPerTick) maxMsPerTick = tickMs;

                    // Sample after each tick (outside the stopwatch — CountActive is just a sum of bucket counts).
                    peakActive = Mathf.Max(peakActive, CountActive(chunks));
                }

                runAvgsMs.Add(runTotalMs / scenario.Ticks);
                TeardownChunks(chunks);
                yield return null; // keep the editor responsive between runs
            }

            DistributionStats stats = DistributionStats.From(runAvgsMs);
            // µs/voxel uses the MIN ms/tick — the GC-free, uninterrupted floor — so the headline per-voxel cost is the
            // clean CPU cost, not inflated by GC/OS-scheduler spikes (whose magnitude is shown separately by stddev/peak).
            double usPerVoxel = peakActive > 0 ? stats.Min * 1000.0 / peakActive : 0;

            onComplete(new ScenarioResult(scenario.Name, leg.Label, scenario.ChunkCount, peakActive, stats, maxMsPerTick, usPerVoxel));
        }

        /// <summary>One A/B leg: the World fluid-flag configuration to measure under, plus its report label.</summary>
        private readonly struct BenchLeg
        {
            public readonly bool BorderBurst;
            public readonly bool BandGather;
            public readonly string Label;

            public BenchLeg(bool borderBurst, bool bandGather, string label)
            {
                BorderBurst = borderBurst;
                BandGather = bandGather;
                Label = label;
            }
        }

        /// <summary>
        /// Builds the A/B leg list from the sweep flags. With both binds available: an optional managed-border leg
        /// (<see cref="_sweepBorderBurst"/>), then either the full-vs-band halo pair (<see cref="_sweepBandGather"/>)
        /// or a single halo leg at the world's current band flag. If neither sweep is on — or reflection couldn't bind
        /// the private flags — it degrades to a single leg at the world's current state (measured, never mutated).
        /// </summary>
        private List<BenchLeg> BuildLegs()
        {
            bool currentBorder = World.Instance.EnableFluidBorderBurst;
            bool currentBand = World.Instance.EnableFluidBandGather;

            // Degrade to measuring (never mutating) the world's current state when reflection can't bind the private
            // flags, or when no sweep is requested.
            bool canSweep = s_borderBurstField != null && s_bandGatherField != null;
            if (!canSweep)
                Debug.LogWarning("FluidTickBenchmark: could not bind World._enableFluidBorderBurst/_enableFluidBandGather " +
                                 "by reflection — measuring only the current flag state (a field may have been renamed).");

            if (!canSweep || (!_sweepBorderBurst && !_sweepBandGather))
                return new List<BenchLeg> { new BenchLeg(currentBorder, currentBand, "current") };

            List<BenchLeg> legs = new List<BenchLeg>(3);
            if (_sweepBorderBurst)
                legs.Add(new BenchLeg(false, false, "managed"));

            if (_sweepBandGather)
            {
                legs.Add(new BenchLeg(true, false, "halo-full"));
                legs.Add(new BenchLeg(true, true, "halo-band"));
            }
            else
            {
                legs.Add(new BenchLeg(true, currentBand, "halo"));
            }

            return legs;
        }

        /// <summary>Sets <c>World._enableFluidBorderBurst</c> by reflection (no-op if the field couldn't bind).</summary>
        private static void SetBorderBurst(bool value) => s_borderBurstField?.SetValue(World.Instance, value);

        /// <summary>Sets <c>World._enableFluidBandGather</c> by reflection (no-op if the field couldn't bind).</summary>
        private static void SetBandGather(bool value) => s_bandGatherField?.SetValue(World.Instance, value);

        /// <summary>One behavior step over all chunks: tick each (production path), then drain the mod queue.</summary>
        private static void StepAll(List<Chunk> chunks)
        {
            foreach (Chunk chunk in chunks)
                chunk.TickUpdate();

            World.Instance.ApplyModifications();
        }

        #endregion

        #region Chunk Setup / Teardown

        /// <summary>
        /// Builds <see cref="FluidScenario.ChunkCount"/> independent interior chunks, seeds each via the scenario,
        /// registers active voxels through the production scan (<see cref="Chunk.OnDataPopulated"/>), and wires each
        /// into the live world via <c>World.RegisterSyntheticChunk</c>.
        /// </summary>
        private static List<Chunk> BuildChunks(FluidScenario scenario)
        {
            List<Chunk> chunks = new List<Chunk>(scenario.ChunkCount);

            for (int i = 0; i < scenario.ChunkCount; i++)
            {
                ChunkCoord coord = new ChunkCoord(i, 0);

                ChunkData data = new ChunkData(coord.ToVoxelOrigin());
                scenario.Seed(data);
                data.IsPopulated = true;

                Chunk chunk = new Chunk(coord);
                // Chunk.Reset normally sets ChunkPosition, but we bypass Reset (it would re-fetch ChunkData from
                // worldData instead of using our seeded data). Set it explicitly: ApplyModifications' six-neighbor
                // re-activation converts global→local via Chunk.GetVoxelPositionInChunkFromGlobalVector3, which
                // subtracts ChunkPosition — without it, chunks at a non-zero origin (the multi-chunk scenarios)
                // read the wrong cell and mis-register neighbors.
                chunk.ChunkPosition = coord.ToWorldPosition();
                chunk.ChunkData = data;
                data.Chunk = chunk;

                // Production active-voxel scan → fills the real per-family NativeHashSet buckets (the structure
                // under measurement), exactly as the non-job populate path does.
                chunk.OnDataPopulated();

                World.Instance.RegisterSyntheticChunk(chunk);
                chunks.Add(chunk);
            }

            return chunks;
        }

        /// <summary>Disposes each chunk's data, fully destroys the chunk (GameObject + section-renderer meshes), and clears the world's synthetic registrations.</summary>
        private static void TeardownChunks(List<Chunk> chunks)
        {
            foreach (Chunk chunk in chunks)
            {
                chunk.ChunkData?.Dispose();
                // chunk.Destroy() also releases the per-section SectionRenderer meshes; destroying only the
                // ChunkGameObject would leak them across the many rebuilt runs.
                chunk.Destroy();
            }

            World.Instance.ClearSyntheticChunks();
        }

        /// <summary>Sums the active-voxel count across all chunks (grass + fluid buckets).</summary>
        private static int CountActive(List<Chunk> chunks)
        {
            int total = 0;
            foreach (Chunk chunk in chunks)
                total += chunk.GetActiveVoxelCount();
            return total;
        }

        #endregion

        #region Reporting

        /// <summary>
        /// Summary statistics over a set of per-run avg-ms/tick samples. <see cref="Min"/> is the clean,
        /// uninterrupted floor (best proxy for raw CPU cost); <see cref="Mean"/> includes GC/scheduler overhead;
        /// <see cref="StdDev"/> quantifies the spread (here dominated by per-voxel <c>List&lt;VoxelMod&gt;</c> GC —
        /// the cost TG-4 Phase 2 would remove).
        /// </summary>
        private readonly struct DistributionStats
        {
            public readonly double Mean;
            public readonly double Min;
            public readonly double Median;
            public readonly double StdDev;

            private DistributionStats(double mean, double min, double median, double stdDev)
            {
                Mean = mean;
                Min = min;
                Median = median;
                StdDev = stdDev;
            }

            /// <summary>
            /// Computes mean, min, median, and sample standard deviation (N−1) over <paramref name="samples"/>.
            /// Returns zeros for an empty set; <see cref="StdDev"/> is 0 for a single sample.
            /// </summary>
            /// <param name="samples">The per-run samples (not mutated; a copy is sorted for the median).</param>
            public static DistributionStats From(List<double> samples)
            {
                int n = samples.Count;
                if (n == 0) return new DistributionStats(0, 0, 0, 0);

                double sum = 0;
                double min = double.MaxValue;
                foreach (double s in samples)
                {
                    sum += s;
                    if (s < min) min = s;
                }

                double mean = sum / n;

                double sumSq = 0;
                foreach (double s in samples)
                {
                    double d = s - mean;
                    sumSq += d * d;
                }

                double stdDev = n > 1 ? Math.Sqrt(sumSq / (n - 1)) : 0;

                List<double> sorted = new List<double>(samples);
                sorted.Sort();
                double median = n % 2 == 1
                    ? sorted[n / 2]
                    : (sorted[n / 2 - 1] + sorted[n / 2]) * 0.5;

                return new DistributionStats(mean, min, median, stdDev);
            }
        }

        /// <summary>The aggregated measurement for one scenario.</summary>
        private readonly struct ScenarioResult
        {
            public readonly string Name;
            public readonly string Leg; // "managed" / "halo-full" / "halo-band" (or "halo"/"current") — the A/B leg this row measures
            public readonly int ChunkCount;
            public readonly int PeakActive;
            public readonly DistributionStats Stats;
            public readonly double PeakMsPerTick;
            public readonly double UsPerVoxel;

            public ScenarioResult(string name, string leg, int chunkCount, int peakActive,
                DistributionStats stats, double peakMsPerTick, double usPerVoxel)
            {
                Name = name;
                Leg = leg;
                ChunkCount = chunkCount;
                PeakActive = peakActive;
                Stats = stats;
                PeakMsPerTick = peakMsPerTick;
                UsPerVoxel = usPerVoxel;
            }
        }

        /// <summary>Logs (and optionally writes) the system info + results table.</summary>
        /// <param name="results">Per-scenario aggregated results.</param>
        /// <param name="totalElapsed">Wall-clock time of the whole benchmark.</param>
        private void GenerateReport(List<ScenarioResult> results, TimeSpan totalElapsed)
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("<color=cyan><b>--- FLUID / BEHAVIOR TICK BENCHMARK REPORT ---</b></color>");
            report.AppendLine($"Measured over {_benchmarkRuns} run(s) per scenario, {_warmupTicks} warm-up tick(s) discarded.");
            report.AppendLine("Path: production Chunk.TickUpdate (per-family buckets) + World.ApplyModifications. " +
                              "Isolated — no render/mesh/light.");
            report.AppendLine("ms/tick columns are over per-run avg-ms/tick samples (one per run). min = clean floor; " +
                              "mean includes GC/scheduler cost; stddev = spread (mostly per-voxel mod-list GC); peak = worst single tick.");
            report.AppendLine("µs/voxel = MIN ms/tick × 1000 ÷ peak active voxels (clean per-voxel cost, GC-spike-free).");
            report.AppendLine("Leg = the A/B config: 'managed' = Phase-3/4a hybrid (border fluids on the managed path); " +
                              "'halo-full' = Phase-4b full Burst halo (every fluid Bursted, border voxels reading the " +
                              "per-tick 9-snapshot neighbor halo, gathered full chunk height); 'halo-band' = the same " +
                              "halo with the gather/read window sized to the active-fluid Y-band. Serial cost — the " +
                              "benchmark drives Chunk.TickUpdate per chunk (not the parallel pass), so halo-full→managed " +
                              "is the added gather cost and halo-band→halo-full is the band's per-tick copy reduction.");
            report.AppendLine($"Total wall-clock runtime: {BenchmarkEnvironment.FormatDuration(totalElapsed)}");
            report.AppendLine();
            report.Append(BenchmarkEnvironment.DescribeSystem());
            report.AppendLine("=== Results ===");
            report.AppendLine();

            ReportTable table = new ReportTable(
                "Scenario", "Leg", "Chunks", "PeakActive", "mean", "min", "median", "stddev", "peak", "µs/voxel");

            foreach (ScenarioResult r in results)
            {
                table.AddRow(
                    r.Name,
                    r.Leg,
                    r.ChunkCount.ToString(),
                    r.PeakActive.ToString(),
                    r.Stats.Mean.ToString("F3"),
                    r.Stats.Min.ToString("F3"),
                    r.Stats.Median.ToString("F3"),
                    r.Stats.StdDev.ToString("F3"),
                    r.PeakMsPerTick.ToString("F3"),
                    r.UsPerVoxel.ToString("F3"));
            }

            table.AppendTo(report);
            report.AppendLine();

            string fullReport = report.ToString();
            Debug.Log(fullReport);

            if (_writeReportToFile)
                BenchmarkEnvironment.WriteReportToDisk(fullReport, "FluidTickBenchmark");
        }

        #endregion
    }
}
#endif
