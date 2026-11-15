namespace Diagnostics
{
    /// <summary>
    /// Running totals of the lighting merge's cross-chunk mod handling: the route's per-arm counts, the deferred drain,
    /// where effective applies came from, the removal support scans, a sampled split of the route's per-mod cost, and
    /// the opt-in per-job mod census. Counted with <see cref="PerfMergeTotals"/> (Systems tier and above); the census only
    /// while its owner enables it. Main thread only.
    /// </summary>
    public struct PerfRouteTotals
    {
        /// <summary>Cross-chunk mods the merged jobs emitted.</summary>
        public long Emitted;

        /// <summary>Mods the route dropped (out of world), persisted (target unloaded), deferred (target in flight) and applied directly.</summary>
        public long Drops, Persists, Defers, Direct;

        /// <summary>Deferred mods the drain applied.</summary>
        public long Drained;

        /// <summary>Applies that changed light, by source: the route's direct arm, the drain, the pull-back verifier.</summary>
        public long EffectiveDirect, EffectiveDrained, EffectivePullBack;

        /// <summary>Removal support scans (sky and block), and their time inside the route and inside the drain, in ticks.</summary>
        public long SupportScans, SupportTicks, SupportRouteTicks, SupportDrainTicks;

        /// <summary>Mods whose target chunk equals the previous mod's of the same job.</summary>
        public long SameTargetMods;

        /// <summary>Sampled mods, and their target-resolution and arm time, in ticks (an estimate of the per-mod split).</summary>
        public long Samples, SampleResolveTicks, SampleArmTicks;

        /// <summary>Census: mods counted, repeats of an earlier (voxel, channel) key, placements no brighter than an earlier one, removals.</summary>
        public long CensusMods, CensusRepeats, CensusRedundant, CensusRemovals;

        /// <summary>Adds another total into this one.</summary>
        /// <param name="o">The totals to fold in.</param>
        public void Add(in PerfRouteTotals o)
        {
            Emitted += o.Emitted;
            Drops += o.Drops;
            Persists += o.Persists;
            Defers += o.Defers;
            Direct += o.Direct;
            Drained += o.Drained;
            EffectiveDirect += o.EffectiveDirect;
            EffectiveDrained += o.EffectiveDrained;
            EffectivePullBack += o.EffectivePullBack;
            SupportScans += o.SupportScans;
            SupportTicks += o.SupportTicks;
            SupportRouteTicks += o.SupportRouteTicks;
            SupportDrainTicks += o.SupportDrainTicks;
            SameTargetMods += o.SameTargetMods;
            Samples += o.Samples;
            SampleResolveTicks += o.SampleResolveTicks;
            SampleArmTicks += o.SampleArmTicks;
            CensusMods += o.CensusMods;
            CensusRepeats += o.CensusRepeats;
            CensusRedundant += o.CensusRedundant;
            CensusRemovals += o.CensusRemovals;
        }

        /// <summary>The totals accumulated since an earlier reading of the same running totals.</summary>
        /// <param name="later">The later reading.</param>
        /// <param name="earlier">The earlier reading.</param>
        /// <returns>The difference.</returns>
        public static PerfRouteTotals Delta(in PerfRouteTotals later, in PerfRouteTotals earlier)
        {
            PerfRouteTotals d = later;
            d.Emitted -= earlier.Emitted;
            d.Drops -= earlier.Drops;
            d.Persists -= earlier.Persists;
            d.Defers -= earlier.Defers;
            d.Direct -= earlier.Direct;
            d.Drained -= earlier.Drained;
            d.EffectiveDirect -= earlier.EffectiveDirect;
            d.EffectiveDrained -= earlier.EffectiveDrained;
            d.EffectivePullBack -= earlier.EffectivePullBack;
            d.SupportScans -= earlier.SupportScans;
            d.SupportTicks -= earlier.SupportTicks;
            d.SupportRouteTicks -= earlier.SupportRouteTicks;
            d.SupportDrainTicks -= earlier.SupportDrainTicks;
            d.SameTargetMods -= earlier.SameTargetMods;
            d.Samples -= earlier.Samples;
            d.SampleResolveTicks -= earlier.SampleResolveTicks;
            d.SampleArmTicks -= earlier.SampleArmTicks;
            d.CensusMods -= earlier.CensusMods;
            d.CensusRepeats -= earlier.CensusRepeats;
            d.CensusRedundant -= earlier.CensusRedundant;
            d.CensusRemovals -= earlier.CensusRemovals;
            return d;
        }

        /// <summary>Whether the per-arm counts add up to the mods emitted.</summary>
        public bool Reconciles => Emitted == Drops + Persists + Defers + Direct;
    }
}
