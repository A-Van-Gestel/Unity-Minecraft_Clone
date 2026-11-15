using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Diagnostics
{
    /// <summary>
    /// One launch's startup-coroutine split: phase 1 (generation) iterations and quota stops, each lighting sweep's
    /// recalc / schedule / wait / merge laps, and the <c>StartWorld</c> tail before the handoff. Recorded at every
    /// monitor tier in <see cref="Stopwatch"/> ticks; the merge sub-split is live only where <see cref="PerfMergeTotals"/>
    /// counts (Systems tier and above). Main thread only; observes, never gates.
    /// </summary>
    public sealed class StartupPhaseTotals
    {
        /// <summary>Sweeps reported one by one; later sweeps fold into one overflow row.</summary>
        public const int MaxRecordedSweeps = 16;

        private const double MILLISECONDS_PER_SECOND = 1000.0;

        /// <summary>One lighting sweep of the startup coroutine.</summary>
        public struct Sweep
        {
            /// <summary>Step 2a: skylight recalc + initial-lighting clear, in ticks.</summary>
            public long RecalcTicks;

            /// <summary>Step 2b: the schedule loop, <see cref="AcquireTicks"/> included, in ticks.</summary>
            public long ScheduleTicks;

            /// <summary>Allocating and filling the jobs' snapshot maps and padded volumes, in ticks.</summary>
            public long AcquireTicks;

            /// <summary>Step 2c: waiting on every scheduled job's <c>Complete()</c>, in ticks.</summary>
            public long CompleteTicks;

            /// <summary>Step 2c: the production merge of every completed job, in ticks.</summary>
            public long MergeTicks;

            /// <summary>The merge's apply / drain / route / cascade parts, in ticks (Systems tier and above).</summary>
            public long MergeApplyTicks, MergeDrainTicks, MergeRouteTicks, MergeCascadeTicks;

            /// <summary>Chunks recalculated in 2a.</summary>
            public int Recalcs;

            /// <summary>Lighting jobs scheduled in 2b.</summary>
            public int Jobs;

            /// <summary>Cross-chunk light mods the merge routed, and those that changed light.</summary>
            public int CrossRouted, CrossEffective;

            /// <summary>Gen-0 collections during the sweep.</summary>
            public int Collections;

            /// <summary>Shared snapshot maps the sweep filled (voxel + light); 0 when each job copied its own.</summary>
            public int SharedSnapshots;

            /// <summary>The merge's cross-chunk route split (Systems tier and above; census only when enabled).</summary>
            public PerfRouteTotals Route;

            /// <summary>FNV-1a over the chunks the sweep merged, in merge order, and how many it merged; not folded by <see cref="Add"/>.</summary>
            public ulong MergeOrderHash;

            /// <inheritdoc cref="MergeOrderHash"/>
            public int MergeOrderJobs;

            /// <summary>Adds another sweep's totals into this one.</summary>
            /// <param name="other">The sweep to fold in.</param>
            public void Add(in Sweep other)
            {
                RecalcTicks += other.RecalcTicks;
                ScheduleTicks += other.ScheduleTicks;
                AcquireTicks += other.AcquireTicks;
                CompleteTicks += other.CompleteTicks;
                MergeTicks += other.MergeTicks;
                MergeApplyTicks += other.MergeApplyTicks;
                MergeDrainTicks += other.MergeDrainTicks;
                MergeRouteTicks += other.MergeRouteTicks;
                MergeCascadeTicks += other.MergeCascadeTicks;
                Recalcs += other.Recalcs;
                Jobs += other.Jobs;
                CrossRouted += other.CrossRouted;
                CrossEffective += other.CrossEffective;
                Collections += other.Collections;
                SharedSnapshots += other.SharedSnapshots;
                Route.Add(other.Route);
            }
        }

        /// <summary>The tail's clouds initialization split by part, each part with its gen-0 collections.</summary>
        public struct CloudsSplit
        {
            /// <summary>
            /// Pattern noise job (or the wait for a prebuild), coverage threshold + grid fill, tile meshing (or creating
            /// the prebuilt meshes), tile placement, in ticks.
            /// </summary>
            public long PatternTicks, ThresholdTicks, MeshTicks, TileTicks;

            /// <summary>Tile meshes built, and tile GameObjects created (pool misses).</summary>
            public int MeshesBuilt, TilesCreated;

            /// <summary>Gen-0 collections per part.</summary>
            public int PatternCollections, ThresholdCollections, MeshCollections, TileCollections;

            /// <summary>Gen-0 collections over the whole initialization.</summary>
            public int Collections;
        }

        private readonly Sweep[] _sweeps = new Sweep[MaxRecordedSweeps];
        private Sweep _overflow;

        /// <summary>Whether the coroutine ran to its report.</summary>
        public bool Recorded;

        /// <summary>Phase 1 iterations (one per frame).</summary>
        public int GenerationIterations;

        /// <summary>Iterations whose generation pass stopped on the structure-mods quota.</summary>
        public int GenerationQuotaStops;

        /// <summary>Generation jobs the passes processed, and the structure mods they queued.</summary>
        public long GenerationJobs, GenerationStructureMods;

        /// <summary>Phase 1 wall time including the yields, in ticks.</summary>
        public long GenerationWallTicks;

        /// <summary>Phase 1's main-thread <c>ProcessGenerationJobs</c> and <c>ApplyModifications</c> time, in ticks.</summary>
        public long GenerationPassTicks, GenerationApplyTicks;

        /// <summary>Gen-0 collections during phase 1.</summary>
        public int GenerationCollections;

        /// <summary>Lighting sweeps run.</summary>
        public int SweepCount { get; private set; }

        /// <summary>The <c>StartWorld</c> tail: coroutine report, spawn resolve, clouds, world-border wall, in ticks.</summary>
        public long TailReportTicks, TailSpawnTicks, TailCloudsTicks, TailBorderWallTicks;

        /// <summary>Gen-0 collections in the tail.</summary>
        public int TailCollections;

        /// <summary>The clouds part of the tail, split; "other" in the report is the rest of <see cref="TailCloudsTicks"/>.</summary>
        public CloudsSplit TailClouds;

        /// <summary>Records one finished sweep.</summary>
        /// <param name="sweep">The sweep's totals.</param>
        public void AddSweep(in Sweep sweep)
        {
            if (SweepCount < MaxRecordedSweeps) _sweeps[SweepCount] = sweep;
            else _overflow.Add(sweep);
            SweepCount++;
        }

        /// <summary>A recorded sweep, or the overflow row at <see cref="MaxRecordedSweeps"/>.</summary>
        /// <param name="index">The sweep index, 0-based.</param>
        /// <returns>The sweep's totals.</returns>
        public Sweep GetSweep(int index) => index < MaxRecordedSweeps ? _sweeps[index] : _overflow;

        /// <summary>Converts <see cref="Stopwatch"/> ticks to milliseconds.</summary>
        /// <param name="ticks">The tick count.</param>
        /// <returns>The milliseconds.</returns>
        public static double TicksToMs(long ticks) => ticks * MILLISECONDS_PER_SECOND / Stopwatch.Frequency;

        /// <summary>Converts milliseconds to <see cref="Stopwatch"/> ticks.</summary>
        /// <param name="ms">The milliseconds.</param>
        /// <returns>The tick count.</returns>
        public static long MsToTicks(double ms) => (long)(ms * Stopwatch.Frequency / MILLISECONDS_PER_SECOND);

        /// <summary>Appends the "Startup coroutine" section, culture-invariant, one line per sweep.</summary>
        /// <param name="sb">The report to append to.</param>
        public void AppendReport(StringBuilder sb)
        {
            sb.AppendLine("=== Startup Coroutine ===");
            if (!Recorded)
            {
                sb.AppendLine("not recorded (the coroutine did not reach its report)");
                return;
            }

            sb.AppendLine(Inv($"Phase 1:          {TicksToMs(GenerationWallTicks):F1} ms wall over {GenerationIterations} iterations, ") +
                              Inv($"{GenerationQuotaStops} quota-stopped; main thread pass {TicksToMs(GenerationPassTicks):F1} ms + ") +
                              Inv($"apply mods {TicksToMs(GenerationApplyTicks):F1} ms; {GenerationJobs} jobs, ") +
                              Inv($"{GenerationStructureMods} structure mods; {GenerationCollections} collections"));

            Sweep total = default;
            int rows = SweepCount < MaxRecordedSweeps ? SweepCount : MaxRecordedSweeps;
            for (int i = 0; i < rows; i++)
            {
                AppendSweep(sb, $"Sweep {i + 1,-2}", _sweeps[i]);
                total.Add(_sweeps[i]);
            }

            if (SweepCount > MaxRecordedSweeps)
            {
                AppendSweep(sb, $"Sweeps {MaxRecordedSweeps + 1}+", _overflow);
                total.Add(_overflow);
            }

            AppendSweep(sb, "Sweeps total", total);
            sb.AppendLine(Inv($"Tail:             report {TicksToMs(TailReportTicks):F1} ms, spawn {TicksToMs(TailSpawnTicks):F1} ms, ") +
                              Inv($"clouds {TicksToMs(TailCloudsTicks):F1} ms, border wall {TicksToMs(TailBorderWallTicks):F1} ms; ") +
                              Inv($"{TailCollections} collections"));

            CloudsSplit c = TailClouds;
            long otherTicks = Math.Max(0L, TailCloudsTicks - c.PatternTicks - c.ThresholdTicks - c.MeshTicks - c.TileTicks);
            sb.AppendLine(Inv($"Clouds:           pattern {TicksToMs(c.PatternTicks):F1} ms, threshold {TicksToMs(c.ThresholdTicks):F1} ms, ") +
                              Inv($"meshes {TicksToMs(c.MeshTicks):F1} ms ({c.MeshesBuilt} built), tiles {TicksToMs(c.TileTicks):F1} ms ") +
                              Inv($"({c.TilesCreated} created), other {TicksToMs(otherTicks):F1} ms; {c.Collections} collections ") +
                              Inv($"(pattern {c.PatternCollections}, threshold {c.ThresholdCollections}, meshes {c.MeshCollections}, tiles {c.TileCollections})"));

            if (SweepCount > 0) AppendRoute(sb, "Route sweep 1", _sweeps[0].Route);
            AppendRoute(sb, "Route total", total.Route);
        }

        /// <summary>Appends each recorded sweep's merge-order hash and merge count, culture-invariant, on one line.</summary>
        /// <param name="sb">The text to append to.</param>
        public void AppendMergeOrder(StringBuilder sb)
        {
            int rows = SweepCount < MaxRecordedSweeps ? SweepCount : MaxRecordedSweeps;
            for (int i = 0; i < rows; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Inv($"s{i + 1} {_sweeps[i].MergeOrderHash:X16} ({_sweeps[i].MergeOrderJobs})"));
            }
        }

        // The route split: where the emitted mods went, where effective applies came from, the removal scans, and the
        // sampled share of per-mod time spent resolving the target (an estimate).
        private static void AppendRoute(StringBuilder sb, string label, in PerfRouteTotals r)
        {
            double sameTarget = r.Emitted > 0 ? 100.0 * r.SameTargetMods / r.Emitted : 0.0;
            long sampled = r.SampleResolveTicks + r.SampleArmTicks;
            double resolveShare = sampled > 0 ? 100.0 * r.SampleResolveTicks / sampled : 0.0;
            string census = r.CensusMods > 0
                ? Inv($"census {r.CensusMods} mods, {r.CensusRepeats} repeats, {r.CensusRedundant} redundant, {r.CensusRemovals} removals")
                : "census off";
            sb.AppendLine(Inv($"{label,-17} emitted {r.Emitted} = drop {r.Drops} + persist {r.Persists} + defer {r.Defers} + direct {r.Direct} ") +
                              Inv($"({(r.Reconciles ? "OK" : "MISMATCH")}); drained {r.Drained}; effective direct {r.EffectiveDirect} / ") +
                              Inv($"drained {r.EffectiveDrained} / pull-back {r.EffectivePullBack}; support {r.SupportScans} scans ") +
                              Inv($"{TicksToMs(r.SupportTicks):F1} ms (route {TicksToMs(r.SupportRouteTicks):F1}, drain {TicksToMs(r.SupportDrainTicks):F1}); ") +
                              Inv($"same-target {sameTarget:F1} %; resolve {resolveShare:F1} % of {r.Samples} samples (est.); {census}"));
        }

        private static void AppendSweep(StringBuilder sb, string label, in Sweep s)
        {
            double wall = TicksToMs(s.RecalcTicks + s.ScheduleTicks + s.CompleteTicks + s.MergeTicks);
            sb.AppendLine(Inv($"{label,-17} {wall:F1} ms: recalc {TicksToMs(s.RecalcTicks):F1} ({s.Recalcs}), ") +
                              Inv($"schedule {TicksToMs(s.ScheduleTicks):F1} (acquire {TicksToMs(s.AcquireTicks):F1}; {s.Jobs} jobs, {s.SharedSnapshots} shared maps), ") +
                              Inv($"wait {TicksToMs(s.CompleteTicks):F1}, merge {TicksToMs(s.MergeTicks):F1} ") +
                              Inv($"(apply {TicksToMs(s.MergeApplyTicks):F1}, drain {TicksToMs(s.MergeDrainTicks):F1}, ") +
                              Inv($"route {TicksToMs(s.MergeRouteTicks):F1}, cascade {TicksToMs(s.MergeCascadeTicks):F1}); ") +
                              Inv($"cross-chunk {s.CrossRouted} routed / {s.CrossEffective} effective; {s.Collections} collections"));
        }

        private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
    }
}
