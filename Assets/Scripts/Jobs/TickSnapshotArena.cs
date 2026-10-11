using System;
using System.Collections.Generic;
using Data;
using Helpers;
using Unity.Collections;
using UnityEngine;

namespace Jobs
{
    /// <summary>
    /// The behavior tick's pre-tick voxel snapshot: every chunk section any tick job reads, copied once per tick into
    /// one packed native arena that all of the tick's jobs read concurrently. A job asks for a Y-band of its chunk and
    /// of its neighbors; the arena copies the sections that band covers the first time any job asks for them and hands
    /// every later request the same copy, so a chunk read by its own job and by up to eight neighbors' jobs is copied
    /// once, and only where a band reaches. Each request fills a per-job table (see <see cref="SectionArenaTable"/>)
    /// mapping (slot, section) to the section's arena index.
    /// <para>
    /// <b>All requests come before any schedule.</b> A scheduled job holds <see cref="Sections"/>, so the arena may grow
    /// only while nothing reads it: the tick prepares every chunk first, then schedules every job, and calls
    /// <see cref="EndTick"/> only after all of them complete. Main thread only.
    /// </para>
    /// </summary>
    public sealed class TickSnapshotArena : IDisposable
    {
        // A null section is all air; every request for one points at this zeroed section instead of copying.
        private const int AIR_SECTION_START = 0;

        // Marks a section of a known chunk that no request has copied yet this tick.
        private const int NOT_COPIED = -1;

        // After a tick, capacity above this multiple of what the tick used is given back (a burst's peak is not kept).
        private const int TRIM_FACTOR = 4;

        // Capacity the arena always keeps, in sections (1 MB), so quiet ticks never reallocate.
        private const int MIN_RETAINED_SECTIONS = 64;

        private NativeList<uint> _sections;

        // Chunk origin → its first entry in _sectionStarts, which holds SECTIONS_PER_CHUNK arena indices per chunk.
        private readonly Dictionary<Vector2Int, int> _chunkBases = new Dictionary<Vector2Int, int>();
        private readonly List<int> _sectionStarts = new List<int>();

        /// <summary>The packed sections. Valid for jobs from the last request of a tick until <see cref="EndTick"/>.</summary>
        public NativeArray<uint> Sections => _sections.AsArray();

        /// <summary>Sections copied since <see cref="BeginTick"/>.</summary>
        public int SectionsCopied { get; private set; }

        /// <summary>Clears the previous tick's copies. Call before the tick's first request.</summary>
        public void BeginTick()
        {
            EnsureAllocated();
            _chunkBases.Clear();
            _sectionStarts.Clear();
            _sections.ResizeUninitialized(ChunkMath.SECTION_VOLUME); // keeps the zeroed air section
            SectionsCopied = 0;
        }

        /// <summary>
        /// Copies the sections <paramref name="firstSection"/>..<paramref name="lastSection"/> of
        /// <paramref name="center"/> and of each populated neighbor among the first <paramref name="slotCount"/>
        /// <see cref="GatherSlot"/>s (each at most once per tick), and fills <paramref name="table"/> for one job: an
        /// absent or unpopulated neighbor reads <see cref="SectionArenaTable.MissingChunk"/>, every section outside the
        /// band <see cref="SectionArenaTable.NotRequested"/>.
        /// </summary>
        /// <param name="center">The chunk the job ticks; always copied.</param>
        /// <param name="worldData">The chunk store the neighbors are looked up in.</param>
        /// <param name="firstSection">The band's lowest section.</param>
        /// <param name="lastSection">The band's highest section, inclusive.</param>
        /// <param name="slotCount">How many slots the job reads, counted from <see cref="GatherSlot.Center"/>.</param>
        /// <param name="table">The job's <see cref="SectionArenaTable.Length"/>-entry table.</param>
        /// <returns>Sections this call copied (not those an earlier request already had).</returns>
        public int Request(ChunkData center, WorldData worldData, int firstSection, int lastSection, int slotCount,
            NativeArray<int> table)
        {
            for (int i = 0; i < table.Length; i++)
                table[i] = SectionArenaTable.NotRequested;

            int copied = 0;
            for (int slot = 0; slot < slotCount; slot++)
            {
                int tableRow = slot * ChunkMath.SECTIONS_PER_CHUNK;
                ChunkData source = slot == GatherSlot.Center ? center : PopulatedNeighbor(center, worldData, slot);
                if (source == null)
                {
                    for (int s = 0; s < ChunkMath.SECTIONS_PER_CHUNK; s++)
                        table[tableRow + s] = SectionArenaTable.MissingChunk;
                    continue;
                }

                int chunkBase = BaseOf(source.Position);
                for (int s = firstSection; s <= lastSection; s++)
                {
                    int startIndex = chunkBase + s;
                    int start = _sectionStarts[startIndex];
                    if (start == NOT_COPIED)
                    {
                        start = CopySection(source, s, ref copied);
                        _sectionStarts[startIndex] = start;
                    }

                    table[tableRow + s] = start;
                }
            }

            SectionsCopied += copied;
            return copied;
        }

