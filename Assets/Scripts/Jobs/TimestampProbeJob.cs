using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Profiling.LowLevel.Unsafe;

namespace Jobs
{
    /// <summary>
    /// Reads <see cref="ProfilerUnsafeUtility.Timestamp"/> from inside a Burst job and times a loop of calls —
    /// the engine API probe's check that a timestamp source exists for in-job execute timing, and what one
    /// read costs there. Reports which code path actually ran, because a job whose Burst compilation failed
    /// silently runs as managed code and would otherwise pass for a Burst result.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct TimestampProbeJob : IJob
    {
        /// <summary>Index in <see cref="Result"/> of the timestamp read before the loop.</summary>
        public const int StartIndex = 0;

        /// <summary>Index in <see cref="Result"/> of the timestamp read after the loop.</summary>
        public const int EndIndex = 1;

        /// <summary>Index in <see cref="Result"/> of the path flag: 1 when the Burst-compiled code ran, 0 when managed.</summary>
        public const int BurstRanIndex = 2;

        /// <summary>Index in <see cref="Result"/> of the loop's accumulated reads, kept so the loop cannot be optimized away.</summary>
        public const int SinkIndex = 3;

        /// <summary>Required length of <see cref="Result"/>.</summary>
        public const int ResultLength = 4;

        /// <summary>Timestamp reads performed between the start and end stamps.</summary>
        public int Iterations;

        /// <summary>Output slots, addressed by the index constants above.</summary>
        public NativeArray<long> Result;

        /// <inheritdoc/>
        public void Execute()
        {
            long sink = 0;
            long start = ProfilerUnsafeUtility.Timestamp;
            for (int i = 0; i < Iterations; i++)
                sink += ProfilerUnsafeUtility.Timestamp;
            long end = ProfilerUnsafeUtility.Timestamp;

            long burstRan = 1;
            MarkManaged(ref burstRan);

            Result[StartIndex] = start;
            Result[EndIndex] = end;
            Result[BurstRanIndex] = burstRan;
            Result[SinkIndex] = sink;
        }

        // Stripped from the Burst-compiled body, so it only runs on the managed path.
        [BurstDiscard]
        private static void MarkManaged(ref long burstRan)
        {
            burstRan = 0;
        }
    }
}
