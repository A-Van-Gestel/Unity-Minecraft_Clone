using System.Runtime.InteropServices;

namespace Jobs.Data
{
    /// <summary>
    /// Per-worm diagnostic data emitted by <see cref="StandardWormCarverJob"/> when telemetry is enabled.
    /// Gated behind <c>NativeList.IsCreated</c> — production code passes an unallocated list for zero overhead.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WormTelemetryEntry
    {
        /// <summary>Chunk-space X origin of the worm's spawning chunk.</summary>
        public int OriginChunkX;

        /// <summary>Chunk-space Z origin of the worm's spawning chunk.</summary>
        public int OriginChunkZ;

        /// <summary>Whether this worm was a world-level trunk worm (true) or a per-biome local worm (false).</summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool IsTrunk;

        /// <summary>Branch depth: 0 = root worm, 1+ = child branch.</summary>
        public byte BranchDepth;

        /// <summary>Actual number of steps the worm took before termination.</summary>
        public short ActualSteps;

        /// <summary>Originally configured length (LengthRemaining at spawn).</summary>
        public short ConfiguredLength;

        /// <summary>Number of branch worms spawned by this worm.</summary>
        public byte BranchesSpawned;

        /// <summary>Number of noise-seeking checks that fired (passed interval + chance roll).</summary>
        public byte NoiseSeekAttempts;

        /// <summary>Number of noise-seeking checks that found a target cave feature.</summary>
        public byte NoiseSeekSuccesses;

        /// <summary>Number of mask-seeking checks that fired.</summary>
        public byte MaskSeekAttempts;

        /// <summary>Number of mask-seeking checks that found an existing worm tunnel.</summary>
        public byte MaskSeekSuccesses;

        /// <summary>Why the worm stopped: 0 = natural (ran out of steps), 1 = traversal-blocked, 2 = fade-complete.</summary>
        public byte TerminationReason;

        public const byte TERMINATION_NATURAL = 0;
        public const byte TERMINATION_TRAVERSAL_BLOCKED = 1;
        public const byte TERMINATION_FADE_COMPLETE = 2;
    }
}
