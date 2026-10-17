using System.Collections.Generic;

namespace Data
{
    /// <summary>How a pre-generation run ended (CMD-6, design CHUNK_PREGENERATION.md §4.5).</summary>
    public enum PregenerationStatus : byte
    {
        /// <summary>Every missing chunk in the rectangle was generated.</summary>
        Completed,

        /// <summary>Targets were still missing after the retry pass; <see cref="PregenerationOutcome.Stalled"/> counts them.</summary>
        CompletedWithStalls,

        /// <summary>Canceled by request, a view-distance change, a moved anchor, or a storage fault.</summary>
        Canceled,
    }

    /// <summary>The result of one pre-generation run: what was asked, what was done, and what was not.</summary>
    public sealed class PregenerationOutcome
    {
        /// <summary>How the run ended.</summary>
        public PregenerationStatus Status;

        /// <summary>The rectangle the run covered.</summary>
        public ChunkRect Rect;

        /// <summary>Chunks neither on disk nor resident when the run started.</summary>
        public int MissingAtStart;

        /// <summary>Stations planned for the first pass.</summary>
        public int Stations;

        /// <summary>Missing chunks the run generated.</summary>
        public int Generated;

        /// <summary>Chunks still missing after the retry pass (0 unless <see cref="PregenerationStatus.CompletedWithStalls"/>).</summary>
        public int Stalled;

        /// <summary>Generated chunks whose lighting had not settled when the settle watchdog gave up on them.</summary>
        public int SettleTimeouts;

        /// <summary>Settled chunks marked for saving again because they had been reloaded from an unsettled save.</summary>
        public int Remarked;

        /// <summary>Wall-clock duration of the run, in seconds.</summary>
        public double ElapsedSeconds;

        /// <summary>Station time spent until every target was populated, summed over stations.</summary>
        public double PopulateSeconds;

        /// <summary>Station time spent after that, waiting for lighting to settle, summed over stations.</summary>
        public double SettleSeconds;

        /// <summary>The longest single station, in seconds (§8 Q1's measure).</summary>
        public double SlowestStationSeconds;

        /// <summary>The station <see cref="SlowestStationSeconds"/> was measured at.</summary>
        public ChunkCoord SlowestStation;

        /// <summary>Why the run was canceled, or null.</summary>
        public string CancelReason;

        /// <summary>The first few chunks still missing after the retry pass, for the error report.</summary>
        public readonly List<ChunkCoord> FirstStalled = new List<ChunkCoord>();
    }
}
