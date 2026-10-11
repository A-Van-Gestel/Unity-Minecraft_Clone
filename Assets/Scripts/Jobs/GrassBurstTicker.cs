using System;
using Data;
using Helpers;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Jobs
{
    /// <summary>
    /// ES-29 — runs one chunk's grass tick through the Burst <see cref="GrassTickJob"/>, the grass counterpart of
    /// <see cref="FluidBurstTicker"/>. It partitions <see cref="ChunkData.ActiveGrassBucket"/>, requests the grass
    /// band's sections of the chunk and its four cardinal neighbors from the tick's <see cref="TickSnapshotArena"/>, and
    /// owns the job's section table and output lists, so the per-tick work allocates nothing after warm-up.
    /// <para>
    /// One instance per in-flight chunk (each rents its own from a pool). It never mutates the chunk: the caller
    /// completes the job, then <c>Chunk.DrainTick</c> replays <see cref="Mods"/> per <see cref="ModsPerSource"/> run
    /// and removes <see cref="InactiveIndices"/> — the stream the managed grass loop enqueued, in the same order.
    /// <see cref="RunGrass"/> runs the same work serially on the ticker's own arena (the validation oracle path).
    /// </para>
    /// </summary>
    public sealed class GrassBurstTicker : IDisposable
    {
        // Grass reads reach one row below its own (spread-down dirt) and two above (the air over above-adjacent dirt).
        private const int GRASS_REACH_DOWN = 1;
        private const int GRASS_REACH_UP = 2;

        // Center plus the four cardinal neighbors: grass reads one cell across a seam and never diagonally.
        private const int GRASS_SLOT_COUNT = GatherSlot.North + 1;

        private NativeArray<int> _sectionTable;

        private NativeList<int> _indices;
        private NativeList<VoxelMod> _mods;
        private NativeList<int> _modsPerSource;
        private NativeList<int> _inactive;
        private bool _allocated;

        // The arena the last successful prepare filled the section table from; Schedule hands its sections to the job.
        private TickSnapshotArena _arena;

        // RunGrass's own arena, created on first use.
        private TickSnapshotArena _ownArena;

        /// <summary>Voxel mods emitted by the most recent run (valid until the next prepare).</summary>
        public NativeList<VoxelMod> Mods => _mods;

        /// <summary>Per-source mod run lengths from the most recent run, parallel to <see cref="Indices"/>.</summary>
        public NativeList<int> ModsPerSource => _modsPerSource;

        /// <summary>Flat indices of grass voxels that became inactive in the most recent run.</summary>
        public NativeList<int> InactiveIndices => _inactive;

        /// <summary>The flat indices of the grass voxels the most recent run ticked, in bucket-enumeration order.</summary>
        public NativeList<int> Indices => _indices;

        /// <summary>Sections the most recent <see cref="TryPrepare"/> copied into its arena (those no earlier request had).</summary>
        public int SnapshotSections { get; private set; }

        /// <summary>
        /// The serial (<c>.Run()</c>) grass tick of <paramref name="cd"/> on this ticker's own arena — the same work as
        /// <see cref="TryPrepare"/> + <see cref="Schedule"/>, with identical output.
        /// </summary>
        /// <param name="cd">The chunk whose grass to tick.</param>
        /// <param name="tickCounter">The current tick salt (<c>World.TickCounter</c>) for the spread RNG.</param>
        /// <param name="blockTypes">The global block-type job blob.</param>
        /// <param name="worldData">The chunk store the neighbors are looked up in.</param>
        public void RunGrass(ChunkData cd, int tickCounter, NativeArray<BlockTypeJobData> blockTypes, WorldData worldData)
        {
            _ownArena ??= new TickSnapshotArena();
            _ownArena.BeginTick();
            if (TryPrepare(cd, worldData, _ownArena))
                BuildJob(cd, tickCounter, blockTypes).Run();
        }

        /// <summary>
        /// Clears the per-run outputs, collects every active grass index, sizes the grass Y-band and requests its
        /// sections of the chunk and its cardinal neighbors from <paramref name="arena"/>.
        /// </summary>
        /// <param name="cd">The chunk whose grass to prepare.</param>
        /// <param name="worldData">The chunk store used to resolve the neighbors.</param>
        /// <param name="arena">The tick's snapshot arena; its <see cref="TickSnapshotArena.BeginTick"/> must have run.</param>
        /// <returns>True if there is any active grass (a job should run); false if the bucket is empty.</returns>
        public bool TryPrepare(ChunkData cd, WorldData worldData, TickSnapshotArena arena)
        {
            EnsureAllocated();
            _indices.Clear();
            _mods.Clear();
            _modsPerSource.Clear();
            _inactive.Clear();
            SnapshotSections = 0;
            _arena = null;

            NativeHashSet<int> bucket = cd.ActiveGrassBucket;
            if (!bucket.IsCreated || bucket.Count == 0)
                return false;

            int minY = int.MaxValue;
            int maxY = int.MinValue;
            foreach (int index in bucket)
            {
                _indices.Add(index);
                ChunkMath.GetLocalPositionFromFlattenedIndex(index, out _, out int y, out _);
                minY = math.min(minY, y);
                maxY = math.max(maxY, y);
            }

            int firstSection = math.max(0, minY - GRASS_REACH_DOWN) / ChunkMath.SECTION_SIZE;
            int lastSection = math.min(ChunkMath.CHUNK_HEIGHT - 1, maxY + GRASS_REACH_UP) / ChunkMath.SECTION_SIZE;
            SnapshotSections = arena.Request(cd, worldData, firstSection, lastSection, GRASS_SLOT_COUNT, _sectionTable);
            _arena = arena;
            return true;
        }

        /// <summary>
        /// Schedules the grass job over the state the last successful <see cref="TryPrepare"/> of <paramref name="cd"/>
        /// left in this ticker, once no further request will grow that prepare's arena.
        /// </summary>
        /// <param name="cd">The chunk passed to <see cref="TryPrepare"/>.</param>
        /// <param name="tickCounter">The current tick salt (<c>World.TickCounter</c>) for the spread RNG.</param>
        /// <param name="blockTypes">The global block-type job blob.</param>
        /// <returns>The scheduled job's handle.</returns>
        public JobHandle Schedule(ChunkData cd, int tickCounter, NativeArray<BlockTypeJobData> blockTypes) =>
            BuildJob(cd, tickCounter, blockTypes).Schedule();

        private GrassTickJob BuildJob(ChunkData cd, int tickCounter, NativeArray<BlockTypeJobData> blockTypes) =>
            new GrassTickJob
            {
                ArenaSections = _arena.Sections,
                SectionTable = _sectionTable,
                BlockTypes = blockTypes,
                GrassIndices = _indices.AsArray(),
                TickCounter = tickCounter,
                ChunkOrigin = new int2(cd.Position.x, cd.Position.y),
                Mods = _mods,
                NowInactive = _inactive,
                ModsPerSource = _modsPerSource,
            };

        private void EnsureAllocated()
        {
            if (_allocated)
                return;

            _sectionTable = new NativeArray<int>(SectionArenaTable.Length, Allocator.Persistent);
            _indices = new NativeList<int>(256, Allocator.Persistent);
            _mods = new NativeList<VoxelMod>(64, Allocator.Persistent);
            _modsPerSource = new NativeList<int>(256, Allocator.Persistent);
            _inactive = new NativeList<int>(256, Allocator.Persistent);
            _allocated = true;
        }

        /// <summary>Disposes the reusable native scratch. Call once when the owner tears down.</summary>
        public void Dispose()
        {
            _ownArena?.Dispose();
            _ownArena = null;
            _arena = null;
            if (!_allocated)
                return;

            if (_sectionTable.IsCreated) _sectionTable.Dispose();
            if (_indices.IsCreated) _indices.Dispose();
            if (_mods.IsCreated) _mods.Dispose();
            if (_modsPerSource.IsCreated) _modsPerSource.Dispose();
            if (_inactive.IsCreated) _inactive.Dispose();
            _allocated = false;
        }
    }
}
