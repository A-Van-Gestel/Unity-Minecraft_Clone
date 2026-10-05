using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Serialization;
using Data;
using Diagnostics;
using Editor.Validation.Behavior.Framework;
using Editor.Validation.Framework;
using Helpers;
using Jobs;
using Jobs.BurstData;
using UnityEngine;

namespace Editor.Validation.PerfMonitoring
{
    /// <summary>
    /// The behavior-tick half of the suite: the production <c>World.TickChunksParallel</c> over one seeded chunk, charging
    /// <see cref="PerfTickTotals"/> part by part and sampling them into the <c>Tick*</c> counters. Counts and the
    /// totals-to-counter mapping are exact; times are asserted only as positive and within the call's own bracket.
    /// </summary>
    public static partial class PerfMonitorValidationSuite
    {
        /// <summary>Where the seeded grass voxel sits: away from the fluid row, with air all around, so it emits nothing.</summary>
        private static readonly Vector3Int s_grassCell = new Vector3Int(8, 40, 8);

        /// <summary>The center map plus the one populated neighbor the tick fixture seeds.</summary>
        private const int TICK_SNAPSHOT_MAPS = 2;

        private const int TICK_FRAMES = 2;

        #region Scenarios

        private static bool RunB31TickBreakdown() => WithFreshStore(() =>
        {
            PerfStore.SetTier(PerfTier.Systems);
            using BehaviorTestWorld rig = new BehaviorTestWorld();
            rig.EnableModifyVoxel();
            rig.SetNeighborBlock(1, 0, 0, 0, 0, BlockIDs.Stone);
            World world = World.Instance;
            Chunk chunk = (Chunk)FormatterServices.GetUninitializedObject(typeof(Chunk));
            chunk.ChunkData = rig.ChunkData;
            List<Chunk> snapshot = new List<Chunk> { chunk };
            Queue<VoxelMod> modifications = (Queue<VoxelMod>)ValidationReflection.GetInstanceField(world, "_modifications");
            try
            {
                SeedTickChunk(rig);
                VoxelMod[] oracle = RunFluidOracle(rig);
                InvokeWorldPrivate(world, "SampleTickCounters");

                long bracketStart = Stopwatch.GetTimestamp();
                InvokeWorldPrivate(world, "TickChunksParallel", snapshot);
                long bracketTicks = Stopwatch.GetTimestamp() - bracketStart;
                PerfTickTotals first = TickTotals(world);

                bool ok = Check("A real tick counts one fluid chunk, the center and one neighbor map, and one grass voxel",
                    first.FluidChunks == 1 && first.SnapshotMaps == TICK_SNAPSHOT_MAPS && first.GrassVoxels == 1);
                ok &= Check("Prepare, schedule, wait, grass and replay are each timed",
                    first.PrepareTicks > 0 && first.ScheduleTicks > 0 && first.WaitTicks > 0 && first.GrassTicks > 0 && first.ReplayTicks > 0);
                ok &= Check("The active-chunk list belongs to the caller, so a direct tick leaves it at 0", first.ListTicks == 0);
                long partsTicks = first.PrepareTicks + first.ScheduleTicks + first.WaitTicks + first.GrassTicks + first.ReplayTicks;
                ok &= Check($"The parts ({partsTicks} ticks) sum to no more than the call ({bracketTicks} ticks)", partsTicks <= bracketTicks);
                ok &= Check($"The tick emits the serial oracle's {oracle.Length} fluid modifications, in order",
                    oracle.Length > 0 && SameMods(modifications, oracle));

                InvokeWorldPrivate(world, "SampleTickCounters");
                PerfStore.CommitFrame(Readings(1, 1));
                ok &= Check("The counters carry the counts: one chunk, two maps in KB, one grass voxel, one ticker created",
                    PerfStore.SumCounter(PerfCounter.TickFluidChunks) == 1
                    && PerfStore.SumCounter(PerfCounter.TickSnapshotKb) == TICK_SNAPSHOT_MAPS * PerfTickTotals.SnapshotMapKb
                    && PerfStore.SumCounter(PerfCounter.TickGrassVoxels) == 1
                    && PerfStore.SumCounter(PerfCounter.FluidTickerPoolMisses) == 1);
                ok &= Check("Each time counter carries its own part, in microseconds", TimeCountersMatch(first));

                modifications.Clear();
                SeedTickChunk(rig);
                InvokeWorldPrivate(world, "TickChunksParallel", snapshot);
                InvokeWorldPrivate(world, "SampleTickCounters");
                PerfStore.CommitFrame(Readings(1, TICK_FRAMES));
                ok &= Check("A second tick reuses the pooled ticker: no further pool miss",
                    TickTotals(world).FluidChunks == TICK_FRAMES && PerfStore.SumCounter(PerfCounter.FluidTickerPoolMisses) == 1);

                PerfStore.SetTier(PerfTier.Basic);
                PerfTickTotals before = TickTotals(world);
                modifications.Clear();
                SeedTickChunk(rig);
                InvokeWorldPrivate(world, "TickChunksParallel", snapshot);
                ok &= Check("Below Systems a real tick adds nothing", TickTotals(world).Equals(before));
                return ok;
            }
            finally
            {
                // World.OnDestroy, which frees the ticker pool, never runs for this edit-mode stub.
                (ValidationReflection.GetInstanceField(world, "_fluidTickerPool") as DynamicPool<FluidBurstTicker>)?.Clear();
            }
        });

