using System.Reflection;
using Data;
using Diagnostics;
using Editor.Validation.Behavior.Framework;
using Helpers;
using Jobs.BurstData;
using Unity.Collections;
using UnityEngine;

namespace Editor.Validation.PerfMonitoring
{
    /// <summary>
    /// The lighting-merge half of the suite: <see cref="PerfMergeTotals"/> sampled into the <c>Merge*</c> and
    /// <c>Unload*</c> counters, the band predicate behind the out-of-band count, and the production
    /// <see cref="ChunkData.ApplyJobLightMap(NativeArray{uint}, NativeArray{ushort}, BlockType[], int, int, ref PerfMergeTotals)"/>
    /// compacting a uniform-lit absent section without churn, and its instrumented-build block-count verifier (the
    /// Editor compiles with <c>UNITY_INCLUDE_INSTRUMENTATION</c>). World-free: no suite stands up the production lighting merge, so the probes
    /// in <c>WorldJobManager.MergeCompletedLightingJob</c> are not reached here.
    /// </summary>
    public static partial class PerfMonitorValidationSuite
    {
        /// <summary>The section the scenario lights uniformly while it is absent — compacted without a rent, so no churn.</summary>
        private const int CHURN_SECTION = 3;

        private const byte FULL_SKY = 15;
        private const byte DIM_SKY = 10;

        /// <summary>A band covering rows 40–89: sections 2–5 are partly or wholly inside, 0, 1, 6 and 7 are outside.</summary>
        private const int BAND_MIN_Y = 40;

        private const int BAND_END_Y = 90;
        private const int SECTIONS_OUTSIDE_BAND = 4;

        /// <summary>Spacing between the distinct values the mapping check gives each totals field.</summary>
        private const long FIELD_VALUE_STEP = 100_000L;

        /// <summary>Each totals field and the counter it must reach; whether it is converted from ticks to microseconds.</summary>
        private static readonly (string Field, PerfCounter Counter, bool Ticks)[] s_mergeCounterMap =
        {
            (nameof(PerfMergeTotals.ApplyTicks), PerfCounter.MergeApplyUs, true),
            (nameof(PerfMergeTotals.CountsTicks), PerfCounter.MergeCountsUs, true),
            (nameof(PerfMergeTotals.DrainTicks), PerfCounter.MergeDrainUs, true),
            (nameof(PerfMergeTotals.PullBackTicks), PerfCounter.MergePullBackUs, true),
            (nameof(PerfMergeTotals.RouteTicks), PerfCounter.MergeRouteUs, true),
            (nameof(PerfMergeTotals.CascadeTicks), PerfCounter.MergeCascadeUs, true),
            (nameof(PerfMergeTotals.Merges), PerfCounter.MergeJobs, false),
            (nameof(PerfMergeTotals.StableMerges), PerfCounter.MergeStable, false),
            (nameof(PerfMergeTotals.StableUnchangedMerges), PerfCounter.MergeStableUnchanged, false),
            (nameof(PerfMergeTotals.SelfRemeshes), PerfCounter.MergeSelfRemeshes, false),
            (nameof(PerfMergeTotals.NeighborRemeshes), PerfCounter.MergeNeighborRemeshes, false),
            (nameof(PerfMergeTotals.UnchangedRemeshes), PerfCounter.MergeUnchangedRemeshes, false),
            (nameof(PerfMergeTotals.OutOfBandSections), PerfCounter.MergeOutOfBandSections, false),
            (nameof(PerfMergeTotals.ChurnedSections), PerfCounter.MergeChurnedSections, false),
            (nameof(PerfMergeTotals.UnloadPendingSkyChunks), PerfCounter.UnloadPendingSkyChunks, false),
            (nameof(PerfMergeTotals.UnloadPendingSkyNodes), PerfCounter.UnloadPendingSkyNodes, false),
            (nameof(PerfMergeTotals.CountChecks), PerfCounter.MergeCountChecks, false),
            (nameof(PerfMergeTotals.CountDrifts), PerfCounter.MergeCountDrifts, false),
        };

        /// <summary>The kept section's non-air count after its one stone voxel.</summary>
        private const int KEPT_SECTION_BLOCKS = 1;

