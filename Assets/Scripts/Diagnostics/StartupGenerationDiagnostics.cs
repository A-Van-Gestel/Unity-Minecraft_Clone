using System.Collections.Generic;
using System.Text;
using Data;
using Helpers;
using Unity.Collections;
using Unity.Mathematics;

namespace Diagnostics
{
    /// <summary>
    /// Opt-in (<c>startupWorldDigest</c>) per-chunk record of startup generation: the raw job output, the structure
    /// markers in dequeue order and as a set, and how each structure mod reached the chunk. Lets two runs' per-chunk
    /// lines be diffed to split "the job output varies" from "the mod application varies". Diagnostic only.
    /// </summary>
    public sealed class StartupGenerationDiagnostics
    {
        /// <summary>One chunk's counters and hashes.</summary>
        public sealed class Entry
        {
            /// <summary>FNV-1a over the job's voxel map before any structure mod.</summary>
            public ulong Raw;

            /// <summary>FNV-1a over the chunk's own markers in dequeue order.</summary>
            public ulong MarkerOrder = MeshInputDigest.FnvOffset;

            /// <summary>Order-independent sum of the chunk's own marker hashes.</summary>
            public ulong MarkerSet;

            /// <summary>Markers dequeued for the chunk.</summary>
            public int Markers;

            /// <summary>Mods targeting the chunk applied through the replacement rule.</summary>
            public int Applied;

            /// <summary>Mods targeting the chunk the replacement rule refused.</summary>
            public int Refused;

            /// <summary>Mods targeting the chunk parked as pending because it was not populated yet.</summary>
            public int Pended;

            /// <summary>Pending mods replayed into the chunk.</summary>
            public int Replayed;

            /// <summary>Replayed mods the replacement rule refused.</summary>
            public int ReplayRefused;
        }

        /// <summary>Format of every hash in the report: 16 uppercase hex digits.</summary>
        public const string HashFormat = "X16";

        /// <summary>Characters to reserve per report line, so the report builder grows once at most.</summary>
        public const int ReportLineCapacity = 220;

        private readonly Dictionary<ChunkCoord, Entry> _entries = new Dictionary<ChunkCoord, Entry>();

        /// <summary>Returns the chunk's entry, creating it on first use.</summary>
        /// <param name="coord">The chunk.</param>
        /// <returns>The chunk's entry.</returns>
        public Entry Get(ChunkCoord coord)
        {
            if (!_entries.TryGetValue(coord, out Entry entry))
            {
                entry = new Entry();
                _entries[coord] = entry;
            }

            return entry;
        }

        /// <summary>Records the chunk's raw generation output.</summary>
        /// <param name="coord">The chunk.</param>
        /// <param name="map">The job's voxel map, before any structure mod.</param>
        public void RecordRaw(ChunkCoord coord, NativeArray<uint> map)
        {
            ulong hash = MeshInputDigest.FnvOffset;
            for (int i = 0; i < map.Length; i++)
                hash = (hash ^ map[i]) * MeshInputDigest.FnvPrime;
            Get(coord).Raw = hash;
        }

        /// <summary>Records one structure marker the chunk's job emitted, in dequeue order.</summary>
        /// <param name="coord">The chunk whose job emitted the marker.</param>
        /// <param name="position">The marker's root position.</param>
        /// <param name="poolEntryIndex">The marker's pool entry.</param>
        public void RecordMarker(ChunkCoord coord, int3 position, int poolEntryIndex)
        {
            Entry entry = Get(coord);
            ulong markerHash = math.hash(new int4(position, poolEntryIndex));
            entry.MarkerOrder = (entry.MarkerOrder ^ markerHash) * MeshInputDigest.FnvPrime;
            entry.MarkerSet += markerHash * MeshInputDigest.FnvPrime;
            entry.Markers++;
        }

        /// <summary>Appends one report line for the chunk.</summary>
        /// <param name="sb">The report.</param>
        /// <param name="coord">The chunk.</param>
        /// <param name="finalHash">FNV-1a over the chunk's voxels at the digest.</param>
        public void AppendLine(StringBuilder sb, ChunkCoord coord, ulong finalHash)
        {
            Entry e = Get(coord);
            sb.Append("chunk ").Append(coord.X).Append(',').Append(coord.Z)
                .Append(" final=").Append(finalHash.ToString(HashFormat))
                .Append(" raw=").Append(e.Raw.ToString(HashFormat))
                .Append(" mset=").Append(e.MarkerSet.ToString(HashFormat))
                .Append(" mord=").Append(e.MarkerOrder.ToString(HashFormat))
                .Append(" markers=").Append(e.Markers)
                .Append(" applied=").Append(e.Applied)
                .Append(" refused=").Append(e.Refused)
                .Append(" pended=").Append(e.Pended)
                .Append(" replayed=").Append(e.Replayed)
                .Append(" replayRefused=").Append(e.ReplayRefused)
                .AppendLine();
        }
    }
}