        #endregion

        #region Helpers

        /// <summary>Seeds the fluid row and the grass voxel into their buckets again; a drain removes what went inactive.</summary>
        private static void SeedTickChunk(BehaviorTestWorld rig)
        {
            SeedFluidRow(rig.ChunkData);
            rig.ChunkData.SetVoxel(s_grassCell.x, s_grassCell.y, s_grassCell.z,
                BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Grass, 0));
            rig.ChunkData.AddActiveVoxel(s_grassCell, BlockIDs.Grass);
        }

        /// <summary>The fluid modifications the serial runner emits for the rig's chunk as seeded, on the World's tick salt.</summary>
        private static VoxelMod[] RunFluidOracle(BehaviorTestWorld rig)
        {
            FluidBurstTicker ticker = new FluidBurstTicker();
            try
            {
                ticker.RunFluids(rig.ChunkData, World.Instance.TickCounter, rig.BlockTypesJob, rig.WorldData);
                return ticker.Mods.AsArray().ToArray();
            }
            finally
            {
                ticker.Dispose();
            }
        }

        private static bool SameMods(Queue<VoxelMod> queue, VoxelMod[] expected)
        {
            if (queue.Count != expected.Length) return false;

            int i = 0;
            foreach (VoxelMod mod in queue)
                if (!mod.Equals(expected[i++])) return false;
            return true;
        }

        /// <summary>Whether every time counter of the one committed frame equals its own part's total, converted.</summary>
        private static bool TimeCountersMatch(PerfTickTotals totals) =>
            PerfStore.SumCounter(PerfCounter.TickListUs) == PerfStore.TicksToMicros(totals.ListTicks)
            && PerfStore.SumCounter(PerfCounter.TickPrepareUs) == PerfStore.TicksToMicros(totals.PrepareTicks)
            && PerfStore.SumCounter(PerfCounter.TickScheduleUs) == PerfStore.TicksToMicros(totals.ScheduleTicks)
            && PerfStore.SumCounter(PerfCounter.TickWaitUs) == PerfStore.TicksToMicros(totals.WaitTicks)
            && PerfStore.SumCounter(PerfCounter.TickGrassUs) == PerfStore.TicksToMicros(totals.GrassTicks)
            && PerfStore.SumCounter(PerfCounter.TickFluidReplayUs) == PerfStore.TicksToMicros(totals.ReplayTicks);

        private static PerfTickTotals TickTotals(World world) =>
            (PerfTickTotals)ValidationReflection.GetInstanceField(world, "_tickTotals");

        private static void InvokeWorldPrivate(World world, string methodName, params object[] args)
        {
            MethodInfo method = typeof(World).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)
                                ?? throw new MissingMethodException(nameof(World), methodName);
            method.Invoke(world, args);
        }

        #endregion
    }
}