        /// <summary>The wrong non-air count the drift check plants in the kept section.</summary>
        private const int PLANTED_NON_AIR = 7;

        #region Scenarios

        private static bool RunB40MergeBreakdown() => WithFreshStore(() =>
        {
            PerfStore.SetTier(PerfTier.Systems);
            FieldInfo[] fields = typeof(PerfMergeTotals).GetFields(BindingFlags.Public | BindingFlags.Instance);
            bool ok = Check($"Every one of the {fields.Length} totals fields has a counter in the map",
                fields.Length == s_mergeCounterMap.Length);

            ok &= Check("Each totals field reaches its own counter, ticks converted to microseconds", MergeCountersMatch());

            int outside = 0;
            for (int s = 0; s < ChunkMath.SECTIONS_PER_CHUNK; s++)
                if (ChunkMath.IsSectionOutsideBand(s, BAND_MIN_Y, BAND_END_Y)) outside++;
            ok &= Check($"A band over rows {BAND_MIN_Y}–{BAND_END_Y - 1} leaves exactly {SECTIONS_OUTSIDE_BAND} sections outside",
                outside == SECTIONS_OUTSIDE_BAND);
            ok &= Check("A full-height band leaves none outside; row edges are exact on both sides",
                !AnySectionOutsideBand(0, ChunkMath.CHUNK_HEIGHT)
                && ChunkMath.IsSectionOutsideBand(1, ChunkMath.SECTION_SIZE * 2, ChunkMath.CHUNK_HEIGHT)
                && !ChunkMath.IsSectionOutsideBand(2, ChunkMath.SECTION_SIZE * 2, ChunkMath.CHUNK_HEIGHT)
                && ChunkMath.IsSectionOutsideBand(6, 0, ChunkMath.SECTION_SIZE * 6)
                && !ChunkMath.IsSectionOutsideBand(5, 0, ChunkMath.SECTION_SIZE * 6 - 1));

            BlockType[] palette = TestBehaviorBlockPalette.Create();
            PerfMergeTotals totals = default;
            bool changed = ApplyChurnFixture(ref totals, palette, plantDrift: false, out ChunkData merged);
            ok &= Check("A real merge over one kept section and one uniform-lit absent section reports a change", changed);
            ok &= Check($"The uniform-lit absent section compacts with no section and no churn (churned {totals.ChurnedSections}); the kept section's verifier is timed",
                totals.ChurnedSections == 0 && merged.sections[CHURN_SECTION] == null
                && merged.SectionUniformSkyLevel[CHURN_SECTION] == FULL_SKY && totals.CountsTicks > 0);
            ok &= Check($"The verifier recounts the one kept section and finds its maintained counts current (checks {totals.CountChecks}, drifts {totals.CountDrifts})",
                totals.CountChecks == 1 && totals.CountDrifts == 0);
            ok &= Check("The apply charges only its own fields",
                totals.ApplyTicks == 0 && totals.Merges == 0 && totals.OutOfBandSections == 0);

            PerfMergeTotals drift = default;
            ApplyChurnFixture(ref drift, palette, plantDrift: true, out ChunkData drifted);
            ok &= Check($"A planted non-air count is caught as one drift and repaired to {KEPT_SECTION_BLOCKS} (got {drifted.sections[0].nonAirCount})",
                drift.CountChecks == 1 && drift.CountDrifts == 1 && drifted.sections[0].nonAirCount == KEPT_SECTION_BLOCKS);

            PerfMergeTotals noPalette = default;
            ApplyChurnFixture(ref noPalette, null, plantDrift: false, out ChunkData _);
            ok &= Check("Without a palette (the lighting harness) the verifier does not run", noPalette.CountChecks == 0);

            PerfStore.SetTier(PerfTier.Basic);
            PerfMergeTotals below = default;
            ApplyChurnFixture(ref below, palette, plantDrift: false, out ChunkData _);
            ok &= Check("Below Systems the merge adds no time or churn; the verifier still checks",
                below.ChurnedSections == 0 && below.CountsTicks == 0 && below.CountChecks == 1 && below.CountDrifts == 0);
            return ok;
        });

