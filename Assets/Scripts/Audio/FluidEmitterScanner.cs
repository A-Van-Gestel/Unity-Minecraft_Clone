using System;
using Data;
using Helpers;
using Jobs;
using Jobs.Data;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Audio
{
    /// <summary>
    /// Drives the fluid-emitter scan (SOUND_ENGINE_DESIGN.md §5.2): picks the chunk sections worth looking
    /// at around the listener, snapshots them into native memory, and runs
    /// <see cref="FluidEmitterScanJob"/> over the snapshot. Owns all of its scratch, so a steady-state scan
    /// allocates nothing.
    /// </summary>
    /// <remarks>
    /// <para>The section filter is what makes the cost bearable: only sections whose
    /// <see cref="ChunkSection.emitterFluidCount"/> is positive are copied, so a listener floating over an
    /// ocean — every voxel of it a still source block — snapshots nothing at all. Without that filter the
    /// scan would memcpy roughly 1.6 MB per tick to find nothing.</para>
    /// <para>Produce-on-worker / consume-on-main: <see cref="Begin"/> schedules and returns immediately;
    /// the caller reads <see cref="Bins"/> only after <see cref="Complete"/>, on a later frame.</para>
    /// </remarks>
    public sealed class FluidEmitterScanner : IDisposable
    {
        /// <summary>
        /// How many sections one scan may snapshot. A hard ceiling on the per-tick memcpy (48 x 16 KB),
        /// paid only by a listener genuinely surrounded by moving water; the nearest sections win, and the
        /// ones dropped are the ones that would have faded in quietest anyway.
        /// </summary>
        private const int MAX_SECTIONS = 48;

        /// <summary>Offset from a section's low corner to its center, used for the distance ordering.</summary>
        private const int SECTION_HALF = ChunkMath.SECTION_SIZE / 2;

        private NativeArray<uint> _sections;
        private NativeArray<BlockTypeJobData> _palette;
        private NativeArray<int3> _sectionOrigins;
        private NativeArray<FluidEmitterBin> _bins;
        private JobHandle _handle;
        private bool _allocated;

        // Nearest-first candidate selection, kept sorted by distance as candidates are offered so no sort
        // (and no comparer allocation) is needed afterwards.
        private readonly ChunkSection[] _candidateSections = new ChunkSection[MAX_SECTIONS];
        private readonly int3[] _candidateOrigins = new int3[MAX_SECTIONS];
        private readonly int[] _candidateDistanceSq = new int[MAX_SECTIONS];
        private int _candidateCount;

        /// <summary>True while a scheduled scan has not been completed yet.</summary>
        public bool IsScanning { get; private set; }

        /// <summary>True once a completed scan's <see cref="Bins"/> hold a usable result.</summary>
        public bool HasResult { get; private set; }

        /// <summary>How many sections the last completed scan snapshotted. Diagnostics and validation only.</summary>
        public int LastSectionCount { get; private set; }

        /// <summary>The last completed scan's accumulation grid. Meaningful only while <see cref="HasResult"/> is true.</summary>
        public NativeArray<FluidEmitterBin> Bins => _bins;

        /// <summary>The bin-grid origin the last completed scan used, in voxel world space.</summary>
        public int3 BinOrigin { get; private set; }

        /// <summary>
        /// Selects the sections worth scanning around a listener and schedules the scan job over them.
        /// </summary>
        /// <param name="world">The live world to read chunk data from.</param>
        /// <param name="listenerVoxel">The listener's voxel cell.</param>
        /// <returns>True when a scan was scheduled; false only when there is no world to scan.</returns>
        /// <remarks>
        /// <b>A scan with nothing to snapshot still runs.</b> Finding no flowing fluid is a result, not a
        /// reason to skip: the job clears the grid, the caller reads an empty one and fades its emitters
        /// out. Returning early instead left the previous scan's targets standing, so emitters kept sounding
        /// at their old positions until the listener happened to walk back into flowing water.
        /// </remarks>
        public bool Begin(World world, Vector3Int listenerVoxel)
        {
            if (IsScanning || world == null || world.worldData == null) return false;

            EnsureAllocated();

            int3 listener = new int3(listenerVoxel.x, listenerVoxel.y, listenerVoxel.z);
            _candidateCount = 0;
            CollectCandidates(world, listener);

            for (int i = 0; i < _candidateCount; i++)
            {
                NativeArray<uint>.Copy(_candidateSections[i].voxels, 0, _sections, i * ChunkMath.SECTION_VOLUME,
                    ChunkMath.SECTION_VOLUME);
                _sectionOrigins[i] = _candidateOrigins[i];

                // Dropped immediately: holding a ChunkSection reference across the job would keep a pooled
                // section alive past its recycle, and the snapshot is already taken.
                _candidateSections[i] = null;
            }

            BinOrigin = FluidEmitterScanGeometry.BinOrigin(listener);
            LastSectionCount = _candidateCount;

            if (!CopyPalette(world)) return false;

            _handle = new FluidEmitterScanJob
            {
                Sections = _sections,
                SectionOrigins = _sectionOrigins,
                SectionCount = _candidateCount,
                BlockTypes = _palette,
                ListenerVoxel = listener,
                BinOrigin = BinOrigin,
                Bins = _bins,
            }.Schedule();

            IsScanning = true;
            return true;
        }

        /// <summary>
        /// Completes a scheduled scan, making <see cref="Bins"/> readable. Safe to call when none is in flight.
        /// </summary>
        public void Complete()
        {
            if (!IsScanning) return;

            _handle.Complete();
            IsScanning = false;
            HasResult = true;
        }

        /// <summary>Completes any in-flight scan and frees the native scratch.</summary>
        public void Dispose()
        {
            if (IsScanning)
            {
                _handle.Complete();
                IsScanning = false;
            }

            if (!_allocated) return;

            _sections.Dispose();
            _sectionOrigins.Dispose();
            _bins.Dispose();
            if (_palette.IsCreated) _palette.Dispose();
            _allocated = false;
            HasResult = false;
        }

        /// <summary>
        /// Walks the chunk columns in range and offers every populated, flow-bearing section to the
        /// nearest-first selection.
        /// </summary>
        /// <param name="world">The world to read chunk data from.</param>
        /// <param name="listener">The listener's voxel cell.</param>
        private void CollectCandidates(World world, int3 listener)
        {
            int minChunkX = ChunkMath.VoxelToChunk(listener.x - FluidEmitterScanGeometry.RadiusXZ);
            int maxChunkX = ChunkMath.VoxelToChunk(listener.x + FluidEmitterScanGeometry.RadiusXZ);
            int minChunkZ = ChunkMath.VoxelToChunk(listener.z - FluidEmitterScanGeometry.RadiusXZ);
            int maxChunkZ = ChunkMath.VoxelToChunk(listener.z + FluidEmitterScanGeometry.RadiusXZ);

            int minSection = Mathf.Max(0,
                (listener.y - FluidEmitterScanGeometry.RadiusY) / ChunkMath.SECTION_SIZE);
            int maxSection = Mathf.Min(ChunkMath.SECTIONS_PER_CHUNK - 1,
                (listener.y + FluidEmitterScanGeometry.RadiusY) / ChunkMath.SECTION_SIZE);

            for (int cx = minChunkX; cx <= maxChunkX; cx++)
            for (int cz = minChunkZ; cz <= maxChunkZ; cz++)
            {
                Vector2Int chunkVoxelPos = new Vector2Int(cx * ChunkMath.CHUNK_WIDTH, cz * ChunkMath.CHUNK_WIDTH);
                if (!world.worldData.TryGetChunk(chunkVoxelPos, out ChunkData chunkData)) continue;
                if (chunkData is not { IsPopulated: true } || chunkData.sections == null) continue;

                for (int s = minSection; s <= maxSection; s++)
                {
                    ChunkSection section = chunkData.sections[s];
                    if (section == null) continue;

                    // A count computed under a different palette describes a different world. Recomputing
                    // here rather than trusting it is what stops a rebind leaving sections permanently
                    // under-counted — and an under-count reads as silence, with no other symptom.
                    if (section.emitterFluidGeneration != FluidBlockLookup.Generation)
                        section.RecalculateEmitterFluidCount();

                    // <= rather than ==: an imbalanced count must fail toward scanning, never toward silence.
                    if (section.emitterFluidCount <= 0) continue;

                    int3 origin = new int3(chunkVoxelPos.x, s * ChunkMath.SECTION_SIZE, chunkVoxelPos.y);
                    OfferCandidate(section, origin, listener);
                }
            }
        }

        /// <summary>
        /// Inserts a section into the distance-ordered candidate list, dropping the farthest when full.
        /// </summary>
        /// <param name="section">The section to snapshot.</param>
        /// <param name="origin">Its voxel-space low corner.</param>
        /// <param name="listener">The listener's voxel cell.</param>
        private void OfferCandidate(ChunkSection section, int3 origin, int3 listener)
        {
            int3 delta = origin + new int3(SECTION_HALF, SECTION_HALF, SECTION_HALF) - listener;
            int distanceSq = delta.x * delta.x + delta.y * delta.y + delta.z * delta.z;

            if (_candidateCount == MAX_SECTIONS && distanceSq >= _candidateDistanceSq[MAX_SECTIONS - 1]) return;

            int insert = _candidateCount < MAX_SECTIONS ? _candidateCount : MAX_SECTIONS - 1;
            while (insert > 0 && _candidateDistanceSq[insert - 1] > distanceSq)
            {
                _candidateDistanceSq[insert] = _candidateDistanceSq[insert - 1];
                _candidateSections[insert] = _candidateSections[insert - 1];
                _candidateOrigins[insert] = _candidateOrigins[insert - 1];
                insert--;
            }

            _candidateDistanceSq[insert] = distanceSq;
            _candidateSections[insert] = section;
            _candidateOrigins[insert] = origin;

            if (_candidateCount < MAX_SECTIONS) _candidateCount++;
        }

        /// <summary>
        /// Mirrors the world's block palette into scanner-owned memory for the job to read.
        /// </summary>
        /// <param name="world">The world holding the live palette.</param>
        /// <returns>True when a usable palette was copied.</returns>
        /// <remarks>
        /// Copied rather than referenced because the job outlives the frame that scheduled it, and
        /// <c>World.OnDestroy</c> disposes <c>JobDataManager</c>'s arrays unconditionally. The director sits
        /// on a different GameObject, and Unity guarantees no destruction order between them, so a scene
        /// unload can free the palette while the scan is still in flight — a safety-system exception in the
        /// editor and a use-after-free under IL2CPP. A few KB per scan against a section snapshot that can
        /// reach 768 KB.
        /// </remarks>
        private bool CopyPalette(World world)
        {
            NativeArray<BlockTypeJobData> source = world.JobDataManager.BlockTypesJobData;
            if (!source.IsCreated || source.Length == 0) return false;

            if (_palette.IsCreated && _palette.Length != source.Length) _palette.Dispose();
            if (!_palette.IsCreated)
                _palette = new NativeArray<BlockTypeJobData>(source.Length, Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);

            _palette.CopyFrom(source);
            return true;
        }

        private void EnsureAllocated()
        {
            if (_allocated) return;

            _sections = new NativeArray<uint>(MAX_SECTIONS * ChunkMath.SECTION_VOLUME, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _sectionOrigins = new NativeArray<int3>(MAX_SECTIONS, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _bins = new NativeArray<FluidEmitterBin>(FluidEmitterScanGeometry.BinCount, Allocator.Persistent);
            _allocated = true;
        }
    }
}
