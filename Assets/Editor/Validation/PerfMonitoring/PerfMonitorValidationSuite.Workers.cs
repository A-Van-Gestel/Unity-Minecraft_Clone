using System;
using System.Reflection;
using System.Threading.Tasks;
using Data;
using Data.WorldTypes;
using Diagnostics;
using Editor.DataGeneration;
using Editor.Validation.Behavior.Framework;
using Editor.Validation.Framework;
using Editor.WorldTools.Libraries;
using Jobs;
using Jobs.BurstData;
using Jobs.Data;
using Jobs.Generators;
using Serialization;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.PerfMonitoring
{
    /// <summary>
    /// The workers half of the suite: <see cref="JobBusyTimer"/> and <see cref="PerfJobTimingPool"/>, a real generation
    /// chain timing every one of its links, the per-job sample rings behind <see cref="PerfStore.RecordJob"/>, and the
    /// <see cref="StorageIoStats"/> totals over a real save and loads. Link, operation and sample counts are exact; times
    /// and byte counts are asserted only as positive.
    /// </summary>
    public static partial class PerfMonitorValidationSuite
    {
        private const string WORLD_TYPE_PATH = "Assets/Data/WorldGen/WorldTypes/Standard.asset";
        private const int GENERATION_SEED = 4242;
        private const int TIMING_POOL_CAPACITY = 2;

        /// <summary>One <c>IJobFor</c> execution per column.</summary>
        private const int TERRAIN_LINKS = VoxelData.ChunkWidth * VoxelData.ChunkWidth;

        private const int SAMPLE_RING_CAPACITY = 4;
        private const int SAMPLE_RING_OVERFLOW = 2;

        /// <summary>Samples recorded past the store's capacity, and a non-round tick stride so no two samples are equal.</summary>
        private const int JOB_SAMPLE_OVERFLOW = 37;

        private const long JOB_TICK_STRIDE = 1_009L;

        private const double NANOSECONDS_PER_MILLISECOND = 1_000_000.0;

        /// <summary>Chunk X of the load that must miss: never saved by the I/O scenario.</summary>
        private const int UNSAVED_CHUNK_X = 3;

        private const int IO_SAVES = 0;
        private const int IO_SAVE_BYTES = 1;
        private const int IO_LOAD_HITS = 2;
        private const int IO_LOAD_MISSES = 3;
        private const int IO_LOAD_BYTES = 4;
        private const int IO_BACKGROUND_OPS = 10;

        /// <summary>Background operations in the I/O round trip: one async save and two loads.</summary>
        private const int ROUND_TRIP_BACKGROUND_OPS = 3;

        /// <summary>Utilization inputs: job threads, frames, and a per-frame busy-time step distinct per frame and type.</summary>
        private const int UTILIZATION_WORKERS = 7;

        private const int UTILIZATION_FRAMES = 10;
        private const long BUSY_STEP_US = 1_013L;
        private const double MICROSECONDS_PER_MILLISECOND = 1_000.0;

        /// <summary>The fluid fixture: the row's height, its tick salt, and the fluid levels it cycles through.</summary>
        private const int FLUID_SEED_Y = 64;

        private const int FLUID_TICK = 7;
        private const int FLUID_LEVELS = 8;

        #region Scenarios

        private static bool RunB24TimingPool()
        {
            using PerfJobTimingPool pool = new PerfJobTimingPool(TIMING_POOL_CAPACITY);
            bool ok = Check("The default timer is untimed and begins at 0",
                !default(JobBusyTimer).IsTimed && default(JobBusyTimer).Begin() == 0L);
            ok &= Check("No record is rented before the first rent", pool.RentedCount == 0);

            int first = pool.Rent(out JobBusyTimer firstTimer);
            int second = pool.Rent(out JobBusyTimer secondTimer);
            ok &= Check("Rents hand out distinct, non-zero handles with live timers",
                first != 0 && second != 0 && first != second && firstTimer.IsTimed && secondTimer.IsTimed);

            int third = pool.Rent(out JobBusyTimer thirdTimer);
            ok &= Check("An exhausted pool returns 0 and an untimed timer, and counts it",
                third == 0 && !thirdTimer.IsTimed && pool.UntimedTotal == 1);

            long start = firstTimer.Begin();
            Spin();
            firstTimer.End(start);
            pool.Read(first, out long busy, out long links);
            pool.Read(second, out long otherBusy, out long otherLinks);
            ok &= Check("A timed execution adds one link and its time to its own record only",
                links == 1 && busy > 0 && otherBusy == 0 && otherLinks == 0);

            pool.Return(first);
            pool.Return(0);
            ok &= Check("Return frees the record; returning handle 0 is a no-op", pool.RentedCount == 1);

            int reused = pool.Rent(out JobBusyTimer _);
            pool.Read(reused, out long reusedBusy, out long reusedLinks);
            ok &= Check("A returned record is rented again, zeroed", reused == first && reusedBusy == 0 && reusedLinks == 0);

            pool.Dispose();
            ok &= Check("Dispose frees the records", pool.RentedCount == 0);
            return ok;
        }

        private static bool RunB25GenerationChainLinks()
        {
            WorldTypeDefinition worldType = AssetDatabase.LoadAssetAtPath<WorldTypeDefinition>(WORLD_TYPE_PATH);
            BlockDatabase database = EditorBlockDatabaseCache.Database;
            if (!Check("The Standard world type and the block database load", worldType != null && database != null))
                return false;

            EditorChunkPipelineRunner runner = new EditorChunkPipelineRunner();
            using PerfJobTimingPool pool = new PerfJobTimingPool(TIMING_POOL_CAPACITY);
            try
            {
                runner.Initialize(GENERATION_SEED, worldType, database);
                ChunkCoord coord = new ChunkCoord(0, 0);

                int untimedRecord = pool.Rent(out JobBusyTimer _);
                RunGeneration(runner, coord, default);
                pool.Read(untimedRecord, out long untimedBusy, out long untimedLinks);
                bool ok = Check("A chain scheduled without a timer writes nothing", untimedBusy == 0 && untimedLinks == 0);
                pool.Return(untimedRecord);

                GenerationFeatureFlags flags = GenerationFeatureFlags.Default;
                flags.EnableCaves = false;
                runner.FeatureFlags = flags;
                ok &= CheckChainLinks(runner, pool, coord, TERRAIN_LINKS + 1, "Caves off: every column and the active-voxel scan");

                flags.EnableCaves = true;
                runner.FeatureFlags = flags;
                int filterLinks = GetGeneratorMinCavePocketSize(runner) > 0 ? 1 : 0;
                ok &= CheckChainLinks(runner, pool, coord, 1 + TERRAIN_LINKS + filterLinks + 1,
                    "Caves on: the worm carver, every column, the cave filter when scheduled, and the scan");
                return ok;
            }
            finally
            {
                runner.Dispose();
            }
        }

        private static bool RunB26SampleRing()
        {
            PerfSampleRing ring = new PerfSampleRing(SAMPLE_RING_CAPACITY);
            float[] held = new float[SAMPLE_RING_CAPACITY];

            ring.Add(0f);
            ring.Add(1f);
            bool ok = Check("Below capacity the ring holds every sample", HoldsExactly(ring, held, 0, 2));

            for (int i = 2; i < SAMPLE_RING_CAPACITY + SAMPLE_RING_OVERFLOW; i++)
                ring.Add(i);
            ok &= Check("Past capacity the ring holds the newest samples only",
                HoldsExactly(ring, held, SAMPLE_RING_OVERFLOW, SAMPLE_RING_CAPACITY));

            ring.Clear();
            ok &= Check("Clear forgets every sample", ring.Count == 0 && ring.CopyTo(held) == 0);
            ring.Add(SAMPLE_RING_CAPACITY);
            ok &= Check("A cleared ring fills from the start again", HoldsExactly(ring, held, SAMPLE_RING_CAPACITY, 1));
            return ok;
        }

        private static bool RunB27JobSamples() => WithFreshStore(() =>
        {
            PerfStore.RecordJob(PerfJobType.Lighting, JOB_TICK_STRIDE, JOB_TICK_STRIDE, true);
            bool ok = Check("Below Systems no job sample is held", PerfStore.SummarizeJobLatencyMs(PerfJobType.Lighting).Count == 0);

            PerfStore.SetTier(PerfTier.Systems);
            int recorded = PerfStore.JobSampleCapacity + JOB_SAMPLE_OVERFLOW;
            int timed = 0;
            long maxTimedTicks = 0;
            for (int i = 1; i <= recorded; i++)
            {
                bool hasBusy = i % 2 == 0;
                if (hasBusy)
                {
                    timed++;
                    maxTimedTicks = i * JOB_TICK_STRIDE;
                }

                PerfStore.RecordJob(PerfJobType.Lighting, i * JOB_TICK_STRIDE, i * JOB_TICK_STRIDE, hasBusy);
            }

            double tickToMs = (double)GetStoreField("s_tickToMs");
            double profilerTickToNs = (double)GetStoreField("s_profilerTickToNs");
            PerfWindowSummary latency = PerfStore.SummarizeJobLatencyMs(PerfJobType.Lighting);
            PerfWindowSummary busy = PerfStore.SummarizeJobBusyMs(PerfJobType.Lighting);
            ok &= Check("Latency holds the newest jobs up to capacity; its worst is the newest job's, converted",
                latency.Count == PerfStore.JobSampleCapacity
                && ExactValue.Equal(latency.Max, (float)(recorded * JOB_TICK_STRIDE * tickToMs)));
            ok &= Check("Busy time holds only the timed jobs, on the profiler clock",
                busy.Count == timed
                && ExactValue.Equal(busy.Max, (float)(maxTimedTicks * profilerTickToNs / NANOSECONDS_PER_MILLISECOND)));
            ok &= Check("Other job types are untouched",
                PerfStore.SummarizeJobLatencyMs(PerfJobType.Meshing).Count == 0
                && PerfStore.SummarizeJobBusyMs(PerfJobType.Generation).Count == 0);

            PerfStore.ResetCounters();
            ok &= Check("ResetCounters empties the job samples",
                PerfStore.SummarizeJobLatencyMs(PerfJobType.Lighting).Count == 0
                && PerfStore.SummarizeJobBusyMs(PerfJobType.Lighting).Count == 0);

            PerfStore.RecordJob(PerfJobType.Lighting, JOB_TICK_STRIDE, JOB_TICK_STRIDE, true);
            PerfStore.SetTier(PerfTier.Frame);
            ok &= Check("Dropping below Systems frees the job samples",
                PerfStore.SummarizeJobLatencyMs(PerfJobType.Lighting).Count == 0);
            return ok;
        });

        private static bool RunB28StorageIoCounters() => WithFreshStore(() =>
        {
            using IoFixture fx = new IoFixture();
            Vector2Int savedPos = new Vector2Int(0, 0);
            Vector2Int missingPos = new Vector2Int(VoxelData.ChunkWidth * UNSAVED_CHUNK_X, 0);
            ChunkData data = World.Instance.ChunkPool.GetChunkData(savedPos);
            try
            {
                long[] before = IoTotals();
                RunIoRoundTrip(fx, data, savedPos, missingPos);
                bool ok = Check("Below Systems no I/O is counted", SameTotals(before, IoTotals()));

                PerfStore.SetTier(PerfTier.Systems);
                int inFlightBefore = StorageIoStats.InFlight;
                before = IoTotals();
                bool roundTrip = RunIoRoundTrip(fx, data, savedPos, missingPos);
                long[] after = IoTotals();
                ok &= Check("The save is written, the saved chunk loads and the unsaved one does not", roundTrip);
                ok &= Check("One save counted, with its payload bytes",
                    after[IO_SAVES] - before[IO_SAVES] == 1 && after[IO_SAVE_BYTES] > before[IO_SAVE_BYTES]);
                ok &= Check("One load hit with its payload bytes, and one miss",
                    after[IO_LOAD_HITS] - before[IO_LOAD_HITS] == 1 && after[IO_LOAD_MISSES] - before[IO_LOAD_MISSES] == 1
                    && after[IO_LOAD_BYTES] > before[IO_LOAD_BYTES]);
                ok &= Check("Every background operation that started has finished", StorageIoStats.InFlight == inFlightBefore);
                ok &= Check("Each background operation counted once for the ThreadPool wait — the save and both loads",
                    after[IO_BACKGROUND_OPS] - before[IO_BACKGROUND_OPS] == ROUND_TRIP_BACKGROUND_OPS);
                return ok;
            }
            finally
            {
                World.Instance.ChunkPool.ReturnChunkData(data);
            }
        });

        private static bool RunB29WorkerUtilization() => WithFreshStore(() =>
        {
            bool ok = Check("Before any counter frame utilization is NaN and the sums are 0",
                double.IsNaN(PerfStore.WorkerUtilization(UTILIZATION_WORKERS))
                && PerfStore.SumCounter(PerfCounter.GenerationBusyUs) == 0
                && ExactValue.IsZero(PerfStore.SumCounterFramesWallMs()));

            PerfStore.SetTier(PerfTier.Frame);
            PerfStore.CommitFrame(Readings(BASE_INJECTED_TICKS, 0));
            PerfStore.SetTier(PerfTier.Systems);

            PerfCounter[] busyCounters =
            {
                PerfCounter.GenerationBusyUs, PerfCounter.LightBusyUs, PerfCounter.MeshBusyUs, PerfCounter.FluidBusyUs,
                PerfCounter.FluidSoundScanBusyUs,
            };
            long[] totals = new long[busyCounters.Length];
            foreach (PerfCounter counter in busyCounters)
                PerfStore.SampleTotal(counter, 0L);

            double tickToMs = (double)GetStoreField("s_tickToMs");
            double expectedWallMs = 0.0;
            long expectedBusyUs = 0L;
            long expectedGenerationUs = 0L;
            for (int frame = 1; frame <= UTILIZATION_FRAMES; frame++)
            {
                for (int k = 0; k < busyCounters.Length; k++)
                {
                    long step = BUSY_STEP_US * frame + k;
                    totals[k] += step;
                    expectedBusyUs += step;
                    if (k == 0) expectedGenerationUs += step;
                    PerfStore.SampleTotal(busyCounters[k], totals[k]);
                }

                long wallTicks = BASE_INJECTED_TICKS + frame * INJECTED_TICK_STRIDE;
                expectedWallMs += (float)(wallTicks * tickToMs);
                PerfStore.CommitFrame(Readings(wallTicks, frame));
            }

            ok &= Check("A counter sums over the frames that carry counters, not the earlier Frame-tier one",
                PerfStore.SumCounter(PerfCounter.GenerationBusyUs) == expectedGenerationUs);
            ok &= Check("Wall time sums over the same frames",
                ExactValue.Equal(PerfStore.SumCounterFramesWallMs(), expectedWallMs));
            ok &= Check("Utilization is every job type's busy time over workers × wall time",
                ExactValue.Equal(PerfStore.WorkerUtilization(UTILIZATION_WORKERS),
                    expectedBusyUs / MICROSECONDS_PER_MILLISECOND / (UTILIZATION_WORKERS * expectedWallMs)));
            ok &= Check("No workers gives NaN", double.IsNaN(PerfStore.WorkerUtilization(0)));
            return ok;
        });

        private static bool RunB30FluidJobLinks()
        {
            using PerfJobTimingPool pool = new PerfJobTimingPool(TIMING_POOL_CAPACITY);
            bool ok;
            using (BehaviorTestWorld rig = new BehaviorTestWorld())
            {
                SeedFluidRow(rig.ChunkData);
                FluidBurstTicker ticker = new FluidBurstTicker();
                try
                {
                    int untimedRecord = pool.Rent(out JobBusyTimer _);
                    ticker.ScheduleFluids(rig.ChunkData, FLUID_TICK, rig.BlockTypesJob, rig.WorldData).Complete();
                    pool.Read(untimedRecord, out long untimedBusy, out long untimedLinks);
                    ok = Check("A fluid tick scheduled without a timer writes nothing", untimedBusy == 0 && untimedLinks == 0);
                    pool.Return(untimedRecord);

                    int record = pool.Rent(out JobBusyTimer timer);
                    JobHandle handle = ticker.ScheduleFluids(rig.ChunkData, FLUID_TICK, rig.BlockTypesJob, rig.WorldData, timer);
                    ok &= Check("The seeded chunk schedules a fluid tick job", !handle.Equals(default(JobHandle)));
                    handle.Complete();
                    pool.Read(record, out long busy, out long links);
                    pool.Return(record);
                    ok &= Check($"A timed fluid tick job times one link (got {links}) with busy time", links == 1 && busy > 0);
                }
                finally
                {
                    ticker.Dispose();
                }
            }

            NativeArray<uint> sections = new NativeArray<uint>(0, Allocator.TempJob);
            NativeArray<int3> origins = new NativeArray<int3>(0, Allocator.TempJob);
            NativeArray<BlockTypeJobData> palette = new NativeArray<BlockTypeJobData>(0, Allocator.TempJob);
            NativeArray<FluidEmitterBin> bins = new NativeArray<FluidEmitterBin>(FluidEmitterScanGeometry.BinCount, Allocator.TempJob);
            try
            {
                int record = pool.Rent(out JobBusyTimer timer);
                new FluidEmitterScanJob
                {
                    Sections = sections,
                    SectionOrigins = origins,
                    SectionCount = 0,
                    BlockTypes = palette,
                    Bins = bins,
                    Timer = timer,
                }.Schedule().Complete();
                pool.Read(record, out long _, out long links);
                pool.Return(record);
                ok &= Check($"A timed fluid sound scan, even an empty one, times one link (got {links})", links == 1);
            }
            finally
            {
                sections.Dispose();
                origins.Dispose();
                palette.Dispose();
                bins.Dispose();
            }

            return ok;
        }

        #endregion

        #region Helpers

        /// <summary>A row of flowing water across the chunk, registered as active, so the fluid tick has work.</summary>
        private static void SeedFluidRow(ChunkData chunk)
        {
            for (int x = 0; x < VoxelData.ChunkWidth; x++)
            {
                byte meta = BurstVoxelDataBitMapping.BuildMetaLegacy(0, (byte)(x % FLUID_LEVELS), true);
                chunk.SetVoxel(x, FLUID_SEED_Y, 0, BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Water, meta));
                chunk.AddActiveVoxel(new Vector3Int(x, FLUID_SEED_Y, 0), BlockIDs.Water);
            }
        }

        /// <summary>A volatile-path storage fixture with a stub <c>World.Instance</c>.</summary>
        private sealed class IoFixture : StorageValidationFixture
        {
            public IoFixture() : base("PerfMonitorIoTest")
            {
            }
        }

        /// <summary>Saves <paramref name="data"/>, then loads it back and loads a chunk never saved.</summary>
        /// <returns>True when the save was written, the saved chunk loaded and the unsaved one did not.</returns>
        private static bool RunIoRoundTrip(IoFixture fx, ChunkData data, Vector2Int savedPos, Vector2Int missingPos)
        {
            ChunkSaveResult result = Task.Run(() => fx.Storage.SaveChunkAsync(data)).GetAwaiter().GetResult();
            ChunkData loaded = Task.Run(() => fx.Storage.LoadChunkAsync(savedPos)).GetAwaiter().GetResult();
            ChunkData missing = Task.Run(() => fx.Storage.LoadChunkAsync(missingPos)).GetAwaiter().GetResult();
            if (loaded != null) World.Instance.ChunkPool.ReturnChunkData(loaded);
            if (missing != null) World.Instance.ChunkPool.ReturnChunkData(missing);
            return result == ChunkSaveResult.Written && loaded != null && missing == null;
        }

        /// <summary>The storage I/O running totals, indexed by the <c>IO_*</c> constants.</summary>
        private static long[] IoTotals() => new[]
        {
            StorageIoStats.Saves, StorageIoStats.SaveBytes, StorageIoStats.LoadHits, StorageIoStats.LoadMisses,
            StorageIoStats.LoadBytes, StorageIoStats.SerializeMicros, StorageIoStats.WriteMicros, StorageIoStats.ReadMicros,
            StorageIoStats.DeserializeMicros, StorageIoStats.QueueWaitMicros, StorageIoStats.BackgroundOps,
        };

        private static bool SameTotals(long[] a, long[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }

            return true;
        }

        /// <summary>Generates one chunk with the given timer, completes it and frees its output.</summary>
        private static void RunGeneration(EditorChunkPipelineRunner runner, ChunkCoord coord, JobBusyTimer timer)
        {
            GenerationJobData data = runner.ScheduleGeneration(coord, timer);
            data.Handle.Complete();
            data.Dispose();
        }

        private static bool CheckChainLinks(EditorChunkPipelineRunner runner, PerfJobTimingPool pool, ChunkCoord coord,
            int expectedLinks, string label)
        {
            int record = pool.Rent(out JobBusyTimer timer);
            RunGeneration(runner, coord, timer);
            pool.Read(record, out long busy, out long links);
            pool.Return(record);
            return Check($"{label} — {expectedLinks} timed links (got {links}), busy time recorded", links == expectedLinks && busy > 0);
        }

        /// <summary>The generator's cave-pocket threshold, which decides whether the cave filter joins the chain.</summary>
        private static int GetGeneratorMinCavePocketSize(EditorChunkPipelineRunner runner)
        {
            FieldInfo generatorField = typeof(EditorChunkPipelineRunner).GetField("_generator", BindingFlags.NonPublic | BindingFlags.Instance)
                                       ?? throw new MissingFieldException(nameof(EditorChunkPipelineRunner), "_generator");
            object generator = generatorField.GetValue(runner);
            FieldInfo pocketField = typeof(StandardChunkGenerator).GetField("_globalMinCavePocketSize", BindingFlags.NonPublic | BindingFlags.Instance)
                                    ?? throw new MissingFieldException(nameof(StandardChunkGenerator), "_globalMinCavePocketSize");
            return (int)pocketField.GetValue(generator);
        }

        /// <summary>Whether the ring holds exactly the consecutive values <paramref name="first"/> … first + count − 1.</summary>
        private static bool HoldsExactly(PerfSampleRing ring, float[] buffer, int first, int count)
        {
            int copied = ring.CopyTo(buffer);
            if (copied != count || ring.Count != count) return false;

            Array.Sort(buffer, 0, copied);
            for (int i = 0; i < copied; i++)
            {
                if (!ExactValue.Equal(buffer[i], first + i)) return false;
            }

            return true;
        }

        #endregion
    }
}