        #endregion

        #region Helpers

        /// <summary>
        /// Samples zeroed totals (the baseline), then totals with a distinct value per field, commits the frame and compares
        /// every counter with its own field.
        /// </summary>
        private static bool MergeCountersMatch()
        {
            MethodInfo sample = typeof(World).GetMethod("SampleMergeCounters", BindingFlags.NonPublic | BindingFlags.Static)
                                ?? throw new System.MissingMethodException(nameof(World), "SampleMergeCounters");
            sample.Invoke(null, new object[] { default(PerfMergeTotals) });

            object boxed = default(PerfMergeTotals);
            for (int i = 0; i < s_mergeCounterMap.Length; i++)
                typeof(PerfMergeTotals).GetField(s_mergeCounterMap[i].Field).SetValue(boxed, (i + 1) * FIELD_VALUE_STEP);
            sample.Invoke(null, new[] { boxed });
            PerfStore.CommitFrame(Readings(1, 1));

            bool match = true;
            for (int i = 0; i < s_mergeCounterMap.Length; i++)
            {
                long value = (i + 1) * FIELD_VALUE_STEP;
                long expected = s_mergeCounterMap[i].Ticks ? PerfStore.TicksToMicros(value) : value;
                if (PerfStore.SumCounter(s_mergeCounterMap[i].Counter) == expected) continue;

                Debug.LogError($"  {s_mergeCounterMap[i].Field} → {s_mergeCounterMap[i].Counter}: expected {expected}, " +
                               $"got {PerfStore.SumCounter(s_mergeCounterMap[i].Counter)}");
                match = false;
            }

            return match;
        }

        private static bool AnySectionOutsideBand(int bandMinY, int bandEndY)
        {
            for (int s = 0; s < ChunkMath.SECTIONS_PER_CHUNK; s++)
                if (ChunkMath.IsSectionOutsideBand(s, bandMinY, bandEndY)) return true;
            return false;
        }

        /// <summary>
        /// Merges a job map into a fresh chunk whose section 0 holds a block: section 0 gets mixed sky light (kept and
        /// verified), <see cref="CHURN_SECTION"/> uniform full sky (rented, compacted and returned), the rest stays dark.
        /// </summary>
        /// <param name="totals">The totals the merge charges.</param>
        /// <param name="palette">The block palette handed to the merge, or null as the lighting harness passes.</param>
        /// <param name="plantDrift">Whether to overwrite the kept section's non-air count with a wrong value first.</param>
        /// <param name="chunk">The merged chunk.</param>
        /// <returns>The merge's change report.</returns>
        private static bool ApplyChurnFixture(ref PerfMergeTotals totals, BlockType[] palette, bool plantDrift, out ChunkData chunk)
        {
            chunk = new ChunkData(Vector2Int.zero);
            chunk.SetVoxel(0, 0, 0, BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Stone, 0),
                palette?[BlockIDs.Stone], palette?[BlockIDs.Air]);
            if (plantDrift) chunk.sections[0].nonAirCount = PLANTED_NON_AIR;

            NativeArray<uint> voxels = new NativeArray<uint>(ChunkMath.CHUNK_VOLUME, Allocator.Temp);
            NativeArray<ushort> light = new NativeArray<ushort>(ChunkMath.CHUNK_VOLUME, Allocator.Temp);
            try
            {
                for (int i = 0; i < ChunkMath.SECTION_VOLUME; i++)
                    light[i] = LightBitMapping.PackLightData(i % 2 == 0 ? FULL_SKY : DIM_SKY, 0, 0, 0);

                int churnOffset = CHURN_SECTION * ChunkMath.SECTION_VOLUME;
                for (int i = 0; i < ChunkMath.SECTION_VOLUME; i++)
                    light[churnOffset + i] = LightBitMapping.PackLightData(FULL_SKY, 0, 0, 0);

                return chunk.ApplyJobLightMap(voxels, light, palette, 0, ChunkMath.CHUNK_HEIGHT, ref totals);
            }
            finally
            {
                voxels.Dispose();
                light.Dispose();
            }
        }

        #endregion
    }
}
