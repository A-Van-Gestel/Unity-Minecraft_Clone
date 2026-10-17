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

    /// <summary>
    /// A running pre-generation's position, raised after each station is checked. The walk finds missing chunks one
    /// station at a time, so progress is measured in stations, not chunks.
    /// </summary>
    public readonly struct PregenerationProgressReport
    {
        /// <summary>Stations checked so far, walked or skipped.</summary>
        public readonly long StationsChecked;

        /// <summary>Stations in the rectangle's grid.</summary>
        public readonly long StationCount;

        /// <summary>Chunks generated so far.</summary>
        public readonly long Generated;

        /// <summary>Wall-clock seconds since the run started.</summary>
        public readonly double ElapsedSeconds;

        /// <summary>Whether the run is a console command's rather than the benchmark's.</summary>
        public readonly bool Interactive;

        /// <summary>Creates a progress report.</summary>
        /// <param name="stationsChecked">Stations checked so far.</param>
        /// <param name="stationCount">Stations in the grid.</param>
        /// <param name="generated">Chunks generated so far.</param>
        /// <param name="elapsedSeconds">Seconds since the run started.</param>
        /// <param name="interactive">Whether the run is a console command's.</param>
        public PregenerationProgressReport(long stationsChecked, long stationCount, long generated, double elapsedSeconds,
            bool interactive)
        {
            StationsChecked = stationsChecked;
            StationCount = stationCount;
            Generated = generated;
            ElapsedSeconds = elapsedSeconds;
            Interactive = interactive;
        }
    }

    /// <summary>The result of one pre-generation run: what was asked, what was done, and what was not.</summary>
    public sealed class PregenerationOutcome
    {
        /// <summary>How the run ended.</summary>
        public PregenerationStatus Status;

        /// <summary>The rectangle the run covered.</summary>
        public ChunkRect Rect;

        /// <summary>Whether the run was a console command's rather than the benchmark's.</summary>
        public bool Interactive;

        /// <summary>Chunks the walk found neither on disk nor resident, over the stations it checked.</summary>
        public long MissingFound;

        /// <summary>Stations in the rectangle's grid.</summary>
        public long StationGridCount;

        /// <summary>Stations checked, walked or skipped; below <see cref="StationGridCount"/> only when canceled.</summary>
        public long StationsChecked;

        /// <summary>Stations the first pass moved the anchor to — the ones that held a missing chunk.</summary>
        public int Stations;

        /// <summary>Missing chunks the run generated.</summary>
        public long Generated;

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

        /// <summary>Where the anchor stood for the station <see cref="SlowestStationSeconds"/> was measured at.</summary>
        public ChunkCoord SlowestStation;

        /// <summary>Why the run was canceled, or null.</summary>
        public string CancelReason;

        /// <summary>The first few chunks still missing after the retry pass, for the error report.</summary>
        public readonly List<ChunkCoord> FirstStalled = new List<ChunkCoord>();
    }
}
