using System;
using Data;
using Helpers;
using Jobs.Data;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Jobs
{
    /// <summary>
    /// TG-4 Phase 3–4b — runs one chunk's <b>entire</b> fluid tick (interior AND border voxels) through the Burst
    /// <see cref="FluidTickJob"/>, border voxels resolving cross-chunk reads from a per-tick Y-banded
    /// neighbor halo. The pre-tick voxels come from a <see cref="TickSnapshotArena"/> shared by every job of the tick;
    /// the ticker owns only the padded gather destination, the job's section table and the index/output lists, so the
    /// per-tick work allocates nothing after warm-up. This keeps the per-chunk main-thread cost to a bucket partition,
    /// the band's arena request and the drain — the heavy flow compute runs in the job.
    /// <para>
    /// <b>One instance per in-flight chunk, not one shared instance.</b> The production tick
    /// (<see cref="World.TickChunksParallel"/>) schedules every active-fluid chunk's job concurrently, so
    /// each rents its own ticker from a <c>DynamicPool&lt;FluidBurstTicker&gt;</c> and returns it after the
    /// drain. Sharing one ticker across chunks would hand the same padded volume to jobs running in parallel —
    /// the scratch is what makes an instance single-owner while its job is in flight.
    /// </para>
    /// <para>
    /// It reads a <i>pre-tick</i> snapshot and never mutates the chunk: the caller completes the handle,
    /// then drains <see cref="Mods"/> / <see cref="ModsPerSource"/> / <see cref="InactiveInterior"/>
    /// serially in snapshot order (<c>Chunk.DrainTick</c>). That split — parallel read+emit, serial apply,
    /// emission order fixed by the emitting voxel — is what makes the parallel tick byte-identical to the
    /// original single-threaded loop. <see cref="TryPrepare"/> and <see cref="Schedule"/> are separate so the main-thread
    /// copy can be timed apart from scheduling; <see cref="ScheduleFluids"/> is the pair in one call, and
    /// <see cref="RunFluids"/> runs the same work serially with identical output (<c>Schedule == Run</c>). Those two
    /// standalone paths prepare through an arena of the ticker's own; the production tick passes the shared one.
    /// </para>
    /// </summary>
    public sealed class FluidBurstTicker : IDisposable
    {
        // TG-4 Phase 4b: the gather destination the FluidTickJob reads — halo-padded and allocated at FULL height
        // (ChunkMath.PADDED_FLUID_VOLUME), but each tick fills only the active Y-band as a leading prefix. The job's
        // GetStateLocal offsets its read by BandMinY, so padded row 0 is BandMinY — not Y=0.
        private NativeArray<uint> _paddedVoxels;

        // Arena index of each (slot, section) the job reads, filled by TickSnapshotArena.Request. An absent or
        // unpopulated neighbor (Fluid Bug 18) maps to SectionArenaTable.MissingChunk, which the gather sentinel-fills
        // so its border reads resolve to void exactly as the old out-of-chunk read did.
        private NativeArray<int> _sectionTable;

        private NativeList<int> _interior;
        private NativeList<VoxelMod> _mods;
        private NativeList<int> _modsPerSource;
        private NativeList<int> _inactive;
        private bool _allocated;

        // TG-4 Y-band: the active-fluid Y-band the FluidTickJob gathers + reads (a prefix of the full-height padded
        // volume) — the tight [minActiveY−reach, maxActiveY+reach] computed by the Prepare pass, making the per-tick
        // copy independent of world height.
        private int _bandMinY;
        private int _bandHeight;

        // The arena the last successful prepare filled the section table from; Schedule hands its sections to the job.
        private TickSnapshotArena _arena;

        // The standalone paths' own arena (RunFluids / ScheduleFluids), created on first use.
        private TickSnapshotArena _ownArena;

        /// <summary>Voxel mods emitted by the most recent <see cref="RunFluids"/> (valid until the next call).</summary>
        public NativeList<VoxelMod> Mods => _mods;

        /// <summary>
        /// Per-source mod run lengths from the most recent run, one entry per active fluid voxel in
        /// <see cref="InteriorIndices"/> order. Consume <see cref="Mods"/> in these runs to replay the job's emission
        /// in bucket order (valid until the next call).
        /// </summary>
        public NativeList<int> ModsPerSource => _modsPerSource;

        /// <summary>Flat indices of fluid voxels that became inactive in the most recent run (valid until the next call).</summary>
        public NativeList<int> InactiveInterior => _inactive;

        /// <summary>
        /// The flat chunk indices of the fluid voxels processed by the most recent run, in the same order as
        /// <see cref="ModsPerSource"/> (i.e. the order they were enumerated from the bucket). The i-th entry owns
        /// the i-th <see cref="ModsPerSource"/> run — lets a caller map the job's per-source output back to a voxel
        /// position without re-deriving the partition (valid until the next call).
        /// </summary>
        public NativeList<int> InteriorIndices => _interior;

        /// <summary>
        /// Sections the most recent <see cref="TryPrepare"/> copied into its arena — those of the band, in the center
        /// and each populated neighbor, that no earlier request of the tick had copied — or 0 when it found no active fluid.
        /// </summary>
        public int SnapshotSections { get; private set; }

        /// <summary>
        /// The serial (<c>.Run()</c>) fluid tick: ticks EVERY active fluid (interior AND border) of <paramref name="cd"/>
        /// through <see cref="FluidTickJob"/>, border voxels resolving cross-chunk reads from the gathered neighbor
        /// halo (neighbors looked up in <paramref name="worldData"/>). Reads a pre-tick snapshot and does NOT modify
        /// the chunk; the caller drains <see cref="Mods"/>/<see cref="ModsPerSource"/>/<see cref="InactiveInterior"/>
        /// afterward. Its output is byte-identical to the scheduled path's, which makes it the serial determinism baseline.
        /// </summary>
        /// <param name="cd">The chunk whose fluids to tick.</param>
        /// <param name="tickCounter">The current tick salt (<c>World.TickCounter</c>) for the viscosity RNG.</param>
        /// <param name="blockTypes">The global block-type job blob.</param>
        /// <param name="worldData">The chunk store used to resolve the 8 neighbor snapshots (missing → sentinel/void).</param>
        /// <remarks>
        /// The gather + reads are sized to the tight active-fluid Y-band (<see cref="ChunkMath.FLUID_VERTICAL_REACH"/>-padded)
        /// rather than full chunk height — byte-identical by the reach invariant, but the per-tick copy is independent
        /// of world height.
        /// </remarks>
        public void RunFluids(ChunkData cd, int tickCounter, NativeArray<BlockTypeJobData> blockTypes, WorldData worldData)
        {
            if (TryPrepare(cd, worldData, BeginOwnArena()))
                BuildJob(cd, tickCounter, blockTypes).Run();
        }

        /// <summary>
        /// The parallel counterpart of <see cref="RunFluids"/>: prepares all-fluid indices + the neighbor halo, then
        /// schedules the job on a worker and returns its <see cref="JobHandle"/>. The caller batches handles across
        /// chunks, completes them, then drains the outputs in deterministic chunk order. <b>Each concurrently-scheduled
        /// chunk needs its own ticker instance</b> (the snapshot + neighbor scratch is per-ticker, not shareable in flight).
        /// </summary>
        /// <param name="cd">The chunk whose fluids to tick.</param>
        /// <param name="tickCounter">The current tick salt (<c>World.TickCounter</c>) for the viscosity RNG.</param>
        /// <param name="blockTypes">The global block-type job blob.</param>
        /// <param name="worldData">The chunk store used to resolve the 8 neighbor snapshots (missing → sentinel/void).</param>
        /// <param name="timer">The performance monitor's busy-time timer for the job; untimed by default.</param>
        /// <returns>The scheduled job's handle, or <c>default</c> when the chunk has no active fluids.</returns>
        public JobHandle ScheduleFluids(ChunkData cd, int tickCounter, NativeArray<BlockTypeJobData> blockTypes, WorldData worldData,
            JobBusyTimer timer = default)
        {
            if (!TryPrepare(cd, worldData, BeginOwnArena()))
                return default;

            return Schedule(cd, tickCounter, blockTypes, timer);
        }

        /// <summary>
        /// Clears the per-run outputs and partitions <see cref="ChunkData.ActiveFluidsBucket"/> for the tick: collects
        /// <b>every</b> active fluid index for the job, sizes the active-fluid Y-band, then requests the band's sections
        /// of the center and its 8 neighbors from <paramref name="arena"/>. Shared by the serial (<see cref="RunFluids"/>)
        /// and parallel (<see cref="ScheduleFluids"/>) paths, and public apart from <see cref="Schedule"/> so the
        /// production tick can prepare every chunk before it schedules any job (and time the copies alone).
        /// </summary>
        /// <param name="cd">The chunk whose fluids to prepare.</param>
        /// <param name="worldData">The chunk store used to resolve the neighbors.</param>
        /// <param name="arena">The tick's snapshot arena; its <see cref="TickSnapshotArena.BeginTick"/> must have run.</param>
        /// <returns>True if there is any active fluid (a job should run); false if the bucket is empty.</returns>
        public bool TryPrepare(ChunkData cd, WorldData worldData, TickSnapshotArena arena)
        {
            EnsureAllocated();
            _interior.Clear();
            _mods.Clear();
            _modsPerSource.Clear();
            _inactive.Clear();
            SnapshotSections = 0;
            _arena = null;

            NativeHashSet<int> bucket = cd.ActiveFluidsBucket;
            if (!bucket.IsCreated || bucket.Count == 0)
                return false;

            // Every active fluid is job-computed (the halo lets border voxels read across seams). Collect the indices
            // and track their Y-extent in the same pass to size the gather window.
            int minY = int.MaxValue;
            int maxY = int.MinValue;
            foreach (int index in bucket)
            {
                _interior.Add(index);
                ChunkMath.GetLocalPositionFromFlattenedIndex(index, out _, out int y, out _);
                minY = math.min(minY, y);
                maxY = math.max(maxY, y);
            }

            // Size the Y-band: [minActiveY−reach, maxActiveY+reach] clamped to the chunk — a tight superset of every
            // read (FLUID_VERTICAL_REACH invariant), making the per-tick copy independent of world height.
            _bandMinY = math.max(0, minY - ChunkMath.FLUID_VERTICAL_REACH);
            int bandMaxYExclusive = math.min(ChunkMath.CHUNK_HEIGHT, maxY + ChunkMath.FLUID_VERTICAL_REACH + 1);
            _bandHeight = bandMaxYExclusive - _bandMinY;

            // Snapshot the band's sections of the center and the 8 neighbors (for the border voxels), all pre-tick.
            int firstSection = _bandMinY / ChunkMath.SECTION_SIZE;
            int lastSection = (_bandMinY + _bandHeight - 1) / ChunkMath.SECTION_SIZE;
            SnapshotSections = arena.Request(cd, worldData, firstSection, lastSection, GatherSlot.Count, _sectionTable);
            _arena = arena;
            return true;
        }

        /// <summary>
        /// Schedules the fluid job over the state the last successful <see cref="TryPrepare"/> of <paramref name="cd"/>
        /// left in this ticker. Call it only after that returned true, and only once no further request will grow the
        /// arena that prepare used.
        /// </summary>
        /// <param name="cd">The chunk passed to <see cref="TryPrepare"/>.</param>
        /// <param name="tickCounter">The current tick salt (<c>World.TickCounter</c>) for the viscosity RNG.</param>
        /// <param name="blockTypes">The global block-type job blob.</param>
        /// <param name="timer">The performance monitor's busy-time timer for the job; untimed by default.</param>
        /// <returns>The scheduled job's handle.</returns>
        public JobHandle Schedule(ChunkData cd, int tickCounter, NativeArray<BlockTypeJobData> blockTypes, JobBusyTimer timer = default) =>
            BuildJob(cd, tickCounter, blockTypes, timer).Schedule();

        /// <summary>Builds the fluid job over the prepared arena sections + indices and this ticker's outputs.</summary>
        private FluidTickJob BuildJob(ChunkData cd, int tickCounter, NativeArray<BlockTypeJobData> blockTypes,
            JobBusyTimer timer = default) =>
            new FluidTickJob
            {
                ArenaSections = _arena.Sections,
                SectionTable = _sectionTable,
                PaddedVoxels = _paddedVoxels,
                BlockTypes = blockTypes,
                InteriorFluidIndices = _interior.AsArray(),
                BandMinY = _bandMinY,
                BandHeight = _bandHeight,
                TickCounter = tickCounter,
                ChunkOrigin = new int2(cd.Position.x, cd.Position.y),
                Mods = _mods,
                NowInactive = _inactive,
                ModsPerSource = _modsPerSource,
                Timer = timer,
            };

        /// <summary>Lazily allocates the reusable persistent scratch on first use.</summary>
        private void EnsureAllocated()
        {
            if (_allocated)
                return;

            _paddedVoxels = new NativeArray<uint>(ChunkMath.PADDED_FLUID_VOLUME, Allocator.Persistent);
            _sectionTable = new NativeArray<int>(SectionArenaTable.Length, Allocator.Persistent);
            _interior = new NativeList<int>(256, Allocator.Persistent);
            _mods = new NativeList<VoxelMod>(256, Allocator.Persistent);
            _modsPerSource = new NativeList<int>(256, Allocator.Persistent);
            _inactive = new NativeList<int>(64, Allocator.Persistent);
            _allocated = true;
        }

        /// <summary>Starts a tick on this ticker's own arena, for the standalone paths.</summary>
        private TickSnapshotArena BeginOwnArena()
        {
            _ownArena ??= new TickSnapshotArena();
            _ownArena.BeginTick();
            return _ownArena;
        }

        /// <summary>Disposes the reusable native scratch. Call once when the owner (World) tears down.</summary>
        public void Dispose()
        {
            if (!_allocated)
                return;

            if (_paddedVoxels.IsCreated) _paddedVoxels.Dispose();
            if (_sectionTable.IsCreated) _sectionTable.Dispose();
            _ownArena?.Dispose();
            _ownArena = null;
            _arena = null;

            if (_interior.IsCreated) _interior.Dispose();
            if (_mods.IsCreated) _mods.Dispose();
            if (_modsPerSource.IsCreated) _modsPerSource.Dispose();
            if (_inactive.IsCreated) _inactive.Dispose();
            _allocated = false;
        }
    }
}