        /// <summary>
        /// Ends the tick: gives back capacity a burst grew past what this tick used. Call only after every job that
        /// read <see cref="Sections"/> has completed.
        /// </summary>
        public void EndTick()
        {
            if (!_sections.IsCreated) return;

            int keep = Math.Max(_sections.Length, MIN_RETAINED_SECTIONS * ChunkMath.SECTION_VOLUME);
            if (_sections.Capacity > keep * TRIM_FACTOR)
                _sections.Capacity = keep;
        }

        /// <summary>Releases the arena's native memory.</summary>
        public void Dispose()
        {
            if (_sections.IsCreated) _sections.Dispose();
        }

        private void EnsureAllocated()
        {
            if (_sections.IsCreated) return;

            _sections = new NativeList<uint>(MIN_RETAINED_SECTIONS * ChunkMath.SECTION_VOLUME, Allocator.Persistent);
            _sections.Resize(ChunkMath.SECTION_VOLUME, NativeArrayOptions.ClearMemory); // the air section
        }

        private int BaseOf(Vector2Int origin)
        {
            if (_chunkBases.TryGetValue(origin, out int chunkBase)) return chunkBase;

            chunkBase = _sectionStarts.Count;
            for (int s = 0; s < ChunkMath.SECTIONS_PER_CHUNK; s++)
                _sectionStarts.Add(NOT_COPIED);
            _chunkBases.Add(origin, chunkBase);
            return chunkBase;
        }

        private int CopySection(ChunkData source, int section, ref int copied)
        {
            if (source.sections[section] == null) return AIR_SECTION_START;

            int start = _sections.Length;
            _sections.ResizeUninitialized(start + ChunkMath.SECTION_VOLUME);
            source.CopySectionVoxels(section, _sections.AsArray(), start);
            copied++;
            return start;
        }

        // Populated, not merely present (Fluid Bug 18): an unpopulated placeholder has no voxels to read, so it must
        // read as missing — exactly as ChunkData.GetState's cross-chunk lookup does.
        private static ChunkData PopulatedNeighbor(ChunkData center, WorldData worldData, int slot)
        {
            if (worldData == null) return null;

            Vector2Int origin = center.Position + SlotOffset(slot);
            return worldData.TryGetChunk(origin, out ChunkData neighbor) && neighbor != null && neighbor.IsPopulated
                ? neighbor
                : null;
        }

        private static Vector2Int SlotOffset(int slot)
        {
            const int w = ChunkMath.CHUNK_WIDTH;
            switch (slot)
            {
                case GatherSlot.West: return new Vector2Int(-w, 0);
                case GatherSlot.East: return new Vector2Int(w, 0);
                case GatherSlot.South: return new Vector2Int(0, -w);
                case GatherSlot.North: return new Vector2Int(0, w);
                case GatherSlot.SouthWest: return new Vector2Int(-w, -w);
                case GatherSlot.NorthWest: return new Vector2Int(-w, w);
                case GatherSlot.SouthEast: return new Vector2Int(w, -w);
                case GatherSlot.NorthEast: return new Vector2Int(w, w);
                default: return Vector2Int.zero;
            }
        }
    }
}
